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
        while (true)
        {
            try { return CompileCore(paths, name, library, libraryPaths, symbols, elsewherePaths, workers, declarations); }
            catch (DeclarationDemand demand) when (declarations is not null) { declarations.Include(demand.Key); }
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
            CompilationUnit[] parsed = ParseSources(sources, symbols, workers);
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
                        if (declarations is not null && !isElsewhere) member.OwnedImplementation = true;
                    }
                }

                unit.Types.AddRange(one.Types);
                // Ownership has moved to unit. Do not keep the original
                // compilation roots alive through a stale parser-array slot.
                one.Types.Clear();
                one.Usings.Clear();
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

        IReadOnlyList<CompileError> generic;
        unit = Monomorphiser.Expand(unit, name, library, out generic, declarations is null ? null : declarations.Require);

        if (Report(generic))
        {
            return null;
        }

#if COR_SELFHOST_BENCHMARK
        Program.BenchmarkStage("bind-initial");
#endif
        BindResult bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces);

        // The checker's first pass discovers which generic methods were called
        // with which type arguments; each becomes a copy, and the whole thing
        // goes round again because a copy's body can ask for more. Bounded,
        // because a method that instantiates itself deeper every time has no
        // fixed point.
        for (int round = 0; round < 8 && bound.Errors.Count == 0 && bound.Wanted.Count > 0; round++)
        {
#if COR_SELFHOST_BENCHMARK
            Program.BenchmarkStage("specialise-" + round);
#endif
            if (!Specialise(unit, bound))
            {
                break;
            }

            // The previous round is no longer an input. Keeping its maps
            // until the replacement binding returns doubles graph pressure.
            bound.ReleaseForRebind();

            unit = Monomorphiser.Expand(unit, name, library, out generic, declarations is null ? null : declarations.Require);

            if (Report(generic))
            {
                return null;
            }

#if COR_SELFHOST_BENCHMARK
            Program.BenchmarkStage("bind-" + round);
#endif
            bound = Binder.Bind(unit, name, declarations is null ? null : declarations.Require, declarations?.Interfaces);
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
                Console.Error.WriteLine(warning.ToString().Replace("): warning: ", "): error: "));
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
        IReadOnlyCollection<string>? symbols, int workers)
    {
        CompilationUnit[] parsed = new CompilationUnit[sources.Count];
        if (workers <= 1)
        {
            for (int i = 0; i < sources.Count; i++)
                parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols);
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
                    try { parsed[i] = ParseSource(sources[i].Name, sources[i].Path, symbols); }
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

    private static CompilationUnit ParseSource(string name, string? path, IReadOnlyCollection<string>? symbols)
    {
        // Source text belongs to the active parser, not the project. Retain
        // paths in the queue so completed files release their text before the
        // next file is read. At most one source buffer per worker is live.
        string text = path is null ? Prelude.Source : File.ReadAllText(path);
        return Parser.ParseText(text, name, symbols);
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
    private static bool Specialise(Lang.CompilationUnit unit, Lang.BindResult bound)
    {
        bool made = false;

        foreach ((Lang.CallExpr call, Lang.MethodDecl template, List<Lang.TypeRef> args) in bound.Wanted)
        {
            // WHOSE MEMBER IS IT? The template is a member of exactly one
            // declaration, and the copy goes beside it so the call reaches it
            // the same way -- same receiver, same static class.
            Lang.TypeDecl? owner = unit.Types.FirstOrDefault(t => t.Members.Contains(template));

            if (owner is null)
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
                Lang.MethodDecl copy = Lang.Monomorphiser.Specialise(template, args, wanted);

                copy.File = template.File;

                // OURS, wherever it sits. The owner may be a library's type,
                // and a member of a library's type is normally an import --
                // but the library never compiled this copy, so an import of it
                // is a name nothing provides. The library's OWN copies arrive
                // through its header under this very name and are found by the
                // check above, so this line is reached exactly when the copy
                // has to be made here.
                copy.LocalCopy = true;
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
                        Lang.MethodDecl twin = Lang.Monomorphiser.Specialise(origin, args, wanted);

                        twin.File = origin.File;
                        twin.LocalCopy = true;
                        shared.Members.Add(twin);
                        made = true;
                    }
                }
            }

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
