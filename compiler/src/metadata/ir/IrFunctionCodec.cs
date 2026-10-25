using Corsac.Lang.Ir;
using Corsac.Lang.Opt;
using IrBlock = Corsac.Lang.Ir.Block;

namespace Corsac.Lang.Metadata;

/// <summary>Complete post-async IR function encoding, independently addressable in an object.</summary>
public static class IrFunctionCodec
{
    /// <summary>
    /// The record's version: one layout, 0, until a release. It carries what
    /// the analyses mark on the IR -- a load's or a copy's Number (never an
    /// address), a parameter's Number, a site the link chose for a region
    /// (RegionSite), a loop header it gave one (RegionLoop, RegionLoopBytes),
    /// the field a load or a store names (Instr.Family) -- so that whatever
    /// the link rebuilds from archived IR (its late passes, its lifetime runs,
    /// the copy a refused run is taken back from, UnitBackend) reads them as
    /// the compile left them; and the bytes a stdcall function pops
    /// (CalleePops), an async frame's MayMove and StackSymbol, and each class
    /// parameter's chain and interfaces. Names (a register's, a slot's) are
    /// for the dump alone and stay out.
    /// </summary>
    private const int Version = 0;

    // An instruction's marks (Instr): one byte.
    private const byte NumberMark = 1, RegionSiteMark = 2, FamilyMark = 4, DeviceMark = 8;

