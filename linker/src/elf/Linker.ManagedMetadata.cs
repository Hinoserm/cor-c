using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

public static partial class Linker
{
    private static void AddManagedMetadata(List<Input> inputs, Layout layout, List<string> errors)
    {
        List<(Input Input, Symbol? Frames, Symbol? StackMaps)> units = new();
        foreach (Input input in inputs)
        {
            Symbol? frames = input.Object.Symbols.FirstOrDefault(symbol => symbol.Name == ManagedDirectory.FrameSymbol && symbol.IsDefined);
            Symbol? maps = input.Object.Symbols.FirstOrDefault(symbol => symbol.Name == ManagedDirectory.StackMapSymbol && symbol.IsDefined);
            if (frames is not null || maps is not null) units.Add((input, frames, maps));
        }
        bool required = units.Count != 0 || inputs.Any(input => input.Object.Sections.SelectMany(section => section.Relocs)
            .Any(relocation => relocation.Symbol == ManagedDirectory.Symbol));
        if (!required) return;
        if (inputs.Any(input => input.Object.Symbols.Any(symbol => symbol.Name == ManagedDirectory.Symbol && symbol.IsDefined)))
        { errors.Add("'" + ManagedDirectory.Symbol + "' is reserved for the image metadata directory"); return; }
        ObjectFile directory = new();
        Section data = new(".data.rel.ro.corsac.units", SectionKind.Data) { Align = 4 };
        byte[] bytes = new byte[checked(ManagedDirectory.HeaderBytes + units.Count * ManagedDirectory.RecordBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ManagedDirectory.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)units.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), ManagedDirectory.RecordBytes);
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            void Table(Symbol? symbol, int member, string suffix, int minimum)
            {
                if (symbol is null) return;
                if (symbol.Offset < 0 || symbol.Size < minimum || symbol.Offset > symbol.Section!.Bytes.Count
                    || symbol.Size > symbol.Section.Bytes.Count - symbol.Offset)
                { errors.Add(unit.Input.Name + ": invalid managed table " + symbol.Name); return; }
                string alias = "__corsac_unit_" + i + "_" + suffix;
                if (inputs.Any(input => input.Object.Symbols.Any(existing => existing.Name == alias)))
                { errors.Add("Reserved metadata alias collision: " + alias); return; }
                int offset = ManagedDirectory.HeaderBytes + i * ManagedDirectory.RecordBytes + member;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), checked((uint)symbol.Size));
                data.Relocs.Add(new(offset, alias, 0, RelocKind.Abs32));
                layout.MetadataBindings.Add((unit.Input, alias, symbol));
                layout.NotExported.Add(alias);
            }
            Table(unit.Frames, 0, "frames", 20);
            Table(unit.StackMaps, 8, "stackmaps", 16);
        }
        data.Bytes.AddRange(bytes); directory.Sections.Add(data);
        directory.Symbols.Add(new Symbol { Name = ManagedDirectory.Symbol, Section = data, Size = bytes.Length });
        layout.NotExported.Add(ManagedDirectory.Symbol);
        inputs.Add(new Input("image metadata directory", directory));
    }

    private static void ResolveManagedMetadata(Layout layout, List<string> errors)
    {
        foreach (var binding in layout.MetadataBindings)
        {
            Placed? part = binding.Input.Placed.FirstOrDefault(placed => ReferenceEquals(placed.Section, binding.Symbol.Section));
            if (part is null) { errors.Add("Missing placement for managed table " + binding.Symbol.Name); continue; }
            Definition definition = new(binding.Input.Name, part.Output, checked(part.Offset + (uint)binding.Symbol.Offset), binding.Symbol);
            if (!layout.Globals.TryAdd(binding.Alias, definition)) errors.Add("Reserved metadata alias collision: " + binding.Alias);
            else layout.GlobalOrder.Add(binding.Alias);
        }
    }
}
