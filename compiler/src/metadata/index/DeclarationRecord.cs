namespace Corsac.Lang.Metadata;

/// <summary>One indexed declaration fragment. Equal keys retain all partial declarations.</summary>
public sealed record DeclarationRecord(string Key, byte[] Payload);
