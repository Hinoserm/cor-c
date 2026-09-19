namespace Corsac.Lang.Metadata;

/// <summary>Collect independent missing signatures, then discard the incomplete binding transaction.</summary>
public sealed class DeclarationBatch
{
    private HashSet<string>? keys;
    public void Add(DeclarationDemand demand)
    {
        keys ??= new(StringComparer.Ordinal);
        foreach (string key in demand.Keys) keys.Add(key);
    }
    public void ThrowIfAny()
    {
        if (keys is not null && keys.Count != 0) throw new DeclarationDemand(keys);
    }
}
