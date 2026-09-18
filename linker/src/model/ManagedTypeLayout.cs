namespace Corsac.Lang.Ir;

/// <summary>Compiler-certified managed layout assumptions for a canonical type name.</summary>
public sealed record ManagedTypeLayout(string Name, byte[] Fingerprint);
