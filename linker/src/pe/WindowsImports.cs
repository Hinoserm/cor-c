#nullable enable
namespace Corsac.Lang.Pe;

/// <summary>
/// HOW AN OBJECT NAMES A FUNCTION OF A WINDOWS DLL: one undefined symbol,
/// `kernel32.dll!WriteFile` (or `kernel32.dll!#17` by ordinal) -- the DLL as
/// LoadLibrary would be asked for it, lower case and with its extension, and
/// the export as the DLL spells it. The PE link makes each one an entry of the
/// import directory and a `jmp [IAT]` thunk the symbol names, so a call to it
/// is an ordinary call; nothing else in an object says what is imported.
/// </summary>
public static class WindowsImports
{
    /// <summary>The symbol an object names an import by.</summary>
    public static string Symbol(string dll, string export) => DllName(dll) + "!" + export;

    /// <summary>
    /// An import's DLL and export, from its symbol: the export's name, or
    /// null with the ordinal for one by number. False for a symbol that
    /// names no import.
    /// </summary>
    public static bool TryParse(string symbol, out string dll, out string? export, out ushort ordinal)
    {
        dll = "";
        export = null;
        ordinal = 0;
        int bang = symbol.IndexOf('!');
        if (bang <= 0 || bang == symbol.Length - 1)
        {
            return false;
        }
        dll = symbol[..bang];
        string rest = symbol[(bang + 1)..];
        if (rest[0] == '#')
        {
            return ushort.TryParse(rest.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ordinal) && ordinal != 0;
        }
        export = rest;
        return true;
    }

    /// <summary>
    /// The DLL a [DllImport] names, as the import directory names it: lower
    /// case, and ".dll" added where the name has no extension, which is what
    /// LoadLibrary and .NET's probing both do with "kernel32".
    /// </summary>
    public static string DllName(string written)
    {
        string lower = written.ToLowerInvariant();
        return Path.HasExtension(lower) ? lower : lower + ".dll";
    }
}
