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
            case "KeepAlive":
                _e.Call(MachineIntrinsics.KeepAlive, IrType.Void, new RegOperand(Arg(call, target, 0)));
                return Void();
            case "PokeHalf":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 2);
                return Void();
            case "PokeWord":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 4);
                return Void();

            case "Word":
            {
                // THE WORD A VALUE IS HELD IN, whatever its type -- and a
                // floating-point value is held in its bits. Handed through as
                // it was, a double stayed a register of the other bank, and
                // the library's comparers (List<double>.IndexOf) compared two
                // of them with an integer instruction.
                VReg held = Arg(call, target, 0);
                if (held.Type == IrType.F64) return _e.Unary(Opcode.Bits, R(held), IrType.I64);
                if (held.Type == IrType.F32) return Widen(_e.Unary(Opcode.Bits, R(held), IrType.I32));
                return Widen(held);
            }
            case "FromWord":
                return ToWord(Arg(call, target, 0));
            case "CharsOf":
                // The string itself: its layout is a char[]'s.
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

            // ---- strings: a count, then that many UTF-16 code units -------------
            case "NewChars":
            {
                VReg count = ToI32(Arg(call, target, 0));
                CheckArrayCount(count, 2);
                VReg total = _e.Binary(Opcode.Add, WordOf(_e.Binary(Opcode.Mul, count, 2)), _t.ArrayHeaderBytes);
                VReg s = AllocateDynamic(call, total, leaf: true);
                _e.Store(R(s), new SymOperand(StringDescriptor(), _t.DescriptorBytes), 0, _t.WordSize);
                _e.Store(R(s), R(count), _t.ArrayCountOffset, 4);
                return s;
            }
            case "GetChar":
            {
                VReg s = Arg(call, target, 0);
                VReg at = WordOf(Arg(call, target, 1));
                VReg p = _e.Binary(Opcode.Add, s, _e.Binary(Opcode.Mul, at, 2));
                return Numbered(_e, _e.Load(IrType.I32, p, _t.ArrayHeaderBytes, 2, signed: false));
            }
            case "GetCharPair":
            {
                VReg s = Arg(call, target, 0);
                VReg at = WordOf(Arg(call, target, 1));
                VReg p = _e.Binary(Opcode.Add, s, _e.Binary(Opcode.Mul, at, 2));
                return Numbered(_e, _e.Load(IrType.I32, p, _t.ArrayHeaderBytes, 4, signed: false));
            }
            case "SetChar":
            {
                VReg s = Arg(call, target, 0);
                VReg at = WordOf(Arg(call, target, 1));
                VReg v = ToI32(Arg(call, target, 2));
                VReg p = _e.Binary(Opcode.Add, s, _e.Binary(Opcode.Mul, at, 2));
                _e.Store(R(p), R(v), _t.ArrayHeaderBytes, 2);
                return Void();
            }
            // A STRING'S CURSORS AND COUNT ARE IN CHARS, a byte array's in
            // bytes; the machine moves bytes either way.
            case "Copy":
            case "CopyNoOverlap":
            {
                int unit = target.Params[0].Type.Prim == Prim.String ? 2 : 1;
                VReg Scaled(VReg v) => unit == 1 ? v : _e.Binary(Opcode.Mul, v, unit);
                VReg dst = _e.Binary(Opcode.Add, Arg(call, target, 0), Scaled(WordOf(Arg(call, target, 1))));
                VReg src = _e.Binary(Opcode.Add, Arg(call, target, 2), Scaled(WordOf(Arg(call, target, 3))));
                VReg n = Scaled(WordOf(Arg(call, target, 4)));
                _e.Emit(Opcode.MemCopy, null, R(_e.Binary(Opcode.Add, dst, _t.ArrayHeaderBytes)),
                        R(_e.Binary(Opcode.Add, src, _t.ArrayHeaderBytes)), R(n));
                // CHARACTERS OR BYTES, NEVER AN ADDRESS (Instr.Number): read
                // as a copy of words that might be, every string a join was
                // handed held what its parts held, and a key a dictionary's
                // missing-key message was made from went with the exception
                // to where nobody follows -- the name a front end's node
                // looked up, and with it, merged over the node's class, the
                // tree (test 1200).
                _e.Block.Instrs[^1].Number = true;
                return Void();
            }
            // Two byte ranges equal or not, and in which order. Two ranges of a
            // string's code units are Runtime.StringEqual and StringCompare,
            // reached as fallbacks: they walk a pair of units at a time with
            // a 32-bit cursor, and ORDER over a string is the order of its
            // code units (.NET's ordinal comparison), which the bytes of a
            // little-endian unit do not have.
            case "CompareBytes":
            {
                MethodSymbol? cmp = RequireRuntime(call, "CompareBytes", 3, $"Sys.{name}");
                if (cmp is null)
                    return Void();
                VReg a = _e.Binary(Opcode.Add, _e.Binary(Opcode.Add, Arg(call, target, 0), WordOf(Arg(call, target, 1))), _t.ArrayHeaderBytes);
                VReg b = _e.Binary(Opcode.Add, _e.Binary(Opcode.Add, Arg(call, target, 2), WordOf(Arg(call, target, 3))), _t.ArrayHeaderBytes);
                VReg n = WordOf(Arg(call, target, 4));
                // The runtime declares these as longs; a word pushed where a
                // long is read leaves the callee with half of the next slot.
                return _e.Call(CallLabel(cmp), IrTypes.Of(cmp.Returns),
                               R(AsParam(a, cmp.Params[0].Type)), R(AsParam(b, cmp.Params[1].Type)), R(AsParam(n, cmp.Params[2].Type)))!;
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
            case "GetGs":
                // The selector GS holds now: on Linux, before the runtime
                // takes it, the C library's thread pointer.
                return Widen(_e.Call(MachineIntrinsics.GetGs, IrType.I32)!);
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
                VReg flags = Numbered(_e, _e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize));
                VReg bit = _e.Binary(Opcode.And, flags, 2);
                _e.CopyTo(result, R(_e.Binary(Opcode.Ne, R(bit), Imm(0, IrType.I32), IrType.I32)));
                _e.Jump(end);
                _e.SetBlock(end);
                return result;
            }
            case "IsObject":
            {
                // A T? IS ALWAYS ASKED THE KEY QUESTIONS (NullableKey), which
                // answer as EqualityComparer<T?> does -- two without a value
                // equal, one without a value hashing as 0. Asked only when it
                // had one, two empty values were compared by their addresses.
                if (_b.TypeOf(call.Args[0]).IsNullableValue)
                {
                    Eval(call.Args[0]);
                    return _e.Const(1, IrType.I32);
                }
                // A STRUCT IS ASKED as an object is -- KeyEquals, KeyHash and
                // KeyCompare below compare its value -- so the collections'
                // fallback to comparing words never takes its block's address.
                if (IsStructValue(_b.TypeOf(call.Args[0])))
                {
                    Eval(call.Args[0]);
                    return _e.Const(1, IrType.I32);
                }
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
                if (NullableKey(call, "KeyEquals") is VReg nullableKey) return nullableKey;
                if (IsStructValue(_b.TypeOf(call.Args[0])) && IsStructValue(_b.TypeOf(call.Args[1])))
                {
                    VReg x = ToWord(Arg(call, target, 0)), y = ToWord(Arg(call, target, 1));
                    return _e.Call(StructEquals(StructOf(_b.TypeOf(call.Args[0]))), IrType.I32, R(x), R(y))!;
                }
                if (!CouldBeObject(_b.TypeOf(call.Args[0])) || !CouldBeObject(_b.TypeOf(call.Args[1])))
                {
                    Eval(call.Args[0]);
                    Eval(call.Args[1]);
                    return _e.Const(0, IrType.I32);
                }
                VReg one = ToWord(Arg(call, target, 0));
                VReg two = ToWord(Arg(call, target, 1));
                return _e.Call(KeyEqualsStub(_b.TypeOf(call.Args[0])), IrType.I32, R(one), R(two))!;
            }
            case "EqualValues":
            {
                VReg x = Arg(call, target, 0), y = Arg(call, target, 1);
                if (x.Type is IrType.F32 or IrType.F64)
                {
                    // Equal as numbers (0 and -0 alike), or both NaN.
                    VReg same = _e.Binary(Opcode.FEq, R(x), R(y), IrType.I32);
                    VReg bothNaN = _e.Binary(Opcode.And, R(_e.Binary(Opcode.FNe, R(x), R(x), IrType.I32)),
                                             R(_e.Binary(Opcode.FNe, R(y), R(y), IrType.I32)), IrType.I32);
                    return _e.Binary(Opcode.Or, R(same), R(bothNaN), IrType.I32);
                }
                VReg wx = Widen(x), wy = Widen(y);
                return _e.Binary(Opcode.Eq, R(wx), R(wy), IrType.I32);
            }
            case "HashValue":
            {
                VReg held = Arg(call, target, 0);
                if (held.Type is IrType.F32 or IrType.F64)
                {
                    // Every zero hashes as 0 and every NaN alike, as they are equal.
                    bool wide = held.Type == IrType.F64;
                    VReg bits = wide ? _e.Unary(Opcode.Bits, R(held), IrType.I64) : Widen(_e.Unary(Opcode.Bits, R(held), IrType.I32));
                    VReg zero = _e.Unary(Opcode.IToF, R(_e.Const(0, IrType.I32)), held.Type);
                    VReg isZero = _e.Binary(Opcode.FEq, R(held), R(zero), IrType.I32);
                    VReg isNaN = _e.Binary(Opcode.FNe, R(held), R(held), IrType.I32);
                    VReg result = _f.NewReg(IrType.I64, "hashv");
                    Block nan = _f.NewBlock("hvnan"), number = _f.NewBlock("hvnum"), zeroed = _f.NewBlock("hvzero"), end = _f.NewBlock("hvend");
                    _e.Branch(isNaN, nan, number);
                    _e.SetBlock(nan);
                    _e.CopyTo(result, Imm(0x7FF8000000000000, IrType.I64));
                    _e.Jump(end);
                    _e.SetBlock(number);
                    _e.CopyTo(result, R(bits.Type == IrType.I64 ? bits : _e.Unary(Opcode.ZExt32, R(bits), IrType.I64)));
                    _e.Branch(isZero, zeroed, end);
                    _e.SetBlock(zeroed);
                    _e.CopyTo(result, Imm(0, IrType.I64));
                    _e.Jump(end);
                    _e.SetBlock(end);
                    return Widen(result);
                }
                return Widen(held);
            }
            case "CompareValues":
            {
                Type of = _b.TypeOf(call.Args[0]);
                VReg x = Eval(call.Args[0]), y = Eval(call.Args[1]);
                if (of.IsFloat)
                {
                    VReg below = _e.Binary(Opcode.FLt, R(x), R(y), IrType.I32);
                    VReg above = _e.Binary(Opcode.FGt, R(x), R(y), IrType.I32);
                    VReg same = _e.Binary(Opcode.FEq, R(x), R(y), IrType.I32);
                    // Unordered: NaN is below every number and equal to itself.
                    VReg xNaN = _e.Binary(Opcode.FNe, R(x), R(x), IrType.I32);
                    VReg yNaN = _e.Binary(Opcode.FNe, R(y), R(y), IrType.I32);
                    VReg ordered = _e.Binary(Opcode.Or, R(_e.Binary(Opcode.Or, R(below), R(above), IrType.I32)), R(same), IrType.I32);
                    VReg unordered = _e.Binary(Opcode.Xor, R(ordered), Imm(1, IrType.I32), IrType.I32);
                    // x NaN: 0 when y is too, else -1; y alone NaN: 1.
                    VReg xNumber = _e.Binary(Opcode.Xor, R(xNaN), Imm(1, IrType.I32), IrType.I32);
                    VReg yNumber = _e.Binary(Opcode.Xor, R(yNaN), Imm(1, IrType.I32), IrType.I32);
                    VReg nanOrder = _e.Binary(Opcode.Sub, R(xNumber), R(_e.Binary(Opcode.And, R(xNaN), R(yNumber), IrType.I32)), IrType.I32);
                    VReg plain = _e.Binary(Opcode.Sub, R(above), R(below), IrType.I32);
                    return _e.Binary(Opcode.Add, R(plain), R(_e.Binary(Opcode.Mul, R(unordered), R(nanOrder), IrType.I32)), IrType.I32);
                }
                bool unsigned = of.IsEnumValue && of.Symbol is { } named
                    ? named.EnumUnderlying is Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64 or Prim.Char or Prim.NUInt
                    : of.IsUnsigned || of.Prim == Prim.Bool;
                VReg less = _e.Binary(unsigned ? Opcode.LtU : Opcode.LtS, R(x), R(y), IrType.I32);
                VReg more = _e.Binary(unsigned ? Opcode.GtU : Opcode.GtS, R(x), R(y), IrType.I32);
                return _e.Binary(Opcode.Sub, R(more), R(less), IrType.I32);
            }
            case "KeyCompare":
            {
                if (NullableKey(call, "KeyCompare") is VReg nullableKey) return nullableKey;
                if (IsStructValue(_b.TypeOf(call.Args[0])) && IsStructValue(_b.TypeOf(call.Args[1])))
                {
                    TypeSymbol shape = StructOf(_b.TypeOf(call.Args[0]));
                    VReg x = ToWord(Arg(call, target, 0)), y = ToWord(Arg(call, target, 1));
                    bool boxed;
                    MethodSymbol? order = StructCompareTo(shape, out boxed);
                    if (order is null) return _e.Const(0, IrType.I32);
                    Require(order);
                    VReg other = boxed ? Box(call, y, new Type { Symbol = shape }) : y;
                    VReg said = CallDirect(order, IrTypes.Of(order.Returns), new List<Operand> { R(x), R(other) })!;
                    return said.Type == IrType.I32 ? said : _e.Unary(Opcode.Trunc64, R(said), IrType.I32);
                }
                if (!CouldBeObject(_b.TypeOf(call.Args[0])) || !CouldBeObject(_b.TypeOf(call.Args[1])))
                {
                    Eval(call.Args[0]);
                    Eval(call.Args[1]);
                    return _e.Const(0, IrType.I32);
                }
                VReg before = ToWord(Arg(call, target, 0));
                VReg after = ToWord(Arg(call, target, 1));
                return _e.Call(KeyCompareStub(_b.TypeOf(call.Args[0])), IrType.I32, R(before), R(after))!;
            }
            case "KeyHash":
            {
                if (NullableKey(call, "KeyHash") is VReg nullableKey) return nullableKey;
                if (IsStructValue(_b.TypeOf(call.Args[0])))
                    return _e.Call(StructHash(StructOf(_b.TypeOf(call.Args[0]))), IrType.I32, R(ToWord(Arg(call, target, 0))))!;
                if (!CouldBeObject(_b.TypeOf(call.Args[0])))
                {
                    Eval(call.Args[0]);
                    return _e.Const(0, IrType.I32);
                }
                return _e.Call(KeyHashStub(_b.TypeOf(call.Args[0])), IrType.I32, R(ToWord(Arg(call, target, 0))))!;
            }
            case "ArrayData":
            case "StringData":
            {
                // A STRING IS NOT ITS BYTES. Its elements are UTF-16 code units,
                // and every place that took a string's "array data" was handing
                // bytes to something that reads bytes -- a path, a write. That is
                // an encoding (Utf8Transcoder), or, for code that wants the units
                // themselves, StringData, which says so.
                if (name == "ArrayData" && _b.TypeOf(call.Args[0]).Prim == Prim.String)
                {
                    Error(call, "Sys.ArrayData of a string: its elements are UTF-16 code units, not bytes; "
                              + "encode it (Utf8Transcoder.Encode) for bytes, or use Sys.StringData for the units");
                }
                VReg arr = ToWord(Arg(call, target, 0));
                Addressed(arr);
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
                // A control register is a word wide -- 32 bits on i386, 64 in
                // long mode, where CR3 holds a physical address above 4 GiB --
                // and none of them is a signed quantity, so the long the
                // language sees is the zero-extension.
                return Widen(_e.Call(MachineIntrinsics.ReadCr, IrTypes.Word, R(ToI32(Arg(call, target, 0))))!);
            case "WriteCr":
                _e.Call(MachineIntrinsics.WriteCr, IrType.Void,
                        R(ToI32(Arg(call, target, 0))), R(ToWord(Arg(call, target, 1))));
                return Void();
            case "LoadSegments":
                _e.Call(MachineIntrinsics.LoadSegments, IrType.Void, R(ToI32(Arg(call, target, 0))));
                return Void();
            case "LoadCodeSegment":
                _e.Call(MachineIntrinsics.LoadCodeSegment, IrType.Void, R(ToI32(Arg(call, target, 0))));
                return Void();
            case "LoadTaskRegister":
                _e.Call(MachineIntrinsics.LoadTaskRegister, IrType.Void, R(ToI32(Arg(call, target, 0))));
                return Void();
            case "ReadMsr":
                return _e.Call(MachineIntrinsics.ReadMsr, IrType.I64, R(ToI32(Arg(call, target, 0))))!;
            case "WriteMsr":
                _e.Call(MachineIntrinsics.WriteMsr, IrType.Void, R(ToI32(Arg(call, target, 0))), R(Arg(call, target, 1)));
                return Void();
            case "Cpuid":
                _e.Call(MachineIntrinsics.Cpuid, IrType.Void, R(ToI32(Arg(call, target, 0))), R(ToI32(Arg(call, target, 1))),
                        R(ToWord(Arg(call, target, 2))));
                return Void();
            case "ReadTsc":
                return _e.Call(MachineIntrinsics.ReadTsc, IrType.I64)!;
            case "SwapGs":
                _e.Call(MachineIntrinsics.SwapGs, IrType.Void);
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
                int vector = target.Name == "GuiCall" ? 0x81 : target.Name == "GuiService" ? 0x82 : Ring1Syscalls ? 0x83 : 0x80;
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
                // AS ANY CALL OF THIS LANGUAGE'S: its arguments are longs, as
                // Sys.Call declares them and as every method it reaches
                // declares its own -- two words each on i386, as the kernel's
                // entry code pushes them for Arch.TrapEntry. Words, they were
                // read two to a long: Platform.Unmap's hook took the address
                // and the length together as its address and none as its
                // length, and a futex hook compared every wait against zero.
                // Code with the machine's own convention -- a word an argument
                // -- is reached by Sys.CallNative.
                VReg fn = Address(call, target, 0);
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(Arg(call, target, i)));
                }
                return _e.CallIndirect(R(fn), IrType.I64, args)!;
            }
            case "CallNative":
            {
                VReg fn = Address(call, target, 0);
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(ToWord(Arg(call, target, i))));
                }
                return Widen(_e.CallIndirect(R(fn), word, args, NativeCall.Indirect)!);
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
        MethodSymbol? rt = RuntimeMethod(target.Name, target.Params.Count, target.Params);
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

    /// <summary>
    /// THE KEY QUESTIONS ASKED OF A T?, as .NET's comparers answer them: no
    /// value equals no value and nothing else, hashes to zero and sorts first;
    /// two values are compared as T would be. The cell's own address -- which
    /// is what the word of a T? is -- was what these compared, so two equal
    /// nullable structs were two keys, and the linker's records, which hold a
    /// Definition?, never equalled their copies. Null when the arguments are
    /// not nullable values.
    /// </summary>
    private VReg? NullableKey(CallExpr call, string question)
    {
        Type held = _b.TypeOf(call.Args[0]);
        bool pair = question != "KeyHash";
        if (!held.IsNullableValue || pair && !_b.TypeOf(call.Args[1]).IsNullableValue) return null;
        Type value = held.Underlying;
        VReg a = ToWord(Eval(call.Args[0]));
        VReg? b = pair ? ToWord(Eval(call.Args[1])) : null;
        VReg result = _f.NewReg(IrType.I32, "nkey");
        Block both = _f.NewBlock("nkboth"), end = _f.NewBlock("nkend");

        if (!pair)
        {
            _e.CopyTo(result, Imm(0, IrType.I32));
            _e.Branch(HasValue(a), both, end);
            _e.SetBlock(both);
            VReg one = NullableRead(a, held);
            _e.CopyTo(result, R(KeyOfValue("KeyHash", one, null, value)));
            _e.Jump(end);
            _e.SetBlock(end);
            return result;
        }

        // Which of the two have a value: 0 for neither, and the answer when
        // only one has is decided without looking any further.
        VReg hasA = HasValue(a);
        VReg hasB = HasValue(b!);
        Block oneSide = _f.NewBlock("nkone");
        _e.CopyTo(result, Imm(question == "KeyEquals" ? 1 : 0, IrType.I32));
        VReg eitherHas = _e.Binary(Opcode.Or, R(hasA), R(hasB), IrType.I32);
        Block some = _f.NewBlock("nksome");
        _e.Branch(eitherHas, some, end);
        _e.SetBlock(some);
        VReg bothHave = _e.Binary(Opcode.And, R(hasA), R(hasB), IrType.I32);
        _e.Branch(bothHave, both, oneSide);
        _e.SetBlock(oneSide);
        // One has no value: not equal; the one without sorts first.
        if (question == "KeyEquals") _e.CopyTo(result, Imm(0, IrType.I32));
        else _e.CopyTo(result, R(_e.Binary(Opcode.Sub, R(hasA), R(hasB), IrType.I32)));
        _e.Jump(end);
        _e.SetBlock(both);
        VReg x = NullableRead(a, held);
        VReg y = NullableRead(b, held);
        _e.CopyTo(result, R(KeyOfValue(question, x, y, value)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>A key question asked of two values (one for a hash) of a type that is not nullable.</summary>
    private VReg KeyOfValue(string question, VReg x, VReg? y, Type value)
    {
        if (IsStructValue(value))
        {
            TypeSymbol shape = StructOf(value);
            switch (question)
            {
                case "KeyEquals":
                    return _e.Call(StructEquals(shape), IrType.I32, R(ToWord(x)), R(ToWord(y!)))!;
                case "KeyHash":
                    return _e.Call(StructHash(shape), IrType.I32, R(ToWord(x)))!;
                default:
                {
                    MethodSymbol? order = StructCompareTo(shape, out bool boxed);
                    if (order is null) return _e.Const(0, IrType.I32);
                    Require(order);
                    VReg other = boxed ? Box(null!, ToWord(y!), new Type { Symbol = shape }) : ToWord(y!);
                    VReg said = CallDirect(order, IrTypes.Of(order.Returns), new List<Operand> { R(ToWord(x)), R(other) })!;
                    return said.Type == IrType.I32 ? said : _e.Unary(Opcode.Trunc64, R(said), IrType.I32);
                }
            }
        }
        if (CouldBeObject(value))
        {
            return question switch
            {
                "KeyEquals" => _e.Call(KeyEqualsStub(value), IrType.I32, R(ToWord(x)), R(ToWord(y!)))!,
                "KeyHash" => _e.Call(KeyHashStub(value), IrType.I32, R(ToWord(x)))!,
                _ => _e.Call(KeyCompareStub(value), IrType.I32, R(ToWord(x)), R(ToWord(y!)))!,
            };
        }
        // A number (or an enum, a bool, a char): its value, as the comparers
        // treat a plain T -- equal words, the word as the hash, and the order
        // of the values.
        VReg wx = x.Type == IrType.I64 ? x : _e.Unary((!value.IsUnsigned && value.Prim != Prim.Bool) ? Opcode.SExt32 : Opcode.ZExt32, R(x), IrType.I64);
        if (question == "KeyHash") return wx.Type == IrType.I32 ? wx : _e.Unary(Opcode.Trunc64, R(wx), IrType.I32);
        VReg wy = y!.Type == IrType.I64 ? y : _e.Unary((!value.IsUnsigned && value.Prim != Prim.Bool) ? Opcode.SExt32 : Opcode.ZExt32, R(y), IrType.I64);
        if (question == "KeyEquals") return _e.Binary(Opcode.Eq, R(wx), R(wy), IrType.I32);
        VReg less = _e.Binary((!value.IsUnsigned && value.Prim != Prim.Bool) ? Opcode.LtS : Opcode.LtU, R(wx), R(wy), IrType.I32);
        VReg more = _e.Binary((!value.IsUnsigned && value.Prim != Prim.Bool) ? Opcode.GtS : Opcode.GtU, R(wx), R(wy), IrType.I32);
        return _e.Binary(Opcode.Sub, R(more), R(less), IrType.I32);
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
