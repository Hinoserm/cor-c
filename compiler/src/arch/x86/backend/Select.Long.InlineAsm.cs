#nullable enable
using Corsac.Asm;
using Corsac.Lang.Ir;
using Operand = Corsac.Lang.Ir.Operand;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    /// <summary>
    /// AN ASM BLOCK and the AsmOuts right after it, as one -- i386's way
    /// (TryInlineAsm, Select.InlineAsm.cs): the memory locals' slots filled,
    /// the head's bindings moved into their registers, the block, the
    /// tail's bindings moved out and the written slots read back.
    /// </summary>
    private bool TryInlineAsmL(List<Instr> instrs, int n, out int consumed)
    {
        consumed = 0;
        Instr i = instrs[n];
        if (i.Op != Opcode.Call || i.Callee is not { } callee
            || !callee.StartsWith(MachineIntrinsics.Asm, StringComparison.Ordinal))
            return false;
        consumed = 1;
        InlineAsm.Spec spec = InlineAsm.Decode(callee[MachineIntrinsics.Asm.Length..]);
        if (spec.Bits != 64)
        {
            Error($"an asm block written for {spec.Bits}-bit code, in 64-bit code");
            return true;
        }
        InlineAsm.Analysis analysis;
        InlineAsm.Plan plan;
        try
        {
            analysis = InlineAsm.Analyze(spec.Text, 64, spec.Locals);
            plan = InlineAsm.Bind(analysis);
        }
        catch (AsmException e)
        {
            Error($"line {i.Line + Math.Max(1, e.Line) - 1}: asm: {e.Message}");
            return true;
        }

        int count = spec.Locals.Length;
        Operand?[] input = new Operand?[count];
        int next = 0;
        for (int k = 0; k < count; k++)
            if (spec.Read[k]) input[k] = next < i.Operands.Count ? i.Operands[next++] : null;
        VReg?[] output = new VReg?[count];
        int at = n + 1;
        for (int k = 0; k < count; k++)
        {
            if (!spec.Written[k]) continue;
            if (at >= instrs.Count || instrs[at] is not { Op: Opcode.Call, Callee: MachineIntrinsics.AsmOut } taken
                || taken.Operands is not [ImmOperand { Value: var which }] || which != k)
            {
                Error("an asm block's results are not right after it");
                return true;
            }
            output[k] = taken.Dest;
            at++;
        }
        consumed = at - n;

        MMem?[] slot = new MMem?[count];
        List<MOperand> operands = new();
        int[] operandOf = new int[count];
        Array.Fill(operandOf, -1);
        for (int k = 0; k < count; k++)
        {
            if (!plan.InMemory[k]) continue;
            int width = spec.Locals[k].Width;
            slot[k] = MMem.Frame(_m.Frame.Allocate(width, Math.Max(8, width)));
            operandOf[k] = operands.Count;
            operands.Add(slot[k]!);
            if (plan.StoreBefore[k] && input[k] is { } value) StoreFromL(slot[k]!, value, width);
        }
        List<Gpr> uses = new();
        foreach ((int gpr, int k) in plan.Inputs)
        {
            if (input[k] is not { } value)
            {
                Error("an asm block's bound input has no value");
                return true;
            }
            int width = spec.Locals[k].Width == 8 ? 8 : 4;
            MovL(MReg.Of((Gpr)gpr), RML(value, width), width);
            uses.Add((Gpr)gpr);
        }
        InlineAsmCode code = new()
        {
            Analysis = analysis, Plan = plan, OperandOf = operandOf,
            Uses = uses.ToArray(), Defs = InlineAsmCode.Registers(plan.Clobbered), XmmClobbers = analysis.XmmWritten,
        };
        Emit(new MInstr(MOp.InlineAsm, operands.ToArray()) { Asm = code });
        foreach ((int k, int gpr) in plan.Outputs)
            if (output[k] is { } d) MovL(V(d), MReg.Of((Gpr)gpr), spec.Locals[k].Width == 8 ? 8 : 4);
        for (int k = 0; k < count; k++)
            if (plan.LoadAfter[k] && output[k] is { } d)
                LoadIntoL(d, slot[k]!, spec.Locals[k].Width, spec.Locals[k].Kind == 's');
        return true;
    }
}
