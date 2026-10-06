using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// An object's symbols and relocations indexed ONCE for every fingerprint taken
/// of it: the definitions by name, and each section's relocations in offset
/// order. Asked afresh for each symbol -- every relocation of the section
/// filtered, every symbol of the object searched for each relocation -- the
/// fingerprints of one object cost the square of its size twice over, most of
/// a large unit's compile.
/// </summary>
public sealed class DefinitionIndex
{
    private readonly Dictionary<string, List<Symbol>> globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Symbol> locals = new(StringComparer.Ordinal);
    private readonly Dictionary<Section, Relocation[]> relocations = new();

    public DefinitionIndex(ObjectFile obj)
    {
        Obj = obj;
        foreach (Symbol symbol in obj.Symbols)
        {
            if (!symbol.IsDefined) continue;
            if (symbol.Global)
            {
                if (!globals.TryGetValue(symbol.Name, out List<Symbol>? same)) globals[symbol.Name] = same = new();
                same.Add(symbol);
            }
            else locals.TryAdd(symbol.Name, symbol);   // the first, as FirstOrDefault found it
        }
    }

    public ObjectFile Obj { get; }

    /// <summary>The defined global symbols of this name, in the object's order.</summary>
    public IReadOnlyList<Symbol> Globals(string name)
        => globals.TryGetValue(name, out List<Symbol>? same) ? same : Array.Empty<Symbol>();

    /// <summary>The first defined local symbol of this name, or null.</summary>
    public Symbol? Local(string name) => locals.TryGetValue(name, out Symbol? local) ? local : null;

    /// <summary>
    /// The section's relocations with offsets in [from, to), by offset, those
    /// at one offset in the section's own order (a stable sort, as OrderBy is).
    /// </summary>
    public ArraySegment<Relocation> Within(Section section, long from, long to)
    {
        if (!relocations.TryGetValue(section, out Relocation[]? sorted))
        {
            // Offsets read out once: a Relocation is a struct, and every read
            // of one through the list was a copy of it, for every comparison.
            Relocation[] all = section.Relocs.ToArray();
            int[] offsets = new int[all.Length];
            int[] order = new int[all.Length];
            for (int i = 0; i < order.Length; i++) { order[i] = i; offsets[i] = all[i].Offset; }
            Array.Sort(order, (a, b) => offsets[a] != offsets[b] ? offsets[a].CompareTo(offsets[b]) : a.CompareTo(b));
            sorted = new Relocation[order.Length];
            for (int i = 0; i < order.Length; i++) sorted[i] = all[order[i]];
            relocations[section] = sorted;
        }
        int low = LowerBound(sorted, from), high = LowerBound(sorted, to);
        return new ArraySegment<Relocation>(sorted, low, high - low);
    }

    private static int LowerBound(Relocation[] sorted, long offset)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (sorted[middle].Offset < offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

/// <summary>Native definition integrity, including referenced object-local constants/code.</summary>
public static class DefinitionFingerprint
{
    public static byte[] Compute(ObjectFile obj, Symbol root) => Compute(new DefinitionIndex(obj), root);

    public static byte[] Compute(DefinitionIndex index, Symbol root)
    {
        using SHA256 hash = SHA256.Create();
        using CryptoStream stream = new(Stream.Null, hash, CryptoStreamMode.Write);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        Dictionary<Symbol, int> active = new();
        int visits = 0;
        void Definition(Symbol symbol)
        {
            if (active.TryGetValue(symbol, out int cycle)) { writer.Write((byte)0); writer.Write(cycle); return; }
            if (++visits > 65536 || active.Count >= 128)
                throw new ElfFormatException("Coalescing definition graph exceeds its validation budget");
            Section section = symbol.Section ?? throw new ElfFormatException("Coalesced symbol is undefined: " + symbol.Name);
            if (symbol.Offset < 0 || symbol.Size < 0 || symbol.Offset > section.Size || symbol.Size > section.Size - symbol.Offset)
                throw new ElfFormatException("Coalesced symbol is outside its section: " + symbol.Name);
            active.Add(symbol, active.Count);
            writer.Write((byte)1); writer.Write((int)section.Kind); writer.Write(symbol.Size); writer.Write(symbol.IsFunction);
            if (section.Kind != SectionKind.Uninitialised)
            {
                byte[] bytes = section.Bytes.Slice(checked((int)symbol.Offset), checked((int)symbol.Size));
                ArraySegment<Relocation> relocations = index.Within(section, symbol.Offset, symbol.Offset + symbol.Size);
                foreach (Relocation relocation in relocations)
                {
                    long offset = relocation.Offset - symbol.Offset;
                    if (offset > bytes.Length - 4) throw new ElfFormatException("Coalesced definition has a truncated relocation");
                    Array.Clear(bytes, (int)offset, 4);
                }
                writer.Write(bytes); writer.Write(relocations.Count);
                foreach (Relocation relocation in relocations)
                {
                    writer.Write(relocation.Offset - symbol.Offset); writer.Write((int)relocation.Kind); writer.Write(relocation.Addend);
                    Symbol? local = index.Local(relocation.Symbol);
                    writer.Write(local is not null);
                    if (local is not null) Definition(local);
                    else writer.Write(relocation.Symbol);
                }
            }
            active.Remove(symbol);
        }
        Definition(root);
        writer.Flush(); stream.FlushFinalBlock();
        return hash.Hash!;
    }
}
