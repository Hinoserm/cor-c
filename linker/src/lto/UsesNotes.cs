#nullable enable
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT EACH DEFINITION NAMES AS WRITTEN, for the unused-code report
/// (`--unused-report`, UnusedReport): a unit's every function and data item
/// as lowering made them, before any pass inlined a call away or dropped a
/// static, each with the symbols it calls or takes the address of, the
/// statics it only stores into, and where the source declared it. In a
/// section of its own (`.corsac.uses`), written only when the report is
/// asked for; the link reads it and lets it go no further.
/// </summary>
public static class UsesNotes
{
    public const string SectionName = ".corsac.uses";
    const int Version = 1;

    public enum Kind : byte { Function = 1, Data = 2 }

    /// <summary>
    /// One definition. `Reads` are the symbols it calls, loads from or takes
    /// the address of; `Writes` the statics it only stores into, which do not
    /// make them used. `Display` is the name as written (`Type.Member`), null
    /// for what the compiler made and nobody wrote.
    /// </summary>
    public sealed record Definition(Kind Kind, string Name, bool Exported, bool FromLibrary, string? File, int Line, string? Display,
        string[] Reads, string[] Writes);

    public static void Attach(ObjectFile obj, IReadOnlyList<Definition> definitions)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(definitions.Count);
            foreach (Definition d in definitions)
            {
                writer.Write((byte)d.Kind);
                writer.Write(d.Name);
                writer.Write(d.Exported);
                writer.Write(d.FromLibrary);
                writer.Write(d.File ?? "");
                writer.Write(d.Line);
                writer.Write(d.Display ?? "");
                writer.Write(d.Reads.Length);
                foreach (string name in d.Reads) writer.Write(name);
                writer.Write(d.Writes.Length);
                foreach (string name in d.Writes) writer.Write(name);
            }
        }
        Section section = new(SectionName, SectionKind.Note) { Align = 1 };
        section.Bytes.AddRange(stream.ToArray());
        obj.Sections.Add(section);
    }

    /// <summary>The object's notes, or null when it was compiled without them.</summary>
    public static List<Definition>? Read(ObjectFile obj)
    {
        Section? section = obj.Sections.FirstOrDefault(s => s.Name == SectionName);
        if (section is null) return null;
        using Stream stream = section.OpenRead();
        using BinaryReader reader = new(stream, Encoding.UTF8);
        if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
        int count = reader.ReadInt32();
        List<Definition> definitions = new(count);
        for (int i = 0; i < count; i++)
        {
            Kind kind = (Kind)reader.ReadByte();
            string name = reader.ReadString();
            bool exported = reader.ReadBoolean();
            bool library = reader.ReadBoolean();
            string file = reader.ReadString();
            int line = reader.ReadInt32();
            string display = reader.ReadString();
            string[] reads = new string[reader.ReadInt32()];
            for (int k = 0; k < reads.Length; k++) reads[k] = reader.ReadString();
            string[] writes = new string[reader.ReadInt32()];
            for (int k = 0; k < writes.Length; k++) writes[k] = reader.ReadString();
            definitions.Add(new(kind, name, exported, library, file.Length == 0 ? null : file, line, display.Length == 0 ? null : display, reads, writes));
        }
        return definitions;
    }

    /// <summary>The notes taken out of every object: they are the link's to read, not the image's to carry.</summary>
    public static void Strip(IEnumerable<ObjectFile> objects)
    {
        foreach (ObjectFile obj in objects) obj.Sections.RemoveAll(section => section.Name == SectionName);
    }
}
