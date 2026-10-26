#nullable enable
using Corsac.Lang;
using Corsac.Lang.Metadata;
using System.Threading.Tasks;

namespace Corsac;

/// <summary>
/// The front half of a compilation: parsing, merging, monomorphisation and
/// binding, shared by every command that compiles source. Everything here
/// is target-independent; what happens to the bound tree afterwards is the
/// driver's business.
/// </summary>
public static class Frontend
{
    /// <summary>
    /// THE RULE ALONE, closed over what it reads: made inline, the lambda
    /// shared this method's closure, which holds the parsed unit, and the
    /// index the driver keeps for the whole compile kept every syntax tree
    /// and symbol of it alive through the late passes and code generation --
    /// 200 MB of the class library's unit compiled against an index.
    /// </summary>
    private static Func<string, bool> LibrarySideOf(bool every, HashSet<string> fromLibrary)
        => path => every || fromLibrary.Contains(Path.GetFullPath(path));

    /// <summary>
    /// Parses and binds a set of sources against the prelude, with the
    /// generic-method discovery rounds the binder needs. Returns null after
    /// printing diagnostics on failure.
    /// </summary>
    public static (CompilationUnit Unit, BindResult Bound)? Compile(
        IReadOnlyList<string> paths, string name, bool library,
        IReadOnlyCollection<string>? libraryPaths = null,
        IReadOnlyCollection<string>? symbols = null,
        IReadOnlyCollection<string>? elsewherePaths = null, int workers = 1, IndexedDeclarations? declarations = null,
        IReadOnlyCollection<string>? classLibraryPaths = null)
    {
        while (true)
        {
            if (declarations is not null) declarations.Passes++;
            // A PASS THAT ENDS IN A DEMAND IS THROWN AWAY WHOLE, so what the
            // discarded ones cost is its own line in --report-passes.
            Meter pass = Meter.Start();
            try
            {
                var done = CompileCore(paths, name, library, libraryPaths, symbols, elsewherePaths, workers, declarations, classLibraryPaths);
                pass.Stop("front:pass-kept");
                // WHICH TEMPLATES THE UNIT COPIED A BODY OF, for its receipt:
                // settled with the binding, and taken now.
                declarations?.PassKept();
                return done;
            }
            catch (DeclarationDemand demand) when (declarations is not null)
            {
                pass.Stop("front:pass-discarded");
                if (Switches.TraceDemand)
                    Console.Error.WriteLine("pass " + declarations.Passes + " demanded " + demand.Keys.Count + ": "
                        + string.Join(", ", demand.Keys.Select(k => k.Split('\n').Last())));
                foreach (string key in demand.Keys)
                {
                    declarations.Include(key);
                }
            }
        }
    }

    private static (CompilationUnit Unit, BindResult Bound)? CompileCore(
        IReadOnlyList<string> paths, string name, bool library,
        IReadOnlyCollection<string>? libraryPaths, IReadOnlyCollection<string>? symbols,
        IReadOnlyCollection<string>? elsewherePaths, int workers, IndexedDeclarations? declarations,
        IReadOnlyCollection<string>? classLibraryPaths)
    {
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("read-sources");
#endif
        CompilationUnit unit = new() { Line = 1, Col = 1 };
        List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources = new() { ("<prelude>", null, true, false) };

        // WHICH SOURCES ARE THE CLASS LIBRARY. Told by the driver, which is
        // the only part that knows what it links by default; matched on the
        // full path so that naming stdlib/src/System/Core.cor on the command line --
        // as the test runner does -- does not turn it into program code.
        HashSet<string> fromLibrary = new(StringComparer.Ordinal);
        foreach (string lib in libraryPaths ?? Array.Empty<string>())
        {
            if (File.Exists(lib))
            {
                fromLibrary.Add(Path.GetFullPath(lib));
            }
        }

        // WHICH SOURCES ARE THE CLASS LIBRARY ITSELF, the compiler's own
        // library sources whatever this compile marks as library: a unit
        // compiled with --lib marks its own files library too, and a part of
        // a partial type is the class library's or the program's by its file.
        HashSet<string> classLibrary = new(StringComparer.Ordinal);
        foreach (string lib in classLibraryPaths ?? Array.Empty<string>())
        {
            classLibrary.Add(Path.GetFullPath(lib));
        }

        // WHICH SOURCES ARE SOMEBODY ELSE'S CODE: given for their
        // declarations, with the code they describe in a shared object this
        // compilation links. See TypeDecl.Elsewhere.
        HashSet<string> elsewhere = new(StringComparer.Ordinal);
        foreach (string other in elsewherePaths ?? Array.Empty<string>())
        {
            if (File.Exists(other))
            {
                elsewhere.Add(Path.GetFullPath(other));
            }
        }

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"no such file: {path}");
            }
            sources.Add((Path.GetFileName(path), path,
                fromLibrary.Contains(Path.GetFullPath(path)),
                elsewhere.Contains(Path.GetFullPath(path))));
        }

#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("parse");
#endif
        List<string> programGlobals = new(), libraryGlobals = new();
        try
        {
            Meter parsing = Meter.Start();
            CompilationUnit[] parsed = ParseSources(sources, symbols, workers, declarations?.Tokens, declarations?.RecordNames(),
                declarations?.GlobalUsings(library: false), declarations?.GlobalUsings(library: true));
            parsing.Stop("front:parse");
            int sourceIndex = 0;
            foreach ((string file, string? path, bool isLibrary, bool isElsewhere) in sources)
            {
                CompilationUnit one = parsed[sourceIndex++];
                unit.Usings.AddRange(one.Usings);
                (isLibrary ? libraryGlobals : programGlobals).AddRange(one.GlobalUsings);

                // Which file each type and member came from, for diagnostics:
                // everything merges into one unit here and a line number on
                // its own then names a file the message would otherwise not.
                foreach (TypeDecl decl in one.Types)
                {
                    decl.File = file;
                    decl.SourcePath = sourceIndex == 1 ? null : Path.GetFullPath(paths[sourceIndex - 2]);
                    decl.FromLibrary = isLibrary;
                    decl.Elsewhere = isElsewhere;
                    foreach (MemberDecl member in decl.Members)
                    {
                        member.File = file;
                        // WHOSE IMPLEMENTATION, said by the member itself: a
                        // type can be two declarations merged -- the prelude's
                        // intrinsic Math and the library's Math -- and the
                        // merged type's own flag then speaks for only one of
                        // them. A --ref library's members are compiled
                        // elsewhere whatever they merge into.
                        if (isElsewhere) member.OwnedImplementation = false;
                        else if (declarations is not null) member.OwnedImplementation = true;
                    }
                }

                unit.Types.AddRange(one.Types);
                if (one.UsesDynamic && !isLibrary && !isElsewhere) unit.UsesDynamic = true;

                // Retagged the same way decl.File is above: this loop's own
                // `file` is the name a diagnostic will actually be stamped
                // with, and stamping the pragma table with anything else
                // would leave a `#pragma warning disable` unable to find the
                // warnings it was written to silence.
                foreach (PragmaWarning p in one.Pragmas)
                {
                    unit.Pragmas.Add(p with { File = file });
                }

                // Ownership has moved to unit. Do not keep the original
                // compilation roots alive through a stale parser-array slot.
                one.Types.Clear();
                one.Usings.Clear();
                one.GlobalUsings.Clear();
                one.Pragmas.Clear();
                parsed[sourceIndex - 1] = null!;
            }
            sources.Clear();
        }
        catch (CompileError e)
        {
            Console.Error.WriteLine(e.ToString());
            return null;
        }

