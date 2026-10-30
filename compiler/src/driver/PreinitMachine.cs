using System.Runtime.CompilerServices;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using Corsac.Lang.Metadata;

namespace Corsac;

using Val = Corsac.Lang.Lto.PreinitWorld.Val;
using Num = Corsac.Lang.Lto.PreinitWorld.Num;
using Ptr = Corsac.Lang.Lto.PreinitWorld.Ptr;
using Code = Corsac.Lang.Lto.PreinitWorld.Code;
using Refused = Corsac.Lang.Lto.PreinitWorld.Refused;

/// <summary>
/// THE IR RUN AT THE LINK, over the program's memory as the link sees it
/// (Lto.PreinitWorld): a type's initialiser body, and everything it calls,
/// one instruction at a time, as the machine would -- every unit's IR read
/// from its archive (IrArchive), or, for a program compiled whole, from the
/// snapshot its compile kept (Remember).
///
/// THE RUNTIME'S OWN WORK IS DONE HERE BY NAME, not run: an allocation is a
/// fresh zeroed object (Runtime.Alloc, AllocLeaf, AllocObject, the region
/// allocators), the collector's barriers and frees are nothing to a program
/// no collector has seen yet, and the thread's block is one zeroed block.
/// The machine's instructions that only order or mark (cli, sti, the
/// barriers) do nothing, and the flags read with interrupts off, as they are
/// at startup. ANYTHING ELSE THE MACHINE CANNOT DO REFUSES: a system call, a
/// port, a control register, a throw, a routine with no IR, an address
/// turned into a number. The run is then undone, and the type is made at run
/// time as it always was.
/// </summary>
public sealed class PreinitMachine : Preinit.IMachine
{
    // A PROGRAM COMPILED WHOLE has no archive in its object: its compile kept
    // its IR as the link's records would hold it (Driver, before the late
    // passes), for the link of that object to run.
    private static readonly ConditionalWeakTable<ObjectFile, List<IrArchiveRecord>> Snapshots = new();

    public static void Remember(ObjectFile obj, List<IrArchiveRecord> records) => Snapshots.AddOrUpdate(obj, records);

