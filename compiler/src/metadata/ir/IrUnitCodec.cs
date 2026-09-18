using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using Corsac.Lang.Opt;

namespace Corsac.Lang.Metadata;

/// <summary>Unit settings plus independently addressable native-backend IR records.</summary>
public static class IrUnitCodec
{
    public static void Attach(ObjectFile obj, Module module, bool stackMaps)
    {
        List<IrArchiveRecord> records = new();
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, IrBinary.Utf8, leaveOpen: true))
        {
            writer.Write(1); IrBinary.Text(writer, module.Name); IrBinary.Text(writer, module.Entry);
            writer.Write(module.NeedsHeap); writer.Write(module.PreserveExports); writer.Write(stackMaps);
            writer.Write(module.Imports.Count);
            foreach (string import in module.Imports.Order(StringComparer.Ordinal)) IrBinary.Text(writer, import);
        }
        records.Add(new("M:unit", false, 0, Array.Empty<string>(), stream.ToArray()));
        HashSet<string> locals = module.Functions.Where(function => !function.Exported).Select(function => function.Name)
            .Concat(module.Data.Where(item => !item.Exported).Select(item => item.Name)).ToHashSet(StringComparer.Ordinal);
        HashSet<string> addressed = Inline.AddressTaken(module);
        foreach (Function function in module.Functions)
        {
            Instr[] instructions = function.Blocks.SelectMany(block => block.Instrs).ToArray();
            string[] calls = instructions.Where(instruction => instruction.Op == Opcode.Call && instruction.Callee is not null)
                .Select(instruction => instruction.Callee!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            // Import only bodies that do not need object-local dependencies.
            // Their original native owner remains present for calls not inlined.
            bool importable = function.Exported && function.Name != module.Entry && instructions.Length <= 160
                && Inline.Inlineable(function, addressed)
                && !calls.Any(locals.Contains)
                && !instructions.SelectMany(instruction => instruction.Operands).OfType<SymOperand>().Any(address => locals.Contains(address.Name));
            string[] references = calls.Concat(instructions.SelectMany(instruction => instruction.Operands).OfType<SymOperand>()
                .Select(address => address.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            records.Add(new("F:" + function.Name, importable, instructions.Length, calls, IrFunctionCodec.Write(function), references));
        }
        foreach (DataItem item in module.Data)
            records.Add(new("D:" + item.Name, false, 0, Array.Empty<string>(), IrDataCodec.Write(item),
                item.Relocs.Select(relocation => relocation.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        IrArchive.Attach(obj, records);
    }

    public static (Module Module, bool StackMaps, long AccountedBytes) Read(IrArchive archive, long memoryBudget = 64L * 1024 * 1024,
        IReadOnlySet<string>? retained = null)
    {
        IrReadBudget budget = new(memoryBudget);
        using MemoryStream stream = new(archive.ReadBody("M:unit"), writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        try
        {
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported IR unit version");
            budget.Charge(1024, 1, "unit settings");
            Module module = new(IrBinary.Name(reader, budget))
            { Entry = IrBinary.Text(reader, budget), NeedsHeap = IrBinary.Flag(reader), PreserveExports = IrBinary.Flag(reader) };
            bool stackMaps = IrBinary.Flag(reader);
            int imports = IrBinary.Count(reader);
            budget.Charge(imports, 64, "import table");
            for (int i = 0; i < imports; i++)
                if (!module.Imports.Add(IrBinary.Name(reader, budget))) throw new InvalidDataException("Duplicate IR import");
            IrBinary.End(reader);
            foreach (IrArchiveEntry entry in archive.Entries.Values)
            {
                if (entry.Key == "M:unit") continue;
                if (retained is not null && !retained.Contains(entry.Key)) continue;
                if (entry.Key.StartsWith("F:", StringComparison.Ordinal))
                {
                    Function function = IrFunctionCodec.Read(archive.ReadBody(entry.Key), budget);
                    if (entry.Key != "F:" + function.Name) throw new InvalidDataException("IR function key mismatch");
                    module.Functions.Add(function);
                }
                else if (entry.Key.StartsWith("D:", StringComparison.Ordinal))
                {
                    DataItem item = IrDataCodec.Read(archive.ReadBody(entry.Key), memoryBudget, budget);
                    if (entry.Key != "D:" + item.Name) throw new InvalidDataException("IR data key mismatch");
                    module.Data.Add(item);
                }
                else throw new InvalidDataException("Unknown required IR record " + entry.Key);
            }
            return (module, stackMaps, budget.Used);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated IR unit"); }
        catch (System.Text.DecoderFallbackException) { throw new InvalidDataException("Invalid IR UTF-8"); }
    }
}
