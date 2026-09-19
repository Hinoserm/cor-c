namespace Corsac.Lang.Metadata;

/// <summary>Requests a declaration before restarting a unit's binding transaction.</summary>
public sealed class DeclarationDemand : Exception
{
    public IReadOnlyList<string> Keys { get; }
    public string Key => Keys[0];
    public DeclarationDemand(string key) : this(new[] { key }) { }
    public DeclarationDemand(IEnumerable<string> keys) : base("Additional declarations required")
    {
        Keys = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (Keys.Count == 0) throw new ArgumentException("A declaration request cannot be empty", nameof(keys));
    }
}
