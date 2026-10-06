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
        TextHash = HashCode(text, 0, text.Bytes.Count);
        Section output = new(SectionName, SectionKind.Note);
        WriteTo(output.Bytes);
        obj.Sections.Add(output);
    }

    public byte[] Write()
    {
        ChunkedBytes data = new();
        WriteTo(data);
        return data.ToArray();
    }

    /// <summary>
    /// The summary appended to `data`, a section's bytes: a record a direct
    /// call, so a large unit's is megabytes, and built as a list and copied
    /// out it was held three times over, each contiguous.
    /// </summary>
    public void WriteTo(ChunkedBytes data)
    {
        int start = data.Count;
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
        data.WriteUInt32(start + 8, (uint)(data.Count - start));
    }

    /// <summary>ELF REL stores addends in code words; normalize those fields for hashing.</summary>
    public static byte[] HashCode(Section section, int offset, int length)
    {
        // A WINDOW AT A TIME, hashed as it goes: the whole .text of a unit,
        // copied out as one array to be hashed, was megabytes in one run.
        // The bytes hashed are the same, so the hash is too.
        const int Window = 64 * 1024;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] code = new byte[Math.Min(length, Window)];
        for (int done = 0; done < length;)
        {
            int n = Math.Min(Window, length - done);
            section.Bytes.CopyTo(offset + done, code, n);
            long from = (long)offset + done, until = from + n;
            foreach (Relocation relocation in section.Relocs)
            {
                long start = Math.Max(from, relocation.Offset);
                long end = Math.Min(until, (long)relocation.Offset + 4);
                for (long i = start; i < end; i++) code[(int)(i - from)] = 0;
            }
            hash.AppendData(code, 0, n);
            done += n;
        }
        return hash.GetHashAndReset();
    }

    public static OptimizationSummary? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(s => s.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1) throw new ElfFormatException("duplicate LTO section");
        // Read where it lies, in the section's chunks, not copied out whole.
        ChunkedBytes bytes = sections[0].Bytes;
        int at = 0;
        uint Word()
        {
            if (at > bytes.Count - 4) throw new ElfFormatException("truncated LTO record");
            uint value = bytes.ReadUInt32(at);
            at += 4;
            return value;
        }
        byte[] Blob(int size)
        {
            if (size < 0 || at > bytes.Count - size) throw new ElfFormatException("truncated LTO data");
            byte[] value = bytes.Slice(at, size);
            at += size;
            return value;
        }
        string Text()
        {
            uint size = Word();
            if (size == 0 || size > (uint)(bytes.Count - at)) throw new ElfFormatException("invalid LTO string length");
            string value;
            try { value = new UTF8Encoding(false, true).GetString(Blob((int)size)); }
            catch (DecoderFallbackException) { throw new ElfFormatException("invalid LTO UTF-8"); }
            if (value.Contains('\0')) throw new ElfFormatException("invalid LTO symbol name");
            return value;
        }
        if (bytes.Count < 52 || !bytes.Slice(0, 4).AsSpan().SequenceEqual("CLTO"u8))
            throw new ElfFormatException("invalid LTO header");
        at = 4;
        if (Word() != 1) throw new ElfFormatException("unsupported LTO version");
        if (Word() != bytes.Count) throw new ElfFormatException("invalid LTO byte length");
        uint returns = Word();
        uint calls = Word();
        if (returns > (bytes.Count - 52) / 41 || calls > (bytes.Count - 52) / 9)
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
        if (at != bytes.Count) throw new ElfFormatException("trailing LTO data");
        Section[] text = obj.Sections.Where(s => s.Name == ".text").ToArray();
        if (text.Length != 1 || text[0].Kind != SectionKind.Code
            || !HashCode(text[0], 0, text[0].Bytes.Count).SequenceEqual(result.TextHash))
            throw new ElfFormatException("LTO text hash mismatch");
        return result;
    }
}
