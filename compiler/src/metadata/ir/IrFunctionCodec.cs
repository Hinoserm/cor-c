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
        // A lowered async body's record (Write): its size symbol and the frame.
        if (function.Async is AsyncFrame frame) { bytes = checked(bytes + 128); Text(frame.SizeSymbol); Text(frame.StackSymbol); }
        foreach (Instr instruction in function.Blocks.SelectMany(block => block.Instrs))
        {
            bytes = checked(bytes + 256 + 64L * instruction.Operands.Count + 16L * instruction.Targets.Count);
            Text(instruction.Callee);
            Text(instruction.DispatchType);
            Text(instruction.Field);
            foreach (SymOperand address in instruction.Operands.OfType<SymOperand>()) Text(address.Name);
        }
        return bytes;
    }

    /// <summary>
    /// The function's record. `kept`: the calls the inliner is to leave
    /// calls (Module.KeepCalls) -- an owned-elements candidate's, recorded
    /// with the IR the link runs the late passes over, so that run keeps them
    /// as the compile did and judges the same candidate.
    /// </summary>
    public static byte[] Write(Function function, IReadOnlySet<Instr>? kept = null)
    {
        // An async body is written before its lowering too -- a unit's IR for
        // the link is taken before the late passes and the async transform --
        // with what the transform reads: whether it ran, and what a suspension
        // hands back.
        if (function.Async is { Lowered: false, SuspendResult: not (null or ImmOperand) })
            throw new InvalidDataException("An async body's suspension result is not a constant");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, IrBinary.Utf8, leaveOpen: true);
        writer.Write(7); IrBinary.Text(writer, function.Name); writer.Write((byte)function.Returns);
        writer.Write(function.Exported); writer.Write(function.Coalescible); writer.Write(function.FromLibrary);
        writer.Write(function.NoInlining);
        writer.Write(function.CalleePops);
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
        // A lowered async body keeps the record that it was one (AsyncFrame).
        writer.Write(function.Async is not null);
        if (function.Async is AsyncFrame frame)
        {
            writer.Write(frame.StateMachine.Id); writer.Write(frame.StateOffset); writer.Write(frame.FieldsStart);
            IrBinary.Text(writer, frame.SizeSymbol);
            writer.Write(frame.Lowered);
            writer.Write(frame.SuspendResult is ImmOperand);
            if (frame.SuspendResult is ImmOperand result) { writer.Write(result.Value); writer.Write((byte)result.Type); }
            // Version 7: an `async ValueTask` machine that may move, and the
            // word its kickoff reads (AsyncFrame.MayMove).
            writer.Write(frame.MayMove);
            IrBinary.Text(writer, frame.StackSymbol);
        }
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
                IrBinary.Text(writer, instruction.Callee); IrBinary.Text(writer, instruction.DispatchType); IrBinary.Text(writer, instruction.Field); writer.Write(instruction.Line);
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
        // The kept calls, by their place among the function's instructions.
        List<int> keeping = new();
        if (kept is { Count: > 0 })
        {
            int at = 0;
            foreach (Instr instruction in function.Blocks.SelectMany(block => block.Instrs))
            {
                if (instruction.Op == Opcode.Call && kept.Contains(instruction)) keeping.Add(at);
                at++;
            }
        }
        writer.Write(keeping.Count);
        foreach (int at in keeping) writer.Write(at);
        return stream.ToArray();
    }

    /// <summary>The function a record holds; the calls it keeps (Write) added to `kept` when one is given.</summary>
    public static Function Read(byte[] payload, IrReadBudget? budget = null, ICollection<Instr>? kept = null)
    {
        budget ??= new();
        budget.Charge(512L + payload.Length, 1, "function payload");
        using MemoryStream stream = new(payload, writable: false);
        using BinaryReader reader = new(stream, IrBinary.Utf8);
        try
        {
            // Version 6 adds the bytes a stdcall function pops (CalleePops);
            // a version 5 record's function pops none. Version 7 adds an async
            // frame's MayMove and StackSymbol.
            int version = reader.ReadInt32();
            if (version is not (5 or 6 or 7)) throw new InvalidDataException("Unsupported IR function version");
            Function function = new(IrBinary.Name(reader, budget), IrBinary.Type(reader))
            {
                Exported = IrBinary.Flag(reader), Coalescible = IrBinary.Flag(reader), FromLibrary = IrBinary.Flag(reader),
                NoInlining = IrBinary.Flag(reader), CalleePops = version >= 6 ? reader.ReadInt32() : 0,
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
            if (IrBinary.Flag(reader))
            {
                budget.Charge(1, 128, "async frame");
                VReg machine = At(registers, reader.ReadInt32());
                int stateOffset = reader.ReadInt32(), fieldsStart = reader.ReadInt32();
                string sizeSymbol = IrBinary.Text(reader, budget) ?? throw new InvalidDataException("Async frame without a size symbol");
                bool lowered = IrBinary.Flag(reader);
                ImmOperand? suspendResult = IrBinary.Flag(reader) ? new ImmOperand(reader.ReadInt64(), (IrType)reader.ReadByte()) : null;
                bool mayMove = version >= 7 && IrBinary.Flag(reader);
                string? stackSymbol = version >= 7 ? IrBinary.Text(reader, budget) : null;
                function.Async = new AsyncFrame
                {
                    StateMachine = machine, StateOffset = stateOffset, FieldsStart = fieldsStart,
                    SizeSymbol = sizeSymbol,
                    Lowered = lowered,
                    SuspendResult = suspendResult,
                    MayMove = mayMove,
                    StackSymbol = stackSymbol is { Length: > 0 } ? stackSymbol : null,
                };
            }
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
                        Callee = IrBinary.Text(reader, budget), DispatchType = IrBinary.Text(reader, budget), Field = IrBinary.Text(reader, budget), Line = reader.ReadInt32(),
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
            // Each a place among the instructions already read and charged,
            // its four bytes among the payload's (DecodeCost).
            int keeping = IrBinary.Count(reader);
            if (keeping > 0)
            {
                Instr[] all = blocks.SelectMany(block => block.Instrs).ToArray();
                int last = -1;
                for (int k = 0; k < keeping; k++)
                {
                    int at = reader.ReadInt32();
                    if (at <= last || at >= all.Length || all[at].Op != Opcode.Call) throw new InvalidDataException("Invalid IR kept call");
                    last = at;
                    kept?.Add(all[at]);
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
