namespace Corsac.Lang.Metadata;

/// <summary>One indexed declaration fragment. Equal keys retain all partial declarations.</summary>
public sealed record DeclarationRecord(string Key, byte[] Payload)
{
    /// <summary>
    /// Read from an index under the one asked (DeclarationIndex.Under): a
    /// kernel's, under a module's. Its part of a type is the image's beneath,
    /// which owns the type however the module's own part sorts
    /// (TypeDecl.Below, Frontend's sort before MergePartialTypes).
    /// </summary>
    public bool Below { get; init; }
}
