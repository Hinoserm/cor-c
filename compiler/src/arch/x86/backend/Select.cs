#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Instruction selection: one IR function into machine instructions over
/// virtual registers.
///
/// The output is in a form the allocator can work with directly: every
/// two-address instruction is preceded by a move into its destination (the
/// allocator coalesces the move away when it can), fixed-register
/// instructions read and write the physical registers by name, and I64
/// values are two virtual registers from here on. Floating-point values
/// never enter a virtual register at all: each lives in a frame slot and
/// every operation is fld / op / fstp, as the design says.
/// </summary>
internal sealed partial class Selector
{
    private readonly Function _f;
    private readonly MFunction _m;
    private readonly List<string> _errors;
    // BY THE REGISTER'S NUMBER, in arrays: a function's registers are
    // numbered from 0 (Function.NewReg), and a table of every one of them
    // keyed by its number was an entry, a bucket and a hash for what is a
    // slot. The largest function's tables are what selection holds at most.
    private readonly ByRegister<MReg> _lo;
    private readonly ByRegister<MReg> _hi;
    private readonly Dictionary<Block, MBlock> _heads = new();
    private readonly UseCounts _useCount;
    private readonly Dictionary<VReg, Instr?> _definitions = new();
    /// <summary>
    /// Every definition of a register defined more than once, or of a
    /// parameter, in instruction order. A register defined once has its one
    /// definition in _definitions and no list: most registers are, and a list
    /// each was an object for nearly every instruction selected.
    /// </summary>
    private readonly Dictionary<VReg, List<Instr>> _rangeDefinitions = new();
    private int _rangeVisits;
    private MBlock _cur = null!;
    private Block _sourceBlock = null!;
    private int _splits;
    private readonly bool _automaticPacked;
    /// <summary>Whether SSE may be selected anywhere in the function (MFunction.SseState): code whose thread owns its state (Lowering.OwnsFpuState), where --cpu has it.</summary>
    private readonly bool _packedSse;

    /// <summary>
    /// THE KEPT STATE SELECTION IS INSIDE (PackedRegions): 1 the x87's save,
    /// 2 FXSAVE's, which keeps SSE's too, 0 none. Hosted code may pack anywhere already (_automaticPacked); a
    /// freestanding function only between a block's save and its give-back,
    /// and what is selected there says so (MInstr.OwnsState). The blocks a
    /// selection is inside, innermost lowest, two bits each (PackedRegions):
    /// an inner block's give-back is back inside the outer one.
    /// </summary>
    private int _packedState;
    private int _packedNow => (_packedState & 3) is var now and not 0 ? now : _packedBase;

    /// <summary>
    /// The state a method's $inside copy runs inside (Function.InsideState),
    /// outside any block of its own: its caller's, saved by FXSAVE where
    /// --cpu has SSE (SettlePackedSaves: a call to such a copy), else the
    /// x87's. 0 for every other function.
    /// </summary>
    private readonly int _packedBase;

    /// <summary>Whether a packed group may be selected here: code whose thread owns its state (Lowering.OwnsFpuState), or inside a packed block.</summary>
    private bool PackedHere => _automaticPacked || _packedNow != 0;

    /// <summary>Whether SSE may be selected here: code whose thread owns its state, or inside a state FXSAVE kept.</summary>
    private bool SseHere => _packedSse || _packedNow == 2;

    private static readonly MReg Eax = MReg.Of(Gpr.Eax);
    private static readonly MReg Ecx = MReg.Of(Gpr.Ecx);
    private static readonly MReg Edx = MReg.Of(Gpr.Edx);
    private static readonly MReg Ebx = MReg.Of(Gpr.Ebx);
    private static readonly MReg Esi = MReg.Of(Gpr.Esi);
    private static readonly MReg Edi = MReg.Of(Gpr.Edi);
    private static readonly MReg Esp = MReg.Of(Gpr.Esp);
    private static readonly MReg Ebp = MReg.Of(Gpr.Ebp);

    /// <summary>
    /// Code for a shared object: the card and barrier stubs read the card
    /// table by its absolute address, which such code may not, so their
    /// intrinsics are the runtime's own routines, called as any function is.
    /// </summary>
    private bool _positionIndependent;

    /// <summary>A Windows image: the thread block is in a TLS slot of the TEB, not behind GS.</summary>
    private bool _windowsTls;

    /// <summary>The word holding that slot's number, which the Windows runtime fills from TlsAlloc.</summary>
    /// <summary>
    /// WINDOWS' TLS SLOTS, wherever the system keeps them: the offset in the
    /// thread's own block (FS) of the word to start from, and what to add to
    /// that word to reach the runtime's slot. NT keeps the slots in the TEB,
    /// so the word is the TEB's own address (0x18) and the offset 0xE10 + 4 *
    /// slot; Windows 9x keeps them in an array the thread block points to
    /// (0x2C) and the offset is 4 * slot. The runtime fills both as it takes
    /// its slot (Sys.SetThreadBlockSlot), and one sequence serves either.
    /// </summary>
    public const string TlsBaseSymbol = "__corsac_tls_base";
    public const string TlsOffsetSymbol = "__corsac_tls_offset";

    /// <summary>
    /// The runtime's copy and fill around the caches (Runtime.StreamCopy,
    /// StreamFill), where the image has them and this code owns the x87
    /// state they borrow: a copy or fill whose length is known only as it
    /// runs calls them at StreamBytes and over (StreamedOr). Null otherwise.
    /// </summary>
    private string? _streamCopy, _streamFill;

    /// <summary>
    /// The streaming length: beyond what a K7's or a K8's level-two cache
    /// (256 KB to 1 MB) would keep of the block anyway. A shorter one is read
    /// again soon, and stored around the cache it would be fetched back from
    /// memory; the inline REP MOVSD is the copy for it.
    /// </summary>
    private const int StreamBytes = 256 * 1024;

    private Selector(Function f, List<string> errors, bool automaticPacked, bool sse)
    {
        _f = f;
        _m = new MFunction(f) { AutomaticPacked = automaticPacked, SseState = sse };
        _packedSse = sse;
        _lo = new ByRegister<MReg>(f.RegCount);
        _hi = new ByRegister<MReg>(f.RegCount);
        _useCount = new UseCounts(f.RegCount);
        _errors = errors;
        _automaticPacked = automaticPacked;
        X86Cpu cpu = Target.Current.X86Profile;
        _packedBase = automaticPacked || !f.InsideState || cpu.Fpu == "none" ? 0 : cpu.Has(X86Feature.Sse) ? 2 : 1;
        foreach (VReg parameter in f.Params) _definitions[parameter] = null;
        foreach (Block b in f.Blocks)
        foreach (Instr instruction in b.Instrs)
            if (instruction.Dest is { } dest)
            {
                if (!_definitions.TryGetValue(dest, out Instr? prior))
                {
                    _definitions[dest] = instruction;
                    continue;
                }
                _definitions[dest] = null;
                if (!_rangeDefinitions.TryGetValue(dest, out List<Instr>? definitions))
                {
                    _rangeDefinitions[dest] = definitions = new();
                    if (prior is not null) definitions.Add(prior);
                }
                definitions.Add(instruction);
            }
    }

    /// <summary>
    /// A PACKED OPERATION (Lowering.PackedIntrinsic, MachineIntrinsics.
    /// PackedOp and the rest): its operands taken from the packed registers
    /// that hold them, or loaded into one (PackedIn), the instruction on a
    /// register of its own, and the result kept there as its buffer's
    /// (PackedOut) for the next operation to take. Probed: the
    /// block's variant that holds it runs only where the processor has its
    /// features (Sys.PackedChoose).
    /// </summary>
    private void SelectPackedOp(string name, Instr i)
    {
        MInstr P(MOp op, params MOperand[] operands) => Emit(new MInstr(op, operands) { Probed = true });
        MImm N(int register) => Imm(register);
        Operand Address(int k) => i.Operands[k];
        string[] part = name[MachineIntrinsics.PackedOpPrefix.Length..].Split('.');
        switch (part[0])
        {
            case "mov":
            {
                int value = PackedIn(false, Address(1), 8);
                PackedOut(false, Address(0), 8, value, copy: true);
                break;
            }
            case "op":
            case "3d":
            {
                int a = PackedIn(false, Address(1), 8);
                (MOperand b, int pinned) = PackedSourceOf(false, Address(2), 8, a);
                int made = PackedResult(false, a, pinned);
                if (part[0] == "op") P(MOp.MmxOp, Imm(Convert.ToByte(part[1], 16)), b, N(made));
                else P(MOp.ThreeDNowOp, Imm(Convert.ToByte(part[1], 16)), b, N(made));
                PackedOut(false, Address(0), 8, made, copy: false);
                break;
            }
            case "shift":
            {
                int a = PackedIn(false, Address(1), 8);
                int made = PackedResult(false, a, -1);
                P(MOp.MmxShiftImm, Imm(Convert.ToByte(part[1], 16)), Imm(int.Parse(part[2])), Imm(int.Parse(part[3])), N(made));
                PackedOut(false, Address(0), 8, made, copy: false);
                break;
            }
            case "shuffle":
            {
                (MOperand a, int pinned) = PackedSourceOf(false, Address(1), 8, -1);
                int made = PackedFree(false, pinned, -1);
                P(MOp.MmxShuffleW, a, Imm(int.Parse(part[1])), N(made));
                PackedOut(false, Address(0), 8, made, copy: false);
                break;
            }
            case "extract":
            {
                int a = PackedIn(false, Address(0), 8);
                P(MOp.MmxExtractW, i.Dest is not null ? Lo(i.Dest) : Temp(), Imm(int.Parse(part[1])), N(a));
                break;
            }
            case "insert":
            {
                int a = PackedIn(false, Address(1), 8);
                int made = PackedResult(false, a, -1);
                P(MOp.MmxInsertW, R(i.Operands[2]), Imm(int.Parse(part[1])), N(made));
                PackedOut(false, Address(0), 8, made, copy: false);
                break;
            }
            case "storent":
            {
                int value = PackedIn(false, Address(1), 8);
                PackedStoredTo(Address(0), 8);
                P(MOp.MmxStoreNt, PackedAt(Address(0)), N(value));
                break;
            }
            case "fence":
                P(MOp.Sfence);
                return;
            // A CACHE LINE ASKED FOR AHEAD: no register touched, nothing
            // given up (MachineIntrinsics.PackedPrefetch).
            case "prefetch":
                P(part[1] switch { "nta" => MOp.PrefetchNta, "write" => MOp.PrefetchWrite, _ => MOp.PrefetchLine }, PackedAt(Address(0)));
                return;
            // MOVD: four bytes into a register of their own, the high four
            // zero -- a register holding any of them newer than memory
            // written back first -- and the result kept as its buffer's.
            case "ld32":
            {
                PackedLetGo(PackedPlaceOf(Address(1)), 4, replaced: false);
                int made = PackedFree(false, -1, -1);
                P(MOp.MmxLoadD, PackedAt(Address(1)), N(made));
                PackedOut(false, Address(0), 8, made, copy: false);
                break;
            }
            case "st32":
            {
                int value = PackedIn(false, Address(1), 8);
                PackedStoredTo(Address(0), 4);
                P(MOp.MmxStoreD, PackedAt(Address(0)), N(value));
                break;
            }
            // THE XMM REGISTERS' (SSE, SSE2). They are not the x87's, and
            // leave the MMX registers as they were.
            case "x":
            {
                int a = PackedIn(true, Address(1), 16);
                (MOperand b, int pinned) = PackedSourceOf(true, Address(2), 16, a);
                int made = PackedResult(true, a, pinned);
                P(MOp.XmmOpRR, Imm(Convert.ToByte(part[1], 16)), Imm(Convert.ToByte(part[2], 16)), N(made), b);
                PackedOut(true, Address(0), 16, made, copy: false);
                return;
            }
            case "xshift":
            {
                int a = PackedIn(true, Address(1), 16);
                int made = PackedResult(true, a, -1);
                P(MOp.XmmShiftImm, Imm(Convert.ToByte(part[1], 16)), Imm(int.Parse(part[2])), Imm(int.Parse(part[3])), N(made));
                PackedOut(true, Address(0), 16, made, copy: false);
                return;
            }
            case "xshuffle":
            {
                (MOperand a, int pinned) = PackedSourceOf(true, Address(1), 16, -1);
                int made = PackedFree(true, pinned, -1);
                P(MOp.XmmOpRR, Imm(Convert.ToByte(part[1], 16)), Imm(0x70), N(made), a, Imm(int.Parse(part[2])));
                PackedOut(true, Address(0), 16, made, copy: false);
                return;
            }
            case "xstorent":
            {
                int value = PackedIn(true, Address(1), 16);
                PackedStoredTo(Address(0), 16);
                P(MOp.XmmStoreNt, PackedAt(Address(0)), N(value));
                return;
            }
            case "xextract":
            {
                int a = PackedIn(true, Address(0), 16);
                P(MOp.XmmExtractW, i.Dest is not null ? Lo(i.Dest) : Temp(), Imm(int.Parse(part[1])), N(a));
                return;
            }
            case "xinsert":
            {
                int a = PackedIn(true, Address(1), 16);
                int made = PackedResult(true, a, -1);
                P(MOp.XmmInsertW, R(i.Operands[2]), Imm(int.Parse(part[1])), N(made));
                PackedOut(true, Address(0), 16, made, copy: false);
                return;
            }
            case "xf":
            case "f2":
            {
                // Four floats (xf), or two (f2: a Packed64's, its eight bytes
                // in the register's low half, MOVLPS in and out).
                int bytes = part[0] == "xf" ? 16 : 8;
                byte prefix = Convert.ToByte(part[1], 16), op = Convert.ToByte(part[2], 16);
                int immediate = int.Parse(part[3]);
                bool unary = part[4] == "1";
                int made;
                if (unary)
                {
                    (MOperand a, int pinned) = PackedSourceOf(true, Address(1), bytes, -1);
                    made = PackedFree(true, pinned, -1);
                    if (immediate >= 0) P(MOp.XmmOpRR, Imm(prefix), Imm(op), N(made), a, Imm(immediate));
                    else P(MOp.XmmOpRR, Imm(prefix), Imm(op), N(made), a);
                }
                else
                {
                    int a = PackedIn(true, Address(1), bytes);
                    (MOperand b, int pinned) = PackedSourceOf(true, Address(2), bytes, a);
                    made = PackedResult(true, a, pinned);
                    if (immediate >= 0) P(MOp.XmmOpRR, Imm(prefix), Imm(op), N(made), b, Imm(immediate));
                    else P(MOp.XmmOpRR, Imm(prefix), Imm(op), N(made), b);
                }
                PackedOut(true, Address(0), bytes, made, copy: false);
                return;
            }
            default:
                Error("unknown packed operation " + name);
                return;
        }
        _mmxDirty = true;
    }

    // THE PACKED REGISTERS A BLOCK'S OPERATIONS KEEP THEIR VALUES IN
    // (SelectPackedOp): mm0-mm7 and xmm0-xmm7, each the bytes at a place --
    // a buffer's, a Packed64 or Packed128 local's, a Load's source -- as its
    // last operation left them, written there only when they must be
    // (PackedRegistersBefore, an eviction). The next operation on them takes
    // them from the register: a chain of operations is a chain of registers,
    // as a hand-written loop is. Physical registers: the allocator gives the
    // general registers only, and nothing it inserts touches these.
    //
    // A PLACE is where the bytes are, not the register that names them
    // (PackedPlaceOf): a frame slot and an offset into it, or an address
    // register and an offset from it, through the copies and constant
    // additions that make one address of another. Two names of one place are
    // one entry; two places that share a byte are never both held (each
    // operation lets the other go first), so a register is never older than
    // memory another name reads.
    private sealed class PackedRegisters
    {
        /// <summary>The operand the value's address is (its write-back's), null where the register holds nothing.</summary>
        public readonly Operand?[] Name = new Operand?[8];
        public readonly object?[] Root = new object?[8];
        public readonly long[] Offset = new long[8];
        public readonly int[] Bytes = new int[8];
        public readonly bool[] Dirty = new bool[8];
        public readonly bool[] Buffer = new bool[8];
        public readonly long[] Used = new long[8];

        public bool Overlaps(int r, object root, long offset, int bytes)
            => Name[r] is not null && ReferenceEquals(Root[r], root) && Offset[r] < offset + bytes && offset < Offset[r] + Bytes[r];

        public void Clear()
        {
            for (int r = 0; r < 8; r++) { Name[r] = null; Root[r] = null; Dirty[r] = false; }
        }
    }

    private readonly PackedRegisters _mm = new(), _xmm = new();
    private long _packedClock;

    /// <summary>Whether either file may hold anything: where neither does, nothing before an instruction has anything to give up.</summary>
    private bool _packedHeld;

    private PackedRegisters PackedFile(bool xmm) => xmm ? _xmm : _mm;

    private readonly record struct PackedPlace(object Root, long Offset, bool Buffer);

    /// <summary>Every register's place, as its single definition makes it (PackedPlaceOf); and the slots whose address goes anywhere but a packed operation's address, a load's or store's, or another address of them.</summary>
    private Dictionary<VReg, (object Root, long Offset)>? _packedPlaces;
    private HashSet<FrameSlot>? _packedEscaped;

    /// <summary>
    /// The place an operand names: a buffer where it is in a frame slot
    /// whose address nothing else is given -- not a call, a stored value, a
    /// register set more than once -- so that no other address of it can be
    /// made, and an operation through any of its names is the only way its
    /// bytes change; else memory any store may change.
    /// </summary>
    private PackedPlace PackedPlaceOf(Operand o)
    {
        if (_packedPlaces is null) PackedFindPlaces();
        (object root, long offset) = o switch
        {
            SlotOperand s => (s.Slot, 0L),
            RegOperand r => PackedPlaceOfRegister(r.Reg),
            _ => ((object)o, 0L),
        };
        return new(root, offset, root is FrameSlot slot && !_packedEscaped!.Contains(slot));
    }

    /// <summary>Whether a register's value is the same through the whole function: defined once, or a parameter never set.</summary>
    private bool PackedStable(VReg v)
        => _definitions.TryGetValue(v, out Instr? d) && (d is not null || !_rangeDefinitions.ContainsKey(v));

    private (object Root, long Offset) PackedPlaceOfRegister(VReg v)
    {
        if (_packedPlaces!.TryGetValue(v, out var known)) return known;
        // A cycle's answer, and every register's not made of another: itself.
        _packedPlaces[v] = (v, 0);
        (object Root, long Offset) made = (v, 0);
        if (_definitions.TryGetValue(v, out Instr? d) && d is not null)
        {
            (object Root, long Offset)? Of(Operand o) => o switch
            {
                SlotOperand s => (s.Slot, 0L),
                RegOperand r => PackedPlaceOfRegister(r.Reg) is var p && (p.Root is FrameSlot || p.Root is VReg w && PackedStable(w)) ? p : null,
                _ => null,
            };
            long? Constant(Operand o) => o switch
            {
                ImmOperand c => c.Value,
                RegOperand r when _definitions.TryGetValue(r.Reg, out Instr? k) && k is { Op: Opcode.Copy, Operands: [ImmOperand c] } => c.Value,
                _ => null,
            };
            // Another address of a place that does not move: the slot, or a
            // register set once. One set again would leave this one naming
            // its old value's place under its new value's name.
            if (d.Op == Opcode.Copy && d.Operands.Count == 1 && Of(d.Operands[0]) is { } same) made = same;
            else if (d.Op == Opcode.Add && d.Operands.Count == 2)
            {
                if (Of(d.Operands[0]) is { } a && Constant(d.Operands[1]) is long n) made = (a.Root, a.Offset + n);
                else if (Of(d.Operands[1]) is { } b && Constant(d.Operands[0]) is long m) made = (b.Root, b.Offset + m);
            }
        }
        _packedPlaces[v] = made;
        return made;
    }