#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("merge-expand");
#endif
        // WHAT THE INDEX GIVES IS ON THE SIDE A NAMED FILE WOULD BE, by the
        // rule the loop above applied to the files this compile was given
        // (IndexedDeclarations.LibrarySide): the library's when the file is
        // a class library source, and every one when this is a library's
        // compile that made every file it was given the library's (--lib,
        // whose driver names them all so); the program's otherwise.
        if (declarations is not null)
        {
            bool every = library && paths.All(path => fromLibrary.Contains(Path.GetFullPath(path)));
            declarations.LibrarySide = LibrarySideOf(every, fromLibrary);
        }
        declarations?.AddHeaders(unit);
        // WHAT THE INDEX BROUGHT IS LIBRARY CODE BY ITS FILE, as what was
        // parsed is: one of this compile's library files, or the class
        // library's. Left the program's, the runtime's own `Scheduler` parts
        // met as two types in its units, and a program's own Clock was not
        // moved aside for the runtime's.
        foreach (TypeDecl indexed in unit.Types)
        {
            if (!indexed.FromLibrary && indexed.SignatureOnly && indexed.Elsewhere && indexed.SourcePath is string at
                && (fromLibrary.Contains(Path.GetFullPath(at)) || classLibrary.Contains(Path.GetFullPath(at))))
            {
                indexed.FromLibrary = true;
            }
        }
        if (declarations is not null)
        {
            programGlobals.AddRange(declarations.GlobalUsings(library: false));
            libraryGlobals.AddRange(declarations.GlobalUsings(library: true));
        }
        ApplyGlobalUsings(unit, programGlobals, libraryGlobals);
        if (declarations is not null)
            unit.Types.Sort((left, right) =>
            {
                int path = StringComparer.Ordinal.Compare(left.SourcePath, right.SourcePath);
                return path != 0 ? path : left.SourceFrom.CompareTo(right.SourceFrom);
            });
        MergePartialTypes(unit, classLibrary);

        // BEFORE ANYTHING IS BOUND, because a declared setting is not
        // storage: the fields under a [Registry] class become properties
        // here, and binding a name to a field it no longer has would be too
        // late. The schemas ride along on the unit to the driver, which
        // writes them into the object file.
        List<CompileError> settings = new();

        unit.RegistrySchemas.AddRange(RegistryDeclarations.Collect(unit, settings));

        if (Report(settings))
        {
            return null;
        }

        // AND .NET'S COM INTEROP, written out as source: a wrapper class for
        // each [ComImport] interface, and the vtables and IDispatch of each
        // class COM can call (ComDeclarations). Before binding, which is
        // where the classes they write are bound with everything else.
        List<CompileError> com = new();
        ComDeclarations.Expand(unit, symbols, com);
        if (Report(com))
        {
            return null;
        }

        // AND `dynamic`'s view of a program's own classes: each member by
        // name, for a dynamic receiver bound at run time to find
        // (DynamicDeclarations). Only for a program that writes `dynamic`, and
        // the library's own [LateBound] classes.
        if (DynamicDeclarations.Wanted(unit))
        {
            List<CompileError> late = new();
            DynamicDeclarations.Expand(unit, symbols, late);
            if (Report(late))
            {
                return null;
            }
        }

        IReadOnlyList<CompileError> generic;
        Meter expanding = Meter.Start();
        unit = Monomorphiser.Expand(unit, name, library, out generic, declarations is null ? null : declarations.Require);
        expanding.Stop("front:expand");

        if (Report(generic))
        {
            return null;
        }

