using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Metadata;

namespace Corsac.Projects;

/// <summary>One compiler process, bounded source units, indexed references and a separate link.</summary>
public static class ProjectCommand
{
    // A native single-file image contains both compiler and linker assemblies.
    // Hash that image instead of nonexistent embedded assembly paths.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty embedded assembly locations use the native process image.")]
    private static string ToolIdentity(System.Reflection.Assembly assembly)
    {
        string path = assembly.Location;
        if (path.Length == 0)
            path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the native compiler image");
        return ProjectState.FileIdentity(path);
    }

    public static int Run(string[] arguments)
    {
        string? path = null, output = null, framework = null, targetName = null;
        string configuration = "Release";
        int workers = Environment.ProcessorCount;
        bool linkOnly = false, runtimeOnly = false;
        List<string> profileArguments = new();
        for (int i = 0; i < arguments.Length; i++)
        {
            string Value() => ++i < arguments.Length ? arguments[i] : throw new ArgumentException("Missing project option value");
            switch (arguments[i])
            {
                case "-o": output = Value(); break;
                case "--configuration": configuration = Value(); break;
                case "--framework": framework = Value(); break;
                case "--jobs": workers = int.Parse(Value()); break;
                // THE OBJECTS AS THEY ARE, LINKED AGAIN. A fix in the linker
                // changes the compiler's identity, which every unit's stamp
                // carries, so an ordinary build recompiles the whole project
                // to try one link. This links what was last compiled and
                // compiles nothing; an object that is missing is an error.
                case "--link-only": linkOnly = true; break;
                // AND THE RUNTIME WITH IT: runtime.o compiled again when its
                // sources changed, the units kept. Sound for a change to the
                // runtime's own non-generic code -- the collector, the
                // scheduler -- which no unit copies; a change to a library
                // generic, whose body every unit using it holds, needs the
                // units too.
                case "--runtime-only": linkOnly = runtimeOnly = true; break;
                case "--target": targetName = Value(); break;
                case "--cpu": case "--tune": case "--fpu":
                    profileArguments.Add(arguments[i]); profileArguments.Add(Value()); break;
                case "--enable-mmx": case "--disable-mmx": case "--enable-3dnow": case "--disable-3dnow":
                    profileArguments.Add(arguments[i]); break;
                default:
                    if (arguments[i].StartsWith("--cpu=") || arguments[i].StartsWith("--tune=") || arguments[i].StartsWith("--fpu="))
                    { profileArguments.Add(arguments[i]); break; }
                    if (arguments[i].StartsWith('-') || path is not null) throw new ArgumentException("Unexpected project argument: " + arguments[i]);
                    path = arguments[i]; break;
            }
        }
        if (path is null || workers < 1) throw new ArgumentException("project needs a .csproj path and a positive worker count");
        IReadOnlyList<EvaluatedProject> graph = ProjectGraph.Evaluate(path, configuration, framework);
        EvaluatedProject project = graph[^1];
        Parser.ProjectUsings = project.Usings;
        // Ordinary MSBuild properties are ignored by .NET but select the native
        // target here. Command-line selections override project defaults.
        List<string> defaults = new();
        foreach (var property in new[] { ("CorCCpu", "--cpu="), ("CorCTune", "--tune="), ("CorCFpu", "--fpu=") })
            if (project.Properties.TryGetValue(property.Item1, out string? value) && value.Length != 0) defaults.Add(property.Item2 + value);
        defaults.AddRange(profileArguments);
        if (targetName is null && project.Properties.TryGetValue("CorCTarget", out string? projectTarget) && projectTarget.Length != 0)
            targetName = projectTarget;
        Target target = targetName is null ? Target.X86
            : Target.ByName(targetName) ?? throw new ArgumentException("unknown target '" + targetName + "'");
        // Every unit is compiled for the target; in long mode the one CPU is
        // the K8 and the i386 profile options do not apply.
        string[] cpuArguments = target == Target.X86_64
            ? (profileArguments.Count == 0 ? new[] { "--target", "x86-64", "--cpu", "k8" }
                : throw new ArgumentException("--cpu/--tune/--fpu and the MMX/3DNow! switches are for the i386 target"))
            : global::Corsac.Lang.X86.X86Cpu.Parse(defaults).Contract.Arguments();
        // The link validates each object's CPU contract against the i386
        // profile; a long-mode object carries its own (k8/sse2).
        string[] linkArguments = target == Target.X86_64 ? Array.Empty<string>() : cpuArguments;
        if (project.OutputType is not ("Exe" or "WinExe")) throw new InvalidDataException("Standalone native library packaging is not yet implemented");
        string directory = Path.GetDirectoryName(project.Path)!;
        string work = Path.Combine(directory, "obj", "cor-c", configuration, project.Framework);
        Directory.CreateDirectory(work);
        using FileStream buildLock = new(Path.Combine(work, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        output = Path.GetFullPath(output ?? Path.Combine(directory, "bin", "cor-c", configuration, project.Framework, project.AssemblyName));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Dictionary<string, EvaluatedProject> owners = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyCollection<string>> symbols = new(StringComparer.Ordinal);
        Dictionary<string, string> types = new(StringComparer.Ordinal);
        List<(string Path, string Type)> entries = new();
        foreach (EvaluatedProject node in graph)
        foreach (string source in node.Sources)
        {
            // A SOURCE TWO PROJECTS COMPILE -- build.csproj takes the project
            // evaluator's files from the compiler's tree -- is two copies of
            // its types in .NET, one per assembly. One native image has one
            // table of types, so it is compiled once, in the first project
            // that has it (a referenced one: the graph is in build order); the
            // one difference is that .NET would give each copy its own statics.
            if (!owners.TryAdd(source, node))
            {
                Console.Error.WriteLine("corc: " + source + " is in " + owners[source].AssemblyName + " and " + node.AssemblyName + "; compiled once");
                continue;
            }
            symbols[source] = node.Defines;
            CompilationUnit header = Parser.ParseText(File.ReadAllText(source), source, node.Defines, declarationsOnly: true);
            foreach (TypeDecl type in header.Types)
            {
                string key = Binder.TypeKey(type);
                if (types.TryGetValue(key, out string? owner) && owner != node.Path)
                    throw new InvalidDataException("Cross-assembly duplicate type identity is not yet supported: " + key);
                types[key] = node.Path;
                if (node == project && type.TypeParams.Count == 0 && (project.StartupObject.Length == 0 || key == project.StartupObject)
                    && type.Members.OfType<MethodDecl>().Any(method => method.Name == "Main" && method.Mods.HasFlag(Mods.Static) && method.TypeParams.Count == 0))
                    entries.Add((source, key));
            }
        }
        if (entries.Count != 1) throw new InvalidDataException("Project must select exactly one entry point; found " + entries.Count);
        Dictionary<string, string> generation = owners.Keys.ToDictionary(source => source, ProjectState.FileIdentity, StringComparer.Ordinal);
        string index = Path.Combine(work, "declarations.idx");
        // One set of project usings for the whole compilation: a referenced
        // project with different ones would need them per file.
        foreach (EvaluatedProject node in graph)
            if (!node.Usings.SequenceEqual(project.Usings))
                throw new InvalidDataException("Referenced projects with different global usings are not yet supported: " + node.Path);
        Parser.ProjectUsings = project.Usings;
        if (!linkOnly || !File.Exists(index)) SourceIndexBuilder.Write(index, owners.Keys, project.AssemblyName, fileSymbols: symbols);
        string[] libraries = Driver.DefaultLibraries(target).ToArray();
        if (libraries.Length == 0) throw new InvalidDataException("The native runtime sources were not found");
        string toolchain = ProjectState.Digest(ToolIdentity(typeof(Driver).Assembly) + "\n" + ToolIdentity(typeof(ObjectLinkCommand).Assembly));
        string libraryState = ProjectState.Digest(string.Join("\n", libraries.Select(ProjectState.FileIdentity)));
        using DeclarationCatalog catalog = new(index);
        string interfaces = string.Join("\n", catalog.Interfaces(project.AssemblyName).OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Arity).Select(pair => pair.Key.Name + ":" + pair.Key.Arity + ":" + pair.Value));
        string settings = toolchain + "\n" + libraryState + "\n" + interfaces + "\n" + string.Join("\n", graph.Select(node => node.Evaluation))
            + "\n" + string.Join("\n", cpuArguments);
        string runtime = Path.Combine(work, "runtime.o");
        List<string> runtimeArgs = new() { "compile", "--nostdlib", "--lib", "--obj", "--jobs", workers.ToString(), "--decl-index", index, "--assembly", project.AssemblyName };
        runtimeArgs.AddRange(libraries);
        runtimeArgs.AddRange(cpuArguments);
        if (linkOnly && !runtimeOnly)
        {
            if (!File.Exists(runtime)) throw new InvalidDataException("--link-only: the runtime object was never compiled: " + runtime);
        }
        else if (!Compile(runtimeArgs, runtime, ProjectState.Digest(settings), index)) return 1;
        List<string> objects = new() { runtime };
        int changed = 0;

        // THE SOURCES IN PARALLEL, through compile-project -- the unit
        // compiler the OS build uses: one process, a worker per unit, the
        // declaration index and lexed headers shared between them, and a
        // unit skipped when its receipt and stamp (its source, its options,
        // this compiler) say its object is current. Units are handed over in
        // groups that share their options: a project's defines and warning
        // mode, and the entry unit, which names the program's Main.
        Dictionary<string, (List<string> Args, List<(string Source, string Object)> Units, bool Entry)> groups = new(StringComparer.Ordinal);
        foreach (var owned in owners.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string source = owned.Key; EvaluatedProject owner = owned.Value;
            if (ProjectState.FileIdentity(source) != generation[source]) throw new InvalidDataException("Source changed during project compilation: " + source);
            string destination = Path.Combine(work, ProjectState.Digest(source)[..24] + ".o");
            bool entry = source == entries[0].Path;
            List<string> options = new() { "--nostdlib", "--obj", "--decl-index", index, "--assembly", project.AssemblyName };
            options.AddRange(cpuArguments);
            if (entry) { options.Add("--main-type"); options.Add(entries[0].Type); }
            if (!owner.WarningsAsErrors) options.Add("-Wno-error");
            foreach (string define in owner.Defines) { options.Add("--define"); options.Add(define); }
            foreach (string use in owner.Usings) { options.Add("--using"); options.Add(use); }
            foreach (string library in libraries) { options.Add("--ref"); options.Add(library); }
            string key = (entry ? "entry\n" : "lib\n") + string.Join("\n", options);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (options, new List<(string, string)>(), entry);
                groups[key] = group;
            }
            group.Units.Add((source, destination));
            objects.Add(destination);
        }
        foreach (var group in groups.Values)
        {
            if (linkOnly)
            {
                foreach ((string Source, string Object) unit in group.Units)
                    if (!File.Exists(unit.Object)) throw new InvalidDataException("--link-only: " + unit.Source + " was never compiled (" + unit.Object + ")");
                continue;
            }
            string list = Path.Combine(work, "units-" + ProjectState.Digest(string.Join("\n", group.Args))[..16] + ".tsv");
            File.WriteAllLines(list, group.Units.Select(unit => unit.Source + "\t" + unit.Object + "\t" + unit.Object + ".deps\t" + (group.Entry ? "entry" : "lib")));
            Dictionary<string, DateTime> before = group.Units.ToDictionary(unit => unit.Object,
                unit => File.Exists(unit.Object) ? File.GetLastWriteTimeUtc(unit.Object) : DateTime.MinValue, StringComparer.Ordinal);
            int processes = Processes(workers, group.Units.Count);
            if (processes > 1)
            {
                if (!InProcesses(list, group.Units, group.Entry, group.Args, processes, workers)) return 1;
            }
            else
            {
                List<string> args = new() { "--units", list, "--jobs", workers.ToString() };
                args.AddRange(group.Args);
                if (ProjectCompile.Run(args.ToArray()) != 0) return 1;
            }
            changed += group.Units.Count(unit => File.GetLastWriteTimeUtc(unit.Object) != before[unit.Object]);
        }
        foreach (var source in generation)
            if (ProjectState.FileIdentity(source.Key) != source.Value) throw new InvalidDataException("Source changed during project compilation: " + source.Key);
        if (ProjectState.Digest(string.Join("\n", libraries.Select(ProjectState.FileIdentity))) != libraryState)
            throw new InvalidDataException("Runtime sources changed during project compilation");
        string linkSignature = ProjectState.Digest(toolchain + "\n" + string.Join("\n", objects.Select(ProjectState.FileIdentity)));
        string linkState = Path.Combine(work, "link.state");
        if (!ProjectState.Current(linkState, linkSignature, output))
        {
            string temporary = output + "." + Guid.NewGuid().ToString("N");
            List<string> link = new() { "link" }; link.AddRange(objects); link.Add("-o"); link.Add(temporary);
            link.AddRange(linkArguments);
            try
            {
                if (Driver.Run(link.ToArray()) != 0) return 1;
                File.Move(temporary, output, overwrite: true);
                ProjectState.Record(linkState, linkSignature, output);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Console.Error.WriteLine("project: " + changed + "/" + owners.Count + " source units rebuilt; output " + output);
        return 0;
    }

    /// <summary>
    /// How many processes to spread a group of units over. One, as a rule:
    /// the units share a process's caches and its heap. But a 32-bit process
    /// has four gigabytes of address space at most, and the self-hosted
    /// compiler holds a gigabyte for a unit -- so on a machine with the cores
    /// and the memory for several such spaces, a single process compiles one
    /// unit at a time and leaves the rest idle. There the group is dealt out
    /// to child processes of this same compiler, each with a space of its
    /// own. A small machine -- one processor, or memory for one space --
    /// compiles in process as before. What is compiled never changes: every
    /// unit is compiled on its own, with the same options, wherever it runs.
    /// </summary>
    private static int Processes(int workers, int units)
    {
        const long Space = 4L * 1024 * 1024 * 1024;
        if (Environment.Is64BitProcess || workers < 2 || units < 2 || Environment.ProcessPath is null) return 1;
        long memory = Corsac.Lang.Lto.MachineMemory.MachineAvailable();
        int spaces = (int)Math.Min(int.MaxValue, memory / Space);
        return Math.Max(1, Math.Min(Math.Min(workers / 2, spaces), units));
    }

    /// <summary>
    /// A group's units dealt out to `processes` child compile-project runs of
    /// this compiler, largest first and in turn so each gets its share of the
    /// big ones, each with its share of the workers. Their output is this
    /// process's; answers whether every one succeeded.
    /// </summary>
    private static bool InProcesses(string list, List<(string Source, string Object)> units, bool entry, List<string> options,
        int processes, int workers)
    {
        List<(string Source, string Object)>[] shares = new List<(string Source, string Object)>[processes];
        for (int i = 0; i < processes; i++) shares[i] = new();
        int turn = 0;
        foreach (var unit in units.OrderByDescending(unit => new FileInfo(unit.Source).Length).ThenBy(unit => unit.Source, StringComparer.Ordinal))
        {
            shares[turn].Add(unit);
            turn = (turn + 1) % processes;
        }
        int each = Math.Max(1, workers / processes);
        List<System.Diagnostics.Process> children = new();
        for (int i = 0; i < processes; i++)
        {
            if (shares[i].Count == 0) continue;
            string share = list[..^4] + "-" + i + ".tsv";
            File.WriteAllLines(share, shares[i].Select(unit => unit.Source + "\t" + unit.Object + "\t" + unit.Object + ".deps\t" + (entry ? "entry" : "lib")));
            System.Diagnostics.ProcessStartInfo start = new() { FileName = Environment.ProcessPath!, UseShellExecute = false };
            start.ArgumentList.Add("compile-project");
            start.ArgumentList.Add("--units"); start.ArgumentList.Add(share);
            start.ArgumentList.Add("--jobs"); start.ArgumentList.Add(each.ToString());
            foreach (string option in options) start.ArgumentList.Add(option);
            children.Add(System.Diagnostics.Process.Start(start)!);
        }
        bool ok = true;
        foreach (System.Diagnostics.Process child in children)
        {
            child.WaitForExit();
            if (child.ExitCode != 0) ok = false;
        }
        return ok;
    }

    private static bool Current(string output, string signature, string index)
        => ProjectState.Current(output + ".state", signature, output) && UnitDependencies.IsCurrent(output + ".deps", index);

    private static bool Compile(List<string> arguments, string output, string signature, string index)
    {
        if (Current(output, signature, index)) { Console.Error.WriteLine("project: up-to-date " + output); return true; }
        string temporary = output + "." + Guid.NewGuid().ToString("N");
        arguments.Add("-o"); arguments.Add(temporary);
        arguments.Add("--dependency-file"); arguments.Add(temporary + ".deps");
        try
        {
            if (Driver.Run(arguments.ToArray()) != 0) return false;
            File.Move(temporary, output, overwrite: true);
            File.Move(temporary + ".deps", output + ".deps", overwrite: true);
            ProjectState.Record(output + ".state", signature, output);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporary + ".deps")) File.Delete(temporary + ".deps");
        }
    }
}
