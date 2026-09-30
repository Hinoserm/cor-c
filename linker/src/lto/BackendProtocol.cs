using System.Text;

namespace Corsac.Lang.Lto;

/// <summary>Bounded framed requests over one persistent compiler-backend process.</summary>
public static class BackendProtocol
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void WriteRequest(BinaryWriter writer, BackendRequest request)
    {
        writer.Write((byte)0x52); writer.Write(6);
        WriteText(writer, request.Input); WriteText(writer, request.Output); writer.Write(request.Imports.Count);
        foreach (IrImport import in request.Imports)
        { WriteText(writer, import.Symbol); writer.Write(import.DecodeBytes); writer.Write(import.Body.Length); writer.Write(import.Body); }
        writer.Write(request.Retained?.Count ?? -1);
        if (request.Retained is not null)
            foreach (string key in request.Retained.Order(StringComparer.Ordinal)) WriteText(writer, key);
        // The link's lifetime answers for the unit (LifetimeSolver), in name order.
        LifetimeFacts? facts = request.Facts;
        writer.Write(facts is null ? -1 : facts.Escapes.Count);
        if (facts is not null)
        {
            foreach ((string name, bool[] escapes) in facts.Escapes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteText(writer, name); writer.Write(escapes.Length);
                foreach (bool escape in escapes) writer.Write(escape);
            }
            writer.Write(facts.Fresh.Count);
            foreach (string name in facts.Fresh.Order(StringComparer.Ordinal)) WriteText(writer, name);
            writer.Write(facts.Helpers.Count);
            foreach (string name in facts.Helpers.Order(StringComparer.Ordinal)) WriteText(writer, name);
            writer.Write(facts.Fields.Count);
            foreach ((string name, SolvedFields?[] parameters) in facts.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteText(writer, name); writer.Write(parameters.Length);
                foreach (SolvedFields? fields in parameters) WriteFields(writer, fields);
            }
            writer.Write(facts.FreshFields.Count);
            foreach ((string name, SolvedFields fields) in facts.FreshFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            { WriteText(writer, name); WriteFields(writer, fields); }
            writer.Write(facts.ForeignCatchable?.Length ?? -1);
            foreach (string type in facts.ForeignCatchable ?? Array.Empty<string>()) WriteText(writer, type);
            // The owned fields, by name: offset, and whether a store fills it;
            // then the functions and virtual symbols that hand one back.
            OwnedFieldFacts? owned = facts.OwnedFields;
            writer.Write(owned?.Fields.Count ?? -1);
            if (owned is not null)
            {
                foreach ((string field, long offset) in owned.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { WriteText(writer, field); writer.Write(offset); writer.Write(owned.Mapped.Contains(field)); }
                writer.Write(owned.Borrowers.Count);
                foreach (string name in owned.Borrowers.Order(StringComparer.Ordinal)) WriteText(writer, name);
            }
        }
        writer.Flush();
    }
    public static BackendRequest? ReadRequest(BinaryReader reader)
    {
        int marker = reader.BaseStream.ReadByte();
        if (marker == -1) return null;
        if (marker != 0x52 || reader.ReadInt32() != 6) throw new InvalidDataException("Unsupported backend protocol");
        string input = ReadText(reader), output = ReadText(reader);
        int count = reader.ReadInt32(), bytes = 0;
        if (count < 0 || count > 256) throw new InvalidDataException("Backend import count exceeds budget");
        List<IrImport> imports = new(count);
        for (int i = 0; i < count; i++)
        {
            string symbol = ReadText(reader); long decodeBytes = reader.ReadInt64(); int length = reader.ReadInt32();
            if (decodeBytes < 0 || decodeBytes > 64L * 1024 * 1024) throw new InvalidDataException("Invalid imported IR decode budget");
            if (length < 0 || length > 16 * 1024 * 1024 - bytes) throw new InvalidDataException("Backend import bytes exceed budget");
            byte[] body = reader.ReadBytes(length);
            if (body.Length != length) throw new EndOfStreamException("Truncated backend import");
            imports.Add(new(symbol, body, decodeBytes)); bytes += length;
        }
        int retainedCount = reader.ReadInt32();
        if (retainedCount < -1 || retainedCount > 100000) throw new InvalidDataException("Invalid backend retention count");
        HashSet<string>? retained = retainedCount < 0 ? null : new(StringComparer.Ordinal);
        int nameBytes = 0;
        for (int i = 0; i < retainedCount; i++)
        {
            string key = ReadText(reader); nameBytes = checked(nameBytes + Utf8.GetByteCount(key));
            if (nameBytes > 16 * 1024 * 1024 || !retained!.Add(key)) throw new InvalidDataException("Invalid backend retention set");
        }
        LifetimeFacts? facts = null;
        int summaries = reader.ReadInt32();
        if (summaries < -1 || summaries > 1000000) throw new InvalidDataException("Invalid backend lifetime facts");
        if (summaries >= 0)
        {
            facts = new();
            for (int i = 0; i < summaries; i++)
            {
                string name = ReadText(reader); int parameters = reader.ReadInt32();
                if (parameters < 0 || parameters > 65536) throw new InvalidDataException("Invalid backend lifetime facts");
                bool[] escapes = new bool[parameters];
                for (int p = 0; p < parameters; p++) escapes[p] = reader.ReadBoolean();
                if (!facts.Escapes.TryAdd(name, escapes)) throw new InvalidDataException("Duplicate backend lifetime fact");
            }
            int fresh = reader.ReadInt32();
            if (fresh < 0 || fresh > 1000000) throw new InvalidDataException("Invalid backend lifetime facts");
            for (int i = 0; i < fresh; i++) facts.Fresh.Add(ReadText(reader));
            int helpers = reader.ReadInt32();
            if (helpers < 0 || helpers > 64) throw new InvalidDataException("Invalid backend lifetime facts");
            for (int i = 0; i < helpers; i++) facts.Helpers.Add(ReadText(reader));
            int fieldFunctions = reader.ReadInt32();
            if (fieldFunctions < 0 || fieldFunctions > 1000000) throw new InvalidDataException("Invalid backend lifetime facts");
            for (int i = 0; i < fieldFunctions; i++)
            {
                string name = ReadText(reader); int parameters = reader.ReadInt32();
                if (parameters < 0 || parameters > 65536) throw new InvalidDataException("Invalid backend lifetime facts");
                SolvedFields?[] fields = new SolvedFields?[parameters];
                for (int p = 0; p < parameters; p++) fields[p] = ReadFields(reader);
                if (!facts.Fields.TryAdd(name, fields)) throw new InvalidDataException("Duplicate backend lifetime fact");
            }
            int freshFields = reader.ReadInt32();
            if (freshFields < 0 || freshFields > 1000000) throw new InvalidDataException("Invalid backend lifetime facts");
            for (int i = 0; i < freshFields; i++)
            {
                string name = ReadText(reader);
                if (ReadFields(reader) is not SolvedFields fields || !facts.FreshFields.TryAdd(name, fields))
                    throw new InvalidDataException("Invalid backend fresh field fact");
            }
            int catchable = reader.ReadInt32();
            if (catchable < -1 || catchable > 1000000) throw new InvalidDataException("Invalid backend catch fact");
            if (catchable >= 0)
            {
                string[] types = new string[catchable];
                for (int i = 0; i < catchable; i++) types[i] = ReadText(reader);
                facts.ForeignCatchable = types;
            }
            int owned = reader.ReadInt32();
            if (owned < -1 || owned > 1000000) throw new InvalidDataException("Invalid backend owned-field fact");
            if (owned >= 0)
            {
                OwnedFieldFacts fields = new();
                for (int i = 0; i < owned; i++)
                {
                    string field = ReadText(reader); long offset = reader.ReadInt64();
                    if (!fields.Fields.TryAdd(field, offset)) throw new InvalidDataException("Duplicate backend owned-field fact");
                    if (reader.ReadBoolean()) fields.Mapped.Add(field);
                }
                int borrowers = reader.ReadInt32();
                if (borrowers < 0 || borrowers > 1000000) throw new InvalidDataException("Invalid backend owned-field fact");
                for (int i = 0; i < borrowers; i++) fields.Borrowers.Add(ReadText(reader));
                facts.OwnedFields = fields;
            }
        }
        return new(input, output, imports, retained, facts);
    }
    private static void WriteFields(BinaryWriter writer, SolvedFields? fields)
    {
        if (fields is null) { writer.Write(false); return; }
        writer.Write(true); writer.Write(fields.Opaque);
        writer.Write(fields.Dirty.Length); foreach (long offset in fields.Dirty) writer.Write(offset);
        writer.Write(fields.Fresh.Length); foreach (long offset in fields.Fresh) writer.Write(offset);
    }

    private static SolvedFields? ReadFields(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        bool opaque = reader.ReadBoolean();
        long[] Offsets()
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 65536) throw new InvalidDataException("Invalid backend field offsets");
            long[] offsets = new long[count];
            for (int i = 0; i < count; i++) offsets[i] = reader.ReadInt64();
            return offsets;
        }
        long[] dirty = Offsets();
        return new(opaque, dirty, Offsets());
    }

    public static void WriteResponse(BinaryWriter writer, string? error)
    { writer.Write(error is null ? 0 : 1); WriteText(writer, error ?? ""); writer.Flush(); }
    public static void ReadResponse(BinaryReader reader)
    {
        int status = reader.ReadInt32(); string error = ReadText(reader);
        if (status == 1) throw new InvalidDataException("Compiler backend: " + error);
        if (status != 0 || error.Length != 0) throw new InvalidDataException("Invalid backend response");
    }
    private static void WriteText(BinaryWriter writer, string text)
    {
        byte[] bytes = Utf8.GetBytes(text);
        if (bytes.Length > 65536 || text.Contains('\0')) throw new InvalidDataException("Invalid backend text");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 65536) throw new InvalidDataException("Invalid backend text length");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated backend text");
        string text = Utf8.GetString(bytes);
        if (text.Contains('\0')) throw new InvalidDataException("Invalid backend text");
        return text;
    }
}
