namespace Corsac.Lang.Lto;

/// <summary>An opaque compiler payload with linker-readable import costs and calls.</summary>
public sealed record IrArchiveRecord(string Key, bool Importable, int Instructions,
    IReadOnlyList<string> Calls, byte[] Payload, IReadOnlyList<string>? References = null);
