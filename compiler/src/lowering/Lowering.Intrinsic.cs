#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    /// <summary>
    /// The prelude's intrinsics: what the language offers to reach the
    /// machine. Each is either a few IR instructions here, or a call into
    /// the runtime library under the same name and signature -- which is the
    /// rule for anything that was an instruction on the old machine and is a
    /// routine on this one. An intrinsic with neither is an error naming the
    /// runtime method that would satisfy it, so a port knows what to write.
    /// </summary>
    private VReg EmitIntrinsic(CallExpr call, MethodSymbol target)
    {
        // A GENERIC INTRINSIC is reached through its specialised copy --
        // `Sys.AddressOf<T>(ref T)` becomes `AddressOf$Box?$305` -- and it is
        // the same instruction whatever T is, so it is known by the name
        // before the first '$'. No intrinsic's own name has one.
        string name = target.Name;
        if (name.IndexOf('$') is int cut and > 0)
        {
            name = name[..cut];
        }
        IrType word = IrTypes.Word;
        IrType returns = IrTypes.Of(target.Returns);

        if (target.Owner.Name == Prelude.MachineType)
        {
            return Fail(call, $"'{Prelude.MachineType}.{name}' is an instruction of the original CORSAC processor and does not exist on {_t.Name}");
        }

        if (target.Owner.Name == Prelude.MathType)
        {
            switch (name)
            {
                case "Sqrt":
                    return _e.Unary(Opcode.FSqrt, Arg(call, target, 0));
                case "Abs":
                {
                    VReg x = Arg(call, target, 0);
                    VReg bits = _e.Unary(Opcode.Bits, R(x), IrType.I64);
                    VReg masked = _e.Binary(Opcode.And, bits, 0x7FFF_FFFF_FFFF_FFFF);
                    return _e.Unary(Opcode.Bits, R(masked), IrType.F64);
                }
                default:
                    return RuntimeFallback(call, target);
            }
        }

        switch (name)
        {
            case "Rethrow":
                Rethrow(Arg(call, target, 0), call);
                return Void();
            // ---- memory as words ------------------------------------------------
            case "Peek":
            case "PeekWord" when _t.WordSize == 4:
                return Widen(_e.Load(word, Address(call, target, 0), 0));
            case "PeekByte":
                return Widen(_e.Load(IrType.I32, Address(call, target, 0), 0, 1, signed: false));
            case "PeekHalf":
                return Widen(_e.Load(IrType.I32, Address(call, target, 0), 0, 2, signed: false));
            case "PeekWord":
                return Widen(_e.Load(IrType.I32, Address(call, target, 0), 0, 4, signed: false));

            case "Poke":
            case "PokeWord" when _t.WordSize == 4:
                _e.Store(R(Address(call, target, 0)), R(ToWord(Arg(call, target, 1))), 0, _t.WordSize);
                return Void();
            case "PokeByte":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 1);
                return Void();
            case "PokeHalf":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 2);
                return Void();
            case "PokeWord":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 4);
                return Void();

            case "Word":
                return Widen(Arg(call, target, 0));
            case "FromWord":
                return ToWord(Arg(call, target, 0));

            case "Bits":
                return _e.Unary(Opcode.Bits, R(Arg(call, target, 0)), IrType.I64);
            case "FromBits":
                return _e.Unary(Opcode.Bits, R(Arg(call, target, 0)), IrType.F64);
            case "SingleBits":
                return _e.Unary(Opcode.Bits, R(Arg(call, target, 0)), IrType.I32);
            case "FromSingleBits":
                return _e.Unary(Opcode.Bits, R(ToI32(Arg(call, target, 0))), IrType.F32);

            case "MemoryCopy":
            {
                VReg dst = Address(call, target, 0);
                VReg src = Address(call, target, 1);
                VReg n = ToWord(Arg(call, target, 2));
                _e.Emit(Opcode.MemCopy, null, R(dst), R(src), R(n));
                return Void();
            }
            case "MemorySet":
            {
                VReg dst = Address(call, target, 0);
                VReg v = ToI32(Arg(call, target, 1));
                VReg n = ToWord(Arg(call, target, 2));
                _e.Emit(Opcode.MemSet, null, R(dst), R(v), R(n));
                return Void();
            }

            case "Allocate":
                return Widen(AllocateDynamic(call, ToWord(Arg(call, target, 0))));

            // ---- strings and byte arrays: a count then the bytes ---------------
            case "NewBytes":
            {
                VReg count = ToI32(Arg(call, target, 0));
                VReg total = _e.Binary(Opcode.Add, WordOf(count), _t.ArrayHeaderBytes);
                VReg s = AllocateDynamic(call, total);
                _e.Store(R(s), new SymOperand(SequenceDescriptor("byte", 1, isString: true), _t.DescriptorBytes), 0, _t.WordSize);
                _e.Store(R(s), R(count), _t.ArrayCountOffset, 4);
                return s;
            }
            case "GetByte":
            {
                VReg s = Arg(call, target, 0);
                VReg at = WordOf(Arg(call, target, 1));
                VReg p = _e.Binary(Opcode.Add, s, at);
                return _e.Load(IrType.I32, p, _t.ArrayHeaderBytes, 1, signed: false);
            }
            case "SetByte":
            {
                VReg s = Arg(call, target, 0);
                VReg at = WordOf(Arg(call, target, 1));
                VReg v = ToI32(Arg(call, target, 2));
                VReg p = _e.Binary(Opcode.Add, s, at);
                _e.Store(R(p), R(v), _t.ArrayHeaderBytes, 1);
                return Void();
            }
            case "Copy":
            case "CopyNoOverlap":
            {
                VReg dst = _e.Binary(Opcode.Add, Arg(call, target, 0), WordOf(Arg(call, target, 1)));
                VReg src = _e.Binary(Opcode.Add, Arg(call, target, 2), WordOf(Arg(call, target, 3)));
                VReg n = WordOf(Arg(call, target, 4));
                _e.Emit(Opcode.MemCopy, null, R(_e.Binary(Opcode.Add, dst, _t.ArrayHeaderBytes)),
                        R(_e.Binary(Opcode.Add, src, _t.ArrayHeaderBytes)), R(n));
                return Void();
            }
            case "CompareBytes":
            case "StringCompare":
            case "StringEqual":
            {
                MethodSymbol? cmp = RequireRuntime(call, "CompareBytes", 3, $"Sys.{name}");
                if (cmp is null)
                    return Void();
                VReg a = _e.Binary(Opcode.Add, _e.Binary(Opcode.Add, Arg(call, target, 0), WordOf(Arg(call, target, 1))), _t.ArrayHeaderBytes);
                VReg b = _e.Binary(Opcode.Add, _e.Binary(Opcode.Add, Arg(call, target, 2), WordOf(Arg(call, target, 3))), _t.ArrayHeaderBytes);
                VReg n = WordOf(Arg(call, target, 4));
                // The runtime declares these as longs; a word pushed where a
                // long is read leaves the callee with half of the next slot.
                VReg r = _e.Call(CallLabel(cmp), IrTypes.Of(cmp.Returns),
                                 R(AsParam(a, cmp.Params[0].Type)), R(AsParam(b, cmp.Params[1].Type)), R(AsParam(n, cmp.Params[2].Type)))!;
                if (name == "StringEqual")
                {
                    return _e.Binary(Opcode.Eq, R(r), Imm(0, r.Type), IrType.I32);
                }
                return r;
            }

            // ---- atomics -------------------------------------------------------------
            case "Cas":
            case "CasRelaxed":
            case "CasAcquire":
            case "CasRelease":
            case "CasSequential":
            {
                VReg at = Address(call, target, 0);
                VReg expect = ToWord(Arg(call, target, 1));
                VReg value = ToWord(Arg(call, target, 2));
                VReg old = _e.Reg(word);
                _e.Emit(Opcode.AtomicCas, old, R(at), R(expect), R(value));
                return Widen(old);
            }
            case "AtomicAdd" or "AtomicAddRelaxed" or "AtomicAddAcquire" or "AtomicAddRelease" or "AtomicAddSequential":
                return Atomic(call, target, Opcode.AtomicAdd);
            case "AtomicAnd" or "AtomicAndRelaxed" or "AtomicAndAcquire" or "AtomicAndRelease" or "AtomicAndSequential":
                return Atomic(call, target, Opcode.AtomicAnd);
            case "AtomicOr" or "AtomicOrRelaxed" or "AtomicOrAcquire" or "AtomicOrRelease" or "AtomicOrSequential":
                return Atomic(call, target, Opcode.AtomicOr);
            case "AtomicXor" or "AtomicXorRelaxed" or "AtomicXorAcquire" or "AtomicXorRelease" or "AtomicXorSequential":
                return Atomic(call, target, Opcode.AtomicXor);
            case "AtomicSwap":
                return Atomic(call, target, Opcode.AtomicSwap);

            case "Fence":
                _e.Emit(Opcode.Fence, null);
                return Void();
            case "Pause":
                _e.Emit(Opcode.Pause, null);
                return Void();
            case "Breakpoint":
                _e.Emit(Opcode.Trap, null);
                return Void();

            case "Stack":
            {
                VReg sp = _e.Reg(word, "sp");
                _e.Emit(Opcode.StackPointer, sp);
                return Widen(sp);
            }
            case "EntryStack":
                // The top of THIS thread's stack: the stack pointer the loader
                // left on the main thread, and where the child began on any
                // other. It is what a stack scan ends at, and on the main
                // thread also where argc and argv are.
                return Widen(_e.Load(word, ThreadBlockNow(), TlsStackBase / 4 * _t.WordSize));
            case "FrameTable":
                return Widen(_e.Address(Corsac.Lang.X86.FrameTable.Symbol));
            case "FrameDirectory":
                return Widen(_e.Address(ManagedDirectory.Symbol));
            case "FramePointer":
            {
                VReg fp = _e.Reg(word, "fp");
                _e.Emit(Opcode.FramePointer, fp);
                return Widen(fp);
            }
            case "ThreadBlock":
                return Widen(ThreadBlockNow());
            case "MainThreadBlock":
                return Widen(_e.Address(ThreadBlock0));
            case "SetThreadBlock":
                // The fallback for a target with no segment of its own: the
                // word that ThreadBlock reads when there is no GS.
                _e.Store(new SymOperand(ThreadBlockSelf), R(ToWord(Arg(call, target, 0))));
                return Void();
            case "SetGs":
                // The selector of the descriptor whose base is the block. Only
                // an operating system that made that descriptor may say this.
                _e.Call(MachineIntrinsics.SetGs, IrType.Void, R(ToI32(Arg(call, target, 0))));
                return Void();
            case "IsString":
            {
                // A value type is never a string, and a load through it would
                // read a number as a pointer: decided here, from the static type.
                Type t = _b.TypeOf(call.Args[0]);
                bool couldBe = t.Prim is Prim.String or Prim.Any or Prim.NullLiteral || t.ParamName is not null
                            || t.Symbol is { Kind: TypeKind.Class or TypeKind.Interface } || t.IsArray;
                if (!couldBe)
                {
                    Eval(call.Args[0]);
                    return _e.Const(0, IrType.I32);
                }
                VReg obj = Eval(call.Args[0]);
                VReg result = _f.NewReg(IrType.I32, "isstr");
                Block some = _f.NewBlock("issome");
                Block end = _f.NewBlock("isend");
                _e.CopyTo(result, Imm(0, IrType.I32));
                _e.Branch(obj, some, end);
                _e.SetBlock(some);
                VReg vt = _e.Load(word, obj, 0);
                VReg flags = _e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);
                VReg bit = _e.Binary(Opcode.And, flags, 2);
                _e.CopyTo(result, R(_e.Binary(Opcode.Ne, R(bit), Imm(0, IrType.I32), IrType.I32)));
                _e.Jump(end);
                _e.SetBlock(end);
                return result;
            }
            case "IsObject":
            {
                // Decided from the static type, as IsString is: a number is
                // never an object, and nothing may be read through it.
                if (!CouldBeObject(_b.TypeOf(call.Args[0])))
                {
                    Eval(call.Args[0]);
                    return _e.Const(0, IrType.I32);
                }
                VReg held = ToWord(Arg(call, target, 0));
                return _e.Binary(Opcode.Ne, R(held), Imm(0, held.Type), IrType.I32);
            }
            case "KeyEquals":
            {
                if (!CouldBeObject(_b.TypeOf(call.Args[0])) || !CouldBeObject(_b.TypeOf(call.Args[1])))
                {
                    Eval(call.Args[0]);
                    Eval(call.Args[1]);
                    return _e.Const(0, IrType.I32);
                }
                VReg one = ToWord(Arg(call, target, 0));
                VReg two = ToWord(Arg(call, target, 1));
                return _e.Call(KeyEqualsStub(), IrType.I32, R(one), R(two))!;
            }
            case "KeyCompare":
            {
                if (!CouldBeObject(_b.TypeOf(call.Args[0])) || !CouldBeObject(_b.TypeOf(call.Args[1])))
                {
                    Eval(call.Args[0]);
                    Eval(call.Args[1]);
                    return _e.Const(0, IrType.I32);
                }
                VReg before = ToWord(Arg(call, target, 0));
                VReg after = ToWord(Arg(call, target, 1));
                return _e.Call(KeyCompareStub(), IrType.I32, R(before), R(after))!;
            }
            case "KeyHash":
            {
                if (!CouldBeObject(_b.TypeOf(call.Args[0])))
                {
                    Eval(call.Args[0]);
                    return _e.Const(0, IrType.I32);
                }
                return _e.Call(KeyHashStub(), IrType.I32, R(ToWord(Arg(call, target, 0))))!;
            }
            case "ArrayData":
            {
                VReg arr = ToWord(Arg(call, target, 0));
                return Widen(_e.Binary(Opcode.Add, arr, _t.ArrayHeaderBytes));
            }
            case "SyncWord":
            {
                VReg obj = ToWord(Arg(call, target, 0));
                return Widen(_e.Binary(Opcode.Add, obj, _t.WordSize));
            }
            case "AsString":
                return ToWord(Arg(call, target, 0));
            case "DataStart":
                return Widen(_e.Address("__data_start"));
            case "DataEnd":
                return Widen(_e.Address("_end"));

            // ---- the I/O space and the privileged instructions -----------------
            //
            // Each of these is one machine instruction, and lowering names it
            // by a reserved callee rather than by a new IR opcode: everything
            // between here and instruction selection already treats a call as
            // something that may do anything, which is precisely the rule an
            // OUT to a device needs, and no pass has to learn a new opcode to
            // avoid deleting or duplicating it. The selector replaces the
            // call; no symbol of that name is ever emitted or called.
            case "PortIn8":
            case "PortIn16":
            case "PortIn32":
            {
                string which = name == "PortIn8" ? MachineIntrinsics.In8
                             : name == "PortIn16" ? MachineIntrinsics.In16
                             : MachineIntrinsics.In32;
                return _e.Call(which, IrType.I32, R(ToI32(Arg(call, target, 0))))!;
            }
            case "PortOut8":
            case "PortOut16":
            case "PortOut32":
            {
                string which = name == "PortOut8" ? MachineIntrinsics.Out8
                             : name == "PortOut16" ? MachineIntrinsics.Out16
                             : MachineIntrinsics.Out32;
                _e.Call(which, IrType.Void, R(ToI32(Arg(call, target, 0))), R(ToI32(Arg(call, target, 1))));
                return Void();
            }
            case "PortInString16":
            case "PortOutString16":
            {
                string which = name == "PortInString16" ? MachineIntrinsics.InString16 : MachineIntrinsics.OutString16;
                _e.Call(which, IrType.Void,
                        R(ToI32(Arg(call, target, 0))),
                        R(Address(call, target, 1)),
                        R(ToI32(Arg(call, target, 2))));
                return Void();
            }
            case "Cli":
            case "Sti":
            case "Hlt":
            {
                string which = name == "Cli" ? MachineIntrinsics.Cli : name == "Sti" ? MachineIntrinsics.Sti : MachineIntrinsics.Hlt;
                _e.Call(which, IrType.Void);
                return Void();
            }
            case "Lidt":
            case "Lgdt":
            case "Invlpg":
            {
                string which = name == "Lidt" ? MachineIntrinsics.Lidt
                             : name == "Lgdt" ? MachineIntrinsics.Lgdt
                             : MachineIntrinsics.Invlpg;
                _e.Call(which, IrType.Void, R(Address(call, target, 0)));
                return Void();
            }
            case "ReadCr":
                // A control register is 32 bits wide and none of them is a
                // signed quantity, so the long the language sees is the
                // zero-extension and not a sign-extension.
                return Widen(_e.Call(MachineIntrinsics.ReadCr, IrType.I32, R(ToI32(Arg(call, target, 0))))!);
            case "WriteCr":
                _e.Call(MachineIntrinsics.WriteCr, IrType.Void,
                        R(ToI32(Arg(call, target, 0))), R(ToI32(Arg(call, target, 1))));
                return Void();
            case "LoadSegments":
                _e.Call(MachineIntrinsics.LoadSegments, IrType.Void, R(ToI32(Arg(call, target, 0))));
                return Void();

            // ---- the operating system and function pointers --------------------
            case "Syscall":
            case "GuiCall":
            case "GuiService":
            {
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(ToWord(Arg(call, target, i))));
                }
                int vector = target.Name == "GuiCall" ? 0x81 : target.Name == "GuiService" ? 0x82 : 0x80;
                VReg r = _e.Syscall(R(ToWord(Arg(call, target, 0))), args, vector);
                return Widen(r);
            }
            case "NtCall":
            {
                // NT's convention: the arguments' address in EDX, which is the
                // third of Syscall's registers (EBX, ECX, EDX).
                VReg number = ToWord(Arg(call, target, 0));
                List<Operand> args = new() { Imm(0, number.Type), Imm(0, number.Type), R(ToWord(Arg(call, target, 1))) };
                VReg r = _e.Syscall(R(number), args, 0x2E);
                return Widen(r);
            }
            case "Call":
            {
                VReg fn = Address(call, target, 0);
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(ToWord(Arg(call, target, i))));
                }
                return Widen(_e.CallIndirect(R(fn), word, args)!);
            }
            case "AddressOf":
                return Widen(Arg(call, target, 0));

            // ---- small arithmetic the old machine had instructions for --------------
            case "Max":
            case "MinUnsigned":
            case "MaxUnsigned":
            {
                VReg a = Arg(call, target, 0);
                VReg b = Arg(call, target, 1);
                Opcode cmp = name == "Max" ? Opcode.GtS : name == "MaxUnsigned" ? Opcode.GtU : Opcode.LtU;
                VReg pick = _e.Binary(cmp, a, b);
                VReg r = _f.NewReg(a.Type, "sel");
                Block ya = _f.NewBlock("sela");
                Block yb = _f.NewBlock("selb");
                Block end = _f.NewBlock("selend");
                _e.Branch(pick, ya, yb);
                _e.SetBlock(ya);
                _e.CopyTo(r, R(a));
                _e.Jump(end);
                _e.SetBlock(yb);
                _e.CopyTo(r, R(b));
                _e.Jump(end);
                _e.SetBlock(end);
                return r;
            }
            case "SetBit":
            {
                VReg v = Arg(call, target, 0);
                VReg one = _e.Binary(Opcode.Shl, R(_e.Const(1, v.Type)), R(ToI32(Arg(call, target, 1))), v.Type);
                return _e.Binary(Opcode.Or, v, one);
            }
            case "ClearBit":
            {
                VReg v = Arg(call, target, 0);
                VReg one = _e.Binary(Opcode.Shl, R(_e.Const(1, v.Type)), R(ToI32(Arg(call, target, 1))), v.Type);
                return _e.Binary(Opcode.And, v, _e.Unary(Opcode.Not, one));
            }
            case "TestBit":
            {
                VReg v = Arg(call, target, 0);
                VReg shifted = _e.Binary(Opcode.ShrU, R(v), R(ToI32(Arg(call, target, 1))), v.Type);
                VReg bit = _e.Binary(Opcode.And, shifted, 1);
                return _e.Binary(Opcode.Ne, R(bit), Imm(0, bit.Type), IrType.I32);
            }
            case "LowBitMask":
            {
                // (1 << n) - 1, with n == 64 giving all ones: shift 1 left by
                // n-1, double it via or, then subtract one -- avoiding the
                // undefined 64-bit shift.
                VReg n = ToI32(Arg(call, target, 0));
                VReg nm1 = _e.Binary(Opcode.Sub, n, 1);
                VReg half = _e.Binary(Opcode.Shl, R(_e.Const(1, IrType.I64)), R(nm1), IrType.I64);
                VReg full = _e.Binary(Opcode.Sub, R(_e.Binary(Opcode.Shl, R(half), R(_e.Const(1, IrType.I32)), IrType.I64)), Imm(1, IrType.I64), IrType.I64);
                // n == 0 must give 0: half is 1<<-1 which the machine makes 1<<63; mask that case.
                VReg zero = _e.Binary(Opcode.Eq, R(n), Imm(0, IrType.I32), IrType.I32);
                VReg r = _f.NewReg(IrType.I64, "mask");
                Block z = _f.NewBlock("lbz");
                Block nz = _f.NewBlock("lbnz");
                Block end = _f.NewBlock("lbend");
                _e.Branch(zero, z, nz);
                _e.SetBlock(z);
                _e.CopyTo(r, Imm(0, IrType.I64));
                _e.Jump(end);
                _e.SetBlock(nz);
                _e.CopyTo(r, R(full));
                _e.Jump(end);
                _e.SetBlock(end);
                return r;
            }

            default:
                return RuntimeFallback(call, target);
        }
    }

    /// <summary>
    /// Anything without instructions here becomes a call to a runtime routine
    /// of the same name and shape. Print, the string routines, the bit tricks
    /// and the checksum all live in lib/sys as ordinary source.
    /// </summary>
    private VReg RuntimeFallback(CallExpr call, MethodSymbol target)
    {
        MethodSymbol? rt = RuntimeMethod(target.Name, target.Params.Count);
        if (rt is null)
        {
            Error(call, $"'{target.Owner.Name}.{target.Name}' is not an instruction on {_t.Name}; "
                      + $"it needs {RuntimeType}.{target.Name} with {target.Params.Count} parameter(s), which no compiled source provides");
            return Void();
        }
        Require(rt);
        List<Operand> args = new();
        for (int i = 0; i < call.Args.Count; i++)
        {
            ParamSymbol p = rt.Params[i];
            args.Add(R(EvalAs(call.Args[i], p)));
        }
        VReg? r = _e.Call(CallLabel(rt), IrTypes.Of(rt.Returns), args.ToArray());
        return r ?? Void();
    }

    private VReg Atomic(CallExpr call, MethodSymbol target, Opcode op)
    {
        VReg at = Address(call, target, 0);
        VReg value = ToWord(Arg(call, target, 1));
        VReg old = _e.Reg(IrTypes.Word);
        _e.Emit(op, old, R(at), R(value));
        return Widen(old);
    }

    private VReg Void() => _e.Const(0, IrTypes.Word);

    /// <summary>A machine value as a runtime routine's declared parameter: widened when the routine takes a long.</summary>
    private VReg AsParam(VReg v, Type param)
    {
        IrType want = IrTypes.Of(param);
        if (want == v.Type)
        {
            return v;
        }
        if (want == IrType.I64 && v.Type == IrType.I32)
        {
            return _e.Unary(param.IsUnsigned || v.Type == IrTypes.Word ? Opcode.ZExt32 : Opcode.SExt32, v);
        }
        if (want == IrType.I32 && v.Type == IrType.I64)
        {
            return _e.Unary(Opcode.Trunc64, v);
        }
        return v;
    }

    /// <summary>An intrinsic's argument, converted to the declared parameter type.</summary>
    private VReg Arg(CallExpr call, MethodSymbol target, int i)
    {
        // AN INTRINSIC'S `object` IS A MACHINE WORD, not System.Object.
        //
        // Sys.Word and Sys.IsString exist to look at the WORD a value is held
        // in whatever its type -- that is how the library writes its default
        // comparers -- and the prelude spells that parameter `object` because
        // a machine word is what object is here. A written `object x = 5` now
        // copies the five onto the heap, which is C#, and doing that to an
        // intrinsic's argument would hand it the box instead of the value.
        if (target.Params[i].Type.Prim == Prim.Any && !target.Params[i].ByRef)
        {
            return Eval(call.Args[i]);
        }
        return EvalAs(call.Args[i], target.Params[i]);
    }

    /// <summary>An argument that is an address: a long on the language's side, a word on the machine's.</summary>
    private VReg Address(CallExpr call, MethodSymbol target, int i) => ToWord(Arg(call, target, i));

    private VReg ToWord(VReg v)
    {
        if (v.Type == IrTypes.Word)
            return v;
        if (v.Type == IrType.I64)
            return _e.Unary(Opcode.Trunc64, v);
        if (v.Type == IrType.I32)
            return _e.Unary(Opcode.ZExt32, v);
        return v;
    }

    private VReg ToI32(VReg v) => v.Type == IrType.I64 ? _e.Unary(Opcode.Trunc64, v) : v;

    /// <summary>A machine word as the long the language hands back: zero-extended, an address is never negative.</summary>
    private VReg Widen(VReg v) => v.Type == IrType.I32 ? _e.Unary(Opcode.ZExt32, v) : v;
}