#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("bind-initial");
#endif
        Meter binding = Meter.Start();
        BindResult bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces,
            declarations is null ? null : declarations.RequireExtensions, declarations?.LibraryInterfaces,
                declarations is null ? null : declarations.RequireOverrides, kernelInterfaces: declarations?.KernelInterfaces, asked: declarations?.Asked, indexedForms: declarations?.InterfaceForms);
        binding.Stop("front:bind");

        // The checker's first pass discovers which generic methods were called
        // with which type arguments; each becomes a copy, and the whole thing
        // goes round again because a copy's body can ask for more. Bounded,
        // because a method that instantiates itself deeper every time has no
        // fixed point.
        //
        // ONLY THE NEW BODIES BETWEEN ROUNDS. What a round adds -- the copies,
        // and the methods of any specialised type they brought in -- is all
        // that can want a copy not yet made, so the rounds that close the set
        // of copies check only those (Binder.BindFresh), and one full binding
        // follows. A full binding each round checked every body in the unit
        // again to find a handful of new wants. Should the full binding still
        // want something, the rounds begin again from it.
        // AND A TYPE THE CHECKER SPELT (BindResult.Reexpand) goes round as a
        // copy would: the expansion makes it, and the next binding has it.
        static bool More(Lang.BindResult b) => b.Wanted.Count > 0 || b.WantedOverrides.Count > 0 || b.Reexpand;
        // A SYNTHESISED DELEGATE NOT YET DECLARED: what the binding found
        // wrong about a call of it -- `refFirst(arr) = 70` through C#'s
        // ref-returning natural type -- it found with no Invoke to ask, so
        // the round goes again with it declared, errors or none, and the next
        // binding's are the ones that stand.
        bool Undeclared(Lang.BindResult b)
            => b.AnonymousDelegates.Any(shape => !unit.Types.Any(t => t.Name == Lang.Parser.AnonymousDelegateName(shape)));
        bool Go(Lang.BindResult b) => (b.Errors.Count == 0 || Undeclared(b)) && (More(b) || Undeclared(b));
        for (int round = 0; round < 8 && Go(bound); round++)
        {
            bool made = false;
            for (int step = 0; step < 8 && Go(bound); step++)
            {
#if COR_SELFHOST_BENCHMARK
                Program.BenchmarkStage("specialise-" + round + "-" + step);
#endif
                HashSet<string> known = Names(unit.Types);
                bool reexpand = bound.Reexpand;

                // THE DELEGATES C# SYNTHESISES for natural types no Func or
                // Action can say, declared once a unit by shape.
                bool declared = false;
                foreach (string shape in bound.AnonymousDelegates)
                {
                    if (!known.Contains(Lang.Parser.AnonymousDelegateName(shape)))
                    {
                        unit.Types.AddRange(Lang.Parser.AnonymousDelegates(shape));
                        known.Add(Lang.Parser.AnonymousDelegateName(shape));
                        declared = true;
                    }
                }
                if (!Specialise(unit, bound) && !reexpand && !declared)
                {
                    break;
                }
                made = true;
                Unsettled(bound);

                // The previous round is no longer an input. Keeping its maps
                // until the replacement binding returns doubles graph pressure.
                bound.ReleaseForRebind();

                Meter again = Meter.Start();
                unit = Monomorphiser.Expand(unit, name, library, out generic, declarations is null ? null : declarations.Require);
                again.Stop("front:expand-round");

                if (Report(generic))
                {
                    return null;
                }

                // Every member, properties too: their accessors are bodies
                // the binder makes from them, and take the flag with them.
                // MADE ONES ONLY: members still deferred are not made merely to
                // be flagged. A word-shaped copy's bodies are never checked
                // (Binder.CheckBodies), and a copy per argument whose members
                // are made during the binding has them flagged then
                // (Binder.BodiesNow).
                Flag(unit.Types, known, fresh: true);

                Meter fresh = Meter.Start();
                bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces,
                    declarations is null ? null : declarations.RequireExtensions, declarations?.LibraryInterfaces,
                    declarations is null ? null : declarations.RequireOverrides, freshOnly: true, kernelInterfaces: declarations?.KernelInterfaces, asked: declarations?.Asked, indexedForms: declarations?.InterfaceForms);
                fresh.Stop("front:bind-fresh");

                Flag(unit.Types, null, fresh: false);
            }

            if (!made)
            {
                break;
            }

#if COR_SELFHOST_BENCHMARK
            Program.BenchmarkStage("bind-" + round);
#endif
            bound.ReleaseForRebind();
            Meter rebinding = Meter.Start();
            bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces,
                declarations is null ? null : declarations.RequireExtensions, declarations?.LibraryInterfaces,
                declarations is null ? null : declarations.RequireOverrides, kernelInterfaces: declarations?.KernelInterfaces, asked: declarations?.Asked, indexedForms: declarations?.InterfaceForms);
            rebinding.Stop("front:bind-round");
            if (Switches.TraceWants)
            {
                foreach (var w in bound.Wanted)
                    Console.Error.WriteLine("full bind still wants " + w.Item2.Name + " in " + w.Item1.Line + " of " + w.Item2.File);
                foreach (var w in bound.WantedOverrides)
                    Console.Error.WriteLine("full bind still wants override " + w.Item1.Name + "." + w.Item2.Name + " as " + w.Item4);
            }
        }

        // WHAT THE UNIT GREW TO, for --report-unit: its declarations after
        // expansion by kind, and the members each kind carries -- where a
        // unit's memory goes before a line of it is lowered.
        if (Switches.ReportUnit)
        {
            // Counted as they stand: a deferred list is not made to be counted.
            foreach (var group in unit.Types.GroupBy(t => t.TypeParams.Count > 0 ? "template"
                         : t.MembersPending ? (t.Canon is not null ? "shared-copy-deferred" : t.SignatureOnly ? "imported-deferred" : "specialised-deferred")
                         : t.Canon is not null ? "shared-copy" : t.Specialised ? "specialised" : t.SignatureOnly ? "imported" : "own"))
            {
                Console.Error.WriteLine("unit " + group.Key + ": " + group.Count() + " types, "
                    + group.Sum(t => t.MembersMade.Count) + " members, "
                    + group.Sum(t => t.MembersMade.OfType<MethodDecl>().Count(m => m.Body is { Statements.Count: > 0 })) + " with bodies");
            }
        }

        // WARNINGS ARE ERRORS. Every warning the binder raises is a statement
        // about the program that is true -- a value that may be null where
        // one may not be, a name that is never read -- and a program with a
        // true statement like that in it is not finished. -Wno-error shows
        // them as warnings and goes on, for the build that is fixing them.
        foreach (CompileError warning in bound.Warnings)
        {
            if (WarningsAreErrors)
            {
                // Every warning now carries a code -- "): warning CS8602: " --
                // so the word alone, not the colon that used to sit right
                // after it, is what marks where "error" replaces "warning".
                Console.Error.WriteLine(warning.ToString().Replace("): warning ", "): error "));
            }
            else
            {
                Console.Error.WriteLine(warning.ToString());
            }
        }

        if (Report(bound.Errors))
        {
            return null;
        }

        if (WarningsAreErrors && bound.Warnings.Count > 0)
        {
            Console.Error.WriteLine(bound.Warnings.Count == 1
                ? "corc: 1 warning, and warnings are errors (-Wno-error to allow them)"
                : $"corc: {bound.Warnings.Count} warnings, and warnings are errors (-Wno-error to allow them)");
            return null;
        }

