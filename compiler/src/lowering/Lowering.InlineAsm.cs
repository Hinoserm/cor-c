#nullable enable
using Corsac.Asm;
using Corsac.Lang.Ir;
using Operand = Corsac.Lang.Ir.Operand;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

public sealed partial class Lowering
{
    /// <summary>
    /// AN ASM BLOCK (Corsac.Asm.InlineAsm), as the binder found it
    /// (AsmBinding): a call to the block's own intrinsic
    /// (MachineIntrinsics.Asm), its spec in the name and the locals it reads
    /// as operands, each as the program reads it; then one AsmOut for each
    /// local it writes, all of them together right after the block, and
    /// only then each stored where that local lives -- the selector takes
    /// the block and its AsmOuts as one (TryInlineAsm), and nothing may come
    /// between.
    /// </summary>
    private void LowerAsm(AsmStmt a)
    {
        // A block the binder refused has said why; there is nothing to make.
        if (!_b.AsmBlocks.TryGetValue(a, out AsmBinding? block)) return;
        List<Operand> inputs = new();
        for (int k = 0; k < block.Locals.Length; k++)
            if (block.Read[k]) inputs.Add(R(Eval(block.Names[k])));
        string spec = InlineAsm.Encode(new InlineAsm.Spec(block.Bits, block.Locals, block.Read, block.Written, block.Text));
        _e.Call(MachineIntrinsics.Asm + spec, IrType.Void, inputs.ToArray());
        List<(int Local, VReg Value)> outputs = new();
        for (int k = 0; k < block.Locals.Length; k++)
            if (block.Written[k])
                outputs.Add((k, _e.Call(MachineIntrinsics.AsmOut, InlineAsm.IrTypeOf(block.Locals[k]), new ImmOperand(k, IrType.I32))!));
        foreach ((int k, VReg value) in outputs)
        {
            if (PlaceOf(block.Names[k]) is not { } place)
            {
                Error(block.Names[k], $"asm: '{block.Names[k].Name}' is written by the block and is not a place a value goes");
                continue;
            }
            StorePlace(place, value);
        }
    }
}
