namespace Corsac.Lang.Metadata;

/// <summary>One compilation unit's pinned declaration dependencies.</summary>
public sealed class IndexedDeclarations : IDisposable
{
    private readonly DeclarationCatalog catalog;
    private readonly string assembly;
    private readonly HashSet<string> owned;
    private readonly Dictionary<string, DeclarationLease> loaded = new(StringComparer.Ordinal);
    public long PayloadLoads => catalog.PayloadLoads;
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }

    public IndexedDeclarations(string path, string assembly, IEnumerable<string> ownedFiles)
    {
        catalog = new DeclarationCatalog(path);
        this.assembly = assembly;
        Interfaces = catalog.Interfaces(assembly);
        owned = ownedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
    }

    public void Require(string bindingName)
    {
        string? key = catalog.BindingKey(assembly, bindingName);
        if (key is not null && !loaded.ContainsKey(key)) throw new DeclarationDemand(key);
    }

    public void Include(string key)
    {
        if (loaded.ContainsKey(key)) throw new InvalidDataException("Declaration discovery made no progress: " + key);
        DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing requested declaration: " + key);
        loaded.Add(key, lease);
    }

    public void AddHeaders(CompilationUnit unit)
    {
        // A partial declaration cannot be bound from just the locally owned
        // fragment. Demand its family before entering body binding.
        foreach (TypeDecl type in unit.Types.Where(type => type.Mods.HasFlag(Mods.Partial)))
            Require(Binder.TypeKey(type));
        foreach (DeclarationLease lease in loaded.Values)
            foreach (SourceDeclaration source in lease.Records)
            {
                if (owned.Contains(source.Path)) { _ = source.ReadSource(); continue; }
                CompilationUnit header = Parser.ParseText(source.Text, source.Path, declarationsOnly: true);
                TypeDecl root = header.Types.OrderBy(type => type.SourceFrom).First();
                if (root.TypeParams.Count != 0 || root.Members.OfType<MethodDecl>().Any(method => method.TypeParams.Count != 0))
                {
                    // Templates need implementations for specialization. Keep
                    // unrelated ordinary bodies out of this imported tree.
                    CompilationUnit templates = Parser.ParseText(source.ReadSource(), source.Path,
                        source.ConditionalSymbols, declarationsOnly: true, includeTemplateBodies: true);
                    root = templates.Types.Single(type => type.SourceFrom == source.From && type.SourceTo == source.To);
                }
                // Nested declarations have separate index records. Import the
                // requested declaration only, retaining its own lexical scope.
                root.Namespace = source.Namespace;
                root.Outer = source.Outer.Length == 0 ? null : source.Outer;
                root.Scope = source.Scope;
                root.File = source.Path;
                root.SourcePath = source.Path;
                root.Elsewhere = true;
                root.SignatureOnly = true;
                foreach (MemberDecl member in root.Members)
                {
                    member.File = source.Path;
                    member.Namespace = source.Namespace;
                    member.Scope = source.Scope;
                    member.OwnedImplementation = false;
                }
                unit.Types.Add(root);
            }
    }

    public void Dispose()
    {
        foreach (DeclarationLease lease in loaded.Values) lease.Dispose();
        loaded.Clear(); catalog.Dispose();
    }
}