    private void PackedFindPlaces()
    {
        _packedPlaces = new(ReferenceEqualityComparer.Instance);
        _packedEscaped = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in _f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                bool packed = i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal);
                bool inserts = packed && (i.Callee!.StartsWith(MachineIntrinsics.PackedOpPrefix + "insert.", StringComparison.Ordinal)
                    || i.Callee.StartsWith(MachineIntrinsics.PackedOpPrefix + "xinsert.", StringComparison.Ordinal));
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    FrameSlot? slot = i.Operands[k] switch
                    {
                        SlotOperand s => s.Slot,
                        RegOperand r => PackedPlaceOfRegister(r.Reg).Root as FrameSlot,
                        _ => null,
                    };
                    if (slot is null) continue;
                    bool address = packed && !(inserts && k == 2)
                        || i.Op is Opcode.Load && k == 0
                        || i.Op is Opcode.Store && k == 0
                        || i.Op is Opcode.MemCopy or Opcode.MemSet && k < 2
                        || i.Op is >= Opcode.Eq and <= Opcode.GeU
                        || i.Op is Opcode.Copy or Opcode.Add && i.Dest is { } d && ReferenceEquals(PackedPlaceOfRegister(d).Root, slot);
                    if (!address) _packedEscaped.Add(slot);
                }
            }
    }

    /// <summary>The memory an entry's bytes are written back to, or loaded from: its name's, made anew for each instruction that reads it.</summary>
    private MMem PackedAt(Operand name)
        => name is SlotOperand s ? MMem.Frame(_m.Frame.SlotOffset(s.Slot)) : new MMem(R(name), 0);

    private static int PackedFind(PackedRegisters file, PackedPlace place)
    {
        for (int r = 0; r < 8; r++)
            if (file.Name[r] is not null && ReferenceEquals(file.Root[r], place.Root) && file.Offset[r] == place.Offset) return r;
        return -1;
    }

    /// <summary>A register's value written back to its place, where its last operation left it there not.</summary>
    private void PackedWriteBack(bool xmm, int r)
    {
        PackedRegisters file = PackedFile(xmm);
        if (!file.Dirty[r] || file.Name[r] is not { } name) return;
        if (!xmm) Emit(new MInstr(MOp.MmxStore, PackedAt(name), Imm(r)) { Probed = true });
        else Emit(new MInstr(file.Bytes[r] == 16 ? MOp.XmmStore : MOp.XmmStoreLow, PackedAt(name), Imm(r)) { Probed = true });
        file.Dirty[r] = false;
    }

    /// <summary>
    /// Every register of either file holding bytes of [place, place +
    /// bytes) let go -- written back first, unless `replaced` and the range
    /// covers all of its bytes, which the operation is about to make anew --
    /// but `keep`'s, which the operation still reads: written back and
    /// held, clean. Clean registers may share bytes, all as memory has them;
    /// a dirty one shares none.
    /// </summary>
    private void PackedLetGo(PackedPlace place, int bytes, bool replaced, bool keepXmm = false, int keep = -1)
    {
        for (int f = 0; f < 2; f++)
        {
            bool xmm = f == 1;
            PackedRegisters file = PackedFile(xmm);
            for (int r = 0; r < 8; r++)
            {
                if (!file.Overlaps(r, place.Root, place.Offset, bytes)) continue;
                if (xmm == keepXmm && r == keep) { if (!replaced) PackedWriteBack(xmm, r); continue; }
                bool covered = place.Offset <= file.Offset[r] && file.Offset[r] + file.Bytes[r] <= place.Offset + bytes;
                if (!(replaced && covered)) PackedWriteBack(xmm, r);
                file.Name[r] = null;
                file.Dirty[r] = false;
            }
        }
    }

    /// <summary>A register for a value: a free one, else the least recently used not `pinned` (or `pinned2`), written back first.</summary>
    private int PackedFree(bool xmm, int pinned, int pinned2)
    {
        PackedRegisters file = PackedFile(xmm);
        int pick = -1;
        for (int r = 0; r < 8; r++)
        {
            if (r == pinned || r == pinned2) continue;
            if (file.Name[r] is null) { pick = r; break; }
            if (pick < 0 || file.Used[r] < file.Used[pick]) pick = r;
        }
        PackedWriteBack(xmm, pick);
        file.Name[pick] = null;
        file.Dirty[pick] = false;
        file.Used[pick] = ++_packedClock;
        return pick;
    }

    private void PackedHold(bool xmm, int r, Operand name, PackedPlace place, int bytes, bool dirty)
    {
        PackedRegisters file = PackedFile(xmm);
        file.Name[r] = name;
        file.Root[r] = place.Root;
        file.Offset[r] = place.Offset;
        file.Bytes[r] = bytes;
        file.Buffer[r] = place.Buffer;
        file.Dirty[r] = dirty;
        file.Used[r] = ++_packedClock;
        _packedHeld = true;
    }

    /// <summary>The register holding the bytes at `address`, loaded there if no register does -- every other holding any of them written back and let go first. `pinned`: a register the operation still reads, kept.</summary>
    private int PackedIn(bool xmm, Operand address, int bytes, int pinned = -1)
    {
        PackedRegisters file = PackedFile(xmm);
        PackedPlace place = PackedPlaceOf(address);
        int r = PackedFind(file, place);
        if (r >= 0 && file.Bytes[r] >= bytes) { file.Used[r] = ++_packedClock; return r; }
        PackedLetGo(place, bytes, replaced: false, xmm, pinned);
        r = PackedFree(xmm, pinned, -1);
        if (!xmm) Emit(new MInstr(MOp.MmxLoad, PackedAt(address), Imm(r)) { Probed = true });
        else Emit(new MInstr(bytes == 16 ? MOp.XmmLoad : MOp.XmmLoadLow, Imm(r), PackedAt(address)) { Probed = true });
        PackedHold(xmm, r, address, place, bytes, dirty: false);
        return r;
    }

    /// <summary>An operation's second operand: its register where one holds it, else the memory itself, which the instruction reads where it is.</summary>
    private (MOperand Source, int Pinned) PackedSourceOf(bool xmm, Operand address, int bytes, int pinned)
    {
        PackedRegisters file = PackedFile(xmm);
        PackedPlace place = PackedPlaceOf(address);
        int r = PackedFind(file, place);
        if (r >= 0 && file.Bytes[r] >= bytes) { file.Used[r] = ++_packedClock; return (Imm(r), r); }
        // Not SSE's memory forms, which fault on sixteen bytes not aligned so
        // -- a Store's target, a local, may be anywhere -- and read sixteen
        // where a Packed64's are eight: MOVUPS or MOVLPS into a register. Nor
        // memory a register holds any of its bytes newer than.
        if (xmm || PackedAnyOverlaps(place, bytes)) { int loaded = PackedIn(xmm, address, bytes, pinned); return (Imm(loaded), loaded); }
        return (PackedAt(address), -1);
    }

    private bool PackedAnyOverlaps(PackedPlace place, int bytes)
    {
        for (int r = 0; r < 8; r++)
            if (_mm.Overlaps(r, place.Root, place.Offset, bytes) || _xmm.Overlaps(r, place.Root, place.Offset, bytes)) return true;
        return false;
    }

    /// <summary>The register an operation's result is made in: a copy of its first operand's, which the operation then works on.</summary>
    private int PackedResult(bool xmm, int first, int pinned)
    {
        int made = PackedFree(xmm, first, pinned);
        if (!xmm) Emit(new MInstr(MOp.MmxOp, Imm(0x6F), Imm(first), Imm(made)) { Probed = true });
        else Emit(new MInstr(MOp.XmmOpRR, Imm(0), Imm(0x28), Imm(made), Imm(first)) { Probed = true });
        return made;
    }

    /// <summary>
    /// The result in register `r` as the bytes at `address`: kept there,
    /// written back later, for a buffer, which no address but its own names;
    /// stored at once anywhere else -- a Store's target may be any memory --
    /// and every register taken from memory that is not a buffer's let go,
    /// which that store may have changed. `copy`: the value is another's (a
    /// move), made in a register of its own.
    /// </summary>
    private void PackedOut(bool xmm, Operand address, int bytes, int r, bool copy)
    {
        PackedRegisters file = PackedFile(xmm);
        PackedPlace place = PackedPlaceOf(address);
        if (!place.Buffer)
        {
            PackedForgetOutside();
            if (!xmm) Emit(new MInstr(MOp.MmxStore, PackedAt(address), Imm(r)) { Probed = true });
            else Emit(new MInstr(bytes == 16 ? MOp.XmmStore : MOp.XmmStoreLow, PackedAt(address), Imm(r)) { Probed = true });
            // The register is what memory holds now: the next read of it
            // takes the register.
            if (file.Name[r] is null) PackedHold(xmm, r, address, place, bytes, dirty: false);
            return;
        }
        PackedLetGo(place, bytes, replaced: true, xmm, r);
        if (copy && file.Name[r] is not null)
        {
            // A move onto its own bytes changes nothing; onto some of them,
            // the rest written back and the register the new place's.
            if (file.Overlaps(r, place.Root, place.Offset, bytes))
            {
                if (file.Offset[r] == place.Offset && file.Bytes[r] == bytes) return;
                PackedWriteBack(xmm, r);
            }
            else
            {
                int own = PackedFree(xmm, r, -1);
                if (!xmm) Emit(new MInstr(MOp.MmxOp, Imm(0x6F), Imm(r), Imm(own)) { Probed = true });
                else Emit(new MInstr(MOp.XmmOpRR, Imm(0), Imm(0x28), Imm(own), Imm(r)) { Probed = true });
                r = own;
            }
        }
        PackedHold(xmm, r, address, place, bytes, dirty: true);
    }

    /// <summary>A store of the operation's own (MOVNTQ, MOVNTDQ, MOVD) to `address`: the registers holding its bytes let go, or, not a buffer's, every one memory may hold.</summary>
    private void PackedStoredTo(Operand address, int bytes)
    {
        PackedPlace place = PackedPlaceOf(address);
        if (place.Buffer) PackedLetGo(place, bytes, replaced: true);
        else PackedForgetOutside();
    }

    /// <summary>Every register holding a value taken from memory that is not a buffer's -- which a store to such memory may have changed -- let go. They are never newer than it.</summary>
    private void PackedForgetOutside()
    {
        for (int f = 0; f < 2; f++)
        {
            PackedRegisters file = PackedFile(f == 1);
            for (int r = 0; r < 8; r++)
                if (file.Name[r] is not null && !file.Buffer[r])
                {
                    PackedWriteBack(f == 1, r);
                    file.Name[r] = null;
                }
        }
    }

    /// <summary>Every register written back; and, `forget`, let go -- what it held may change, or the registers may.</summary>
    private void PackedFlush(bool forget)
    {
        if (!_packedHeld) return;
        for (int r = 0; r < 8; r++)
        {
            PackedWriteBack(false, r);
            PackedWriteBack(true, r);
        }
        if (!forget) return;
        _mm.Clear();
        _xmm.Clear();
        _packedHeld = false;
    }

    /// <summary>
    /// BEFORE AN IR INSTRUCTION, WHAT THE PACKED REGISTERS MUST GIVE UP
    /// (the registers above): what was taken through a register it sets
    /// anew, which names other memory after it (p = q); nothing more for a
    /// packed operation, nor for the general registers' own arithmetic;
    /// their values written back before a load, which may read them; written
    /// back and let go before anything else -- a store, a call, float code
    /// (the x87's registers are the MMX ones), the block's end -- which may
    /// change what they hold, or them.
    /// </summary>
    private void PackedRegistersBefore(Instr i)
    {
        if (!_packedHeld) return;
        if (i.Dest is { } d)
            for (int f = 0; f < 2; f++)
            {
                PackedRegisters file = PackedFile(f == 1);
                for (int r = 0; r < 8; r++)
                    if (file.Name[r] is RegOperand name && (ReferenceEquals(name.Reg, d) || ReferenceEquals(file.Root[r], d)))
                    {
                        PackedWriteBack(f == 1, r);
                        file.Name[r] = null;
                    }
            }
        if (i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)) return;
        bool floats = i.Dest is { Type: IrType.F32 or IrType.F64 };
        foreach (Operand o in i.Operands) floats |= o.Type is IrType.F32 or IrType.F64;
        if (!floats && i.Op is Opcode.Copy or Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.And or Opcode.Or or Opcode.Xor
                or Opcode.Shl or Opcode.ShrS or Opcode.ShrU or Opcode.Neg or Opcode.Not
                or >= Opcode.Eq and <= Opcode.GeU or Opcode.SExt8 or Opcode.SExt16 or Opcode.ZExt8 or Opcode.ZExt16
                or Opcode.SExt32 or Opcode.ZExt32 or Opcode.Trunc64 or Opcode.LabelAddr)
            return;
        PackedFlush(forget: floats || i.Op != Opcode.Load);
    }

    /// <summary>
    /// THE MMX REGISTERS HOLD A PACKED OPERATION'S LANES (SelectPackedOp)
    /// from it to the next x87 instruction, call or return, which finds the
    /// x87 stack full unless it is emptied first (EMMS): where each IR block
    /// starts so (PackedDirty), and as its selection goes.
    /// </summary>
    private bool _mmxDirty;

    /// <summary>Whether an IR instruction wants the x87 stack empty: a real call, a system call, a return, or anything of a float type (the x87's).</summary>
    private static bool WantsX87(Instr i)
    {
        if (i.Op is Opcode.CallIndirect or Opcode.Syscall or Opcode.Ret or Opcode.Unwind or Opcode.Trap) return true;
        if (i.Op == Opcode.Call) return i.Callee is not { } c || !c.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal) && c != "__exception";
        if (i.Dest is { Type: IrType.F32 or IrType.F64 }) return true;
        foreach (Operand o in i.Operands) if (o.Type is IrType.F32 or IrType.F64) return true;
        return false;
    }

    /// <summary>
    /// Whether an IR instruction in a packed block needs the x87's save
    /// (SettlePackedSaves): float code, a call through a pointer or to the
    /// operating system, a call to a routine that may run float code -- not
    /// a trap or a throw, which leave the block.
    /// </summary>
    private static bool SettleWantsX87(Instr i)
    {
        if (i.Op is Opcode.CallIndirect or Opcode.Syscall) return true;
        // A landing pad's `__exception` is no call: the object thrown, which
        // the landing left in a register. Counted as one, every block with a
        // finally -- the compiler's own around a body -- kept the x87's
        // save, even a variant without the packed registers.
        if (i.Op == Opcode.Call)
            return i.Callee is not { } c || !c.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal) && c != "__exception";
        if (i.Op is Opcode.Ret or Opcode.Unwind or Opcode.Trap) return false;
        if (i.Dest is { Type: IrType.F32 or IrType.F64 }) return true;
        foreach (Operand o in i.Operands) if (o.Type is IrType.F32 or IrType.F64) return true;
        return false;
    }

    /// <summary>The handler landing an instruction sets up -- its label taken (Lowering's handler records) -- which no edge reaches, or null.</summary>
    private static Block? LandingOf(Instr i)
        => i.Op == Opcode.LabelAddr && i.Targets.Count > 0 && i.Targets[0].IsLandingPad ? i.Targets[0] : null;

    /// <summary>Whether an IR instruction leaves the MMX registers empty: the x87 wanted (an EMMS before it), an EMMS of its own, a packed block's save or give-back.</summary>
    private static bool EmptiesMmx(Instr i)
        => WantsX87(i) || i.Op == Opcode.Call && i.Callee is MachineIntrinsics.PackedEnd or MachineIntrinsics.PackedEndFast or MachineIntrinsics.StreamEnd
            or MachineIntrinsics.PackedSaveX87 or MachineIntrinsics.PackedRestoreX87 or MachineIntrinsics.PackedSaveFxsr or MachineIntrinsics.PackedRestoreFxsr;

    /// <summary>Whether an IR instruction is a packed operation that leaves lanes in the MMX registers: MMX's and 3DNow!'s, not the XMM registers', SFENCE or a prefetch.</summary>
    private static bool PackedOpCall(Instr i)
        => i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal) && c != MachineIntrinsics.PackedFence
            && !c.StartsWith(MachineIntrinsics.PackedPrefetchPrefix, StringComparison.Ordinal)
            && !c.StartsWith(MachineIntrinsics.PackedOpPrefix + "x", StringComparison.Ordinal) && !c.StartsWith(MachineIntrinsics.PackedOpPrefix + "f2.", StringComparison.Ordinal);

    /// <summary>
    /// A PACKED BLOCK'S SAVE, SETTLED from what its code uses, before the
    /// function is selected (Lowering's PackedEnter writes it unsettled,
    /// MachineIntrinsics.PackedSave): FXSAVE where an XMM register may be
    /// used, where --cpu has SSE -- SSE's packed operations in it, its own
    /// or a callee's inlined there, or a call to a method's $inside copy,
    /// which may use them in its caller's state; the x87's (FNSTENV, or FNSAVE where the stack is in
    /// use) where its code has float code, a packed operation, a call (the
    /// callee's own float code), a packed block inside it, or the packed
    /// groups may run (a variant with MMX on a --cpu with it); none where it
    /// touches nothing of the FPU's, or there is no FPU. The code a block
    /// holds is what its save reaches before its give-back, along the
    /// function's edges, and a handler's landing from where its label is
    /// taken (LandingOf): a catch inside the block is the block's code.
    /// </summary>
    private static void SettlePackedSaves(Function f)
    {
        List<(Block Block, Instr At, int Kind, int Number, int Features, bool Saves)> marks = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Call || i.Callee is not { } c) continue;
                bool saves = c.StartsWith(MachineIntrinsics.PackedSavePrefix, StringComparison.Ordinal);
                if (!saves && !c.StartsWith(MachineIntrinsics.PackedRestorePrefix, StringComparison.Ordinal)) continue;
                string[] part = c[(saves ? MachineIntrinsics.PackedSavePrefix : MachineIntrinsics.PackedRestorePrefix).Length..].Split('.');
                marks.Add((b, i, int.Parse(part[0]), int.Parse(part[1]), int.Parse(part[2]), saves));
            }
        if (marks.Count == 0) return;
        X86Cpu profile = Target.Current.X86Profile;
        bool fpu = profile.Fpu != "none";
        // BY THE BLOCK'S WHOLE NAME -- kind, number and features: blocks of
        // two files' methods inlined into one may share a number, and a
        // block's copies (a finally's, a loop's) settle as one, the largest
        // save any copy needs.
        Dictionary<string, string?> settled = new(StringComparer.Ordinal);
        foreach (var save in marks.Where(m => m.Saves))
        {
            bool xmm = false, x87 = false, inside = false;
            HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
            Stack<(Block, int)> work = new();
            work.Push((save.Block, save.Block.Instrs.IndexOf(save.At) + 1));
            while (work.Count > 0)
            {
                (Block b, int from) = work.Pop();
                bool closed = false;
                for (int k = from; k < b.Instrs.Count && !closed; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.Op == Opcode.Call && i.Callee is { } c)
                    {
                        if (c == MachineIntrinsics.PackedRestore(save.Kind, save.Number, save.Features)) { closed = true; continue; }
                        if (c.EndsWith("$inside", StringComparison.Ordinal)) inside = true;
                    }
                    // A native that uses the FPU (Sys.CallNativeFloat) may use SSE.
                    if (i.Op == Opcode.CallIndirect && i.Callee == NativeCall.IndirectUsingFpu) xmm = true;
                    if (i.Op == Opcode.Call && i.Callee is { } named)
                    {
                        if (named.StartsWith(MachineIntrinsics.PackedPrefetchPrefix, StringComparison.Ordinal) || named == MachineIntrinsics.PackedFence) { }
                        else if (named.StartsWith(MachineIntrinsics.PackedOpPrefix + "x", StringComparison.Ordinal)
                            || named.StartsWith(MachineIntrinsics.PackedOpPrefix + "f2.", StringComparison.Ordinal)) xmm = true;
                        else if (named.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
                            || named.StartsWith(MachineIntrinsics.PackedSavePrefix, StringComparison.Ordinal)) x87 = true;
                    }
                    if (SettleWantsX87(i)) x87 = true;
                    if (LandingOf(i) is { } pad && seen.Add(pad)) work.Push((pad, 0));
                }
                if (closed) continue;
                foreach (Block next in b.Successors)
                    if (seen.Add(next)) work.Push((next, 0));
            }
            if ((save.Features & PackedOps.Mmx) != 0 && profile.Mmx) x87 = true;
            // FXSAVE also where the XMM registers are used by code that is
            // not the variant's own -- a callee inlined here, or a method's
            // $inside copy called, which saves nothing and may run its own
            // SSE variant -- wherever --cpu has SSE: only there does
            // freestanding code have an SSE variant at all.
            bool fx = profile.Has(X86Feature.Sse) && (xmm || inside);
            string key = save.Kind + "." + save.Number + "." + save.Features;
            string? kind = fx ? "fx" : x87 && fpu ? "x87" : null;
            string? had = settled.GetValueOrDefault(key);
            settled[key] = had == "fx" || kind == "fx" ? "fx" : had ?? kind;
        }
        List<Operand> dropped = new();
        // An area settled to the x87's save is its 112 bytes, not FXSAVE's
        // room Lowering gave a block that might have needed it.
        Dictionary<VReg, FrameSlot> areas = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Copy && i.Dest is { } d && i.Operands is [SlotOperand area]) areas[d] = area.Slot;
        foreach (var m in marks)
        {
            string? kind = settled.GetValueOrDefault(m.Kind + "." + m.Number + "." + m.Features);
            int at = m.Block.Instrs.IndexOf(m.At);
            if (kind is null) { m.Block.Instrs.RemoveAt(at); dropped.Add(m.At.Operands[0]); continue; }
            if (kind == "x87" && m.At.Operands[0] is RegOperand { Reg: var named } && areas.TryGetValue(named, out FrameSlot? x87Area) && x87Area.Bytes > 112)
                x87Area.Bytes = 112;
            string callee = (kind, m.Saves) switch
            {
                ("fx", true) => MachineIntrinsics.PackedSaveFxsr,
                ("fx", false) => MachineIntrinsics.PackedRestoreFxsr,
                (_, true) => MachineIntrinsics.PackedSaveX87,
                _ => MachineIntrinsics.PackedRestoreX87,
            };
            Instr made = new() { Op = Opcode.Call, Callee = callee, Line = m.At.Line };
            made.Operands.AddRange(m.At.Operands);
            m.Block.Instrs[at] = made;
        }
        // A DROPPED SAVE'S AREA goes with it: the copy of its address that
        // nothing reads now, and the slot nothing names, so the frame keeps
        // none of its bytes.
        HashSet<VReg> unused = new(dropped.OfType<VReg>(), ReferenceEqualityComparer.Instance);
        if (unused.Count == 0) return;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is VReg v) unused.Remove(v);
        HashSet<FrameSlot> freed = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            b.Instrs.RemoveAll(i =>
            {
                if (i.Op != Opcode.Copy || i.Dest is not { } d || !unused.Contains(d) || i.Operands.Count != 1 || i.Operands[0] is not SlotOperand slot) return false;
                freed.Add(slot.Slot);
                return true;
            });
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is SlotOperand s) freed.Remove(s.Slot);
        f.Slots.RemoveAll(freed.Contains);
    }

    /// <summary>
    /// WHERE THE MMX REGISTERS MAY HOLD A PACKED OPERATION'S LANES as each IR
    /// block starts, on any path there (null where the function has none):
    /// what SelectPackedOp leaves, until an instruction empties them.
    /// </summary>
    private static Dictionary<Block, bool>? PackedDirty(Function f)
    {
        if (!f.Blocks.Any(b => b.Instrs.Any(PackedOpCall))) return null;
        Dictionary<Block, bool> at = new(ReferenceEqualityComparer.Instance) { [f.Entry] = false };
        Stack<Block> work = new();
        work.Push(f.Entry);
        while (work.Count > 0)
        {
            Block b = work.Pop();
            bool dirty = at[b];
            void Join(Block next, bool now)
            {
                bool had = at.TryGetValue(next, out bool known);
                if (!had || (now && !known)) { at[next] = (had && known) || now; work.Push(next); }
            }
            foreach (Instr i in b.Instrs)
            {
                dirty = PackedOpCall(i) || (!EmptiesMmx(i) && dirty);
                if (LandingOf(i) is { } pad) Join(pad, dirty);
            }
            foreach (Block next in b.Successors) Join(next, dirty);
        }
        return at;
    }

    /// <summary>
    /// WHAT THE LINK NEEDS TO SAY WHERE A FUNCTION'S FLOAT CODE RUNS
    /// (Lto.X87Notes, X86Backend.X87InBlocksOnly): the line of its first
    /// float code outside its own packed blocks, where `floats` asks (-1
    /// for none), and its direct calls, each with whether it is made inside
    /// a block; every symbol it takes the address of goes into `taken`. A
    /// function whose every call is made inside a block runs under its
    /// callers' block -- whose save covers a call -- and needs none of its
    /// own: the link alone sees every caller. A parameter's landing-pad home
    /// stored on entry (Opt.LandingPadHomes) is words copied from the
    /// arguments, no x87.
    /// </summary>
    internal static Corsac.Lang.Lto.X87Notes.Fact X87Fact(Function f, bool floats, HashSet<string> taken)
    {
        Dictionary<Block, int>? regions = PackedRegions(f);
        int floatLine = -1;
        List<Corsac.Lang.Lto.X87Notes.Call> calls = new();
        // A CALL THROUGH A FIELD'S LOAD (Sys.CallNative or CallNativeFloat of a native whose
        // address the kernel's start left in a static): a call to the
        // routine in it (X87Notes.InField). The load's value used any other
        // way takes that routine anywhere: its address is taken -- but its
        // test against 0 (`if (Natives.X != 0)`) and a branch on it, which
        // take nothing. A register loaded from two fields takes both.
        Dictionary<VReg, string> loaded = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Load && i.Dest is { } d && i.Offset == 0 && i.Operands is [SymOperand { Name: var field, Offset: 0 }])
                {
                    if (loaded.TryGetValue(d, out string? other) && other != field)
                    {
                        taken.Add(Corsac.Lang.Lto.X87Notes.InField(other));
                        taken.Add(Corsac.Lang.Lto.X87Notes.InField(field));
                    }
                    loaded[d] = field;
                }
        foreach (Block b in f.Blocks)
        {
            int state = regions?.GetValueOrDefault(b) ?? 0;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Call)
                    state = i.Callee switch
                    {
                        MachineIntrinsics.PackedSaveX87 => state << 2 | 1,
                        MachineIntrinsics.PackedSaveFxsr => state << 2 | 2,
                        MachineIntrinsics.PackedRestoreX87 or MachineIntrinsics.PackedRestoreFxsr => state >> 2,
                        _ => state,
                    };
                // A static named otherwise than by such a load or a store into
                // it -- its address passed on -- takes the routine in it too.
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is SymOperand sym)
                    {
                        taken.Add(sym.Name);
                        bool read = i.Op == Opcode.Load && i.Dest is not null && loaded.ContainsKey(i.Dest);
                        bool storedInto = k == 0 && i.Op is Opcode.Store or Opcode.MemCopy or Opcode.MemSet;
                        if (!read && !storedInto) taken.Add(Corsac.Lang.Lto.X87Notes.InField(sym.Name));
                    }
                if (i.Op == Opcode.Call && i.Callee is { } callee && !callee.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal) && callee != "__exception")
                    calls.Add(new(callee, i.Line, state != 0));
                if (loaded.Count > 0)
                    for (int k = 0; k < i.Operands.Count; k++)
                        if (i.Operands[k] is RegOperand { Reg: var used } && loaded.TryGetValue(used, out string? holder))
                        {
                            if (k == 0 && i.Op == Opcode.CallIndirect) calls.Add(new(Corsac.Lang.Lto.X87Notes.InField(holder), i.Line, state != 0));
                            else if (i.Op == Opcode.Branch || i.Op is >= Opcode.Eq and <= Opcode.GeU && i.Operands.Count == 2
                                     && i.Operands[1 - k] is ImmOperand { Value: 0 }) { }
                            else taken.Add(Corsac.Lang.Lto.X87Notes.InField(holder));
                        }
                if (!floats || floatLine >= 0 || state != 0 || i.Op == Opcode.Ret) continue;
                if (i.Op == Opcode.Store && i.Operands is [SlotOperand { Slot.Name: { } home }, RegOperand { Reg: var stored }]
                    && home.StartsWith("home", StringComparison.Ordinal) && f.Params.Contains(stored))
                    continue;
                if (i.Dest is { Type: IrType.F32 or IrType.F64 } || i.Operands.Any(o => o.Type is IrType.F32 or IrType.F64)) floatLine = i.Line;
            }
        }
        return new(f.Name, f.Display ?? f.Name, Path.GetFileName(f.SourceFile ?? ""), floatLine, f.Dispatched, calls.ToArray());
    }

    /// <summary>
    /// WHERE A FUNCTION'S PACKED BLOCKS ARE, by the blocks each IR block
    /// starts inside (0 outside every one): after a save (PackedSaveX87,
    /// PackedSaveFxsr) and before its give-back, on every path there -- a
    /// block some path reaches outside is outside, and a handler's landing,
    /// which no edge reaches, is too, so only its give-back runs there. The
    /// saves and give-backs are calls, which no packed group spans, so the
    /// blocks the optimiser splits or joins keep their places.
    /// </summary>
    private static Dictionary<Block, int>? PackedRegions(Function f)
    {
        // The blocks open, innermost in the low two bits: a save pushes its
        // kind, a give-back pops back to the block around it.
        static int After1(Instr i, int state)
            => i.Op != Opcode.Call ? state : i.Callee switch
            {
                MachineIntrinsics.PackedSaveX87 => state << 2 | 1,
                MachineIntrinsics.PackedSaveFxsr => state << 2 | 2,
                MachineIntrinsics.PackedRestoreX87 or MachineIntrinsics.PackedRestoreFxsr => state >> 2,
                _ => state,
            };
        if (!f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.Call && i.Callee is MachineIntrinsics.PackedSaveX87 or MachineIntrinsics.PackedSaveFxsr)))
            return null;
        Dictionary<Block, int> at = new(ReferenceEqualityComparer.Instance) { [f.Entry] = 0 };
        Stack<Block> work = new();
        work.Push(f.Entry);
        while (work.Count > 0)
        {
            Block b = work.Pop();
            void Join(Block next, int leaving)
            {
                int was = at.TryGetValue(next, out int known) ? known : -1;
                int now = was == -1 ? leaving : was == leaving ? was : 0;
                if (now != was) { at[next] = now; work.Push(next); }
            }
            // A HANDLER'S LANDING is reached from where it was set up (its
            // label taken, LandingOf), in the blocks open there: a catch or a
            // finally's throw copy inside a block is inside it.
            int state = at[b];
            foreach (Instr i in b.Instrs)
            {
                state = After1(i, state);
                if (LandingOf(i) is { } pad) Join(pad, state);
            }
            foreach (Block next in b.Successors) Join(next, state);
        }
        return at;
    }

    public static MFunction Run(Function f, List<string> errors, bool automaticPacked = true, bool positionIndependent = false, bool windowsTls = false,
        string? streamCopy = null, string? streamFill = null, X86Backend.X87Facts? x87 = null)
    {
        // NOT IN THE STREAM ROUTINES THEMSELVES, whose own copy and fill are
        // the long way they fall back on: a block overlapping itself copied
        // there called the routine again, and again, until the stack ran out.
        bool stream = automaticPacked && f.Name != streamCopy && f.Name != streamFill;
        if (!automaticPacked) SettlePackedSaves(f);
        // The program's own code: not the system library's -- written in its
        // trees (Function.SystemCode), however a kernel names their files --
        // nor what the compiler makes itself (__box_tostring_System_Double
        // and its kin); both run where their callers do. A unit compiled
        // with --lib is the program's all the same (FromLibrary says so of
        // every function in it): a kernel's every unit but its entry is one,
        // and the check passed over all of them.
        if (!automaticPacked && x87 is not null) x87.Add(f, floats: !f.SystemCode && !f.Name.StartsWith("__", StringComparison.Ordinal));
        Selector s = new(f, errors, automaticPacked, automaticPacked)
        {
            _positionIndependent = positionIndependent, _windowsTls = windowsTls,
            _streamCopy = stream ? streamCopy : null, _streamFill = stream ? streamFill : null,
        };
        s.Select();
        return s._m;
    }

    private void Error(string what) => _errors.Add($"{_f.Name}: {what}");

    // ---- emission ----------------------------------------------------------

    /// <summary>The source line of the IR instruction being selected, stamped on everything it becomes.</summary>
    private int _line;

    private MInstr Emit(MInstr i)
    {
        if (_packedNow != 0 && i.OwnsState == 0) i = Owning(i);
        i.Line = _line;
        _cur.Instrs.Add(i);
        return i;
    }

    private MInstr Emit(MOp op, params MOperand[] ops) => Emit(new MInstr(op, ops));

    /// <summary>The same instruction, marked as selected inside the packed block (MInstr.OwnsState).</summary>
    private MInstr Owning(MInstr i)
    {
        MInstr n = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native, Probed = i.Probed, OwnsState = (byte)_packedNow };
        n.Operands.AddRange(i.Operands);
        return n;
    }

    // The common arities written out, so no `params` array is made per instruction.
    private MInstr Emit(MOp op) => Emit(new MInstr(op));
    private MInstr Emit(MOp op, MOperand a) => Emit(new MInstr(op, a));
    private MInstr Emit(MOp op, MOperand a, MOperand b) => Emit(new MInstr(op, a, b));
    private MInstr Emit(MOp op, MOperand a, MOperand b, MOperand c) => Emit(new MInstr(op, a, b, c));

    private MInstr EmitW(MOp op, int width, params MOperand[] ops) => Emit(new MInstr(op, ops) { Width = width });
    private MInstr EmitW(MOp op, int width, MOperand a) => Emit(new MInstr(op, a) { Width = width });
    private MInstr EmitW(MOp op, int width, MOperand a, MOperand b) => Emit(new MInstr(op, a, b) { Width = width });

    private void Jcc(Cond c, MBlock target) => Emit(new MInstr(MOp.Jcc, new MLabel(target)) { Cond = c });

    private void Jmp(MBlock target) => Emit(MOp.Jmp, new MLabel(target));

    /// <summary>Start a fresh block right after the current one and continue there.</summary>
    private MBlock Split()
    {
        MBlock b = _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);
        _cur = b;
        return b;
    }

    /// <summary>A block placed after the current one, for code the current block jumps around.</summary>
    private MBlock Aside() => _m.NewBlock($"{_cur.Name}.{_splits++}", _cur);

    private MReg Temp() => _m.NewReg();

    private static MImm Imm(long v) => new(v);

    private void Mov(MOperand dst, MOperand src) => Emit(MOp.Mov, dst, src);

    // ---- symbol references ------------------------------------------------
    //
    // Every reference to a named thing passes through here, so a
    // position-independent mode can later swap the absolute forms for
    // GOT-relative ones without touching the selection of any opcode.

    /// <summary>The address of a symbol as an immediate: an Abs32 relocation.</summary>
    private static MImm SymbolAddress(string name, long addend) => MImm.Sym(name, addend);

    /// <summary>A memory operand at a symbol plus a displacement.</summary>
    private static MMem SymbolMem(string name, long disp) => MMem.Abs(name, checked((int)disp));

    private void CallSymbol(string name) => Emit(new MInstr(MOp.Call, SymbolAddress(name, 0)) { CallReloc = RelocKind.Rel32 });

    // ---- operands ----------------------------------------------------------

    private MReg Lo(VReg v)
    {
        MReg? r = _lo[v.Id];
        if (r is null)
        {
            r = _m.NewReg();
            _lo[v.Id] = r;
            // Both halves of a 64-bit integer are integers: telling the
            // stack maps so is the one bit of precision available while a
            // reference and an int are both an I32 in the IR.
            if (v.Type == IrType.I64)
            {
                _m.WideHalves.Add(r.Id);
            }
        }
        return r;
    }

    private MReg Hi(VReg v)
    {
        MReg? r = _hi[v.Id];
        if (r is null)
        {
            r = _m.NewReg();
            _hi[v.Id] = r;
            _m.WideHalves.Add(r.Id);
        }
        return r;
    }

    /// <summary>An I32 operand in a register, loading a constant or address if it is one.</summary>
    private MReg R(Operand o)
    {
        switch (o)
        {
            case RegOperand r:
                return Lo(r.Reg);
            case SlotOperand s:
            {
                MReg t = Temp();
                Emit(MOp.Lea, t, MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                return t;
            }
            default:
            {
                MReg t = Temp();
                Mov(t, RM(o));
                return t;
            }
        }
    }

    /// <summary>An I32 operand as a register or an immediate: the source side of an ALU instruction.</summary>
    private MOperand RM(Operand o) => o switch
    {
        RegOperand r => Lo(r.Reg),
        ImmOperand i => Imm((int)i.Value),
        SymOperand s => SymbolAddress(s.Name, s.Offset),
        SlotOperand => R(o),
        _ => throw new InvalidOperationException(o.GetType().Name),
    };

    /// <summary>The two halves of an I64 operand as ALU sources.</summary>
    private (MOperand Lo, MOperand Hi) PairRM(Operand o) => o switch
    {
        RegOperand r => (Lo(r.Reg), ZeroHigh(o) ? Imm(0) : Hi(r.Reg)),
        ImmOperand i => (Imm((int)i.Value), Imm((int)(i.Value >> 32))),
        _ => throw new InvalidOperationException($"{o} is not an I64"),
    };

    private (MReg Lo, MReg Hi) Pair(Operand o)
    {
        if (o is RegOperand r)
        {
            return (Lo(r.Reg), Hi(r.Reg));
        }
        (MOperand lo, MOperand hi) = PairRM(o);
        MReg tl = Temp();
        MReg th = Temp();
        Mov(tl, lo);
        Mov(th, hi);
        return (tl, th);
    }

    private static int FWidth(IrType t) => t == IrType.F32 ? 4 : 8;

    /// <summary>The home slot of a floating-point virtual register.</summary>
    private MMem FHome(VReg v) => MMem.Frame(_m.Frame.FloatHome(v));

    private MMem FHome(Operand o) => o is RegOperand r ? FHome(r.Reg) : throw new InvalidOperationException($"{o} is not a float register");

    private void Fld(Operand o) => EmitW(MOp.Fld, FWidth(o.Type), FHome(o));

    private void Fstp(VReg d) => EmitW(MOp.Fstp, FWidth(d.Type), FHome(d));

    private static MMem Displaced(MMem source, int delta) => new(source.Base, source.Disp + delta)
    { Index = source.Index, Scale = source.Scale, Symbol = source.Symbol, Reloc = source.Reloc, Label = source.Label, IsSpill = source.IsSpill, Device = source.Device };

    // Copy bits, not numbers: this also preserves signaling-NaN payloads and
    // avoids touching x87 state for ordinary same-width floating-point moves.
    private void CopyFloatBits(MMem destination, MMem source, int bytes)
    {
        MReg low = Temp(); Mov(low, source);
        if (bytes == 4) { Mov(destination, low); return; }
        MReg high = Temp(); Mov(high, Displaced(source, 4));
        Mov(destination, low); Mov(Displaced(destination, 4), high);
    }

    /// <summary>The memory an address operand plus displacement names.</summary>
    private MMem Address(Operand addr, long offset)
    {
        int disp = checked((int)offset);
        switch (addr)
        {
            case RegOperand r when r.Type == IrType.I32:
                return new MMem(Lo(r.Reg), disp);
            case SymOperand s:
                return SymbolMem(s.Name, s.Offset + disp);
            case SlotOperand s:
                return MMem.Frame(_m.Frame.SlotOffset(s.Slot) + disp);
            case ImmOperand i:
            {
                // AN ABSOLUTE ADDRESS IS 32 BITS OF DISPLACEMENT, and on i386
                // the top half of the space is as addressable as the bottom:
                // the kernel is at 0xC0000000. The word is kept as its bits
                // (0xBFFFFFF0 is disp32 0xBFFFFFF0, which reads as a negative
                // int); only a value that is not 32 bits at all is refused.
                long at = i.Value + disp;
                if (at < int.MinValue || at > uint.MaxValue)
                {
                    Error($"the address 0x{at:X} does not fit in 32 bits");
                    return new MMem(null, 0);
                }
                return new MMem(null, unchecked((int)at));
            }
            default:
                Error($"cannot address through {addr} of type {addr.Type}");
                return new MMem(null, 0);
        }
    }

    private static bool Aliases(VReg? dest, Operand o) => dest is not null && o is RegOperand r && r.Reg == dest;

    // ---- the walk -----------------------------------------------------------

    private void Select()
    {
        foreach (Block b in _f.Blocks)
        {
            _heads[b] = _m.NewBlock(b.Label, null, b);
        }
        foreach (Block b in _f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                foreach (Operand o in i.Operands)
                {
                    if (o is RegOperand r)
                    {
                        _useCount.Count(r.Reg.Id);
                    }
                }
            }
        }

        _cur = _heads[_f.Entry];
        Emit(MOp.Prologue);
        LoadParams();

        Dictionary<Block, int>? packedIn = _automaticPacked ? null : PackedRegions(_f);
        Dictionary<Block, bool>? dirtyIn = PackedDirty(_f);
        foreach (Block b in _f.Blocks)
        {
            _cur = _heads[b];
            _sourceBlock = b;
            _constants.Clear();
            _packedState = packedIn?.GetValueOrDefault(b) ?? 0;
            _mmxDirty = dirtyIn?.GetValueOrDefault(b) ?? false;
            // A block begins with no packed register held: its last ended so.
            _mm.Clear();
            _xmm.Clear();
            _packedHeld = false;
            for (int n = 0; n < b.Instrs.Count; n++)
            {
                Instr i = b.Instrs[n];
                _line = i.Line;
                NoteConstant(i);
                PackedRegistersBefore(i);
                if (_mmxDirty && WantsX87(i))
                {
                    Emit(new MInstr(MOp.Emms) { Probed = true });
                    _mmxDirty = false;
                }
                else if (_mmxDirty && EmptiesMmx(i)) _mmxDirty = false;
                if (TryPackedArithmetic(b.Instrs, n, out int consumed) || TryPackedRounding(b.Instrs, n, out consumed)
                    || TryPackedShifts(b.Instrs, n, out consumed)
                    || TryPackedConversions(b.Instrs, n, out consumed)
                    || TryPackedFloatArithmetic(b.Instrs, n, out consumed)
                    || TryPackedDotProducts(b.Instrs, n, out consumed)
                    || TryPackedComparisons(b.Instrs, n, out consumed)
                    || TryRotate(b.Instrs, n, out consumed))
                {
                    // Their own MMX code uses mm0 and mm1.
                    PackedFlush(forget: true);
                    n += consumed - 1;
                    continue;
                }
                if (IsCompare(i.Op) && i.Dest is not null && n + 1 < b.Instrs.Count
                    && FusesInto(i, b.Instrs[n + 1]))
                {
                    PackedRegistersBefore(b.Instrs[n + 1]);
                    SelectFusedBranch(i, b.Instrs[n + 1]);
                    n++;
                    continue;
                }
                SelectInstr(i);
            }
            if (b.Terminator is null)
            {
                Error($"block {b.Label} has no terminator");
            }
        }
    }

    /// <summary>
    /// Incoming arguments sit above the return address; the integer ones are
    /// loaded into their virtual registers here and the float ones simply
    /// have their home declared to be the argument slot.
    /// </summary>
    private void LoadParams()
    {
        int off = 8;
        foreach (VReg p in _f.Params)
        {
            switch (p.Type)
            {
                case IrType.I32:
                    Mov(Lo(p), MMem.Frame(off));
                    off += 4;
                    break;
                case IrType.I64:
                    Mov(Lo(p), MMem.Frame(off));
                    Mov(Hi(p), MMem.Frame(off + 4));
                    off += 8;
                    break;
                case IrType.F32:
                    _m.Frame.PlaceFloatParam(p, off);
                    off += 4;
                    break;
                case IrType.F64:
                    _m.Frame.PlaceFloatParam(p, off);
                    off += 8;
                    break;
                default:
                    Error($"parameter {p} has type {p.Type}");
                    break;
            }
        }
    }

    /// <summary>
    /// Registers this block has so far set to an integer constant and not
    /// redefined, so a branch on one of them can be resolved here rather
    /// than testing a register everyone can see is zero.
    /// </summary>
    private readonly Dictionary<int, long> _constants = new();

    private void NoteConstant(Instr i)
    {
        if (i.Dest is null)
        {
            return;
        }
        if (i.Op == Opcode.Copy && i.Operands[0] is ImmOperand imm && i.Dest.Type == IrType.I32)
        {
            _constants[i.Dest.Id] = imm.Value;
        }
        else
        {
            _constants.Remove(i.Dest.Id);
        }
    }

    private static bool IsCompare(Opcode op) => op is >= Opcode.Eq and <= Opcode.GeU or >= Opcode.FEq and <= Opcode.FGe;

    /// <summary>
    /// A compare whose only consumer is the branch right after it need not
    /// produce a 0/1: the flags go straight into the jcc.
    /// </summary>
    private bool FusesInto(Instr cmp, Instr next)
        => next.Op == Opcode.Branch && next.Operands[0] is RegOperand r && r.Reg == cmp.Dest
           && _useCount.GetValueOrDefault(r.Reg.Id) == 1;

    private bool TryRotate(List<Instr> instructions, int at, out int consumed)
    {
        consumed = 0;
        bool SingleUse(Instr i) => i.Dest is { } d && _useCount.GetValueOrDefault(d.Id) == 1
            && _definitions.TryGetValue(d, out Instr? definition) && ReferenceEquals(definition, i);
        bool Input(Instr i, VReg? r) => i.Operands.Count == 2 && i.Operands[0] is RegOperand operand && operand.Reg == r;
        bool Mask(Instr i) => i.Op == Opcode.And && i.Dest?.Type == IrType.I64
            && i.Operands.Count == 2 && i.Operands[1] is ImmOperand { Value: 4294967295 };
        bool Combine(Instr i, VReg? a, VReg? b) => i.Op == Opcode.Or && i.Operands.Count == 2
            && i.Operands[0] is RegOperand x && i.Operands[1] is RegOperand y
            && ((x.Reg == a && y.Reg == b) || (x.Reg == b && y.Reg == a));
        bool IsRight(Instr i, bool wide) => i.Op == Opcode.ShrU || (wide && i.Op == Opcode.ShrS);
        bool ShiftPair(Instr a, Instr b, bool wide) =>
            (a.Op == Opcode.Shl && IsRight(b, wide)) || (IsRight(a, wide) && b.Op == Opcode.Shl);

        VReg? source = null;
        Instr right, left, result;
        if (at + 5 < instructions.Count && Mask(instructions[at]) && Mask(instructions[at + 2])
            && instructions[at].Operands[0] is RegOperand first
            && instructions[at + 2].Operands[0] is RegOperand second && first.Reg == second.Reg
            && first.Type == IrType.I64)
        {
            right = instructions[at + 1]; left = instructions[at + 3]; result = instructions[at + 5];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            if (right.Op is not (Opcode.ShrS or Opcode.ShrU) || left.Op != Opcode.Shl
                || !Input(instructions[at + 1], instructions[at].Dest)
                || !Input(instructions[at + 3], instructions[at + 2].Dest)
                || !Combine(instructions[at + 4], right.Dest, left.Dest)
                || !Mask(result) || !Input(result, instructions[at + 4].Dest)) return false;
            source = first.Reg;
            consumed = 6;
        }
        else if (at + 3 < instructions.Count && ShiftPair(instructions[at], instructions[at + 1], true)
            && instructions[at].Dest?.Type == IrType.I64
            && instructions[at].Operands[0] is RegOperand shared && ZeroHigh(shared)
            && Input(instructions[at + 1], shared.Reg)
            && Combine(instructions[at + 2], instructions[at].Dest, instructions[at + 1].Dest)
            && Mask(instructions[at + 3]) && Input(instructions[at + 3], instructions[at + 2].Dest))
        {
            right = instructions[at]; left = instructions[at + 1]; result = instructions[at + 3];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            source = shared.Reg;
            consumed = 4;
        }
        else if (at + 2 < instructions.Count && ShiftPair(instructions[at], instructions[at + 1], false)
            && instructions[at].Dest?.Type == IrType.I32
            && instructions[at].Operands[0] is RegOperand first32
            && Input(instructions[at + 1], first32.Reg)
            && Combine(instructions[at + 2], instructions[at].Dest, instructions[at + 1].Dest))
        {
            right = instructions[at]; left = instructions[at + 1]; result = instructions[at + 2];
            if (right.Op == Opcode.Shl) (right, left) = (left, right);
            source = first32.Reg;
            consumed = 3;
        }
        else return false;
        if (right.Operands[1] is not ImmOperand rightCount || left.Operands[1] is not ImmOperand leftCount
            || rightCount.Value < 1 || rightCount.Value > 31 || leftCount.Value != 32 - rightCount.Value)
            return false;
        for (int n = at; n < at + consumed - 1; n++)
            if (!SingleUse(instructions[n]) || instructions[n].Dest == source
                || instructions[n].Dest?.Type != result.Dest?.Type) return false;
        if (source.Type != result.Dest?.Type) return false;
        NoteConstant(result);
        _line = result.Line;
        Mov(Lo(result.Dest!), Lo(source));
        Emit(MOp.Ror, Lo(result.Dest!), Imm(rightCount.Value));
        if (result.Dest!.Type == IrType.I64) Mov(Hi(result.Dest), Imm(0));
        return true;
    }

    private void SelectInstr(Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Copy:
                SelectCopy(i);
                break;
            case Opcode.Add:
            case Opcode.Sub:
            case Opcode.And:
            case Opcode.Or:
            case Opcode.Xor:
                SelectAlu(i);
                break;
            case Opcode.Mul:
                SelectMul(i);
                break;
            case Opcode.DivS:
            case Opcode.DivU:
            case Opcode.RemS:
            case Opcode.RemU:
                SelectDiv(i);
                break;
            case Opcode.Shl:
            case Opcode.ShrS:
            case Opcode.ShrU:
                SelectShift(i);
                break;
            case Opcode.Neg:
            case Opcode.Not:
                SelectUnary(i);
                break;
            case Opcode.ByteSwap:
            {
                VReg d = i.Dest!;
                if (d.Type == IrType.I32)
                {
                    Mov(Lo(d), RM(i.Operands[0])); ByteSwapWord(Lo(d));
                }
                else
                {
                    // Snapshot both halves before assigning either one:
                    // reversing an I64 also exchanges its low/high words.
                    var source = PairRM(i.Operands[0]);
                    MReg low = Temp(), high = Temp();
                    Mov(low, source.Hi); Mov(high, source.Lo);
                    ByteSwapWord(low); ByteSwapWord(high);
                    Mov(Lo(d), low); Mov(Hi(d), high);
                }
                break;
            }
            case Opcode.Eq:
            case Opcode.Ne:
            case Opcode.LtS:
            case Opcode.LeS:
            case Opcode.GtS:
            case Opcode.GeS:
            case Opcode.LtU:
            case Opcode.LeU:
            case Opcode.GtU:
            case Opcode.GeU:
                SelectCompare(i);
                break;
            case Opcode.FAdd:
            case Opcode.FSub:
            case Opcode.FMul:
            case Opcode.FDiv:
                SelectFloatAlu(i);
                break;
            case Opcode.FNeg:
                Fld(i.Operands[0]);
                Emit(MOp.Fchs);
                Fstp(i.Dest!);
                break;
            case Opcode.FSqrt:
                Fld(i.Operands[0]);
                Emit(MOp.Fsqrt);
                Fstp(i.Dest!);
                break;
            case Opcode.FEq:
            case Opcode.FNe:
            case Opcode.FLt:
            case Opcode.FLe:
            case Opcode.FGt:
            case Opcode.FGe:
                SelectFloatCompare(i);
                break;
            case Opcode.SExt8:
            case Opcode.SExt16:
            case Opcode.ZExt8:
            case Opcode.ZExt16:
                SelectNarrow(i);
                break;
            case Opcode.Trunc64:
                Mov(Lo(i.Dest!), PairRM(i.Operands[0]).Lo);
                break;
            case Opcode.SExt32:
            {
                Mov(Lo(i.Dest!), RM(i.Operands[0]));
                Mov(Hi(i.Dest!), Lo(i.Dest!));
                Emit(MOp.Sar, Hi(i.Dest!), Imm(31));
                break;
            }
            case Opcode.ZExt32:
                // A move of zero rather than xor: the allocator then knows the
                // half is a constant it can remake instead of spilling, and
                // the peephole turns what survives into xor anyway.
                Mov(Lo(i.Dest!), RM(i.Operands[0]));
                Mov(Hi(i.Dest!), Imm(0));
                break;
            case Opcode.FConv:
                Fld(i.Operands[0]);
                Fstp(i.Dest!);
                break;
            case Opcode.IToF:
            case Opcode.UToF:
                SelectIntToFloat(i);
                break;
            case Opcode.FToI:
            case Opcode.FToU:
                SelectFloatToInt(i);
                break;
            case Opcode.Bits:
                SelectBits(i);
                break;
            case Opcode.Load:
                SelectLoad(i);
                break;
            case Opcode.ArrayLength:
                Mov(Lo(i.Dest!), Address(i.Operands[0], Target.Current.ArrayCountOffset));
                break;
            case Opcode.InitArrayLength:
                Mov(Address(i.Operands[0], Target.Current.ArrayCountOffset), RM(i.Operands[1]));
                break;
            case Opcode.Store:
                SelectStore(i);
                break;
            case Opcode.MemCopy:
                SelectMemCopy(i);
                break;
            case Opcode.MemSet:
                if (SelectFrameSet(i)) break;
                SelectMemSet(i);
                break;
            case Opcode.AtomicSwap:
            case Opcode.AtomicAdd:
            case Opcode.AtomicAnd:
            case Opcode.AtomicOr:
            case Opcode.AtomicXor:
            case Opcode.AtomicCas:
                SelectAtomic(i);
                break;
            case Opcode.Fence:
                Emit(MOp.LockOrEsp);
                break;
            case Opcode.CompilerFence:
                // TSO: loads are not passed by later loads or stores, nor
                // stores by earlier ones. Nothing to emit; the optimiser has
                // already kept everything on its side.
                break;
            case Opcode.Call:
            case Opcode.CallIndirect:
                SelectCall(i);
                break;
            case Opcode.Ret:
                SelectRet(i);
                break;
            case Opcode.Jump:
                Jmp(_heads[i.Targets[0]]);
                break;
            case Opcode.Branch:
                SelectBranch(i);
                break;
            case Opcode.Switch:
                SelectSwitch(i);
                break;
            case Opcode.Unreachable:
                // No ud2 on a 486; a breakpoint trap is the loudest thing it has.
                Emit(MOp.Int3);
                break;
            case Opcode.Unwind:
                SelectUnwind(i);
                break;
            case Opcode.LabelAddr:
                Mov(Lo(i.Dest!), MImm.Of(_heads[i.Targets[0]]));
                break;
            case Opcode.StackPointer:
                Mov(Lo(i.Dest!), Esp);
                break;
            case Opcode.FramePointer:
                Mov(Lo(i.Dest!), Ebp);
                break;
            case Opcode.Syscall:
                SelectSyscall(i);
                break;
            case Opcode.Trap:
                // A breakpoint the program may run on from: marked, so that
                // nothing before it is taken for dead (Peephole.BeforeTrap).
                Emit(MOp.Int3, new MImm(1));
                break;
            case Opcode.Pause:
                Emit(MOp.Pause);
                break;
            default:
                Error($"unsupported opcode {i.Op}");
                break;
        }
    }

    // ---- moves --------------------------------------------------------------

    private void SelectCopy(Instr i)
    {
        VReg d = i.Dest!;
        Operand src = i.Operands[0];
        switch (d.Type)
        {
            case IrType.I32:
                if (src is SlotOperand s)
                {
                    Emit(MOp.Lea, Lo(d), MMem.Frame(_m.Frame.SlotOffset(s.Slot)));
                }
                else
                {
                    Mov(Lo(d), RM(src));
                }
                break;
            case IrType.I64:
            {
                (MOperand lo, MOperand hi) = PairRM(src);
                Mov(Lo(d), lo);
                Mov(Hi(d), hi);
                break;
            }
            default:
                if (src is RegOperand r && r.Reg == d)
                {
                    break;
                }
                CopyFloatBits(FHome(d), FHome(src), FWidth(d.Type));
                break;
        }
    }

    // ---- integer arithmetic ---------------------------------------------------

    private static MOp AluOp(Opcode op) => op switch
    {
        Opcode.Add => MOp.Add,
        Opcode.Sub => MOp.Sub,
        Opcode.And => MOp.And,
        Opcode.Or => MOp.Or,
        _ => MOp.Xor,
    };

    /// <summary>
    /// The destination registers for a result, or fresh temporaries when the
    /// IR reuses a source register as the destination in a way the
    /// two-address form would clobber. Returns whether temporaries were used;
    /// the caller then moves them into place.
    /// </summary>
    private (MReg Lo, MReg Hi, bool Temp) DestPair(Instr i, int firstSource = 1)
    {
        VReg d = i.Dest!;
        bool clobbers = false;
        for (int k = firstSource; k < i.Operands.Count; k++)
        {
            clobbers |= Aliases(d, i.Operands[k]);
        }
        if (clobbers)
        {
            return (Temp(), d.Type == IrType.I64 ? Temp() : Lo(d), true);
        }
        return (Lo(d), d.Type == IrType.I64 ? Hi(d) : Lo(d), false);
    }

    private void Commit(Instr i, (MReg Lo, MReg Hi, bool Temp) dest)
    {
        if (!dest.Temp)
        {
            return;
        }
        Mov(Lo(i.Dest!), dest.Lo);
        if (i.Dest!.Type == IrType.I64)
        {
            Mov(Hi(i.Dest!), dest.Hi);
        }
    }

    private void SelectAlu(Instr i)
    {
        var dest = DestPair(i);
        MOp op = AluOp(i.Op);
        if (i.Op == Opcode.Add && i.Dest!.Type == IrType.I64)
        {
            _rangeVisits = 0;
            int leftBits = UnsignedBits(i.Operands[0]), rightBits = UnsignedBits(i.Operands[1]);
            if (Math.Max(leftBits, rightBits) + 1 <= 32)
            {
                // Both unsigned inputs and their complete sum fit one word.
                // Merely knowing each input fits is insufficient: that case
                // can still carry and must keep the ordinary ADD/ADC pair.
                Mov(dest.Lo, PairRM(i.Operands[0]).Lo);
                Emit(MOp.Add, dest.Lo, PairRM(i.Operands[1]).Lo);
                Mov(dest.Hi, Imm(0));
                Commit(i, dest);
                return;
            }
        }
        if (i.Op == Opcode.And && i.Dest!.Type.IsInt())
        {
            Operand value = i.Operands[0], maskOperand = i.Operands[1];
            if (value is ImmOperand) (value, maskOperand) = (maskOperand, value);
            if (maskOperand is ImmOperand mask)
            {
                _rangeVisits = 0;
                int bits = Math.Min(i.Dest.Type.Bytes() * 8, UnsignedBits(value));
                ulong possible = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                if ((unchecked((ulong)mask.Value) & possible) == possible)
                {
                    if (i.Dest.Type == IrType.I32) Mov(dest.Lo, RM(value));
                    else
                    {
                        var pair = PairRM(value);
                        Mov(dest.Lo, pair.Lo); Mov(dest.Hi, pair.Hi);
                    }
                    Commit(i, dest);
                    return;
                }
            }
        }
        if (i.Dest!.Type == IrType.I32)
        {
            BitOrAlu(op, dest.Lo, RM(i.Operands[0]), RM(i.Operands[1]));
        }
        else
        {
            (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
            (MOperand blo, MOperand bhi) = PairRM(i.Operands[1]);
            if (op is MOp.And or MOp.Or or MOp.Xor)
            {
                BitOrAlu(op, dest.Lo, alo, blo);
                BitOrAlu(op, dest.Hi, ahi, bhi);
            }
            else
            {
                Mov(dest.Lo, alo);
                Mov(dest.Hi, ahi);
                Emit(op, dest.Lo, blo);
                MOp hiOp = op == MOp.Add ? MOp.Adc : op == MOp.Sub ? MOp.Sbb : op;
                Emit(hiOp, dest.Hi, bhi);
            }
        }
        Commit(i, dest);
    }

    // IR bitwise operations do not expose flags. In particular, an I64 mask
    // may have an identity/absorbing half even when the whole mask does not.
    // Do not apply these rules to add/sub: the high half consumes carry.
    private void BitOrAlu(MOp op, MReg dest, MOperand left, MOperand right)
    {
        if (op is MOp.And or MOp.Or or MOp.Xor)
        {
            if (left is MImm { IsPlain: true } && right is not MImm) (left, right) = (right, left);
            if (right is MImm { IsPlain: true } imm)
            {
                int mask = unchecked((int)imm.Value);
                if ((op == MOp.And && mask == 0) || (op == MOp.Or && mask == -1))
                { Mov(dest, Imm(mask)); return; }
                if ((op == MOp.And && mask == -1) || (op is MOp.Or or MOp.Xor && mask == 0))
                { Mov(dest, left); return; }
                if (op == MOp.Xor && mask == -1)
                { Mov(dest, left); Emit(MOp.Not, dest); return; }
            }
        }
        Mov(dest, left);
        Emit(op, dest, right);
    }

    // Structural facts only: no assumption about mutable memory contents or
    // signed ranges. A multiply-defined register (including a parameter
    // assigned again) is unknown. Depth bounds make cycles conservative.
    private bool ZeroHigh(Operand operand, int depth = 0)
    {
        if (operand is ImmOperand immediate) return immediate.Value >= 0 && immediate.Value <= uint.MaxValue;
        if (depth == 0 && operand is RegOperand candidate
            && _definitions.TryGetValue(candidate.Reg, out Instr? unique) && unique is null)
        {
            _rangeVisits = 0;
            return UnsignedBits(operand) <= 32;
        }
        if (depth >= 8 || operand is not RegOperand register
            || !_definitions.TryGetValue(register.Reg, out Instr? def) || def is null) return false;
        if (def.Op == Opcode.ZExt32) return true;
        if (def.Op == Opcode.Load && !def.Signed && def.Size <= 4) return true;
        if (def.Op is Opcode.Shl or Opcode.Add or Opcode.ShrS)
        {
            _rangeVisits = 0;
            return UnsignedBits(operand) <= 32;
        }
        if (def.Op == Opcode.And && def.Operands.Count == 2)
            return ZeroHigh(def.Operands[0], depth + 1) || ZeroHigh(def.Operands[1], depth + 1);
        if (def.Op is Opcode.Or or Opcode.Xor && def.Operands.Count == 2)
            return ZeroHigh(def.Operands[0], depth + 1) && ZeroHigh(def.Operands[1], depth + 1);
        return def.Op == Opcode.ShrU && def.Dest?.Type == IrType.I64
            && def.Operands.Count == 2 && def.Operands[1] is ImmOperand shift && (shift.Value & 63) >= 32;
    }

    // Conservative unsigned upper bound, expressed as occupied bits. Unknown
    // values use 64. Every definition contributes to a register's bound;
    // parameters include an unknown entry value even when assigned later.
    private int UnsignedBits(Operand operand, int depth = 0)
    {
        if (operand is ImmOperand imm)
        {
            if (imm.Value < 0) return 64;
            ulong value = (ulong)imm.Value;
            int bits = 0;
            while (value != 0) { bits++; value >>= 1; }
            return bits;
        }
        if (depth >= 8 || ++_rangeVisits > 256 || operand is not RegOperand r
            || _f.Params.Contains(r.Reg) || !_definitions.TryGetValue(r.Reg, out Instr? only)) return 64;
        if (only is not null) return Math.Max(0, DefinitionBits(only, depth));
        int bound = 0;
        List<Instr> definitions = _rangeDefinitions[r.Reg];
        foreach (Instr definition in definitions)
        {
            bound = Math.Max(bound, DefinitionBits(definition, depth));
            if (bound == 64) break;
        }
        return bound;
    }

    private int DefinitionBits(Instr def, int depth)
    {
        if (def.Op == Opcode.Load && !def.Signed && def.Size is 1 or 2 or 4 or 8)
            return def.Size * 8;
        if (def.Op == Opcode.ZExt8) return 8;
        if (def.Op == Opcode.ZExt16) return 16;
        if (def.Op == Opcode.ZExt32) return Math.Min(32, UnsignedBits(def.Operands[0], depth + 1));
        if (def.Op == Opcode.Copy && def.Operands[0].Type == def.Dest?.Type)
            return UnsignedBits(def.Operands[0], depth + 1);
        if (def.Operands.Count != 2 || def.Dest?.Type != IrType.I64) return 64;
        int left = UnsignedBits(def.Operands[0], depth + 1);
        if ((def.Op == Opcode.ShrU || (def.Op == Opcode.ShrS && left <= 63))
            && def.Operands[1] is ImmOperand shift)
            return Math.Max(0, left - (int)(shift.Value & 63));
        if (def.Op == Opcode.Shl && def.Operands[1] is ImmOperand leftShift)
            return left == 0 ? 0 : Math.Min(64, left + (int)(leftShift.Value & 63));
        int right = UnsignedBits(def.Operands[1], depth + 1);
        return def.Op switch
        {
            Opcode.And => Math.Min(left, right),
            Opcode.Or or Opcode.Xor => Math.Max(left, right),
            Opcode.Add => Math.Min(64, Math.Max(left, right) + 1),
            Opcode.Mul => left == 0 || right == 0 ? 0 : Math.Min(64, left + right),
            _ => 64,
        };
    }

    /// <summary>
    /// `dest = src * factor`, 32 bits wrapping, as the processor --cpu names
    /// runs it best: IMUL, or shifts, LEAs, adds and subtracts where those
    /// are quicker -- the same product modulo 2^32, and nothing reads the
    /// flags of a multiplication selected here (a checked one is
    /// CheckedArith's). IMUL r, r, imm is 10 and unpaired on a Pentium (and
    /// AP-500 replaces one with six simple instructions); 2 or 3 on the K6,
    /// whose manual keeps IMUL wherever the shifts and adds would take 3
    /// clocks or more (AMD 21924, "Use integer multiply over shift-add
    /// sequences"); 4 on a Pentium Pro; on a 486 about 10 plus the bits of
    /// the constant, 13 to 42, where a shift is 2 clocks and a LEA with an
    /// index 2 (AP-500), and each step waits on the last; the K7's IMUL is 4
    /// and the K8's 3, a scaled LEA 2 on both (Agner Fog's tables). So at most
    /// 5, 2, 3 or 5 instructions replace it on the Pentium, the K6, the
    /// Pentium Pro and the 486 -- on a 486, 7 to 9 clocks at worst -- and 2 on
    /// the K7, 1 on the K8.
    /// The forms: n * 2^k, with n one of 1; 3, 5, 9 (one LEA); their
    /// products two at a time (two); 1 + {2,4,8} * {3,5,9} (two: 7, 11, 13,
    /// 19, 21, 25, 37, 41, 73); 2^j - 1 (a move, a shift and a subtract);
    /// 2^j + 1 (a move, a shift and an add); the shift by k last, and a
    /// negation for a negative factor. A source that is the destination is
    /// copied first where the recipe reads it after writing the destination,
    /// and the copy counts.
    /// </summary>
    private void MulByConstant(MReg dest, MReg src, int factor)
    {
        int limit = CpuModel.K6 ? 2 : CpuModel.K8 ? 1 : CpuModel.K7 ? 2 : CpuModel.P5 ? 5 : CpuModel.Scalar ? 5 : 3;
        long c = Math.Abs((long)factor);
        bool negate = factor < 0;
        if (c == 0)
        {
            Mov(dest, Imm(0));
            return;
        }
        int k = 0;
        while ((c & 1) == 0) { c >>= 1; k++; }
        static int Scale(long n) => n is 3 or 5 or 9 ? (int)n - 1 : 0;
        // The odd part's recipe and its instructions.
        int body; int kind = -1; long a = 0, b = 0; int j = 0;
        if (c == 1) { body = 0; kind = 0; }
        else if (Scale(c) != 0) { body = 1; kind = 1; a = c; }
        else
        {
            body = int.MaxValue;
            foreach (long x in new long[] { 3, 5, 9 })
                foreach (long y in new long[] { 3, 5, 9 })
                    if (x * y == c && body > 2) { body = 2; kind = 2; a = x; b = y; }
            foreach (long x in new long[] { 3, 5, 9 })
                foreach (long y in new long[] { 2, 4, 8 })
                    if (1 + y * x == c && body > 2) { body = 2; kind = 3; a = x; b = y; }
            for (int t = 2; t < 31 && body > 2; t++)
            {
                if (c == (1L << t) - 1) { body = 3; kind = 4; j = t; }
                else if (c == (1L << t) + 1) { body = 3; kind = 5; j = t; }
            }
        }
        // The recipes that read the source after writing the destination.
        bool copies = dest.Id == src.Id && kind >= 3;
        int cost = body == int.MaxValue ? int.MaxValue : body + (k > 0 ? 1 : 0) + (negate ? 1 : 0) + (copies ? 1 : 0);
        if (cost > limit)
        {
            Emit(MOp.Imul3, dest, src, Imm(factor));
            return;
        }
        MMem Scaled(MReg? baseReg, MReg index, long n) => new(baseReg, 0) { Index = index, Scale = (int)n };
        // The source read after the destination is written (a subtract, an
        // add, the second LEA's base): one register for both is copied first.
        if (copies)
        {
            MReg copy = Temp();
            Mov(copy, src);
            src = copy;
        }
        switch (kind)
        {
            case 0:
                Mov(dest, src);
                break;
            case 1:
                Emit(MOp.Lea, dest, Scaled(src, src, Scale(a)));
                break;
            case 2:
                Emit(MOp.Lea, dest, Scaled(src, src, Scale(a)));
                Emit(MOp.Lea, dest, Scaled(dest, dest, Scale(b)));
                break;
            case 3:
                // c = 1 + b * a: dest = a * src, then src + dest * b.
                Emit(MOp.Lea, dest, Scaled(src, src, Scale(a)));
                Emit(MOp.Lea, dest, Scaled(src, dest, b));
                break;
            case 4:
                Mov(dest, src);
                Emit(MOp.Shl, dest, Imm(j));
                Emit(MOp.Sub, dest, src);
                break;
            case 5:
                Mov(dest, src);
                Emit(MOp.Shl, dest, Imm(j));
                Emit(MOp.Add, dest, src);
                break;
        }
        if (k == 1) Emit(MOp.Add, dest, dest);
        else if (k > 1) Emit(MOp.Shl, dest, Imm(k));
        if (negate) Emit(MOp.Neg, dest);
    }

    private void SelectMul(Instr i)
    {
        var dest = DestPair(i);
        if (i.Dest!.Type == IrType.I32)
        {
            if (i.Operands[1] is ImmOperand imm)
            {
                MulByConstant(dest.Lo, R(i.Operands[0]), (int)imm.Value);
            }
            else
            {
                Mov(dest.Lo, RM(i.Operands[0]));
                Emit(MOp.Imul, dest.Lo, RM(i.Operands[1]));
            }
            Commit(i, dest);
            return;
        }

        Operand left = i.Operands[0], right = i.Operands[1];
        if (left is ImmOperand && right is not ImmOperand) (left, right) = (right, left);
        _rangeVisits = 0;
        if (UnsignedBits(left) + UnsignedBits(right) <= 32)
        {
            // The entire product, not merely each input, fits unsigned I32.
            // IMUL's low word is the same unsigned product; no widening MUL,
            // EDX:EAX clobbers or cross products are needed.
            MReg product = Temp();
            if (right is ImmOperand factor)
                Emit(MOp.Imul3, product, Pair(left).Lo, Imm(unchecked((int)factor.Value)));
            else
            {
                Mov(product, PairRM(left).Lo);
                Emit(MOp.Imul, product, PairRM(right).Lo);
            }
            Mov(dest.Lo, product); Mov(dest.Hi, Imm(0));
            Commit(i, dest);
            return;
        }
        if (right is ImmOperand constant)
        {
            (MOperand low, MOperand high) = PairRM(left);
            uint bottom = unchecked((uint)constant.Value);
            int top = unchecked((int)(constant.Value >> 32));
            if (bottom == 0)
            {
                // The only surviving product is low(left)*high(constant).
                Mov(dest.Hi, low);
                Emit(MOp.Imul3, dest.Hi, dest.Hi, Imm(top));
                Mov(dest.Lo, Imm(0));
                Commit(i, dest);
                return;
            }
            if (top == 0)
            {
                MReg upper = Temp(), multiplier = Temp();
                bool narrow = ZeroHigh(left);
                if (!narrow) { Mov(upper, high); Emit(MOp.Imul3, upper, upper, Imm(unchecked((int)bottom))); }
                Mov(multiplier, Imm(unchecked((int)bottom)));
                Mov(Eax, low); Emit(MOp.Mul, multiplier);
                Mov(dest.Lo, Eax); Mov(dest.Hi, Edx);
                if (!narrow) Emit(MOp.Add, dest.Hi, upper);
                Commit(i, dest);
                return;
            }
        }

        // (ah:al) * (bh:bl) mod 2^64 = al*bl + ((ah*bl + al*bh) << 32).
        // One widening mul for the low product and two truncating imuls for
        // the cross terms; the ah*bh term never reaches the low 64 bits.
        (MReg al, MReg ah) = Pair(i.Operands[0]);
        (MReg bl, MReg bh) = Pair(i.Operands[1]);
        bool aNarrow = ZeroHigh(i.Operands[0]), bNarrow = ZeroHigh(i.Operands[1]);
        MReg cross = Temp();
        if (!aNarrow) { Mov(cross, ah); Emit(MOp.Imul, cross, bl); }
        if (!bNarrow)
        {
            MReg cross2 = aNarrow ? cross : Temp();
            Mov(cross2, al); Emit(MOp.Imul, cross2, bh);
            if (!aNarrow) Emit(MOp.Add, cross, cross2);
        }
        Mov(Eax, al);
        Emit(MOp.Mul, bl);
        Mov(dest.Lo, Eax);
        Mov(dest.Hi, Edx);
        if (!aNarrow || !bNarrow) Emit(MOp.Add, dest.Hi, cross);
        Commit(i, dest);
    }

    private static bool PowerOfTwo(ulong v) => v != 0 && (v & (v - 1)) == 0;

    /// <summary>
    /// The signed magic number and shift for `d` (not -1, 0, 1 or a power of
    /// two in magnitude): Hacker's Delight figure 10-1. Checked against
    /// truncating division for every divisor from -300 to 300 and the
    /// extremes, with every dividend edge and hundreds of random ones.
    /// </summary>
    private static (int Magic, int Shift) MagicSigned(int d)
    {
        const uint two31 = 0x80000000;
        uint ad = (uint)Math.Abs((long)d);
        uint t = two31 + (unchecked((uint)d) >> 31);
        uint anc = t - 1 - t % ad;
        int p = 31;
        uint q1 = two31 / anc, r1 = two31 - q1 * anc, q2 = two31 / ad, r2 = two31 - q2 * ad, delta;
        do
        {
            p++;
            q1 = 2 * q1; r1 = 2 * r1;
            if (r1 >= anc) { q1++; r1 -= anc; }
            q2 = 2 * q2; r2 = 2 * r2;
            if (r2 >= ad) { q2++; r2 -= ad; }
            delta = ad - r2;
        } while (q1 < delta || (q1 == delta && r1 == 0));
        int magic = unchecked((int)(q2 + 1));
        return (d < 0 ? -magic : magic, p - 32);
    }

    /// <summary>
    /// The unsigned magic number, shift and whether the add-and-halve form
    /// is needed, for `d` at least 2 and not a power of two: Hacker's
    /// Delight figure 10-2, checked as MagicSigned is.
    /// </summary>
    private static (uint Magic, int Shift, bool Add) MagicUnsigned(uint d)
    {
        int p = 31;
        uint nc = uint.MaxValue - unchecked(0u - d) % d;
        uint q1 = 0x80000000 / nc, r1 = 0x80000000 - q1 * nc, q2 = 0x7FFFFFFF / d, r2 = 0x7FFFFFFF - q2 * d, delta;
        bool add = false;
        do
        {
            p++;
            if (r1 >= nc - r1) { q1 = unchecked(2 * q1 + 1); r1 = unchecked(2 * r1 - nc); }
            else { q1 = unchecked(2 * q1); r1 = unchecked(2 * r1); }
            if (r2 + 1 >= d - r2)
            {
                if (q2 >= 0x7FFFFFFF) add = true;
                q2 = unchecked(2 * q2 + 1); r2 = unchecked(2 * r2 + 1 - d);
            }
            else
            {
                if (q2 >= 0x80000000) add = true;
                q2 = unchecked(2 * q2); r2 = unchecked(2 * r2 + 1);
            }
            delta = d - 1 - r2;
        } while (p < 64 && (q1 < delta || (q1 == delta && r1 == 0)));
        return (unchecked(q2 + 1), p - 32, add);
    }

    /// <summary>A 32-bit quotient or remainder by a constant, by its magic number (MagicSigned, MagicUnsigned).</summary>
    private void DivideByConstant(Instr i, bool signed, bool rem, long value)
    {
        // THE MAGIC NUMBER IN EAX, THE DIVIDEND THE MULTIPLIER: a 486 stops
        // multiplying at the multiplier's highest bit (MUL and IMUL, 13 to
        // 42 clocks), and a dividend is mostly small where a magic number has
        // 32 bits -- 42 clocks of it and the division lost.
        MReg numerator = Temp(), quotient = Temp();
        Mov(numerator, RM(i.Operands[0]));
        if (signed)
        {
            int d = unchecked((int)value);
            (int magic, int shift) = MagicSigned(d);
            Mov(Eax, Imm(magic));
            Emit(MOp.ImulWide, numerator);
            Mov(quotient, Edx);
            if (d > 0 && magic < 0) Emit(MOp.Add, quotient, numerator);
            if (d < 0 && magic > 0) Emit(MOp.Sub, quotient, numerator);
            if (shift != 0) Emit(MOp.Sar, quotient, Imm(shift));
            // Toward zero: one more for a negative quotient.
            MReg sign = Temp();
            Mov(sign, quotient); Emit(MOp.Shr, sign, Imm(31));
            Emit(MOp.Add, quotient, sign);
        }
        else
        {
            (uint magic, int shift, bool add) = MagicUnsigned(unchecked((uint)value));
            Mov(Eax, Imm(unchecked((int)magic)));
            Emit(MOp.Mul, numerator);
            Mov(quotient, Edx);
            if (!add)
            {
                if (shift != 0) Emit(MOp.Shr, quotient, Imm(shift));
            }
            else
            {
                // ((n - t) >> 1) + t, then >> (shift - 1): the 33-bit magic.
                MReg halved = Temp();
                Mov(halved, numerator); Emit(MOp.Sub, halved, quotient);
                Emit(MOp.Shr, halved, Imm(1)); Emit(MOp.Add, halved, quotient);
                if (shift > 1) Emit(MOp.Shr, halved, Imm(shift - 1));
                quotient = halved;
            }
        }
        if (rem)
        {
            MReg product = Temp();
            MulByConstant(product, quotient, unchecked((int)value));
            Mov(Lo(i.Dest!), numerator); Emit(MOp.Sub, Lo(i.Dest!), product);
        }
        else Mov(Lo(i.Dest!), quotient);
    }

    private void SelectDiv(Instr i)
    {
        bool signed = i.Op is Opcode.DivS or Opcode.RemS;
        bool rem = i.Op is Opcode.RemS or Opcode.RemU;
        // ANY 32-BIT CONSTANT DIVISOR BUT A POWER OF TWO: a multiplication
        // by its magic number and shifts (Granlund and Montgomery; Hacker's
        // Delight, chapter 10). DIV is 41 clocks on a Pentium and IDIV 46,
        // both unpaired; the K6 gives two bits of quotient a clock, some 18
        // for 32 bits, against a two-clock multiply (AMD 21924, "Replace
        // integer division by constants with multiplication by the
        // reciprocal"); a 486 divides in 40 to 43. -1 never reaches here
        // (Lowering.Divide), 0 is DivideByZeroCheck's.
        if (i.Dest!.Type == IrType.I32 && i.Operands[1] is ImmOperand divisorImm
            && (signed ? unchecked((int)divisorImm.Value) is not (-1 or 0 or 1) && !PowerOfTwo((ulong)Math.Abs((long)(int)divisorImm.Value))
                       : unchecked((uint)divisorImm.Value) >= 2 && !PowerOfTwo(unchecked((uint)divisorImm.Value))))
        {
            DivideByConstant(i, signed, rem, divisorImm.Value);
            return;
        }
        if (i.Operands[1] is ImmOperand constant && constant.Value is 3 or 5 or 10
            && i.Dest!.Type == IrType.I64 && ZeroHigh(i.Operands[0]))
        {
            // Exact unsigned quotients over [0, 2^32-1]: high32(n*m) >> s.
            // ceil(2^33/3), ceil(2^34/5), ceil(2^35/10), respectively.
            int shift = constant.Value == 3 ? 1 : constant.Value == 5 ? 2 : 3;
            int magic = constant.Value == 3 ? unchecked((int)0xaaaaaaab) : unchecked((int)0xcccccccd);
            MReg numerator = Temp(), quotient = Temp();
            Mov(numerator, i.Dest.Type == IrType.I64 ? PairRM(i.Operands[0]).Lo : RM(i.Operands[0]));
            // The dividend the multiplier, as DivideByConstant's.
            Mov(Eax, Imm(magic));
            Emit(MOp.Mul, numerator);
            Mov(quotient, Edx); Emit(MOp.Shr, quotient, Imm(shift));
            if (rem)
            {
                MReg product = Temp();
                MulByConstant(product, quotient, (int)constant.Value);
                Mov(Lo(i.Dest), numerator); Emit(MOp.Sub, Lo(i.Dest), product);
            }
            else Mov(Lo(i.Dest), quotient);
            if (i.Dest.Type == IrType.I64) Mov(Hi(i.Dest), Imm(0));
            return;
        }
        if (i.Dest!.Type == IrType.I64 && ZeroHigh(i.Operands[0]) && ZeroHigh(i.Operands[1]))
        {
            // Both I64 values are nonnegative and fit in 32 unsigned bits.
            // Signed/unsigned division therefore agree. Keep DIV's zero-
            // divisor trap rather than folding away exceptional behavior.
            MReg divisor = Temp();
            Mov(divisor, PairRM(i.Operands[1]).Lo);
            Mov(Eax, PairRM(i.Operands[0]).Lo);
            Mov(Edx, Imm(0));
            Emit(MOp.Div, divisor);
            Mov(Lo(i.Dest), rem ? Edx : Eax);
            Mov(Hi(i.Dest), Imm(0));
            return;
        }
        if (i.Dest!.Type == IrType.I32)
        {
            // The divisor must not be an immediate and must not be EAX or
            // EDX; a fresh temporary loaded from whatever it is satisfies
            // both, and coalescing removes the move when it was a register.
            MReg divisor = Temp();
            Mov(divisor, RM(i.Operands[1]));
            Mov(Eax, RM(i.Operands[0]));
            if (signed)
            {
                Emit(MOp.Cdq);
            }
            else
            {
                Emit(MOp.Xor, Edx, Edx);
            }
            Emit(signed ? MOp.Idiv : MOp.Div, divisor);
            Mov(Lo(i.Dest), rem ? Edx : Eax);
            return;
        }

        // The 486 has no 64-bit divide: a cdecl runtime helper does it.
        string helper = (signed, rem) switch
        {
            (true, false) => "__divdi3",
            (false, false) => "__udivdi3",
            (true, true) => "__moddi3",
            _ => "__umoddi3",
        };
        (MOperand blo, MOperand bhi) = PairRM(i.Operands[1]);
        (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
        Emit(MOp.Push, bhi);
        Emit(MOp.Push, blo);
        Emit(MOp.Push, ahi);
        Emit(MOp.Push, alo);
        CallSymbol(helper);
        Emit(MOp.Add, Esp, Imm(16));
        Mov(Lo(i.Dest), Eax);
        Mov(Hi(i.Dest), Edx);
    }

    private void SelectShift(Instr i)
    {
        MOp op = i.Op switch
        {
            Opcode.Shl => MOp.Shl,
            Opcode.ShrU => MOp.Shr,
            _ => MOp.Sar,
        };
        var dest = DestPair(i);
        Operand count = i.Operands[1];

        if (i.Dest!.Type == IrType.I32)
        {
            Mov(dest.Lo, RM(i.Operands[0]));
            if (count is ImmOperand c)
            {
                Emit(op, dest.Lo, Imm(c.Value & 31));
            }
            else
            {
                Mov(Ecx, RM(count));
                EmitCl(op, dest.Lo);
            }
            Commit(i, dest);
            return;
        }

        (MOperand alo, MOperand ahi) = PairRM(i.Operands[0]);
        if (op == MOp.Shl && count is ImmOperand smallCount)
        {
            int n = (int)(smallCount.Value & 63);
            _rangeVisits = 0;
            if (n < 32 && UnsignedBits(i.Operands[0]) + n <= 32)
            {
                Mov(dest.Lo, alo);
                if (n != 0) Emit(MOp.Shl, dest.Lo, Imm(n));
                Mov(dest.Hi, Imm(0));
                Commit(i, dest);
                return;
            }
        }
        if (op != MOp.Shl && count is ImmOperand narrowCount && ZeroHigh(i.Operands[0]))
        {
            int n = (int)(narrowCount.Value & 63);
            // A nonnegative I64 with no upper bits needs only a logical
            // low-word shift, including when the IR requested signed shift.
            Mov(dest.Lo, n >= 32 ? Imm(0) : alo);
            if (n > 0 && n < 32) Emit(MOp.Shr, dest.Lo, Imm(n));
            Mov(dest.Hi, Imm(0));
            Commit(i, dest);
            return;
        }
        if (count is ImmOperand wideCount && (wideCount.Value & 63) >= 32)
        {
            int n = (int)(wideCount.Value & 63) - 32;
            // Only one source half contributes. Do not first copy the half
            // that is immediately overwritten: it creates needless spills.
            if (op == MOp.Shl)
            {
                Mov(dest.Hi, alo);
                if (n != 0) Emit(MOp.Shl, dest.Hi, Imm(n));
                Mov(dest.Lo, Imm(0));
            }
            else
            {
                Mov(dest.Lo, ahi);
                if (op == MOp.Shr) Mov(dest.Hi, Imm(0));
                else { Mov(dest.Hi, ahi); Emit(MOp.Sar, dest.Hi, Imm(31)); }
                if (n != 0) Emit(op, dest.Lo, Imm(n));
            }
            Commit(i, dest);
            return;
        }
        Mov(dest.Lo, alo);
        Mov(dest.Hi, ahi);

        if (count is ImmOperand ci)
        {
            int n = (int)(ci.Value & 63);
            if (n == 0)
            {
                // Nothing to shift.
            }
            else if (n < 32)
            {
                if (op == MOp.Shl)
                {
                    Emit(MOp.Shld, dest.Hi, dest.Lo, Imm(n));
                    Emit(MOp.Shl, dest.Lo, Imm(n));
                }
                else
                {
                    Emit(MOp.Shrd, dest.Lo, dest.Hi, Imm(n));
                    Emit(op, dest.Hi, Imm(n));
                }
            }
            else if (op == MOp.Shl)
            {
                Mov(dest.Hi, dest.Lo);
                Emit(MOp.Xor, dest.Lo, dest.Lo);
                if (n > 32)
                {
                    Emit(MOp.Shl, dest.Hi, Imm(n - 32));
                }
            }
            else
            {
                Mov(dest.Lo, dest.Hi);
                if (op == MOp.Shr)
                {
                    Emit(MOp.Xor, dest.Hi, dest.Hi);
                }
                else
                {
                    Emit(MOp.Sar, dest.Hi, Imm(31));
                }
                if (n > 32)
                {
                    Emit(op, dest.Lo, Imm(n - 32));
                }
            }
            Commit(i, dest);
            return;
        }

        // A variable count: the double shift handles counts under 32 (the
        // hardware masks CL to five bits), and a branch fixes up the rest.
        Mov(Ecx, RM(count));
        if (op == MOp.Shl)
        {
            EmitCl(MOp.Shld, dest.Hi, dest.Lo);
            EmitCl(MOp.Shl, dest.Lo);
        }
        else
        {
            EmitCl(MOp.Shrd, dest.Lo, dest.Hi);
            EmitCl(op, dest.Hi);
        }
        Emit(MOp.Test, Ecx, Imm(32));
        MBlock big = Aside();
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", big);
        Jcc(Cond.E, done);
        _cur = big;
        if (op == MOp.Shl)
        {
            Mov(dest.Hi, dest.Lo);
            Emit(MOp.Xor, dest.Lo, dest.Lo);
        }
        else
        {
            Mov(dest.Lo, dest.Hi);
            if (op == MOp.Shr)
            {
                Emit(MOp.Xor, dest.Hi, dest.Hi);
            }
            else
            {
                Emit(MOp.Sar, dest.Hi, Imm(31));
            }
        }
        _cur = done;
        Commit(i, dest);
    }

    /// <summary>A shift by CL: the count operand is the byte register.</summary>
    private void EmitCl(MOp op, MReg dest, MReg? src = null)
    {
        if (src is null)
        {
            Emit(op, dest, Ecx);
        }
        else
        {
            Emit(op, dest, src, Ecx);
        }
    }

    private void SelectUnary(Instr i)
    {
        MOp op = i.Op == Opcode.Neg ? MOp.Neg : MOp.Not;
        VReg d = i.Dest!;
        if (d.Type == IrType.I32)
        {
            Mov(Lo(d), RM(i.Operands[0]));
            Emit(op, Lo(d));
            return;
        }
        (MOperand lo, MOperand hi) = PairRM(i.Operands[0]);
        Mov(Lo(d), lo);
        Mov(Hi(d), hi);
        if (op == MOp.Not)
        {
            Emit(MOp.Not, Lo(d));
            Emit(MOp.Not, Hi(d));
        }
        else
        {
            // -(h:l) = ~(h:l) + 1 = (-l, ~h + borrow); `neg lo` sets carry when lo was nonzero.
            Emit(MOp.Neg, Lo(d));
            Emit(MOp.Adc, Hi(d), Imm(0));
            Emit(MOp.Neg, Hi(d));
        }
    }

    // ---- comparisons ----------------------------------------------------------

    private static Cond CondOf(Opcode op) => op switch
    {
        Opcode.Eq => Cond.E,
        Opcode.Ne => Cond.Ne,
        Opcode.LtS => Cond.L,
        Opcode.LeS => Cond.Le,
        Opcode.GtS => Cond.G,
        Opcode.GeS => Cond.Ge,
        Opcode.LtU => Cond.B,
        Opcode.LeU => Cond.Be,
        Opcode.GtU => Cond.A,
        _ => Cond.Ae,
    };

    /// <summary>Set the flags for an integer compare; returns the condition that means "true".</summary>
    private Cond IntCompare(Instr i)
    {
        Cond c = CondOf(i.Op);
        Operand a = i.Operands[0];
        Operand b = i.Operands[1];
        bool narrow64 = a.Type == IrType.I64 && ZeroHigh(a) && ZeroHigh(b);
        if (narrow64)
        {
            // Positive I64 operands are ordered by their unsigned low words,
            // not by the sign bit of those low words.
            c = c switch { Cond.L => Cond.B, Cond.Le => Cond.Be, Cond.G => Cond.A, Cond.Ge => Cond.Ae, _ => c };
        }
        if (a.Type == IrType.I32 || narrow64)
        {
            if (a is not RegOperand && b is RegOperand)
            {
                (a, b) = (b, a);
                c = c.Swap();
            }
            MReg ra = R(a);
            if (b is ImmOperand { Value: 0 })
            {
                // Against zero, test is a byte shorter than cmp and says the same.
                Emit(MOp.Test, ra, ra);
            }
            else
            {
                Emit(MOp.Cmp, ra, RM(b));
            }
            return c;
        }

        if (c is Cond.E or Cond.Ne)
        {
            if (a is not RegOperand && b is RegOperand)
            {
                (a, b) = (b, a);
            }
            (MOperand alo, MOperand ahi) = PairRM(a);
            (MOperand blo, MOperand bhi) = PairRM(b);
            // ZF of (alo ^ blo) | (ahi ^ bhi); a zero half of b needs no xor at all.
            MReg t = Temp();
            Mov(t, alo);
            if (blo is not MImm { IsPlain: true, Value: 0 })
            {
                Emit(MOp.Xor, t, blo);
            }
            if (bhi is MImm { IsPlain: true, Value: 0 })
            {
                Emit(MOp.Or, t, ahi);
            }
            else
            {
                MReg u = Temp();
                Mov(u, ahi);
                Emit(MOp.Xor, u, bhi);
                Emit(MOp.Or, t, u);
            }
            return c;
        }

        // A subtract with borrow leaves SF, OF and CF describing the whole
        // 64-bit difference, but ZF only its high half; so less-than and
        // greater-or-equal read straight off it, and the other two are the
        // same test with the operands exchanged.
        if (c is Cond.Le or Cond.G or Cond.Be or Cond.A)
        {
            (a, b) = (b, a);
            c = c.Swap();
        }
        (MReg xl, MReg xh) = Pair(a);
        (MOperand yl, MOperand yh) = PairRM(b);
        MReg h = Temp();
        Emit(MOp.Cmp, xl, yl);
        Mov(h, xh);
        Emit(MOp.Sbb, h, yh);
        return c;
    }

    /// <summary>Materialise a condition as 0 or 1 in a byte-capable register, widened.</summary>
    private void SetCond(Cond c, VReg dest)
    {
        MReg d = Lo(dest);
        _m.ByteRegs.Add(d.Id);
        Emit(new MInstr(MOp.Setcc, d) { Cond = c, Width = 1 });
        EmitW(MOp.Movzx, 1, d, d);
    }

    private void SelectCompare(Instr i)
    {
        Cond c = IntCompare(i);
        SetCond(c, i.Dest!);
    }

    /// <summary>
    /// How an x87 compare result maps to flags. After fucompp / fnstsw /
    /// sahf, CF is "below", ZF is "equal" and PF is "unordered", with all
    /// three set for unordered; every ordered relation is chosen so that
    /// pattern reads as false, and equality has to test parity separately.
    /// The Pentium Pro's fucomip leaves exactly those three flags, so one
    /// mapping serves both.
    /// </summary>
    private readonly record struct FloatCond(Cond Cond, bool ParityFalse, bool ParityTrue);

    private FloatCond FloatCompare(Instr i)
    {
        Operand a = i.Operands[0];
        Operand b = i.Operands[1];
        // ST(0) is whichever was loaded second; the flags describe ST(0) ? ST(1).
        bool aOnTop = i.Op is Opcode.FGt or Opcode.FGe or Opcode.FEq or Opcode.FNe;
        Fld(aOnTop ? b : a);
        Fld(aOnTop ? a : b);
        // WHERE THE FPU SETS EFLAGS ITSELF, from the Pentium Pro on, the
        // comparison is one instruction and EAX stays free. fnstsw is slow
        // on the P6 and the K7, and it writes AX, which the allocator must
        // keep clear or spill around at every comparison.
        if (Target.Current.X86Profile.FloatCmov)
        {
            Emit(MOp.Fucomipp);
        }
        else
        {
            Emit(MOp.Fucompp);
            Emit(MOp.Fnstsw);
            Emit(MOp.Sahf);
        }
        return i.Op switch
        {
            Opcode.FEq => new FloatCond(Cond.E, true, false),
            Opcode.FNe => new FloatCond(Cond.Ne, false, true),
            Opcode.FLt or Opcode.FGt => new FloatCond(Cond.A, false, false),
            _ => new FloatCond(Cond.Ae, false, false),
        };
    }

    private void SelectFloatCompare(Instr i)
    {
        FloatCond fc = FloatCompare(i);
        MReg d = Lo(i.Dest!);
        _m.ByteRegs.Add(d.Id);
        Emit(new MInstr(MOp.Setcc, d) { Cond = fc.Cond, Width = 1 });
        if (fc.ParityFalse || fc.ParityTrue)
        {
            MReg p = Temp();
            _m.ByteRegs.Add(p.Id);
            Emit(new MInstr(MOp.Setcc, p) { Cond = fc.ParityFalse ? Cond.Np : Cond.P, Width = 1 });
            EmitW(fc.ParityFalse ? MOp.And : MOp.Or, 1, d, p);
        }
        EmitW(MOp.Movzx, 1, d, d);
    }

    private void SelectFloatAlu(Instr i)
    {
        MOp op = i.Op switch
        {
            Opcode.FAdd => MOp.Fadd,
            Opcode.FSub => MOp.Fsub,
            Opcode.FMul => MOp.Fmul,
            _ => MOp.Fdiv,
        };
        Fld(i.Operands[0]);
        EmitW(op, FWidth(i.Operands[1].Type), FHome(i.Operands[1]));
        Fstp(i.Dest!);
    }

    // ---- conversions ------------------------------------------------------------

    private void SelectNarrow(Instr i)
    {
        VReg d = i.Dest!;
        MReg src = R(i.Operands[0]);
        MReg lo = Lo(d);
        switch (i.Op)
        {
            case Opcode.ZExt8:
                // `and` rather than movzx from a byte register: any register can do it.
                Mov(lo, src);
                Emit(MOp.And, lo, Imm(0xFF));
                break;
            case Opcode.ZExt16:
                EmitW(MOp.Movzx, 2, lo, src);
                break;
            case Opcode.SExt16:
                EmitW(MOp.Movsx, 2, lo, src);
                break;
            default:
                _m.ByteRegs.Add(src.Id);
                EmitW(MOp.Movsx, 1, lo, src);
                break;
        }
        if (d.Type == IrType.I64)
        {
            if (i.Op is Opcode.ZExt8 or Opcode.ZExt16)
            {
                Emit(MOp.Xor, Hi(d), Hi(d));
            }
            else
            {
                Mov(Hi(d), lo);
                Emit(MOp.Sar, Hi(d), Imm(31));
            }
        }
    }

    private void SelectIntToFloat(Instr i)
    {
        Operand src = i.Operands[0];
        int s = _m.Frame.Scratch;
        bool wide = src.Type == IrType.I64;
        if (wide)
        {
            (MOperand lo, MOperand hi) = PairRM(src);
            Mov(MMem.Frame(s), lo);
            Mov(MMem.Frame(s + 4), hi);
        }
        else
        {
            Mov(MMem.Frame(s), RM(src));
            if (i.Op == Opcode.UToF)
            {
                // fild is signed: a 32-bit unsigned value is the 64-bit value with a zero high half.
                Mov(MMem.Frame(s + 4), Imm(0));
            }
        }
        EmitW(MOp.Fild, wide || i.Op == Opcode.UToF ? 8 : 4, MMem.Frame(s));
        if (wide && i.Op == Opcode.UToF)
        {
            // fild read the value as signed; a set top bit means it was 2^64 too small.
            // 2^64 is a power of two, so a single-precision constant is exact.
            Emit(MOp.Test, MMem.Frame(s + 4), Imm(int.MinValue));
            MBlock fix = Aside();
            MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", fix);
            Jcc(Cond.E, done);
            _cur = fix;
            Emit(MOp.Push, Imm(0x5F800000));
            EmitW(MOp.Fadd, 4, new MMem(Esp, 0));
            Emit(MOp.Add, Esp, Imm(4));
            _cur = done;
        }
        Fstp(i.Dest!);
    }

    private void SelectFloatToIntCore(Instr i, bool unsignedHighHalf = false)
    {
        VReg d = i.Dest!;
        // IN RANGE, SSE TRUNCATES AS C# DOES. SelectFloatToInt has sent NaN,
        // overflow and the negative unsigned cases to blocks of their own, so
        // the operand here is finite and inside the destination's range, where
        // cvttss2si and cvttsd2si answer exactly what .NET does, and need no
        // FNSTCW/FLDCW round trip: FLDCW is an expensive instruction on the P6
        // and the K7. They read the float's home, so no XMM register is taken
        // or kept live. Only code whose thread owns its state
        // (Lowering.OwnsFpuState), and only where --cpu has SSE: the
        // operating system enables it (CR4.OSFXSR) and keeps it, as it keeps
        // the x87 and MMX state AutomaticPacked borrows -- Linux wherever the
        // processor has it, Windows where that --cpu says it does. 32-bit SSE has no 64-bit integer result, so
        // Int64, and the UInt32 that needs the 64-bit store, stay on the x87.
        bool single = FWidth(i.Operands[0].Type) == 4;
        if (!unsignedHighHalf && i.Op == Opcode.FToI && d.Type == IrType.I32 && SseHere
            && Target.Current.X86Profile.Has(single ? X86Feature.Sse : X86Feature.Sse2))
        {
            Emit(single ? MOp.Cvttss2si : MOp.Cvttsd2si, Lo(d), FHome(i.Operands[0]));
            return;
        }
        int s = _m.Frame.Scratch;
        Fld(i.Operands[0]);
        if (unsignedHighHalf)
        {
            // Convert the upper half of UInt64 through a signed FISTP, without
            // overflowing its signed range. The power-of-two subtraction is exact.
            Emit(MOp.Push, Imm(0x5f000000));
            EmitW(MOp.Fsub, 4, new MMem(Esp, 0)); Emit(MOp.Add, Esp, Imm(4));
        }
        // fistp rounds by the control word, which defaults to nearest; C#
        // truncates. Save the word, set RC=11 (chop), convert, restore.
        EmitW(MOp.Fnstcw, 2, MMem.Frame(s));
        MReg cw = Temp();
        EmitW(MOp.Movzx, 2, cw, MMem.Frame(s));
        Emit(MOp.Or, cw, Imm(0xC00));
        EmitW(MOp.Mov, 2, MMem.Frame(s + 2), cw);
        EmitW(MOp.Fldcw, 2, MMem.Frame(s + 2));
        // An unsigned 32-bit result needs the 64-bit store: the 32-bit form
        // cannot represent anything above 2^31 - 1.
        bool qword = d.Type == IrType.I64 || i.Op == Opcode.FToU;
        EmitW(MOp.Fistp, qword ? 8 : 4, MMem.Frame(s + 8));
        EmitW(MOp.Fldcw, 2, MMem.Frame(s));
        Mov(Lo(d), MMem.Frame(s + 8));
        if (d.Type == IrType.I64)
        {
            Mov(Hi(d), MMem.Frame(s + 12));
            if (unsignedHighHalf) Emit(MOp.Or, Hi(d), Imm(int.MinValue));
        }
    }

    private void SelectBits(Instr i)
    {
        VReg d = i.Dest!;
        Operand src = i.Operands[0];
        switch (d.Type)
        {
            case IrType.I32:
                Mov(Lo(d), FHome(src));
                break;
            case IrType.I64:
            {
                MMem home = FHome(src);
                Mov(Lo(d), home);
                Mov(Hi(d), new MMem(home.Base, home.Disp + 4));
                break;
            }
            case IrType.F32:
                Mov(FHome(d), RM(src));
                break;
            default:
            {
                (MOperand lo, MOperand hi) = PairRM(src);
                MMem home = FHome(d);
                Mov(home, lo);
                Mov(new MMem(home.Base, home.Disp + 4), hi);
                break;
            }
        }
    }

    // ---- memory -------------------------------------------------------------------

    private void SelectLoad(Instr i)
    {
        VReg d = i.Dest!;
        MMem m = Address(i.Operands[0], i.Offset);
        if (i.Device) m = new MMem(m.Base, m.Disp) { Index = m.Index, Scale = m.Scale, Symbol = m.Symbol, Reloc = m.Reloc, Label = m.Label, Device = true };
        if (d.Type.IsFloat())
        {
            if (i.Size == FWidth(d.Type)) { CopyFloatBits(FHome(d), m, i.Size); return; }
            EmitW(MOp.Fld, i.Size, m);
            Fstp(d);
            return;
        }
        MReg lo = Lo(d);
        switch (i.Size)
        {
            case 1:
            case 2:
                EmitW(i.Signed ? MOp.Movsx : MOp.Movzx, i.Size, lo, m);
                break;
            case 4:
                Mov(lo, m);
                break;
            case 8:
            {
                if (d.Type != IrType.I64)
                {
                    Error("8-byte load into a 32-bit register");
                    return;
                }
                MMem m4 = Displaced(m, 4);
                // Load the half that is not the base register last, so the
                // address survives until both halves are read.
                if (m.Base is not null && m.Base.Id == lo.Id)
                {
                    Mov(Hi(d), m4);
                    Mov(lo, m);
                }
                else
                {
                    Mov(lo, m);
                    Mov(Hi(d), m4);
                }
                break;
            }
            default:
                Error($"load of size {i.Size}");
                break;
        }
        if (d.Type == IrType.I64 && i.Size < 8)
        {
            if (i.Signed)
            {
                Mov(Hi(d), lo);
                Emit(MOp.Sar, Hi(d), Imm(31));
            }
            else
            {
                Emit(MOp.Xor, Hi(d), Hi(d));
            }
        }
    }

    private void SelectStore(Instr i)
    {
        MMem m = Address(i.Operands[0], i.Offset);
        Operand v = i.StoredValue;
        if (v.Type.IsFloat())
        {
            if (i.Size == FWidth(v.Type)) { CopyFloatBits(m, FHome(v), i.Size); return; }
            Fld(v);
            EmitW(MOp.Fstp, i.Size, m);
            return;
        }
        switch (i.Size)
        {
            case 1:
            case 2:
            {
                MOperand src = v.Type == IrType.I64 ? PairRM(v).Lo : RM(v);
                if (src is MReg r && i.Size == 1)
                {
                    _m.ByteRegs.Add(r.Id);
                }
                if (src is MImm imm)
                {
                    src = Imm(imm.Value & (i.Size == 1 ? 0xFF : 0xFFFF));
                }
                EmitW(MOp.Mov, i.Size, m, src);
                break;
            }
            case 4:
                Mov(m, v.Type == IrType.I64 ? PairRM(v).Lo : RM(v));
                break;
            case 8:
            {
                (MOperand lo, MOperand hi) = PairRM(v);
                Mov(m, lo);
                Mov(Displaced(m, 4), hi);
                break;
            }
            default:
                Error($"store of size {i.Size}");
                break;
        }
    }

    private bool SelectFrameSet(Instr i)
    {
        if (SelectMmxFrameZero(i)) return true;
        if (i.Operands[2] is not ImmOperand count || count.Value < 0 || count.Value > 32
            || i.Operands[1] is not ImmOperand fill) return false;
        if (count.Value == 0) return true;
        Operand address = i.Operands[0];
        for (int depth = 0; depth < 8 && address is RegOperand r; depth++)
        {
            if (!_definitions.TryGetValue(r.Reg, out Instr? definition) || definition is null
                || definition.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) return false;
            address = definition.Operands[0];
        }
        // Restrict widened stores to known frame storage, never arbitrary
        // pointers or device memory. Frame slot alignment also avoids AC faults.
        if (address is not SlotOperand slot || slot.Slot.Align < 4 || count.Value > slot.Slot.Bytes) return false;
        int start = _m.Frame.SlotOffset(slot.Slot);
        int length = (int)count.Value;
        uint repeated = unchecked((uint)(byte)fill.Value * 0x01010101u);
        int offset = 0;
        while (offset + 4 <= length)
        {
            EmitW(MOp.Mov, 4, MMem.Frame(start + offset), Imm(unchecked((int)repeated)));
            offset += 4;
        }
        if (offset + 2 <= length)
        {
            EmitW(MOp.Mov, 2, MMem.Frame(start + offset), Imm(repeated & 65535));
            offset += 2;
        }
        if (offset < length) EmitW(MOp.Mov, 1, MMem.Frame(start + offset), Imm(repeated & 255));
        return true;
    }

    // WHOLE WORDS, like MemCopy beside it. This wrote one byte a time --
    // rep stosb for every length, however long -- while a copy of the same
    // bytes went four at a time. On a Pentium 233 that made CLEARING a page
    // 185 microseconds against 47 to COPY one: four times the cost for half
    // the memory traffic, on the path of every anonymous page fault, every
    // new page table and every frame the allocator hands out. No emulator
    // shows it; the board was asked and said so.
    private void SelectMemSet(Instr i)
    {
        if (i.Operands[2] is not ImmOperand && _streamFill is { } fill) StreamedOr(i, fill, () => InlineMemSet(i));
        else InlineMemSet(i);
    }

    /// <summary>
    /// A LONG BLOCK THE RUNTIME'S WAY: `inline`, the REP form, where the
    /// length is under StreamBytes, and a call of `routine` (the destination,
    /// the source or the fill value, the length: cdecl, as Runtime.StreamCopy
    /// and StreamFill take them) where it is not. Here, past every pass that
    /// reads the MemCopy or the MemSet as what it is (escape, regions, the
    /// fields' rules), and not in the IR, where it would be a call to them.
    /// </summary>
    private void StreamedOr(Instr i, string routine, Action inline)
    {
        MReg n = Temp();
        Mov(n, RM(i.Operands[2]));
        Emit(MOp.Cmp, n, Imm(StreamBytes));
        MBlock big = Aside();
        MBlock done = _m.NewBlock($"{_cur.Name}.{_splits++}", big);
        Jcc(Cond.Ae, big);
        inline();
        Jmp(done);
        _cur = big;
        Emit(MOp.Push, n);
        Emit(MOp.Push, RM(i.Operands[1]));
        Emit(MOp.Push, RM(i.Operands[0]));
        CallSymbol(routine);
        Emit(MOp.Add, Esp, Imm(12));
        Jmp(done);
        _cur = done;
    }

    private void InlineMemSet(Instr i)
    {
        Mov(Edi, RM(i.Operands[0]));

        // The fill byte in all four lanes, so the words below carry it.
        if (i.Operands[1] is ImmOperand fill)
        {
            uint repeated = unchecked((uint)(byte)fill.Value * 0x01010101u);
            Mov(Eax, Imm(unchecked((int)repeated)));
        }
        else
        {
            Mov(Eax, RM(i.Operands[1]));
            Emit(MOp.And, Eax, Imm(0xFF));
            Emit(MOp.Imul3, Eax, Eax, Imm(unchecked((int)0x01010101u)));
        }

        if (i.Operands[2] is ImmOperand n)
        {
            if (n.Value / 4 > 0)
            {
                Mov(Ecx, Imm(n.Value / 4));
                Emit(MOp.RepStosd);
            }
            if (n.Value % 4 > 0)
            {
                Mov(Ecx, Imm(n.Value % 4));
                Emit(MOp.RepStosb);
            }
            return;
        }

        Mov(Ecx, RM(i.Operands[2]));
        // REP consumes ECX, so the remainder is kept before the words go.
        MReg remainder = Temp();
        Mov(remainder, Ecx);
        Emit(MOp.Shr, Ecx, Imm(2));
        Emit(MOp.RepStosd);
        Mov(Ecx, remainder);
        Emit(MOp.And, Ecx, Imm(3));
        Emit(MOp.RepStosb);
    }

    private void SelectMemCopy(Instr i)
    {
        if (SelectMmxFrameCopy(i)) return;
        if (SelectSmallCopy(i)) return;
        if (i.Operands[2] is not ImmOperand && _streamCopy is { } copy) StreamedOr(i, copy, () => InlineMemCopy(i));
        else InlineMemCopy(i);
    }

    private void InlineMemCopy(Instr i)
    {
        Mov(Edi, RM(i.Operands[0]));
        Mov(Esi, RM(i.Operands[1]));
        if (i.Operands[2] is ImmOperand n)
        {
            // A constant length: whole words with movsd, the remainder with movsb.
            if (n.Value / 4 > 0)
            {
                Mov(Ecx, Imm(n.Value / 4));
                Emit(MOp.RepMovsd);
            }
            if (n.Value % 4 > 0)
            {
                Mov(Ecx, Imm(n.Value % 4));
                Emit(MOp.RepMovsb);
            }
            return;
        }
        Mov(Ecx, RM(i.Operands[2]));
        // Runtime-sized copies (including heap reallocation) should move whole
        // words rather than dispatching every byte separately on legacy x86.
        // Retain the original count across REP, which consumes ECX.
        MReg remainder = Temp();
        Mov(remainder, Ecx);
        Emit(MOp.Shr, Ecx, Imm(2));
        Emit(MOp.RepMovsd);
        Mov(Ecx, remainder);
        Emit(MOp.And, Ecx, Imm(3));
        Emit(MOp.RepMovsb);
    }

    // A SMALL CONSTANT COPY IS WORD MOVES, not REP MOVSD: a struct of two or
    // three words (an enumerator, a tuple, a span) is copied at every foreach
    // and every pass by value, and REP's start-up costs more than the copy --
    // the hottest instruction of the native compiler's escape analysis was
    // one. In ascending order a word at a time, as MOVSD goes, so an
    // overlapping copy reads what it read before.
    private const int SmallCopyBytes = 32;

    private static bool Addressable(Operand o) => o is RegOperand { Type: IrType.I32 } or SymOperand or SlotOperand;

    private bool SelectSmallCopy(Instr i)
    {
        if (i.Operands[2] is not ImmOperand { Value: > 0 and <= SmallCopyBytes } n
            || !Addressable(i.Operands[0]) || !Addressable(i.Operands[1])) return false;
        MMem destination = Address(i.Operands[0], 0), source = Address(i.Operands[1], 0);
        int offset = 0;
        for (; offset + 4 <= n.Value; offset += 4)
        {
            MReg word = Temp();
            Mov(word, Displaced(source, offset));
            Mov(Displaced(destination, offset), word);
        }
        for (; offset < n.Value; offset++)
        {
            // Stored a byte: one of the four with a low byte (ByteRegs).
            MReg tail = Temp();
            _m.ByteRegs.Add(tail.Id);
            EmitW(MOp.Movzx, 1, tail, Displaced(source, offset));
            EmitW(MOp.Mov, 1, Displaced(destination, offset), tail);
        }
        return true;
    }

    private void SelectAtomic(Instr i)
    {
        VReg d = i.Dest!;
        if (d.Type != IrType.I32)
        {
            Error("64-bit atomics are not available on a 486");
            return;
        }
        MMem m = Address(i.Operands[0], 0);
        switch (i.Op)
        {
            case Opcode.AtomicSwap:
                // xchg with a memory operand locks the bus by itself.
                Mov(Lo(d), RM(i.Operands[1]));
                Emit(MOp.Xchg, m, Lo(d));
                break;
            case Opcode.AtomicAdd:
                Mov(Lo(d), RM(i.Operands[1]));
                Emit(new MInstr(MOp.Xadd, m, Lo(d)) { Lock = true });
                break;
            case Opcode.AtomicCas:
            {
                MReg value = R(i.Operands[2]);
                Mov(Eax, RM(i.Operands[1]));
                Emit(new MInstr(MOp.Cmpxchg, m, value) { Lock = true });
                Mov(Lo(d), Eax);
                break;
            }
            default:
            {
                // No single locked instruction returns the old value of a
                // bitwise op: read, compute, and cmpxchg until it sticks.
                MOp op = i.Op == Opcode.AtomicAnd ? MOp.And : i.Op == Opcode.AtomicOr ? MOp.Or : MOp.Xor;
                MOperand v = RM(i.Operands[1]);
                Mov(Eax, m);
                MBlock loop = Split();
                MReg t = Temp();
                Mov(t, Eax);
                Emit(op, t, v);
                Emit(new MInstr(MOp.Cmpxchg, m, t) { Lock = true });
                Jcc(Cond.Ne, loop);
                Split();
                Mov(Lo(d), Eax);
                break;
            }
        }
    }

    // ---- calls ----------------------------------------------------------------------

    /// <summary>Push one argument; returns the bytes it took.</summary>
    private int PushArg(Operand o)
    {
        switch (o.Type)
        {
            case IrType.I64:
            {
                (MOperand lo, MOperand hi) = PairRM(o);
                Emit(MOp.Push, hi);
                Emit(MOp.Push, lo);
                return 8;
            }
            case IrType.F64:
            {
                MMem home = FHome(o);
                Emit(MOp.Push, new MMem(home.Base, home.Disp + 4));
                Emit(MOp.Push, home);
                return 8;
            }
            case IrType.F32:
                Emit(MOp.Push, FHome(o));
                return 4;
            default:
                Emit(MOp.Push, RM(o));
                return 4;
        }
    }

    private void SelectCall(Instr i)
    {
        VReg? d = i.Dest;
        if (i.Op == Opcode.Call && i.Callee == "__exception")
        {
            // The pseudo-call at the head of a landing pad: the unwinder
            // arrived with the exception in EAX and nothing else defined.
            if (d is not null)
            {
                Mov(Lo(d), Eax);
            }
            return;
        }

        if (_positionIndependent && i.Op == Opcode.Call && i.Callee is MachineIntrinsics.CardMark or MachineIntrinsics.Barrier)
        {
            Instr routine = new()
            {
                Op = Opcode.Call, Line = i.Line,
                Callee = i.Callee == MachineIntrinsics.CardMark ? Corsac.Lang.Lto.RuntimeAbi.CardMark : Corsac.Lang.Lto.RuntimeAbi.WriteBarrier,
            };
            routine.Operands.AddRange(i.Operands);
            SelectCall(routine);
            return;
        }

        if (i.Op == Opcode.Call && i.Callee is { } callee && callee.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal))
        {
            SelectMachineIntrinsic(callee, i);
            return;
        }

        int first = i.Op == Opcode.CallIndirect ? 1 : 0;

        // A CALL INTO C. GCC's code for i386 System V may assume the stack
        // sixteen-byte aligned at a call (it keeps SSE spills there), and
        // this backend's own calls keep only four. So ESP is kept, aligned
        // down, and padded so the arguments end on the boundary; the
        // arguments themselves are all reached through EBP or registers,
        // never through ESP, so moving it under them is safe.
        bool native = NativeCall.Is(i);
        MReg? savedEsp = null;
        MReg? nativeTarget = null;
        if (native)
        {
            int argumentBytes = 0;
            for (int k = first; k < i.Operands.Count; k++)
            {
                argumentBytes += i.Operands[k].Type is IrType.I64 or IrType.F64 ? 8 : 4;
            }
            if (i.Op == Opcode.CallIndirect)
            {
                nativeTarget = R(i.Operands[0]);
            }
            savedEsp = _m.NewReg();
            Mov(savedEsp, Esp);
            Emit(MOp.And, Esp, Imm(-16));
            int pad = (16 - argumentBytes % 16) % 16;
            if (pad != 0)
            {
                Emit(MOp.Sub, Esp, Imm(pad));
            }
        }

        int bytes = 0;
        for (int k = i.Operands.Count - 1; k >= first; k--)
        {
            bytes += PushArg(i.Operands[k]);
        }
        if (i.Op == Opcode.Call)
        {
            Emit(new MInstr(MOp.Call, SymbolAddress(NativeCall.Symbol(i.Callee!), 0)) { CallReloc = RelocKind.Rel32, Native = native });
        }
        else
        {
            Emit(new MInstr(MOp.CallInd, nativeTarget ?? R(i.Operands[0])) { Native = native });
        }
        if (savedEsp is not null)
        {
            Mov(Esp, savedEsp);
        }
        else if (bytes > 0)
        {
            Emit(MOp.Add, Esp, Imm(bytes));
        }
        if (d is null)
        {
            return;
        }
        switch (d.Type)
        {
            case IrType.I32:
                Mov(Lo(d), Eax);
                break;
            case IrType.I64:
                Mov(Lo(d), Eax);
                Mov(Hi(d), Edx);
                break;
            default:
                Fstp(d);
                break;
        }
    }

    private void SelectRet(Instr i)
    {
        if (i.Operands.Count > 0)
        {
            Operand v = i.Operands[0];
            switch (v.Type)
            {
                case IrType.I32:
                    Mov(Eax, RM(v));
                    break;
                case IrType.I64:
                {
                    (MOperand lo, MOperand hi) = PairRM(v);
                    Mov(Eax, lo);
                    Mov(Edx, hi);
                    break;
                }
                default:
                    Fld(v);
                    break;
            }
        }
        // The width says which of EAX and EDX carry the value out (see Roles.ImplicitUses).
        int width = i.Operands.Count == 0 ? 0 : i.Operands[0].Type switch { IrType.I32 => 4, IrType.I64 => 8, _ => 0 };
        Emit(new MInstr(MOp.Epilogue) { Width = width });
    }

    // ---- control ---------------------------------------------------------------------

    private void SelectBranch(Instr i)
    {
        MBlock t = _heads[i.Targets[0]];
        MBlock f = _heads[i.Targets[1]];
        Operand c = i.Operands[0];
        // A KNOWN CONDITION IS TESTED AT ITS OWN WIDTH, as the TEST below
        // would: an I32 whose long carries junk above bit 31 is only its
        // low half (a word here is an I32).
        if (c is ImmOperand imm)
        {
            Jmp(Corsac.Lang.Opt.IrInfo.Normalise(imm.Value, c.Type) != 0 ? t : f);
            return;
        }
        if (c is RegOperand cr && _constants.TryGetValue(cr.Reg.Id, out long known))
        {
            Jmp(Corsac.Lang.Opt.IrInfo.Normalise(known, c.Type) != 0 ? t : f);
            return;
        }
        MReg r = R(c);
        Emit(MOp.Test, r, r);
        Jcc(Cond.Ne, t);
        Jmp(f);
    }

    private void SelectFusedBranch(Instr cmp, Instr br)
    {
        MBlock t = _heads[br.Targets[0]];
        MBlock f = _heads[br.Targets[1]];
        if (cmp.Op is >= Opcode.FEq and <= Opcode.FGe)
        {
            FloatCond fc = FloatCompare(cmp);
            if (fc.ParityFalse)
            {
                Jcc(Cond.P, f);
            }
            else if (fc.ParityTrue)
            {
                Jcc(Cond.P, t);
            }
            Jcc(fc.Cond, t);
            Jmp(f);
            return;
        }
        Cond c = IntCompare(cmp);
        Jcc(c, t);
        Jmp(f);
    }

    private void SelectSwitch(Instr i)
    {
        MReg idx = R(i.Operands[0]);
        Emit(MOp.Cmp, idx, Imm(i.Targets.Count));
        Jcc(Cond.Ae, _heads[i.Default!]);
        List<MBlock> table = i.Targets.Select(b => _heads[b]).ToList();
        Emit(new MInstr(MOp.JmpTable, idx) { Table = table });
    }

    private void SelectUnwind(Instr i)
    {
        // Once ESP and EBP change, no frame-relative operand is valid, so
        // everything is in fixed registers before the first of them moves.
        Mov(Ecx, RM(i.Operands[0]));
        Mov(Eax, RM(i.Operands[1]));
        Mov(Esp, new MMem(Ecx, 8));
        Mov(Ebp, new MMem(Ecx, 12));
        Emit(MOp.JmpInd, new MMem(Ecx, 4));
    }

    // ---- the instructions only a driver or a kernel may execute -----------------------

    /// <summary>
    /// A machine instruction the language offers as a `Sys.*` intrinsic and
    /// that no sequence of ordinary IR can express: port I/O, the interrupt
    /// flag, the descriptor-table registers, the control registers.
    ///
    /// Lowering emits these as calls to reserved names rather than as new IR
    /// opcodes so that every pass between lowering and here treats them as
    /// what they are -- something with effects, which may not be moved,
    /// duplicated or deleted -- without having to be taught one by one. The
    /// call is never emitted: it is replaced, here, by the instruction.
    ///
    /// Operands go to the registers the hardware fixes (the port in DX, the
    /// datum in AL/AX/EAX, the string forms' ESI/EDI/ECX) and the allocator
    /// learns the dependency from Roles.ImplicitUses, exactly as it does for
    /// the system-call trap.
    /// </summary>
    private void ByteSwapWord(MReg value)
    {
        if (Target.Current.X86Profile.Has(global::Corsac.Lang.X86.X86Feature.I486)) { Emit(MOp.Bswap, value); return; }
        // 386 has no BSWAP: exchange adjacent bytes, then exchange 16-bit halves.
        // General-register temporaries avoid constraining allocation to AL/AH.
        MReg other = Temp(); Mov(other, value);
        Emit(MOp.Shl, value, Imm(8)); Emit(MOp.And, value, Imm(unchecked((int)0xff00ff00)));
        Emit(MOp.Shr, other, Imm(8)); Emit(MOp.And, other, Imm(0x00ff00ff));
        Emit(MOp.Or, value, other); Emit(MOp.Ror, value, Imm(16));
    }

    private void SelectMachineIntrinsic(string name, Instr i)
    {
        if (name.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal))
        {
            SelectPackedOp(name, i);
            return;
        }
        switch (name)
        {
            case MachineIntrinsics.In8:
            case MachineIntrinsics.In16:
            case MachineIntrinsics.In32:
            {
                int w = name == MachineIntrinsics.In8 ? 1 : name == MachineIntrinsics.In16 ? 2 : 4;
                Mov(Edx, RM(i.Operands[0]));
                // The narrow forms write only AL or AX, so the rest of EAX is
                // whatever was there: zero it first and the result is the
                // canonical unsigned byte or word the language promises.
                if (w != 4)
                {
                    Emit(MOp.Xor, Eax, Eax);
                }
                EmitW(MOp.In, w);
                // A read whose value is dropped -- the 400 ns delay of
                // reading a status port -- has no destination to move to.
                if (i.Dest is not null)
                {
                    Mov(Lo(i.Dest), Eax);
                }
                return;
            }
            case MachineIntrinsics.Out8:
            case MachineIntrinsics.Out16:
            case MachineIntrinsics.Out32:
            {
                int w = name == MachineIntrinsics.Out8 ? 1 : name == MachineIntrinsics.Out16 ? 2 : 4;
                Mov(Edx, RM(i.Operands[0]));
                Mov(Eax, RM(i.Operands[1]));
                EmitW(MOp.Out, w);
                return;
            }
            case MachineIntrinsics.InString16:
            case MachineIntrinsics.OutString16:
            {
                bool read = name == MachineIntrinsics.InString16;
                Mov(Edx, RM(i.Operands[0]));
                Mov(read ? Edi : Esi, RM(i.Operands[1]));
                Mov(Ecx, RM(i.Operands[2]));
                Emit(read ? MOp.RepInsw : MOp.RepOutsw);
                return;
            }
            case MachineIntrinsics.Cli:
                Emit(MOp.Cli);
                return;
            case MachineIntrinsics.Sti:
                Emit(MOp.Sti);
                return;
            case MachineIntrinsics.Hlt:
                Emit(MOp.Hlt);
                return;
            case MachineIntrinsics.Lidt:
            case MachineIntrinsics.Lgdt:
            case MachineIntrinsics.Invlpg:
            {
                MReg addr = R(i.Operands[0]);
                MOp op = name == MachineIntrinsics.Lidt ? MOp.Lidt
                       : name == MachineIntrinsics.Lgdt ? MOp.Lgdt
                       : MOp.Invlpg;
                // INVLPG is the 486's, and a kernel that also runs on a 386
                // asks which it is on and reloads CR3 there instead: named
                // by the source, it is Probed as the Pentium's four below.
                Emit(new MInstr(op, new MMem(addr, 0)) { Probed = op == MOp.Invlpg });
                return;
            }
            // NAMED BY THE SOURCE, and so Probed, as INVLPG is: CR4 is the
            // Pentium's (Isa.Requires), and a kernel that also runs on a 486
            // asks which it is on before it reads or writes it.
            case MachineIntrinsics.ReadCr:
                // INTO A TEMPORARY WHERE NOTHING READS IT, as x86-64's: a read
                // whose value was stored to a static nothing reads loses its
                // destination with the store, and keeps its probe.
                Emit(new MInstr(MOp.MovFromCr, i.Dest is not null ? Lo(i.Dest) : Temp(), ControlRegister(i.Operands[0])) { Probed = true });
                return;
            case MachineIntrinsics.WriteCr:
                Emit(new MInstr(MOp.MovToCr, R(i.Operands[1]), ControlRegister(i.Operands[0])) { Probed = true });
                return;
            case MachineIntrinsics.CardMark:
                // The slot's card, set by the object's stub, which keeps
                // every register (X86Backend.CardStub): seven bytes a store
                // where the load, test, shift and store took forty.
                Mov(Eax, R(i.Operands[0]));
                Emit(MOp.CallKeep, MImm.Sym(X86Backend.CardStub, 0), Eax);
                _m.UsesCardStub = true;
                return;
            case MachineIntrinsics.Barrier:
                Mov(Eax, R(i.Operands[0]));
                Mov(Edx, R(i.Operands[1]));
                Emit(MOp.CallKeep, MImm.Sym(X86Backend.BarrierStub, 0), Eax, Edx);
                _m.UsesBarrierStub = true;
                return;
            case MachineIntrinsics.RefStore:
            case MachineIntrinsics.CardStore:
            {
                // A REFERENCE STORED IN ONE SEQUENCE (CardMarks.FuseStores):
                // the slot in EAX, the value in EDX, every register kept, by
                // the image's stub -- the kernel's, from a module, read out of
                // the GOT, for it is the kernel's range of them that its ring 0
                // will not stop a thread inside (X86Backend.RefStoreStub).
                string stub = name == MachineIntrinsics.RefStore ? X86Backend.RefStoreStub : X86Backend.CardStoreStub;
                MReg? target = null;
                if (_positionIndependent)
                {
                    target = Temp();
                    Mov(target, SymbolAddress(stub, 0));
                }
                Mov(Eax, R(i.Operands[0]));
                Mov(Edx, R(i.Operands[1]));
                if (target is null) Emit(MOp.CallKeep, MImm.Sym(stub, 0), Eax, Edx);
                else Emit(MOp.CallKeepInd, target, Eax, Edx);
                return;
            }
            case MachineIntrinsics.RefExchange:
            case MachineIntrinsics.RefCompareExchange:
            {
                // A REFERENCE EXCHANGED AS A SEQUENCE (Sys.ExchangeReference):
                // xchg's stub takes the slot in EAX and the value in EDX, the
                // compare-exchange's the slot in ECX, the value in EDX and the
                // expected one in EAX -- CMPXCHG's own -- and both answer in
                // EAX, every other register kept.
                bool swap = name == MachineIntrinsics.RefExchange;
                string stub = swap ? X86Backend.RefExchangeStub : X86Backend.RefCompareExchangeStub;
                MReg? target = null;
                if (_positionIndependent)
                {
                    target = Temp();
                    Mov(target, SymbolAddress(stub, 0));
                }
                if (swap)
                {
                    Mov(Eax, R(i.Operands[0]));
                    Mov(Edx, R(i.Operands[1]));
                    if (target is null) Emit(MOp.CallKeepEax, MImm.Sym(stub, 0), Eax, Edx);
                    else Emit(MOp.CallKeepEaxInd, target, Eax, Edx);
                }
                else
                {
                    Mov(Ecx, R(i.Operands[0]));
                    Mov(Edx, R(i.Operands[2]));
                    Mov(Eax, R(i.Operands[1]));
                    if (target is null) Emit(MOp.CallKeepEax, MImm.Sym(stub, 0), Eax, Ecx, Edx);
                    else Emit(MOp.CallKeepEaxInd, target, Eax, Ecx, Edx);
                }
                if (i.Dest is not null) Mov(Lo(i.Dest), Eax);
                return;
            }
            case MachineIntrinsics.ThreadBlock:
                // The self pointer at gs:[0]. A machine intrinsic and not an
                // ordinary load because nothing before here knows that GS is
                // not DS, and a pass that hoisted it above a SetGs would read
                // another thread's block.
                if (i.Dest is not null && _windowsTls)
                {
                    // WINDOWS: user code has no GS, and FS is the thread's
                    // TEB. The block is in the TLS slot the runtime took
                    // (TlsAlloc), whose number is a word of the image.
                    MReg r = Lo(i.Dest);
                    Emit(MOp.Mov, r, SymbolMem(TlsBaseSymbol, 0));
                    Emit(MOp.FsLoad, r);
                    Emit(MOp.Add, r, SymbolMem(TlsOffsetSymbol, 0));
                    Emit(MOp.Mov, r, new MMem(r, 0));
                }
                else if (i.Dest is not null)
                {
                    Emit(MOp.GsSelf, Lo(i.Dest));
                }
                return;
            // THE PENTIUM'S FOUR, NAMED BY THE SOURCE: a kernel reaches each
            // only after asking the processor (CPUID's own presence is the
            // EFLAGS.ID test), so each is Probed and the emitted-code audit
            // leaves it to that check rather than to --cpu.
            case MachineIntrinsics.ReadMsr:
            case MachineIntrinsics.ReadTsc:
                if (name == MachineIntrinsics.ReadMsr)
                {
                    Mov(Ecx, R(i.Operands[0]));
                    Emit(new MInstr(MOp.Rdmsr) { Probed = true });
                }
                else
                {
                    Emit(new MInstr(MOp.Rdtsc) { Probed = true });
                }
                if (i.Dest is not null)
                {
                    Mov(Lo(i.Dest), Eax);
                    Mov(Hi(i.Dest), Edx);
                }
                return;
            case MachineIntrinsics.WriteMsr:
            {
                MReg msr = R(i.Operands[0]);
                (MOperand lo, MOperand hi) = PairRM(i.Operands[1]);
                Mov(Eax, lo);
                Mov(Edx, hi);
                Mov(Ecx, msr);
                Emit(new MInstr(MOp.Wrmsr) { Probed = true });
                return;
            }
            case MachineIntrinsics.Cpuid:
            {
                MReg leaf = R(i.Operands[0]);
                MReg subleaf = R(i.Operands[1]);
                MReg into = R(i.Operands[2]);
                MReg keep = _m.NewReg();
                Mov(keep, into);
                Mov(Eax, leaf);
                Mov(Ecx, subleaf);
                Emit(new MInstr(MOp.Cpuid) { Probed = true });
                MReg a = _m.NewReg(), b = _m.NewReg(), c = _m.NewReg(), d = _m.NewReg();
                Mov(a, Eax);
                Mov(b, MReg.Of(Gpr.Ebx));
                Mov(c, Ecx);
                Mov(d, Edx);
                Mov(new MMem(keep, 0), a);
                Mov(new MMem(keep, 4), b);
                Mov(new MMem(keep, 8), c);
                Mov(new MMem(keep, 12), d);
                return;
            }
            case MachineIntrinsics.LoadTaskRegister:
                Emit(MOp.Ltr, R(i.Operands[0]));
                return;
            case MachineIntrinsics.LoadCodeSegment:
                Emit(MOp.LoadCs, R(i.Operands[0]));
                return;
            case MachineIntrinsics.SwapGs:
                Error("swapgs is a long-mode instruction; i386 has no GS base to swap");
                return;
            case MachineIntrinsics.LoaderFini:
                // Unused when no C library is started: nothing to read.
                if (i.Dest is not null)
                {
                    Mov(Lo(i.Dest), Edx);
                }
                return;
            case MachineIntrinsics.DoublePrecision:
            {
                if (Target.Current.X86Profile.Fpu == "none")
                {
                    return;
                }
                // PC, bits 8 and 9 of the control word, to 10: 53 bits.
                int s = _m.Frame.Scratch;
                EmitW(MOp.Fnstcw, 2, MMem.Frame(s));
                MReg cw = Temp();
                EmitW(MOp.Movzx, 2, cw, MMem.Frame(s));
                Emit(MOp.And, cw, Imm(~0x300));
                Emit(MOp.Or, cw, Imm(0x200));
                EmitW(MOp.Mov, 2, MMem.Frame(s), cw);
                EmitW(MOp.Fldcw, 2, MMem.Frame(s));
                return;
            }
            case MachineIntrinsics.KeepAlive:
            {
                MReg kept = R(i.Operands[0]);
                Emit(MOp.Test, kept, kept);
                return;
            }
            case MachineIntrinsics.GetGs:
                // NOTHING WHERE NOTHING READS IT: `Runtime.NativeGs =
                // Sys.GetGs();` in a program that never reads NativeGs lost
                // the store and kept the read, with no register to read into.
                if (i.Dest is not null) Emit(MOp.GetGs, Lo(i.Dest));
                return;
            case MachineIntrinsics.ReadFlags:
                // pushfd; pop r: no call to a stub for the one bit a lock or a
                // gate asks before it turns interrupts off.
                if (i.Dest is not null) Emit(MOp.ReadFlags, Lo(i.Dest));
                return;
            // THE PACKED ROUTINES' INSTRUCTIONS, each Probed: the runtime
            // reaches them only after Processor said the processor has them.
            // MM0 and MM1 only, nothing left in them from one to the next.
            case MachineIntrinsics.StreamPrefetch:
            case MachineIntrinsics.StreamPrefetchLine:
                Emit(new MInstr(name == MachineIntrinsics.StreamPrefetch ? MOp.PrefetchNta : MOp.PrefetchLine, new MMem(R(i.Operands[0]), 0)) { Probed = true });
                return;
            case MachineIntrinsics.StreamCopy64:
            case MachineIntrinsics.StreamFill64:
            {
                // A cache line: eight movq loads (one, of the pattern, for a
                // fill) and eight movntq stores through MM0.
                MReg to = R(i.Operands[0]), from = R(i.Operands[1]);
                bool fill = name == MachineIntrinsics.StreamFill64;
                if (fill) Emit(new MInstr(MOp.MmxLoad, new MMem(from, 0)) { Probed = true });
                for (int k = 0; k < 64; k += 8)
                {
                    if (!fill) Emit(new MInstr(MOp.MmxLoad, new MMem(from, k)) { Probed = true });
                    Emit(new MInstr(MOp.MmxStoreNt, new MMem(to, k)) { Probed = true });
                }
                return;
            }
            case MachineIntrinsics.StreamEnd:
                Emit(new MInstr(MOp.Sfence) { Probed = true });
                Emit(new MInstr(MOp.Emms) { Probed = true });
                return;
            case MachineIntrinsics.PackedSaveX87:
            case MachineIntrinsics.PackedRestoreX87:
            case MachineIntrinsics.PackedSaveFxsr:
            case MachineIntrinsics.PackedRestoreFxsr:
            {
                // The area's address in EAX, which the sequences name;
                // FXSAVE wants it at sixteen (Lowering: 16 bytes
                // more than the 528 it fills). The selection inside the block
                // from here on, or back in the one around it.
                bool fx = name is MachineIntrinsics.PackedSaveFxsr or MachineIntrinsics.PackedRestoreFxsr;
                Mov(Eax, R(i.Operands[0]));
                if (fx)
                {
                    Emit(MOp.Add, Eax, Imm(15));
                    Emit(MOp.And, Eax, Imm(-16));
                }
                // The save and its give-back are the block's own: FXSAVE is
                // SSE's state, which the audit lets only a state FXSAVE keeps touch.
                Emit(new MInstr(name switch
                {
                    MachineIntrinsics.PackedSaveX87 => MOp.PackedSaveX87,
                    MachineIntrinsics.PackedRestoreX87 => MOp.PackedRestoreX87,
                    MachineIntrinsics.PackedSaveFxsr => MOp.PackedSaveFxsr,
                    _ => MOp.PackedRestoreFxsr,
                }) { OwnsState = (byte)(fx ? 2 : 1), Probed = fx });
                _packedState = name switch
                {
                    MachineIntrinsics.PackedSaveX87 => _packedState << 2 | 1,
                    MachineIntrinsics.PackedSaveFxsr => _packedState << 2 | 2,
                    _ => _packedState >> 2,
                };
                return;
            }
            case MachineIntrinsics.PackedEnd:
            case MachineIntrinsics.PackedEndFast:
                Emit(new MInstr(name == MachineIntrinsics.PackedEnd ? MOp.Emms : MOp.Femms) { Probed = true });
                return;
            case MachineIntrinsics.PackedFindChar4:
            case MachineIntrinsics.PackedFindChar8:
                if (i.Dest is not null)
                    Emit(new MInstr(name == MachineIntrinsics.PackedFindChar4 ? MOp.MmxFindW4 : MOp.MmxFindW8, Lo(i.Dest),
                        new MMem(R(i.Operands[0]), 0), new MMem(R(i.Operands[1]), 0)) { Probed = true });
                return;
            case MachineIntrinsics.PackedEqual8:
                if (i.Dest is not null)
                    Emit(new MInstr(MOp.MmxEqual8, Lo(i.Dest), new MMem(R(i.Operands[0]), 0), new MMem(R(i.Operands[1]), 0)) { Probed = true });
                return;
            case MachineIntrinsics.BitScanForward:
            case MachineIntrinsics.BitScanReverse:
                // One instruction from the 386 on; the source is never zero
                // (its caller asks first), where the result is undefined.
                if (i.Dest is not null) Emit(name == MachineIntrinsics.BitScanForward ? MOp.Bsf : MOp.Bsr, Lo(i.Dest), R(i.Operands[0]));
                return;
            case MachineIntrinsics.CpuidEax:
            case MachineIntrinsics.CpuidEdx:
            {
                // One register of the leaf, straight out of the instruction:
                // nothing stored, so the asker needs no memory to ask with.
                MReg leaf = R(i.Operands[0]);
                Mov(Eax, leaf);
                Mov(Ecx, Imm(0));
                Emit(new MInstr(MOp.Cpuid) { Probed = true });
                if (i.Dest is not null) Mov(Lo(i.Dest), name == MachineIntrinsics.CpuidEax ? Eax : Edx);
                return;
            }
            case MachineIntrinsics.CpuidPresent:
            {
                // WHETHER CPUID IS THERE: EFLAGS.ID (bit 21) can be changed
                // only on a processor that has it -- every Pentium, and the
                // later 486s. Flipped, read back, and put as it was.
                if (i.Dest is null) return;
                MReg flags = Temp(), was = Temp();
                Emit(MOp.ReadFlags, flags);
                Mov(was, flags);
                Emit(MOp.Xor, flags, Imm(1 << 21));
                Emit(MOp.WriteFlags, flags);
                Emit(MOp.ReadFlags, flags);
                Emit(MOp.WriteFlags, was);
                Emit(MOp.Xor, flags, was);
                Emit(MOp.Shr, flags, Imm(21));
                Emit(MOp.And, flags, Imm(1));
                Mov(Lo(i.Dest), flags);
                return;
            }
            case MachineIntrinsics.Cas64:
            {
                // LOCK CMPXCHG8B: the Pentium's, reached only after its CPUID
                // bit was seen (Processor.Cas64), so Probed. Every register it
                // names is a fixed one -- the address in ESI too -- so none is
                // left for the allocator to find beside the four it takes:
                // with the address in a register of its own and a CAS loop's
                // guess live across it, i386's six ran out.
                (MOperand expectLo, MOperand expectHi) = PairRM(i.Operands[1]);
                (MOperand valueLo, MOperand valueHi) = PairRM(i.Operands[2]);
                Mov(Esi, R(i.Operands[0]));
                Mov(Eax, expectLo);
                Mov(Edx, expectHi);
                Mov(Ebx, valueLo);
                Mov(Ecx, valueHi);
                Emit(new MInstr(MOp.Cmpxchg8b, new MMem(Esi, 0)) { Lock = true, Probed = true, Width = 8 });
                if (i.Dest is not null)
                {
                    Mov(Lo(i.Dest), Eax);
                    Mov(Hi(i.Dest), Edx);
                }
                return;
            }
            case MachineIntrinsics.ReadCodeSegment:
                if (i.Dest is not null) Emit(MOp.ReadCs, Lo(i.Dest));
                return;
            case MachineIntrinsics.SetGs:
                Emit(MOp.SetGs, R(i.Operands[0]));
                return;
            case MachineIntrinsics.PerCpuRead:
                if (i.Dest is null) return;
                if (i.Operands[0] is ImmOperand { Value: var readAt }) Emit(MOp.GsLoadAt, Lo(i.Dest), new MImm(readAt));
                else Emit(MOp.GsLoadReg, Lo(i.Dest), R(i.Operands[0]));
                return;
            case MachineIntrinsics.PerCpuWrite:
                if (i.Operands[0] is ImmOperand { Value: var writeAt }) Emit(MOp.GsStoreAt, new MImm(writeAt), R(i.Operands[1]));
                else Emit(MOp.GsStoreReg, R(i.Operands[0]), R(i.Operands[1]));
                return;
            case MachineIntrinsics.SetTlsBlock:
                // WINDOWS: the block into the runtime's TLS slot, found as
                // ThreadBlock finds it (TlsBaseSymbol).
                Mov(Edx, R(i.Operands[0]));
                Emit(MOp.Mov, Ecx, SymbolMem(TlsBaseSymbol, 0));
                Emit(MOp.FsLoad, Ecx);
                Emit(MOp.Add, Ecx, SymbolMem(TlsOffsetSymbol, 0));
                Emit(MOp.Mov, new MMem(Ecx, 0), Edx);
                return;
            case MachineIntrinsics.SehChain:
                if (i.Dest is not null) Emit(MOp.FsSehChain, Lo(i.Dest));
                return;
            case MachineIntrinsics.SetSehChain:
                Emit(MOp.FsSehChainStore, R(i.Operands[0]));
                return;
            case MachineIntrinsics.LoadSegments:
                // DS, ES, FS, GS and SS all take the one data selector, which
                // the instruction reads from AX. CS is the far jump's business
                // and not this one's.
                Mov(Eax, RM(i.Operands[0]));
                Emit(MOp.LoadSegments);
                return;
            default:
                Error($"unknown machine intrinsic {name}");
                return;
        }
    }

    /// <summary>
    /// The control register number, which has to have been a constant: the
    /// instruction encodes it and there is no form that reads one from a
    /// register.
    /// </summary>
    private MImm ControlRegister(Operand o)
    {
        long n = o switch
        {
            ImmOperand imm => imm.Value,
            RegOperand r when _constants.TryGetValue(r.Reg.Id, out long k) => k,
            _ => -1,
        };
        if (n is < 0 or > 7)
        {
            Error("the control register number must be a constant 0 to 7");
            n = 0;
        }
        return Imm(n);
    }

    // ---- the operating system --------------------------------------------------------

    /// <summary>
    /// Linux i386: number in EAX, arguments in EBX, ECX, EDX, ESI, EDI, EBP,
    /// result in EAX. The sixth argument displaces the frame pointer for the
    /// duration of the trap. This is the only OS-specific selection.
    /// </summary>
    private void SelectSyscall(Instr i)
    {
        MReg[] regs = { Ebx, Ecx, Edx, Esi, Edi };
        int args = i.Operands.Count - 1;
        if (args > 6)
        {
            Error("syscall with more than six arguments");
            return;
        }
        Mov(Eax, RM(i.Operands[0]));
        for (int k = 0; k < Math.Min(args, 5); k++)
        {
            Mov(regs[k], RM(i.Operands[k + 1]));
        }
        bool sixth = args == 6;
        if (sixth)
        {
            // The argument is read before EBP changes: any spill reload the
            // allocator adds lands ahead of this move, while EBP still holds the frame.
            MReg a6 = R(i.Operands[6]);
            Emit(MOp.Push, Ebp);
            Mov(Ebp, a6);
        }
        // The argument count rides along, so the trap reads only those registers (Roles.ImplicitUses).
        Emit(MOp.SyscallTrap, new MImm(i.Offset == 0 ? 0x80 : i.Offset), new MImm(args));
        if (sixth)
        {
            Emit(MOp.Pop, Ebp);
        }
        Mov(Lo(i.Dest!), Eax);
    }
}

/// <summary>
/// A value for each of a function's registers, by its number (VReg.Id):
/// null until set. Sized by the function's register count and grown if a
/// number is past it, so a number nobody set reads null as a missing key did.
/// </summary>
internal sealed class ByRegister<T> where T : class
{
    private T?[] _slots;

    public ByRegister(int count) => _slots = new T?[Math.Max(count, 1)];

    public T? this[int id]
    {
        get => (uint)id < (uint)_slots.Length ? _slots[id] : null;
        set
        {
            if (id >= _slots.Length) Array.Resize(ref _slots, Math.Max(id + 1, _slots.Length * 2));
            _slots[id] = value;
        }
    }
}

/// <summary>How many times each of a function's registers is read, by its number: 0 for one never counted.</summary>
internal sealed class UseCounts
{
    private int[] _counts;

    public UseCounts(int count) => _counts = new int[Math.Max(count, 1)];

    public void Count(int id)
    {
        if (id >= _counts.Length) Array.Resize(ref _counts, Math.Max(id + 1, _counts.Length * 2));
        _counts[id]++;
    }

    public int GetValueOrDefault(int id) => (uint)id < (uint)_counts.Length ? _counts[id] : 0;
}
