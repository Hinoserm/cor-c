#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Conservative retention queries for a reference stored in an owner's field.</summary>
internal sealed class OwnedFieldEscape
{
    private readonly Dictionary<string, Function> _functions;
    private readonly Dictionary<string, bool[]> _summaries;
    private readonly Dictionary<(string, int, string, string?), bool> _memo = new();
    internal readonly record struct Field(long Offset, int Width);
    /// <summary>The instruction the last refused read stopped at (--trace-escape).</summary>
    [ThreadStatic] internal static Instr? LastRefusal;
    internal sealed class Owner
    {
        public required Corsac.Lang.Ir.Block Block { get; init; }
        public required VReg Root { get; init; }
        public required long Bytes { get; init; }
        public HashSet<VReg> Aliases { get; } = new();
        public HashSet<Instr> Stores { get; } = new();
        public List<(Owner Parent, Field Field)> Parents { get; } = new();
        /// <summary>
        /// A closure the compiler wrote: the descriptor it is stamped with and
        /// where its table begins (Escape.ClosureStamp). Its Invoke, called
        /// through a delegate it is handed as, is then a body to read.
        /// </summary>
        public (string Name, long Offset)? Stamp { get; set; }
    }
    /// <summary>
    /// A function this pass was not handed, looked up by name when a read
    /// reaches a call of it: the link's lifetime run holds one function, and
    /// the bodies it calls are the unit's archived IR (UnitBackend). Null for
    /// one it cannot find, which keeps the read refused.
    /// </summary>
    private readonly Func<string, Function?>? _resolve;
    /// <summary>
    /// What a function does to each parameter's fields (Escape.FieldSummary),
    /// for a callee no body is found for; null where none can be asked.
    /// </summary>
    private readonly Func<string, int, Escape.FieldSummary?>? _fieldsOf;
    public OwnedFieldEscape(Dictionary<string, Function> functions, Dictionary<string, bool[]> summaries, Func<string, Function?>? resolve = null,
        Func<string, int, Escape.FieldSummary?>? fieldsOf = null)
    { _functions = functions; _summaries = summaries; _resolve = resolve; _fieldsOf = fieldsOf; }

    /// <summary>
    /// Whether a callee seen only by its summaries keeps its argument nowhere
    /// and leaves the field a path of one names clean: what it reads there
    /// goes nowhere, and only fresh objects are put there.
    /// </summary>
    private bool CleanInSummary(string callee, int parameter, IReadOnlyList<Field> path)
    {
        if (_fieldsOf is null || path.Count != 1 || path[0].Width != IrTypes.Word.Bytes() || path[0].Offset % IrTypes.Word.Bytes() != 0) return false;
        if (!_summaries.TryGetValue(callee, out bool[]? keeps) || parameter >= keeps.Length || keeps[parameter]) return false;
        return _fieldsOf(callee, parameter) is { Opaque: false } summary && !summary.Dirty.Contains(path[0].Offset);
    }

    /// <summary>The function named, from those handed over or else the resolver, decoded once.</summary>
    private bool TryFunction(string name, out Function? function)
    {
        if (_functions.TryGetValue(name, out function)) return true;
        if (_resolve is null) return false;
        function = _resolve(name);
        if (function is null) return false;
        _functions[name] = function;
        return true;
    }

    /// <summary>
    /// Every repeat of the child must pass through a fresh owner lifetime.
    /// The owner may be made before the child or after it -- a List made,
    /// then the foreach enumerator that holds it -- as long as one of the two
    /// is on every path to the other.
    /// </summary>
    internal static bool OwnerRenews(Cfg cfg, Corsac.Lang.Ir.Block owner, Corsac.Lang.Ir.Block child)
    {
        if (!cfg.Dominates(owner, child) && !cfg.Dominates(child, owner)) return false;
        // The caller has already established instruction order in this case.
        if (owner == child) return true;
        HashSet<Corsac.Lang.Ir.Block> seen = new();
        Stack<Corsac.Lang.Ir.Block> pending = new(cfg.Succs(child));
        while (pending.Count != 0)
        {
            var block = pending.Pop();
            if (block == owner) continue;
            if (block == child) return false;
            if (!seen.Add(block)) continue;
            if (seen.Count > 4096) return false;
            foreach (var next in cfg.Succs(block)) pending.Push(next);
        }
        return true;
    }

