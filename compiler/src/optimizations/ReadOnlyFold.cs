#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A load from read-only data at a known address is the data.
///
/// A string literal's count sits in its header, and `s.Length` on one is a
/// load the compiler laid down itself: the value is in the module, in the
/// bytes it is about to write to the file. So it is read here and the load
/// becomes an immediate. The same holds for any read-only item -- a
/// descriptor's instance size, a jump table -- as long as the word being
/// read is not a relocation, whose value the linker decides.
///
/// The address must be a symbol, directly or through a single-definition
/// copy, plus a constant. Nothing else is knowable.
/// </summary>
public sealed class ReadOnlyFold : IParallelModulePass
{
    public string Name => "readonly-fold";
    public int Workers { get; set; } = 1;

    public void Run(Module m)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
        {
            if (d.ReadOnly && !d.Zero)
            {
                items[d.Name] = d;
            }
        }
        if (items.Count == 0)
        {
            return;
        }

        if (Workers > 1 && m.Functions.Count > 1)
            RunParallel(m, items);
        else
            foreach (Function f in m.Functions) Fold(f, items);
    }

    // A separate method keeps captured task state out of the serial path.
    private void RunParallel(Module module, Dictionary<string, DataItem> items)
        => FunctionWorkers.Run(module, Workers, (function, index) => Fold(function, items));

    private static void Fold(Function f, Dictionary<string, DataItem> items)
    {
            // This pass only queries definitions; no CFG is needed.
            Defs defs = new(f, buildCfg: false);
            foreach (Block b in f.Blocks)
            {
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.Op != Opcode.Load || i.Dest is null || i.Dest.Type.IsFloat())
                    {
                        continue;
                    }

                    // Through single-definition copies: right after inlining
                    // the address is a parameter copied from a register
                    // copied from the symbol, and propagation has not run yet.
                    Operand address = i.Operands[0];
                    for (int hops = 0; hops < 8 && address is RegOperand r && defs.IsSingle(r.Reg)
                         && defs.Definition(r.Reg) is { Op: Opcode.Copy } def; hops++)
                    {
                        address = def.Operands[0];
                    }
                    SymOperand? sym = address as SymOperand;
                    if (sym is null || !items.TryGetValue(sym.Name, out DataItem? item))
                    {
                        continue;
                    }

                    long at = sym.Offset + i.Offset;
                    if (at < 0 || at + i.Size > item.Bytes.Length)
                    {
                        continue;
                    }
                    int relocBytes = IrTypes.Word.Bytes();
                    if (item.Relocs.Any(rel => rel.Offset < at + i.Size && rel.Offset + relocBytes > at))
                    {
                        continue;       // the linker fills this word; its value is not here
                    }

                    long value = 0;
                    for (int n = i.Size - 1; n >= 0; n--)
                    {
                        value = (value << 8) | item.Bytes[at + n];
                    }
                    if (i.Signed)
                    {
                        value = i.Size switch
                        {
                            1 => (sbyte)value,
                            2 => (short)value,
                            4 => (int)value,
                            _ => value,
                        };
                    }

                    b.Instrs[k] = new Instr
                    {
                        Op = Opcode.Copy, Dest = i.Dest, Line = i.Line,
                        Operands = { new ImmOperand(value, i.Dest.Type) },
                    };
                }
            }
    }
}