#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("frontend-complete");
#endif
        return (unit, bound);
    }

    /// <summary>Whether a warning fails the compile. The default, and the rule.</summary>
    public static bool WarningsAreErrors { get; set; } = true;

    /// <summary>
    /// GLOBAL USING DIRECTIVES, which C# applies to every file of the
    /// compilation they are written in: added to every file scope of their
    /// side -- a program's to the program's files, a library's to its own,
    /// a library being another assembly -- at the top of each, where a
    /// file's own directives outside any namespace are. A unit compiled by
    /// itself has the other files' from the index (IndexedDeclarations.
    /// GlobalUsings). Read before as the file's own, a `global using` in one
    /// file of a project imported nothing into the rest.
    /// </summary>
    private static void ApplyGlobalUsings(CompilationUnit unit, List<string> program, List<string> library)
    {
        if (program.Count == 0 && library.Count == 0) return;
        HashSet<FileScope> seen = new(ReferenceEqualityComparer.Instance);
        foreach (TypeDecl type in unit.Types)
        {
            if (type.Scope is not FileScope scope || !seen.Add(scope)) continue;
            foreach (string use in type.FromLibrary ? library : program)
            {
                int equals = use.IndexOf('=');
                if (equals > 0)
                {
                    (string, string, string) alias = ("", use[..equals], use[(equals + 1)..]);
                    if (!scope.Aliases.Contains(alias)) scope.Aliases.Add(alias);
                }
                else if (!scope.Imports.Contains(("", use))) scope.Imports.Add(("", use));
            }
        }
    }

    /// <summary>
    /// THE `global using` ALIASES OF A BUILT-IN OR NAMED TYPE, one side's --
    /// `global using Word = uint;` -- from the tokens of every file of the
    /// side, read before any is parsed (Parser.GlobalTypeAliases): an alias
    /// of a type is its type, put where a file names it as one at parse
    /// time, so a file parsed before the one that declares it did not see
    /// it. A unit compiled apart has the rest from the index. The target is
    /// kept as text, a dotted name only as a name in System, the rule a
    /// file's own alias has (Parser.ParseUsings).
    /// </summary>
    private static (Dictionary<string, string> Program, Dictionary<string, string> Library) GlobalTypeAliases(
        List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources,
        List<string>? indexedProgram, List<string>? indexedLibrary, IReadOnlyCollection<string>? symbols)
    {
        Dictionary<string, string> program = new(StringComparer.Ordinal), library = new(StringComparer.Ordinal);
        foreach ((string name, string? path, bool fromLibrary, bool _) in sources)
        {
            if (path is null) continue;
            string text = SourceText(path);
            if (!text.Contains("global using", StringComparison.Ordinal)) continue;
            List<Token> read;
            // With the compile's own symbols: a `#if` around an alias reads as it will be parsed.
            try { read = Lexer.Tokenize(text, name, 1, 1, symbols); }
            catch (CompileError) { continue; }
            for (int i = 0; i + 3 < read.Count; i++)
            {
                if (read[i] is not { Kind: Tok.Ident, Text: "global" } || read[i + 1].Kind != Tok.KwUsing
                    || read[i + 2].Kind != Tok.Ident || read[i + 3].Kind != Tok.Assign) continue;
                List<string> words = new();
                for (int j = i + 4; j < read.Count && read[j].Kind is not (Tok.Semi or Tok.End); j++) words.Add(read[j].Text);
                if (GlobalAliasTarget(string.Join(" ", words)) is string target) (fromLibrary ? library : program)[read[i + 2].Text] = target;
            }
        }
        foreach ((List<string>? given, Dictionary<string, string> side) in new[] { (indexedProgram, program), (indexedLibrary, library) })
        {
            foreach (string use in given ?? new List<string>())
            {
                int equals = use.IndexOf('=');
                if (equals > 0 && GlobalAliasTarget(use[(equals + 1)..]) is string target) side.TryAdd(use[..equals], target);
            }
        }
        return (program, library);
    }

    /// <summary>The text of an alias's target where it is a type read at the use (no dotted name but one of System's), else null.</summary>
    private static string? GlobalAliasTarget(string text)
    {
        string joined = text.Replace(" ", "");
        int dot = joined.IndexOf('.');
        if (dot >= 0 && !(joined.StartsWith("System.", StringComparison.Ordinal) && joined.IndexOf('.', 7) < 0)) return null;
        return text;
    }

    private static CompilationUnit[] ParseSources(
        List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources,
        IReadOnlyCollection<string>? symbols, int workers, SyntaxTokenCache? tokens = null, List<string>? indexedRecords = null,
        List<string>? indexedProgramGlobals = null, List<string>? indexedLibraryGlobals = null)
    {
        HashSet<string> records = RecordNames(sources);
        if (indexedRecords is not null) records.UnionWith(indexedRecords);
        Parser.ProjectRecords = records;
        (Dictionary<string, string> program, Dictionary<string, string> library) = GlobalTypeAliases(sources, indexedProgramGlobals, indexedLibraryGlobals, symbols);
        // What a declaration header read from the index (another thread, no side) falls back on.
        Dictionary<string, string> everywhere = new(library, StringComparer.Ordinal);
        foreach ((string alias, string aimed) in program) everywhere[alias] = aimed;
        Parser.IndexedGlobalTypeAliases = everywhere;
        CompilationUnit[] parsed = new CompilationUnit[sources.Count];
        if (workers <= 1)
        {
            for (int i = 0; i < sources.Count; i++)
                parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols, tokens, sources[i].Elsewhere, sources[i].FromLibrary ? library : program);
            return parsed;
        }
        CompileError?[] failures = new CompileError?[sources.Count];
        int active = Math.Min(workers, sources.Count);
        Task[] tasks = new Task[active];
        for (int worker = 0; worker < active; worker++)
        {
            int lane = worker;
            tasks[worker] = Task.Run(() =>
            {
                for (int i = lane; i < sources.Count; i += active)
                {
                    try { parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols, tokens, sources[i].Elsewhere, sources[i].FromLibrary ? library : program); }
                    catch (CompileError error) { failures[i] = error; }
                }
            });
        }
        Task.WhenAll(tasks).Wait();
        // Preserve serial diagnostic order regardless of task completion order.
        for (int i = 0; i < failures.Length; i++)
            if (failures[i] is CompileError error) throw error;
        return parsed;
    }

    /// <summary>
    /// One source, parsed. A source compiled ELSEWHERE -- a --ref library,
    /// the whole runtime and class library to every unit of a project -- is
    /// read as the declaration index reads its slices: every declaration, its
    /// constants and initialisers, and the bodies of templates, which copies
    /// are made from here; not the bodies of its ordinary methods, which this
    /// compilation never checks or emits. Kept, they were most of a unit's
    /// syntax tree, held for the whole compilation and copied whole by every
    /// round of generic expansion.
    /// </summary>
    /// <summary>
    /// The names of the record classes the program's files declare, from
    /// their tokens (`record [class] Name`), for a record whose base is one
    /// declared in another file (Parser.ProjectRecords). The library's are
    /// left out: no program record derives from one of them. A unit
    /// compiled apart has the rest from the index (IndexedDeclarations.
    /// RecordNames).
    /// </summary>
    private static HashSet<string> RecordNames(List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach ((string name, string? path, bool fromLibrary, bool _) in sources)
        {
            if (path is null || fromLibrary) continue;
            string text = SourceText(path);
            if (!text.Contains("record", StringComparison.Ordinal)) continue;
            // A file that does not lex is reported by its parse, in order.
            List<Token> read;
            try { read = Lexer.Tokenize(text, name); }
            catch (CompileError) { continue; }
            for (int i = 0; i + 1 < read.Count; i++)
            {
                if (read[i] is not { Kind: Tok.Ident, Text: "record" }) continue;
                int j = read[i + 1].Kind == Tok.KwClass ? i + 2 : i + 1;
                if (j < read.Count && read[j].Kind == Tok.Ident) names.Add(read[j].Text);
            }
        }
        return names;
    }

    private static CompilationUnit ParseSource(string name, string? path, IReadOnlyCollection<string>? symbols,
        SyntaxTokenCache? tokens = null, bool elsewhere = false, IReadOnlyDictionary<string, string>? globalAliases = null)
    {
        Parser.GlobalTypeAliases = globalAliases;
        // Source text belongs to the active parser, not the project. Retain
        // paths in the queue so completed files release their text before the
        // next file is read. At most one source buffer per worker is live.
        string text = path is null ? Prelude.Source : SourceText(path);
        return tokens is null ? Parser.ParseText(text, name, symbols, elsewhere, elsewhere)
            : tokens.Parse(text, name, symbols, elsewhere, elsewhere);
    }

    /// <summary>
    /// A source file's text, as File.ReadAllText reads it. ITS BYTES, THEN ONE
    /// STRING: ReadAllText decodes into a growing builder and copies the text
    /// out of it, twice the text in characters on top of the string -- and
    /// every source is read again in every pass a demand throws away.
    /// A UTF-16 or UTF-32 file, told by its byte-order mark, is left to
    /// ReadAllText, which reads those; a UTF-8 one has its mark dropped as
    /// ReadAllText drops it.
    /// </summary>
    private static string SourceText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFE && bytes[1] == 0xFF || bytes[0] == 0xFF && bytes[1] == 0xFE)
            || bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return File.ReadAllText(path);
        }
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }

    private static bool Report(IReadOnlyList<CompileError> errors)
    {
        foreach (CompileError e in errors)
        {
            Console.Error.WriteLine(e.ToString());
        }
        return errors.Count > 0;
    }

    /// <summary>
    /// THE NAMES OF A UNIT'S TYPES, and (Flag) its new members marked fresh
    /// or not, each walk in a frame of its own. Written inline in CompileCore
    /// they left the walk behind -- a list's enumerator is a struct holding
    /// the list it walks, an enumeration over one an object holding it -- in
    /// a slot of a frame with handlers, whose slots the collector is told of
    /// for as long as the frame runs, read again or not. The list the last
    /// walk of a round held was the expansion before, and through its
    /// declarations, their bodies and the facts the binding left on them the
    /// whole of the round before stayed alive beside the next, declarations
    /// and symbols: a compiler unit's live heap went from 145 megabytes at
    /// its first round's binding to 245 at its second's.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static HashSet<string> Names(List<TypeDecl> types)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (TypeDecl t in types) names.Add(t.Name);
        return names;
    }

    /// <summary>
    /// Every made member of a unit's types flagged fresh, or not: with
    /// <paramref name="known"/>, of the types it does not name, the ones the
    /// round has just added. In a frame of its own, as Names says why.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Flag(List<TypeDecl> types, HashSet<string>? known, bool fresh)
    {
        foreach (TypeDecl t in types)
        {
            if (known is not null && known.Contains(t.Name)) continue;
            foreach (MemberDecl m in t.MembersMade) m.Fresh = fresh;
        }
    }

    /// <summary>
    /// Compiles one or more source files into a single program.
    ///
    /// Linking happens at the AST level rather than by combining separate
    /// object files: every unit is parsed, their declarations merged, and the
    /// whole thing bound together. That means a library and its user are
    /// type-checked against each other rather than against a header, and it is
    /// what lets the standard library be written in C# itself.
    /// </summary>
    /// <summary>
    /// Compiles a copy of each generic method the checker asked for, and points
    /// the calls at them.
    ///
    /// Answers whether anything new was made, so the round loop knows to stop.
    /// A call asking for a copy that already exists is repointed and nothing is
    /// added -- which is what makes the loop terminate for every program that
    /// terminates.
    /// </summary>
    /// <summary>
    /// THE BODIES THAT ASKED FOR A COPY ARE CHECKED AGAIN with the new ones.
    /// What a call resolves to can change once the copy it wanted exists:
    /// `string.Join(",", xs.OrderBy(x => x))` meets IOrderedEnumerable of int
    /// only when OrderBy's copy brings it, and Join's own generic overload,
    /// unreachable before, is the one chosen after. Left to the full binding
    /// to notice, one such call cost a second full binding of everything.
    /// Flagged before the rewrite, which carries the flag to the copies it
    /// makes; an accessor's flag is its property's.
    /// </summary>
    private static void Unsettled(Lang.BindResult bound)
    {
        foreach ((Lang.TypeSymbol? owner, Lang.MethodDecl body) in bound.Wanting)
        {
            body.Fresh = true;
            if (owner?.Decl is not Lang.TypeDecl declared || declared.Members.Contains(body)) continue;
            foreach (Lang.PropertyDecl p in declared.Members.OfType<Lang.PropertyDecl>())
            {
                if (Lang.NameTable.Accessor("get_", p.Name) == body.Name || Lang.NameTable.Accessor("set_", p.Name) == body.Name)
                    p.Fresh = true;
            }
        }
    }

    private static bool Specialise(Lang.CompilationUnit unit, Lang.BindResult bound)
    {
        bool made = false;

        // WHICH NAMES ARE VALUE TYPES, for the copies' `T?` (Monomorphiser.Specialise).
        HashSet<string> values = new(StringComparer.Ordinal);
        foreach (Lang.TypeDecl t in unit.Types)
        {
            if (t.Kind is Lang.TypeKind.Struct or Lang.TypeKind.Enum)
            {
                values.Add(t.Name);
                if (t.Outer is not null) values.Add(t.Outer + "." + t.Name);
            }
        }

        // A copy in the canonical class too, when the owner shares its code
        // with one (see the comment where it is called); answers whether it
        // made one.
        bool CanonicalTwin(Lang.TypeDecl owner, Lang.MethodDecl template, List<Lang.TypeRef> args, string wanted)
        {
            if (owner.Canon is string canonical)
            {
                Lang.TypeDecl? shared = unit.Types.FirstOrDefault(t => t.Name == canonical);

                if (shared is not null && !shared.Members.Any(m => m.Name == wanted))
                {
                    Lang.MethodDecl? origin = shared.Members.OfType<Lang.MethodDecl>()
                        .FirstOrDefault(m => m.Name == template.Name
                            && m.TypeParams.Count == template.TypeParams.Count
                            && (template.TemplateIndex < 0 || m.TemplateIndex == template.TemplateIndex));

                    if (origin is not null)
                    {
                        Lang.MethodDecl twin = Lang.Monomorphiser.Specialise(origin, args, wanted, values, classDictionary: true);
                        shared.BodiesCopied();

                        twin.File = origin.File;
                        twin.LocalCopy = true;
                        twin.Fresh = true;
                        shared.Members.Add(twin);
                        return true;
                    }
                }
            }
            return false;
        }

        // THE INSTANTIATION'S OWN CODE: a generic method's copy on a
        // word-shaped instantiation, every argument known -- no __canon in
        // the receiver's arguments or the method's -- so `new Derived<T, K>()`
        // in it is Derived<string, K>, made over what T is. Compiled as its
        // own (MethodDecl.OwnCode), never sent to a twin over __canon. Not
        // over an erased argument (Holder<List<__canon>>): that copy's casts
        // to T would be unchecked words.
        static bool OwnCode(Lang.TypeDecl owner, List<Lang.TypeRef> args)
            => owner.Canon is not null && owner.Kind != Lang.TypeKind.Interface
            && !owner.TemplateArgs.Any(Lang.Monomorphiser.MentionsCanon) && !args.Any(Lang.Monomorphiser.MentionsCanon);

        // WHOSE MEMBER IS IT? The template is a member of exactly one
        // declaration, and the copy goes beside it so the call reaches it the
        // same way -- same receiver, same static class. Found by a map made
        // once: searching every type's members for every call was a scan of
        // the whole unit per want.
        Dictionary<Lang.MemberDecl, Lang.TypeDecl> owners = new(ReferenceEqualityComparer.Instance);
        // Made members only: a template the checker wanted a copy of is one it
        // found, and it found it in a list that was made for the asking.
        foreach (Lang.TypeDecl t in unit.Types)
        {
            foreach (Lang.MemberDecl m in t.MembersMade) owners.TryAdd(m, t);
        }

        foreach ((Lang.CallExpr call, Lang.MethodDecl template, List<Lang.TypeRef> args) in bound.Wanted)
        {
            if (!owners.TryGetValue(template, out Lang.TypeDecl? owner))
            {
                continue;
            }

            // THE COPY IS NAMED FOR ITS TEMPLATE AS WELL AS ITS ARGUMENTS.
            //
            // `Join<T>(string, List<T>)` and `Join<T>(string, T[])` are two
            // overloads, and both specialise at T = Node -- so a name made
            // only of `Join` and `Node` gives one copy two meanings, and the
            // second call is told that Join$Node does not accept an array.
            // The member's position says which overload it came from.
            // A SHARED METHOD COPY'S NAME SAYS SO (Monomorphiser.CopyName),
            // as the binder spelt it.
            // A CANONICAL CLASS'S COPY TAKES A DICTIONARY whatever its
            // arguments (Monomorphiser.ClassDictionary).
            string wanted = Lang.Monomorphiser.CopyName(template.Name, args, owner.Members.IndexOf(template),
                Lang.Monomorphiser.ClassDictionary(owner, template));
            bool own = OwnCode(owner, args);

            if (!owner.Members.Any(m => m.Name == wanted))
            {
                Lang.MethodDecl copy = Lang.Monomorphiser.Specialise(template, args, wanted, values,
                    classDictionary: owner.CanonMadeWritten is not null);

                copy.File = template.File;
                copy.OwnCode = own;
                // A BODY OF THE OWNER'S TEMPLATE IS THIS UNIT'S NOW, and the
                // unit's receipt depends on its text (TypeDecl.BodiesUsed).
                owner.BodiesCopied();

                // OURS, wherever it sits. The owner may be a library's type,
                // and a member of a library's type is normally an import --
                // but the library never compiled this copy, so an import of it
                // is a name nothing provides. The library's OWN copies arrive
                // through its header under this very name and are found by the
                // check above, so this line is reached exactly when the copy
                // has to be made here.
                copy.LocalCopy = true;
                copy.Fresh = true;
                owner.Members.Add(copy);
                RehostLocals(owner, template, copy, args, values);
                made = true;
            }

            // AND A COPY IN THE CANONICAL CLASS, when the owner is a
            // word-shaped instantiation that shares its code.
            //
            // `Store$Person` has no instructions of its own: Canon says every
            // one of its methods is compiled as `Store$__canon`'s member at the
            // same position, and a call is redirected there by index. A copy
            // added only here is therefore a declaration the binder can see and
            // the code generator never lays down -- the link then fails on a
            // symbol nothing defines, which is what a generic method inside a
            // generic class did every time.
            //
            // The twin is made from the CANONICAL class's own template member,
            // so its body is written over __canon rather than over Person, and
            // it is given the same name so the redirect -- which matches on
            // index and name together -- finds it.
            // NOT FOR AN INSTANTIATION'S OWN CODE (OwnCode), which is laid
            // down beside it.
            if (!own) made |= CanonicalTwin(owner, template, args, wanted);

            // AND THE CALL NAMES THE COPY. The receiver and the arguments are
            // untouched; only which member is being asked for changes.
            switch (call.Target)
            {
                case Lang.MemberExpr member:
                    member.Name = wanted;
                    break;

                case Lang.NameExpr bare:
                    bare.Name = wanted;
                    break;
            }
        }

        // THE OVERRIDES A GENERIC VIRTUAL CALL MAY LAND ON, which no call
        // names: the call keeps naming the method and is dispatched among
        // these by type (Binder.GenericVirtualCall). Made beside their
        // templates exactly as the copies above are, under the name the
        // binder looks for.
        foreach ((Lang.TypeDecl owner, Lang.MethodDecl template, List<Lang.TypeRef> args, string wanted) in bound.WantedOverrides)
        {
            if (!unit.Types.Contains(owner) || owner.Members.Any(m => m.Name == wanted))
            {
                continue;
            }

            Lang.MethodDecl copy = Lang.Monomorphiser.Specialise(template, args, wanted, values,
                classDictionary: owner.CanonMadeWritten is not null);

            copy.File = template.File;
            copy.OwnCode = OwnCode(owner, args);
            owner.BodiesCopied();
            copy.LocalCopy = true;
            copy.Fresh = true;
            owner.Members.Add(copy);
            RehostLocals(owner, template, copy, args, values);
            made = true;
            if (!copy.OwnCode) made |= CanonicalTwin(owner, template, args, wanted);
        }
        return made;
    }

    /// <summary>
    /// THE GENERIC LOCAL FUNCTIONS WRITTEN IN A GENERIC METHOD GO WITH EACH
    /// COPY OF IT, as Roslyn gives a local function the type parameters of the
    /// method around it. Each one hoisted out of the template -- and out of
    /// those, however deep -- is copied beside the copy with the copy's type
    /// arguments put in for the template's type parameters, its own kept, so
    /// what it captured is typed as the copy has it and it is still generic
    /// in its own. The copy, and each carried function, says which carried
    /// function each written name now means (MethodDecl.Rehosted).
    /// </summary>
    private static void RehostLocals(Lang.TypeDecl owner, Lang.MethodDecl template, Lang.MethodDecl copy,
                                     List<Lang.TypeRef> args, HashSet<string> values)
    {
        List<Lang.MethodDecl> carried = new();
        HashSet<string> parents = new(StringComparer.Ordinal) { template.HoistKey };
        for (bool more = true; more;)
        {
            more = false;
            foreach (Lang.MethodDecl m in owner.Members.OfType<Lang.MethodDecl>())
            {
                if (m.HoistedIn is string parent && parents.Contains(parent) && !carried.Contains(m))
                {
                    carried.Add(m);
                    parents.Add(m.HoistKey);
                    more = true;
                }
            }
        }

        // What each written name meant in the template, and means now.
        Dictionary<string, string> rehosted = new(template.Rehosted, StringComparer.Ordinal);
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (Lang.MethodDecl m in carried)
        {
            renamed[m.Name] = copy.Name + "$" + m.Name;
        }
        foreach (string written in rehosted.Keys.ToList())
        {
            if (renamed.TryGetValue(rehosted[written], out string? now)) rehosted[written] = now;
        }
        foreach ((string was, string now) in renamed)
        {
            rehosted[was] = now;
        }

        foreach (string written in rehosted.Keys) copy.WritableRehosted[written] = rehosted[written];
        foreach (Lang.MethodDecl m in carried)
        {
            Lang.MethodDecl made = Lang.Monomorphiser.Rehost(m, template.TypeParams, args, renamed[m.Name], values);
            made.File = m.File;
            made.Scope = m.Scope;
            made.Namespace = m.Namespace;
            made.LocalCopy = true;
            made.OwnCode = copy.OwnCode;
            made.Fresh = true;
            made.HoistedIn = m.HoistedIn == template.HoistKey ? copy.HoistKey : renamed[m.HoistedIn!];
            made.WritableRehosted.Clear();
            foreach ((string written, string now) in rehosted) made.WritableRehosted[written] = now;
            owner.Members.Add(made);
        }
    }

    /// <summary>
    /// Puts a carrier's bodied generic methods into the consumer's view of the
    /// type they belong to.
    ///
    /// The header declared the type with those methods as bare signatures; the
    /// carrier brings the same members with their bodies. Each is matched by
    /// name and shape and REPLACED IN ITS PLACE, because a member's position is
    /// part of a specialisation's name -- the library numbered its own
    /// precompiled copies by it, and both sides have to keep counting alike.
    /// A member the header does not have is appended, which keeps the numbers
    /// of everything before it.
    /// </summary>
    private static void MergeCarrier(Lang.CompilationUnit unit, Lang.TypeDecl carrier)
    {
        Lang.TypeDecl? into = unit.Types.FirstOrDefault(
            t => t.External && t.TypeParams.Count == 0
              && t.Name == carrier.Name && t.Outer == carrier.Outer);

        if (into is null)
        {
            return;
        }

        foreach (Lang.MethodDecl m in carrier.Members.OfType<Lang.MethodDecl>())
        {
            int at = into.Members.FindIndex(x => x is Lang.MethodDecl had
                && had.Name == m.Name
                && had.TypeParams.Count == m.TypeParams.Count
                && had.Params.Count == m.Params.Count
                && had.Params.Zip(m.Params).All(
                       p => p.First.Type.ToString() == p.Second.Type.ToString()));

            if (at >= 0)
            {
                into.Members[at] = m;
            }
            else
            {
                into.Members.Add(m);
            }
        }
    }

    /// <summary>
    /// Combines the source parts of a C# partial type before binding.
    ///
    /// Parsing files separately preserves useful diagnostics, but every later
    /// phase must see a partial type as one declaration: one field layout, one
    /// overload set, and one constructor-initialiser sequence. Non-partial
    /// duplicates stay separate so the binder reports them normally.
    /// </summary>
    private static void MergePartialTypes(Lang.CompilationUnit unit, HashSet<string> classLibrary)
    {
        // ANY SOURCE OF THE COMPILER'S LIBRARY ROOT IS THE CLASS LIBRARY'S, the
        // class library's and the runtime's alike, whichever of them this
        // target's default set names: CORSAC's kernel compiles io.cor and
        // Linux's files.cor as units of its own beside Core.cor and
        // baremetal.cor, and DateTime's two parts, and Os's, were taken for
        // two types each -- the part in the default set the library's, the
        // other the program's -- and declared twice in every unit.
        string? root = Driver.LibraryRoot();
        string[] trees = root is null ? Array.Empty<string>() : new[]
        {
            Path.Combine(Path.GetFullPath(root), "stdlib", "src") + Path.DirectorySeparatorChar,
            Path.Combine(Path.GetFullPath(root), "runtime", "src") + Path.DirectorySeparatorChar,
        };
        bool UnderLibraryRoot(string path) => trees.Any(tree => path.StartsWith(tree, StringComparison.Ordinal));

        Dictionary<string, Lang.TypeDecl> first = new(StringComparer.Ordinal);
        List<Lang.TypeDecl> merged = new();

        foreach (Lang.TypeDecl part in unit.Types)
        {
            // THE LIBRARY'S PARTS AND THE PROGRAM'S APART, by the file each
            // came from -- not by how this compile marks it: a --lib unit of a
            // program marks its own part library and the part the index
            // brought from the program's next file not, and the two are one
            // type. A program's own `Os` sorted before the runtime's two
            // parts kept them from each other, and both were declared twice.
            bool libraryPart = classLibrary.Count == 0
                ? part.FromLibrary
                : part.File == "<prelude>" || part.SourcePath is string at && (classLibrary.Contains(at) || UnderLibraryRoot(at));
            string key = Lang.Binder.TypeKey(part) + (libraryPart ? "\0library" : "");

            if (!first.TryGetValue(key, out Lang.TypeDecl? into))
            {
                first[key] = part;
                merged.Add(part);
                continue;
            }

            // Never a program's part into the library's type, or the
            // library's into the program's: the program's is its own, moved
            // aside (TypeDecl.MovedAside), however it is declared.
            bool compatible = into.Mods.HasFlag(Lang.Mods.Partial)
                && part.Mods.HasFlag(Lang.Mods.Partial)
                && into.Kind == part.Kind
                && into.TypeParams.Count == part.TypeParams.Count
                && into.TypeParams.Zip(part.TypeParams)
                    .All(p => p.First.Name == p.Second.Name);

            if (!compatible)
            {
                merged.Add(part);
                continue;
            }

            foreach (Lang.TypeRef basis in part.Bases)
            {
                if (!into.Bases.Any(had => had.ToString() == basis.ToString()))
                {
                    into.WritableBases.Add(basis);
                }
            }

            into.Members.AddRange(part.Members);
            into.Mods |= part.Mods;
            into.FromLibrary |= part.FromLibrary;
            into.MemberwiseCopied |= part.MemberwiseCopied;
            into.SignatureOnly &= part.SignatureOnly;

            // AN IMPORTED TEMPLATE'S BODIES ARE THE MERGED TYPE'S NOW, so a
            // copy of any of them is made out of it: the part says it used
            // its bodies whenever the merged type says so (TypeDecl.BodiesUsed).
            if (part.IndexedTemplate is not null)
            {
                into.IndexedTemplate ??= part.IndexedTemplate;
                part.IndexedTemplate = into.IndexedTemplate;
            }
        }

        unit.Types.Clear();
        unit.Types.AddRange(merged);
        foreach (Lang.TypeDecl type in merged) MergePartialMethods(type);
    }

    /// <summary>
    /// A PARTIAL METHOD IS ONE METHOD (C# 15.6.9): its declaration and its
    /// implementation, met as two members once the type's parts are one,
    /// become the implementation; a declaration with no implementation
    /// anywhere is a method that does nothing, and calls of it are dropped
    /// (MethodDecl.PartialWithoutBody). Two of them made two methods of one
    /// signature, and one of them had no body to emit.
    /// </summary>
    private static void MergePartialMethods(Lang.TypeDecl type)
    {
        Dictionary<string, Lang.MethodDecl>? seen = null;
        List<Lang.MemberDecl>? dropped = null;
        foreach (Lang.MemberDecl member in type.Members)
        {
            if (member is not Lang.MethodDecl method || !method.Mods.HasFlag(Lang.Mods.Partial)) continue;
            string key = method.Name + "`" + method.TypeParams.Count + "(" + string.Join(",", method.Params.Select(p =>
                (p.IsOut ? "out " : p.IsRef ? "ref " : p.IsReadOnlyRef ? "in " : "") + p.Type)) + ")";
            seen ??= new Dictionary<string, Lang.MethodDecl>(StringComparer.Ordinal);
            if (!seen.TryGetValue(key, out Lang.MethodDecl? other))
            {
                seen[key] = method;
                continue;
            }
            // The one with the body; else another unit's (read from the
            // index, its body there), which may well have one.
            Lang.MethodDecl implementation = method.Body is not null ? method
                : other.Body is not null ? other
                : method.OwnedImplementation == false ? method : other;
            Lang.MethodDecl declaration = ReferenceEquals(implementation, method) ? other : method;
            (dropped ??= new()).Add(declaration);
            seen[key] = implementation;
        }
        if (seen is null) return;
        if (dropped is not null) type.Members.RemoveAll(m => dropped.Contains(m));
        foreach (Lang.MethodDecl lone in seen.Values)
        {
            if (lone.Body is null && lone.OwnedImplementation != false && type.Kind != Lang.TypeKind.Interface)
            {
                lone.Body = new Lang.Block { Line = lone.Line, Col = lone.Col };
                lone.PartialWithoutBody = true;
            }
        }
    }
}

/// <summary>
/// Time and allocation over a stretch of the front end, reported with the
/// optimiser's passes when --report-passes is given, and nothing otherwise.
/// </summary>
internal readonly struct Meter
{
    private readonly long _ticks;
    private readonly long _bytes;

    private Meter(long ticks, long bytes)
    {
        _ticks = ticks;
        _bytes = bytes;
    }

    public static Meter Start() => Corsac.Lang.Opt.Pipeline.Accounting
        ? new Meter(System.Diagnostics.Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes())
        : default;

    public void Stop(string name)
    {
        if (!Corsac.Lang.Opt.Pipeline.Accounting) return;
        Corsac.Lang.Opt.Pipeline.Account(name, System.Diagnostics.Stopwatch.GetTimestamp() - _ticks,
            GC.GetTotalAllocatedBytes() - _bytes);
    }
}
