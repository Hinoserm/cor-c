using System.Text;

namespace Corsac.Lang.Lto;

/// <summary>Bounded framed requests over one persistent compiler-backend process.</summary>
public static class BackendProtocol
{
    /// <summary>The most bodies one request imports: the link plans no more (IrLinkOptimizer), and a request with more is refused.</summary>
    public const int MostImports = 4096;

    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void WriteRequest(BinaryWriter writer, BackendRequest request)
    {
        writer.Write((byte)0x52); writer.Write(0);
        WriteText(writer, request.Input); WriteText(writer, request.Output); writer.Write(request.Imports.Count);
        foreach (IrImport import in request.Imports)
        {
            WriteText(writer, import.Symbol); writer.Write(import.DecodeBytes); writer.Write(import.Body.Length); writer.Write(import.Body);
            // Its region sites by ordinal, and whether it is a boundary.
            writer.Write(import.RegionSites?.Length ?? 0);
            foreach (int ordinal in import.RegionSites ?? Array.Empty<int>()) writer.Write(ordinal);
            writer.Write(import.RegionBoundary);
            writer.Write(import.ReadOnly);
        }
        writer.Write(request.Retained?.Count ?? -1);
        if (request.Retained is not null)
            foreach (IrKey key in request.Retained.Order()) WriteText(writer, key.ToString());
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
            writer.Write(facts.Held.Count);
            foreach ((string name, long[]?[] held) in facts.Held.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteText(writer, name); writer.Write(held.Length);
                foreach (long[]? at in held)
                {
                    writer.Write(at?.Length ?? -1);
                    foreach (long offset in at ?? Array.Empty<long>()) writer.Write(offset);
                }
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
            // The owned fields, by name: offset, and whether a store fills it
            // (1) and whether a collection frees what it replaces itself (2);
            // then the functions and virtual symbols that hand one back.
            OwnedFieldFacts? owned = facts.OwnedFields;
            writer.Write(owned?.Fields.Count ?? -1);
            if (owned is not null)
            {
                foreach ((string field, long offset) in owned.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { WriteText(writer, field); writer.Write(offset); writer.Write((byte)((owned.Mapped.Contains(field) ? 1 : 0) | (owned.SelfFreed.Contains(field) ? 2 : 0) | (owned.ArrayElements.Contains(field) ? 4 : 0))); }
                writer.Write(owned.Borrowers.Count);
                foreach (string name in owned.Borrowers.Order(StringComparer.Ordinal)) WriteText(writer, name);
                writer.Write(owned.Resolved.Count);
                foreach (string name in owned.Resolved.Order(StringComparer.Ordinal)) WriteText(writer, name);
                // The fields elements are owned through, their callees, and the fields kept for it.
                writer.Write(owned.Elements.Count);
                foreach ((string field, string kind) in owned.Elements.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { WriteText(writer, field); WriteText(writer, kind); }
                writer.Write(owned.ElementCallees.Count);
                foreach (((string callee, int argument), string field) in owned.ElementCallees.OrderBy(pair => pair.Key.Callee, StringComparer.Ordinal).ThenBy(pair => pair.Key.Argument))
                { WriteText(writer, callee); writer.Write(argument); WriteText(writer, field); }
                writer.Write(owned.ElementKept.Count);
                foreach (string field in owned.ElementKept.Order(StringComparer.Ordinal)) WriteText(writer, field);
                // The accessors and the fields put back of those proved through their accessors.
                writer.Write(owned.ElementAccessors.Count);
                foreach (string name in owned.ElementAccessors.Order(StringComparer.Ordinal)) WriteText(writer, name);
                writer.Write(owned.ElementFixed.Count);
                foreach (string field in owned.ElementFixed.Order(StringComparer.Ordinal)) WriteText(writer, field);
                // Whether storage is made beside its owner (OwnedFieldFacts.Beside).
                writer.Write(owned.Beside);
            }
            // The regions: boundaries by name, then sites by function and
            // ordinal, then loops by function and header, then the bytes the
            // boundaries and loops proved sized hold.
            RegionFacts? regions = facts.Regions;
            writer.Write(regions?.Boundaries.Count ?? -1);
            if (regions is not null)
            {
                foreach (string name in regions.Boundaries) WriteText(writer, name);
                writer.Write(regions.Sites.Count);
                foreach ((string function, int ordinal) in regions.Sites) { WriteText(writer, function); writer.Write(ordinal); }
                writer.Write(regions.Loops.Count);
                foreach ((string function, int header) in regions.Loops) { WriteText(writer, function); writer.Write(header); }
                writer.Write(regions.BoundaryBytes.Count);
                foreach ((string function, long bytes) in regions.BoundaryBytes) { WriteText(writer, function); writer.Write(bytes); }
                writer.Write(regions.LoopBytes.Count);
                foreach (((string function, int header), long bytes) in regions.LoopBytes) { WriteText(writer, function); writer.Write(header); writer.Write(bytes); }
            }
            // Whether the image needs a collector: 2 undecided, 1 yes, 0 no.
            writer.Write(facts.Heap switch { null => (byte)2, true => (byte)1, false => (byte)0 });
            writer.Write(facts.CompletedReady?.Count ?? -1);
            foreach (string ready in facts.CompletedReady ?? Enumerable.Empty<string>()) WriteText(writer, ready);
            // The arguments the whole program found no arguments (ArgumentFacts):
            // by function, each parameter gone and the number in its place (or
            // none: never read); then by slot, the arguments its calls pass no more.
            ArgumentFacts? arguments = facts.Arguments;
            writer.Write(arguments?.Dropped.Count ?? -1);
            if (arguments is not null)
            {
                foreach ((string name, ArgumentDrop[] drops) in arguments.Dropped)
                {
                    WriteText(writer, name); writer.Write(drops.Length);
                    foreach (ArgumentDrop drop in drops)
                    { writer.Write(drop.Parameter); writer.Write(drop.Value.Kind == ArgumentValueKind.Constant); writer.Write(drop.Value.Number); }
                }
                writer.Write(arguments.DroppedSlots.Count);
                foreach ((string slot, int[] gone) in arguments.DroppedSlots)
                { WriteText(writer, slot); writer.Write(gone.Length); foreach (int k in gone) writer.Write(k); }
                // The versions: by function, each one's name and what it has inside it.
                writer.Write(arguments.Versions.Count);
                foreach ((string name, ArgumentVersion[] versions) in arguments.Versions)
                {
                    WriteText(writer, name); writer.Write(versions.Length);
                    foreach (ArgumentVersion version in versions)
                    {
                        WriteText(writer, version.Name); writer.Write(version.Bound.Length);
                        foreach (ArgumentDrop one in version.Bound) { writer.Write(one.Parameter); writer.Write(one.Value.Number); }
                    }
                }
                // The ways in: by function, each parameter set and its number.
                writer.Write(arguments.Entries.Count);
                foreach ((string name, ArgumentDrop[] given) in arguments.Entries)
                {
                    WriteText(writer, name); writer.Write(given.Length);
                    foreach (ArgumentDrop one in given) { writer.Write(one.Parameter); writer.Write(one.Value.Number); }
                }
            }
        }
        writer.Flush();
    }
    public static BackendRequest? ReadRequest(BinaryReader reader)
    {
        int marker = reader.BaseStream.ReadByte();
        if (marker == -1) return null;
        if (marker != 0x52 || reader.ReadInt32() != 0) throw new InvalidDataException("Unsupported backend protocol");
        string input = ReadText(reader), output = ReadText(reader);
        int count = reader.ReadInt32(), bytes = 0;
        if (count < 0 || count > MostImports) throw new InvalidDataException("Backend import count exceeds budget");
        List<IrImport> imports = new(count);
        for (int i = 0; i < count; i++)
        {
            string symbol = ReadText(reader); long decodeBytes = reader.ReadInt64(); int length = reader.ReadInt32();
            if (decodeBytes < 0 || decodeBytes > 64L * 1024 * 1024) throw new InvalidDataException("Invalid imported IR decode budget");
            if (length < 0 || length > 16 * 1024 * 1024 - bytes) throw new InvalidDataException("Backend import bytes exceed budget");
            byte[] body = reader.ReadBytes(length);
            if (body.Length != length) throw new EndOfStreamException("Truncated backend import");
            int sites = reader.ReadInt32();
            if (sites < 0 || sites > 1000000) throw new InvalidDataException("Invalid imported region sites");
            int[] ordinals = new int[sites];
            for (int s = 0; s < sites; s++) ordinals[s] = reader.ReadInt32();
            bool boundary = reader.ReadBoolean();
            bool readOnly = reader.ReadBoolean();
            imports.Add(new(symbol, body, decodeBytes, sites == 0 ? null : ordinals, boundary, readOnly)); bytes += length;
        }
        int retainedCount = reader.ReadInt32();
        if (retainedCount < -1 || retainedCount > 100000) throw new InvalidDataException("Invalid backend retention count");
        HashSet<IrKey>? retained = retainedCount < 0 ? null : new();
        int nameBytes = 0;
        for (int i = 0; i < retainedCount; i++)
        {
            string key = ReadText(reader); nameBytes = checked(nameBytes + Utf8.GetByteCount(key));
            if (nameBytes > 16 * 1024 * 1024 || key.Length < 3 || key[1] != ':' || !retained!.Add(IrKey.Parse(key))) throw new InvalidDataException("Invalid backend retention set");
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
            int heldFunctions = reader.ReadInt32();
            if (heldFunctions < 0 || heldFunctions > 1000000) throw new InvalidDataException("Invalid backend lifetime facts");
            for (int i = 0; i < heldFunctions; i++)
            {
                string name = ReadText(reader); int parameters = reader.ReadInt32();
                if (parameters < 0 || parameters > 65536) throw new InvalidDataException("Invalid backend lifetime facts");
                long[]?[] held = new long[]?[parameters];
                for (int p = 0; p < parameters; p++)
                {
                    int offsets = reader.ReadInt32();
                    if (offsets < -1 || offsets > 4096) throw new InvalidDataException("Invalid backend lifetime facts");
                    if (offsets < 0) continue;
                    held[p] = new long[offsets];
                    for (int k = 0; k < offsets; k++) held[p]![k] = reader.ReadInt64();
                }
                if (!facts.Held.TryAdd(name, held)) throw new InvalidDataException("Duplicate backend lifetime fact");
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
                    byte flags = reader.ReadByte();
                    if (flags > 7) throw new InvalidDataException("Invalid backend owned-field fact");
                    if ((flags & 1) != 0) fields.Mapped.Add(field);
                    if ((flags & 2) != 0) fields.SelfFreed.Add(field);
                    if ((flags & 4) != 0) fields.ArrayElements.Add(field);
                }
                int borrowers = reader.ReadInt32();
                if (borrowers < 0 || borrowers > 1000000) throw new InvalidDataException("Invalid backend owned-field fact");
                for (int i = 0; i < borrowers; i++) fields.Borrowers.Add(ReadText(reader));
                int resolved = reader.ReadInt32();
                if (resolved < 0 || resolved > 1000000) throw new InvalidDataException("Invalid backend owned-field fact");
                for (int i = 0; i < resolved; i++) fields.Resolved.Add(ReadText(reader));
                int elements = reader.ReadInt32();
                if (elements < 0 || elements > 1000000) throw new InvalidDataException("Invalid backend owned-elements fact");
                for (int i = 0; i < elements; i++)
                    if (!fields.Elements.TryAdd(ReadText(reader), ReadText(reader))) throw new InvalidDataException("Duplicate backend owned-elements fact");
                int callees = reader.ReadInt32();
                if (callees < 0 || callees > 1000000) throw new InvalidDataException("Invalid backend owned-elements fact");
                for (int i = 0; i < callees; i++)
                {
                    string callee = ReadText(reader); int argument = reader.ReadInt32();
                    if (argument < 0 || !fields.ElementCallees.TryAdd((callee, argument), ReadText(reader))) throw new InvalidDataException("Invalid backend owned-elements fact");
                }
                int kept = reader.ReadInt32();
                if (kept < 0 || kept > 1000000) throw new InvalidDataException("Invalid backend owned-elements fact");
                for (int i = 0; i < kept; i++) fields.ElementKept.Add(ReadText(reader));
                int accessors = reader.ReadInt32();
                if (accessors < 0 || accessors > 1000000) throw new InvalidDataException("Invalid backend owned-elements fact");
                for (int i = 0; i < accessors; i++) fields.ElementAccessors.Add(ReadText(reader));
                int fixedFields = reader.ReadInt32();
                if (fixedFields < 0 || fixedFields > 1000000) throw new InvalidDataException("Invalid backend owned-elements fact");
                for (int i = 0; i < fixedFields; i++) fields.ElementFixed.Add(ReadText(reader));
                fields.Beside = reader.ReadBoolean();
                facts.OwnedFields = fields;
            }
            int boundaries = reader.ReadInt32();
            if (boundaries < -1 || boundaries > 1000000) throw new InvalidDataException("Invalid backend region fact");
            if (boundaries >= 0)
            {
                RegionFacts regions = new();
                for (int i = 0; i < boundaries; i++)
                    if (!regions.Boundaries.Add(ReadText(reader))) throw new InvalidDataException("Duplicate backend region boundary");
                int sites = reader.ReadInt32();
                if (sites < 0 || sites > 10000000) throw new InvalidDataException("Invalid backend region fact");
                for (int i = 0; i < sites; i++)
                {
                    string function = ReadText(reader); int ordinal = reader.ReadInt32();
                    if (ordinal < 0 || !regions.Sites.Add((function, ordinal))) throw new InvalidDataException("Invalid backend region site");
                }
                int loops = reader.ReadInt32();
                if (loops < 0 || loops > 10000000) throw new InvalidDataException("Invalid backend region fact");
                for (int i = 0; i < loops; i++)
                {
                    string function = ReadText(reader); int header = reader.ReadInt32();
                    if (header < 0 || !regions.Loops.Add((function, header))) throw new InvalidDataException("Invalid backend region loop");
                }
                int sized = reader.ReadInt32();
                if (sized < 0 || sized > boundaries) throw new InvalidDataException("Invalid backend region fact");
                for (int i = 0; i < sized; i++)
                {
                    string function = ReadText(reader); long regionBytes = reader.ReadInt64();
                    if (regionBytes <= 0 || !regions.Boundaries.Contains(function) || !regions.BoundaryBytes.TryAdd(function, regionBytes)) throw new InvalidDataException("Invalid backend region size");
                }
                sized = reader.ReadInt32();
                if (sized < 0 || sized > loops) throw new InvalidDataException("Invalid backend region fact");
                for (int i = 0; i < sized; i++)
                {
                    string function = ReadText(reader); int header = reader.ReadInt32(); long regionBytes = reader.ReadInt64();
                    if (regionBytes <= 0 || !regions.Loops.Contains((function, header)) || !regions.LoopBytes.TryAdd((function, header), regionBytes)) throw new InvalidDataException("Invalid backend region size");
                }
                facts.Regions = regions;
            }
            facts.Heap = reader.ReadByte() switch
            {
                2 => null, 1 => true, 0 => false,
                _ => throw new InvalidDataException("Invalid backend heap fact"),
            };
            int completed = reader.ReadInt32();
            if (completed >= 0)
            {
                facts.CompletedReady = new(StringComparer.Ordinal);
                for (int k = 0; k < completed; k++) facts.CompletedReady.Add(ReadText(reader));
            }
            int dropped = reader.ReadInt32();
            if (dropped < -1 || dropped > 4000000) throw new InvalidDataException("Invalid backend argument fact");
            if (dropped >= 0)
            {
                ArgumentFacts arguments = new();
                for (int i = 0; i < dropped; i++)
                {
                    string name = ReadText(reader); int entries = reader.ReadInt32();
                    if (entries < 1 || entries > 4096) throw new InvalidDataException("Invalid backend argument fact");
                    ArgumentDrop[] drops = new ArgumentDrop[entries];
                    for (int k = 0; k < entries; k++)
                    {
                        int parameter = reader.ReadInt32(); bool constant = reader.ReadBoolean(); long number = reader.ReadInt64();
                        if (parameter < 0 || k > 0 && parameter <= drops[k - 1].Parameter) throw new InvalidDataException("Invalid backend argument fact");
                        drops[k] = new(parameter, constant ? ArgumentValue.Of(number) : default);
                    }
                    if (!arguments.Dropped.TryAdd(name, drops)) throw new InvalidDataException("Duplicate backend argument fact");
                }
                int slots = reader.ReadInt32();
                if (slots < 0 || slots > 4000000) throw new InvalidDataException("Invalid backend argument fact");
                for (int i = 0; i < slots; i++)
                {
                    string slot = ReadText(reader); int entries = reader.ReadInt32();
                    if (entries < 1 || entries > 4096) throw new InvalidDataException("Invalid backend argument fact");
                    int[] gone = new int[entries];
                    for (int k = 0; k < entries; k++)
                    {
                        gone[k] = reader.ReadInt32();
                        if (gone[k] < 1 || k > 0 && gone[k] <= gone[k - 1]) throw new InvalidDataException("Invalid backend argument fact");
                    }
                    if (!arguments.DroppedSlots.TryAdd(slot, gone)) throw new InvalidDataException("Duplicate backend argument fact");
                }
                int versioned = reader.ReadInt32();
                if (versioned < 0 || versioned > 4000000) throw new InvalidDataException("Invalid backend argument fact");
                for (int i = 0; i < versioned; i++)
                {
                    string name = ReadText(reader); int entries = reader.ReadInt32();
                    if (entries < 1 || entries > 16) throw new InvalidDataException("Invalid backend argument fact");
                    ArgumentVersion[] versions = new ArgumentVersion[entries];
                    for (int v = 0; v < entries; v++)
                    {
                        string version = ReadText(reader); int bound = reader.ReadInt32();
                        if (bound < 1 || bound > 30) throw new InvalidDataException("Invalid backend argument fact");
                        ArgumentDrop[] inside = new ArgumentDrop[bound];
                        for (int k = 0; k < bound; k++)
                        {
                            int parameter = reader.ReadInt32(); long number = reader.ReadInt64();
                            if (parameter < 0 || k > 0 && parameter <= inside[k - 1].Parameter) throw new InvalidDataException("Invalid backend argument fact");
                            inside[k] = new(parameter, ArgumentValue.Of(number));
                        }
                        versions[v] = new(version, inside);
                    }
                    if (!arguments.Versions.TryAdd(name, versions)) throw new InvalidDataException("Duplicate backend argument fact");
                }
                int ways = reader.ReadInt32();
                if (ways < 0 || ways > 4000000) throw new InvalidDataException("Invalid backend argument fact");
                for (int i = 0; i < ways; i++)
                {
                    string name = ReadText(reader); int entries = reader.ReadInt32();
                    if (entries < 1 || entries > 3) throw new InvalidDataException("Invalid backend argument fact");
                    ArgumentDrop[] given = new ArgumentDrop[entries];
                    for (int k = 0; k < entries; k++)
                    {
                        int parameter = reader.ReadInt32(); long number = reader.ReadInt64();
                        if (parameter < 0 || parameter > 30 || k > 0 && parameter <= given[k - 1].Parameter) throw new InvalidDataException("Invalid backend argument fact");
                        given[k] = new(parameter, ArgumentValue.Of(number));
                    }
                    if (!arguments.Entries.TryAdd(name, given)) throw new InvalidDataException("Duplicate backend argument fact");
                }
                facts.Arguments = arguments;
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
