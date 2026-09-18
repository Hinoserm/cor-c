namespace Corsac.Lang.Metadata;

/// <summary>Pins declaration fragments until the owning compilation work releases them.</summary>
public sealed class DeclarationLease : IDisposable
{
    private IReadOnlyList<SourceDeclaration>? records;
    private Action? release;
    internal DeclarationLease(IReadOnlyList<SourceDeclaration> records, Action release)
    { this.records = records; this.release = release; }
    public IReadOnlyList<SourceDeclaration> Records => records ?? throw new ObjectDisposedException(nameof(DeclarationLease));
    public void Dispose()
    {
        Action? action = Interlocked.Exchange(ref release, null);
        records = null;
        action?.Invoke();
    }
}
