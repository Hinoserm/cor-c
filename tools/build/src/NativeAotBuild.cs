using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Corsac.Projects;

namespace Corsac.Build;

/// <summary>Host-native publishing after owned project evaluation and Roslyn compilation.</summary>
public static class NativeAotBuild
{
    public static bool Enabled(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("PublishAot", out string? value)) return true;
        if (!bool.TryParse(value, out bool enabled)) throw new InvalidDataException("PublishAot must be true or false");
        return enabled;
    }

    public static async Task Run(EvaluatedProject project, string dll, string work,
        IEnumerable<string> dependencies, ProcessRunner runner, CancellationToken cancel)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new IOException("Owned Native AOT publishing currently supports Linux x64 hosts; set PublishAot=false explicitly for a managed host build");
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
        const string rid = "linux-x64";
        string compilerRoot = Path.Combine(packages, "runtime." + rid + ".microsoft.dotnet.ilcompiler");
        string runtimeRoot = Path.Combine(packages, "microsoft.netcore.app.runtime.nativeaot." + rid);
        string? version = Directory.Exists(compilerRoot) ? Directory.GetDirectories(compilerRoot)
            .Select(Path.GetFileName).Where(name => Version.TryParse(name, out Version? v)
                && v.Major.ToString() == project.Framework[3..].Split('.')[0]
                && Directory.Exists(Path.Combine(runtimeRoot, name!, "runtimes", rid, "lib", project.Framework)))
            .OrderByDescending(name => Version.Parse(name!)).FirstOrDefault() : null;
        if (version is null) throw new IOException("Native AOT packs are missing; run tools/bootstrap-build to provision the build utility and its matching AOT packs");
        string ilc = Path.Combine(compilerRoot, version, "tools/ilc");
        string runtime = Path.Combine(runtimeRoot, version, "runtimes", rid);
        string native = Path.Combine(runtime, "native");
        string output = Path.Combine(Path.GetDirectoryName(dll)!, project.AssemblyName);
        string state = Path.Combine(work, "native.state");
        string obj = Path.Combine(work, "native.o"), temporary = Path.Combine(work, "native-executable");
        string[] references = Directory.GetFiles(Path.Combine(runtime, "lib", project.Framework), "*.dll")
            .Concat(Directory.GetFiles(native, "*.dll")).Concat(dependencies).Order(StringComparer.Ordinal).ToArray();
        List<string> args = new() { dll, "-o:" + obj, "--targetos:linux", "--targetarch:x64", "-O", "--Ot",
            "--dehydrate", "--stacktracedata", "--scanreflection", "--methodbodyfolding:generic",
            "--initassembly:System.Private.CoreLib", "--initassembly:System.Private.StackTraceMetadata",
            "--initassembly:System.Private.TypeLoader", "--initassembly:System.Private.Reflection.Execution",
            "--generateunmanagedentrypoints:System.Private.CoreLib,HIDDEN" };
        args.AddRange(references.Select(path => "-r:" + path));
        bool invariant = project.Properties.TryGetValue("InvariantGlobalization", out string? flag) && bool.Parse(flag);
        foreach (var feature in new Dictionary<string, bool> {
            ["System.Globalization.Invariant"] = invariant,
            ["System.Globalization.PredefinedCulturesOnly"] = invariant,
            ["System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"] = false,
            ["System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"] = false,
            ["System.StartupHookProvider.IsSupported"] = false,
            ["System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported"] = false,
            ["System.Diagnostics.Tracing.EventSource.IsSupported"] = false,
            ["System.Diagnostics.Debugger.IsSupported"] = false,
            ["System.Resources.UseSystemResourceKeys"] = false })
        {
            string value = feature.Key + "=" + feature.Value.ToString().ToLowerInvariant();
            args.Add("--feature:" + value); args.Add("--runtimeknob:" + value);
        }
        args.Add("--runtimeknob:RUNTIME_IDENTIFIER=" + rid);
        string[] shims = invariant
            ? ["System.Native", "System.IO.Compression.Native", "System.Net.Security.Native", "System.Security.Cryptography.Native.OpenSsl"]
            : ["System.Native", "System.IO.Compression.Native", "System.Net.Security.Native", "System.Security.Cryptography.Native.OpenSsl", "System.Globalization.Native"];
        args.AddRange(shims.Select(name => "--directpinvoke:" + name));
        string[] archives = new[] { "libbootstrapper.o", "libRuntime.WorkstationGC.a", "libeventpipe-disabled.a",
            "libRuntime.VxsortEnabled.a", "libstandalonegc-disabled.a", "libaotminipal.a", "libstdc++compat.a",
            "libz.a", "libbrotlienc.a", "libbrotlidec.a", "libbrotlicommon.a" }
            .Concat(shims.Select(name => "lib" + name + ".a")).Select(name => Path.Combine(native, name)).ToArray();
        List<string> link = new() { obj, "-o", temporary, "-Wl,--start-group" };
        link.AddRange(archives);
        link.AddRange(["-Wl,--end-group", "-ldl", "-lrt", "-lm", "-pthread", "-pie", "-Wl,-z,relro,-z,now", "-Wl,--eh-frame-hdr", "-Wl,--gc-sections"]);
        string cc = Environment.GetEnvironmentVariable("CC") ?? "cc";
        string Identity(string path) => ProjectStateIdentity(path);
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("native-aot-v1\n" + project.Evaluation
            + string.Join('\n', args.Concat(link).Append(cc)) + "\n"
            + string.Join('\n', references.Concat(archives).Append(dll).Append(ilc).Select(Identity)))));
        if (File.Exists(output) && File.Exists(state) && File.ReadAllText(state) == signature + "\n" + Identity(output))
        { Console.WriteLine("current native " + project.Path); return; }
        // Atomic replacement keeps existing processes on their original executable inode.
        Console.WriteLine("native AOT " + project.Path);
        string response = Path.Combine(work, "native.ilc.rsp");
        File.WriteAllLines(response, args.Select(arg => "\"" + arg.Replace("\"", "\\\"") + "\""));
        ProcessResult compiled = await runner.Run("aot-" + project.AssemblyName, ilc, ["@" + response],
            Path.GetDirectoryName(project.Path)!, new Dictionary<string, string>(), TimeSpan.FromMinutes(20), cancel,
            true, count => ["--parallelism", count.ToString()]);
        if (compiled.ExitCode != 0 || compiled.TimedOut) throw new IOException("Native AOT failed; logs: " + compiled.LogPrefix);
        ProcessResult linked = await runner.Run("native-link-" + project.AssemblyName, cc, link,
            Path.GetDirectoryName(project.Path)!, new Dictionary<string, string>(), TimeSpan.FromMinutes(5), cancel);
        if (linked.ExitCode != 0 || linked.TimedOut) throw new IOException("Native host link failed; logs: " + linked.LogPrefix);
        File.Move(temporary, output, overwrite: true);
        File.WriteAllText(state, signature + "\n" + Identity(output));
    }

    private static string ProjectStateIdentity(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(stream));
    }
}
