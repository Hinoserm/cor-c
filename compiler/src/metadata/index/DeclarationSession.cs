namespace Corsac.Lang.Metadata;

/// <summary>
/// What every source of one project shares while they are compiled together.
///
/// A project's sources import the same headers over and over: each of the
/// kernel's hundred and eighteen units reads the same standard library
/// declarations out of the same index and lexes the same text again. None of
/// that depends on which source is being compiled, so it is opened once here
/// and handed to each unit's <see cref="IndexedDeclarations"/>.
///
/// What is NOT shared is the set a unit actually asked for. That set is what
/// its dependency receipt records, and sharing it would make every source
/// appear to depend on everything any other source needed, so an unrelated
/// edit would rebuild the project. See IndexedDeclarations.
///
/// The catalog and the token cache both take their own locks, so several
/// units may compile against one session at the same time.
/// </summary>
public sealed class DeclarationSession : IDisposable
{
    public DeclarationCatalog Catalog { get; }
    public SyntaxTokenCache Tokens { get; }
    public string Assembly { get; }
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces { get; }
    public IReadOnlySet<(string Name, int Arity)> LibraryInterfaces { get; }

    /// <summary>
    /// Names looked up in the index and what they turned out to be, shared
    /// because the answer depends on the index alone. A miss is remembered as
    /// a miss: most of what a signature names is a type parameter or a
    /// primitive and would otherwise be searched for once per source.
    /// </summary>
    private readonly Dictionary<(string Name, int Arity), string?> speculated = new();
    private readonly object gate = new();

    /// <summary>
    /// WHAT THE SHARED CACHES MAY HOLD, all of it bounded and all of it
    /// evicted least-recently-used first. The three together are the whole of
    /// what a project session keeps beyond the unit being compiled: lexed
    /// headers, decoded index payloads, and the verified text of the library
    /// files. The defaults come to a little over three hundred megabytes
    /// against a target with about a gigabyte, and the build may lower them
    /// (CORC_TOKEN_BUDGET, CORC_DECL_BUDGET, CORC_SOURCE_BUDGET) for a
    /// smaller machine. None of them is per worker: one session serves every
    /// thread, so raising --jobs does not raise this.
    /// </summary>
    public DeclarationSession(string path, string assembly, long declarationBudgetBytes = 32 * 1024 * 1024,
        long tokenBudgetBytes = 256 * 1024 * 1024, long sourceBudgetBytes = 16 * 1024 * 1024)
    {
        Catalog = new DeclarationCatalog(path, declarationBudgetBytes, sourceBudgetBytes);
        Tokens = new SyntaxTokenCache(tokenBudgetBytes);
        Assembly = assembly;
        Interfaces = Catalog.Interfaces(assembly);
        LibraryInterfaces = Catalog.LibraryInterfaces(assembly);
    }

    public bool Speculated((string Name, int Arity) key, out string? value)
    {
        lock (gate) return speculated.TryGetValue(key, out value);
    }

    public void Speculate((string Name, int Arity) key, string? value)
    {
        lock (gate) speculated[key] = value;
    }

    public void Dispose() => Catalog.Dispose();
}
