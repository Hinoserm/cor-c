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
/// to sixty kilobytes on the kernel; these entries average nine. The addresses
/// ascend, so each is written as the distance from the one before it and the
/// whole table needs ONE relocation -- the first function's address. Nothing
/// can binary-search it, and nothing needs to: the only reader is a fault, and
/// a fault can afford a walk.
///
/// The layout, all little-endian, all offsets from the symbol:
///
///   u32 magic  'CFRM'      u32 count
///   u32 base   (relocated) u32 strings   u32 lines
///   count entries, in address order:
///     uleb start delta from the previous function (the first is 0 from base)
///     uleb size            uleb name      uleb file
///     uleb line program, plus one -- zero meaning the function has none
///   the line programs: uleb pairs,  uleb count then (uleb offset, sleb line)
///   the strings, each ending in a zero byte
/// </summary>
internal static class FrameTable
{
    public const string Symbol = "__corsac_frames";

    public const uint Magic = 0x4D524643;        // 'CFRM', little-endian

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
        baseFixup = 8;
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

        // The line programs first, so an entry can name where its own begins.
        List<byte> lines = new();
        List<int> lineAt = new();

        foreach (Entry e in entries)
        {
            if (e.Lines.Count == 0)
            {
                lineAt.Add(-1);
                continue;
            }
            lineAt.Add(lines.Count);
            Uleb(lines, (uint)e.Lines.Count);
            int offset = 0, line = 0;
            foreach ((int at, int n) in e.Lines)
            {
                Uleb(lines, (uint)(at - offset));
                Sleb(lines, n - line);
                offset = at;
                line = n;
            }
        }

        List<byte> table = new();
        int previous = entries.Count > 0 ? entries[0].Start : 0;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            Uleb(table, (uint)(e.Start - previous));
            previous = e.Start;
            Uleb(table, (uint)e.Size);
            Uleb(table, (uint)String(e.Name));
            Uleb(table, (uint)String(e.File));
            Uleb(table, lineAt[i] < 0 ? 0u : (uint)(lineAt[i] + 1));
        }

        const int header = 20;
        int linesOff = header + table.Count;
        int stringsOff = linesOff + lines.Count;

        List<byte> all = new();
        U32(all, Magic);
        U32(all, (uint)entries.Count);
        U32(all, 0);                     // the base address, relocated
        U32(all, (uint)stringsOff);
        U32(all, (uint)linesOff);
        all.AddRange(table);
        all.AddRange(lines);
        all.AddRange(strings);
        return all.ToArray();
    }

    private static void U32(List<byte> into, uint v)
    {
        into.Add((byte)v);
        into.Add((byte)(v >> 8));
        into.Add((byte)(v >> 16));
        into.Add((byte)(v >> 24));
    }

    private static void Uleb(List<byte> into, uint v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            into.Add(v != 0 ? (byte)(b | 0x80) : b);
        }
        while (v != 0);
    }

    private static void Sleb(List<byte> into, int v)
    {
        while (true)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            bool sign = (b & 0x40) != 0;

            if ((v == 0 && !sign) || (v == -1 && sign))
            {
                into.Add(b);
                return;
            }
            into.Add((byte)(b | 0x80));
        }
    }
}
