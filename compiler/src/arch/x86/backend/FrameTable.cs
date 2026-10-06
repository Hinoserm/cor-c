#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// WHAT EACH ADDRESS IN THE IMAGE IS, so a fault can say where it happened.
///
/// .NET prints an unhandled exception as its type, its message, and then the
/// frames -- `at Ns.Type.Method(args) in file.cs:line N` -- and that is worth
/// more here than on a machine with a debugger, because there is no debugger.
/// A kernel that faults can name the function it faulted in, and a program
/// that throws can say which line threw.
///
/// The table is written into .rodata under one symbol, so it is mapped where
/// the program can read it, needs no section the linker has to be told about,
/// and works the same in a flat freestanding image as in an ELF.
///
/// COMPACT, BECAUSE IT IS IN EVERY IMAGE. A fixed twenty bytes a function came
/// to sixty kilobytes on the kernel. The addresses ascend, so each entry says
/// only how far its function starts past the end of the one before -- nearly
/// always under four bytes of padding -- and the whole table needs ONE
/// relocation, the first function's address. Its name is a delta from the
/// name before, its file is said only when it changes, and its line program
/// by its length or by where it lies among the programs the table shares.
/// Nothing can binary-search it, and nothing needs to: the only reader is a
/// fault, and a fault can afford a walk.
///
/// The layout ('CFR5') is FrameTableFormat's, in the linker's object model,
/// which the link shares when it rewrites the table; the strings follow the
/// line programs, each ending in a zero byte.
/// </summary>
internal static class FrameTable
{
    public const string Symbol = "__corsac_frames";

    public const uint Magic = FrameTableFormat.Local;

    /// <summary>
    /// One function. <paramref name="Label"/> is the symbol the linker knows
    /// it by and <paramref name="Name"/> is what a person called it, and the
    /// two are not interchangeable: the table's one relocation names the
    /// first function, and naming it `Type.Method` asked the linker for a
    /// symbol no object defines.
    /// </summary>
    internal readonly record struct Entry(
        string Label, string Name, string File, int Start, int Size, List<(int Offset, int Line)> Lines);

    /// <summary>Builds the table's bytes, and the one relocation it needs.</summary>
    public static byte[] Build(IReadOnlyList<Entry> entries, out int baseFixup, out string baseSymbol)
    {
        baseFixup = FrameTableFormat.BaseOffset;
        baseSymbol = entries.Count > 0 ? entries[0].Label : "";

        List<byte> strings = new();
        Dictionary<string, int> interned = new(StringComparer.Ordinal);

        int String(string text)
        {
            if (interned.TryGetValue(text, out int already))
            {
                return already;
            }
            int at = strings.Count;
            interned[text] = at;
            strings.AddRange(Encoding.UTF8.GetBytes(text));
            strings.Add(0);
            return at;
        }

        // The line programs go in as pairs; the format codes them, and shares
        // the ones more than one function here carries.
        List<FrameTableFormat.Entry> coded = new(entries.Count);
        long origin = entries.Count > 0 ? entries[0].Start : 0;

        foreach (Entry e in entries)
        {
            coded.Add(new FrameTableFormat.Entry(e.Start - origin, e.Size, String(e.Name), String(e.File), FrameTableFormat.Normal(e.Lines)));
        }

        return FrameTableFormat.Build(FrameTableFormat.Local, coded, strings);
    }
}
