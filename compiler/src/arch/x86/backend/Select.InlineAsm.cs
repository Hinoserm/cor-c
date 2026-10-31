#nullable enable
using Corsac.Asm;
using Corsac.Lang.Ir;
using Operand = Corsac.Lang.Ir.Operand;

namespace Corsac.Lang.X86;

/// <summary>
/// An asm block as an instruction (MOp.InlineAsm): the block read and
/// planned (InlineAsm.Analyze, InlineAsm.Bind), where each local's slot is
/// among the instruction's operands, and the registers the allocator sees
/// it read and write.
/// </summary>
public sealed class InlineAsmCode
{
    public required InlineAsm.Analysis Analysis { get; init; }
    public required InlineAsm.Plan Plan { get; init; }
    /// <summary>Per local: its slot's index among the instruction's operands, or -1 where it has none.</summary>
    public required int[] OperandOf { get; init; }
    /// <summary>The registers the head's bindings load (read by the block).</summary>
    public required Gpr[] Uses { get; init; }
    /// <summary>The registers the block destroys.</summary>
    public required Gpr[] Defs { get; init; }
    /// <summary>The XMM registers it writes, by number.</summary>
    public required uint XmmClobbers { get; init; }

    private string? _text;
    private byte[]? _bytes;

    /// <summary>
    /// The block's text with its slots where they are now (FrameCompact and
    /// FramePointerFree may have moved them since selection), and its bytes,
    /// assembled once a text.
    /// </summary>
    public (string Text, byte[] Bytes) Assembled(MInstr i)
    {
        string text = InlineAsm.Render(Analysis, Plan, l => Address((MMem)i.Operands[OperandOf[l]]));
        lock (this)
        {
            if (_text != text)
            {
                _bytes = InlineAsm.Assemble(text, 32, InlineAsm.Baseline(Target.Current.X86Profile, 32));
                _text = text;
            }
            return (text, _bytes!);
        }
    }

    /// <summary>A frame slot as the assembler writes it: `[ebp - 12]`.</summary>
    private static string Address(MMem m)
    {
        string base_ = m.Base is { } b ? Operands32[b.Id] : throw new InvalidOperationException("an asm slot has no base register");
        return m.Disp < 0 ? $"[{base_} - {-(long)m.Disp}]" : $"[{base_} + {m.Disp}]";
    }

    private static readonly string[] Operands32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };

    public static Gpr[] Registers(uint mask)
    {
        List<Gpr> r = new();
        for (int g = 0; g < 8; g++) if ((mask & (1u << g)) != 0) r.Add((Gpr)g);
        return r.ToArray();
    }
}

internal sealed partial class Selector
{
    /// <summary>
    /// AN ASM BLOCK and the AsmOuts right after it (MachineIntrinsics.Asm),
    /// as one: its plan (InlineAsm.Bind) carried out --
    ///
    ///   - each local named as memory given a frame slot, filled before the
    ///     block where the block reads it;
    ///   - each head binding a move of its local into its register, which
    ///     the allocator coalesces (the local is often already there);
    ///   - the block itself (MOp.InlineAsm), its slots its operands, reading
    ///     the bound registers and destroying those it writes;
    ///   - each tail binding a move out of its register into the local's
    ///     AsmOut, and each local the block wrote in its slot read back.
    /// </summary>
    private bool TryInlineAsm(List<Instr> instrs, int n, out int consumed)
    {
        consumed = 0;
        Instr i = instrs[n];
        if (i.Op != Opcode.Call || i.Callee is not { } callee || !callee.StartsWith(MachineIntrinsics.Asm, StringComparison.Ordinal))
            return false;
        consumed = 1;
        InlineAsm.Spec spec = InlineAsm.Decode(callee[MachineIntrinsics.Asm.Length..]);
        if (spec.Bits != 32)
        {
            Error($"an asm block written for {spec.Bits}-bit code, in 32-bit code");
            return true;
        }
        InlineAsm.Analysis analysis;
        InlineAsm.Plan plan;
        try
        {
            analysis = InlineAsm.Analyze(spec.Text, 32, spec.Locals);
            plan = InlineAsm.Bind(analysis);
        }
        catch (AsmException e)
        {
            Error($"asm block (line {e.Line}): {e.Message}");
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

        // The slots, and what goes into them.
        MMem?[] slot = new MMem?[count];
        List<MOperand> operands = new();
        int[] operandOf = new int[count];
        Array.Fill(operandOf, -1);
        for (int k = 0; k < count; k++)
        {
            if (!plan.InMemory[k]) continue;
            int width = spec.Locals[k].Width;
            slot[k] = MMem.Frame(_m.Frame.Allocate(width, Math.Max(4, width)));
            operandOf[k] = operands.Count;
            operands.Add(slot[k]!);
            if (plan.StoreBefore[k] && input[k] is { } value) StoreFrom(slot[k]!, value, width);
        }
        uint uses = 0;
        foreach ((int gpr, int k) in plan.Inputs)
        {
            if (input[k] is not { } value)
            {
                Error("an asm block's bound input has no value");
                return true;
            }
            Mov(MReg.Of((Gpr)gpr), RM(value));
            uses |= 1u << gpr;
        }
        InlineAsmCode code = new()
        {
            Analysis = analysis, Plan = plan, OperandOf = operandOf,
            Uses = InlineAsmCode.Registers(uses), Defs = InlineAsmCode.Registers(plan.Clobbered),
            XmmClobbers = analysis.XmmWritten,
        };
        Emit(new MInstr(MOp.InlineAsm, operands.ToArray()) { Asm = code, Probed = true });
        foreach ((int k, int gpr) in plan.Outputs)
            if (output[k] is { } d) Mov(Lo(d), MReg.Of((Gpr)gpr));
        for (int k = 0; k < count; k++)
            if (plan.LoadAfter[k] && output[k] is { } d)
                LoadInto(d, slot[k]!, spec.Locals[k].Width, spec.Locals[k].Kind == 's');
        return true;
    }

    /// <summary>Whether an IR instruction is an asm block that runs float or packed code (the x87's, MMX's or SSE's).</summary>
    internal static bool AsmUsesFloatState(Instr i)
        => i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.Asm, StringComparison.Ordinal)
           && FloatStateOf(c);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FloatState = new(StringComparer.Ordinal);

    private static bool FloatStateOf(string callee) => FloatState.GetOrAdd(callee, c =>
    {
        try
        {
            InlineAsm.Spec spec = InlineAsm.Decode(c[MachineIntrinsics.Asm.Length..]);
            return InlineAsm.Analyze(spec.Text, spec.Bits, spec.Locals).UsesFloatState;
        }
        catch (AsmException)
        {
            return true;
        }
    });
}
