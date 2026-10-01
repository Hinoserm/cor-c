#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A virtual call on an object whose making is in sight is a direct call.
///
/// An object made here has the type it was made with, and the vtable word
/// lowering stores into it as it is made is never written again. So the load
/// of that word is the vtable's symbol; a slot read from the vtable is read
/// from read-only data, at a relocation that names the method; and the
/// indirect call through it is a call of that method. The iterator a
/// `foreach` asks for its enumerator, a lambda invoked where it was written,
/// an enumerator boxed and walked: each was a call nothing could see into,
/// and an unknown call lets every argument escape -- the object itself
/// first, so not one of them was ever freed.
///
/// Three steps, each needing the last: a load of word 0 of a fresh object
/// (an allocation's register, through copies) whose one store of word 0 is
/// a symbol, made where it dominates the load, becomes that symbol; a load
/// at a constant offset from a read-only item, at a relocation, becomes the
/// symbol the relocation names (as ReadOnlyFold does); and a callindirect
/// whose target is a copy of a function's symbol becomes a call of it.
/// </summary>
public sealed class Devirtualize : IModulePass
{
    public string Name => "devirtualize";

    /// <summary>How many indirect calls became direct.</summary>
    public int Resolved { get; private set; }

    public void Run(Module m)
    {
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
            if (d.ReadOnly && !d.Zero) items[d.Name] = d;
        if (items.Count == 0) return;
        foreach (Function f in m.Functions) Run(f, items);
    }

    private void Run(Function f, Dictionary<string, DataItem> items)
    {
        bool any = false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.CallIndirect) { any = true; break; }
        if (!any) return;

        Defs defs = new(f, buildCfg: false);
        Cfg? cfg = null;
        int word = IrTypes.Word.Bytes();

        // The fresh object a register holds the address of, through copies:
        // the allocation that made it, or null.
        Instr? Made(Operand o)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { } d) return null;
                if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)) return d;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) return null;
                o = d.Operands[0];
            }
            return null;
        }
        // A register's value as a symbol, through copies.
        SymOperand? Symbol(Operand o)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is SymOperand s) return s;
                if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { Op: Opcode.Copy } d) return null;
                o = d.Operands[0];
            }
            return null;
        }

        // Each fresh object's vtable store: the only store of its word 0.
        Dictionary<Instr, (Block Block, int Index, SymOperand Vtable)?> vtables = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Store || i.Offset != 0 || i.Size != word || i.Operands.Count < 2) continue;
                if (Made(i.Operands[0]) is not { } made) continue;
                if (vtables.ContainsKey(made)) { vtables[made] = null; continue; }
                vtables[made] = i.Operands[1] is SymOperand vt ? (b, k, vt) : null;
            }
        if (vtables.Count == 0) return;

        bool Before(Block sb, int si, Block lb, int li) =>
            ReferenceEquals(sb, lb) ? si < li : (cfg ??= new Cfg(f)).Dominates(sb, lb);

        // 1. The vtable word of a fresh object. Any other store that may write
        //    word 0 -- through an address not known to be another object --
        //    would make this wrong, but lowering writes word 0 only as it
        //    makes the object, and the object's own address is followed.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Load || i.Offset != 0 || i.Size != word || i.Dest is null) continue;
                if (Made(i.Operands[0]) is not { } made || !vtables.TryGetValue(made, out var store) || store is not { } s) continue;
                if (!Before(s.Block, s.Index, b, k)) continue;
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line, Operands = { new SymOperand(s.Vtable.Name, s.Vtable.Offset) } };
            }
        defs = new(f, buildCfg: false);

        // 2. A slot read from read-only data at a relocation.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Load || i.Size != word || i.Dest is null || i.Dest.Type != IrTypes.Word) continue;
                if (Symbol(i.Operands[0]) is not { } table || !items.TryGetValue(table.Name, out DataItem? item)) continue;
                long at = table.Offset + i.Offset;
                if (item.Relocs.FirstOrDefault(rel => rel.Offset == at) is not { Symbol: { } named } exact) continue;
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line, Operands = { new SymOperand(named, exact.Addend) } };
            }
        defs = new(f, buildCfg: false);

        // 3. The call through it.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.CallIndirect || i.Operands.Count < 1) continue;
                if (Symbol(i.Operands[0]) is not { Offset: 0 } target || !target.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
                Instr call = new() { Op = Opcode.Call, Dest = i.Dest, Line = i.Line, Callee = target.Name };
                for (int a = 1; a < i.Operands.Count; a++) call.Operands.Add(i.Operands[a]);
                b.Instrs[k] = call;
                Resolved++;
            }
    }
}
