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
    public long ResidentDeclarationBytes => catalog.ResidentBytes;
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }

    public IndexedDeclarations(string path, string assembly, IEnumerable<string> ownedFiles,
        long declarationBudgetBytes = 2 * 1024 * 1024)
    {
        catalog = new DeclarationCatalog(path, declarationBudgetBytes);
        this.assembly = assembly;
        Interfaces = catalog.Interfaces(assembly);
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
        foreach (string key in loaded)
        {
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
                CompilationUnit header = Parser.ParseText(source.Text, displayFile, declarationsOnly: true);
                TypeDecl root = header.Types.OrderBy(type => type.SourceFrom).First();
                if (root.TypeParams.Count != 0 || root.Members.OfType<MethodDecl>().Any(method => method.TypeParams.Count != 0))
                {
                    implementations.Add(key);
                    // Templates need implementations for specialization. Keep
                    // unrelated ordinary bodies out of this imported tree.
                    CompilationUnit templates = Parser.ParseText(source.ReadSource(), displayFile,
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
            }
        }
    }

    public void Dispose()
    {
        loaded.Clear(); catalog.Dispose();
    }

    public void WriteDependencies(string path) => UnitDependencies.Write(path, catalog, loaded, implementations, queries);
}
