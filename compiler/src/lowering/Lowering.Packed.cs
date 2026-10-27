using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

/// <summary>
/// THE PACKED OPERATIONS (Prelude's Packed: Packed.Add16, Packed.Average8 and
/// the rest, on Packed64's eight bytes), inside a packed block's body
/// (Parser.ParsePackedBlock). Each is one MMX instruction where the block's
/// variant has it -- its level, 2 with the MMX extensions, 1 with MMX alone
/// (Sys.PackedEnter's third argument) -- loading its first operand into
/// mm0, combining the second from memory, and storing the result into the
/// call's result buffer (Selector's packed operations); and the runtime's
/// scalar form of the same lanes where it has not (Runtime.PackedScalar), as
/// in a block's variant for a processor without MMX, or in long mode.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>A two-operand operation: the instruction's opcode (0F xx), and whether it is one of the MMX extensions'.</summary>
    private static readonly Dictionary<string, (byte Op, bool Ext)> PackedBinary = new(StringComparer.Ordinal)
    {
        ["Add8"] = (0xFC, false), ["Add16"] = (0xFD, false), ["Add32"] = (0xFE, false),
        ["Sub8"] = (0xF8, false), ["Sub16"] = (0xF9, false), ["Sub32"] = (0xFA, false),
        ["AddSaturateU8"] = (0xDC, false), ["AddSaturateS8"] = (0xEC, false),
        ["AddSaturateU16"] = (0xDD, false), ["AddSaturateS16"] = (0xED, false),
        ["SubSaturateU8"] = (0xD8, false), ["SubSaturateS8"] = (0xE8, false),
        ["SubSaturateU16"] = (0xD9, false), ["SubSaturateS16"] = (0xE9, false),
        ["MultiplyLow16"] = (0xD5, false), ["MultiplyHigh16"] = (0xE5, false), ["MultiplyAdd16"] = (0xF5, false),
        ["And"] = (0xDB, false), ["Or"] = (0xEB, false), ["Xor"] = (0xEF, false), ["AndNot"] = (0xDF, false),
        ["Equal8"] = (0x74, false), ["Equal16"] = (0x75, false), ["Equal32"] = (0x76, false),
        ["Greater8"] = (0x64, false), ["Greater16"] = (0x65, false), ["Greater32"] = (0x66, false),
        ["PackSaturateU8"] = (0x67, false), ["PackSaturateS8"] = (0x63, false), ["PackSaturateS16"] = (0x6B, false),
        ["UnpackLow8"] = (0x60, false), ["UnpackLow16"] = (0x61, false), ["UnpackLow32"] = (0x62, false),
        ["UnpackHigh8"] = (0x68, false), ["UnpackHigh16"] = (0x69, false), ["UnpackHigh32"] = (0x6A, false),
        ["MultiplyHighU16"] = (0xE4, true), ["Average8"] = (0xE0, true), ["Average16"] = (0xE3, true),
        ["MinU8"] = (0xDA, true), ["MaxU8"] = (0xDE, true), ["Min16"] = (0xEA, true), ["Max16"] = (0xEE, true),
        ["SumAbsoluteDifferences8"] = (0xF6, true),
    };

    /// <summary>A shift by a constant count: the instruction's group (0F 71, 72, 73) and its /digit.</summary>
    private static readonly Dictionary<string, (byte Group, byte Sub)> PackedShift = new(StringComparer.Ordinal)
    {
        ["ShiftLeft16"] = (0x71, 6), ["ShiftLeft32"] = (0x72, 6), ["ShiftLeft64"] = (0x73, 6),
        ["ShiftRightU16"] = (0x71, 2), ["ShiftRightU32"] = (0x72, 2), ["ShiftRightU64"] = (0x73, 2),
        ["ShiftRightS16"] = (0x71, 4), ["ShiftRightS32"] = (0x72, 4),
    };

    /// <summary>Each function's open packed blocks' levels, innermost on top (PackedEnter pushes, its first PackedLeave pops).</summary>
    private readonly Dictionary<Function, Stack<int>> _packedLevels = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(Function, int)> _packedLeft = new();

    /// <summary>The level of the packed block being lowered, or -1 outside every one.</summary>
    private int PackedLevelHere => _packedLevels.TryGetValue(_f, out Stack<int>? open) && open.Count > 0 ? open.Peek() : -1;

    private VReg PackedIntrinsic(CallExpr call, MethodSymbol target)
    {
        string name = target.Name;
        Type returns = target.Returns;
        switch (name)
        {
            case "Mmx":
            case "Sse":
                return Fail(call, "Packed." + name + "() opens a packed block, as the resource of a using: using (Packed." + name + "()) { ... }");
            // The eight bytes, as they are: no instruction, inside a block or
            // out of one.
            case "FromBits":
            {
                VReg made = ResultBuffer(call, returns);
                _e.Store(RegOperand.Of(made), RegOperand.Of(ToI64(Arg(call, target, 0))), 0, 8);
                return made;
            }
            case "Load":
            {
                VReg made = ResultBuffer(call, returns);
                _e.Emit(Opcode.MemCopy, null, R(made), R(ToWord(Arg(call, target, 0))), Imm(8, IrTypes.Word));
                return made;
            }
            case "Store":
                _e.Emit(Opcode.MemCopy, null, R(ToWord(Arg(call, target, 0))), R(Arg(call, target, 1)), Imm(8, IrTypes.Word));
                return Void();
        }

        int level = PackedLevelHere;
        if (level < 0)
            return Fail(call, "Packed." + name + " is a packed block's: it uses the MMX registers, which only a packed block owns (using (Packed.Mmx()) { ... })");
        bool machine = _t.WordSize == 4 && level >= 1;
        bool extended = machine && level >= 2;

        switch (name)
        {
            // MOVNTQ and SFENCE, the MMX extensions': where the block has
            // none, a plain store and nothing.
            case "StoreNonTemporal":
                if (!extended)
                {
                    _e.Emit(Opcode.MemCopy, null, R(ToWord(Arg(call, target, 0))), R(Arg(call, target, 1)), Imm(8, IrTypes.Word));
                    return Void();
                }
                _e.Call(MachineIntrinsics.PackedStoreNonTemporal, IrType.Void, R(ToWord(Arg(call, target, 0))), R(Arg(call, target, 1)));
                return Void();
            case "Fence":
                if (extended) _e.Call(MachineIntrinsics.PackedFence, IrType.Void);
                return Void();
        }

        if (PackedBinary.TryGetValue(name, out var binary))
        {
            VReg a = Arg(call, target, 0), b = Arg(call, target, 1);
            VReg made = ResultBuffer(call, returns);
            if (machine && (!binary.Ext || extended))
            {
                _e.Call(MachineIntrinsics.PackedOp(binary.Op), IrType.Void, R(made), R(a), R(b));
                return made;
            }
            VReg x = _e.Load(IrType.I64, a, 0), y = _e.Load(IrType.I64, b, 0);
            VReg answer = PackedScalarCall(call, "PackedScalar", _e.Const(binary.Op, IrType.I32), x, y);
            _e.Store(RegOperand.Of(made), RegOperand.Of(answer), 0, 8);
            return made;
        }

        if (PackedShift.TryGetValue(name, out var shift))
        {
            if (PackedConstant(call, target, 1, 0, 255) is not int count) return Void();
            VReg a = Arg(call, target, 0);
            VReg made = ResultBuffer(call, returns);
            if (machine)
            {
                _e.Call(MachineIntrinsics.PackedShift(shift.Group, shift.Sub, count), IrType.Void, R(made), R(a));
                return made;
            }
            VReg answer = PackedScalarCall(call, "PackedScalarShift", _e.Const(shift.Group, IrType.I32), _e.Const(shift.Sub, IrType.I32),
                _e.Const(count, IrType.I32), _e.Load(IrType.I64, a, 0));
            _e.Store(RegOperand.Of(made), RegOperand.Of(answer), 0, 8);
            return made;
        }

        switch (name)
        {
            case "Shuffle16":
            {
                if (PackedConstant(call, target, 1, 0, 255) is not int order) return Void();
                VReg a = Arg(call, target, 0);
                VReg made = ResultBuffer(call, returns);
                if (extended)
                {
                    _e.Call(MachineIntrinsics.PackedShuffle(order), IrType.Void, R(made), R(a));
                    return made;
                }
                VReg answer = PackedScalarCall(call, "PackedScalarShuffle", _e.Load(IrType.I64, a, 0), _e.Const(order, IrType.I32));
                _e.Store(RegOperand.Of(made), RegOperand.Of(answer), 0, 8);
                return made;
            }
            case "Extract16":
            {
                if (PackedConstant(call, target, 1, 0, 3) is not int at) return Void();
                VReg a = Arg(call, target, 0);
                if (extended) return _e.Call(MachineIntrinsics.PackedExtract(at), IrType.I32, R(a))!;
                return PackedScalarCall(call, "PackedScalarExtract", _e.Load(IrType.I64, a, 0), _e.Const(at, IrType.I32));
            }
            case "Insert16":
            {
                if (PackedConstant(call, target, 2, 0, 3) is not int at) return Void();
                VReg a = Arg(call, target, 0);
                VReg value = ToI32(Arg(call, target, 1));
                VReg made = ResultBuffer(call, returns);
                if (extended)
                {
                    _e.Call(MachineIntrinsics.PackedInsert(at), IrType.Void, R(made), R(a), R(value));
                    return made;
                }
                VReg answer = PackedScalarCall(call, "PackedScalarInsert", _e.Load(IrType.I64, a, 0), value, _e.Const(at, IrType.I32));
                _e.Store(RegOperand.Of(made), RegOperand.Of(answer), 0, 8);
                return made;
            }
        }
        return Fail(call, "Packed." + name + " is no packed operation this compiler knows");
    }

    /// <summary>A packed operation's constant argument -- a count, an order, a word's place -- which the instruction holds in its own bytes.</summary>
    private int? PackedConstant(CallExpr call, MethodSymbol target, int index, int least, int most)
    {
        if (LiteralInteger(call.Args[index]) is long v && v >= least && v <= most) return (int)v;
        Error(call, "Packed." + target.Name + "'s " + target.Params[index].Name + " must be a constant from " + least + " to " + most + ": the instruction holds it in its own bytes");
        return null;
    }

    /// <summary>An integer literal's value -- decimal, hex, binary, digit separators and suffixes -- or null.</summary>
    private static long? LiteralInteger(Expr e)
    {
        if (e is not LiteralExpr { Kind: Lit.Int, Text: var text }) return null;
        string t = text.Replace("_", "").TrimEnd('u', 'U', 'l', 'L');
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return System.Convert.ToInt64(t[2..], 16);
            if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return System.Convert.ToInt64(t[2..], 2);
            return long.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>The runtime's scalar form of a packed operation (Runtime.PackedScalar and the rest).</summary>
    private VReg PackedScalarCall(CallExpr call, string routine, params VReg[] given)
    {
        MethodSymbol? scalar = RequireRuntime(call, routine, given.Length, "Packed." + routine);
        if (scalar is null) return _e.Const(0, IrType.I64);
        List<Operand> args = new();
        for (int k = 0; k < given.Length; k++) args.Add(R(AsParam(given[k], scalar.Params[k].Type)));
        return _e.Call(CallLabel(scalar), IrTypes.Of(scalar.Returns), args.ToArray())!;
    }
}
