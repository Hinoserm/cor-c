using System.Text;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

public static class ManagedLayoutContract
{
    public const string SectionName = ".corsac.layout";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Attach(ObjectFile obj, IEnumerable<ManagedTypeLayout> types)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate managed layout contract");
        ManagedTypeLayout[] records = types.OrderBy(type => type.Name, StringComparer.Ordinal).ToArray();
        if (records.Select(type => type.Name).Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new ElfFormatException("Duplicate managed layout type");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(0x594c4d43u); writer.Write(1); writer.Write(records.Length); // CMLY
        foreach (ManagedTypeLayout type in records)
        {
            byte[] name = Utf8.GetBytes(type.Name);
            if (name.Length == 0 || name.Length > 4096 || type.Name.Contains('\0') || type.Fingerprint.Length != 32)
                throw new ElfFormatException("Invalid managed layout type");
            writer.Write(name.Length); writer.Write(name); writer.Write(type.Fingerprint);
        }
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(stream.ToArray()); obj.Sections.Add(section);
    }

    public static void Validate(IEnumerable<(string Name, ObjectFile Object)> inputs)
    {
        Dictionary<string, (byte[] Fingerprint, string Input)> known = new(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            Section[] sections = input.Object.Sections.Where(section => section.Name == SectionName).ToArray();
            if (sections.Length == 0) continue;
            if (sections.Length != 1) throw new ElfFormatException(input.Name + ": duplicate managed layout contract");
            using MemoryStream stream = new(sections[0].Bytes.ToArray(), writable: false);
            using BinaryReader reader = new(stream, Utf8);
            try
            {
                if (reader.ReadUInt32() != 0x594c4d43 || reader.ReadInt32() != 1)
                    throw new ElfFormatException(input.Name + ": unsupported managed layout contract");
                int count = reader.ReadInt32();
                if (count < 0 || count > (stream.Length - stream.Position) / 37)
                    throw new ElfFormatException(input.Name + ": invalid managed layout count");
                HashSet<string> seen = new(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32();
                    if (length < 1 || length > 4096 || length > stream.Length - stream.Position - 32)
                        throw new ElfFormatException(input.Name + ": invalid managed layout name length");
                    string name = Utf8.GetString(reader.ReadBytes(length));
                    byte[] fingerprint = reader.ReadBytes(32);
                    if (name.Contains('\0') || !seen.Add(name)) throw new ElfFormatException(input.Name + ": invalid or duplicate managed layout type");
                    if (known.TryGetValue(name, out var previous) && !fingerprint.SequenceEqual(previous.Fingerprint))
                        throw new LinkException(new[] { input.Name + ": managed layout of '" + name + "' conflicts with " + previous.Input });
                    known.TryAdd(name, (fingerprint, input.Name));
                }
                if (stream.Position != stream.Length) throw new ElfFormatException(input.Name + ": trailing managed layout data");
            }
            catch (EndOfStreamException) { throw new ElfFormatException(input.Name + ": truncated managed layout contract"); }
            catch (DecoderFallbackException) { throw new ElfFormatException(input.Name + ": invalid UTF-8 in managed layout contract"); }
        }
    }
}
