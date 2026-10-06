namespace Corsac.Lang.Metadata;

/// <summary>One compilation unit's discovered declaration dependencies.</summary>
public sealed class IndexedDeclarations : IDisposable
{
    private readonly DeclarationCatalog catalog;
    private readonly DeclarationSession? session;
    private readonly string assembly;
    private readonly HashSet<string> owned;
    private readonly HashSet<string> loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> implementations = new(StringComparer.Ordinal);
    // WHAT THIS UNIT ASKED THE INDEX, by kind and name: spelled out as the
    // index spells a query ("B:" and this assembly's identity before the
    // name) only when the receipt is written. Kept spelled, every name the
    // binder resolved carried the identity again -- ninety thousand strings.
    private readonly HashSet<(char Kind, string Name)> queries = new();
    private readonly HashSet<string> resolvedExtensions = new(StringComparer.Ordinal);
    private readonly HashSet<string> resolvedOverrides = new(StringComparer.Ordinal);
    /// <summary>
    /// What each binding name the binder required came to: the binder asks
    /// for the same names thousands of times a unit.
    /// </summary>
    private readonly Dictionary<string, string?> required = new(StringComparer.Ordinal);
    public long PayloadLoads => catalog.PayloadLoads;
    public SyntaxTokenCache Tokens { get; }
    public int Passes { get; set; }
    public long ResidentDeclarationBytes => catalog.ResidentBytes;
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }
    public IReadOnlySet<(string Name, int Arity)> LibraryInterfaces { get; }

    /// <summary>
    /// Set for a kernel module's compile (--kernel): then the kernel's own
    /// interface families are told apart from the module's (Binder's module
    /// tier). Null otherwise.
    /// </summary>
    public bool ModuleOfKernel { get; set; }
    public IReadOnlySet<(string Name, int Arity)>? KernelInterfaces => ModuleOfKernel ? catalog.KernelInterfaces(assembly) : null;

    /// <summary>The stamp of the kernel's index this unit's index was made on (a module's), or null.</summary>
    public byte[]? UnderStamp => catalog.UnderStamp;

    public IndexedDeclarations(string path, string assembly, IEnumerable<string> ownedFiles,
        long declarationBudgetBytes = 2 * 1024 * 1024)
    {
        catalog = new DeclarationCatalog(path, declarationBudgetBytes);
        Tokens = new SyntaxTokenCache();
        this.assembly = assembly;
        Interfaces = catalog.Interfaces(assembly);
        LibraryInterfaces = catalog.LibraryInterfaces(assembly);
        // Interface slots are reserved over the project's compact family
        // table, even for declarations this unit never demand-loads. Adding
        // an earlier family can move every later slot: it is an ABI input.
        queries.Add(('I', ""));
        owned = ownedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// One source of a project being compiled in a session: the index, the
    /// lexed headers and the resolved names come from the session, and only
    /// what THIS source required is recorded for its receipt.
    /// </summary>
    public IndexedDeclarations(DeclarationSession session, IEnumerable<string> ownedFiles)
    {
        this.session = session;
        catalog = session.Catalog;
        Tokens = session.Tokens;
        assembly = session.Assembly;
        Interfaces = session.Interfaces;
        LibraryInterfaces = session.LibraryInterfaces;
        queries.Add(('I', ""));
        owned = ownedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
    }

    public void Require(string bindingName)
    {
        if (!required.TryGetValue(bindingName, out string? key))
        {
            // A monomorphised name ('$') is in no index, so it is no dependency.
            if (!bindingName.Contains('$')) queries.Add(('B', bindingName));
            key = catalog.BindingKey(assembly, bindingName) ?? Sole(bindingName);
            required[bindingName] = key;
        }
        if (key is not null && !loaded.Contains(key)) throw new DeclarationDemand(key);
    }

    /// <summary>
    /// A bare name that only a type inside a namespace or another type
    /// carries: the one declaration of that simple name in the index, or null
    /// when there is none or more than one. The binder's last resort
    /// (Binder.Sole) does this over the whole program in one compile, and is
    /// how a file with no namespace names System.IAsyncDisposable -- keyed
    /// `System.IAsyncDisposable`, no binding record answers the bare name,
    /// and a unit compiled against the index refused what the same source
    /// compiled whole accepted. Recorded as a query, so a second type of the
    /// name, which makes it ambiguous, makes the receipt stale.
    /// </summary>
    private string? Sole(string name, bool asked = true)
    {
        if (name.Length == 0 || name.Contains('.') || name.Contains('`')) return null;
        // Only what the binder asks is a dependency; a prefetch is a guess.
        if (asked) queries.Add(('S', name));
        return catalog.SoleKey(assembly, name);
    }

    public void Include(string key)
    {
        if (loaded.Contains(key)) throw new InvalidDataException("Declaration discovery made no progress: " + key);
        using DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing requested declaration: " + key);
        loaded.Add(key);
        // AND ITS FAMILY: the types nested in it, and when it is nested itself
        // those nested beside it. A body that uses a type uses what is nested
        // in it as often as not, and each one found by the binder threw the
        // whole pass away again: compiling Lowering.cs demanded Linker, then
        // Linker+Definition, then Linker+Layout, three passes discarded --
        // a quarter of the unit's time and allocation.
        LoadFamily(key);
    }

    private void LoadFamily(string key)
    {
        foreach (string nested in Family(key)) Load(nested);
    }

    // The types nested in this one's outermost type, the first time it is asked.
    private List<string> Family(string key)
    {
        string outer = key;
        int plus = outer.IndexOf('+', outer.IndexOf('\n') + 1);
        if (plus > 0) outer = outer[..plus];
        return families.Add(outer) ? catalog.KeysWithPrefix(outer + "+") : NoKeys;
    }

    private static readonly List<string> NoKeys = new();

    private readonly HashSet<string> families = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads a declaration this unit has decided it needs, saying whether that
    /// was new. Unlike <see cref="Include"/> this is not the frontend's retry
    /// step and makes no claim about discovery progress: it is how the header
    /// closure below pulls in a signature's own dependencies within one pass.
    /// </summary>
    private bool Load(string key)
    {
        if (loaded.Contains(key)) return false;
        using DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing requested declaration: " + key);
        loaded.Add(key);
        return true;
    }

    /// <summary>
    /// The index key for a type name as a signature wrote it, or null when
    /// nothing of that name is indexed. Speculative: an unknown or ambiguous
    /// spelling is simply not prefetched, and the binder still demands what it
    /// actually resolves.
    /// </summary>
    private readonly Dictionary<(string Name, int Arity), string?> speculated = new();

    /// <summary>The names a signature can write that are never index entries.</summary>
    private static readonly HashSet<string> Builtin = new(StringComparer.Ordinal)
    {
        "void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "char", "string", "object", "nint", "nuint", "var", "dynamic",
    };

    private string? Speculate(string name, int arity)
    {
        if (name.Length == 0 || name[0] == '_' || Builtin.Contains(name)) return null;
        // A type parameter is a name with no declaration anywhere; they are
        // numerous and every one of them would otherwise be a failed lookup.
        if (arity == 0 && name.Length <= 2 && char.IsUpper(name[0])) return null;
        if (session is not null) { if (session.Speculated((name, arity), out string? shared)) return shared; }
        else if (speculated.TryGetValue((name, arity), out string? memo)) return memo;
        string simple = arity > 0 ? name + "`" + arity : name;
        string? key;
        try
        {
            key = catalog.BindingKey(assembly, simple);
            if (key is null)
            {
                // Written qualified: `System.Collections.List<T>` is indexed under
                // the name its declaration carries, not the path the use site took.
                int cut = simple.LastIndexOf('.');
                key = cut < 0 ? null : catalog.BindingKey(assembly, simple[(cut + 1)..]);
            }
            key ??= arity == 0 ? Sole(name, asked: false) : null;
        }
        catch (InvalidDataException) { key = null; }
        if (session is not null) session.Speculate((name, arity), key);
        else speculated[(name, arity)] = key;
        return key;
    }

    /// <summary>
    /// Every type name written in a declaration's SIGNATURES: bases, constraints,
    /// field and property types, method returns and parameters. Bodies are not
    /// walked, because an imported header is bound for its signatures only.
    /// </summary>
    /// <summary>
    /// Speculate, for a name written inside `space` in a file whose using
    /// directives are `scope`: the alias it may be, then the declaration of
    /// that name in the namespace it was written in or an enclosing one, then
    /// in each namespace the file imports -- and only then the bare name.
    ///
    /// The bare name alone is no answer when two namespaces declare it. This
    /// compiler has a Block of syntax and a Block of IR, an Operand of the IR
    /// and of each assembler, a Section of the IR and of the linker; each was
    /// skipped as ambiguous, left for the binder to demand, and every such
    /// demand threw a whole pass away. Like Speculate, a guess: a wrong one
    /// loads a declaration the unit did not need, and the binder still
    /// demands whatever this misses.
    /// </summary>
    private string? SpeculateIn(string? space, FileScope? scope, string name, int arity)
    {
        if (name.Length == 0 || name[0] == '_' || Builtin.Contains(name)) return Speculate(name, arity);
        bool dotted = name.Contains('.');
        if (!dotted && arity == 0 && name.Length <= 2 && char.IsUpper(name[0])) return null;
        if (scope is not null && !dotted)
        {
            foreach ((string _, string alias, string target) in scope.Aliases)
                if (alias == name) return Speculate(target, arity);
        }
        // A NAME QUALIFIED FROM WHERE IT WAS WRITTEN -- `Metadata.RegistrySchema`
        // inside Corsac.Lang -- is looked up the same way: under the enclosing
        // namespaces and the imports, as the binder resolves it.
        string simple = arity > 0 ? name + "`" + arity : name;
        for (string? at = space; !string.IsNullOrEmpty(at); at = at.LastIndexOf('.') is int cut && cut > 0 ? at[..cut] : null)
        {
            if (Qualified(at + "." + simple) is string key) return key;
        }
        if (scope is not null)
        {
            foreach ((string _, string import) in scope.Imports)
                if (Qualified(import + "." + simple) is string key) return key;
        }
        return Speculate(name, arity);
    }

    /// <summary>
    /// What BodyTypeNames' qualifiers under one declaration come to, handed
    /// to <paramref name="load"/>: a single name speculated as before, a
    /// dotted path resolved whole (SpeculatePath), each path once.
    /// </summary>
    private Action<string> Qualifiers(TypeDecl type, Action<string> load)
    {
        Dictionary<string, string?>? paths = null;
        HashSet<string>? values = null;
        return qualifier =>
        {
            string? key;
            if (qualifier.IndexOf('.') < 0) key = SpeculateIn(type.Namespace, type.Scope, qualifier, 0);
            else
            {
                paths ??= new(StringComparer.Ordinal);
                if (!paths.TryGetValue(qualifier, out key))
                    paths[qualifier] = key = SpeculatePath(type, qualifier, values ??= ValueNames(type));
            }
            if (key is not null) load(key);
        };
    }

    /// <summary>
    /// The declaration a dotted path written in <paramref name="type"/> names
    /// -- `Corsac.Lang.Elf.Linker`, or `Elf.Linker` inside Corsac.Lang --
    /// found the way Binder.FindType finds it, or null.
    ///
    /// CERTAIN, NOT A GUESS, which a path has to be: loading what nobody asked
    /// for puts a name in scope the compilation did not have (BodyTypeNames).
    /// So the path is looked up only WHOLE, at exactly the places FindType
    /// tries it and in its order -- under the type it was written in and each
    /// type and namespace enclosing that, with the using directives written
    /// at each level after the level's own members; then as written; then the
    /// file's top-level directives -- and the first one the index has is the
    /// answer. FindType asks the index at every one of those places in turn
    /// (TypeCandidate), so a pass that has none of them loaded demands that
    /// first one; this loads it a pass sooner. Never by its last name alone,
    /// which is how Speculate treats a dotted TYPE reference: `Node.Kind`
    /// cut to `Kind` would load whatever Kind is sole. Nor through an alias
    /// on its first name, which FindType does not follow for a dotted name
    /// either. Two imports that both have it are ambiguous to the binder,
    /// and are no answer here.
    ///
    /// What is left uncertain is whether the binder takes the first name for
    /// a namespace at all, which it does only where it names no value
    /// (Binder.NamespaceOnly). So a path whose first name is one of this
    /// declaration's own members or parameters is not looked up: `Options`
    /// as a property is not the namespace of the same name.
    /// </summary>
    private string? SpeculatePath(TypeDecl type, string path, HashSet<string> values)
    {
        string head = path[..path.IndexOf('.')];
        if (values.Contains(head)) return null;
        FileScope? scope = type.Scope;
        for (string? at = Binder.TypeKey(type); at is not null; at = at.LastIndexOf('.') is int cut && cut > 0 ? at[..cut] : null)
        {
            if (Qualified(at + "." + path) is string key) return key;
            if (Imported(scope, at, path, out string? imported)) return imported;
        }
        if (Qualified(path) is string written) return written;
        return Imported(scope, "", path, out string? top) ? top : null;
    }

    /// <summary>
    /// The one declaration the namespaces imported at level <paramref name="at"/>
    /// give this path, as Binder.ImportedNow finds it. True when the level
    /// settles the question: one declaration (in <paramref name="key"/>), or
    /// two, which is ambiguous and stops the search with no answer.
    /// </summary>
    private bool Imported(FileScope? scope, string at, string path, out string? key)
    {
        key = null;
        if (scope is null) return false;
        foreach ((string In, string Namespace) import in scope.Imports)
        {
            if (import.In != at || Qualified(import.Namespace + "." + path) is not string found) continue;
            if (key is not null && key != found) { key = null; return true; }
            key = found;
        }
        return key is not null;
    }

    /// <summary>
    /// The names in a declaration that are values rather than namespaces:
    /// its members, the members of an enum, and every parameter of every
    /// method, for SpeculatePath to leave alone. More than any one place in
    /// the declaration has in scope, which only means fewer paths prefetched.
    /// </summary>
    private static HashSet<string> ValueNames(TypeDecl type)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (MemberDecl member in type.Members)
        {
            names.Add(member.Name);
            switch (member)
            {
                case FieldDecl field: foreach (FieldDecl more in field.More) names.Add(more.Name); break;
                case PropertyDecl property: foreach (Param parameter in property.Params) names.Add(parameter.Name); break;
                case MethodDecl method: foreach (Param parameter in method.Params) names.Add(parameter.Name); break;
            }
        }
        foreach (EnumMember member in type.EnumMembers) names.Add(member.Name);
        return names;
    }

    private readonly Dictionary<string, string?> qualified = new(StringComparer.Ordinal);

    private string? Qualified(string name)
    {
        if (qualified.TryGetValue(name, out string? known)) return known;
        string? key = catalog.BindingKey(assembly, name);
        qualified[name] = key;
        return key;
    }

    private static IEnumerable<(string Name, int Arity)> SignatureNames(TypeDecl type)
    {
        List<(string, int)> found = new();
        void Walk(TypeRef? reference)
        {
            if (reference is null) return;
            found.Add((reference.Name, reference.Args.Count));
            foreach (TypeRef argument in reference.Args) Walk(argument);
            if (reference.UseArgs is not null) foreach (TypeRef argument in reference.UseArgs) Walk(argument);
        }
        foreach (TypeRef basis in type.Bases) Walk(basis);
        foreach (TypeParam parameter in type.TypeParams)
            foreach (TypeRef constraint in parameter.Constraints) Walk(constraint);
        foreach (MemberDecl member in type.Members)
        {
            switch (member)
            {
                case FieldDecl field: Walk(field.Type); break;
                case PropertyDecl property:
                    Walk(property.Type);
                    foreach (Param parameter in property.Params) Walk(parameter.Type);
                    break;
                case MethodDecl method:
                    Walk(method.Returns);
                    foreach (Param parameter in method.Params) Walk(parameter.Type);
                    foreach (TypeParam parameter in method.TypeParams)
                        foreach (TypeRef constraint in parameter.Constraints) Walk(constraint);
                    break;
            }
        }
        return found;
    }

    public void RequireExtensions(string space, string method)
    {
        string query = space + "\n" + method;
        queries.Add(('E', query));
        if (resolvedExtensions.Contains(query)) return;
        foreach (string key in catalog.ExtensionKeys(assembly, space, method))
            if (!loaded.Contains(key)) throw new DeclarationDemand(key);
        resolvedExtensions.Add(query);
    }

    /// <summary>
    /// Every declaration with a generic instance method of this name and
    /// arity: what a call to a generic virtual method needs loaded to know
    /// all the classes that override or implement it.
    /// </summary>
    public void RequireOverrides(string method, int arity)
    {
        string query = method + "`" + arity;
        queries.Add(('G', query));
        if (resolvedOverrides.Contains(query)) return;
        DeclarationBatch missing = new();
        foreach (string key in catalog.OverrideKeys(assembly, query))
            if (!loaded.Contains(key)) missing.Add(new DeclarationDemand(key));
        missing.ThrowIfAny();
        resolvedOverrides.Add(query);
    }

    public void AddHeaders(CompilationUnit unit)
    {
        // Language operations name these helpers implicitly, without a source
        // type reference to trigger ordinary declaration discovery. Load only
        // their headers, not the implementation bodies or entire library files.
        foreach (string name in new[] { "Runtime", "String", "Boolean", "Byte", "SByte", "Int16", "UInt16",
            "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Char" })
        {
            queries.Add(('B', name));
            string? key = catalog.BindingKey(assembly, name);
            if (key is not null && !loaded.Contains(key)) Include(key);
        }
        // A partial declaration cannot be bound from just the locally owned
        // fragment, so its family is loaded before body binding -- in THIS
        // pass, through the closure below, like every other name known here.
        // Demanding it threw the pass away first: the frontend answers a
        // demand by discarding the unit and parsing, merging, monomorphising
        // and binding it again, and every unit that is one file of a partial
        // class (most of this compiler) paid a whole pass for a name it held
        // from the start.
        foreach (TypeDecl type in unit.Types.Where(type => type.Mods.HasFlag(Mods.Partial)))
        {
            string name = Binder.TypeKey(type);
            queries.Add(('B', name));
            string? key = catalog.BindingKey(assembly, name) ?? Sole(name);
            if (key is not null) Load(key);
        }
        // AND WHAT THIS UNIT'S OWN CODE NAMES. Its sources are fully parsed,
        // bodies and all, so the types it uses are knowable before binding
        // begins -- and until now nobody looked. The binder met them one
        // layer at a time instead: a kernel source naming Pipe and UserFile
        // spent a round on those, and only once they were bound could the
        // expressions through them resolve far enough to name Arch, Errno,
        // UserMode and UserPointer, which cost another. Reading the names
        // straight out of the tree finds the whole set at once.
        //
        // A prefetch like the one over imported bodies, and safe for the same
        // reason: these are names the binder was going to demand.
        foreach (TypeDecl type in unit.Types.Where(type => !type.Elsewhere))
        {
            BodyTypeNames.Walk(type, reference =>
            {
                string? key = SpeculateIn(type.Namespace, type.Scope, reference.Name, reference.Args.Count);
                if (key is not null) Load(key);
            }, Qualifiers(type, key => Load(key)));
        }
        // THE WHOLE CHAIN IN ONE PASS. A header's own signatures name more
        // types -- List names IEnumerable, which names IEnumerator, and so on
        // -- and discovering them one at a time meant the frontend threw the
        // unit away and reparsed and rebound EVERYTHING for each link. An
        // empty kernel file cost eighteen of those rounds. The names are
        // already in the header just parsed, so the closure is taken here,
        // and the binder still demands anything this does not foresee.
        Queue<string> pending = new(loaded);
        HashSet<string> visited = new(StringComparer.Ordinal);
        // A TEMPLATE IS PARSED FROM ITS OWN SPAN, not from its whole file. A
        // template's body is not in the index slice, so it is read from the
        // file -- and the file was parsed WHOLE, the bodies of every generic
        // in it, for each unit that imported one: a unit naming List parsed
        // every collection and all of LINQ in Core.cor's fourteen thousand
        // lines, and held all of it until this returned. Now only the
        // declaration's own text is read (Lexer.TokenizeRange), where it sits
        // in the file, so its positions, lines and columns are the ones a
        // whole-file parse gives it, and Parser.StartNamespace puts it where it
        // was written, so its Namespace and Outer, its nested types' paths
        // and a delegate's multicast come out as the whole file makes them.
        //
        // A TYPE NESTED IN A GENERIC ONE takes the outer's parameters first
        // (TypeDecl.OuterParams), which its own text cannot say: `struct
        // Enumerator { T Current; }` read alone has none. So it is read out
        // of the span of its outermost GENERIC enclosing type, whose key is a
        // prefix of its own (List`1+Enumerator, under List`1); the
        // non-generic types around that one give it nothing.
        //
        // THE WHOLE FILE IS STILL READ where a span provably cannot be:
        //  - the file `#define`s or `#undef`s a symbol before the span, which
        //    a span read alone would not see (FirstDefine);
        //  - the span does not read alone: a `#if` around it whose `#elif`,
        //    `#else` or `#endif` falls inside it, which the lexer refuses as
        //    unpaired -- the only way a directive outside a span reaches in;
        //  - the span names a generic local function or a local-function
        //    delegate. Their names carry a count over the whole file
        //    (Parser.Hoisted), they are members and types other code names,
        //    and a span counts from zero;
        //  - the record of the enclosing generic type is not found, or the
        //    span read has no type of the record's own span in it.
        // Nothing else outside a span reaches the tokens inside it: the
        // conditional state at its start is "compiling" (its first token is
        // compiled), and the symbols are the record's own.
        //
        // ONE SPAN IS PARSED ONCE, and a parse is let go as soon as every
        // declaration in it that can be asked for has been taken: an outer
        // generic type and the types nested in it come out of one parse, and
        // a lone generic type's parse dies as its one declaration is taken.
        // The declarations taken out of one parse are disjoint (each is picked
        // by its own span), and the map dies with this call, so nothing is
        // shared between discovery passes. A span found unreadable is kept as
        // null, so the next declaration in it goes to the whole file at once.
        Dictionary<(string Path, string Symbols, int From, int To), CompilationUnit?> templateFiles = new();
        Dictionary<string, int> firstDefine = new(StringComparer.Ordinal);
        (CompilationUnit Unit, (string, string, int, int) Key) Templates(SourceDeclaration source, string displayFile, bool inGeneric)
        {
            string symbols = string.Join("\n", source.ConditionalSymbols);
            string text = catalog.ReadSource(source);
            if (!firstDefine.TryGetValue(source.Path, out int define)) firstDefine[source.Path] = define = FirstDefine(text);
            DeclarationSpan? span = inGeneric ? EnclosingGeneric(source)
                : new DeclarationSpan(source.From, source.To, source.Line, source.Column, source.Namespace, source.Outer);
            if (span is { } at && define >= at.From)
            {
                var spanKey = (source.Path, symbols, at.From, at.To);
                if (!templateFiles.TryGetValue(spanKey, out CompilationUnit? parsed))
                {
                    try
                    {
                        parsed = Tokens.ParseRange(text, at.From, at.To, at.Line, at.Column, displayFile,
                            source.ConditionalSymbols, at.Namespace, at.Outer, out bool hoisted,
                            declarationsOnly: true, includeTemplateBodies: true);
                        if (hoisted) parsed = null;
                    }
                    catch (CompileError) { parsed = null; }
                    templateFiles[spanKey] = parsed;
                }
                if (parsed is not null && parsed.Types.Any(type => type.SourceFrom == source.From && type.SourceTo == source.To))
                    return (parsed, spanKey);
            }
            var wholeKey = (source.Path, symbols, 0, -1);
            if (!templateFiles.TryGetValue(wholeKey, out CompilationUnit? whole) || whole is null)
            {
                whole = Tokens.Parse(text, displayFile, source.ConditionalSymbols, declarationsOnly: true, includeTemplateBodies: true);
                templateFiles[wholeKey] = whole;
            }
            return (whole, wholeKey);
        }
        while (pending.Count != 0)
        {
            string key = pending.Dequeue();
            if (!visited.Add(key)) continue;
            // ITS NESTED TYPES WITH IT, closed over like everything else: a
            // type read for a signature had its Enumerator or its Kind found by
            // the binder a pass later, each pass thrown away whole.
            foreach (string nested in Family(key))
                if (Load(nested)) pending.Enqueue(nested);
            // Parsed headers own their syntax. Keeping their serialized source
            // records pinned as well prevents eviction without helping binding.
            using DeclarationLease lease = catalog.AcquireKey(key)
                ?? throw new InvalidDataException("Missing discovered declaration: " + key);
            foreach (SourceDeclaration source in lease.Records)
            {
                if (owned.Contains(source.Path)) { _ = catalog.ReadSource(source); continue; }
                // Match the direct frontend's diagnostic file spelling. Throw
                // sites embed it in executable string data, so different paths
                // would make identical generic instantiations disagree at link.
                string displayFile = Path.GetFileName(source.Path);
                // WHETHER IT IS A TEMPLATE, from its key where the key says so:
                // a generic type, or one nested in a generic type, has a
                // backtick in its name (List`1, List`1+Enumerator), and for
                // those the signature-only parse of the slice was made only to
                // be thrown away for the template's. Only a type the key
                // cannot tell about -- one that may have generic methods --
                // is parsed as a signature first.
                string typeName = source.Key[(source.Key.LastIndexOf('\n') + 1)..];
                int plus = typeName.LastIndexOf('+');
                bool inGeneric = plus > 0 && typeName[..plus].Contains('`');
                bool template = typeName.Contains('`');
                // Set below, by one branch or the other.
                TypeDecl root = null!;
                if (!template)
                {
                    CompilationUnit header = Tokens.Parse(source.Text, displayFile, declarationsOnly: true);
                    // A CLASS OF ANOTHER RING parses to nothing (Parser.Ring): an
                    // index built for every ring holds it, and this compile does
                    // not, any more than its own sources' copy of it.
                    if (header.Types.Count == 0) continue;
                    // A slice can parse to more than one declaration: a delegate's
                    // text also yields the multicast class synthesised beside it.
                    // The record names which one it is for.
                    string wanted = source.Key[(source.Key.LastIndexOf('.') + 1)..];
                    if (wanted.Contains('\n')) wanted = wanted[(wanted.LastIndexOf('\n') + 1)..];
                    root = header.Types.FirstOrDefault(type => type.Name == wanted)
                        ?? header.Types.OrderBy(type => type.SourceFrom).First();
                    template = root.Members.OfType<MethodDecl>().Any(method => method.TypeParams.Count != 0);
                }
                if (template)
                {
                    implementations.Add(key);
                    // Templates need implementations for specialization. Keep
                    // unrelated ordinary bodies out of this imported tree.
                    (CompilationUnit templates, var templateKey) = Templates(source, displayFile, inGeneric);
                    // BY ITS SPAN, AND ITS NAME WHERE TWO SHARE ONE: declarations
                    // made beside a type (a delegate's multicast class, COM's
                    // wrappers for a [ComImport] interface) carry the span of
                    // what they were made from, and Single found two.
                    string simple = typeName[(Math.Max(typeName.LastIndexOf('+'), typeName.LastIndexOf('.')) + 1)..];
                    int tick = simple.IndexOf('`');
                    if (tick >= 0) simple = simple[..tick];
                    List<TypeDecl> spanned = templates.Types.Where(type => type.SourceFrom == source.From && type.SourceTo == source.To).ToList();
                    // None, as for a class of another ring above: it, and
                    // everything written inside it, parses to nothing in a
                    // span or a whole file alike.
                    if (spanned.Count == 0) continue;
                    root = spanned.Count == 1 ? spanned[0] : spanned.FirstOrDefault(type => type.Name == simple) ?? spanned.First();
                    // TAKEN, so out of the parse; and the parse out of the map
                    // once nothing in it can still be asked for. A local
                    // function's delegate (LocalOnly) is in no index, so no
                    // record will ever come for it.
                    templates.Types.Remove(root);
                    if (!templates.Types.Any(type => !type.LocalOnly)) templateFiles.Remove(templateKey);
                }
                // Nested declarations have separate index records. Import the
                // requested declaration only, retaining its own lexical scope.
                root.Namespace = source.Namespace;
                root.Outer = source.Outer.Length == 0 ? null : source.Outer;
                root.Scope = source.Scope;
                root.File = displayFile;
                root.SourcePath = source.Path;
                // WHERE IT IS IN ITS FILE, not in the slice it was parsed from:
                // the front end orders a partial type's parts by path and then
                // by this before merging them (Frontend.MergePartialTypes), and
                // every part cut out of one file began at 0 -- Escape's four
                // parts in OwnedElements.cs merged in no fixed order, their
                // fields at other offsets than the unit compiling that file
                // gave them, and the link refused the two layouts.
                root.SourceFrom = source.From;
                root.SourceTo = source.To;
                root.Elsewhere = true;
                root.SignatureOnly = true;
                foreach (MemberDecl member in root.Members)
                {
                    member.File = displayFile;
                    member.Namespace = source.Namespace;
                    member.Scope = source.Scope;
                    member.OwnedImplementation = false;
                }
                unit.Types.Add(root);
                foreach ((string name, int arity) in SignatureNames(root))
                {
                    string? next = SpeculateIn(root.Namespace, root.Scope, name, arity);
                    if (next is not null && Load(next)) pending.Enqueue(next);
                }
                // AND WHAT ITS BODIES NAME, when the bodies came too. A
                // template is imported WITH its body, because a specialisation
                // is compiled from it, and that body names types the signature
                // never mentions: List's methods make a ListEnumerator and
                // throw an ArgumentOutOfRangeException. Left to the binder,
                // each of those cost a whole round -- and a round means the
                // unit is thrown away and parsed, merged, monomorphised and
                // bound again. Following them here loads the same
                // declarations the binder would have demanded, only sooner:
                // the kernel's receipts come out the same size and its linked
                // image byte for byte identical. See BodyTypeNames.
                // EVERY imported declaration, not only the ones whose bodies
                // came with them. A signature-only slice still carries its
                // constant and field initializers, and those name types in
                // places no signature does: Config's `const long Cpu =
                // CpuKind.I486` is the whole reason the binder wants CpuKind,
                // and reading it back a round later cost a round.
                BodyTypeNames.Walk(root, reference =>
                {
                    string? next = SpeculateIn(root.Namespace, root.Scope, reference.Name, reference.Args.Count);
                    if (next is not null && Load(next)) pending.Enqueue(next);
                }, Qualifiers(root, next => { if (Load(next)) pending.Enqueue(next); }));
            }
        }
    }

    /// <summary>Where a declaration is read from in its file, and where it was written (Parser.StartNamespace).</summary>
    private readonly record struct DeclarationSpan(int From, int To, int Line, int Column, string Namespace, string Outer);

    /// <summary>
    /// The span of the outermost generic type a nested declaration is written
    /// inside: what gives it its outer parameters (TypeDecl.OuterParams), all
    /// of them, since a type nested in that one has its parameters before its
    /// own and the non-generic types around it have none. Its key is a prefix
    /// of the nested one's, ending at the first part with a backtick:
    /// Ns.A+B`1 for Ns.A+B`1+C+D. Of a partial one, the part in the same file
    /// whose span holds this declaration. Null when there is no such record.
    /// </summary>
    private DeclarationSpan? EnclosingGeneric(SourceDeclaration source)
    {
        string key = source.Key;
        int at = key.LastIndexOf('\n') + 1, end = -1;
        for (int plus = key.IndexOf('+', at); plus > 0; plus = key.IndexOf('+', at))
        {
            if (key.AsSpan(at, plus - at).Contains('`')) { end = plus; break; }
            at = plus + 1;
        }
        if (end < 0) return null;
        using DeclarationLease? lease = catalog.AcquireKey(key[..end]);
        if (lease is null) return null;
        foreach (SourceDeclaration outer in lease.Records)
            if (outer.Path == source.Path && outer.From <= source.From && outer.To >= source.To
                && outer.SourceHash.AsSpan().SequenceEqual(source.SourceHash))
                return new DeclarationSpan(outer.From, outer.To, outer.Line, outer.Column, outer.Namespace, outer.Outer);
        return null;
    }

    /// <summary>
    /// Where a file's first `#define` or `#undef` is, or int.MaxValue when it
    /// has none: a declaration after it cannot be read on its own, because
    /// the symbols it changes are the file's from there on. Any line whose
    /// first non-blank text is one counts, even inside a comment, a string or
    /// a branch not compiled; a span is then read with its whole file, which
    /// is never wrong, only slower.
    /// </summary>
    private static int FirstDefine(string text)
    {
        for (int hash = text.IndexOf('#'); hash >= 0; hash = text.IndexOf('#', hash + 1))
        {
            int line = hash;
            while (line > 0 && text[line - 1] is ' ' or '\t' or '\r') line--;
            if (line > 0 && text[line - 1] != '\n') continue;
            int word = hash + 1;
            while (word < text.Length && text[word] is ' ' or '\t') word++;
            ReadOnlySpan<char> rest = text.AsSpan(word);
            if (rest.StartsWith("define", StringComparison.Ordinal) || rest.StartsWith("undef", StringComparison.Ordinal))
                return line;
        }
        return int.MaxValue;
    }

    public void Dispose()
    {
        loaded.Clear();
        // A session owns its catalog and its lexed headers; the whole point is
        // that the next source in the project finds them still there.
        if (session is null) { Tokens.Clear(); catalog.Dispose(); }
    }

    public void WriteDependencies(string path)
    {
        string identity = SourceIndexBuilder.AssemblyIdentity(assembly);
        UnitDependencies.Write(path, catalog, loaded, implementations,
            queries.Select(asked => asked.Kind + ":" + identity + "\n" + asked.Name));
    }
}
