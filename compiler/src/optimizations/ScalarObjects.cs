#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Scalar replacement of direct object fields with dominated lifetimes.</summary>
public sealed class ScalarObjects : IParallelModulePass
{
    public string Name => "scalar-objects";
    public int Replaced { get; private set; }
    public int Workers { get; set; } = 1;

    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        if (Workers > 1 && module.Functions.Count > 1)
            RunParallel(module);
        else
            foreach (Function f in module.Functions) Replaced += Fold(f);
    }

    private void RunParallel(Module module)
    {
        int[] counts = new int[module.Functions.Count];
        FunctionWorkers.Run(module, Workers, (function, index) => counts[index] = Fold(function));
        foreach (int count in counts) Replaced += count;
    }

    private static int Fold(Function f)
    {
            if (f.Async is not null) return 0;
            int replaced = 0;
            foreach (Block block in f.Blocks)
                foreach (Instr alloc in block.Instrs.ToArray())
                    if (alloc.Op == Opcode.Call && Escape.IsAllocator(alloc.Callee)
                        && alloc.Dest is not null && alloc.Operands.Count == 1
                        && alloc.Operands[0] is ImmOperand size && size.Value > 0 && size.Value <= 1024)
                        if (Replace(f, block, alloc, size.Value)) replaced++;
            if (replaced != 0)
            {
                // Expose child references before the following escape pass:
                // scalar fields need not hide behind dead mutable copies.
                new LocalCopies().Run(f);
                new DeadCodeElimination().Run(f);
            }
            return replaced;
    }

    private static bool Replace(Function f, Block block, Instr alloc, long bytes)
    {
        Defs defs = new(f);
        if (!defs.IsSingle(alloc.Dest!) || defs.Cfg.Roots.Count != 1) return false;
        Dictionary<VReg, long> addresses = new() { [alloc.Dest!] = 0 };
        HashSet<Instr> aliases = new();
        int start = block.Instrs.IndexOf(alloc);
        bool changed;
        do
        {
            changed = false;
            foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
            if (i.Dest is null || !defs.IsSingle(i.Dest) || i.Operands.Count == 0
                || addresses.ContainsKey(i.Dest)
                || i.Operands[0] is not RegOperand r || !addresses.TryGetValue(r.Reg, out long at)) continue;
            if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)
            { addresses[i.Dest] = at; aliases.Add(i); changed = true; }
            else if (i.Op == Opcode.Add && i.Operands.Count == 2 && i.Operands[1] is ImmOperand delta
                && delta.Value >= 0 && delta.Value <= bytes && at <= bytes - delta.Value)
            { addresses[i.Dest] = at + delta.Value; aliases.Add(i); changed = true; }
            }
        } while (changed);

        if (Escape.LiveAtSelf(new Liveness(defs.Cfg), block, alloc, addresses.Keys.ToHashSet())) return false;

        Dictionary<long, (int Size, IrType Type)> fields = new();
        Dictionary<Instr, long> accesses = new();
        foreach (Block b in f.Blocks)
        for (int k = 0; k < b.Instrs.Count; k++)
        {
            Instr i = b.Instrs[k];
            if (!IrInfo.Uses(i).Any(addresses.ContainsKey)) continue;
            // No loop-carried references, calls, address escapes,
            // identity tests, unknown offsets, overlapping fields or partial loads.
            if (!defs.Cfg.Dominates(block, b) || (b == block && k <= start)) return false;
            foreach (VReg r in IrInfo.Uses(i).Where(addresses.ContainsKey))
            {
                var site = defs.Site(r);
                if (site is null || !defs.Cfg.Dominates(site.Value.Block, b)
                    || (site.Value.Block == b && site.Value.Index >= k)) return false;
            }
            if (aliases.Contains(i)) continue;
            if (i.Op is not (Opcode.Load or Opcode.Store) || i.Size is not (1 or 2 or 4 or 8)
                || i.Operands[0] is not RegOperand root || !addresses.TryGetValue(root.Reg, out long offset)) return false;
            if (i.Op == Opcode.Store && i.Operands[1] is RegOperand value && addresses.ContainsKey(value.Reg)) return false;
            if (i.Offset < 0 || i.Offset > bytes || offset > bytes - i.Offset) return false;
            offset += i.Offset;
            if (offset > bytes - i.Size) return false;
            IrType type = i.Op == Opcode.Load ? i.Dest!.Type : i.Operands[1].Type;
            if (!((type == IrType.I32 && i.Size <= 4) || (type == IrType.I64 && i.Size == 8))) return false;
            foreach (var field in fields)
                if (offset < field.Key + field.Value.Size && field.Key < offset + i.Size
                    && (field.Key != offset || field.Value.Size != i.Size || field.Value.Type != type)) return false;
            fields[offset] = (i.Size, type);
            accesses[i] = offset;
        }
        if (fields.Count == 0) return false;
        Dictionary<long, VReg> locals = fields.ToDictionary(p => p.Key, p => f.NewReg(p.Value.Type, "scalarfield"));
        foreach (Block b in f.Blocks)
        {
        List<Instr> result = new();
        foreach (Instr i in b.Instrs)
        {
            if (i == alloc)
            {
                foreach (var field in locals)
                    result.Add(new Instr { Op = Opcode.Copy, Dest = field.Value,
                        Operands = { new ImmOperand(0, field.Value.Type) }, Line = i.Line });
            }
            else if (aliases.Contains(i)) { }
            else if (accesses.TryGetValue(i, out long offset))
            {
                if (i.Size < 4)
                {
                    // A store truncates even when its input is a full-width
                    // register. Signedness belongs to each load, not the slot.
                    bool signed = i.Op == Opcode.Load && i.Signed;
                    Opcode extend = (i.Size, signed) switch
                    {
                        (1, true) => Opcode.SExt8, (1, false) => Opcode.ZExt8,
                        (2, true) => Opcode.SExt16, _ => Opcode.ZExt16,
                    };
                    result.Add(new Instr { Op = extend,
                        Dest = i.Op == Opcode.Load ? i.Dest : locals[offset],
                        Operands = { i.Op == Opcode.Load ? new RegOperand(locals[offset]) : i.Operands[1] },
                        Line = i.Line });
                }
                else
                result.Add(i.Op == Opcode.Load ? IrInfo.CopyOf(i, new RegOperand(locals[offset]))
                    : new Instr { Op = Opcode.Copy, Dest = locals[offset], Operands = { i.Operands[1] }, Line = i.Line });
            }
            else result.Add(i);
        }
        b.Instrs.Clear(); b.Instrs.AddRange(result);
        }
        return true;
    }
}
