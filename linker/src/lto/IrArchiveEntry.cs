namespace Corsac.Lang.Lto;

/// <summary>A body location, not a deserialized compiler IR graph.</summary>
public sealed record IrArchiveEntry(string Key, bool Importable, int Instructions,
    IReadOnlyList<string> Calls, int Offset, int Length, byte[] Hash, IReadOnlyList<string> References, long DecodeBytes);
