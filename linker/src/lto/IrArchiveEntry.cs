namespace Corsac.Lang.Lto;

/// <summary>A body location, not a deserialized compiler IR graph.</summary>
public sealed record IrArchiveEntry(string Key, bool Importable, int Instructions,
    IReadOnlyList<string> Calls, int Offset, int Length, byte[] Hash, IReadOnlyList<string> References, long DecodeBytes)
{
    /// <summary>
    /// A REFERENCE ONLY COMPARED WITH: a descriptor a function names only to
    /// test an object's type against it (`GetType() == typeof(X)`, an exact
    /// type test), marked so. It makes no object of X, so the link's count of
    /// the types made leaves it out (VirtualTargets.MadeTypes); it is reached
    /// all the same (Named).
    /// </summary>
    public const char ComparedOnly = '=';

    /// <summary>The symbol a reference names, the mark taken off.</summary>
    public static string Named(string reference) => reference.Length > 1 && reference[0] == ComparedOnly ? reference[1..] : reference;
}
