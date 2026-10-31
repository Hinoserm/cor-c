#nullable enable
using Corsac.Asm;
using Corsac.Lang.Ir;
using Operand = Corsac.Lang.Ir.Operand;

namespace Corsac.Lang.X64;

/// <summary>
/// An asm block as an instruction (MOp.InlineAsm), long mode's: as i386's
/// (Corsac.Lang.X86.InlineAsmCode), its registers by their ids -- the
/// general ones 0-15, the XMM ones from MReg.XmmBase.
/// </summary>
public sealed class InlineAsmCode
{
    public required InlineAsm.Analysis Analysis { get; init; }
    public required InlineAsm.Plan Plan { get; init; }
    /// <summary>Per local: its slot's index among the instruction's operands, or -1 where it has none.</summary>
    public required int[] OperandOf { get; init; }
    /// <summary>The registers the head's bindings load, by id.</summary>
    public required int[] Uses { get; init; }
    /// <summary>The registers the block destroys, by id: general ones and XMM ones.</summary>
    public required int[] Defs { get; init; }
    /// <summary>Whether it needs the method's frame as it is (i386's InlineAsmCode.NeedsFrame).</summary>
    public bool NeedsFrame => Analysis.UsesStack || Plan.InMemory.Contains(true);

    private string? _text;
    private byte[]? _bytes;

    /// <summary>The block's text with its slots where they are now, and its bytes, assembled once a text.</summary>
    public (string Text, byte[] Bytes) Assembled(MInstr i)
    {
        string text = InlineAsm.Render(Analysis, Plan, l => Address((MMem)i.Operands[OperandOf[l]]));
        lock (this)
        {
            if (_text != text)
            {
                _bytes = InlineAsm.Assemble(text, 64, InlineAsm.Baseline(Target.Current.X86Profile, 64));
                _text = text;
            }
            return (text, _bytes!);
        }
    }

    private static readonly string[] Names64 =
    {
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
    };

    /// <summary>A frame slot as the assembler writes it: `[rbp - 12]`.</summary>
    private static string Address(MMem m)
    {
        string base_ = m.Base is { } b && b.Id < 16 ? Names64[b.Id] : throw new InvalidOperationException("an asm slot has no base register");
        return m.Disp < 0 ? $"[{base_} - {-(long)m.Disp}]" : $"[{base_} + {m.Disp}]";
    }
}

internal sealed partial class Selector
{
    /// <summary>
    /// AN ASM BLOCK and the AsmOuts right after it, as one -- i386's way
    /// (Corsac.Lang.X86's TryInlineAsm): the memory locals' slots filled,
    /// the head's bindings moved into their registers, the block, the
    /// tail's bindings moved out and the written slots read back.
    /// </summary>
    private bool TryInlineAsm(List<Instr> instrs, int n, out int consumed)
    {
        consumed = 0;
        Instr i = instrs[n];
        if (i.Op != Opcode.Call || i.Callee is not { } callee
            || !callee.StartsWith(Corsac.Lang.X86.MachineIntrinsics.Asm, StringComparison.Ordinal))
            return false;
        consumed = 1;
        InlineAsm.Spec spec = InlineAsm.Decode(callee[Corsac.Lang.X86.MachineIntrinsics.Asm.Length..]);
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
            if (at >= instrs.Count || instrs[at] is not { Op: Opcode.Call, Callee: Corsac.Lang.X86.MachineIntrinsics.AsmOut } taken
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
            if (plan.StoreBefore[k] && input[k] is { } value) StoreFrom(slot[k]!, value, width);
        }
        List<int> uses = new();
        foreach ((int gpr, int k) in plan.Inputs)
        {
            if (input[k] is not { } value)
            {
                Error("an asm block's bound input has no value");
                return true;
            }
            int width = spec.Locals[k].Width == 8 ? 8 : 4;
            Mov(MReg.Of((Gpr)gpr), RM(value, width), width);
            uses.Add(gpr);
        }
        List<int> defs = new();
        for (int g = 0; g < 16; g++) if ((plan.Clobbered & (1u << g)) != 0) defs.Add(g);
        for (int x = 0; x < 16; x++) if ((analysis.XmmWritten & (1u << x)) != 0) defs.Add(MReg.XmmBase + x);
        InlineAsmCode code = new()
        {
            Analysis = analysis, Plan = plan, OperandOf = operandOf, Uses = uses.ToArray(), Defs = defs.ToArray(),
        };
        Emit(new MInstr(MOp.InlineAsm, operands.ToArray()) { Asm = code });
        foreach ((int k, int gpr) in plan.Outputs)
            if (output[k] is { } d) Mov(V(d), MReg.Of((Gpr)gpr), spec.Locals[k].Width == 8 ? 8 : 4);
        for (int k = 0; k < count; k++)
            if (plan.LoadAfter[k] && output[k] is { } d)
                LoadInto(d, slot[k]!, spec.Locals[k].Width, spec.Locals[k].Kind == 's');
        return true;
    }
}
