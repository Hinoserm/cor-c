using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using Corsac.Lang.Opt;

namespace Corsac.Lang.Metadata;

/// <summary>Unit settings plus independently addressable native-backend IR records.</summary>
public static class IrUnitCodec
{
    /// <summary>
    /// What a unit was compiled with that the late passes read: the link runs
    /// them again over the unit's archived IR, which then holds the IR from
    /// before them (IrUnitCodec.Snapshot, the record's shape 3; a snapshot
    /// taken after them carries no settings, shape 1).
    /// PositionIndependent: the unit is part of a shared object -- a kernel
    /// module's -- and its regenerated code must be too.
    /// Regions: whether its late passes may give objects to regions
    /// (RegionPointsTo), as its compile said -- never where there is no
    /// operating system to keep an arena, nor under --no-regions. Made again
    /// at the link without it, a freestanding unit called the region helpers
    /// its compile had never asked for, and a closed image, keeping only what
    /// the archived IR reaches, had dropped them: "undefined symbol
    /// m_Runtime_AllocRegion...".
    /// Polls: whether its loops poll for the collector at their back edges
    /// (Module.SafepointPolls), as its compile said.
    /// </summary>
    public sealed record Settings(bool PreLate, bool NoCollector, bool CallsCollector, bool LeavesLinkHints,
        bool OptimizeSize, bool ExperimentalBatch, string[] RuntimeHelpers, bool PositionIndependent = false, bool Regions = true, bool Polls = false);

    /// The unit record's two shapes: a snapshot taken after the late passes,
    /// with nothing for the link to run them again by, and one taken before
    /// them with the settings they read.
    const int WithoutSettings = 1;
    const int WithSettings = 3;

    public static void Attach(ObjectFile obj, Module module, bool stackMaps)
        => IrArchive.Attach(obj, Snapshot(module, stackMaps, null));

