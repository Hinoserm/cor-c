#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Conservative retention queries for a reference stored in an owner's field.</summary>
internal sealed class OwnedFieldEscape
{
    private readonly Dictionary<string, Function> _functions;
    private readonly Dictionary<string, bool[]> _summaries;
    private readonly Dictionary<(string, int, string), bool> _memo = new();
    internal readonly record struct Field(long Offset, int Width);
    internal sealed class Owner
    {
        public required Corsac.Lang.Ir.Block Block { get; init; }
        public required VReg Root { get; init; }
        public required long Bytes { get; init; }
        public HashSet<VReg> Aliases { get; } = new();
        public HashSet<Instr> Stores { get; } = new();
        public List<(Owner Parent, Field Field)> Parents { get; } = new();
    }
    public OwnedFieldEscape(Dictionary<string, Function> functions, Dictionary<string, bool[]> summaries)
    { _functions = functions; _summaries = summaries; }

    /// <summary>Every repeat of the child must pass through a fresh owner lifetime.</summary>
    internal static bool OwnerRenews(Cfg cfg, Corsac.Lang.Ir.Block owner, Corsac.Lang.Ir.Block child)
    {
        if (!cfg.Dominates(owner, child)) return false;
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
        Defs defs = new(f);
        Dictionary<VReg, long> result = roots.Distinct().ToDictionary(r => r, _ => 0L);
        bool changed;
        do
        {
            changed = false;
            foreach (var b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is null || !defs.IsSingle(i.Dest) || result.ContainsKey(i.Dest)
                    || i.Operands.Count == 0 || i.Operands[0] is not RegOperand r
                    || !result.TryGetValue(r.Reg, out long offset)) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)
                { result[i.Dest] = offset; changed = true; }
                else if (i.Op is Opcode.Add or Opcode.Sub && i.Operands.Count == 2
                    && i.Operands[1] is ImmOperand amount)
                {
                    long next;
                    try { next = i.Op == Opcode.Add ? checked(offset + amount.Value) : checked(offset - amount.Value); }
                    catch (OverflowException) { continue; }
                    // Do not confuse machine-address wraparound with a far
                    // disjoint field. Unknown/large address arithmetic makes
                    // Reads reject the receiver rather than hiding a capture.
                    if (next < -1048576 || next > 1048576) continue;
                    result[i.Dest] = next;
                    changed = true;
                }
            }
        } while (changed);
        return result;
    }

    /// <summary>Check direct aliases and every ancestor path that can reload the owner.</summary>
    internal bool ReadsOwner(Function f, Owner owner, IReadOnlyList<Field> path, HashSet<VReg> loaded)
    {
        if (path.Count > 4) return false;
        if (!Reads(f, Addresses(f, owner.Aliases), path, loaded, owner.Stores)) return false;
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
        HashSet<Instr>? receiverStores = null)
    {
        if (path.Count == 0 || path.Count > 4) return false;
        long field = path[0].Offset;
        int width = path[0].Width;
        Defs defs = new(f);
        foreach (var b in f.Blocks)
        foreach (Instr i in b.Instrs)
        {
            if (!IrInfo.Uses(i).Any(addresses.ContainsKey)) continue;
            if (i.Dest is { } alias && addresses.ContainsKey(alias)
                && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add or Opcode.Sub) continue;
            if (i.Op is Opcode.Load or Opcode.Store)
            {
                // These stores were proved when this owner itself became a
                // local child. ReadsOwner checks their ancestor paths too.
                if (i.Op == Opcode.Store && receiverStores is not null && receiverStores.Contains(i)
                    && i.Operands[1] is RegOperand stored && addresses.ContainsKey(stored.Reg)) continue;
                if (i.Operands[0] is not RegOperand baseReg || !addresses.TryGetValue(baseReg.Reg, out long start)) return false;
                if (i.Op == Opcode.Store && i.Operands[1] is RegOperand value && addresses.ContainsKey(value.Reg)) return false;
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
            if (i.Op == Opcode.Call)
            {
                if (i.Callee is null || !_functions.TryGetValue(i.Callee, out Function? callee)) return false;
                for (int a = 0; a < i.Operands.Count; a++)
                    if (i.Operands[a] is RegOperand arg && addresses.TryGetValue(arg.Reg, out long offset))
                    {
                        long relative;
                        try { relative = checked(field - offset); }
                        catch (OverflowException) { return false; }
                        List<Field> relativePath = new(path);
                        relativePath[0] = new(relative, width);
                        if (!Safe(callee, a, relativePath)) return false;
                    }
                continue;
            }
            if (i.Op == Opcode.MemSet && i.Operands[0] is RegOperand target && addresses.ContainsKey(target.Reg)
                && !i.Operands.Skip(1).OfType<RegOperand>().Any(r => addresses.ContainsKey(r.Reg))) continue;
            if (IrInfo.IsIntCompare(i.Op)) continue;
            // No copies of the owner's bytes, indirect calls, syscalls,
            // variable field addresses, returns or unknown operations.
            return false;
        }
        return true;
    }

    private bool Safe(Function f, int parameter, IReadOnlyList<Field> path)
    {
        var key = (f.Name, parameter, string.Join(";", path.Select(p => p.Offset + ":" + p.Width)));
        if (_memo.TryGetValue(key, out bool answer)) return answer;
        // Cycles and an excessive query graph remain pessimistic.
        if (_memo.Count >= 1024 || parameter >= f.Params.Count || f.Async is not null) return false;
        _memo[key] = false;
        HashSet<VReg> loaded = new();
        var addresses = Addresses(f, f.Params[parameter]);
        bool safe = Reads(f, addresses, path, loaded)
            && !Escape.Analyse(f, loaded, _summaries, null).Escapes;
        _memo[key] = safe;
        return safe;
    }
}
