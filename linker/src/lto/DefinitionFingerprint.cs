using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Native definition integrity, including referenced object-local constants/code.</summary>
public static class DefinitionFingerprint
{
    public static byte[] Compute(ObjectFile obj, Symbol root)
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
                byte[] bytes = section.Bytes.GetRange(checked((int)symbol.Offset), checked((int)symbol.Size)).ToArray();
                Relocation[] relocations = section.Relocs.Where(relocation => relocation.Offset >= symbol.Offset && relocation.Offset < symbol.Offset + symbol.Size)
                    .OrderBy(relocation => relocation.Offset).ToArray();
                foreach (Relocation relocation in relocations)
                {
                    long offset = relocation.Offset - symbol.Offset;
                    if (offset > bytes.Length - 4) throw new ElfFormatException("Coalesced definition has a truncated relocation");
                    Array.Clear(bytes, (int)offset, 4);
                }
                writer.Write(bytes); writer.Write(relocations.Length);
                foreach (Relocation relocation in relocations)
                {
                    writer.Write(relocation.Offset - symbol.Offset); writer.Write((int)relocation.Kind); writer.Write(relocation.Addend);
                    Symbol? local = obj.Symbols.FirstOrDefault(candidate => candidate.Name == relocation.Symbol && candidate.IsDefined && !candidate.Global);
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