    private PreinitWorld? _indexed;
    // Each routine's IR and the unit it is of: a unit's own symbols are found as that unit names them.
    private readonly Dictionary<string, (ObjectFile Unit, Func<byte[]> Body)> _bodies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Function Function, ObjectFile Unit)?> _functions = new(StringComparer.Ordinal);
    private readonly IrReadBudget _budget = new(1L << 40);
    private const int Deepest = 400;

    public void Run(PreinitWorld world, string routine)
    {
        // THE WORLD'S TARGET WHILE ITS ROUTINES ARE READ AND RUN, as
        // UnitBackend sets it for a unit it makes again. A routine's IR is
        // verified as it is decoded (IrFunctionCodec), against the word of
        // the target in force, and the link reads the preinit notes before it
        // knows the long mode it lays out by: an x86-64 image's initialisers
        // were checked as x86's, and the store of a new object's header --
        // eight bytes, an I64 address -- was refused ("operand 0 is I64,
        // expected I32"), and the link with it.
        Corsac.Lang.Target was = Corsac.Lang.Target.Current;
        Corsac.Lang.Target.Current = world.LongMode ? Corsac.Lang.Target.X86_64 : Corsac.Lang.Target.X86;
        try
        {
            Index(world);
            Call(world, routine, System.Array.Empty<Val>(), 0);
        }
        finally { Corsac.Lang.Target.Current = was; }
    }

    public void Release()
    {
        _indexed = null;
        _bodies.Clear(); _bodies.TrimExcess();
        _functions.Clear(); _functions.TrimExcess();
    }

    /// <summary>Where each routine's IR is, over the world's inputs, once a world.</summary>
    private void Index(PreinitWorld world)
    {
        if (ReferenceEquals(_indexed, world)) return;
        _indexed = world;
        _bodies.Clear();
        _functions.Clear();
        foreach (ObjectFile obj in world.Objects)
        {
            if (Snapshots.TryGetValue(obj, out List<IrArchiveRecord>? records))
            {
                foreach (IrArchiveRecord record in records)
                    if (record.Key.StartsWith("F:", StringComparison.Ordinal)) _bodies.TryAdd(record.Key[2..], (obj, record.Body));
                continue;
            }
            if (IrArchive.Read(obj) is not IrArchive archive) continue;
            foreach (IrKey key in archive.Entries.Keys)
                if (key.Kind == IrKey.Function) _bodies.TryAdd(key.Name, (obj, () => archive.ReadBody(key)));
        }
    }

    private (Function Function, ObjectFile Unit)? FunctionOf(string label)
    {
        if (_functions.TryGetValue(label, out var had)) return had;
        (Function, ObjectFile)? f = _bodies.TryGetValue(label, out var body) ? (IrFunctionCodec.Read(body.Body(), _budget), body.Unit) : null;
        _functions[label] = f;
        return f;
    }

    // ---- calls ---------------------------------------------------------------------

    private Val? Call(PreinitWorld w, string label, Val[] args, int depth)
    {
        if (depth > Deepest) throw new Refused("calls nested past " + Deepest);
        if (Runtime(w, label, args, out Val? done)) return done;
        // Another type touched: made first, as its first touch would have.
        if (w.InitializerOf(label) is PreinitWorld.TypeEntry type)
        {
            w.Enter(type, body => Call(w, body, System.Array.Empty<Val>(), depth + 1));
            return null;
        }
        var (f, unit) = FunctionOf(label) ?? throw new Refused("'" + label + "' has no IR the link can run");
        // WHERE IT WAS REFUSED, the innermost routine named: the report's
        // reader goes to that code, not to the type's initialiser at large.
        try { return Execute(w, f, unit, args, depth); }
        catch (Refused refused) when (!refused.Message.Contains(" (in '", StringComparison.Ordinal))
        {
            throw new Refused(refused.Message + " (in '" + (f.Display ?? f.Name) + "')");
        }
    }

    private static bool Is(string label, string owner, string name)
        => label.Length > owner.Length + name.Length + 4 && label.StartsWith("m_", StringComparison.Ordinal)
           && string.CompareOrdinal(label, 2, owner, 0, owner.Length) == 0 && label[2 + owner.Length] == '_'
           && string.CompareOrdinal(label, 3 + owner.Length, name, 0, name.Length) == 0 && label[3 + owner.Length + name.Length] == '_'
           && char.IsAsciiDigit(label[4 + owner.Length + name.Length]);

    /// <summary>The runtime's work and the machine's own instructions, done here by name.</summary>
    private static bool Runtime(PreinitWorld w, string label, Val[] args, out Val? result)
    {
        result = null;
        if (label.StartsWith("__x86.i.", StringComparison.Ordinal))
        {
            switch (label["__x86.i.".Length..])
            {
                case "threadblock": result = w.ThreadBlock(); return true;
                case "cli" or "sti" or "barrier" or "cardmark" or "keepalive" or "doubleprecision": return true;
                // Interrupts off, as they are when the image starts.
                case "readflags": result = new Num(0x2); return true;
                // The bit scans of a nonzero value, as the instructions answer;
                // CPUID's presence and a 64-bit exchange are the real machine's.
                case "bsf": result = new Num(System.Numerics.BitOperations.TrailingZeroCount((uint)Number(args[0]))); return true;
                case "bsr": result = new Num(31 - System.Numerics.BitOperations.LeadingZeroCount((uint)Number(args[0]))); return true;
                case "refstore" or "cardstore": w.Store(args[0], w.WordSize, args[1]); return true;
                case "refxchg":
                    result = w.Load(args[0], w.WordSize, signed: false);
                    w.Store(args[0], w.WordSize, args[1]);
                    return true;
                case "refcas":
                    result = w.Load(args[0], w.WordSize, signed: false);
                    if (Same(result, args[1])) w.Store(args[0], w.WordSize, args[2]);
                    return true;
                default: throw new Refused("the machine's '" + label + "'");
            }
        }
        if (!label.StartsWith("m_Runtime_", StringComparison.Ordinal) && !label.StartsWith("m_Gc_", StringComparison.Ordinal)) return false;
        foreach (string alloc in new[] { "Alloc", "AllocLeaf", "AllocObject", "AllocRegion", "AllocRegionSized", "AllocNear", "AllocFast", "AllocFastSized" })
            if (Is(label, "Runtime", alloc) || Is(label, "Gc", alloc))
            {
                if (args.Length == 0 || args[0] is not Num bytes) throw new Refused("an allocation of no known size");
                result = w.Allocate(bytes.Bits);
                return true;
            }
        // THE LIFETIME RULES' FREES AND REGIONS, as the compiler's late passes
        // write them into a unit's runtime: nothing to a world with no heap.
        foreach (string nothing in new[] { "Free", "FreeHeld", "FreeField", "FreeReplaced", "FreeOwnedReplaced", "KeepField", "FreeOwnedElements", "FreeArrayElements",
                     "OwnElements", "FreeStorageInFrame", "RegionLeave", "RegionCatch", "WriteBarrier", "WriteBarrierValues", "CardMark", "CardMarkObject", "ShadeObject" })
            if (Is(label, "Runtime", nothing) || Is(label, "Gc", nothing)) return true;
        if (Is(label, "Runtime", "RegionEnter") || Is(label, "Runtime", "RegionLoop")) { result = new Num(0); return true; }
        foreach (string thrown in new[] { "Capture", "Unhandled", "NullFault", "IndexOutOfRange", "Overflow", "DivideByZero", "InvalidCast", "InvalidCastTo",
                     "InvalidCastOfType", "InvalidCastToType", "ArgumentFailure", "NoValue", "NullUnboxed", "Exit", "ThreadStaticCell", "EnterTypeInitialization" })
            if (Is(label, "Runtime", thrown)) throw new Refused("'" + label + "'");
        return false;
    }

    // ---- one routine ---------------------------------------------------------------

    private Val? Execute(PreinitWorld w, Function f, ObjectFile unit, Val[] args, int depth)
    {
        if (args.Length != f.Params.Count) throw new Refused("'" + f.Name + "' called with " + args.Length + " arguments for " + f.Params.Count);
        Val?[] regs = new Val?[f.RegCount];
        for (int i = 0; i < args.Length; i++) regs[f.Params[i].Id] = Fit(args[i], f.Params[i].Type);
        Dictionary<FrameSlot, Ptr> slots = new(ReferenceEqualityComparer.Instance);
        Ptr frame = w.Frame(0);

        Val Get(Operand o) => o switch
        {
            RegOperand r => regs[r.Reg.Id] ?? throw new Refused("'" + f.Name + "' read " + r.Reg + " before it was set"),
            ImmOperand imm => new Num(Normal(imm.Value, imm.Type)),
            SymOperand sym => w.Address(sym.Name, sym.Offset, unit),
            SlotOperand slot => slots.TryGetValue(slot.Slot, out Ptr? p) ? p : slots[slot.Slot] = w.Frame(slot.Slot.Bytes),
            _ => throw new Refused("an operand " + o),
        };

        Block block = f.Entry;
        Block? from = null;
        while (true)
        {
            Block? next = null;
            // THE JOINS FIRST, together: each takes the value that came from the block before.
            int k = 0;
            if (block.Instrs.Count > 0 && block.Instrs[0].Op == Opcode.Phi)
            {
                List<(VReg, Val)> joined = new();
                for (; k < block.Instrs.Count && block.Instrs[k].Op == Opcode.Phi; k++)
                {
                    Instr phi = block.Instrs[k];
                    int index = -1;
                    for (int t = 0; t < phi.Targets.Count; t++) if (ReferenceEquals(phi.Targets[t], from)) { index = t; break; }
                    if (index < 0) throw new Refused("a join with no value from its predecessor");
                    joined.Add((phi.Dest!, Get(phi.Operands[index])));
                }
                foreach ((VReg d, Val v) in joined) regs[d.Id] = v;
            }
            for (; k < block.Instrs.Count && next is null; k++)
            {
                Instr i = block.Instrs[k];
                if (++w.Steps > PreinitWorld.MostSteps) throw new Refused("more than " + PreinitWorld.MostSteps + " instructions");
                switch (i.Op)
                {
                    case Opcode.Copy:
                        regs[i.Dest!.Id] = Fit(Get(i.Operands[0]), i.Dest.Type);
                        break;
                    case Opcode.Load:
                        regs[i.Dest!.Id] = Fit(w.Load(Offset(Get(i.Operands[0]), i.Offset), i.Size, i.Signed), i.Dest.Type);
                        break;
                    case Opcode.Store:
                        w.Store(Offset(Get(i.Operands[0]), i.Offset), i.Size, Get(i.StoredValue));
                        break;
                    case Opcode.MemCopy:
                        w.Copy(Get(i.Operands[0]), Get(i.Operands[1]), Number(Get(i.Operands[2])));
                        break;
                    case Opcode.MemSet:
                        w.Fill(Get(i.Operands[0]), (byte)Number(Get(i.Operands[1])), Number(Get(i.Operands[2])));
                        break;
                    case Opcode.AtomicSwap or Opcode.AtomicAdd or Opcode.AtomicAnd or Opcode.AtomicOr or Opcode.AtomicXor:
                    {
                        Val at = Offset(Get(i.Operands[0]), i.Offset);
                        Val operand = Get(i.Operands[1]);
                        int size = i.Size != 0 ? i.Size : i.Operands[1].Type.Bytes();
                        Val old = w.Load(at, size, signed: true);
                        Val now = i.Op == Opcode.AtomicSwap ? operand
                            : Arithmetic(i.Op switch { Opcode.AtomicAdd => Opcode.Add, Opcode.AtomicAnd => Opcode.And, Opcode.AtomicOr => Opcode.Or, _ => Opcode.Xor },
                                         old, operand, i.Operands[1].Type);
                        w.Store(at, size, now);
                        if (i.Dest is { } d) regs[d.Id] = Fit(old, d.Type);
                        break;
                    }
                    case Opcode.AtomicCas:
                    {
                        Val at = Offset(Get(i.Operands[0]), i.Offset);
                        Val expect = Get(i.Operands[1]), value = Get(i.Operands[2]);
                        int size = i.Size != 0 ? i.Size : i.Operands[2].Type.Bytes();
                        Val old = w.Load(at, size, signed: true);
                        if (Same(old, expect)) w.Store(at, size, value);
                        if (i.Dest is { } d) regs[d.Id] = Fit(old, d.Type);
                        break;
                    }
                    case Opcode.Fence or Opcode.CompilerFence or Opcode.Pause:
                        break;
                    case Opcode.Call:
                    {
                        if (NativeCall.Is(i)) throw new Refused("a native call from '" + f.Name + "'");
                        Val[] given = new Val[i.Operands.Count];
                        for (int a = 0; a < given.Length; a++) given[a] = Get(i.Operands[a]);
                        Val? returned = Call(w, i.Callee!, given, depth + 1);
                        if (i.Dest is { } d) regs[d.Id] = Fit(returned ?? throw new Refused("'" + i.Callee + "' returned nothing"), d.Type);
                        break;
                    }
                    case Opcode.CallIndirect:
                    {
                        if (Get(i.Operands[0]) is not Code { Offset: 0 } target) throw new Refused("a call through something that is not a routine");
                        Val[] given = new Val[i.Operands.Count - 1];
                        for (int a = 0; a < given.Length; a++) given[a] = Get(i.Operands[a + 1]);
                        Val? returned = Call(w, target.Label, given, depth + 1);
                        if (i.Dest is { } d) regs[d.Id] = Fit(returned ?? throw new Refused("'" + target.Label + "' returned nothing"), d.Type);
                        break;
                    }
                    case Opcode.Ret:
                        return i.Operands.Count > 0 ? Get(i.Operands[0]) : null;
                    case Opcode.Jump:
                        next = i.Targets[0];
                        break;
                    case Opcode.Branch:
                        next = Truth(Get(i.Operands[0])) ? i.Targets[0] : i.Targets[1];
                        break;
                    case Opcode.Switch:
                    {
                        long index = Number(Get(i.Operands[0]));
                        next = index >= 0 && index < i.Targets.Count ? i.Targets[(int)index] : i.Default ?? throw new Refused("a switch with nowhere to go");
                        break;
                    }
                    case Opcode.LabelAddr:
                        regs[i.Dest!.Id] = new Code(f.Name + ":" + i.Targets[0].Label, 0);
                        break;
                    case Opcode.StackPointer or Opcode.FramePointer:
                        regs[i.Dest!.Id] = frame;
                        break;
                    case Opcode.ArrayLength:
                        regs[i.Dest!.Id] = Fit(w.Load(Offset(Get(i.Operands[0]), w.ArrayCountOffset), 4, signed: true), i.Dest.Type);
                        break;
                    case Opcode.InitArrayLength:
                        w.Store(Offset(Get(i.Operands[0]), w.ArrayCountOffset), 4, Get(i.Operands[1]));
                        break;
                    case Opcode.Unreachable or Opcode.Unwind or Opcode.Trap:
                        throw new Refused("'" + f.Name + "' throws or stops");
                    case Opcode.Syscall:
                        throw new Refused("a system call from '" + f.Name + "'");
                    case Opcode.Phi:
                        throw new Refused("a join after the top of a block");
                    default:
                        regs[i.Dest!.Id] = Fit(Compute(w, i, Get), i.Dest.Type);
                        break;
                }
            }
            if (next is null) throw new Refused("'" + f.Name + "' ran off the end of " + block.Label);
            from = block;
            block = next;
        }
    }

    // ---- values --------------------------------------------------------------------

    private static Val Offset(Val address, long offset) => offset == 0 ? address : address switch
    {
        Ptr p => p with { Offset = p.Offset + offset },
        Num { Bits: 0 } => throw new Refused("a null dereference"),
        _ => throw new Refused("an address that is a number"),
    };

    private static long Number(Val v) => v is Num n ? n.Bits : throw new Refused("an address used as a number");

    private static bool Truth(Val v) => v switch
    {
        Num n => n.Bits != 0,
        Ptr or Code => true,
        _ => throw new Refused("a test of " + v),
    };

    private static bool Same(Val a, Val b) => (a, b) switch
    {
        (Num x, Num y) => x.Bits == y.Bits,
        (Ptr x, Ptr y) => ReferenceEquals(x.Region, y.Region) && x.Offset == y.Offset,
        (Code x, Code y) => x.Label == y.Label && x.Offset == y.Offset,
        (Ptr or Code, Num { Bits: 0 }) or (Num { Bits: 0 }, Ptr or Code) => false,
        _ => throw new Refused("an address compared with a number"),
    };

    /// <summary>A number as a register of this type holds it: an int sign-extended, a float's bits as they are.</summary>
    private static long Normal(long bits, IrType type) => type switch
    {
        IrType.I32 => (int)bits,
        IrType.F32 => (uint)bits,
        _ => bits,
    };

    private static Val Fit(Val v, IrType type) => v is Num n ? new Num(Normal(n.Bits, type)) : v;

    private static double F64(long bits) => BitConverter.Int64BitsToDouble(bits);
    private static float F32(long bits) => BitConverter.Int32BitsToSingle((int)bits);
    private static long Bits(double d, IrType type) => type == IrType.F32 ? (uint)BitConverter.SingleToInt32Bits((float)d) : BitConverter.DoubleToInt64Bits(d);
    private static double Real(long bits, IrType type) => type == IrType.F32 ? F32(bits) : F64(bits);

    /// <summary>Every arithmetic, comparison and conversion, on numbers -- and on addresses where an address stays one.</summary>
    private static Val Compute(PreinitWorld w, Instr i, Func<Operand, Val> get)
    {
        IrType of = i.Operands[0].Type;
        Val a = get(i.Operands[0]);
        Val? b = i.Operands.Count > 1 ? get(i.Operands[1]) : null;
        switch (i.Op)
        {
            case Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.DivS or Opcode.DivU or Opcode.RemS or Opcode.RemU
                or Opcode.And or Opcode.Or or Opcode.Xor or Opcode.Shl or Opcode.ShrS or Opcode.ShrU:
                return Arithmetic(i.Op, a, b!, of);
            case Opcode.Eq or Opcode.Ne:
                return new Num(Same(a, b!) == (i.Op == Opcode.Eq) ? 1 : 0);
            case Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU:
                return new Num(Compare(i.Op, a, b!, of) ? 1 : 0);
        }
        // Past here, numbers only -- but a pointer may pass through a widening or a narrowing whole.
        if (a is Ptr or Code)
        {
            if (i.Op is Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32) return a;
            throw new Refused("an address as a number (" + i.Op + ")");
        }
        long x = ((Num)a).Bits;
        long y = b is Num nb ? nb.Bits : 0;
        IrType to = i.Dest!.Type;
        switch (i.Op)
        {
            case Opcode.Neg: return new Num(-x);
            case Opcode.Not: return new Num(~x);
            case Opcode.ByteSwap:
                return new Num(of == IrType.I64 ? (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ulong)x)
                                                : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((int)x));
            case Opcode.FAdd: return new Num(Bits(Real(x, of) + Real(y, of), of));
            case Opcode.FSub: return new Num(Bits(Real(x, of) - Real(y, of), of));
            case Opcode.FMul: return new Num(Bits(Real(x, of) * Real(y, of), of));
            case Opcode.FDiv: return new Num(Bits(Real(x, of) / Real(y, of), of));
            case Opcode.FNeg: return new Num(of == IrType.F32 ? (uint)((int)x ^ int.MinValue) : x ^ long.MinValue);
            case Opcode.FSqrt: return new Num(Bits(Math.Sqrt(Real(x, of)), of));
            case Opcode.FEq: return new Num(Real(x, of) == Real(y, of) ? 1 : 0);
            case Opcode.FNe: return new Num(Real(x, of) != Real(y, of) ? 1 : 0);
            case Opcode.FLt: return new Num(Real(x, of) < Real(y, of) ? 1 : 0);
            case Opcode.FLe: return new Num(Real(x, of) <= Real(y, of) ? 1 : 0);
            case Opcode.FGt: return new Num(Real(x, of) > Real(y, of) ? 1 : 0);
            case Opcode.FGe: return new Num(Real(x, of) >= Real(y, of) ? 1 : 0);
            case Opcode.SExt8: return new Num((sbyte)x);
            case Opcode.SExt16: return new Num((short)x);
            case Opcode.ZExt8: return new Num((byte)x);
            case Opcode.ZExt16: return new Num((ushort)x);
            case Opcode.Trunc64: return new Num((int)x);
            case Opcode.SExt32: return new Num((int)x);
            case Opcode.ZExt32: return new Num((uint)x);
            case Opcode.FConv: return new Num(Bits(Real(x, of), to));
            case Opcode.IToF: return new Num(Bits(of == IrType.I64 ? x : (int)x, to));
            case Opcode.UToF: return new Num(Bits(of == IrType.I64 ? (double)(ulong)x : (uint)x, to));
            case Opcode.FToI:
            {
                // AS THE MACHINE TRUNCATES: out of range or NaN, the integer
                // indefinite -- the least value of the width.
                double d = Real(x, of);
                if (to == IrType.I64) return new Num(double.IsNaN(d) || d >= 9.2233720368547758e18 || d < -9.2233720368547758e18 ? long.MinValue : (long)d);
                return new Num(double.IsNaN(d) || d >= 2147483648.0 || d <= -2147483649.0 ? int.MinValue : (int)d);
            }
            case Opcode.FToU:
            {
                double d = Real(x, of);
                if (double.IsNaN(d) || d < 0 || d >= (to == IrType.I64 ? 1.8446744073709552e19 : 4294967296.0)) throw new Refused("a float out of an unsigned range");
                return new Num(to == IrType.I64 ? (long)(ulong)d : (uint)d);
            }
            case Opcode.Bits: return new Num(x);
            default: throw new Refused("the instruction " + i.Op);
        }
    }

    private static Val Arithmetic(Opcode op, Val a, Val b, IrType of)
    {
        // AN ADDRESS STAYS ONE through adding and taking away a number, and
        // two in one region taken apart are the bytes between them.
        if (a is Ptr or Code || b is Ptr or Code)
        {
            switch (op, a, b)
            {
                case (Opcode.Add, Ptr p, Num n): return p with { Offset = p.Offset + n.Bits };
                case (Opcode.Add, Num n, Ptr p): return p with { Offset = p.Offset + n.Bits };
                case (Opcode.Sub, Ptr p, Num n): return p with { Offset = p.Offset - n.Bits };
                case (Opcode.Add, Code c, Num n): return c with { Offset = c.Offset + n.Bits };
                case (Opcode.Sub, Ptr p, Ptr q) when ReferenceEquals(p.Region, q.Region): return new Num(p.Offset - q.Offset);
                // An alignment test or a rounding, within what the region's start is aligned to.
                case (Opcode.And, Ptr p, Num n) when n.Bits >= 0 && n.Bits < Aligned(p): return new Num(p.Offset & n.Bits);
                case (Opcode.And, Ptr p, Num n) when n.Bits < 0 && ~n.Bits < Aligned(p): return p with { Offset = p.Offset & n.Bits };
                default: throw new Refused("an address in " + op);
            }
        }
        long x = ((Num)a).Bits, y = ((Num)b).Bits;
        bool wide = of == IrType.I64;
        int shift = (int)(y & (wide ? 63 : 31));
        long r = op switch
        {
            Opcode.Add => x + y,
            Opcode.Sub => x - y,
            Opcode.Mul => x * y,
            Opcode.And => x & y,
            Opcode.Or => x | y,
            Opcode.Xor => x ^ y,
            Opcode.Shl => x << shift,
            Opcode.ShrS => wide ? x >> shift : (int)x >> shift,
            Opcode.ShrU => wide ? (long)((ulong)x >> shift) : (uint)x >> shift,
            _ when y == 0 => throw new Refused("a division by zero"),
            Opcode.DivS => wide ? (x == long.MinValue && y == -1 ? x : x / y) : ((int)x == int.MinValue && (int)y == -1 ? (int)x : (int)x / (int)y),
            Opcode.RemS => wide ? (y == -1 ? 0 : x % y) : ((int)y == -1 ? 0 : (int)x % (int)y),
            Opcode.DivU => wide ? (long)((ulong)x / (ulong)y) : (uint)x / (uint)y,
            Opcode.RemU => wide ? (long)((ulong)x % (ulong)y) : (uint)x % (uint)y,
            _ => throw new Refused("the instruction " + op),
        };
        return new Num(Normal(r, of));
    }

    /// <summary>What a region's start is aligned to: an object the link lays down at sixteen, a unit's datum at its section's.</summary>
    private static long Aligned(Ptr p) => p.Region.Kind == PreinitWorld.RegionKind.Heap ? 16 : p.Region.Align;

    private static bool Compare(Opcode op, Val a, Val b, IrType of)
    {
        bool unsigned = op is Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU;
        int order;
        switch (a, b)
        {
            case (Num x, Num y):
                order = unsigned
                    ? (of == IrType.I64 ? ((ulong)x.Bits).CompareTo((ulong)y.Bits) : ((uint)x.Bits).CompareTo((uint)y.Bits))
                    : x.Bits.CompareTo(y.Bits);
                break;
            case (Ptr x, Ptr y) when ReferenceEquals(x.Region, y.Region) && unsigned:
                order = x.Offset.CompareTo(y.Offset);
                break;
            // A real address is past every small number, unsigned.
            case (Ptr, Num { Bits: >= 0 and < 4096 }) when unsigned: order = 1; break;
            case (Num { Bits: >= 0 and < 4096 }, Ptr) when unsigned: order = -1; break;
            default: throw new Refused("an address ordered against " + b);
        }
        return op switch
        {
            Opcode.LtS or Opcode.LtU => order < 0,
            Opcode.LeS or Opcode.LeU => order <= 0,
            Opcode.GtS or Opcode.GtU => order > 0,
            _ => order >= 0,
        };
    }
}
