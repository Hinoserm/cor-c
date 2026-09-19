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
    private readonly HashSet<string> queries = new(StringComparer.Ordinal);
    private readonly HashSet<string> resolvedExtensions = new(StringComparer.Ordinal);
    public long PayloadLoads => catalog.PayloadLoads;
    public SyntaxTokenCache Tokens { get; }
    public int Passes { get; set; }
    public long ResidentDeclarationBytes => catalog.ResidentBytes;
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }
    public IReadOnlySet<(string Name, int Arity)> LibraryInterfaces { get; }

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
        queries.Add("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n");
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
        queries.Add("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n");
        owned = ownedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
    }

    public void Require(string bindingName)
    {
        queries.Add("B:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + bindingName);
        string? key = catalog.BindingKey(assembly, bindingName);
        if (key is not null && !loaded.Contains(key)) throw new DeclarationDemand(key);
    }

    public void Include(string key)
    {
        if (loaded.Contains(key)) throw new InvalidDataException("Declaration discovery made no progress: " + key);
        using DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing requested declaration: " + key);
        loaded.Add(key);
    }

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
        queries.Add("E:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + query);
        if (resolvedExtensions.Contains(query)) return;
        foreach (string key in catalog.ExtensionKeys(assembly, space, method))
            if (!loaded.Contains(key)) throw new DeclarationDemand(key);
        resolvedExtensions.Add(query);
    }

    public void AddHeaders(CompilationUnit unit)
    {
        // Language operations name these helpers implicitly, without a source
        // type reference to trigger ordinary declaration discovery. Load only
        // their headers, not the implementation bodies or entire library files.
        foreach (string name in new[] { "Runtime", "String", "Boolean", "Byte", "SByte", "Int16", "UInt16",
            "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Char" })
        {
            queries.Add("B:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + name);
            string? key = catalog.BindingKey(assembly, name);
            if (key is not null && !loaded.Contains(key)) Include(key);
        }
        // A partial declaration cannot be bound from just the locally owned
        // fragment. Demand its family before entering body binding -- ALL of
        // them, in one demand. Asking for them one at a time threw on the
        // first missing one, and the frontend answers a demand by discarding
        // the unit and parsing, merging, monomorphising and binding it again:
        // a unit whose headers named Path, DateTime, DateTimeOffset,
        // Scheduler, Directory and File paid a whole extra round for each,
        // discovering exactly one name per round. They are all known here.
        DeclarationBatch partials = new();
        foreach (TypeDecl type in unit.Types.Where(type => type.Mods.HasFlag(Mods.Partial)))
        {
            try { Require(Binder.TypeKey(type)); }
            catch (DeclarationDemand demand) { partials.Add(demand); }
        }
        partials.ThrowIfAny();
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
                string? key = Speculate(reference.Name, reference.Args.Count);
                if (key is not null) Load(key);
            }, qualifier =>
            {
                string? key = Speculate(qualifier, 0);
                if (key is not null) Load(key);
            });
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
        // ONE FILE IS PARSED ONCE FOR ITS TEMPLATE BODIES, however many of
        // its declarations this unit imports. A template's body is not in
        // the index slice, so it is taken from the file, and the file is
        // parsed WHOLE -- so importing ten generic types out of
        // Collections.cor parsed Collections.cor ten times and threw nine of
        // the results away. The declarations taken out of one parse are
        // disjoint (each is picked by its own source span), and the map dies
        // with this call, so nothing is shared between discovery passes.
        Dictionary<(string Path, string Symbols), CompilationUnit> templateFiles = new();
        while (pending.Count != 0)
        {
            string key = pending.Dequeue();
            if (!visited.Add(key)) continue;
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
                CompilationUnit header = Tokens.Parse(source.Text, displayFile, declarationsOnly: true);
                // A slice can parse to more than one declaration: a delegate's
                // text also yields the multicast class synthesised beside it.
                // The record names which one it is for.
                string wanted = source.Key[(source.Key.LastIndexOf('.') + 1)..];
                if (wanted.Contains('\n')) wanted = wanted[(wanted.LastIndexOf('\n') + 1)..];
                TypeDecl root = header.Types.FirstOrDefault(type => type.Name == wanted)
                    ?? header.Types.OrderBy(type => type.SourceFrom).First();
                if (root.TypeParams.Count != 0 || root.Members.OfType<MethodDecl>().Any(method => method.TypeParams.Count != 0))
                {
                    implementations.Add(key);
                    // Templates need implementations for specialization. Keep
                    // unrelated ordinary bodies out of this imported tree.
                    var templateKey = (source.Path, string.Join("\n", source.ConditionalSymbols));
                    if (!templateFiles.TryGetValue(templateKey, out CompilationUnit? templates))
                    {
                        templates = Tokens.Parse(catalog.ReadSource(source), displayFile,
                            source.ConditionalSymbols, declarationsOnly: true, includeTemplateBodies: true);
                        templateFiles[templateKey] = templates;
                    }
                    root = templates.Types.Single(type => type.SourceFrom == source.From && type.SourceTo == source.To);
                }
                // Nested declarations have separate index records. Import the
                // requested declaration only, retaining its own lexical scope.
                root.Namespace = source.Namespace;
                root.Outer = source.Outer.Length == 0 ? null : source.Outer;
                root.Scope = source.Scope;
                root.File = displayFile;
                root.SourcePath = source.Path;
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
                    string? next = Speculate(name, arity);
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
                    string? next = Speculate(reference.Name, reference.Args.Count);
                    if (next is not null && Load(next)) pending.Enqueue(next);
                }, qualifier =>
                {
                    string? next = Speculate(qualifier, 0);
                    if (next is not null && Load(next)) pending.Enqueue(next);
                });
            }
        }
    }

    public void Dispose()
    {
        loaded.Clear();
        // A session owns its catalog and its lexed headers; the whole point is
        // that the next source in the project finds them still there.
        if (session is null) { Tokens.Clear(); catalog.Dispose(); }
    }

    public void WriteDependencies(string path) => UnitDependencies.Write(path, catalog, loaded, implementations, queries);
}
