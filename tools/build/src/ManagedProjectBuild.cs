using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Corsac.Projects;

namespace Corsac.Build;

/// <summary>Standard SDK project evaluation is ours; Roslyn supplies managed code generation, never MSBuild.</summary>
public static class ManagedProjectBuild
{
    /// <summary>
    /// `name=command` pairs from ToolAliases, naming the old programs this
    /// one executable replaced and the command each of them stands for.
    /// </summary>
    internal static IEnumerable<string> Aliases(string declared)
        => declared.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim()).Where(entry => entry.Contains('='));

    public static async Task Run(string path, string configuration, CancellationToken cancel, ProcessRunner runner)
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
        Dictionary<string, EvaluatedProject> projects = graph.ToDictionary(project => project.Path, StringComparer.Ordinal);
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        foreach (EvaluatedProject project in graph)
        {
            string Property(string name, string fallback = "") => project.Properties.TryGetValue(name, out string? value) ? value : fallback;
            HashSet<string> closure = new(StringComparer.Ordinal);
            void Visit(string reference)
            {
                if (!closure.Add(reference)) return;
                foreach (string child in projects[reference].References) Visit(child);
            }
            foreach (string reference in project.References) Visit(reference);
            string[] projectReferences = (Property("DisableTransitiveProjectReferences").Equals("true", StringComparison.OrdinalIgnoreCase)
                ? project.References.AsEnumerable() : closure.Order(StringComparer.Ordinal)).Select(reference => outputs[reference]).ToArray();
            string root = Path.GetDirectoryName(project.Path)!;
            string output = Path.Combine(root, "bin/managed", configuration, project.Framework);
            string work = Path.Combine(root, "obj/managed", configuration, project.Framework);
            Directory.CreateDirectory(output); Directory.CreateDirectory(work);
            using FileStream buildLock = await Lock(Path.Combine(work, "build.lock"), cancel);
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
            foreach (string reference in references.Concat(projectReferences)) arguments.Add("-r:" + reference);
            arguments.AddRange(sources);
            string Identity(string file) => file + ":" + File.GetLastWriteTimeUtc(file).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.Evaluation + string.Join('\n', arguments)
                + string.Join('\n', sources.Concat(references).Concat(projectReferences).Append(csc).Select(Identity)))));
            string state = Path.Combine(work, "compile.state");
            if (!File.Exists(dll) || !File.Exists(state) || File.ReadAllText(state) != signature + "\n" + Identity(dll))
            {
                string temporary = Path.Combine(work, project.AssemblyName + ".dll");
                arguments.Add("-out:" + temporary);
                Console.WriteLine("managed compile " + project.Path);
                ProcessResult result = await runner.Run("managed-" + project.AssemblyName, host,
                    new[] { csc }.Concat(arguments), root, new Dictionary<string, string>(), TimeSpan.FromMinutes(10), cancel);
                if (result.ExitCode != 0 || result.TimedOut)
                    throw new IOException("Managed compilation failed: " + project.Path + "; logs: " + result.LogPrefix);
                File.Move(temporary, dll, true);
                File.WriteAllText(state, signature + "\n" + Identity(dll));
            }
            else Console.WriteLine("current managed " + project.Path);
            outputs.Add(project.Path, dll);
            foreach (string dependency in closure.Order(StringComparer.Ordinal).Select(reference => outputs[reference]))
            {
                string destination = Path.Combine(output, Path.GetFileName(dependency));
                if (!File.Exists(destination) || !SHA256.HashData(File.ReadAllBytes(destination)).AsSpan()
                    .SequenceEqual(SHA256.HashData(File.ReadAllBytes(dependency))))
                {
                    // REPLACED, NOT WRITTEN OVER. The toolchain is one
                    // executable now, so a build of it is very often running
                    // from the files it is about to replace; writing into a
                    // mapped image gives the running process torn metadata
                    // rather than an error. Moving a freshly written file
                    // over the name leaves that process on the old inode.
                    string staged = destination + ".new";
                    File.Copy(dependency, staged, true);
                    File.Move(staged, destination, true);
                }
            }
            if (project.OutputType != "Library")
            {
                bool nativeAot = NativeAotBuild.Enabled(project.Properties);
                if (!nativeAot && !OperatingSystem.IsWindows())
                {
                    string launcher = Path.Combine(output, project.AssemblyName);
                    if (project.AssemblyName.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                        throw new IOException("Assembly name cannot be represented by a portable launcher");
                    WriteChanged(launcher, "#!/bin/sh\nexec dotnet \"$(dirname \"$0\")/" + project.AssemblyName + ".dll\" \"$@\"\n");
                    File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    // THE NAMES THIS TOOL USED TO INSTALL UNDER. A managed
                    // launcher is a shell script, so a symbolic link to it
                    // would lose the name the caller typed; each alias gets
                    // its own launcher naming the command it stands for.
                    foreach (string alias in Aliases(Property("ToolAliases")))
                    {
                        string aliasPath = Path.Combine(output, alias.Split('=')[0]);
                        WriteChanged(aliasPath, "#!/bin/sh\nexec dotnet \"$(dirname \"$0\")/" + project.AssemblyName
                            + ".dll\" " + alias.Split('=')[1] + " \"$@\"\n");
                        File.SetUnixFileMode(aliasPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                }
                string version = project.Framework[3..] + ".0";
                JsonObject switches = new();
                foreach (var pair in ManagedRuntimeConfiguration.Create(project.Properties))
                    switches[pair.Key] = pair.Value switch
                    {
                        bool flag => JsonValue.Create(flag),
                        long number => JsonValue.Create(number),
                        _ => JsonValue.Create(pair.Value.ToString()),
                    };
                WriteChanged(Path.Combine(output, project.AssemblyName + ".runtimeconfig.json"), new JsonObject {
                    ["runtimeOptions"] = new JsonObject { ["tfm"] = project.Framework,
                        ["framework"] = new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = version },
                        ["configProperties"] = switches }
                }.ToJsonString());
                if (nativeAot) await NativeAotBuild.Run(project, dll, work,
                    closure.Order(StringComparer.Ordinal).Select(reference => outputs[reference]), runner, cancel);
            }
        }
    }

    private static void WriteChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<FileStream> Lock(string path, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 6000) { await Task.Delay(100, cancel); }
        }
    }
}
