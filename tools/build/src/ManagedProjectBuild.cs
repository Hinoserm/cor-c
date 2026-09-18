using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corsac.Projects;

namespace Corsac.Build;

/// <summary>Standard SDK project evaluation is ours; Roslyn supplies managed code generation, never MSBuild.</summary>
public static class ManagedProjectBuild
{
    public static async Task Run(string path, string configuration, CancellationToken cancel)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessStartInfo discover = new(host) { RedirectStandardOutput = true, UseShellExecute = false };
        discover.ArgumentList.Add("--list-sdks");
        using Process discovery = Process.Start(discover) ?? throw new IOException("Cannot locate .NET SDK");
        string installed = await discovery.StandardOutput.ReadToEndAsync(cancel);
        await discovery.WaitForExitAsync(cancel);
        var sdks = installed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            int space = line.IndexOf(' '), bracket = line.IndexOf('[');
            string version = line[..space];
            return (Version: Version.Parse(version.Split('-')[0]), Path: Path.Combine(line[(bracket + 1)..].Trim().TrimEnd(']'), version));
        }).OrderByDescending(sdk => sdk.Version).ToArray();
        if (discovery.ExitCode != 0 || sdks.Length == 0) throw new IOException("A .NET SDK is required for managed components");
        string sdk = sdks[0].Path, dotnet = Path.GetFullPath(Path.Combine(sdk, "../.."));
        string csc = Path.Combine(sdk, "Roslyn/bincore/csc.dll");
        IReadOnlyList<EvaluatedProject> graph = ProjectGraph.Evaluate(path, configuration, null, managed: true);
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        foreach (EvaluatedProject project in graph)
        {
            string Property(string name, string fallback = "") => project.Properties.TryGetValue(name, out string? value) ? value : fallback;
            string root = Path.GetDirectoryName(project.Path)!;
            string output = Path.Combine(root, "bin/managed", configuration, project.Framework);
            string work = Path.Combine(root, "obj/managed", configuration, project.Framework);
            Directory.CreateDirectory(output); Directory.CreateDirectory(work);
            string packRoot = Path.Combine(dotnet, "packs/Microsoft.NETCore.App.Ref");
            string[] packs = Directory.GetDirectories(packRoot).Where(pack => Directory.Exists(Path.Combine(pack, "ref", project.Framework)))
                .OrderByDescending(pack => Version.Parse(Path.GetFileName(pack))).ToArray();
            if (packs.Length == 0) throw new IOException("No reference pack for " + project.Framework);
            string[] references = Directory.GetFiles(Path.Combine(packs[0], "ref", project.Framework), "*.dll").Order(StringComparer.Ordinal).ToArray();
            string dll = Path.Combine(output, project.AssemblyName + ".dll");
            List<string> sources = project.Sources.ToList();
            if (Property("ImplicitUsings") is "enable" or "true")
            {
                string usings = Path.Combine(work, "GlobalUsings.g.cs");
                WriteChanged(usings, string.Join('\n', new[] { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" }.Select(ns => "global using global::" + ns + ";")));
                sources.Add(usings);
            }
            List<string> arguments = new() { "-nologo", "-noconfig", "-nostdlib+", "-deterministic+", "-parallel-",
                "-target:" + (project.OutputType == "Library" ? "library" : project.OutputType == "WinExe" ? "winexe" : "exe"),
                "-langversion:" + Property("LangVersion", "latest"), "-nullable:" + Property("Nullable", "disable"),
                "-unsafe" + (Property("AllowUnsafeBlocks") == "true" ? "+" : "-"),
                "-checked" + (Property("CheckForOverflowUnderflow") == "true" ? "+" : "-"),
                "-optimize" + (Property("Optimize", configuration == "Release" ? "true" : "false") == "true" ? "+" : "-"),
                "-warnaserror" + (project.WarningsAsErrors ? "+" : "-"), "-define:" + string.Join(';', project.Defines) };
            if (project.StartupObject.Length != 0) arguments.Add("-main:" + project.StartupObject);
            foreach (string reference in references.Concat(project.References.Select(reference => outputs[reference]))) arguments.Add("-r:" + reference);
            arguments.AddRange(sources);
            string Identity(string file) => file + ":" + File.GetLastWriteTimeUtc(file).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.Evaluation + string.Join('\n', arguments)
                + string.Join('\n', sources.Concat(references).Concat(project.References.Select(reference => outputs[reference])).Append(csc).Select(Identity)))));
            string state = Path.Combine(work, "compile.state");
            if (!File.Exists(dll) || !File.Exists(state) || File.ReadAllText(state) != signature + "\n" + Identity(dll))
            {
                string temporary = Path.Combine(work, project.AssemblyName + ".dll");
                arguments.Add("-out:" + temporary);
                ProcessStartInfo start = new(host) { UseShellExecute = false };
                start.ArgumentList.Add(csc);
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                Console.WriteLine("managed compile " + project.Path);
                using Process process = Process.Start(start) ?? throw new IOException("Cannot start managed C# compiler");
                using var registration = cancel.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                await process.WaitForExitAsync(cancel);
                if (process.ExitCode != 0) throw new IOException("Managed compilation failed: " + project.Path);
                File.Move(temporary, dll, true);
                File.WriteAllText(state, signature + "\n" + Identity(dll));
            }
            else Console.WriteLine("current managed " + project.Path);
            outputs.Add(project.Path, dll);
            foreach (string dependency in outputs.Values.Where(file => file != dll))
                File.Copy(dependency, Path.Combine(output, Path.GetFileName(dependency)), true);
            if (project.OutputType != "Library")
            {
                if (!OperatingSystem.IsWindows())
                {
                    string launcher = Path.Combine(output, project.AssemblyName);
                    if (project.AssemblyName.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                        throw new IOException("Assembly name cannot be represented by a portable launcher");
                    WriteChanged(launcher, "#!/bin/sh\nexec dotnet \"$(dirname \"$0\")/" + project.AssemblyName + ".dll\" \"$@\"\n");
                    File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                string version = project.Framework[3..] + ".0";
                WriteChanged(Path.Combine(output, project.AssemblyName + ".runtimeconfig.json"), JsonSerializer.Serialize(new
                {
                    runtimeOptions = new { tfm = project.Framework, framework = new { name = "Microsoft.NETCore.App", version },
                        configProperties = new Dictionary<string, object> { ["System.Globalization.Invariant"] = Property("InvariantGlobalization") == "true" } }
                }));
            }
        }
    }

    private static void WriteChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }
}
