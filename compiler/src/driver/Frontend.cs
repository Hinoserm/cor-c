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
    /// Parses and binds a set of sources against the prelude, with the
    /// generic-method discovery rounds the binder needs. Returns null after
    /// printing diagnostics on failure.
    /// </summary>
    public static (CompilationUnit Unit, BindResult Bound)? Compile(
        IReadOnlyList<string> paths, string name, bool library,
        IReadOnlyCollection<string>? libraryPaths = null,
        IReadOnlyCollection<string>? symbols = null,
        IReadOnlyCollection<string>? elsewherePaths = null, int workers = 1, IndexedDeclarations? declarations = null)
    {
        declarations?.PrefetchLearned();
        while (true)
        {
            if (declarations is not null) declarations.Passes++;
            // A PASS THAT ENDS IN A DEMAND IS THROWN AWAY WHOLE, so what the
            // discarded ones cost is its own line in CORC_REPORT_PASSES.
            Meter pass = Meter.Start();
            try
            {
                var done = CompileCore(paths, name, library, libraryPaths, symbols, elsewherePaths, workers, declarations);
                pass.Stop("front:pass-kept");
                return done;
            }
            catch (DeclarationDemand demand) when (declarations is not null)
            {
                pass.Stop("front:pass-discarded");
                if (Environment.GetEnvironmentVariable("CORC_TRACE_DEMAND") is not null)
                    Console.Error.WriteLine("pass " + declarations.Passes + " demanded " + demand.Keys.Count + ": "
                        + string.Join(", ", demand.Keys.Select(k => k.Split('\n').Last())));
                foreach (string key in demand.Keys)
                {
                    declarations.Include(key);
                    declarations.Demanded(key);
                }
            }
        }
    }

    private static (CompilationUnit Unit, BindResult Bound)? CompileCore(
        IReadOnlyList<string> paths, string name, bool library,
        IReadOnlyCollection<string>? libraryPaths, IReadOnlyCollection<string>? symbols,
        IReadOnlyCollection<string>? elsewherePaths, int workers, IndexedDeclarations? declarations)
    {
#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("read-sources");
#endif
        CompilationUnit unit = new() { Line = 1, Col = 1 };
        List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources = new() { ("<prelude>", null, true, false) };

        // WHICH SOURCES ARE THE CLASS LIBRARY. Told by the driver, which is
        // the only part that knows what it links by default; matched on the
        // full path so that naming lib/collections.cor on the command line --
        // as the test runner does -- does not turn it into program code.
        HashSet<string> fromLibrary = new(StringComparer.Ordinal);
        foreach (string lib in libraryPaths ?? Array.Empty<string>())
        {
            if (File.Exists(lib))
            {
                fromLibrary.Add(Path.GetFullPath(lib));
            }
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
        try
        {
            Meter parsing = Meter.Start();
            CompilationUnit[] parsed = ParseSources(sources, symbols, workers, declarations?.Tokens);
            parsing.Stop("front:parse");
            int sourceIndex = 0;
            foreach ((string file, string? path, bool isLibrary, bool isElsewhere) in sources)
            {
                CompilationUnit one = parsed[sourceIndex++];
                unit.Usings.AddRange(one.Usings);

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
        declarations?.AddHeaders(unit);
        if (declarations is not null)
            unit.Types.Sort((left, right) =>
            {
                int path = StringComparer.Ordinal.Compare(left.SourcePath, right.SourcePath);
                return path != 0 ? path : left.SourceFrom.CompareTo(right.SourceFrom);
            });
        MergePartialTypes(unit);

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
                declarations is null ? null : declarations.RequireOverrides);
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
        for (int round = 0; round < 8 && bound.Errors.Count == 0 && (bound.Wanted.Count > 0 || bound.WantedOverrides.Count > 0); round++)
        {
            bool made = false;
            for (int step = 0; step < 8 && bound.Errors.Count == 0 && (bound.Wanted.Count > 0 || bound.WantedOverrides.Count > 0); step++)
            {
#if COR_SELFHOST_BENCHMARK
                Program.BenchmarkStage("specialise-" + round + "-" + step);
#endif
                HashSet<string> known = new(unit.Types.Select(t => t.Name), StringComparer.Ordinal);
                if (!Specialise(unit, bound))
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
                foreach (TypeDecl t in unit.Types)
                {
                    if (known.Contains(t.Name)) continue;
                    foreach (MemberDecl m in t.Members) m.Fresh = true;
                }

                Meter fresh = Meter.Start();
                bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces,
                    declarations is null ? null : declarations.RequireExtensions, declarations?.LibraryInterfaces,
                    declarations is null ? null : declarations.RequireOverrides, freshOnly: true);
                fresh.Stop("front:bind-fresh");

                foreach (TypeDecl t in unit.Types)
                {
                    foreach (MemberDecl m in t.Members) m.Fresh = false;
                }
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
                declarations is null ? null : declarations.RequireOverrides);
            rebinding.Stop("front:bind-round");
            if (Environment.GetEnvironmentVariable("CORC_TRACE_WANTS") is not null)
            {
                foreach (var w in bound.Wanted)
                    Console.Error.WriteLine("full bind still wants " + w.Item2.Name + " in " + w.Item1.Line + " of " + w.Item2.File);
                foreach (var w in bound.WantedOverrides)
                    Console.Error.WriteLine("full bind still wants override " + w.Item1.Name + "." + w.Item2.Name + " as " + w.Item4);
            }
        }

        // WHAT THE UNIT GREW TO, for CORC_REPORT_UNIT: its declarations after
        // expansion by kind, and the members each kind carries -- where a
        // unit's memory goes before a line of it is lowered.
        if (Environment.GetEnvironmentVariable("CORC_REPORT_UNIT") is not null)
        {
            foreach (var group in unit.Types.GroupBy(t => t.TypeParams.Count > 0 ? "template"
                         : t.Canon is not null ? "shared-copy" : t.Specialised ? "specialised" : t.SignatureOnly ? "imported" : "own"))
            {
                Console.Error.WriteLine("unit " + group.Key + ": " + group.Count() + " types, "
                    + group.Sum(t => t.Members.Count) + " members, "
                    + group.Sum(t => t.Members.OfType<MethodDecl>().Count(m => m.Body is { Statements.Count: > 0 })) + " with bodies");
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

    private static CompilationUnit[] ParseSources(
        List<(string Name, string? Path, bool FromLibrary, bool Elsewhere)> sources,
        IReadOnlyCollection<string>? symbols, int workers, SyntaxTokenCache? tokens = null)
    {
        CompilationUnit[] parsed = new CompilationUnit[sources.Count];
        if (workers <= 1)
        {
            for (int i = 0; i < sources.Count; i++)
                parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols, tokens, sources[i].Elsewhere);
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
                    try { parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols, tokens, sources[i].Elsewhere); }
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
    private static CompilationUnit ParseSource(string name, string? path, IReadOnlyCollection<string>? symbols,
        SyntaxTokenCache? tokens = null, bool elsewhere = false)
    {
        // Source text belongs to the active parser, not the project. Retain
        // paths in the queue so completed files release their text before the
        // next file is read. At most one source buffer per worker is live.
        string text = path is null ? Prelude.Source : File.ReadAllText(path);
        return tokens is null ? Parser.ParseText(text, name, symbols, elsewhere, elsewhere)
            : tokens.Parse(text, name, symbols, elsewhere, elsewhere);
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
    /// Compiles one or more source files into a single program.
    ///
    /// Linking happens at the AST level rather than by combining separate
    /// object files: every unit is parsed, their declarations merged, and the
    /// whole thing bound together. That means a library and its user are
    /// type-checked against each other rather than against a header, and it is
    /// what lets the standard library be written in CORSAC Script itself.
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
                        Lang.MethodDecl twin = Lang.Monomorphiser.Specialise(origin, args, wanted, values);

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

        // WHOSE MEMBER IS IT? The template is a member of exactly one
        // declaration, and the copy goes beside it so the call reaches it the
        // same way -- same receiver, same static class. Found by a map made
        // once: searching every type's members for every call was a scan of
        // the whole unit per want.
        Dictionary<Lang.MemberDecl, Lang.TypeDecl> owners = new(ReferenceEqualityComparer.Instance);
        foreach (Lang.TypeDecl t in unit.Types)
        {
            foreach (Lang.MemberDecl m in t.Members) owners.TryAdd(m, t);
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
            string wanted = Lang.Monomorphiser.MethodName(template.Name, args)
                          + "$" + owner.Members.IndexOf(template);

            if (!owner.Members.Any(m => m.Name == wanted))
            {
                Lang.MethodDecl copy = Lang.Monomorphiser.Specialise(template, args, wanted, values);

                copy.File = template.File;

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
            made |= CanonicalTwin(owner, template, args, wanted);

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

            Lang.MethodDecl copy = Lang.Monomorphiser.Specialise(template, args, wanted, values);

            copy.File = template.File;
            copy.LocalCopy = true;
            copy.Fresh = true;
            owner.Members.Add(copy);
            made = true;
            made |= CanonicalTwin(owner, template, args, wanted);
        }
        return made;
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
    private static void MergePartialTypes(Lang.CompilationUnit unit)
    {
        Dictionary<string, Lang.TypeDecl> first = new(StringComparer.Ordinal);
        List<Lang.TypeDecl> merged = new();

        foreach (Lang.TypeDecl part in unit.Types)
        {
            string key = Lang.Binder.TypeKey(part);

            if (!first.TryGetValue(key, out Lang.TypeDecl? into))
            {
                first[key] = part;
                merged.Add(part);
                continue;
            }

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
                    into.Bases.Add(basis);
                }
            }

            into.Members.AddRange(part.Members);
            into.Mods |= part.Mods;
            into.SignatureOnly &= part.SignatureOnly;
        }

        unit.Types.Clear();
        unit.Types.AddRange(merged);
    }
}

/// <summary>
/// Time and allocation over a stretch of the front end, reported with the
/// optimiser's passes when CORC_REPORT_PASSES is set, and nothing otherwise.
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