    /// <summary>
    /// What Read charges for the function's record, `payloadBytes` long:
    /// the most decoding it alone holds at once. Every object at its size,
    /// as Read charges it -- an instruction 104 bytes and its place in its
    /// block's list, a register with the operand its uses share, an operand
    /// a register is not 32, an array its header and a word an element --
    /// and every name once (IrReadBudget.ChargeName).
    /// </summary>
    public static long DecodeCost(Function function, int payloadBytes)
    {
        long bytes = 512L + payloadBytes + 64L * function.RegCount + 16L * function.Params.Count
            + 96L * function.Slots.Count + 160L * function.Blocks.Count;
        HashSet<string> names = _costNames ??= new(StringComparer.Ordinal);
        names.Clear();
        void Name(string? value) { if (value is not null && names.Add(value)) bytes = checked(bytes + IrReadBudget.NameBytes(IrBinary.Utf8.GetByteCount(value))); }
        void Text(string? value) { if (value is not null) bytes = checked(bytes + 32 + 3L * IrBinary.Utf8.GetByteCount(value)); }
        Name(function.Name); Name(function.SourceFile); Text(function.Display);
        // A lowered async body's record (Write): its size symbol and the frame.
        if (function.Async is AsyncFrame frame) { bytes = checked(bytes + 128); Name(frame.SizeSymbol); Name(frame.StackSymbol); }
        // Each class parameter's chain and interfaces (Write), as Read charges them.
        if (function.ParamClasses is { } classes)
            foreach ((VReg parameter, string[] chain) in classes)
            {
                bytes = checked(bytes + 64 + 32L * chain.Length);
                foreach (string name in chain) Name(name);
                if (function.ParamInterfaces?.GetValueOrDefault(parameter) is { } faces)
                {
                    bytes = checked(bytes + 32L * faces.Count);
                    foreach (string name in faces) Name(name);
                }
            }
        // WALKED WITHOUT A QUERY: a SelectMany over the blocks and an OfType
        // over every instruction's operands were an iterator, a closure and a
        // boxed enumerator an instruction, for every function archived.
        foreach (IrBlock block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
            {
                bytes = checked(bytes + InstructionBytes + OperandArrayBytes(instruction.Operands.Count) + TargetArrayBytes(instruction.Targets.Count));
                if (Extra(instruction.Size, instruction.Offset, instruction.DispatchType) || instruction.Default is not null) bytes = checked(bytes + ExtraBytes);
                Name(instruction.Callee);
                Name(instruction.DispatchType);
                Name(instruction.Field);
                Name(instruction.Family);
                foreach (Operand operand in instruction.Operands)
                {
                    if (operand is RegOperand) continue;
                    bytes = checked(bytes + OperandBytes);
                    if (operand is SymOperand address) Name(address.Name);
                }
            }
        return bytes;
    }

    [ThreadStatic] private static HashSet<string>? _costNames;

    // WHAT A DECODED INSTRUCTION HOLDS. Instr, 104 bytes, and its place in
    // its block's list; an operand a register is not (a register's is the
    // one its every use shares, RegOperand.Of, charged with the register);
    // InstrRare, where the instruction has what does not fit it.
    private const int InstructionBytes = 128, OperandBytes = 32, ExtraBytes = 48;

    // Past the two an instruction holds itself, its operands are an array
    // exactly their number (OperandList); its targets one Block or an array of them (Instr.SetTargets).
    private static long OperandArrayBytes(int count) => count > 2 ? 24 + 8L * (count - 2) : 0;
    private static long TargetArrayBytes(int count) => count > 0 ? 24 + 8L * count : 0;

    // Whether an instruction keeps a size, an offset or a dispatch type in
    // its InstrRare (as a switch's default is): what its own fields cannot hold.
    private static bool Extra(int size, long offset, string? dispatchType)
        => size is < 0 or > byte.MaxValue || offset is < int.MinValue or > int.MaxValue || dispatchType is not null;

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
        BinaryWriter writer = IrBinary.Writer();
        writer.Write(Version); IrBinary.Text(writer, function.Name); writer.Write((byte)function.Returns);
        writer.Write(function.Exported); writer.Write(function.Coalescible); writer.Write(function.FromLibrary);
        writer.Write(function.NoInlining);
        writer.Write(function.CalleePops);
        IrBinary.Text(writer, function.SourceFile); writer.Write(function.Line); IrBinary.Text(writer, function.Display);
        // EACH REGISTER'S TYPE BY NUMBER, in an array, every instruction walked
        // without an enumerator: a table and a query a function were most of
        // what writing its record made.
        int count = function.RegCount;
        IrType?[] registers = count == 0 ? Array.Empty<IrType?>() : new IrType?[count];
        void Remember(VReg? register)
        {
            if (register is null) return;
            if (register.Id < 0 || register.Id >= count
                || registers[register.Id] is IrType previous && previous != register.Type)
                throw new InvalidDataException("Inconsistent IR register identity");
            registers[register.Id] = register.Type;
        }
        foreach (VReg parameter in function.Params) Remember(parameter);
        foreach (IrBlock block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
            {
                Remember(instruction.Dest);
                foreach (Operand operand in instruction.Operands)
                    if (operand is RegOperand reg) Remember(reg.Reg);
            }
        writer.Write(count);
        for (int i = 0; i < count; i++) writer.Write((byte)(registers[i] ?? IrType.I32));
        writer.Write(function.Params.Count);
        foreach (VReg parameter in function.Params) { writer.Write(parameter.Id); writer.Write(parameter.Number); }
        // The class each class parameter is declared as and the
        // interfaces it implements (Function.ParamClasses, ParamInterfaces),
        // by the parameter's place: what the link's run of the late passes
        // names a virtual call on it with (Escape.VirtualCallee) and folds
        // its type tests by, as the compile did.
        int classed = 0;
        for (int k = 0; k < function.Params.Count; k++) if (function.ParamClasses?.ContainsKey(function.Params[k]) == true) classed++;
        writer.Write(classed);
        for (int k = 0; k < function.Params.Count && classed > 0; k++)
        {
            if (function.ParamClasses?.TryGetValue(function.Params[k], out string[]? chain) != true) continue;
            writer.Write(k);
            writer.Write(chain!.Length);
            foreach (string name in chain) IrBinary.Text(writer, name);
            HashSet<string>? faces = function.ParamInterfaces?.GetValueOrDefault(function.Params[k]);
            string[] ordered = faces is null ? Array.Empty<string>() : faces.Order(StringComparer.Ordinal).ToArray();
            writer.Write(ordered.Length);
            foreach (string name in ordered) IrBinary.Text(writer, name);
        }
        // A lowered async body keeps the record that it was one, and which
        // kind (AsyncFrame).
        writer.Write(function.Async is not null);
        if (function.Async is AsyncFrame frame)
        {
            writer.Write((byte)frame.Kind);
            writer.Write(frame.StateMachine.Id); writer.Write(frame.StateOffset); writer.Write(frame.FieldsStart);
            IrBinary.Text(writer, frame.SizeSymbol);
            writer.Write(frame.Lowered);
            writer.Write(frame.SuspendResult is ImmOperand);
            if (frame.SuspendResult is ImmOperand result) { writer.Write(result.Value); writer.Write((byte)result.Type); }
            // An `async ValueTask` machine that may move, and the word its
            // kickoff reads (AsyncFrame.MayMove).
            writer.Write(frame.MayMove);
            IrBinary.Text(writer, frame.StackSymbol);
        }
        writer.Write(function.Slots.Count);
        foreach (FrameSlot slot in function.Slots) { writer.Write(slot.Bytes); writer.Write(slot.Align); }
        Dictionary<FrameSlot, int> slots = new(function.Slots.Count);
        for (int id = 0; id < function.Slots.Count; id++) slots.Add(function.Slots[id], id);
        Dictionary<IrBlock, int> blocks = new(function.Blocks.Count);
        for (int id = 0; id < function.Blocks.Count; id++) blocks.Add(function.Blocks[id], id);
        writer.Write(function.Blocks.Count);
        foreach (IrBlock block in function.Blocks)
        {
            writer.Write(block.IsLandingPad); writer.Write(block.RegionLoop);
            if (block.RegionLoop) writer.Write(block.RegionLoopBytes);
        }
        foreach (IrBlock block in function.Blocks)
        {
            writer.Write(block.Instrs.Count);
            foreach (Instr instruction in block.Instrs)
            {
                writer.Write((int)instruction.Op); writer.Write(instruction.Dest?.Id ?? -1);
                writer.Write(instruction.Size); writer.Write(instruction.Signed); writer.Write(instruction.Offset);
                IrBinary.Text(writer, instruction.Callee); IrBinary.Text(writer, instruction.DispatchType); IrBinary.Text(writer, instruction.Field); writer.Write(instruction.Line);
                writer.Write((byte)((instruction.Number ? NumberMark : 0) | (instruction.RegionSite ? RegionSiteMark : 0) | (instruction.Family is not null ? FamilyMark : 0)
                    | (instruction.Device ? DeviceMark : 0)));
                if (instruction.Family is not null) IrBinary.Text(writer, instruction.Family);
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
            foreach (IrBlock block in function.Blocks)
                foreach (Instr instruction in block.Instrs)
                {
                    if (instruction.Op == Opcode.Call && kept.Contains(instruction)) keeping.Add(at);
                    at++;
                }
        }
        writer.Write(keeping.Count);
        foreach (int at in keeping) writer.Write(at);
        return IrBinary.Written(writer);
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
            if (reader.ReadInt32() != Version) throw new InvalidDataException("Unsupported IR function version");
            Function function = new(IrBinary.Name(reader, budget), IrBinary.Type(reader))
            {
                Exported = IrBinary.Flag(reader), Coalescible = IrBinary.Flag(reader), FromLibrary = IrBinary.Flag(reader),
                NoInlining = IrBinary.Flag(reader), CalleePops = reader.ReadInt32(),
                SourceFile = IrBinary.NameOrNull(reader, budget), Line = reader.ReadInt32(), Display = IrBinary.Text(reader, budget),
            };
            int count = IrBinary.Count(reader);
            budget.Charge(count, 64, "registers");
            VReg[] registers = new VReg[count];
            for (int i = 0; i < count; i++) registers[i] = function.NewReg(IrBinary.Type(reader));
            T At<T>(T[] values, int index) => index >= 0 && index < values.Length ? values[index]
                : throw new InvalidDataException("IR reference outside table");
            int parameters = IrBinary.Count(reader);
            budget.Charge(parameters, 16, "parameters");
            for (int i = 0; i < parameters; i++)
            {
                VReg parameter = At(registers, reader.ReadInt32());
                parameter.Number = IrBinary.Flag(reader);
                function.Params.Add(parameter);
            }
            int classed = IrBinary.Count(reader);
            budget.Charge(classed, 64, "parameter classes");
            for (int i = 0; i < classed; i++)
            {
                VReg parameter = At(function.Params.ToArray(), reader.ReadInt32());
                int depth = IrBinary.Count(reader);
                budget.Charge(depth, 32, "parameter class chain");
                string[] chain = new string[depth];
                for (int k = 0; k < depth; k++) chain[k] = IrBinary.NameOrNull(reader, budget) ?? throw new InvalidDataException("A parameter class without a name");
                int faceCount = IrBinary.Count(reader);
                budget.Charge(faceCount, 32, "parameter interfaces");
                HashSet<string> faces = new(StringComparer.Ordinal);
                for (int k = 0; k < faceCount; k++) faces.Add(IrBinary.NameOrNull(reader, budget) ?? throw new InvalidDataException("A parameter interface without a name"));
                if (depth == 0) throw new InvalidDataException("A parameter class with no chain");
                (function.ParamClasses ??= new())[parameter] = chain;
                (function.ParamInterfaces ??= new())[parameter] = faces;
            }
            if (IrBinary.Flag(reader))
            {
                budget.Charge(1, 128, "async frame");
                CoroutineKind kind = (CoroutineKind)reader.ReadByte();
                if (!Enum.IsDefined(kind)) throw new InvalidDataException("Invalid async frame kind");
                VReg machine = At(registers, reader.ReadInt32());
                int stateOffset = reader.ReadInt32(), fieldsStart = reader.ReadInt32();
                string sizeSymbol = IrBinary.NameOrNull(reader, budget) ?? throw new InvalidDataException("Async frame without a size symbol");
                bool lowered = IrBinary.Flag(reader);
                ImmOperand? suspendResult = IrBinary.Flag(reader) ? new ImmOperand(reader.ReadInt64(), (IrType)reader.ReadByte()) : null;
                bool mayMove = IrBinary.Flag(reader);
                string? stackSymbol = IrBinary.NameOrNull(reader, budget);
                function.Async = new AsyncFrame
                {
                    Kind = kind,
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
            for (int i = 0; i < blockCount; i++)
            {
                blocks[i] = function.NewBlock();
                blocks[i].IsLandingPad = IrBinary.Flag(reader);
                if (blocks[i].RegionLoop = IrBinary.Flag(reader))
                {
                    long lap = reader.ReadInt64();
                    if (lap < 0) throw new InvalidDataException("Invalid IR loop region size");
                    blocks[i].RegionLoopBytes = lap;
                }
            }
            foreach (IrBlock block in blocks)
            {
                int instructions = IrBinary.Count(reader);
                budget.Charge(instructions, InstructionBytes, "instructions");
                for (int i = 0; i < instructions; i++)
                {
                    Opcode opcode = (Opcode)reader.ReadInt32(); int destination = reader.ReadInt32();
                    if (!Enum.IsDefined(opcode) || destination < -1) throw new InvalidDataException("Invalid IR instruction");
                    int size = reader.ReadInt32(); bool signed = IrBinary.Flag(reader); long offset = reader.ReadInt64();
                    string? callee = IrBinary.NameOrNull(reader, budget), dispatchType = IrBinary.NameOrNull(reader, budget);
                    string? field = IrBinary.NameOrNull(reader, budget);
                    bool extra = Extra(size, offset, dispatchType);
                    if (extra) budget.Charge(1, ExtraBytes, "instructions");
                    Instr instruction = new()
                    {
                        Op = opcode, Dest = destination == -1 ? null : At(registers, destination),
                        Size = size, Signed = signed, Offset = offset,
                        Callee = callee, DispatchType = dispatchType, Field = field, Line = reader.ReadInt32(),
                    };
                    byte marks = reader.ReadByte();
                    if ((marks & ~(NumberMark | RegionSiteMark | FamilyMark | DeviceMark)) != 0) throw new InvalidDataException("Invalid IR instruction marks");
                    instruction.Number = (marks & NumberMark) != 0;
                    instruction.RegionSite = (marks & RegionSiteMark) != 0;
                    instruction.Device = (marks & DeviceMark) != 0;
                    if ((marks & FamilyMark) != 0)
                        instruction.Family = IrBinary.NameOrNull(reader, budget) ?? throw new InvalidDataException("Invalid IR instruction field");
                    // MADE AT ONCE, operands and targets: added one at a time,
                    // each was a copy of all before it (OperandList, TargetList).
                    int operands = IrBinary.Count(reader);
                    if (operands > 2) budget.Charge(1, checked((int)OperandArrayBytes(operands)), "operands");
                    Operand[]? all = operands > 2 ? new Operand[operands] : null;
                    for (int operand = 0; operand < operands; operand++)
                    {
                        Operand read;
                        switch (reader.ReadByte())
                        {
                            case 1: read = RegOperand.Of(At(registers, reader.ReadInt32())); break;
                            case 2:
                                IrType type = IrBinary.Type(reader);
                                budget.Charge(1, OperandBytes, "operands");
                                read = new ImmOperand(reader.ReadInt64(), type); break;
                            case 3:
                                budget.Charge(1, OperandBytes, "operands");
                                read = new SymOperand(IrBinary.Name(reader, budget), reader.ReadInt64()); break;
                            case 4:
                                budget.Charge(1, OperandBytes, "operands");
                                read = new SlotOperand(At(slots, reader.ReadInt32())); break;
                            default: throw new InvalidDataException("Unknown IR operand encoding");
                        }
                        if (all is not null) all[operand] = read;
                        else instruction.Operands.Add(read);
                    }
                    if (all is not null) instruction.Operands.SetAll(all);
                    int targets = IrBinary.Count(reader);
                    if (targets > 0)
                    {
                        budget.Charge(1, checked((int)TargetArrayBytes(targets)), "block references");
                        IrBlock[] to = new IrBlock[targets];
                        for (int target = 0; target < targets; target++) to[target] = At(blocks, reader.ReadInt32());
                        instruction.SetTargets(to);
                    }
                    int otherwise = reader.ReadInt32();
                    if (otherwise < -1) throw new InvalidDataException("Invalid IR default target");
                    if (otherwise >= 0)
                    {
                        if (!extra) budget.Charge(1, ExtraBytes, "instructions");
                        instruction.Default = At(blocks, otherwise);
                    }
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
