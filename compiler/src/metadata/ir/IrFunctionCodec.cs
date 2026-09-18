using Corsac.Lang.Ir;
using Corsac.Lang.Opt;
using IrBlock = Corsac.Lang.Ir.Block;

namespace Corsac.Lang.Metadata;

/// <summary>Complete post-async IR function encoding, independently addressable in an object.</summary>
public static class IrFunctionCodec
{
    public static long DecodeCost(Function function, int payloadBytes)
    {
        long bytes = 512L + payloadBytes + 64L * function.RegCount + 16L * function.Params.Count
            + 96L * function.Slots.Count + 160L * function.Blocks.Count;
        void Text(string? value) { if (value is not null) bytes = checked(bytes + 32 + 3L * IrBinary.Utf8.GetByteCount(value)); }
        Text(function.Name); Text(function.SourceFile); Text(function.Display);
        foreach (Instr instruction in function.Blocks.SelectMany(block => block.Instrs))
        {
            bytes = checked(bytes + 256 + 64L * instruction.Operands.Count + 16L * instruction.Targets.Count);
            Text(instruction.Callee);
            foreach (SymOperand address in instruction.Operands.OfType<SymOperand>()) Text(address.Name);
        }
        return bytes;
    }

    public static byte[] Write(Function function)
    {
        if (function.Async is not null) throw new InvalidDataException("Serialize IR after async lowering");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, IrBinary.Utf8, leaveOpen: true);
        writer.Write(1); IrBinary.Text(writer, function.Name); writer.Write((byte)function.Returns);
        writer.Write(function.Exported); writer.Write(function.Coalescible); writer.Write(function.FromLibrary);
        IrBinary.Text(writer, function.SourceFile); writer.Write(function.Line); IrBinary.Text(writer, function.Display);
        Dictionary<int, IrType> registers = new();
        void Remember(VReg? register)
        {
            if (register is null) return;
            if (register.Id < 0 || register.Id >= function.RegCount
                || registers.TryGetValue(register.Id, out IrType previous) && previous != register.Type)
                throw new InvalidDataException("Inconsistent IR register identity");
            registers[register.Id] = register.Type;
        }
        foreach (VReg parameter in function.Params) Remember(parameter);
        foreach (Instr instruction in function.Blocks.SelectMany(block => block.Instrs))
        {
            Remember(instruction.Dest);
            foreach (RegOperand operand in instruction.Operands.OfType<RegOperand>()) Remember(operand.Reg);
        }
        writer.Write(function.RegCount);
        for (int i = 0; i < function.RegCount; i++) writer.Write((byte)registers.GetValueOrDefault(i, IrType.I32));
        writer.Write(function.Params.Count);
        foreach (VReg parameter in function.Params) writer.Write(parameter.Id);
        writer.Write(function.Slots.Count);
        foreach (FrameSlot slot in function.Slots) { writer.Write(slot.Bytes); writer.Write(slot.Align); }
        Dictionary<FrameSlot, int> slots = function.Slots.Select((slot, id) => (slot, id)).ToDictionary(pair => pair.slot, pair => pair.id);
        Dictionary<IrBlock, int> blocks = function.Blocks.Select((block, id) => (block, id)).ToDictionary(pair => pair.block, pair => pair.id);
        writer.Write(function.Blocks.Count);
        foreach (IrBlock block in function.Blocks) writer.Write(block.IsLandingPad);
        foreach (IrBlock block in function.Blocks)
        {
            writer.Write(block.Instrs.Count);
            foreach (Instr instruction in block.Instrs)
            {
                writer.Write((int)instruction.Op); writer.Write(instruction.Dest?.Id ?? -1);
                writer.Write(instruction.Size); writer.Write(instruction.Signed); writer.Write(instruction.Offset);
                IrBinary.Text(writer, instruction.Callee); writer.Write(instruction.Line);
                writer.Write(instruction.Operands.Count);
                foreach (Operand operand in instruction.Operands)
                    switch (operand)
                    {
                        case RegOperand register: writer.Write((byte)1); writer.Write(register.Reg.Id); break;
                        case ImmOperand immediate: writer.Write((byte)2); writer.Write((byte)immediate.Type); writer.Write(immediate.Value); break;
                        case SymOperand address: writer.Write((byte)3); IrBinary.Text(writer, address.Name); writer.Write(address.Offset); break;
                        case SlotOperand slot: writer.Write((byte)4); writer.Write(slots[slot.Slot]); break;
                        default: throw new InvalidDataException("Unknown IR operand");
                    }
                writer.Write(instruction.Targets.Count);
                foreach (IrBlock target in instruction.Targets) writer.Write(blocks[target]);
                writer.Write(instruction.Default is null ? -1 : blocks[instruction.Default]);
            }
        }
        return stream.ToArray();
    }

    public static Function Read(byte[] payload, IrReadBudget? budget = null)
    {
        budget ??= new();
        budget.Charge(512L + payload.Length, 1, "function payload");
        using MemoryStream stream = new(payload, writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        try
        {
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported IR function version");
            Function function = new(IrBinary.Name(reader, budget), IrBinary.Type(reader))
            {
                Exported = IrBinary.Flag(reader), Coalescible = IrBinary.Flag(reader), FromLibrary = IrBinary.Flag(reader),
                SourceFile = IrBinary.Text(reader, budget), Line = reader.ReadInt32(), Display = IrBinary.Text(reader, budget),
            };
            int count = IrBinary.Count(reader);
            budget.Charge(count, 64, "registers");
            VReg[] registers = new VReg[count];
            for (int i = 0; i < count; i++) registers[i] = function.NewReg(IrBinary.Type(reader));
            T At<T>(T[] values, int index) => index >= 0 && index < values.Length ? values[index]
                : throw new InvalidDataException("IR reference outside table");
            int parameters = IrBinary.Count(reader);
            budget.Charge(parameters, 16, "parameters");
            for (int i = 0; i < parameters; i++) function.Params.Add(At(registers, reader.ReadInt32()));
            int slotCount = IrBinary.Count(reader);
            budget.Charge(slotCount, 96, "frame slots");
            FrameSlot[] slots = new FrameSlot[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                int size = reader.ReadInt32(), align = reader.ReadInt32();
                if (size < 0 || size > 64 * 1024 * 1024 || align < 1 || align > 4096 || (align & (align - 1)) != 0)
                    throw new InvalidDataException("Invalid IR frame slot");
                slots[i] = function.NewSlot(size, align);
            }
            int blockCount = IrBinary.Count(reader);
            budget.Charge(blockCount, 160, "blocks");
            IrBlock[] blocks = new IrBlock[blockCount];
            for (int i = 0; i < blockCount; i++) { blocks[i] = function.NewBlock(); blocks[i].IsLandingPad = IrBinary.Flag(reader); }
            foreach (IrBlock block in blocks)
            {
                int instructions = IrBinary.Count(reader);
                budget.Charge(instructions, 256, "instructions");
                for (int i = 0; i < instructions; i++)
                {
                    Opcode opcode = (Opcode)reader.ReadInt32(); int destination = reader.ReadInt32();
                    if (!Enum.IsDefined(opcode) || destination < -1) throw new InvalidDataException("Invalid IR instruction");
                    Instr instruction = new()
                    {
                        Op = opcode, Dest = destination == -1 ? null : At(registers, destination),
                        Size = reader.ReadInt32(), Signed = IrBinary.Flag(reader), Offset = reader.ReadInt64(),
                        Callee = IrBinary.Text(reader, budget), Line = reader.ReadInt32(),
                    };
                    int operands = IrBinary.Count(reader);
                    budget.Charge(operands, 64, "operands");
                    for (int operand = 0; operand < operands; operand++)
                    {
                        switch (reader.ReadByte())
                        {
                            case 1: instruction.Operands.Add(new RegOperand(At(registers, reader.ReadInt32()))); break;
                            case 2:
                                IrType type = IrBinary.Type(reader);
                                instruction.Operands.Add(new ImmOperand(reader.ReadInt64(), type)); break;
                            case 3: instruction.Operands.Add(new SymOperand(IrBinary.Name(reader, budget), reader.ReadInt64())); break;
                            case 4: instruction.Operands.Add(new SlotOperand(At(slots, reader.ReadInt32()))); break;
                            default: throw new InvalidDataException("Unknown IR operand encoding");
                        }
                    }
                    int targets = IrBinary.Count(reader);
                    budget.Charge(targets, 16, "block references");
                    for (int target = 0; target < targets; target++) instruction.Targets.Add(At(blocks, reader.ReadInt32()));
                    int otherwise = reader.ReadInt32();
                    if (otherwise < -1) throw new InvalidDataException("Invalid IR default target");
                    if (otherwise >= 0) instruction.Default = At(blocks, otherwise);
                    block.Instrs.Add(instruction);
                }
            }
            IrBinary.End(reader); Verifier.Check(function, "IR import");
            return function;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated IR function"); }
        catch (System.Text.DecoderFallbackException) { throw new InvalidDataException("Invalid IR UTF-8"); }
        catch (IrVerifyException error) { throw new InvalidDataException(error.Message, error); }
    }
}
