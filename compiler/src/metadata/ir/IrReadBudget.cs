namespace Corsac.Lang.Metadata;

/// <summary>Charge decoded nodes and arrays before allocating them, not a multiplier of file size.</summary>
public sealed class IrReadBudget
{
    public long Limit { get; }
    public long Used { get; private set; }
    public IrReadBudget(long limit = 64L * 1024 * 1024)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
    }
    public void Charge(long count, int bytes, string kind)
    {
        if (count < 0 || bytes < 0 || count > (Limit - Used) / Math.Max(1, bytes))
            throw new InvalidDataException("IR decode budget exceeded by " + kind + "; accounted=" + Used + ", limit=" + Limit);
        Used += count * bytes;
    }
}