    internal static Dictionary<VReg, long> Addresses(Function f, VReg root)
        => Addresses(f, new[] { root });

    internal static Dictionary<VReg, long> Addresses(Function f, IEnumerable<VReg> roots)
    {
        AddressScan scan = ReferenceEquals(_cacheFor, f) ? _cached ??= new AddressScan(f) : new AddressScan(f);
        return scan.Find(roots);
    }

    // ONE SCAN OF A FUNCTION WHILE NOTHING CHANGES IT (Escape.PromoteIn):
    // every allocation, every owner of it and every attempt asked for the
    // addresses of something, and each scanned the whole function again --
    // a library compiled as one unit sat ten minutes there. The function
    // marked by Cache is scanned once and the scan kept until Changed says
    // the function was written; any other is scanned for the one question.
    [ThreadStatic] private static Function? _cacheFor;
    [ThreadStatic] private static AddressScan? _cached;
    internal static void Cache(Function f) { _cacheFor = f; _cached = null; }
    internal static void Changed() => _cached = null;
    internal static void Uncache() { _cacheFor = null; _cached = null; }

    private sealed class AddressScan
    {
        private readonly Function _f;
        private readonly Defs _defs;
        // THE STEPS AN ADDRESS CAN TAKE: every single write that copies,
        // widens or moves a register by a constant, by the register it reads.
        // The copies into registers written more than once wait for
        // JoinedAliases, which has nothing to do unless one reads an address.
        private readonly List<Instr> _steps = new();
        private readonly bool _joins;
        private Dictionary<VReg, List<Instr>>? _writes;