    /// <summary>
    /// The unit's records as the module stands now. Taken before the late
    /// passes (settings non-null), the link can run them over the whole
    /// program's answers; taken after them (no settings), it cannot.
    /// </summary>
    public static List<IrArchiveRecord> Snapshot(Module module, bool stackMaps, Settings? settings)
    {
        List<IrArchiveRecord> records = new();
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, IrBinary.Utf8, leaveOpen: true))
        {
            writer.Write(settings is null ? WithoutSettings : WithSettings); IrBinary.Text(writer, module.Name); IrBinary.Text(writer, module.Entry);
            writer.Write(module.NeedsHeap); writer.Write(module.PreserveExports); writer.Write(stackMaps);
            writer.Write(module.Imports.Count);
            foreach (string import in module.Imports.Order(StringComparer.Ordinal)) IrBinary.Text(writer, import);
            if (settings is not null)
            {
                writer.Write(settings.NoCollector); writer.Write(settings.CallsCollector); writer.Write(settings.LeavesLinkHints);
                writer.Write(settings.OptimizeSize); writer.Write(settings.ExperimentalBatch);
                writer.Write(settings.RuntimeHelpers.Length);
                foreach (string helper in settings.RuntimeHelpers.Order(StringComparer.Ordinal)) IrBinary.Text(writer, helper);
                writer.Write(settings.PositionIndependent);
                writer.Write(settings.Regions);
                writer.Write(settings.Polls);
            }
        }
        records.Add(new("M:unit", false, 0, Array.Empty<string>(), stream.ToArray()));
        HashSet<string> locals = module.Functions.Where(function => !function.Exported).Select(function => function.Name)
            .Concat(module.Data.Where(item => !item.Exported).Select(item => item.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (Function function in module.Functions)
        {
            Instr[] instructions = function.Blocks.SelectMany(block => block.Instrs).ToArray();
            string[] calls = instructions.Where(instruction => instruction.Op == Opcode.Call && instruction.Callee is not null)
                .Select(instruction => instruction.Callee!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            // Import only bodies that do not need object-local dependencies.
            // Their original native owner remains present for calls not inlined.
            bool importable = function.Exported && function.Name != module.Entry && instructions.Length <= 160
                // A body whose address is taken is imported as readily: the
                // copy is the importer's, and the owner's stays in its vtable.
                && Inline.Inlineable(function, NoneKept)
                && !calls.Any(locals.Contains)
                && !instructions.SelectMany(instruction => instruction.Operands).OfType<SymOperand>().Any(address => locals.Contains(address.Name));
            string[] references = calls.Concat(instructions.SelectMany(instruction => instruction.Operands).OfType<SymOperand>()
                .Select(address => address.Name)).Distinct(StringComparer.Ordinal)
                .Select(name => ComparedOnly(instructions, name) ? Lto.IrArchiveEntry.ComparedOnly + name : name)
                .Order(StringComparer.Ordinal).ToArray();
            byte[] body = IrFunctionCodec.Write(function, module.KeepCalls);
            // Held packed until the object is written (IrArchiveRecord.Packed).
            records.Add(IrArchiveRecord.Packed("F:" + function.Name, importable, instructions.Length, calls, body, references, IrFunctionCodec.DecodeCost(function, body.Length)));
        }
        foreach (DataItem item in module.Data)
            records.Add(new("D:" + item.Name, false, 0, Array.Empty<string>(), IrDataCodec.Write(item),
                item.Relocs.Select(relocation => relocation.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        // Other units' descriptors as this one knew them (Module.ShadowData):
        // the late passes the link runs again devirtualize as the compile did.
        foreach (DataItem item in module.ShadowData.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
            // Its slots are calls the unit may come to make: the link gives
            // the unit their answers (LifetimeSolver.For).
            records.Add(new("S:" + item.Name, false, 0,
                item.Relocs.Select(relocation => relocation.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                IrDataCodec.Write(item)));
        return records;
    }

    private static readonly HashSet<string> NoneKept = new(StringComparer.Ordinal);

    /// <summary>
    /// WHETHER A FUNCTION NAMES A DESCRIPTOR ONLY TO COMPARE WITH IT
    /// (IrArchiveEntry.ComparedOnly): every use of it an operand of an integer
    /// comparison, or copied into a register only such comparisons read --
    /// `GetType() == typeof(X)` in First, asking whether its source is an
    /// ordering. Such a function makes no X; counted as making one, every
    /// virtual call X's methods implement had them as targets, in every
    /// program that asked.
    /// </summary>
    private static bool ComparedOnly(Instr[] instructions, string name)
    {
        if (!(name.Length > 2 && name[1] == '_' && name[0] is 't' or 'q' or 'v' or 'b')) return false;
        HashSet<VReg>? held = null;
        bool any = false;
        foreach (Instr i in instructions)
            foreach (Operand o in i.Operands)
            {
                if (o is not SymOperand s || s.Name != name) continue;
                any = true;
                if (IrInfo.IsIntCompare(i.Op)) continue;
                if (i.Op == Opcode.Copy && i.Operands.Count == 1 && i.Dest is not null) { (held ??= new()).Add(i.Dest); continue; }
                return false;
            }
        if (!any) return false;
        if (held is null) return true;
        foreach (Instr i in instructions)
        {
            if (i.Dest is not null && held.Contains(i.Dest) && !(i.Op == Opcode.Copy && i.Operands.Count == 1 && i.Operands[0] is SymOperand { Name: var n } && n == name)) return false;
            if (IrInfo.IsIntCompare(i.Op)) continue;
            foreach (Operand o in i.Operands)
                if (o is RegOperand r && held.Contains(r.Reg)) return false;
        }
        return true;
    }

    /// <summary>A unit's settings record alone, without its functions: what a link reads of every unit before it plans.</summary>
    public static (Settings? Settings, bool StackMaps) ReadSettings(IrArchive archive)
    {
        using MemoryStream stream = new(archive.ReadBody("M:unit"), writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        IrReadBudget budget = new(1024 * 1024);
        int shape = reader.ReadInt32();
        if (shape is not (WithoutSettings or WithSettings)) throw new InvalidDataException("Unknown IR unit record");
        IrBinary.Name(reader, budget); IrBinary.Text(reader, budget);
        IrBinary.Flag(reader); IrBinary.Flag(reader);
        bool stackMaps = IrBinary.Flag(reader);
        int imports = IrBinary.Count(reader);
        for (int i = 0; i < imports; i++) IrBinary.Name(reader, budget);
        return (shape == WithSettings ? ReadSettingsTail(reader, budget) : null, stackMaps);
    }

    private static Settings ReadSettingsTail(BinaryReader reader, IrReadBudget budget)
    {
        bool noCollector = IrBinary.Flag(reader), callsCollector = IrBinary.Flag(reader), hints = IrBinary.Flag(reader);
        bool size = IrBinary.Flag(reader), batch = IrBinary.Flag(reader);
        int count = IrBinary.Count(reader);
        budget.Charge(count, 64, "runtime helpers");
        string[] helpers = new string[count];
        for (int i = 0; i < count; i++) helpers[i] = IrBinary.Name(reader, budget);
        bool pic = IrBinary.Flag(reader);
        bool regions = IrBinary.Flag(reader);
        bool polls = IrBinary.Flag(reader);
        return new(true, noCollector, callsCollector, hints, size, batch, helpers, pic, regions, polls);
    }

    public static (Module Module, bool StackMaps, long AccountedBytes) Read(IrArchive archive, long memoryBudget = 64L * 1024 * 1024,
        IReadOnlySet<string>? retained = null, IReadOnlyDictionary<string, bool>? functionHeaders = null)
    {
        var (module, stackMaps, used, _) = ReadWithSettings(archive, memoryBudget, retained, functionHeaders);
        return (module, stackMaps, used);
    }

    /// <summary>The unit and, for a snapshot taken before the late passes, what it was compiled with (applied to the module too).</summary>
    public static (Module Module, bool StackMaps, long AccountedBytes, Settings? Settings) ReadWithSettings(IrArchive archive,
        long memoryBudget = 64L * 1024 * 1024, IReadOnlySet<string>? retained = null, IReadOnlyDictionary<string, bool>? functionHeaders = null)
    {
        IrReadBudget budget = new(memoryBudget);
        using MemoryStream stream = new(archive.ReadBody("M:unit"), writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        try
        {
            int shape = reader.ReadInt32();
            if (shape is not (WithoutSettings or WithSettings)) throw new InvalidDataException("Unknown IR unit record");
            budget.Charge(1024, 1, "unit settings");
            Module module = new(IrBinary.Name(reader, budget))
            { Entry = IrBinary.Text(reader, budget), NeedsHeap = IrBinary.Flag(reader), PreserveExports = IrBinary.Flag(reader) };
            bool stackMaps = IrBinary.Flag(reader);
            int imports = IrBinary.Count(reader);
            budget.Charge(imports, 64, "import table");
            for (int i = 0; i < imports; i++)
                if (!module.Imports.Add(IrBinary.Name(reader, budget))) throw new InvalidDataException("Duplicate IR import");
            Settings? settings = shape == WithSettings ? ReadSettingsTail(reader, budget) : null;
            if (settings is not null)
            {
                module.NoCollector = settings.NoCollector; module.CallsCollector = settings.CallsCollector;
                module.LeavesLinkHints = settings.LeavesLinkHints; module.RuntimeHelpers.UnionWith(settings.RuntimeHelpers);
                module.SafepointPolls = settings.Polls;
            }
            IrBinary.End(reader);
            // EACH RECORD'S BYTES LET GO once it is decoded (IrReadBudget.Release):
            // the module holds what was made of them, never them.
            foreach (IrArchiveEntry entry in archive.Entries.Values)
            {
                if (entry.Key == "M:unit") continue;
                if (entry.Key.StartsWith("S:", StringComparison.Ordinal))
                {
                    byte[] body = archive.ReadBody(entry.Key);
                    DataItem shadow = IrDataCodec.Read(body, memoryBudget, budget);
                    budget.Release(body.Length);
                    if (entry.Key != "S:" + shadow.Name) throw new InvalidDataException("IR shadow key mismatch");
                    module.ShadowData[shadow.Name] = shadow;
                    continue;
                }
                if (retained is not null && !retained.Contains(entry.Key)) continue;
                if (entry.Key.StartsWith("F:", StringComparison.Ordinal))
                {
                    if (functionHeaders is not null)
                    {
                        string name = entry.Key[2..];
                        if (!functionHeaders.TryGetValue(name, out bool exported) || entry.DecodeBytes < entry.Length)
                            throw new InvalidDataException("Invalid deferred IR function header");
                        budget.Charge(512L + 2L * name.Length, 1, "function header");
                        module.Functions.Add(new Function(name, IrType.Void) { Exported = exported });
                        continue;
                    }
                    byte[] body = archive.ReadBody(entry.Key);
                    Function function = IrFunctionCodec.Read(body, budget, module.KeepCalls);
                    budget.Release(body.Length);
                    if (entry.Key != "F:" + function.Name) throw new InvalidDataException("IR function key mismatch");
                    module.Functions.Add(function);
                }
                else if (entry.Key.StartsWith("D:", StringComparison.Ordinal))
                {
                    byte[] body = archive.ReadBody(entry.Key);
                    DataItem item = IrDataCodec.Read(body, memoryBudget, budget);
                    budget.Release(body.Length);
                    if (entry.Key != "D:" + item.Name) throw new InvalidDataException("IR data key mismatch");
                    module.Data.Add(item);
                }
                else throw new InvalidDataException("Unknown required IR record " + entry.Key);
            }
            return (module, stackMaps, budget.Used, settings);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated IR unit"); }
        catch (System.Text.DecoderFallbackException) { throw new InvalidDataException("Invalid IR UTF-8"); }
    }
}
