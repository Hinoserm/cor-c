#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A store to a static nothing ever reads is not a store.
///
/// A program links the whole standard library as source and reaches a
/// fraction of it, but every static initialiser of every type still runs
/// before Main: `Path.DirectorySeparatorChar` is written by a program that
/// never names Path. The write is a word of code and a word of data for
/// nothing. This finds every symbol the module loads from, takes its
/// address for, or exports, and removes the stores to the rest -- then the
/// data item itself, since nothing is left to refer to it.
///
/// Only symbols with no address taken: a store through a pointer someone
/// computed from the symbol is a read this cannot see. A Copy of the
/// SymOperand into a register is exactly that and disqualifies the symbol.
/// </summary>
public sealed class DeadStatics : IModulePass
{
    public string Name => "dead-statics";

    public void Run(Module m)
    {
        if (m.Entry is null)
        {
            return;     // a library: its statics belong to whoever links it
        }

        HashSet<string> read = new(StringComparer.Ordinal);
        HashSet<string> written = new(StringComparer.Ordinal);

        foreach (DataItem d in m.Data)
        {
            foreach (DataReloc r in d.Relocs)
            {
                read.Add(r.Symbol);
            }
        }

        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (i.Operands[k] is not SymOperand s)
                        {
                            continue;
                        }

                        // The address as the destination of a store is the
                        // one use that is not a read.
                        if (i.Op == Opcode.Store && k == 0)
                        {
                            written.Add(s.Name);
                        }
                        else
                        {
                            read.Add(s.Name);
                        }
                    }
                }
            }
        }

        HashSet<string> dead = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            if (d.Zero && !read.Contains(d.Name) && written.Contains(d.Name))
            {
                dead.Add(d.Name);
            }
        }

        if (dead.Count == 0)
        {
            return;
        }

        foreach (Function f in m.Functions)
        {
            foreach (Block b in f.Blocks)
            {
                b.Instrs.RemoveAll(i => i.Op == Opcode.Store
                                     && i.Operands[0] is SymOperand s
                                     && dead.Contains(s.Name));
            }
        }

        m.Data.RemoveAll(d => dead.Contains(d.Name));
    }
}
