using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

public sealed class OptimizationSummary
{
    public const string SectionName = ".corsac.lto";
    public List<ConstantReturn> Returns { get; } = new();
    public List<DirectCall> Calls { get; } = new();
    public byte[] TextHash { get; set; } = new byte[32];

    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(s => s.Name == SectionName)) throw new ElfFormatException("duplicate LTO section");
        Section text = obj.Section(".text");
        TextHash = SHA256.HashData(text.Bytes.ToArray());
        Section output = new(SectionName, SectionKind.Note);
        output.Bytes.AddRange(Write());
        obj.Sections.Add(output);
    }

    public byte[] Write()
    {
        List<byte> data = new();
        void Word(uint value)
        {
            for (int i = 0; i < 4; i++) data.Add((byte)(value >> (i * 8)));
        }
        void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length == 0 || value.Contains('\0')) throw new ElfFormatException("invalid LTO symbol name");
            Word((uint)bytes.Length);
            data.AddRange(bytes);
        }
        data.AddRange("CLTO"u8.ToArray());
        Word(1);
        Word(0); // Filled with total byte length below.
        Word((uint)Returns.Count);
        Word((uint)Calls.Count);
        if (TextHash.Length != 32) throw new ElfFormatException("invalid LTO text hash");
        data.AddRange(TextHash);
        foreach (ConstantReturn item in Returns.OrderBy(r => r.Symbol, StringComparer.Ordinal))
        {
            if (item.CodeHash.Length != 32) throw new ElfFormatException("invalid LTO function hash");
            Text(item.Symbol);
            Word(unchecked((uint)item.Value));
            data.AddRange(item.CodeHash);
        }
        foreach (DirectCall item in Calls.OrderBy(c => c.Offset))
        {
            if (item.Offset < 1) throw new ElfFormatException("invalid LTO call offset");
            Word((uint)item.Offset);
            Text(item.Symbol);
        }
        byte[] result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
        return result;
    }

    public static OptimizationSummary? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(s => s.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1) throw new ElfFormatException("duplicate LTO section");
        byte[] bytes = sections[0].Bytes.ToArray();
        int at = 0;
        uint Word()
        {
            if (at > bytes.Length - 4) throw new ElfFormatException("truncated LTO record");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
            at += 4;
            return value;
        }
        byte[] Blob(int size)
        {
            if (size < 0 || at > bytes.Length - size) throw new ElfFormatException("truncated LTO data");
            byte[] value = bytes.AsSpan(at, size).ToArray();
            at += size;
            return value;
        }
        string Text()
        {
            uint size = Word();
            if (size == 0 || size > (uint)(bytes.Length - at)) throw new ElfFormatException("invalid LTO string length");
            string value;
            try { value = new UTF8Encoding(false, true).GetString(Blob((int)size)); }
            catch (DecoderFallbackException) { throw new ElfFormatException("invalid LTO UTF-8"); }
            if (value.Contains('\0')) throw new ElfFormatException("invalid LTO symbol name");
            return value;
        }
        if (bytes.Length < 52 || !bytes.AsSpan(0, 4).SequenceEqual("CLTO"u8))
            throw new ElfFormatException("invalid LTO header");
        at = 4;
        if (Word() != 1) throw new ElfFormatException("unsupported LTO version");
        if (Word() != bytes.Length) throw new ElfFormatException("invalid LTO byte length");
        uint returns = Word();
        uint calls = Word();
        if (returns > (bytes.Length - 52) / 41 || calls > (bytes.Length - 52) / 9)
            throw new ElfFormatException("invalid LTO record count");
        OptimizationSummary result = new() { TextHash = Blob(32) };
        HashSet<string> names = new(StringComparer.Ordinal);
        for (uint i = 0; i < returns; i++)
        {
            string name = Text();
            if (!names.Add(name)) throw new ElfFormatException("duplicate LTO function record");
            result.Returns.Add(new ConstantReturn(name, unchecked((int)Word()), Blob(32)));
        }
        HashSet<int> offsets = new();
        for (uint i = 0; i < calls; i++)
        {
            uint offset = Word();
            if (offset > int.MaxValue || offset == 0 || !offsets.Add((int)offset))
                throw new ElfFormatException("invalid or duplicate LTO call offset");
            result.Calls.Add(new DirectCall((int)offset, Text()));
        }
        if (at != bytes.Length) throw new ElfFormatException("trailing LTO data");
        Section[] text = obj.Sections.Where(s => s.Name == ".text").ToArray();
        if (text.Length != 1 || text[0].Kind != SectionKind.Code
            || !SHA256.HashData(text[0].Bytes.ToArray()).SequenceEqual(result.TextHash))
            throw new ElfFormatException("LTO text hash mismatch");
        return result;
    }
}
