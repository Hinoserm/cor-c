namespace Corsac.Lang.Metadata;

/// <summary>One indexed declaration fragment. Equal keys retain all partial declarations.</summary>
public sealed record DeclarationRecord(string Key, byte[] Payload)
{
    /// <summary>
    /// How many indexes under the one asked it was read from
    /// (DeclarationIndex.Under): 0 its own, 1 the index under it -- a
    /// kernel's, under a module's -- 2 the one under that (a kernel's, under
    /// nt's on gui's). Its part of a type is the image's beneath, the deepest
    /// owning the type however the others' parts sort (TypeDecl.Below,
    /// Frontend's sort before MergePartialTypes).
    /// </summary>
    public int Below { get; init; }
}
