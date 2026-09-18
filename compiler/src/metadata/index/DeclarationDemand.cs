namespace Corsac.Lang.Metadata;

/// <summary>Requests a declaration before restarting a unit's binding transaction.</summary>
public sealed class DeclarationDemand : Exception
{
    public string Key { get; }
    public DeclarationDemand(string key) : base("Declaration required: " + key) { Key = key; }
}
