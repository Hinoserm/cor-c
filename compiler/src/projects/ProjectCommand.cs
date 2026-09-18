using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Metadata;

namespace Corsac.Projects;

/// <summary>One compiler process, bounded source units, indexed references and a separate link.</summary>
public static class ProjectCommand
{
    public static int Run(string[] arguments)
    {
        string? path = null, output = null, framework = null;
        string configuration = "Release";
        int workers = Environment.ProcessorCount;
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
        // Ordinary MSBuild properties are ignored by .NET but select the native
        // target here. Command-line selections override project defaults.
        List<string> defaults = new();
        foreach (var property in new[] { ("CorCCpu", "--cpu="), ("CorCTune", "--tune="), ("CorCFpu", "--fpu=") })
            if (project.Properties.TryGetValue(property.Item1, out string? value) && value.Length != 0) defaults.Add(property.Item2 + value);
        defaults.AddRange(profileArguments);
        string[] cpuArguments = global::Corsac.Lang.X86.X86Cpu.Parse(defaults).Contract.Arguments();
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
            if (!owners.TryAdd(source, node)) throw new InvalidDataException("The same source participates in multiple projects: " + source);
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
        SourceIndexBuilder.Write(index, owners.Keys, project.AssemblyName, fileSymbols: symbols);
        string[] libraries = Driver.DefaultLibraries(Target.X86).ToArray();
        if (libraries.Length == 0) throw new InvalidDataException("The native runtime sources were not found");
        string compiler = typeof(Driver).Assembly.Location;
        string toolchain = ProjectState.Digest(ProjectState.FileIdentity(compiler) + "\n" + ProjectState.FileIdentity(typeof(ObjectLinkCommand).Assembly.Location));
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
        if (!Compile(runtimeArgs, runtime, ProjectState.Digest(settings), index)) return 1;
        List<string> objects = new() { runtime };
        int changed = 0;
        foreach (var owned in owners.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string source = owned.Key; EvaluatedProject owner = owned.Value;
            string destination = Path.Combine(work, ProjectState.Digest(source)[..24] + ".o");
            if (ProjectState.FileIdentity(source) != generation[source]) throw new InvalidDataException("Source changed during project compilation: " + source);
            string signature = ProjectState.Digest(settings + "\n" + generation[source] + "\n" + string.Join(";", owner.Defines)
                + "\n" + owner.WarningsAsErrors + "\n" + entries[0].Type);
            List<string> args = new() { "compile", "--nostdlib", "--obj", "--jobs", workers.ToString(), "--decl-index", index,
                "--assembly", project.AssemblyName, source };
            args.AddRange(cpuArguments);
            if (source != entries[0].Path) args.Add("--lib");
            else { args.Add("--main-type"); args.Add(entries[0].Type); }
            if (!owner.WarningsAsErrors) args.Add("-Wno-error");
            foreach (string define in owner.Defines) { args.Add("--define"); args.Add(define); }
            foreach (string library in libraries) { args.Add("--ref"); args.Add(library); }
            bool current = Current(destination, signature, index);
            if (!Compile(args, destination, signature, index)) return 1;
            if (!current) changed++;
            objects.Add(destination);
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
            link.AddRange(cpuArguments);
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
