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
            // The word as it is: no test, no conversion (Sys.As).
            case "As":
            {
                // THE WORD AT THE TYPE ASKED FOR. As changes the static type
                // and nothing else, but a value held in another register width
                // -- a long's two words, read as a string in a table whose key
                // is long, on a path the copy's IsString folds away -- came
                // back a register of the old width, and its copy into a word
                // was IR of two widths that only the folding kept from being
                // compiled. Narrowed or widened as a word is (ToWord).
                // A FLOAT'S BITS, AS THEY ARE, the same way: a double read as a
                // string in a table keyed by double came back the double's own
                // register, a call handed it for a string parameter -- IR of two
                // types the call check refuses (Verifier.CheckCalls), however
                // the copy's IsString folds the path away after. Reinterpreted
                // (Bits) and narrowed or widened as integers are.
                VReg value = Arg(call, target, 0);
                IrType wanted = IrTypes.Of(_b.TypeOf(call));
                return AsBits(value, wanted);
            }
            case "Rethrow":
                Rethrow(Arg(call, target, 0), call);
                return Void();
            // ---- memory: an address is a nuint, the target's word -----------------
            // BY WIDTH (LONG-MODE.md 7.2), answering exactly the width read. THE
            // WIDTH-NAMED READS ARE A DEVICE'S (Instr.Device): read every time
            // they are written, never answered by an earlier read. Read64 and
            // Write64 are two words on i386.
            case "Read8":
                return _e.Load(IrType.I32, Address(call, target, 0), 0, 1, signed: false, device: true);
            case "Read16":
                return _e.Load(IrType.I32, Address(call, target, 0), 0, 2, signed: false, device: true);
            case "Read32":
                return _e.Load(IrType.I32, Address(call, target, 0), 0, 4, signed: false, device: true);
            case "Read64":
                return _e.Load(IrType.I64, Address(call, target, 0), 0, 8, device: true);
            case "Write8":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 1);
                return Void();
            case "Write16":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 2);
                return Void();
            case "Write32":
                _e.Store(R(Address(call, target, 0)), R(ToI32(Arg(call, target, 1))), 0, 4);
                return Void();
            case "Write64":
                _e.Store(R(Address(call, target, 0)), R(ToI64(Arg(call, target, 1))), 0, 8);
                return Void();
            // A WORD, the collector's: an ordinary load and store.
            case "ReadAddress":
                return _e.Load(word, Address(call, target, 0), 0);
            case "WriteAddress":
                _e.Store(R(Address(call, target, 0)), R(ToWord(Arg(call, target, 1))), 0, _t.WordSize);
                return Void();
            case "Low32":
                return ToI32(Arg(call, target, 0));
            case "KeepAlive":
                _e.Call(MachineIntrinsics.KeepAlive, IrType.Void, RegOperand.Of(Arg(call, target, 0)));
                return Void();

            // WHERE A REFERENCE POINTS, and the reference at an address: its
            // word, unconverted (Word is the bits of any value as a long).
            // A FLOAT, WHERE A GENERIC IS MADE FOR ONE, is its bits: the branch
            // that frees an element is dead for a number (IsObject is false),
            // but is lowered, and its call takes a word, not a register of the
            // other bank (OwnedElements.Release<float>).
            case "ObjectAddress":
            case "ObjectAt":
            {
                VReg held = Arg(call, target, 0);
                if (held.Type == IrType.F64) held = _e.Unary(Opcode.Bits, R(held), IrType.I64);
                else if (held.Type == IrType.F32) held = _e.Unary(Opcode.Bits, R(held), IrType.I32);
                return ToWord(held);
            }

            case "Word":
            {
                // THE WORD A VALUE IS HELD IN, whatever its type -- and a
                // floating-point value is held in its bits. Handed through as
                // it was, a double stayed a register of the other bank, and
                // the library's comparers (List<double>.IndexOf) compared two
                // of them with an integer instruction.
                VReg held = Arg(call, target, 0);
                if (held.Type == IrType.F64) return _e.Unary(Opcode.Bits, R(held), IrType.I64);
                if (held.Type == IrType.F32) return Fit(_e.Unary(Opcode.Bits, R(held), IrType.I32), returns);
                return Fit(held, returns);
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
                return Fit(AllocateDynamic(call, ToWord(Arg(call, target, 0))), returns);

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
                VReg dstObject = Arg(call, target, 0);
                VReg dst = _e.Binary(Opcode.Add, dstObject, Scaled(WordOf(Arg(call, target, 1))));
                VReg srcObject = Arg(call, target, 2);
                VReg src = _e.Binary(Opcode.Add, srcObject, Scaled(WordOf(Arg(call, target, 3))));
                VReg n = Scaled(WordOf(Arg(call, target, 4)));
                VReg into = _e.Binary(Opcode.Add, dst, _t.ArrayHeaderBytes);
                VReg from = _e.Binary(Opcode.Add, src, _t.ArrayHeaderBytes);
                // Strings and byte arrays: never an address.
                MoveBytes(into, from, n, name == "Copy" ? _e.Binary(Opcode.Eq, dstObject, srcObject) : null, numbers: true);
                return Void();
            }
            // AN ADDRESS'S BYTES MOVED, overlapping or not (Sys.MemoryMove):
            // what Copy does to two arrays' elements, for a block an address
            // names -- Array.Copy's of elements holding no reference
            // (Span.Move). NumbersMove where every element is a number that
            // is never an address (Instr.Number: Sys.ElementsAreNumbers); a
            // long's, a nint's or a pointer's may be one, which the runtime
            // keeps addresses in, and region inference follows those. The
            // fourth, whether the two are in one block (one array).
            case "MemoryMove":
            case "NumbersMove":
            {
                VReg into = Address(call, target, 0);
                VReg from = Address(call, target, 1);
                VReg n = ToWord(Arg(call, target, 2));
                MoveBytes(into, from, n, ToI32(Arg(call, target, 3)), numbers: name == "NumbersMove");
                return Void();
            }
            // WHETHER AN ARRAY'S ELEMENTS HOLD A REFERENCE, a constant of the
            // array's static element type, as the collector reads the array:
            // its descriptor's flag (ElementsAreReferences). A type parameter
            // -- a shared copy's T -- may be one. Whether each is a number
            // that is never an address (ElementsAreNumbers: NeverAddress).
            // And the bytes from one element to the next (ElementStride), a
            // struct held in line padded as laid.
            case "ElementsHoldReferences":
            case "ElementsAreNumbers":
            case "ElementStride":
            {
                Type? element = target.Params[0].Type.Element;
                if (name == "ElementStride")
                    return _e.Const(element is null ? _t.WordSize : ElementStride(element), IrType.I32);
                if (name == "ElementsAreNumbers")
                    return Fit(_e.Const(element is not null && NeverAddress(element) ? 1 : 0, IrType.I32), returns);
                bool holds = element is null || element.ParamName is not null || element.Prim is Prim.Any
                    || (InlineElement(element) ? InlineHasReferences(StructOf(element)) : HoldsReference(element));
                return Fit(_e.Const(holds ? 1 : 0, IrType.I32), returns);
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
                EmitAtomic(Opcode.AtomicCas, old, R(at), R(expect), R(value));
                return Fit(old, returns);
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

            // AN INT'S FOUR BYTES, and no more: the same instructions at 32
            // bits (the destination's type is what the selector sizes them
            // by), so in long mode the field after the int is left alone. On
            // i386 these are the word forms exactly.
            case "Cas32":
            {
                VReg at = Address(call, target, 0);
                VReg expect = ToI32(Arg(call, target, 1));
                VReg value = ToI32(Arg(call, target, 2));
                VReg old = _e.Reg(IrType.I32);
                EmitAtomic(Opcode.AtomicCas, old, R(at), R(expect), R(value));
                return old;
            }
            // A LONG'S EIGHT BYTES compared and exchanged at once: in long
            // mode the word's own compare-exchange; on i386 lock cmpxchg8b,
            // which its caller reaches only after CPUID's CX8 bit
            // (Processor.Cas64): a 486 has no such instruction.
            case "Cas64":
            {
                VReg at = Address(call, target, 0);
                VReg expect = ToI64(Arg(call, target, 1));
                VReg value = ToI64(Arg(call, target, 2));
                if (_t.WordSize == 8)
                {
                    VReg old = _e.Reg(IrType.I64);
                    EmitAtomic(Opcode.AtomicCas, old, R(at), R(expect), R(value));
                    return old;
                }
                return _e.Call(MachineIntrinsics.Cas64, IrType.I64, R(at), R(expect), R(value))!;
            }
            case "AtomicAdd32":
                return Atomic32(call, target, Opcode.AtomicAdd);
            case "AtomicSwap32":
                return Atomic32(call, target, Opcode.AtomicSwap);

            // A REFERENCE EXCHANGED, with what every reference store takes:
            // the snapshot barrier and the card (Interlocked's reference
            // exchanges). As the image's sequence where stores are
            // (StoreSequences, X86Backend/X64Backend.RefStoreStub): the test, the
            // barrier, the atomic exchange and the card in a stub no thread
            // is stopped inside. Elsewhere the barrier's test before it, as
            // a field's store has it, and the card after, and before too
            // where cards are marked so (CardMarkBefore).
            case "ExchangeReference":
            case "CompareExchangeReference":
            {
                bool swap = name == "ExchangeReference";
                VReg at = Address(call, target, 0);
                VReg expect = swap ? at : ToWord(Arg(call, target, 1));
                VReg value = ToWord(Arg(call, target, swap ? 1 : 2));
                if (MakesStoreSequences)
                {
                    RequireSequenceRoutines();
                    VReg seen = swap
                        ? _e.Call(MachineIntrinsicRefExchange, IrTypes.Word, R(at), R(value))!
                        : _e.Call(MachineIntrinsicRefCompareExchange, IrTypes.Word, R(at), R(expect), R(value))!;
                    return Fit(seen, returns);
                }
                MemPlace place = new(R(at), 0, Type.String);
                ReferenceBarrier(place, value);
                CardMarkAhead(place, value);
                VReg old = _e.Reg(IrTypes.Word);
                // Both through EmitAtomic: on a 386 the exchange is the
                // runtime's routine under the lock the compare-exchange takes.
                if (swap) EmitAtomic(Opcode.AtomicSwap, old, R(at), R(value));
                else EmitAtomic(Opcode.AtomicCas, old, R(at), R(expect), R(value));
                CardMark(place, value);
                return Fit(old, returns);
            }

            case "Fence":
                _e.Emit(Opcode.Fence, null);
                return Void();
            case "CompilerFence":
                _e.Emit(Opcode.CompilerFence, null);
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
                return Fit(sp, returns);
            }
            case "EntryStack":
                // The top of THIS thread's stack: the stack pointer the loader
                // left on the main thread, and where the child began on any
                // other. It is what a stack scan ends at, and on the main
                // thread also where argc and argv are.
                return Fit(_e.Load(word, ThreadBlockNow(), TlsStackBase / 4 * _t.WordSize), returns);
            case "FrameTable":
                return Fit(_e.Address(Corsac.Lang.X86.FrameTable.Symbol), returns);
            case "FrameDirectory":
                return Fit(_e.Address(ManagedDirectory.Symbol), returns);
            case "FramePointer":
            {
                VReg fp = _e.Reg(word, "fp");
                _e.Emit(Opcode.FramePointer, fp);
                return Fit(fp, returns);
            }
            case "ThreadBlock":
                return Fit(ThreadBlockNow(), returns);
            case "MainThreadBlock":
                return Fit(_e.Address(ThreadBlock0), returns);
            case "SetThreadBlock":
                // WINDOWS: into the runtime's TLS slot of the TEB.
                if (Windows)
                {
                    _e.Call(MachineIntrinsics.SetTlsBlock, IrType.Void, R(ToWord(Arg(call, target, 0))));
                    return Void();
                }
                // The fallback for a target with no segment of its own: the
                // word that ThreadBlock reads when there is no GS.
                _e.Store(new SymOperand(ThreadBlockSelf), R(ToWord(Arg(call, target, 0))));
                return Void();
            case "SetThreadBlockSlot":
                // Windows: where the runtime's TLS slot is -- the word of the
                // thread's block to start from and what to add to it -- which
                // every read of the block names (Selector.TlsBaseSymbol).
                if (!Windows)
                {
                    Error(call, "Sys.SetThreadBlockSlot is for a Windows image (--windows, --dll)");
                    return Void();
                }
                _e.Store(new SymOperand(Corsac.Lang.X86.Selector.TlsBaseSymbol), R(ToWord(Arg(call, target, 0))));
                _e.Store(new SymOperand(Corsac.Lang.X86.Selector.TlsOffsetSymbol), R(ToWord(Arg(call, target, 1))));
                return Void();
            case "GetGs":
                // The selector GS holds now: on Linux, before the runtime
                // takes it, the C library's thread pointer.
                return Fit(_e.Call(MachineIntrinsics.GetGs, IrType.I32)!, returns);
            // A WORD OF THIS PROCESSOR'S OWN AREA, gs-relative, as Linux's
            // this_cpu_read and this_cpu_write: one instruction, so no
            // preemption falls between finding the processor and reading it.
            // Only where GS is a processor's (a kernel's ring 0).
            // A WORD, the target's: in long mode eight bytes, a pointer of the
            // higher half among them; typed as 32 bits, its high half was
            // cut on the way in and on the way out.
            case "PerCpuRead":
                return Fit(_e.Call(MachineIntrinsics.PerCpuRead, IrTypes.Word, R(ToWord(Arg(call, target, 0))))!, returns);
            case "PerCpuWrite":
                _e.Call(MachineIntrinsics.PerCpuWrite, IrType.Void, R(ToWord(Arg(call, target, 0))), R(ToWord(Arg(call, target, 1))));
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
                // NOR IS A CLASS's VALUE, unless the class is one a string is
                // held as (object, and the rest NarrowKey refuses): a class
                // that is not String cannot hold one, String being sealed.
                // A table keyed by its own class asked this of every key.
                if (!couldBe || NarrowKey(t) is { Kind: TypeKind.Class })
                {
                    Eval(call.Args[0]);
                    return _e.Const(0, IrType.I32);
                }
                // A STRING IS ONE, unless it is null: the descriptor need not
                // be read to learn what the static type already says. Every
                // probe of a table keyed by string asked it twice.
                if (t is { Prim: Prim.String, IsArray: false, IsPointer: false })
                {
                    VReg text = ToWord(Eval(call.Args[0]));
                    return _e.Binary(Opcode.Ne, R(text), Imm(0, text.Type), IrType.I32);
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
                // THE SAME OBJECT IS EQUAL WITHOUT A CALL, as the routine's
                // first test says (KeyEqualsStub): a table's probe reaches
                // here once the full hashes match, which is nearly always
                // the key it holds being the very one asked about.
                VReg same = _f.NewReg(IrType.I32, "kesame");
                Block ask = _f.NewBlock("keask"), done = _f.NewBlock("kedone");
                _e.CopyTo(same, Imm(1, IrType.I32));
                _e.Branch(_e.Binary(Opcode.Eq, R(one), R(two), IrType.I32), done, ask);
                _e.SetBlock(ask);
                if (SharedKey(_b.TypeOf(call.Args[0])) && RuntimeMethod("ShapedAs", 3 + CanonShape.MostArguments) is MethodSymbol shaped)
                {
                    // A SHARED COPY'S T, which only its object's type context
                    // says: by T's typed Equals where the key is an IEquatable
                    // of T -- its own class T, or one that implements it
                    // (Runtime.ShapedAs) -- and by Equals(object) otherwise, as
                    // .NET's EqualityComparer<T>.Default chooses for that T.
                    // Asked of the key's descriptor alone, a Point was
                    // compared by its typed Equals in a HashSet<IShape> or a
                    // List<object>, where .NET's asks Equals(object). The
                    // same code in every unit: the interface asked by its
                    // family alone, which a unit naming no IEquatable at all
                    // computes as the rest do.
                    Block maybe = _f.NewBlock("keqmaybe"), other = _f.NewBlock("keqother");
                    Block typed = _f.NewBlock("keqtyped"), plain = _f.NewBlock("keqplain");
                    VReg desc = _e.Binary(Opcode.Sub, _e.Load(IrTypes.Word, one, 0), _t.DescriptorBytes);
                    VReg flags = Numbered(_e, _e.Load(IrType.I32, desc, DescFlags * _t.WordSize));
                    _e.Branch(_e.Binary(Opcode.And, flags, TypeFlagEquatable), maybe, plain);
                    _e.SetBlock(maybe);
                    Operand wanted = RunTimeDescriptor(_b.TypeOf(call.Args[0]), statics: false);
                    VReg wantedReg = wanted is RegOperand held ? held.Reg : _e.Const(((ImmOperand)wanted).Value, IrTypes.Word);
                    _e.Branch(_e.Binary(Opcode.Eq, R(desc), R(wantedReg), IrType.I32), typed, other);
                    _e.SetBlock(other);
                    Require(shaped);
                    VReg isObject = _e.Binary(Opcode.Eq, R(wantedReg), R(_e.Address(ObjectDescriptor())), IrType.I32);
                    VReg objectBit = IrTypes.Word == IrType.I32 ? isObject : _e.Unary(Opcode.ZExt32, R(isObject), IrTypes.Word);
                    VReg shapeWord = _e.Binary(Opcode.Or, Imm(1 | FaceOfShape, IrTypes.Word), R(_e.Binary(Opcode.Shl, objectBit, ShapeObjectShift)), IrTypes.Word);
                    List<VReg> words = new()
                    {
                        one, _e.Const(CanonShape.FamilyOf(Prelude.EquatableTemplate, 1), IrTypes.Word), shapeWord, wantedReg,
                        _e.Const(0, IrTypes.Word), _e.Const(0, IrTypes.Word), _e.Const(0, IrTypes.Word),
                    };
                    Operand[] shapedArgs = new Operand[words.Count];
                    for (int i = 0; i < words.Count; i++) shapedArgs[i] = R(AsParam(words[i], shaped.Params[i].Type));
                    VReg answer = _e.Call(CallLabel(shaped), IrTypes.Of(shaped.Returns), shapedArgs)!;
                    _e.Branch(answer.Type == IrType.I32 ? answer : _e.Unary(Opcode.Trunc64, R(answer), IrType.I32), typed, plain);
                    _e.SetBlock(typed);
                    _e.CopyTo(same, R(_e.Call(KeyEqualsStub(null), IrType.I32, R(one), R(two))!));
                    _e.Jump(done);
                    _e.SetBlock(plain);
                    _e.CopyTo(same, R(_e.Call(KeyEqualsStub(null, table: false), IrType.I32, R(one), R(two))!));
                    _e.Jump(done);
                    _e.SetBlock(done);
                    return same;
                }
                _e.CopyTo(same, R(_e.Call(KeyEqualsStub(_b.TypeOf(call.Args[0])), IrType.I32, R(one), R(two))!));
                _e.Jump(done);
                _e.SetBlock(done);
                return same;
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
                // AT THE VALUES' OWN WIDTH, never the answer's: cut to the
                // bool's 32 bits, two longs that differed only in their high
                // halves were equal on i386 -- a record struct's Equals, a
                // Dictionary<long, V>'s keys, List<long>.Contains -- and the
                // compiler's own expression keys merged `x & 0x7FFF...` with
                // `x & 0xFFFFF...` (stage 1 and stage 2 differed by it).
                IrType width = x.Type == IrType.I64 || y.Type == IrType.I64 ? IrType.I64 : x.Type;
                VReg wx = Fit(x, width), wy = Fit(y, width);
                return _e.Binary(Opcode.Eq, R(wx), R(wy), IrType.I32);
            }
            case "HashValue":
            {
                // THE HASH .NET's GetHashCode GIVES THE VALUE'S TYPE, as an int
                // widened to the long this answers: a record's GetHashCode and
                // EqualityComparer<T>.Default.GetHashCode are observable, and
                // were the value's low word -- a long's high half, a char's
                // mixing, a NaN's or a zero's canonical form all left out
                // (Int64.GetHashCode is (int)v ^ (int)(v >> 32)).
                Type of = _b.TypeOf(call.Args[0]);
                VReg held = Arg(call, target, 0);
                // A number's own type: an enum by its underlying one; a pointer
                // or a nullable is no number here, whatever Prim it carries.
                Prim prim = of.IsPointer || of.IsNullableValue ? Prim.Void
                    : of.IsEnumValue && of.Symbol is { } counted ? counted.EnumUnderlying : of.Prim;
                VReg hash;
                if (held.Type == IrType.F64)
                {
                    // Double.GetHashCode: a NaN or a zero by its exponent alone.
                    VReg bits = _e.Unary(Opcode.Bits, R(held), IrType.I64);
                    hash = HashOfWide(Canonical(bits, IrType.I64, 0x7FF0000000000000L));
                }
                else if (held.Type == IrType.F32)
                {
                    VReg bits = _e.Unary(Opcode.Bits, R(held), IrType.I32);
                    hash = Canonical(bits, IrType.I32, 0x7F800000L);
                }
                else if (held.Type == IrType.I64 && prim is Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt)
                {
                    hash = HashOfWide(held);
                }
                else if (held.Type == IrType.I32 && prim == Prim.Char)
                {
                    // Char: (int)m_value | ((int)m_value << 16).
                    hash = _e.Binary(Opcode.Or, R(held), R(_e.Binary(Opcode.Shl, R(held), Imm(16, IrType.I32), IrType.I32)), IrType.I32);
                }
                else
                {
                    // Bool, Byte, SByte, Int16, UInt16, Int32, UInt32, a native
                    // integer of 32 bits: the value itself (SByte and Int16 too,
                    // as .NET's own answer -2 for (short)-2). Anything else (a
                    // reference, a pointer): its word, as before. Sign-extended
                    // as every other hash here is, an int widened to the long.
                    return Fit(held.Type == IrType.I32 && returns == IrType.I64 ? _e.Unary(Opcode.SExt32, R(held), IrType.I64) : held, returns);
                }
                return Fit(hash.Type == IrType.I64 ? hash : _e.Unary(Opcode.SExt32, R(hash), IrType.I64), returns);
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
                    // None of its own: refused, as .NET's default comparer refuses it.
                    if (order is null) { RefuseUnordered(); return _e.Const(0, IrType.I32); }
                    Require(order);
                    VReg other = boxed ? Box(call, y, Type.Plain(shape, Prim.Void)) : y;
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
                return Fit(_e.Binary(Opcode.Add, arr, _t.ArrayHeaderBytes), returns);
            }
            case "FrameData":
            {
                VReg arr = ToWord(Arg(call, target, 0));
                Addressed(arr, toReturn: true);
                return Fit(_e.Binary(Opcode.Add, arr, _t.ArrayHeaderBytes), returns);
            }
            case "SyncWord":
            {
                VReg obj = ToWord(Arg(call, target, 0));
                return Fit(_e.Binary(Opcode.Add, obj, _t.WordSize), returns);
            }
            case "AsString":
                return ToWord(Arg(call, target, 0));
            case "DataStart":
                return Fit(_e.Address("__data_start"), returns);
            case "DataEnd":
                return Fit(_e.Address("_end"), returns);
            case "BuildStamp":
                return Fit(_e.Address(Corsac.Lang.Elf.KernelExports.StampSymbol), returns);
            case "KernelSyscalls":
                // Whether this compile's Sys.Syscall is the kernel's own
                // function (--kernel-syscalls): a constant, its tests folded.
                return _e.Const(KernelSyscalls ? 1 : 0, IrType.I32);

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
            case "DoublePrecision":
                // On i386 alone, as the entry asks it (Lowering.EntryStub).
                if (Target.Current.WordSize == 4) _e.Call(MachineIntrinsics.DoublePrecision, IrType.Void);
                return Void();
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
                return Fit(_e.Call(MachineIntrinsics.ReadCr, IrTypes.Word, R(ToI32(Arg(call, target, 0))))!, returns);
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
            // The lowest or highest set bit of a nonzero 32-bit value: BSF and
            // BSR, from the 386 on. Undefined for zero, which BitOperations
            // answers itself before it asks.
            case "BitScanForward":
                return _e.Call(MachineIntrinsics.BitScanForward, IrType.I32, R(ToI32(Arg(call, target, 0))))!;
            case "BitScanReverse":
                return _e.Call(MachineIntrinsics.BitScanReverse, IrType.I32, R(ToI32(Arg(call, target, 0))))!;
            case "CpuidPresent":
                return _e.Call(MachineIntrinsics.CpuidPresent, IrType.I32)!;
            case "CpuidEax" or "CpuidEdx":
                if (_t.WordSize != 4)
                {
                    Error(call, $"Sys.{name} is i386's; long mode asks Sys.Cpuid");
                    return Void();
                }
                return _e.Call(name == "CpuidEax" ? MachineIntrinsics.CpuidEax : MachineIntrinsics.CpuidEdx, IrType.I32, R(ToI32(Arg(call, target, 0))))!;
            case "StreamPrefetch" or "StreamPrefetchLine" or "StreamCopy64" or "StreamFill64" or "StreamEnd"
                or "PackedFindChar4" or "PackedFindChar8" or "PackedEqual8" or "PackedEnd" or "PackedEndFast":
            {
                if (_t.WordSize != 4)
                {
                    Error(call, $"Sys.{name} is an i386 instruction; long mode has its own");
                    return Void();
                }
                string machine = name switch
                {
                    "StreamPrefetch" => MachineIntrinsics.StreamPrefetch,
                    "StreamPrefetchLine" => MachineIntrinsics.StreamPrefetchLine,
                    "StreamCopy64" => MachineIntrinsics.StreamCopy64,
                    "StreamFill64" => MachineIntrinsics.StreamFill64,
                    "StreamEnd" => MachineIntrinsics.StreamEnd,
                    "PackedFindChar4" => MachineIntrinsics.PackedFindChar4,
                    "PackedFindChar8" => MachineIntrinsics.PackedFindChar8,
                    "PackedEqual8" => MachineIntrinsics.PackedEqual8,
                    "PackedEnd" => MachineIntrinsics.PackedEnd,
                    _ => MachineIntrinsics.PackedEndFast,
                };
                List<Operand> given = new();
                for (int k = 0; k < target.Params.Count; k++) given.Add(R(ToWord(Arg(call, target, k))));
                IrType answers = name is "PackedFindChar4" or "PackedFindChar8" or "PackedEqual8" ? IrType.I32 : IrType.Void;
                VReg? got = _e.Call(machine, answers, given.ToArray());
                return got ?? Void();
            }
            // A PACKED BLOCK'S STATE, taken and given back (Parser.
            // ParsePackedBlock: the kind, the compiler's own block's,
            // Parser.ImplicitKind; the block's number in its method; its
            // variant's features, PackedOps' bits).
            // Hosted code owns it already -- the operating system saves it at
            // a switch -- and needs nothing: the MMX registers a packed
            // operation used are emptied before the x87 code, call or return
            // after it (Selector.PackedDirty). Freestanding code borrows a
            // thread's: a frame slot of the block's own (112 bytes for the
            // x87's save; 544 where FXSAVE may be chosen, 512 at sixteen and
            // MXCSR's word) -- one per block, so a block inside another keeps
            // the outer one's save -- and a save and give-back whose kind the
            // backend settles from what the block's code uses, or drops
            // (Selector.SettlePackedSaves). The block moves with its method
            // wherever it is inlined.
            case "PackedEnter":
            case "PackedLeave":
            {
                int kind = call.Args[0] is LiteralExpr { Text: var k } ? int.Parse(k) : Corsac.Lang.Parser.ImplicitKind;
                int number = call.Args[1] is LiteralExpr { Text: var serial } ? int.Parse(serial) : 0;
                int features = call.Args[2] is LiteralExpr { Text: var told } ? int.Parse(told) : 0;
                if (!Freestanding) return Void();
                bool enters = name == "PackedEnter";
                if (!KeepsState(kind)) return Void();
                if (PackedVariantNeverRuns(number, features)) return Void();
                // FXSAVE's room wherever --cpu has SSE: a callee's $inside
                // copy, or one inlined, may use the XMM registers in it
                // (Selector.SettlePackedSaves), which shrinks the area to the
                // x87's 112 bytes where its save settles so.
                bool fx = (features & PackedOps.Sse) != 0 || _t.X86Profile.Has(X86Feature.Sse);
                if (!_packedAreas.TryGetValue((_f, number), out FrameSlot? area))
                    _packedAreas[(_f, number)] = area = _f.NewSlot(fx ? 544 : 112, 16, "packed");
                _e.Call(enters ? MachineIntrinsics.PackedSave(kind, number, features) : MachineIntrinsics.PackedRestore(kind, number, features),
                    IrType.Void, R(_e.SlotAddress(area)));
                return Void();
            }
            // WHICH OF A PACKED BLOCK'S VARIANTS RUNS (Parser.ParsePackedBlock):
            // the last, featureless, in long mode (its packed operations are
            // their scalar forms), the others nothing; the one left, where
            // --cpu's floor and its bans leave one; else the processor's
            // answer among those left, asked the first time the block is
            // reached (Runtime.PackedChoose) and kept in a word of the
            // block's own, one more than the variant so that 0 is unasked:
            // every time after, one load.
            case "PackedChoose":
            {
                int block = LiteralInteger(call.Args[0]) is long b ? (int)b : 0;
                ulong masks = LiteralInteger(call.Args[1]) is long m ? (ulong)m : 0;
                int count = LiteralInteger(call.Args[2]) is long c ? (int)c : 1;
                if (_t.WordSize != 4)
                {
                    for (int v = 0; v < count - 1; v++) _packedDead.Add((_f, block - count + v));
                    return Fit(_e.Const(count - 1, IrType.I32), returns);
                }
                // --cpu IS THE FLOOR: the processor has at least its features,
                // so the first variant they cover always wins over every one
                // after it, which never runs; nor does one with a feature a
                // --disable-NAME bans (X86Cpu.Banned), nor one with SSE in
                // freestanding code whose --cpu has none. Each is nothing
                // (PackedVariantNeverRuns), and a single one left is the
                // answer at the compile. Not where the image's knob caps the
                // processor's features (Corsac.Cpu.Features): then the run
                // decides, every variant kept.
                bool capped = RuntimeKnobs.Any(k => k.StartsWith("Corsac.Cpu.Features=", StringComparison.Ordinal));
                int profile = ProfileFeatures, floor = count - 1, live = -1, alive = 0;
                for (int v = 0; v < count; v++)
                    if ((profile & (int)(masks >> (8 * v) & 0xFF)) == (int)(masks >> (8 * v) & 0xFF)) { floor = v; break; }
                for (int v = 0; v < count; v++)
                {
                    int mask = (int)(masks >> (8 * v) & 0xFF);
                    if (!capped && v > floor || PackedFeaturesNeverRun(mask)) _packedDead.Add((_f, block - count + v));
                    else { live = v; alive++; }
                }
                if (alive == 1) return Fit(_e.Const(live, IrType.I32), returns);
                MethodSymbol? asked = RequireRuntime(call, "PackedChoose", 3, "Sys.PackedChoose");
                if (asked is null) return Fit(_e.Const(count - 1, IrType.I32), returns);
                string choice = "pk_pick$" + _f.Name + "$" + block;
                if (_packedPicks.Add(choice))
                    _m.Data.Add(new DataItem(choice, new byte[4]) { Zero = true, Align = 4, Exported = false });
                VReg at = _e.Address(choice);
                Block ask = _f.NewBlock("packedask"), known = _f.NewBlock("packedknown");
                _e.Branch(_e.Load(IrType.I32, at, 0), known, ask);
                _e.SetBlock(ask);
                // FREESTANDING, SSE ONLY WHERE --cpu HAS IT: the operating
                // system that runs this code turns SSE's state on (CR4.OSFXSR)
                // for the processors its build is for, and a variant with SSE
                // picked by CPUID alone on a newer one would fault there.
                // Nor a banned feature (X86Cpu.Banned), and none after the
                // floor: those variants are nothing here, and the floor -- the
                // run's last word, which needs nothing the processor lacks --
                // is where it falls back.
                int allowed = (Freestanding && !_t.X86Profile.Has(X86Feature.Sse) ? ~(PackedOps.Sse | PackedOps.Sse2) : ~0) & ~PackedBits(_t.X86Profile.Banned);
                VReg answer = _e.Call(CallLabel(asked), IrType.I32, R(AsParam(_e.Const((long)masks, IrType.I64), asked.Params[0].Type)),
                    R(_e.Const(capped ? count : floor + 1, IrType.I32)), R(_e.Const(allowed, IrType.I32)))!;
                _e.Store(RegOperand.Of(at), RegOperand.Of(_e.Binary(Opcode.Add, answer, 1)), 0, 4);
                _e.Jump(known);
                _e.SetBlock(known);
                return Fit(_e.Binary(Opcode.Sub, _e.Load(IrType.I32, at, 0), 1), returns);
            }
            case "ReadFlags":
                return Fit(_e.Call(MachineIntrinsics.ReadFlags, IrTypes.Word)!, returns);
            case "ReadCodeSegment":
                return Fit(_e.Call(MachineIntrinsics.ReadCodeSegment, IrType.I32)!, returns);
            case "SwapGs":
                _e.Call(MachineIntrinsics.SwapGs, IrType.Void);
                return Void();

            // ---- the operating system and function pointers --------------------
            case "Syscall" when KernelSyscalls:
            {
                // RING 0: the kernel's own function, not a trap (KernelSyscalls).
                if (KernelSyscallMethod() is not MethodSymbol served)
                {
                    Error(call, "--kernel-syscalls: Sys.Syscall is a call of KernelFiles.Syscall(int number, SyscallWord a, ..., SyscallWord f), "
                              + (KernelSyscallCandidates() == 0 ? "and no such static method is declared" : "and more than one type KernelFiles declares one"));
                    return _e.Const(0, IrTypes.Word);
                }
                Require(served);
                List<Operand> passed = new();
                for (int i = 0; i < 7; i++)
                    passed.Add(i < target.Params.Count ? R(Arg(call, target, i)) : Imm(0, IrTypes.Word));
                return _e.Call(CallLabel(served), IrTypes.Word, passed.ToArray())!;
            }
            case "Syscall":
            case "GuiCall":
            {
                // The registers are SyscallWord wide, which is the machine's
                // word where --syscall-abi is the target's own (the driver
                // refuses the others): each argument is already that wide.
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(Arg(call, target, i)));
                }
                int vector = target.Name == "GuiCall" ? 0x81 : 0x80;
                VReg r = _e.Syscall(R(ToWord(Arg(call, target, 0))), args, vector);
                return Fit(r, returns);
            }
            case "NtCall":
            {
                // NT's convention: the arguments' address in EDX, which is the
                // third of Syscall's registers (EBX, ECX, EDX).
                VReg number = ToWord(Arg(call, target, 0));
                List<Operand> args = new() { Imm(0, number.Type), Imm(0, number.Type), R(Arg(call, target, 1)) };
                VReg r = _e.Syscall(R(number), args, 0x2E);
                return Fit(r, returns);
            }
            case "Call":
            case "CallLong":
            {
                // AS ANY CALL OF THIS LANGUAGE'S, of a method declaring
                // (nuint, nuint, nuint): a word an argument, as Sys.Call declares
                // them and as the kernel's entry code pushes them for
                // Arch.TrapEntry, answering a word -- or a long (CallLong),
                // two registers on i386. Code with the machine's own C
                // convention is reached by Sys.CallNative.
                VReg fn = Address(call, target, 0);
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(ToWord(Arg(call, target, i))));
                }
                return _e.CallIndirect(R(fn), returns, args)!;
            }
            case "CallNative":
            case "CallNativeFloat":
            {
                VReg fn = Address(call, target, 0);
                List<Operand> args = new();
                for (int i = 1; i < target.Params.Count; i++)
                {
                    args.Add(R(ToWord(Arg(call, target, i))));
                }
                // A plain native runs on the thread's own state, given back
                // around it where this code keeps one; CallNativeFloat's in
                // the state kept for it (Binder.PackedStateOf).
                VReg? answer = name == "CallNative"
                    ? OnOwnState(() => _e.CallIndirect(R(fn), word, args, NativeCall.Indirect))
                    : _e.CallIndirect(R(fn), word, args, NativeCall.Indirect);
                return Fit(answer!, returns);
            }
            case "AddressOf":
                return Fit(Arg(call, target, 0), returns);

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

    /// <summary>A packed block's give-back's kind (Sys.PackedLeave's first).</summary>
    private static int PackedKindOf(CallExpr call) => call.Args[0] is LiteralExpr { Text: var kind } ? int.Parse(kind) : Corsac.Lang.Parser.ImplicitKind;

    /// <summary>A packed block's give-back's number (Sys.PackedLeave's second): its variant's, which Sys.PackedChoose finds it by.</summary>
    private static int PackedNumberOf(CallExpr call) => call.Args[1] is LiteralExpr { Text: var serial } ? int.Parse(serial) : -1;

    /// <summary>The packed block a call gives back (Sys.PackedLeave, Parser.ParsePackedBlock): its variant's features; null for any other call.</summary>
    private static int? PackedLeaveOf(CallExpr call)
        => call is { Target: MemberExpr { Target: NameExpr { Name: "Sys" }, Name: "PackedLeave" },
                     Args: [LiteralExpr, LiteralExpr, LiteralExpr { Text: var features }] }
            ? int.Parse(features) : null;

    /// <summary>
    /// Whether a packed block's give-back does anything here (PackedLeave):
    /// freestanding i386 code, whose save the backend settles. Hosted code
    /// and long mode give nothing back, and their blocks are their bodies
    /// alone.
    /// </summary>
    private bool PackedStateHere => Freestanding && _t.WordSize == 4;

    /// <summary>
    /// WHETHER A PACKED BLOCK OF `kind` KEEPS THE PROCESSOR'S STATE here:
    /// freestanding i386 code, and for the compiler's own block around a
    /// body (Parser.ImplicitKind) a method that needs it (Binder's
    /// PackedState) lowered as itself -- its $inside copy runs inside its
    /// caller's state, and keeps none.
    /// </summary>
    private bool KeepsState(int kind)
        => PackedStateHere && (kind != Corsac.Lang.Parser.ImplicitKind || !_insideCopy && _method is not null && _b.PackedState.ContainsKey(_method));

    /// <summary>Lowering a method's $inside copy (EmitMethod twice): its body as it is, inside its caller's state.</summary>
    private bool _insideCopy;

    /// <summary>The kept states the code being lowered is inside (Lowering.Stmt's packed TryStmt).</summary>
    private int _stateKept;

    /// <summary>Those states' blocks, innermost last: their kinds, numbers and features, which a give-back and a taking again name (OnOwnState).</summary>
    private readonly List<(int Kind, int Number, int Features)> _keptBlocks = new();

    /// <summary>
    /// A CALL ON THE THREAD'S OWN STATE inside a kept one: every kept state
    /// around it given back first, innermost first, and taken again after,
    /// outermost first -- a plain native (Sys.CallNative), or a method that
    /// reaches one (BindResult.PackedNatives): a BIOS thunk, a CPU's
    /// bring-up before its SSE is on, a return to user code, which must
    /// find, and leave, the state as the thread has it. A $inside copy is
    /// never made of code that reaches one, so nothing here runs inside a
    /// caller's state this code cannot give back.
    /// </summary>
    private T OnOwnState<T>(Func<T> call)
    {
        if (_stateKept == 0 || _insideCopy || _keptBlocks.Count == 0) return call();
        for (int k = _keptBlocks.Count - 1; k >= 0; k--)
        {
            (int kind, int number, int features) = _keptBlocks[k];
            _e.Call(MachineIntrinsics.PackedRestore(kind, number, features), IrType.Void, R(_e.SlotAddress(_packedAreas[(_f, number)])));
        }
        T made = call();
        foreach ((int kind, int number, int features) in _keptBlocks)
            _e.Call(MachineIntrinsics.PackedSave(kind, number, features), IrType.Void, R(_e.SlotAddress(_packedAreas[(_f, number)])));
        return made;
    }

    /// <summary>
    /// A METHOD'S COPY THAT RUNS INSIDE ITS CALLER'S STATE ($inside): of one
    /// that keeps the state (Binder's PackedState) and that a second
    /// function can be made of (Binder's PackedCopyable), whose body this
    /// unit has, called directly -- not a generic shared copy's, which
    /// takes a context.
    /// </summary>
    private bool CopiesInside(MethodSymbol m)
        => PackedStateHere && _b.PackedState.ContainsKey(m) && _b.PackedCopyable.Contains(m) && m.Decl?.Body is not null && !m.Async && !TakesContext(Canonical(m));

    /// <summary>A direct call's label: the callee's $inside copy where the call is made inside a kept state.</summary>
    private string InsideLabel(MethodSymbol m, string label) => (_insideCopy || _stateKept > 0) && CopiesInside(m) ? label + "$inside" : label;

    /// <summary>--packed-report: every method that keeps the processor's state, said as it is lowered.</summary>
    public static bool PackedReport { get; set; }

    /// <summary>The packed blocks' choice words this unit defines (PackedChoose): one each.</summary>
    private readonly HashSet<string> _packedPicks = new(StringComparer.Ordinal);

    /// <summary>The packed blocks' variants never picked here, by their functions and numbers (PackedChoose).</summary>
    private readonly HashSet<(Function, int)> _packedDead = new();

    /// <summary>Whether a packed block's variant, by its number and features, is never picked here (Sys.PackedChoose): one after the first --cpu covers, or one with SSE in freestanding i386 code whose --cpu has none.</summary>
    private bool PackedVariantNeverRuns(int number, int features) => _packedDead.Contains((_f, number)) || PackedFeaturesNeverRun(features);

    /// <summary>Whether a variant's features are never picked here, whatever the processor: one a --disable-NAME bans, or one with SSE in freestanding i386 code whose --cpu has none.</summary>
    private bool PackedFeaturesNeverRun(int features)
        => (features & PackedBits(_t.X86Profile.Banned)) != 0 || Freestanding && _t.WordSize == 4 && !_t.X86Profile.Has(X86Feature.Sse) && (features & (PackedOps.Sse | PackedOps.Sse2)) != 0;

    /// <summary>
    /// PROCESSOR.HAS OF CONSTANT FEATURES, AT THE COMPILE (Runtime's
    /// Processor): true where --cpu promises every one -- the processor has
    /// at least its features -- and false where a --disable-NAME bans one
    /// (X86Cpu.Banned): the image may not use it, whatever the processor
    /// has. The branch it guards goes with dead code. Otherwise null, the
    /// question the run asks (the cached word, CPUID asked once); and never
    /// where the image's knob caps the features (Corsac.Cpu.Features), or in
    /// long mode, whose profile is another's.
    /// </summary>
    private bool? ProcessorHasFolded(CallExpr call, MethodSymbol target)
    {
        if (target.Name != "Has" || target.Owner.Name != "Processor" || target.Owner.Decl?.FromLibrary != true || call.Args.Count != 1
            || _t.WordSize != 4 || IntegerConstantOf(call.Args[0]) is not long asked
            || RuntimeKnobs.Any(k => k.StartsWith("Corsac.Cpu.Features=", StringComparison.Ordinal)))
            return null;
        int promised = ProcessorBits(_t.X86Profile.Features), banned = ProcessorBits(_t.X86Profile.Banned);
        if (((int)asked & promised) == (int)asked) return true;
        if (((int)asked & banned) != 0) return false;
        return null;
    }

    /// <summary>Processor's feature bits of X86Feature's: Cas64 the Pentium's CMPXCHG8B, Cmov, and the packed ones (PackedBits).</summary>
    private static int ProcessorBits(X86Feature f)
        => PackedBits(f) | ((f & X86Feature.Pentium) != 0 ? 1 << 0 : 0) | ((f & X86Feature.Cmov) != 0 ? 1 << 1 : 0);

    /// <summary>An integer the expression is at the compile: a literal, a const, and the bitwise or of such.</summary>
    private long? IntegerConstantOf(Expr e) => e switch
    {
        LiteralExpr { Kind: Lit.Int } l => l.IntValue,
        NameExpr or MemberExpr when _b.Resolved.TryGetValue(e, out Sym? sym) && sym is ConstSym { Type.IsInteger: true } k => k.Value,
        BinaryExpr { Op: BinOp.Or } b when IntegerConstantOf(b.Left) is long l && IntegerConstantOf(b.Right) is long r => l | r,
        _ => null,
    };

    /// <summary>The features --cpu promises, as PackedOps' bits (Processor's).</summary>
    private int ProfileFeatures => PackedBits(_t.X86Profile.Features);

    private static int PackedBits(X86Feature f)
        => ((f & X86Feature.Mmx) != 0 ? PackedOps.Mmx : 0) | ((f & X86Feature.MmxExtensions) != 0 ? PackedOps.MmxExtensions : 0)
         | ((f & X86Feature.Sse) != 0 ? PackedOps.Sse : 0) | ((f & X86Feature.Sse2) != 0 ? PackedOps.Sse2 : 0)
         | ((f & X86Feature.ThreeDNow) != 0 ? PackedOps.ThreeDNow : 0);

    /// <summary>Each packed block's save area, by its function and its number (PackedEnter, PackedLeave): one for its start and every give-back alike.</summary>
    private readonly Dictionary<(Function, int), FrameSlot> _packedAreas = new();

    /// <summary>
    /// `n` bytes from `from` to `into`: forward a word at a time (MemCopy);
    /// where `oneBlock` -- both in one object -- and the destination starts
    /// inside the source, from the end down.
    /// </summary>
    private void MoveBytes(VReg into, VReg from, VReg n, VReg? oneBlock, bool numbers)
    {
        // COPY IS MEMMOVE (the Prelude's word for it): where the
        // destination starts inside the source -- src <= dst < src + n,
        // one unsigned compare of dst - src against n -- the bytes go
        // last first, so that none is overwritten before it is read.
        // The forward copy below moves words at a time from the
        // front, and an upward overlapping Copy wrote the source's
        // first words over its own tail before reading it.
        // CopyNoOverlap's callers promise there is none (no `oneBlock`).
        //
        // ONLY A BLOCK MOVED WITHIN ITSELF CAN OVERLAP: two arrays' bytes are
        // copied forward without taking one's address from the other's,
        // which the link's run of an initialiser cannot do (PreinitMachine:
        // two objects' addresses are no number). A static made from a span's
        // ToArray was refused there, and the type made at run time -- then
        // reached from an interrupt handler, which the link refuses.
        Block forward = _f.NewBlock("copyfwd"), done = _f.NewBlock("copydone");
        if (oneBlock is not null)
        {
            Block within = _f.NewBlock("copywithin"), backward = _f.NewBlock("copyback"), step = _f.NewBlock("copystep");
            _e.Branch(oneBlock, within, forward);
            _e.SetBlock(within);
            _e.Branch(_e.Binary(Opcode.LtU, _e.Binary(Opcode.Sub, into, from), n), backward, forward);
            _e.SetBlock(backward);
            VReg left = _f.NewReg(IrTypes.Word, "copyleft");
            _e.CopyTo(left, R(n));
            // FOUR BYTES A STEP FROM THE END while four are left, then
            // the bytes before them, where it was a byte a step: a
            // block moved up within itself (the crypto library's
            // Copy) was a load and a store per byte. Each word is
            // read whole before it is written, and the destination
            // is above the source, so what a step writes is never
            // what a later step reads.
            Block words = _f.NewBlock("copywords"), aWord = _f.NewBlock("copyword");
            _e.Jump(words);
            _e.SetBlock(words);
            _e.Branch(_e.Binary(Opcode.LtU, left, _e.Const(4, IrTypes.Word)), step, aWord);
            _e.SetBlock(aWord);
            _e.CopyTo(left, R(_e.Binary(Opcode.Sub, left, 4)));
            VReg unit32 = _e.Load(IrType.I32, _e.Binary(Opcode.Add, from, left), 0, 4, signed: false);
            _e.Store(_e.Binary(Opcode.Add, into, left), unit32, 0, 4);
            _e.Jump(words);
            _e.SetBlock(step);
            Block each = _f.NewBlock("copyeach");
            _e.Branch(left, each, done);
            _e.SetBlock(each);
            _e.CopyTo(left, R(_e.Binary(Opcode.Sub, left, 1)));
            VReg unit8 = _e.Load(IrType.I32, _e.Binary(Opcode.Add, from, left), 0, 1, signed: false);
            _e.Store(_e.Binary(Opcode.Add, into, left), unit8, 0, 1);
            _e.Jump(step);
        }
        else
        {
            _e.Jump(forward);
        }
        _e.SetBlock(forward);
        _e.Emit(Opcode.MemCopy, null, R(into), R(from), R(n));
        // CHARACTERS OR BYTES, NEVER AN ADDRESS (Instr.Number): read as a
        // copy of words that might be, every string a join was handed held
        // what its parts held, and a key a dictionary's missing-key message
        // was made from went with the exception to where nobody follows --
        // the name a front end's node looked up, and with it, merged over the
        // node's class, the tree (test 1200). A long one is streamed by the
        // backend (Select.SelectMemCopy), past every pass that reads it. Only
        // `numbers` -- Copy's strings and byte arrays, NumbersMove's elements:
        // a long's or a nint's may be an address.
        if (numbers) _e.Block.Instrs[^1].Number = true;
        _e.Jump(done);
        _e.SetBlock(done);
    }

    /// <summary>The runtime's routines with a packed form (Runtime.Packed*), hosted i386 code's to call instead.</summary>
    private static readonly HashSet<string> PackedForms = new(StringComparer.Ordinal)
        { "StringFind", "StringFindReverse", "StringSearch", "StringEqual", "StringCompare", "BytesEqual" };

    /// <summary>
    /// Anything without instructions here becomes a call to a runtime routine
    /// of the same name and shape. Print, the string routines, the bit tricks
    /// and the checksum all live in the runtime (runtime/src/core) as ordinary source.
    /// </summary>
    private VReg RuntimeFallback(CallExpr call, MethodSymbol target)
    {
        MethodSymbol? rt = RuntimeMethod(target.Name, target.Params.Count, target.Params);
        // THE MMX REGISTERS' FORM where the hosted runtime has one: it asks
        // Processor itself, and takes the scalar routine for a short span.
        if (!Freestanding && _t.WordSize == 4 && PackedForms.Contains(target.Name)
            && RuntimeMethod("Packed" + target.Name, target.Params.Count, target.Params) is MethodSymbol packed)
            rt = packed;
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
        EmitAtomic(op, old, R(at), R(value));
        return Fit(old, IrTypes.Of(target.Returns));
    }

    /// <summary>An atomic on an int: four bytes at `at`, answering the int that was there.</summary>
    private VReg Atomic32(CallExpr call, MethodSymbol target, Opcode op)
    {
        VReg at = Address(call, target, 0);
        VReg value = ToI32(Arg(call, target, 1));
        VReg old = _e.Reg(IrType.I32);
        EmitAtomic(op, old, R(at), R(value));
        return old;
    }

    /// <summary>
    /// THE 386 HAS NO XADD AND NO CMPXCHG: under a profile without the 486,
    /// an atomic add, compare-exchange, and, or or exclusive or of a word is
    /// a call of the runtime's routine for it (Runtime.AtomicAdd386,
    /// AtomicCas386, AtomicBits386), which reads and writes with interrupts
    /// off on bare metal or under its one lock in a process. AN EXCHANGE
    /// TOO (AtomicSwap386), though the 386 has XCHG: under the lock a
    /// compare-exchange is a read and then a write, and a bare XCHG between
    /// the two from another thread -- a Monitor's release while a contender
    /// sets its Asleep bit -- was lost. Not in the lock's own taking and
    /// giving back (Platform.AtomicsEnter, AtomicsLeave), which are the
    /// exchanges everything else waits on. Where the runtime has no such
    /// routine -- a program of its own with no library -- the instruction
    /// stays, and the emitted-code audit refuses it.
    /// </summary>
    private void EmitAtomic(Opcode op, VReg old, Operand at, params Operand[] values)
    {
        bool takesTheLock = _method is { Name: "AtomicsEnter" or "AtomicsLeave", Owner.Name: "Platform" };
        if (!(op == Opcode.AtomicSwap && takesTheLock) && _t == Target.X86 && !_t.X86Profile.Has(X86Feature.I486) && old.Type == IrType.I32)
        {
            (string name, int arity) = op switch
            {
                Opcode.AtomicAdd => ("AtomicAdd386", 2),
                Opcode.AtomicCas => ("AtomicCas386", 3),
                Opcode.AtomicSwap => ("AtomicSwap386", 2),
                _ => ("AtomicBits386", 3),
            };
            if (RuntimeMethod(name, arity) is MethodSymbol routine)
            {
                Require(routine);
                List<Operand> args = new() { at };
                args.AddRange(values);
                if (op is Opcode.AtomicAnd or Opcode.AtomicOr or Opcode.AtomicXor)
                    args.Add(new ImmOperand(op == Opcode.AtomicAnd ? 0 : op == Opcode.AtomicOr ? 1 : 2, IrType.I32));
                VReg answered = _e.Call(CallLabel(routine), IrType.I32, args.ToArray())!;
                _e.CopyTo(old, R(answered));
                return;
            }
        }
        List<Operand> operands = new() { at };
        operands.AddRange(values);
        _e.Emit(op, old, operands.ToArray());
    }

    private VReg Void() => _e.Const(0, IrTypes.Word);

    /// <summary>The selector's names for the reference exchanges as sequences (X86 MachineIntrinsics.RefExchange, RefCompareExchange).</summary>
    private const string MachineIntrinsicRefExchange = "__x86.i.refxchg", MachineIntrinsicRefCompareExchange = "__x86.i.refcas";

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

    /// <summary>
    /// Whether a table's key of this type is a shared copy's T in the class's
    /// own instance code, where the object's type context says what T is
    /// (RunTimeDescriptor).
    /// </summary>
    private bool SharedKey(Type of)
        => of is { CanonParam: >= 0, IsArray: false, IsPointer: false } && ContextSelf is not null
        && _method is { } running && SharedContext(running.Owner)
        && (of.CanonOwner is not string owner || owner == running.Owner.Key);

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
                    // None of its own: refused, as .NET's default comparer refuses it.
                    if (order is null) { RefuseUnordered(); return _e.Const(0, IrType.I32); }
                    Require(order);
                    VReg other = boxed ? Box(null!, ToWord(y!), Type.Plain(shape, Prim.Void)) : ToWord(y!);
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
    /// <summary>
    /// A value's bits at another IR type, with no conversion (Sys.As): a float
    /// reinterpreted as the integer of its width (Bits), integers narrowed or
    /// widened as words are, and an integer read back as a float the same way.
    /// </summary>
    private VReg AsBits(VReg value, IrType wanted)
    {
        if (value.Type == wanted) return value;
        VReg bits = value.Type switch
        {
            IrType.F32 => _e.Unary(Opcode.Bits, R(value), IrType.I32),
            IrType.F64 => _e.Unary(Opcode.Bits, R(value), IrType.I64),
            _ => value,
        };
        IrType integer = wanted switch { IrType.F32 => IrType.I32, IrType.F64 => IrType.I64, _ => wanted };
        if (bits.Type == IrType.I64 && integer == IrType.I32) bits = _e.Unary(Opcode.Trunc64, bits);
        else if (bits.Type == IrType.I32 && integer == IrType.I64) bits = _e.Unary(Opcode.ZExt32, R(bits), IrType.I64);
        return wanted is IrType.F32 or IrType.F64 ? _e.Unary(Opcode.Bits, R(bits), wanted) : bits;
    }

    /// <summary>
    /// A machine word as the type the intrinsic answers: a long zero-extended
    /// from it (Widen), a nuint or nint the word itself, a 32-bit type its
    /// low half.
    /// </summary>
    private VReg Fit(VReg v, IrType to)
    {
        if (v.Type == to) return v;
        if (v.Type == IrType.I32 && to == IrType.I64) return _e.Unary(Opcode.ZExt32, v);
        if (v.Type == IrType.I64 && to == IrType.I32) return _e.Unary(Opcode.Trunc64, v);
        return v;
    }

    private VReg ToI64(VReg v) => v.Type == IrType.I32 ? _e.Unary(Opcode.ZExt32, v) : v;

    /// <summary>(int)v ^ (int)(v >> 32): Int64's GetHashCode, an I32.</summary>
    private VReg HashOfWide(VReg v)
    {
        VReg low = _e.Unary(Opcode.Trunc64, R(v), IrType.I32);
        VReg high = _e.Unary(Opcode.Trunc64, R(_e.Binary(Opcode.ShrU, R(v), Imm(32, IrType.I32), IrType.I64)), IrType.I32);
        return _e.Binary(Opcode.Xor, R(low), R(high), IrType.I32);
    }

    /// <summary>
    /// A float's bits as its GetHashCode takes them: a NaN or a zero
    /// (((bits - 1) &amp; magnitude) at or above infinity's) by its exponent
    /// field alone, `infinity`; anything else as it is.
    /// </summary>
    private VReg Canonical(VReg bits, IrType type, long infinity)
    {
        long magnitude = type == IrType.I64 ? long.MaxValue : int.MaxValue;
        VReg less = _e.Binary(Opcode.Sub, R(bits), Imm(1, type), type);
        VReg masked = _e.Binary(Opcode.And, R(less), Imm(magnitude, type), type);
        VReg special = _e.Binary(Opcode.GeU, R(masked), Imm(infinity, type), IrType.I32);
        VReg result = _f.NewReg(type, "hashbits");
        Block canonical = _f.NewBlock("hcanon"), plain = _f.NewBlock("hplain"), end = _f.NewBlock("hend");
        _e.Branch(special, canonical, plain);
        _e.SetBlock(canonical);
        _e.CopyTo(result, R(_e.Binary(Opcode.And, R(bits), Imm(infinity, type), type)));
        _e.Jump(end);
        _e.SetBlock(plain);
        _e.CopyTo(result, R(bits));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// The kernel's KernelFiles.Syscall(int number, SyscallWord a .. f), whatever
    /// its namespace, for --kernel-syscalls; null when no type KernelFiles
    /// declares exactly one, or more than one does.
    /// </summary>
    private MethodSymbol? KernelSyscallMethod()
    {
        List<MethodSymbol> found = KernelSyscallFound();
        return found.Count == 1 ? found[0] : null;
    }

    private int KernelSyscallCandidates() => KernelSyscallFound().Count;

    private List<MethodSymbol> KernelSyscallFound()
        => _b.Types.Values.Where(t => t.Name == "KernelFiles")
            .SelectMany(t => { t.EnsureMembers(); return t.Methods; })
            .Where(m => m.Static && m.Name == "Syscall" && m.Params.Count == 7
                && m.Params[0].Type.Prim == Prim.I32
                && m.Params.Skip(1).All(p => p.Type.Prim == (SyscallAbi == 64 ? Prim.U64 : Prim.U32))
                && m.Returns.Prim == (SyscallAbi == 64 ? Prim.U64 : Prim.U32))
            .Distinct().ToList();
}
