namespace Corsac.Lang.Metadata;

/// <summary>One compilation unit's discovered declaration dependencies.</summary>
public sealed class IndexedDeclarations : IDisposable
{
    private readonly DeclarationCatalog catalog;
    private readonly string assembly;
    private readonly HashSet<string> owned;
    private readonly HashSet<string> loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> implementations = new(StringComparer.Ordinal);
    private readonly HashSet<string> queries = new(StringComparer.Ordinal);
    private readonly HashSet<string> resolvedExtensions = new(StringComparer.Ordinal);
    public long PayloadLoads => catalog.PayloadLoads;
    public SyntaxTokenCache Tokens { get; } = new();
    public int Passes { get; set; }
    public long ResidentDeclarationBytes => catalog.ResidentBytes;
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }
    public IReadOnlySet<(string Name, int Arity)> LibraryInterfaces { get; }

    public IndexedDeclarations(string path, string assembly, IEnumerable<string> ownedFiles,
        long declarationBudgetBytes = 2 * 1024 * 1024)
    {
        catalog = new DeclarationCatalog(path, declarationBudgetBytes);
        this.assembly = assembly;
        Interfaces = catalog.Interfaces(assembly);
        LibraryInterfaces = catalog.LibraryInterfaces(assembly);
        // Interface slots are reserved over the project's compact family
        // table, even for declarations this unit never demand-loads. Adding
        // an earlier family can move every later slot: it is an ABI input.
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
        if (speculated.TryGetValue((name, arity), out string? memo)) return memo;
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
        speculated[(name, arity)] = key;
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
        // fragment. Demand its family before entering body binding.
        foreach (TypeDecl type in unit.Types.Where(type => type.Mods.HasFlag(Mods.Partial)))
            Require(Binder.TypeKey(type));
        // THE WHOLE CHAIN IN ONE PASS. A header's own signatures name more
        // types -- List names IEnumerable, which names IEnumerator, and so on
        // -- and discovering them one at a time meant the frontend threw the
        // unit away and reparsed and rebound EVERYTHING for each link. An
        // empty kernel file cost eighteen of those rounds. The names are
        // already in the header just parsed, so the closure is taken here,
        // and the binder still demands anything this does not foresee.
        Queue<string> pending = new(loaded);
        HashSet<string> visited = new(StringComparer.Ordinal);
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
                if (owned.Contains(source.Path)) { _ = source.ReadSource(); continue; }
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
                    CompilationUnit templates = Tokens.Parse(source.ReadSource(), displayFile,
                        source.ConditionalSymbols, declarationsOnly: true, includeTemplateBodies: true);
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
            }
        }
    }

    public void Dispose()
    {
        loaded.Clear(); Tokens.Clear(); catalog.Dispose();
    }

    public void WriteDependencies(string path) => UnitDependencies.Write(path, catalog, loaded, implementations, queries);
}
