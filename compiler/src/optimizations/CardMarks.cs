#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE CARD MARK, WRITTEN OUT. Lowering emits the generational barrier as a
/// call to Runtime.CardMark after every store of a reference (Gc,
/// generations), and it stays a call through every lifetime pass: to them it
/// is a note to the collector, like the snapshot barrier, and an object whose
/// address it is given is not thereby let go of (EscapeFields.IsCollectorNote).
/// Written out as instructions there, the address it shifts would read to them
/// as the object leaving.
///
/// So this runs last, after escape analysis and the final inliner, and makes
/// each call what it stands for: the table read, and when there is one, the
/// byte for the store's kilobyte set.
///
///     t = load Runtime.Cards
///     branch t -> mark, after
///   mark:
///     store.u1 [t + (slot >> 10)], 1
///     jump after
/// </summary>
public sealed class CardMarks : IModulePass
{
    public string Name => "card-marks";

    /// <summary>Runtime.CardMark: the call lowering emits.</summary>
    public const string CardMark = Corsac.Lang.Lto.RuntimeAbi.CardMark;

    /// <summary>Runtime.Cards: the table, 0 when the collector keeps none.</summary>
    public const string Cards = "s_Runtime_Cards";

    /// <summary>A card is a kilobyte (Runtime.CardShift).</summary>
    public const int CardShift = 10;

    public void Run(Module m)
    {
        foreach (Function f in m.Functions)
        {
            Expand(f);
        }
    }

    /// <summary>Runtime.WriteBarrier: the snapshot barrier's slow path, which lowering calls.</summary>
    public const string Barrier = Corsac.Lang.Lto.RuntimeAbi.WriteBarrier;

    /// <summary>
    /// What lowering widened to pass as the runtime's `long`: the word it
    /// widened, when that is in the same block and not written again since.
    /// </summary>
    private static Operand Narrow(Block block, int k, Operand wide)
    {
        if (wide is not RegOperand { Reg: var held } || wide.Type == IrTypes.Word) return wide;
        for (int d = k - 1; d >= 0; d--)
        {
            if (!ReferenceEquals(block.Instrs[d].Dest, held)) continue;
            if (block.Instrs[d] is { Op: Opcode.ZExt32, Operands: [var narrow] } && narrow.Type == IrTypes.Word
                && (narrow is not RegOperand { Reg: var source }
                    || !block.Instrs.Skip(d + 1).Take(k - d - 1).Any(between => ReferenceEquals(between.Dest, source))))
                return narrow;
            break;
        }
        return wide;
    }

    public static void Expand(Function f)
    {
        // ON I386 A CALL TO THE OBJECT'S STUB, which keeps every register:
        // seven bytes a store where the table's load, test, shift and byte
        // store, written out here, took forty and spilled what was live.
        if (Target.Current.Name == "x86")
        {
            foreach (Block block in f.Blocks)
                for (int k = 0; k < block.Instrs.Count; k++)
                {
                    Instr i = block.Instrs[k];
                    if (i.Op == Opcode.Call && i.Callee == Barrier && i.Operands.Count == 2)
                    {
                        // The snapshot barrier's slow path, the same way: slot
                        // and value in registers, the stub calls the runtime.
                        Operand at = Narrow(block, k, i.Operands[0]), value = Narrow(block, k, i.Operands[1]);
                        if (at.Type == IrTypes.Word && value.Type == IrTypes.Word)
                            block.Instrs[k] = new Instr { Op = Opcode.Call, Callee = "__x86.i.barrier", Operands = { at, value }, Line = i.Line };
                        continue;
                    }
                    if (i.Op != Opcode.Call || i.Callee != CardMark || i.Operands.Count != 1) continue;
                    Operand slot = Narrow(block, k, i.Operands[0]);
                    // The runtime's parameter is a long; the stub takes the address, a word.
                    if (slot.Type != IrTypes.Word)
                    {
                        VReg word = f.NewReg(IrTypes.Word);
                        block.Instrs.Insert(k, new Instr { Op = Opcode.Trunc64, Dest = word, Operands = { slot } });
                        k++;
                        slot = new RegOperand(word);
                    }
                    block.Instrs[k] = new Instr { Op = Opcode.Call, Callee = "__x86.i.cardmark", Operands = { slot }, Line = i.Line };
                }
            return;
        }
        for (int b = 0; b < f.Blocks.Count; b++)
        {
            Block block = f.Blocks[b];
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr i = block.Instrs[k];
                if (i.Op != Opcode.Call || i.Callee != CardMark || i.Operands.Count != 1)
                {
                    continue;
                }

                Block mark = f.NewBlock("card");
                Block after = f.NewBlock("carded");
                after.Instrs.AddRange(block.Instrs.GetRange(k + 1, block.Instrs.Count - k - 1));
                block.Instrs.RemoveRange(k, block.Instrs.Count - k);

                Builder e = new(f, block);
                VReg table = e.Load(IrTypes.Word, new SymOperand(Cards), 0, IrTypes.Word.Bytes());
                e.Branch(table, mark, after);

                e.SetBlock(mark);
                // The runtime's parameter is a long; the address is a word.
                Operand slot = i.Operands[0];
                VReg word = slot is RegOperand { Reg: { } held } && held.Type == IrTypes.Word
                    ? held
                    : slot.Type == IrTypes.Word
                        ? e.Unary(Opcode.Copy, slot, IrTypes.Word)
                        : e.Unary(IrTypes.Word == IrType.I32 ? Opcode.Trunc64 : Opcode.ZExt32, slot, IrTypes.Word);
                VReg card = e.Binary(Opcode.Add, table, e.Binary(Opcode.ShrU, word, CardShift));
                e.Store(new RegOperand(card), new ImmOperand(1, IrType.I32), 0, 1);
                e.Jump(after);
                // The rest of the old block is `after` now; carry on there.
                break;
            }
        }
    }
}