        public AddressScan(Function f)
        {
            _f = f;
            _defs = new(f, buildCfg: false);
            foreach (var b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || i.Operands.Count == 0 || i.Operands[0] is not RegOperand) continue;
                    if (!_defs.IsSingle(d)) { _joins |= i.Op == Opcode.Copy; continue; }
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                        || i.Op is Opcode.Add or Opcode.Sub && i.Operands.Count == 2 && i.Operands[1] is ImmOperand)
                        _steps.Add(i);
                }
            _steps.Sort(BySource);
        }

        public Dictionary<VReg, long> Find(IEnumerable<VReg> roots)
        {
            // The first of each root, in order, as Distinct and ToDictionary
            // gave them, without the iterators and the copy of the keys.
            Dictionary<VReg, long> result = new();
            foreach (VReg r in roots) result.TryAdd(r, 0L);
            Follow(_steps, result, result.Keys);
            if (_joins) JoinedAliases(_f, _defs, result, _steps, _writes ??= MultiWrites(_f, _defs));
            return result;
        }
    }

    private static readonly Comparison<Instr> BySource = (x, y) => Source(x).CompareTo(Source(y));
    private static int Source(Instr i) => ((RegOperand)i.Operands[0]).Reg.Id;

    /// <summary>
    /// Every step whose operand is an address makes its register one too, at
    /// the operand's offset moved by the step's constant: from each address
    /// newly known (`from`), the steps that read it, found in `steps` sorted
    /// by the register they read. Swept to a fixed point instead, a chain
    /// listed against its order took a sweep a link -- a whole library's
    /// unit sat minutes in it.
    /// </summary>
    private static void Follow(List<Instr> steps, Dictionary<VReg, long> result, IEnumerable<VReg> from)
    {
        // `from` is read whole into the queue before `result` is written, so
        // it may be result's own keys, or a set the caller keeps.
        Queue<VReg> next = new(from);
        while (next.TryDequeue(out VReg? at))
        {
            long offset0 = result[at];
            int lo = 0, hi = steps.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (Source(steps[mid]) < at.Id) lo = mid + 1; else hi = mid; }
            for (int k = lo; k < steps.Count && Source(steps[k]) == at.Id; k++)
            {
                Instr i = steps[k];
                if (result.ContainsKey(i.Dest!)) continue;
                long offset = offset0;
                if (i.Op is Opcode.Add or Opcode.Sub)
                {
                    long amount = ((ImmOperand)i.Operands[1]).Value;
                    try { offset = i.Op == Opcode.Add ? checked(offset + amount) : checked(offset - amount); }
                    catch (OverflowException) { continue; }
                    // Do not confuse machine-address wraparound with a far
                    // disjoint field. Unknown/large address arithmetic makes
                    // Reads reject the receiver rather than hiding a capture.
                    if (offset < -1048576 || offset > 1048576) continue;
                }
                result[i.Dest!] = offset;
                next.Enqueue(i.Dest!);
            }
        }
    }

    /// <summary>
    /// REGISTERS WRITTEN MORE THAN ONCE that hold nothing but the object (or
    /// null): `x ??= new T()` is `nc = x; if (nc == null) nc = made; x = nc`,
    /// x from null or nc, nc from x or the object -- both are its addresses.
    /// The largest such group whose every write is null, a known address or
    /// another of the group, all at one offset; to a fixed point, since each
    /// waits on the other.
    /// </summary>
    private static void JoinedAliases(Function f, Defs defs, Dictionary<VReg, long> result, List<Instr> steps,
        Dictionary<VReg, List<Instr>> writes)
    {
        // ONE GROUP AND ONE SNAPSHOT OF IT, emptied each round: emptied, a
        // set takes its members in the order a new one would, and the loops
        // below are loops, not All over a closure for every register.
        HashSet<VReg> group = new();
        List<VReg> members = new();
        while (true)
        {
            group.Clear();
            foreach ((VReg r, List<Instr> all) in writes)
            {
                if (result.ContainsKey(r)) continue;
                bool copies = true;
                foreach (Instr w in all)
                {
                    if (w is not { Op: Opcode.Copy, Operands: [ImmOperand { Value: 0 } or RegOperand] })
                    {
                        copies = false;
                        break;
                    }
                }
                if (copies) group.Add(r);
            }
            bool shrank = true;
            while (shrank)
            {
                shrank = false;
                members.Clear();
                members.AddRange(group);
                foreach (VReg r in members)
                {
                    bool held = true;
                    foreach (Instr w in writes[r])
                    {
                        if (!(w.Operands[0] is ImmOperand || w.Operands[0] is RegOperand { Reg: var from } && (result.ContainsKey(from) || group.Contains(from))))
                        {
                            held = false;
                            break;
                        }
                    }
                    if (!held) { group.Remove(r); shrank = true; }
                }
            }
            // One offset for the group, from the addresses written into it.
            long? offset = null;
            bool agree = true;
            foreach (VReg r in group)
                foreach (Instr w in writes[r])
                    if (w.Operands[0] is RegOperand { Reg: var from } && result.TryGetValue(from, out long at))
                    {
                        if (offset is null) offset = at;
                        else if (offset != at) agree = false;
                    }
            if (group.Count == 0 || offset is null || !agree) return;
            foreach (VReg r in group) result[r] = offset.Value;
            // Whatever follows from them, as the single writes do.
            Follow(steps, result, group);
        }
    }

    private static Dictionary<VReg, List<Instr>> MultiWrites(Function f, Defs defs)
    {
        Dictionary<VReg, List<Instr>> writes = new();
        foreach (var b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && !defs.IsSingle(d))
                {
                    if (!writes.TryGetValue(d, out List<Instr>? list)) writes[d] = list = new();
                    list.Add(i);
                }
        return writes;
    }

    /// <summary>Check direct aliases and every ancestor path that can reload the owner.</summary>
    internal bool ReadsOwner(Function f, Owner owner, IReadOnlyList<Field> path, HashSet<VReg> loaded)
    {
        if (path.Count > 4) return false;
        if (!Reads(f, Addresses(f, owner.Aliases), path, loaded, owner.Stores, owner.Stamp)) return false;
        foreach (var parent in owner.Parents)
        {
            List<Field> throughParent = new() { parent.Field };
            throughParent.AddRange(path);
            if (!ReadsOwner(f, parent.Parent, throughParent, loaded)) return false;
        }
        return true;
    }

    /// <summary>Collect leaf references along a field path, including direct callee traversals.</summary>
    private bool Reads(Function f, Dictionary<VReg, long> addresses, IReadOnlyList<Field> path, HashSet<VReg> loaded,
        HashSet<Instr>? receiverStores = null, (string Name, long Offset)? stamp = null)
    {
        if (path.Count == 0 || path.Count > 4) return false;
        long field = path[0].Offset;
        int width = path[0].Width;
        Defs defs = new(f, buildCfg: false);
        // COPIES OF THE OWNER'S BYTES, whole field and all: a struct that holds
        // the reference -- a foreach's enumerator, returned by value and copied
        // into the frame slot the loop walks -- is the owner again wherever it
        // was copied to, and is read there under the same rules.
        Dictionary<FrameSlot, long> slots = new();
        if (!FollowCopies(f, defs, addresses, slots, field, width)) return false;
        foreach (var b in f.Blocks)
        foreach (Instr i in b.Instrs)
        {
            if (!IrInfo.Uses(i).Any(addresses.ContainsKey) && !NamesSlot(i, slots)) continue;
            if (i.Dest is { } alias && addresses.ContainsKey(alias)
                && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add or Opcode.Sub) continue;
            if (i.Op is Opcode.Load or Opcode.Store)
            {
                // These stores were proved when this owner itself became a
                // local child. ReadsOwner checks their ancestor paths too.
                if (i.Op == Opcode.Store && receiverStores is not null && receiverStores.Contains(i)
                    && i.Operands[1] is RegOperand stored && addresses.ContainsKey(stored.Reg)) continue;
                long start;
                if (i.Operands[0] is RegOperand baseReg && addresses.TryGetValue(baseReg.Reg, out long fromRegister)) start = fromRegister;
                else if (i.Operands[0] is SlotOperand baseSlot && slots.TryGetValue(baseSlot.Slot, out long fromSlot)) start = fromSlot;
                else return false;
                if (i.Op == Opcode.Store && i.Operands[1] is RegOperand value && addresses.ContainsKey(value.Reg)) return false;
                if (i.Op == Opcode.Store && i.Operands[1] is SlotOperand storedSlot && slots.ContainsKey(storedSlot.Slot)) return false;
                long at;
                try { at = checked(start + i.Offset); }
                catch (OverflowException) { return false; }
                if (at > long.MaxValue - i.Size || field > long.MaxValue - width) return false;
                if (at < field + width && field < at + i.Size)
                {
                    if (at != field || i.Size != width) return false;
                    if (i.Op == Opcode.Load)
                    {
                        if (i.Dest is null || !defs.IsSingle(i.Dest) || !i.Dest.Type.IsInt()) return false;
                        if (path.Count == 1) loaded.Add(i.Dest);
                        else if (!Reads(f, Addresses(f, i.Dest), path.Skip(1).ToArray(), loaded)) return false;
                    }
                }
                continue;
            }
            // THE COLLECTOR TOLD OF A STORE (the write barrier): it reads the
            // field's old value for the collector and keeps no pointer the
            // program can use -- the rule Escape keeps for the same call. Its
            // argument is the field's address as the target's word, which on
            // i386 passes through a widening that hid it from the callee
            // walk below, and in long mode did not.
            if (i.Op == Opcode.Call && Escape.IsCollectorNote(i.Callee)) continue;
            if (i.Op == Opcode.Call)
            {
                if (i.Callee is null) { LastRefusal = i; return false; }
                Function? callee = TryFunction(i.Callee, out Function? found) ? found : null;
                for (int a = 0; a < i.Operands.Count; a++)
                    if (i.Operands[a] is RegOperand arg && addresses.TryGetValue(arg.Reg, out long offset)
                        || i.Operands[a] is SlotOperand argSlot && slots.TryGetValue(argSlot.Slot, out offset))
                    {
                        long relative;
                        try { relative = checked(field - offset); }
                        catch (OverflowException) { return false; }
                        List<Field> relativePath = new(path);
                        relativePath[0] = new(relative, width);
                        // A BODY NOT IN SIGHT -- another unit's, not brought in
                        // -- answered by its field summary, for a child at one
                        // remove: the whole program's word of what the callee
                        // does to that field of its argument, clean if it never
                        // lets go of what it reads there nor puts anything but
                        // a fresh object in it.
                        if (callee is null)
                        {
                            if (!CleanInSummary(i.Callee, a, relativePath)) { LastRefusal = i; return false; }
                            continue;
                        }
                        LastRefusal = null;
                        // The innermost refusal is the one worth naming (--trace-escape).
                        // The owner itself, not a place inside it, keeps its stamp.
                        if (!Safe(callee, a, relativePath, offset == 0 ? stamp : null)) { LastRefusal ??= i; return false; }
                    }
                continue;
            }
            // A CLOSURE'S OWN INVOKE: the delegate it was handed as called with
            // it as the receiver, and nothing else of the owner handed over.
            // The method is the one its stamp's table holds at the slot the
            // call reads -- the lambda's body, which reads the field as its
            // `this`'s -- and the field is safe where that body keeps nothing
            // it reads there. Any other indirect call is refused below.
            if (i.Op == Opcode.CallIndirect && i.Field == Instr.DelegateInvoke && stamp is { } closure
                && Invoked(defs, i, addresses, closure) is string body && TryFunction(body, out Function? lambda) && lambda is not null)
            {
                for (int a = 2; a < i.Operands.Count; a++)
                    if (i.Operands[a] is RegOperand passed && addresses.ContainsKey(passed.Reg)
                        || i.Operands[a] is SlotOperand passedSlot && slots.ContainsKey(passedSlot.Slot))
                    { LastRefusal = i; return false; }
                LastRefusal = null;
                if (!Safe(lambda, 0, path, closure)) { LastRefusal ??= i; return false; }
                continue;
            }
            if (i.Op == Opcode.MemSet && i.Operands[0] is RegOperand target && addresses.ContainsKey(target.Reg)
                && !i.Operands.Skip(1).OfType<RegOperand>().Any(r => addresses.ContainsKey(r.Reg))) continue;
            if (i.Op == Opcode.MemCopy && CopyIsFollowed(i, addresses, slots, field, width)) continue;
            if (IrInfo.IsIntCompare(i.Op)) continue;
            // No copies of the owner's bytes, indirect calls, syscalls,
            // variable field addresses, returns or unknown operations.
            LastRefusal = i;
            return false;
        }
        return true;
    }

    /// <summary>Where a memory operand of the owner (register or frame slot) starts in it; false when it is not one.</summary>
    private static bool StartOf(Operand o, Dictionary<VReg, long> addresses, Dictionary<FrameSlot, long> slots, out long start)
    {
        start = 0;
        return o is RegOperand r && addresses.TryGetValue(r.Reg, out start)
            || o is SlotOperand s && slots.TryGetValue(s.Slot, out start);
    }

    private static bool NamesSlot(Instr i, Dictionary<FrameSlot, long> slots)
    {
        if (slots.Count == 0) return false;
        foreach (Operand o in i.Operands) if (o is SlotOperand s && slots.ContainsKey(s.Slot)) return true;
        return false;
    }

    /// <summary>
    /// Every memcopy out of the owner that carries the whole field makes its
    /// destination -- a register's block, or a frame slot and every register
    /// holding its address -- one more place the owner's bytes are, at the
    /// offset they were copied from. To a fixed point, for a copy of a copy.
    /// False for a copy that takes part of the field, or puts the owner's
    /// bytes at two different offsets of one place.
    /// </summary>
    private static bool FollowCopies(Function f, Defs defs, Dictionary<VReg, long> addresses, Dictionary<FrameSlot, long> slots,
        long field, int width)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.MemCopy || i.Operands.Count != 3 || i.Operands[2] is not ImmOperand { Value: var length }
                    || !StartOf(i.Operands[1], addresses, slots, out long from)) continue;
                if (!(from < field + width && field < from + length)) continue;
                if (field < from || field + width > from + length) return false;
                // ONLY INTO THE FRAME: a slot, directly or through the one copy
                // of its address that made the register. Anywhere else -- an
                // element of a list's array, a caller's return buffer -- the
                // bytes outlive this function and are read where these rules
                // cannot see, and a child judged by them was put in the frame
                // and read back zeroed by the self-built compiler.
                FrameSlot? slot = i.Operands[0] as SlotOperand is { } direct ? direct.Slot : null;
                if (i.Operands[0] is RegOperand target)
                {
                    if (!defs.IsSingle(target.Reg) || defs.Site(target.Reg) is not { } site
                        || site.Block.Instrs[site.Index] is not { Op: Opcode.Copy, Operands: [SlotOperand { Slot: var held }] })
                        return false;
                    slot = held;
                    if (addresses.TryGetValue(target.Reg, out long known))
                    {
                        if (known != from) return false;
                    }
                    else
                    {
                        foreach (var alias in Addresses(f, target.Reg))
                        {
                            if (addresses.TryGetValue(alias.Key, out long was) && was != alias.Value + from) return false;
                            addresses[alias.Key] = alias.Value + from;
                        }
                        changed = true;
                    }
                }
                if (slot is null) return false;
                if (slots.TryGetValue(slot, out long at))
                {
                    if (at != from) return false;
                    continue;
                }
                slots[slot] = from;
                changed = true;
                // Every register given the slot's address reads it too.
                foreach (var sb in f.Blocks)
                foreach (Instr c in sb.Instrs)
                    if (c.Op == Opcode.Copy && c.Dest is { } d && c.Operands is [SlotOperand { Slot: var named }] && named == slot)
                        foreach (var alias in Addresses(f, d))
                        {
                            if (addresses.TryGetValue(alias.Key, out long was) && was != alias.Value + from) return false;
                            addresses[alias.Key] = alias.Value + from;
                        }
            }
        }
        return true;
    }

    /// <summary>
    /// A memcopy in or out of the owner that the field takes no part in, or
    /// one FollowCopies followed: the destination already holds the owner's
    /// bytes at the offset they came from. A copy INTO the owner over the
    /// field, from anywhere else, is not.
    /// </summary>
    private static bool CopyIsFollowed(Instr i, Dictionary<VReg, long> addresses, Dictionary<FrameSlot, long> slots, long field, int width)
    {
        if (i.Operands.Count != 3 || i.Operands[2] is not ImmOperand { Value: var length }) return false;
        bool fromOwner = StartOf(i.Operands[1], addresses, slots, out long from);
        bool intoOwner = StartOf(i.Operands[0], addresses, slots, out long into);
        bool readsField = fromOwner && from < field + width && field < from + length;
        bool writesField = intoOwner && into < field + width && field < into + length;
        if (!readsField && !writesField) return true;
        return readsField && intoOwner && into == from && field >= from && field + width <= from + length;
    }

    /// <summary>
    /// The method a delegate's Invoke reaches on the owner `stamp` names:
    /// the call's method read from the table the receiver's first word
    /// points at, at a constant slot, the receiver the owner itself (offset
    /// 0). Null for any other shape.
    /// </summary>
    private static string? Invoked(Defs defs, Instr call, Dictionary<VReg, long> addresses, (string Name, long Offset) stamp)
    {
        if (call.Operands.Count < 2 || call.Operands[0] is not RegOperand { Reg: var method }
            || call.Operands[1] is not RegOperand { Reg: var receiver } || !addresses.TryGetValue(receiver, out long at) || at != 0
            || !defs.IsSingle(method) || defs.Site(method) is not { } slotSite
            || slotSite.Block.Instrs[slotSite.Index] is not { Op: Opcode.Load, Operands: [RegOperand { Reg: var table }] } slotLoad
            || !defs.IsSingle(table) || defs.Site(table) is not { } tableSite
            || tableSite.Block.Instrs[tableSite.Index] is not { Op: Opcode.Load, Offset: 0, Operands: [RegOperand { Reg: var self }] }
            || !addresses.TryGetValue(self, out long selfAt) || selfAt != 0) return null;
        return Escape.StampMethod(stamp.Name, stamp.Offset + slotLoad.Offset);
    }

    private bool Safe(Function f, int parameter, IReadOnlyList<Field> path, (string Name, long Offset)? stamp = null)
    {
        var key = (f.Name, parameter, string.Join(";", path.Select(p => p.Offset + ":" + p.Width)), stamp is { } named ? named.Name + "+" + named.Offset : null);
        if (_memo.TryGetValue(key, out bool answer)) return answer;
        // Cycles and an excessive query graph remain pessimistic.
        if (_memo.Count >= 1024 || parameter >= f.Params.Count || f.Async is not null) return false;
        _memo[key] = false;
        HashSet<VReg> loaded = new();
        var addresses = Addresses(f, f.Params[parameter]);
        bool safe = Reads(f, addresses, path, loaded, null, stamp);
        if (safe && Escape.Analyse(f, loaded, _summaries, null) is { Escapes: true } lost) { safe = false; LastRefusal = lost.Why; }
        _memo[key] = safe;
        return safe;
    }
}
