using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Compiler-certified equivalent definitions; ordinary strong duplicates remain errors.</summary>
public static class CoalescingContract
{
    public const string SectionName = ".corsac.coalesce";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Attach(ObjectFile obj, IReadOnlyDictionary<string, byte[]> semantics)
    {
        if (semantics.Count == 0) return;
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate coalescing contract");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(0x4c414f43u); writer.Write(1); writer.Write(semantics.Count); // COAL
        foreach (var record in semantics.OrderBy(record => record.Key, StringComparer.Ordinal))
        {
            Symbol[] symbols = obj.Symbols.Where(symbol => symbol.Name == record.Key && symbol.IsDefined && symbol.Global).ToArray();
            if (symbols.Length != 1 || record.Value.Length != 32) throw new ElfFormatException("Invalid coalescing definition " + record.Key);
            byte[] name = Utf8.GetBytes(record.Key);
            if (name.Length == 0 || name.Length > 4096 || record.Key.Contains('\0')) throw new ElfFormatException("Invalid coalescing name");
            writer.Write(name.Length); writer.Write(name); writer.Write(record.Value);
            writer.Write(DefinitionFingerprint.Compute(obj, symbols[0]));
        }
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(stream.ToArray()); obj.Sections.Add(section);
    }

    public static Dictionary<string, byte[]> Read(ObjectFile obj)
    {
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return result;
        if (sections.Length != 1) throw new ElfFormatException("Duplicate coalescing contract");
        using MemoryStream stream = new(sections[0].Bytes.ToArray(), writable: false);
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != 0x4c414f43 || reader.ReadInt32() != 1) throw new ElfFormatException("Unsupported coalescing contract");
            int count = reader.ReadInt32();
            if (count < 0 || count > (stream.Length - stream.Position) / 69) throw new ElfFormatException("Invalid coalescing record count");
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 4096 || length > stream.Length - stream.Position - 64) throw new ElfFormatException("Invalid coalescing name length");
                string name = Utf8.GetString(reader.ReadBytes(length));
                byte[] semantic = reader.ReadBytes(32), native = reader.ReadBytes(32);
                if (name.Contains('\0') || !seen.Add(name)) throw new ElfFormatException("Invalid or duplicate coalescing name");
                if (obj.SuppressedDefinitions.Contains(name)) continue;
                Symbol[] symbols = obj.Symbols.Where(symbol => symbol.Name == name && symbol.IsDefined && symbol.Global).ToArray();
                if (symbols.Length != 1 || !DefinitionFingerprint.Compute(obj, symbols[0]).SequenceEqual(native))
                    throw new ElfFormatException("Coalescing definition integrity mismatch: " + name);
                result.Add(name, semantic);
            }
            if (stream.Position != stream.Length) throw new ElfFormatException("Trailing coalescing contract data");
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated coalescing contract"); }
        catch (DecoderFallbackException) { throw new ElfFormatException("Invalid UTF-8 in coalescing contract"); }
        return result;
    }
}
