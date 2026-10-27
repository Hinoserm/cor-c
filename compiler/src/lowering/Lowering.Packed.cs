using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

/// <summary>
/// THE PACKED OPERATIONS (Prelude's Packed: Packed.Add16, Packed.Average8,
/// Packed.AddF32 and the rest, on Packed64's eight bytes and Packed128's
/// sixteen; PackedOps), inside a packed block's body
/// (Parser.ParsePackedBlock). Each is the instruction the block's variant
/// has -- its features, Sys.PackedEnter's third argument -- and its lanes
/// one at a time where it has none (Runtime.PackedScalar and kin), as in the
/// variant for a processor without MMX, or in long mode:
///
///   Packed64, integer lanes: MMX (0F xx), the MMX extensions' where it has
///   them; Add64 and Sub64 are SSE2's.
///   Packed128, integer lanes: SSE2 (66 0F xx); else two MMX operations, a
///   half each, where the lanes keep to their halves; else the scalar form.
///   Float lanes (AddF32...): SSE (0F xx, on two floats through MOVLPS);
///   else 3DNow! (0F 0F /r xx) where it has the operation exactly; else the
///   x87's, one float at a time.
///
/// Every machine form loads its operands from memory, computes in mm0 or
/// xmm0, and stores the result into the call's result buffer (Selector.
/// SelectPackedOp): nothing is kept in a packed register between two.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>Each function's open packed blocks' variants' features, innermost on top (Lowering.Stmt's TryStmt: pushed for the body's lowering, its finally's copies among it).</summary>
    private readonly Dictionary<Function, Stack<int>> _packedOpen = new(ReferenceEqualityComparer.Instance);

    private Stack<int> _packedFeatures => _packedOpen.TryGetValue(_f, out Stack<int>? open) ? open : _packedOpen[_f] = new();

    /// <summary>
    /// A PACKED OPERATION'S OPERAND where its lanes are, not a copy: a
    /// Packed64 or Packed128 is read and never written, so a local's own
    /// storage -- EvalAs's copy of a struct argument was a heap object on
    /// every pass in freestanding code -- and a Packed.Load's own address,
    /// where nothing the operation's operands do can write memory before it
    /// reads them (PackedPure): one movq from the source, not two moves into
    /// a slot and one out. Any other parameter as Arg makes it.
    /// </summary>
    private VReg PackedArg(CallExpr call, MethodSymbol target, int i)
    {
        if (target.Params[i].ByRef || target.Params[i].Type.Symbol?.Name is not ("Packed64" or "Packed128")) return Arg(call, target, i);
        if (call.Args[i] is CallExpr { Target: MemberExpr { Target: NameExpr { Name: "Packed" }, Name: "Load" or "Load128" }, Args: [Expr from] }
            && call.Args.All(PackedPure))
            return ToWord(Eval(from));
        return Eval(call.Args[i]);
    }

    /// <summary>Whether a Packed64's eight bytes move through mm0 here (PackedMove): inside a packed block whose variant has MMX, in i386 code; elsewhere, and a Packed128's sixteen, a copy.</summary>
    private bool PackedMoveHere(bool wide)
        => !wide && _t.WordSize == 4 && _packedFeatures.Count > 0 && (_packedFeatures.Peek() & PackedOps.Mmx) != 0;

    /// <summary>Whether an operand's evaluation writes no memory: names, literals, arithmetic on them, conversions, and the packed operations themselves (each writes only its own result), but no store, fence, assignment, step or other call.</summary>
    private static bool PackedPure(Expr e) => e switch
    {
        LiteralExpr or NameExpr => true,
        BinaryExpr b => PackedPure(b.Left) && PackedPure(b.Right),
        UnaryExpr { Op: UnOp.Neg or UnOp.Not or UnOp.BitNot } u => PackedPure(u.Operand),
        CastExpr c => PackedPure(c.Operand),
        CallExpr { Target: MemberExpr { Target: NameExpr { Name: "Packed" }, Name: var op } } c
            => op is not ("Store" or "StoreNonTemporal" or "Fence") && c.Args.All(PackedPure),
        _ => false,
    };

    private VReg PackedIntrinsic(CallExpr call, MethodSymbol target)
    {
        string name = target.Name;
        Type returns = target.Returns;
        // SIXTEEN BYTES where any operand or the result is a Packed128: a
        // Store's is its last, after the address.
        bool wide = target.Params.Any(p => p.Type.Symbol?.Name == "Packed128")
            || returns.Symbol?.Name == "Packed128" || name == "Load128";
        int bytes = wide ? 16 : 8;
        switch (name)
        {
            case "Mmx":
            case "Sse":
            case "X87":
                return Fail(call, "Packed." + name + "() opens a packed block, as the resource of a using: using (Packed." + name + "()) { ... }");
            // The bytes as they are: no instruction, inside a block or out.
            case "FromBits":
            {
                VReg made = ResultBuffer(call, returns);
                _e.Store(RegOperand.Of(made), RegOperand.Of(ToI64(PackedArg(call, target, 0))), 0, 8);
                if (wide) _e.Store(RegOperand.Of(made), RegOperand.Of(ToI64(PackedArg(call, target, 1))), 8, 8);
                return made;
            }
            case "Load":
            case "Load128":
            {
                VReg made = ResultBuffer(call, returns);
                VReg from = ToWord(PackedArg(call, target, 0));
                if (PackedMoveHere(wide)) _e.Call(MachineIntrinsics.PackedMove, IrType.Void, R(made), R(from));
                else _e.Emit(Opcode.MemCopy, null, R(made), R(from), Imm(bytes, IrTypes.Word));
                return made;
            }
            case "Store":
            {
                VReg into = ToWord(PackedArg(call, target, 0)), value = PackedArg(call, target, 1);
                if (PackedMoveHere(wide)) _e.Call(MachineIntrinsics.PackedMove, IrType.Void, R(into), R(value));
                else _e.Emit(Opcode.MemCopy, null, R(into), R(value), Imm(bytes, IrTypes.Word));
                return Void();
            }
        }

        if (_packedFeatures.Count == 0)
            return Fail(call, "Packed." + name + " is a packed block's: it uses the packed registers, which only a packed block owns (using (Packed.Mmx()) { ... })");
        // What this variant may use: nothing in long mode, whose packed
        // operations are their scalar forms.
        int has = _t.WordSize == 4 ? _packedFeatures.Peek() : 0;
        // THE VARIANT RUNNING, as the features it may use (PackedOps' bits):
        // a constant in each, which says which one the processor got.
        if (name == "Features") return _e.Const(has, IrType.I32);
        bool mmx = (has & PackedOps.Mmx) != 0, ext = mmx && (has & PackedOps.MmxExtensions) != 0;
        bool sse = (has & PackedOps.Sse) != 0, sse2 = sse && (has & PackedOps.Sse2) != 0, amd = mmx && (has & PackedOps.ThreeDNow) != 0;

        switch (name)
        {
            // MOVNTQ and SFENCE, the MMX extensions': a plain store and
            // nothing where the block has none. A Packed128's is two MOVNTQ,
            // which want no alignment, as MOVNTDQ would.
            case "StoreNonTemporal":
            {
                VReg at = ToWord(PackedArg(call, target, 0)), value = PackedArg(call, target, 1);
                if (!ext)
                {
                    _e.Emit(Opcode.MemCopy, null, R(at), R(value), Imm(bytes, IrTypes.Word));
                    return Void();
                }
                _e.Call(MachineIntrinsics.PackedStoreNonTemporal, IrType.Void, R(at), R(value));
                if (wide) _e.Call(MachineIntrinsics.PackedStoreNonTemporal, IrType.Void, R(Plus(at, 8)), R(Plus(value, 8)));
                return Void();
            }
            case "Fence":
                if (ext) _e.Call(MachineIntrinsics.PackedFence, IrType.Void);
                return Void();
        }

        if (PackedOps.Binary.TryGetValue(name, out var binary))
        {
            VReg a = PackedArg(call, target, 0), b = PackedArg(call, target, 1);
            VReg made = ResultBuffer(call, returns);
            // PADDQ and PSUBQ are SSE2's, on the MMX registers too.
            bool quad = binary.Op is 0xD4 or 0xFB;
            if (wide && sse2)
                _e.Call(MachineIntrinsics.PackedWide(0x66, binary.Op), IrType.Void, R(made), R(a), R(b));
            else if (wide && !binary.Crosses)
            {
                Half64(call, binary.Op, binary.Ext, quad, mmx, ext, sse2, made, a, b, 0);
                Half64(call, binary.Op, binary.Ext, quad, mmx, ext, sse2, made, a, b, 8);
            }
            else if (wide)
                PackedScalar128(call, binary.Op, 0, made, a, b);
            else
                Half64(call, binary.Op, binary.Ext, quad, mmx, ext, sse2, made, a, b, 0);
            return made;
        }

        if (PackedOps.Shift.TryGetValue(name, out var shift))
        {
            if (PackedConstant(call, target, 1, 0, 255) is not int count) return Void();
            VReg a = PackedArg(call, target, 0);
            VReg made = ResultBuffer(call, returns);
            bool bytewise = shift.Sub is 7 or 3 && shift.Group == 0x73;
            if (wide && sse2)
                _e.Call(MachineIntrinsics.PackedWideShift(shift.Group, shift.Sub, count), IrType.Void, R(made), R(a));
            else if (bytewise)
                PackedScalar128(call, 0x73, shift.Sub << 8 | count, made, a, a);
            else
                for (int half = 0; half < bytes; half += 8)
                {
                    if (mmx)
                    {
                        _e.Call(MachineIntrinsics.PackedShift(shift.Group, shift.Sub, count), IrType.Void, R(Plus(made, half)), R(Plus(a, half)));
                        continue;
                    }
                    VReg answer = PackedScalarCall(call, "PackedScalarShift", _e.Const(shift.Group, IrType.I32), _e.Const(shift.Sub, IrType.I32),
                        _e.Const(count, IrType.I32), _e.Load(IrType.I64, a, half));
                    _e.Store(RegOperand.Of(made), RegOperand.Of(answer), half, 8);
                }
            return made;
        }

        if (PackedOps.Float.TryGetValue(name, out var flt) || name is "SqrtF32" or "IntToFloat32" or "FloatToInt32")
        {
            bool unary = name is "SqrtF32" or "IntToFloat32" or "FloatToInt32";
            VReg a = PackedArg(call, target, 0), b = unary ? a : PackedArg(call, target, 1);
            VReg made = ResultBuffer(call, returns);
            // SSE's form: MOVUPS or MOVLPS in, the operation, out. The
            // conversions are SSE2's (CVTDQ2PS, CVTTPS2DQ).
            bool conversion = name is "IntToFloat32" or "FloatToInt32";
            if (sse && (!conversion || sse2))
            {
                (byte prefix, byte op, int predicate) = name switch
                {
                    "SqrtF32" => ((byte)0, (byte)0x51, -1),
                    "IntToFloat32" => ((byte)0, (byte)0x5B, -1),
                    "FloatToInt32" => ((byte)0xF3, (byte)0x5B, -1),
                    _ => ((byte)0, flt.Sse, flt.Predicate),
                };
                _e.Call(MachineIntrinsics.PackedFloat(wide, prefix, op, predicate, unary), IrType.Void, R(made), R(a), R(b));
                return made;
            }
            // 3DNow!'s, where it has the operation exactly: a half each.
            // Not the conversions, which round and saturate otherwise than
            // SSE's (PackedOps.Float), nor a square root, which 3DNow! has
            // only as an estimate.
            byte suffix = unary ? (byte)0 : flt.ThreeDNow;
            bool swapped = !unary && flt.Swapped;
            for (int half = 0; half < bytes; half += 8)
            {
                if (amd && suffix != 0)
                {
                    VReg first = Plus(swapped ? b : a, half), second = Plus(swapped ? a : b, half);
                    _e.Call(MachineIntrinsics.Packed3DNow(suffix), IrType.Void, R(Plus(made, half)), R(first), R(second));
                    continue;
                }
                VReg answer = PackedScalarCall(call, "PackedScalarFloat", _e.Const(PackedOps.FloatCode(name), IrType.I32),
                    _e.Load(IrType.I64, a, half), _e.Load(IrType.I64, b, half));
                _e.Store(RegOperand.Of(made), RegOperand.Of(answer), half, 8);
            }
            return made;
        }

        switch (name)
        {
            case "Shuffle16":
            {
                if (PackedConstant(call, target, 1, 0, 255) is not int order) return Void();
                VReg a = PackedArg(call, target, 0);
                VReg made = ResultBuffer(call, returns);
                if (ext)
                {
                    _e.Call(MachineIntrinsics.PackedShuffle(order), IrType.Void, R(made), R(a));
                    return made;
                }
                VReg answer = PackedScalarCall(call, "PackedScalarShuffle", _e.Load(IrType.I64, a, 0), _e.Const(order, IrType.I32));
                _e.Store(RegOperand.Of(made), RegOperand.Of(answer), 0, 8);
                return made;
            }
            case "Shuffle32":
            case "ShuffleLow16":
            case "ShuffleHigh16":
            {
                if (PackedConstant(call, target, 1, 0, 255) is not int order) return Void();
                VReg a = PackedArg(call, target, 0);
                VReg made = ResultBuffer(call, returns);
                byte prefix = name switch { "Shuffle32" => 0x66, "ShuffleLow16" => 0xF2, _ => 0xF3 };
                if (sse2) _e.Call(MachineIntrinsics.PackedWideShuffle(prefix, order), IrType.Void, R(made), R(a));
                else PackedScalar128(call, 0x70, prefix << 8 | order, made, a, a);
                return made;
            }
            case "ShuffleF32":
            case "UnpackLowF32":
            case "UnpackHighF32":
            {
                int order = -1;
                if (name == "ShuffleF32")
                {
                    if (PackedConstant(call, target, 2, 0, 255) is not int given) return Void();
                    order = given;
                }
                VReg a = PackedArg(call, target, 0), b = PackedArg(call, target, 1);
                VReg made = ResultBuffer(call, returns);
                byte op = name switch { "ShuffleF32" => 0xC6, "UnpackLowF32" => 0x14, _ => 0x15 };
                if (sse) _e.Call(MachineIntrinsics.PackedFloat(true, 0, op, order, false), IrType.Void, R(made), R(a), R(b));
                else PackedScalar128(call, op, Math.Max(order, 0), made, a, b);
                return made;
            }
            case "Extract16":
            {
                if (PackedConstant(call, target, 1, 0, wide ? 7 : 3) is not int at) return Void();
                VReg a = PackedArg(call, target, 0);
                if (wide && sse2) return _e.Call(MachineIntrinsics.PackedWideExtract(at), IrType.I32, R(a))!;
                VReg half = Plus(a, at >= 4 ? 8 : 0);
                if (ext) return _e.Call(MachineIntrinsics.PackedExtract(at & 3), IrType.I32, R(half))!;
                return PackedScalarCall(call, "PackedScalarExtract", _e.Load(IrType.I64, half, 0), _e.Const(at & 3, IrType.I32));
            }
            case "Insert16":
            {
                if (PackedConstant(call, target, 2, 0, wide ? 7 : 3) is not int at) return Void();
                VReg a = PackedArg(call, target, 0);
                VReg value = ToI32(PackedArg(call, target, 1));
                VReg made = ResultBuffer(call, returns);
                if (wide && sse2)
                {
                    _e.Call(MachineIntrinsics.PackedWideInsert(at), IrType.Void, R(made), R(a), R(value));
                    return made;
                }
                // A Packed128's other half as it was, and this one's word put in.
                if (wide) _e.Emit(Opcode.MemCopy, null, R(made), R(a), Imm(16, IrTypes.Word));
                int half = at >= 4 ? 8 : 0;
                if (ext)
                {
                    _e.Call(MachineIntrinsics.PackedInsert(at & 3), IrType.Void, R(Plus(made, half)), R(Plus(a, half)), R(value));
                    return made;
                }
                VReg answer = PackedScalarCall(call, "PackedScalarInsert", _e.Load(IrType.I64, a, half), value, _e.Const(at & 3, IrType.I32));
                _e.Store(RegOperand.Of(made), RegOperand.Of(answer), half, 8);
                return made;
            }
        }
        return Fail(call, "Packed." + name + " is no packed operation this compiler knows");
    }

    /// <summary>
    /// One 64-bit half of an integer operation, at `half` bytes into the
    /// result and the operands: the MMX instruction where the variant has it
    /// (the extensions' where they are its; PADDQ and PSUBQ SSE2's), else the
    /// runtime's lanes (Runtime.PackedScalar).
    /// </summary>
    private void Half64(CallExpr call, byte op, bool extension, bool quad, bool mmx, bool ext, bool sse2, VReg made, VReg a, VReg b, int half)
    {
        if (mmx && (!extension || ext) && (!quad || sse2))
        {
            _e.Call(MachineIntrinsics.PackedOp(op), IrType.Void, R(Plus(made, half)), R(Plus(a, half)), R(Plus(b, half)));
            return;
        }
        VReg answer = PackedScalarCall(call, "PackedScalar", _e.Const(op, IrType.I32), _e.Load(IrType.I64, a, half), _e.Load(IrType.I64, b, half));
        _e.Store(RegOperand.Of(made), RegOperand.Of(answer), half, 8);
    }

    /// <summary>A Packed128 operation whose lanes cross its halves, one lane at a time (Runtime.PackedScalar128): the result's and the operands' addresses.</summary>
    private void PackedScalar128(CallExpr call, int op, int detail, VReg made, VReg a, VReg b)
    {
        MethodSymbol? scalar = RequireRuntime(call, "PackedScalar128", 5, "Packed's scalar form");
        if (scalar is null) return;
        _e.Call(CallLabel(scalar), IrType.Void, R(_e.Const(op, IrType.I32)), R(_e.Const(detail, IrType.I32)),
            R(AsParam(made, scalar.Params[2].Type)), R(AsParam(a, scalar.Params[3].Type)), R(AsParam(b, scalar.Params[4].Type)));
    }

    /// <summary>An address `bytes` further on, or the address itself.</summary>
    private VReg Plus(VReg address, int bytes) => bytes == 0 ? address : _e.Binary(Opcode.Add, address, bytes);

    /// <summary>A packed operation's constant argument -- a count, an order, a word's place -- which the instruction holds in its own bytes.</summary>
    private int? PackedConstant(CallExpr call, MethodSymbol target, int index, int least, int most)
    {
        if (LiteralInteger(call.Args[index]) is long v && v >= least && v <= most) return (int)v;
        Error(call, "Packed." + target.Name + "'s " + target.Params[index].Name + " must be an integer literal from " + least + " to " + most + ": the instruction holds it in its own bytes");
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
        MethodSymbol? scalar = RequireRuntime(call, routine, given.Length, "Packed's scalar form");
        if (scalar is null) return _e.Const(0, IrType.I64);
        List<Operand> args = new();
        for (int k = 0; k < given.Length; k++) args.Add(R(AsParam(given[k], scalar.Params[k].Type)));
        return _e.Call(CallLabel(scalar), IrTypes.Of(scalar.Returns), args.ToArray())!;
    }
}
