#nullable enable
using System.Text;
using Corsac.Lang.X86;

namespace Corsac.Asm;

/// <summary>What an instruction does to one of its operands: reads it, writes it, both, or only takes its address.</summary>
[Flags]
public enum AsmRole : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
    /// <summary>The operand is an address the instruction uses, not a value: LEA's source, INVLPG's, a prefetch's.</summary>
    Address = 4,
}

/// <summary>
/// A local an asm block names, as the block sees it: its name, its width in
/// bytes, and its kind -- 'u' an unsigned integer or address, 's' a signed
/// integer, 'f' a float or double.
/// </summary>
public readonly record struct AsmLocal(string Name, int Width, char Kind)
{
    public bool IsFloat => Kind == 'f';
    public string SizeWord => Width switch { 1 => "byte", 2 => "word", 4 => "dword", 8 => "qword", _ => "oword" };
}

/// <summary>One statement of a block: its labels, and an instruction or a `.runtime` line.</summary>
public sealed class AsmStatement
{
    /// <summary>The statement's line in the block's text, from 1.</summary>
    public int Line;
    public List<string> Labels = new();
    /// <summary>The whole `.runtime ...` line, where the statement is one; null for an instruction.</summary>
    public string? Directive;
    /// <summary>rep, repe, repz, repne, repnz or lock, or "".</summary>
    public string Prefix = "";
    public string Mnemonic = "";
    public List<string> Operands = new();
    /// <summary>Per operand: the local it names whole, or -1 (Analyze fills it).</summary>
    public int[] Local = Array.Empty<int>();
    /// <summary>Per operand: what the instruction does with it (Analyze fills it).</summary>
    public AsmRole[] Roles = Array.Empty<AsmRole>();
    /// <summary>Per operand: the size keyword written before a local's name (`dword x`), or null.</summary>
    public string?[] SizeWords = Array.Empty<string?>();
    /// <summary>What the instruction does (Analyze), and its operands as parsed -- a local's as memory of its width.</summary>
    internal InlineAsm.Facts? Fact;
    internal Operand[] Parsed = Array.Empty<Operand>();

    public bool IsInstruction => Directive is null;
}

/// <summary>An identifier a block's operand spells that is not a register, a keyword or one of its labels: a local, a parameter or a constant of the method.</summary>
public readonly record struct AsmName(string Name, int Statement, int Operand, bool Whole, int Line);

/// <summary>
/// INLINE ASSEMBLY: an `asm { ... }` block of a COR-C# method, one of the few
/// departures from C# the language allows (CONTRACT.md section 9, step 8).
/// MSVC's way, not GCC's: the method's locals are named in the
/// instructions themselves, and no list says what goes in, what comes out
/// or what is destroyed -- this assembler reads every instruction, so the
/// compiler works all of that out (Analyze):
///
///   - a local an instruction reads before the block writes it on every
///     path is an input; one the block writes is an output;
///   - every register the block writes -- by name or as CPUID writes EBX --
///     is clobbered, and the register allocator keeps nothing there;
///   - an instruction newer than the 386 needs the block's own `.runtime`
///     (an instruction or a CPUID feature bit, never a processor), whatever
///     --cpu says (the baseline inside a block is the 386's, with the
///     x87 where the target has one).
///
/// The rules the block is held to, every path through it checked
/// (Analyze): no jump out of the block, no call, no return; ESP left where
/// it was found (PUSH and POP pair up) and EBP never written; the x87 stack
/// as deep at the end as at the start; EMMS after MMX before the end, and
/// no x87 instruction between MMX and its EMMS; the direction flag clear at
/// the end; a local's width is the register's it meets (`mov eax, x` with
/// a ushort x is an error, not an extension); no object reference named,
/// which the collector could not follow into a register.
///
/// BLOCKS ARE OPTIMISED, not chains of moves (Plan): a `mov reg, local` at
/// the block's head and a `mov local, reg` at its tail are not
/// instructions but bindings -- the local goes into that register before
/// the block and is taken from it after, as the register allocator
/// coalesces it -- so `mov eax, leaf ... cpuid ... mov a, eax` loads the
/// leaf straight into EAX and stores EAX straight where `a` goes. A local
/// named anywhere else is a memory operand: its frame slot, filled before
/// the block and read back after.
/// </summary>
public static class InlineAsm
{
    // ---- reading the text ------------------------------------------------------

    /// <summary>The words an operand spells that are neither names nor registers.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "byte", "word", "dword", "qword", "tword", "tbyte", "oword", "xmmword", "dqword", "ptr", "short", "near", "far", "rip",
    };

    private static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "rep", "repe", "repz", "repne", "repnz", "lock" };

    /// <summary>The instructions whose operand is a label: every branch the block may take, to a place of its own.</summary>
    internal static bool IsBranch(string mn)
        => mn is "jmp" or "loop" or "loope" or "loopz" or "loopne" or "loopnz" or "jcxz" or "jecxz" or "jrcxz" or "call"
           || mn.Length > 1 && mn[0] == 'j' && ConditionName(mn[1..]);

    private static bool ConditionName(string cc) => cc is "o" or "no" or "b" or "c" or "nae" or "ae" or "nb" or "nc" or "e" or "z"
        or "ne" or "nz" or "be" or "na" or "a" or "nbe" or "s" or "ns" or "p" or "pe" or "np" or "po" or "l" or "nge"
        or "ge" or "nl" or "le" or "ng" or "g" or "nle";

    /// <summary>
    /// The block's statements: one to a line, each line's labels first, a
    /// `;` comment to the end of the line. Every name an operand spells
    /// that is not a register, a size word or one of the block's labels is
    /// listed (Names), for the compiler to find as a local, a parameter or a
    /// constant. Throws AsmException with the line, from 1.
    /// </summary>
    public static (List<AsmStatement> Statements, List<string> EndLabels, List<AsmName> Names) Scan(string text, int bits)
    {
        List<AsmStatement> statements = new();
        List<string> pending = new();
        HashSet<string> labels = new(StringComparer.Ordinal);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            int line = l + 1;
            string rest = X86Assembler.StripCommentText(lines[l]).Trim();
            while (rest.Length > 0)
            {
                int colon = X86Assembler.LabelEndText(rest);
                if (colon < 0) break;
                string label = rest[..colon].Trim();
                if (!X86Assembler.IsNameText(label) || label.StartsWith('.'))
                    throw new AsmException("<asm>", line, $"'{label}' is not a label name");
                if (!labels.Add(label)) throw new AsmException("<asm>", line, $"'{label}' is already a label of this block");
                if (Operands.IsRegisterName(label.ToLowerInvariant()))
                    throw new AsmException("<asm>", line, $"'{label}' is a register, not a label name");
                pending.Add(label);
                rest = rest[(colon + 1)..].Trim();
            }
            if (rest.Length == 0) continue;
            AsmStatement s = new() { Line = line, Labels = pending };
            pending = new List<string>();
            string head = X86Assembler.HeadText(rest);
            string lower = head.ToLowerInvariant();
            if (lower.StartsWith('.'))
            {
                if (lower != ".runtime")
                    throw new AsmException("<asm>", line, $"'{head}': the one directive an asm block takes is .runtime -- a block is code, not data or layout");
                s.Directive = rest;
                statements.Add(s);
                continue;
            }
            string operands = rest[head.Length..].Trim();
            if (Prefixes.Contains(lower))
            {
                s.Prefix = lower;
                if (operands.Length == 0) throw new AsmException("<asm>", line, $"{lower} has to be followed by an instruction");
                head = X86Assembler.HeadText(operands);
                lower = head.ToLowerInvariant();
                operands = operands[head.Length..].Trim();
            }
            s.Mnemonic = lower;
            s.Operands = X86Assembler.SplitText(operands).Select(o => o.Trim()).ToList();
            statements.Add(s);
        }

        // The names: what is left of every operand once the registers, the
        // keywords, the numbers, the quoted text and the labels are out.
        List<AsmName> names = new();
        for (int k = 0; k < statements.Count; k++)
        {
            AsmStatement s = statements[k];
            if (!s.IsInstruction) continue;
            bool branch = IsBranch(s.Mnemonic);
            for (int o = 0; o < s.Operands.Count; o++)
            {
                string operand = s.Operands[o];
                if (branch)
                {
                    // A branch's target is a label of the block, or the branch
                    // leaves it (Analyze refuses that, not this).
                    continue;
                }
                foreach ((string word, int at) in Words(operand))
                {
                    if (labels.Contains(word)) continue;
                    string w = word.ToLowerInvariant();
                    if (Keywords.Contains(w) || Operands.IsRegisterName(w) || w is "st") continue;
                    names.Add(new AsmName(word, k, o, Whole(operand, word), s.Line));
                }
            }
        }
        return (statements, pending, names);
    }

    /// <summary>Whether an operand is the name alone, perhaps behind a size word: `x`, `dword x`, `dword ptr x`.</summary>
    private static bool Whole(string operand, string name)
    {
        string rest = operand.Trim();
        string first = X86Assembler.HeadText(rest).ToLowerInvariant();
        if (first is "byte" or "word" or "dword" or "qword" or "tword" or "tbyte" or "oword" or "xmmword" or "dqword")
        {
            rest = rest[first.Length..].Trim();
            if (X86Assembler.HeadText(rest).ToLowerInvariant() == "ptr") rest = rest[3..].Trim();
        }
        return rest == name;
    }

    /// <summary>The size word in front of a whole local, or null.</summary>
    private static string? SizeWordOf(string operand)
    {
        string first = X86Assembler.HeadText(operand.Trim()).ToLowerInvariant();
        return first is "byte" or "word" or "dword" or "qword" or "tword" or "tbyte" or "oword" or "xmmword" or "dqword" ? first : null;
    }

    /// <summary>The identifiers of an operand, with their offsets: no number's digits, no quoted text, nothing after a `$`.</summary>
    internal static IEnumerable<(string Word, int At)> Words(string operand)
    {
        int i = 0;
        while (i < operand.Length)
        {
            char c = operand[i];
            if (c is '\'' or '"')
            {
                char quote = c;
                i++;
                while (i < operand.Length && operand[i] != quote) { if (operand[i] == '\\') i++; i++; }
                i++;
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < operand.Length && (char.IsLetterOrDigit(operand[i]) || operand[i] == '_')) i++;
                continue;
            }
            if (c == '$')
            {
                while (i < operand.Length && (operand[i] == '$' || char.IsLetterOrDigit(operand[i]) || operand[i] == '_')) i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < operand.Length && (char.IsLetterOrDigit(operand[i]) || operand[i] == '_')) i++;
                // `st(1)` gives `st`, the x87 register, which Scan passes over.
                yield return (operand[start..i], start);
                continue;
            }
            i++;
        }
    }

    /// <summary>
    /// The text with every name in `values` replaced where it stands by the
    /// value -- the constants a block names, written as numbers -- and its
    /// lines kept where they were, so a later message's line is still the
    /// source's.
    /// </summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return text;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            string line = lines[l];
            string code = X86Assembler.StripCommentText(line);
            // The labels stay as they are; only what follows them is rewritten.
            int from = 0;
            string rest = code;
            while (true)
            {
                int colon = X86Assembler.LabelEndText(rest.TrimStart());
                if (colon < 0) break;
                int lead = rest.Length - rest.TrimStart().Length;
                from += lead + colon + 1;
                rest = code[from..];
            }
            string head = X86Assembler.HeadText(rest.TrimStart());
            int bodyAt = from + (rest.Length - rest.TrimStart().Length) + head.Length;
            if (Prefixes.Contains(head.ToLowerInvariant()))
            {
                string after = code[bodyAt..];
                string inner = X86Assembler.HeadText(after.TrimStart());
                bodyAt += (after.Length - after.TrimStart().Length) + inner.Length;
            }
            StringBuilder b = new(code[..bodyAt]);
            string body = code[bodyAt..];
            int last = 0;
            foreach ((string word, int at) in Words(body))
            {
                if (!values.TryGetValue(word, out string? value)) continue;
                b.Append(body, last, at - last).Append(value);
                last = at + word.Length;
            }
            b.Append(body, last, body.Length - last);
            lines[l] = b.ToString();
        }
        return string.Join("\n", lines);
    }

    // ---- the block in the IR ----------------------------------------------------

    /// <summary>A block as the IR carries it (MachineIntrinsics.Asm): the target's bits, its locals and their parts, its text.</summary>
    public sealed record Spec(int Bits, AsmLocal[] Locals, bool[] Read, bool[] Written, string Text);

    /// <summary>`bits;name:4sr,name:8fw,...` and a newline, then the text: a name is an identifier, which holds none of `;:,` or a newline.</summary>
    public static string Encode(Spec spec)
    {
        StringBuilder b = new();
        b.Append(spec.Bits).Append(';');
        for (int k = 0; k < spec.Locals.Length; k++)
        {
            if (k > 0) b.Append(',');
            AsmLocal l = spec.Locals[k];
            b.Append(l.Name).Append(':').Append(l.Width).Append(l.Kind);
            if (spec.Read[k]) b.Append('r');
            if (spec.Written[k]) b.Append('w');
        }
        return b.Append('\n').Append(spec.Text).ToString();
    }

    public static Spec Decode(string encoded)
    {
        int newline = encoded.IndexOf('\n');
        string head = newline < 0 ? encoded : encoded[..newline];
        string text = newline < 0 ? "" : encoded[(newline + 1)..];
        int semi = head.IndexOf(';');
        int bits = int.Parse(head[..semi], System.Globalization.CultureInfo.InvariantCulture);
        string[] parts = head[(semi + 1)..].Length == 0 ? Array.Empty<string>() : head[(semi + 1)..].Split(',');
        AsmLocal[] locals = new AsmLocal[parts.Length];
        bool[] read = new bool[parts.Length], written = new bool[parts.Length];
        for (int k = 0; k < parts.Length; k++)
        {
            string part = parts[k];
            int colon = part.IndexOf(':');
            string shape = part[(colon + 1)..];
            int digits = 0;
            while (digits < shape.Length && char.IsDigit(shape[digits])) digits++;
            int width = int.Parse(shape[..digits], System.Globalization.CultureInfo.InvariantCulture);
            locals[k] = new AsmLocal(part[..colon], width, shape[digits]);
            read[k] = shape.IndexOf('r', digits + 1) >= 0;
            written[k] = shape.IndexOf('w', digits + 1) >= 0;
        }
        return new Spec(bits, locals, read, written, text);
    }

    /// <summary>A local's IR type: a word or less is I32, eight bytes of integer I64 (a long, or an address in long mode), floats their own.</summary>
    public static Corsac.Lang.Ir.IrType IrTypeOf(AsmLocal local)
        => local.Kind == 'f' ? (local.Width == 4 ? Corsac.Lang.Ir.IrType.F32 : Corsac.Lang.Ir.IrType.F64)
         : local.Width == 8 ? Corsac.Lang.Ir.IrType.I64 : Corsac.Lang.Ir.IrType.I32;

    // ---- what each instruction does -------------------------------------------

    /// <summary>What one instruction does, as Analyze needs it.</summary>
    internal sealed class Facts
    {
        public AsmRole[] Roles = Array.Empty<AsmRole>();
        /// <summary>The general registers (hardware numbers 0-15) read and written without being named.</summary>
        public uint ImplicitRead, ImplicitWritten;
        /// <summary>The x87 stack's change in depth; ResetsX87: FNINIT and its kin leave it empty.</summary>
        public int X87;
        public bool ResetsX87, UsesX87, UsesMmx, EmptiesMmx, UsesSse, SetsDf, ClearsDf;
        /// <summary>What ESP moves by: a push's -size, a pop's +size.</summary>
        public int Esp;
        /// <summary>0 no branch, 1 a conditional one, 2 an unconditional one; the label is the first operand.</summary>
        public int Branch;
        /// <summary>Why the block may not have this instruction, or null.</summary>
        public string? Refused;
    }

    private static uint G(params int[] regs) { uint m = 0; foreach (int r in regs) m |= 1u << r; return m; }
    private const int Eax = 0, Ecx = 1, Edx = 2, Ebx = 3, Esp = 4, Ebp = 5, Esi = 6, Edi = 7;

    private static readonly HashSet<string> Alu = new(StringComparer.Ordinal) { "add", "sub", "adc", "sbb", "and", "or", "xor" };
    private static readonly HashSet<string> StringInstructions = new(StringComparer.Ordinal)
    {
        "movsb", "movsw", "movsd", "movsq", "stosb", "stosw", "stosd", "stosq", "lodsb", "lodsw", "lodsd", "lodsq",
        "scasb", "scasw", "scasd", "scasq", "cmpsb", "cmpsw", "cmpsd", "cmpsq", "insb", "insw", "insd", "outsb", "outsw", "outsd",
    };
    private static readonly HashSet<string> Shifts = new(StringComparer.Ordinal) { "shl", "sal", "shr", "sar", "rol", "ror", "rcl", "rcr" };
    private static readonly HashSet<string> NoEffect = new(StringComparer.Ordinal)
    {
        "nop", "hlt", "cli", "sti", "clc", "stc", "cmc", "wait", "fwait", "pause", "sfence", "lfence", "mfence",
        "wbinvd", "invd", "int3", "into",
    };

    // The x87's depth changes, by mnemonic: what each pushes (+) or pops (-).
    private static readonly Dictionary<string, int> X87Depth = new(StringComparer.Ordinal)
    {
        ["fld"] = 1, ["fild"] = 1, ["fbld"] = 1, ["fld1"] = 1, ["fldz"] = 1, ["fldpi"] = 1, ["fldl2t"] = 1, ["fldl2e"] = 1,
        ["fldlg2"] = 1, ["fldln2"] = 1, ["fsincos"] = 1, ["fptan"] = 1, ["fxtract"] = 1,
        ["fstp"] = -1, ["fistp"] = -1, ["fbstp"] = -1, ["faddp"] = -1, ["fsubp"] = -1, ["fsubrp"] = -1, ["fmulp"] = -1,
        ["fdivp"] = -1, ["fdivrp"] = -1, ["fcomp"] = -1, ["fucomp"] = -1, ["ficomp"] = -1, ["fcomip"] = -1, ["fucomip"] = -1,
        ["fpatan"] = -1, ["fyl2x"] = -1, ["fyl2xp1"] = -1,
        ["fcompp"] = -2, ["fucompp"] = -2,
    };

    // The x87 instructions that store to their memory operand; every other one with memory reads it.
    private static readonly HashSet<string> X87Stores = new(StringComparer.Ordinal)
    {
        "fst", "fstp", "fist", "fistp", "fbstp", "fnstcw", "fstcw", "fnstsw", "fstsw", "fnstenv", "fstenv", "fnsave", "fsave",
    };

    /// <summary>The facts of one instruction, its operands parsed (a local's as memory of its width).</summary>
    internal static Facts InstructionFacts(string prefix, string mn, Operand[] ops, int bits)
    {
        Facts f = new() { Roles = new AsmRole[ops.Length] };
        int n = ops.Length;
        void All(AsmRole first, AsmRole rest)
        {
            for (int k = 0; k < n; k++) f.Roles[k] = k == 0 ? first : rest;
        }
        int word = bits == 64 ? 8 : 4;
        // A rep or a lock's instruction: the prefix adds ECX for rep.
        if (prefix is "rep" or "repe" or "repz" or "repne" or "repnz")
        {
            f.ImplicitRead |= G(Ecx);
            f.ImplicitWritten |= G(Ecx);
        }

        // ---- control ----
        if (mn == "call") { f.Refused = "an asm block may not call: it is no safepoint, and a call is one"; return f; }
        if (mn is "ret" or "retn" or "retf" or "retfq" or "lretq" or "iret" or "iretd" or "iretq" or "enter" or "leave"
            or "sysenter" or "sysexit" or "sysret" or "sysretq" or "rsm")
        {
            f.Refused = $"an asm block may not leave the method or rebuild its frame ({mn})";
            return f;
        }
        if (IsBranch(mn))
        {
            f.Branch = mn == "jmp" ? 2 : 1;
            if (n != 1) { f.Refused = $"{mn} takes one label"; return f; }
            if (mn.StartsWith("loop", StringComparison.Ordinal)) { f.ImplicitRead |= G(Ecx); f.ImplicitWritten |= G(Ecx); }
            else if (mn is "jcxz" or "jecxz" or "jrcxz") f.ImplicitRead |= G(Ecx);
            f.Roles[0] = AsmRole.None;
            return f;
        }
        if (NoEffect.Contains(mn)) { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn == "cld") { f.ClearsDf = true; return f; }
        if (mn == "std") { f.SetsDf = true; return f; }
        if (mn == "int")
        {
            // A trap to a system call or a handler: it may read every
            // argument register, and answers in EAX.
            All(AsmRole.Read, AsmRole.Read);
            f.ImplicitRead |= G(Eax, Ecx, Edx, Ebx, Esi, Edi);
            f.ImplicitWritten |= G(Eax);
            return f;
        }
        if (mn == "syscall")
        {
            if (bits != 64) { f.Refused = "syscall is long mode's; use int in 32-bit code"; return f; }
            f.ImplicitRead |= G(Eax, Edi, Esi, Edx, 10, 8, 9);
            f.ImplicitWritten |= G(Eax, Ecx, 11);
            return f;
        }

        // ---- the stack ----
        if (mn == "push") { All(AsmRole.Read, AsmRole.Read); f.Esp = -(n == 1 && ops[0].Kind is OperandKind.Register or OperandKind.Memory && ops[0].Size != 0 ? ops[0].Size : word); return f; }
        if (mn == "pop") { All(AsmRole.Write, AsmRole.Write); f.Esp = n == 1 && ops[0].Size != 0 ? ops[0].Size : word; return f; }
        if (mn is "pushf" or "pushfd" or "pushfq" or "pushfw") { f.Esp = -(mn == "pushfw" ? 2 : word); return f; }
        if (mn is "popf" or "popfd" or "popfq" or "popfw") { f.Esp = mn == "popfw" ? 2 : word; f.ClearsDf = false; return f; }
        if (mn is "pusha" or "pushad" or "pushaw")
        {
            if (bits == 64) { f.Refused = $"{mn} is not a long-mode instruction"; return f; }
            f.ImplicitRead |= G(Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi);
            f.Esp = mn == "pushaw" ? -16 : -32;
            return f;
        }
        if (mn is "popa" or "popad" or "popaw")
        {
            if (bits == 64) { f.Refused = $"{mn} is not a long-mode instruction"; return f; }
            f.ImplicitWritten |= G(Eax, Ecx, Edx, Ebx, Ebp, Esi, Edi);
            f.Esp = mn == "popaw" ? 16 : 32;
            return f;
        }

        // ---- the integer instructions ----
        if (Alu.Contains(mn)) { All(AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (mn is "cmp" or "test" or "bt") { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn is "inc" or "dec" or "neg" or "not" or "bswap") { All(AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (Shifts.Contains(mn) || mn is "bts" or "btr" or "btc" or "shld" or "shrd") { All(AsmRole.ReadWrite, AsmRole.Read); return f; }
        // BSF and BSR leave the destination as it was for a zero source (AMD's word for it), so they read it.
        if (mn is "bsf" or "bsr") { All(AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (mn is "mov" or "movzx" or "movsx" or "movsxd" || mn.StartsWith("set", StringComparison.Ordinal) && ConditionName(mn[3..]))
        {
            All(AsmRole.Write, AsmRole.Read);
            return f;
        }
        if (mn.StartsWith("cmov", StringComparison.Ordinal) && ConditionName(mn[4..])) { All(AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (mn == "lea") { All(AsmRole.Write, AsmRole.Address); return f; }
        if (mn is "xchg" or "xadd") { All(AsmRole.ReadWrite, AsmRole.ReadWrite); return f; }
        if (mn == "cmpxchg") { All(AsmRole.ReadWrite, AsmRole.Read); f.ImplicitRead |= G(Eax); f.ImplicitWritten |= G(Eax); return f; }
        if (mn == "cmpxchg8b")
        {
            All(AsmRole.ReadWrite, AsmRole.Read);
            f.ImplicitRead |= G(Eax, Edx, Ebx, Ecx);
            f.ImplicitWritten |= G(Eax, Edx);
            return f;
        }
        if (mn is "mul" or "div" or "idiv" || mn == "imul" && n == 1)
        {
            All(AsmRole.Read, AsmRole.Read);
            bool bytes = n == 1 && ops[0].Size == 1;
            f.ImplicitRead |= mn is "div" or "idiv" && !bytes ? G(Eax, Edx) : G(Eax);
            f.ImplicitWritten |= bytes ? G(Eax) : G(Eax, Edx);
            return f;
        }
        if (mn == "imul") { All(n == 3 ? AsmRole.Write : AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (mn is "cbw" or "cwde" or "cdqe") { f.ImplicitRead |= G(Eax); f.ImplicitWritten |= G(Eax); return f; }
        if (mn is "cwd" or "cdq" or "cqo") { f.ImplicitRead |= G(Eax); f.ImplicitWritten |= G(Edx); return f; }
        if (mn == "lahf") { f.ImplicitRead |= G(Eax); f.ImplicitWritten |= G(Eax); return f; }
        if (mn == "sahf") { f.ImplicitRead |= G(Eax); return f; }
        if (mn == "cpuid") { f.ImplicitRead |= G(Eax, Ecx); f.ImplicitWritten |= G(Eax, Ebx, Ecx, Edx); return f; }
        if (mn == "rdtsc") { f.ImplicitWritten |= G(Eax, Edx); return f; }
        if (mn == "rdmsr") { f.ImplicitRead |= G(Ecx); f.ImplicitWritten |= G(Eax, Edx); return f; }
        if (mn == "wrmsr") { f.ImplicitRead |= G(Eax, Ecx, Edx); return f; }
        if (mn == "in") { All(AsmRole.Write, AsmRole.Read); return f; }
        if (mn == "out") { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn is "lgdt" or "lidt" or "invlpg" or "prefetch" or "prefetchw" or "prefetchnta" or "prefetcht0" or "prefetcht1"
            or "prefetcht2" or "clflush")
        {
            All(AsmRole.Address, AsmRole.Address);
            return f;
        }
        if (mn is "ltr" or "lldt") { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn is "str" or "sldt") { All(AsmRole.Write, AsmRole.Read); return f; }

        // The string instructions, by their names and with no operands (SSE2's
        // MOVSD and CMPSD have two): what each reads and steps, and REP's count.
        if (n == 0 && StringInstructions.Contains(mn))
        {
            switch (mn.StartsWith("ins", StringComparison.Ordinal) ? "ins" : mn[..4])
            {
                case "movs": f.ImplicitRead |= G(Esi, Edi); f.ImplicitWritten |= G(Esi, Edi); break;
                case "cmps": f.ImplicitRead |= G(Esi, Edi); f.ImplicitWritten |= G(Esi, Edi); break;
                case "stos": f.ImplicitRead |= G(Eax, Edi); f.ImplicitWritten |= G(Edi); break;
                case "lods": f.ImplicitRead |= G(Esi); f.ImplicitWritten |= G(Eax, Esi); break;
                case "scas": f.ImplicitRead |= G(Eax, Edi); f.ImplicitWritten |= G(Edi); break;
                case "ins": f.ImplicitRead |= G(Edx, Edi); f.ImplicitWritten |= G(Edi); break;
                case "outs": f.ImplicitRead |= G(Edx, Esi); f.ImplicitWritten |= G(Esi); break;
            }
            return f;
        }

        // ---- the x87 ----
        if (mn.StartsWith('f') && mn != "fxsave" && mn != "fxrstor")
        {
            f.UsesX87 = true;
            if (mn is "fincstp" or "fdecstp" or "ffree")
            {
                f.Refused = $"{mn} moves the x87 stack's top without its tags; the block's stack depth could not be checked";
                return f;
            }
            if (mn is "finit" or "fninit" or "fsave" or "fnsave" or "frstor") f.ResetsX87 = true;
            f.X87 = X87Depth.GetValueOrDefault(mn);
            // `fadd` and its kin bare: the popping form onto ST(1).
            if (n == 0 && mn is "fadd" or "fsub" or "fsubr" or "fmul" or "fdiv" or "fdivr") f.X87 = -1;
            for (int k = 0; k < n; k++)
            {
                f.Roles[k] = ops[k].Kind == OperandKind.Memory ? (X87Stores.Contains(mn) ? AsmRole.Write : AsmRole.Read)
                    : ops[k].Kind == OperandKind.Register ? (mn is "fnstsw" or "fstsw" ? AsmRole.Write : AsmRole.Read)
                    : AsmRole.None;
            }
            return f;
        }

        // ---- MMX, 3DNow!, SSE ----
        bool mmx = ops.Any(o => o.Kind == OperandKind.Mmx), xmm = ops.Any(o => o.Kind == OperandKind.Xmm);
        f.UsesMmx = mmx || mn == "maskmovq";
        f.UsesSse = xmm || mn is "ldmxcsr" or "stmxcsr" or "fxsave" or "fxrstor";
        if (mn is "emms" or "femms") { f.EmptiesMmx = true; return f; }
        if (mn is "ldmxcsr" or "fxrstor") { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn is "stmxcsr" or "fxsave") { All(AsmRole.Write, AsmRole.Write); return f; }
        if (mn is "maskmovq" or "maskmovdqu") { All(AsmRole.Read, AsmRole.Read); f.ImplicitRead |= G(Edi); return f; }
        // A move writes the destination whole, but these keep part of it.
        bool partial = mn is "movlps" or "movhps" or "movlpd" or "movhpd" or "movhlps" or "movlhps"
            || mn is "movss" or "movsd" && n == 2 && ops[0].Kind == OperandKind.Xmm && ops[1].Kind == OperandKind.Xmm;
        if (mn.StartsWith("mov", StringComparison.Ordinal) || mn is "pmovmskb" or "movmskps" or "movmskpd" or "pshufw" or "pshufd"
            or "pshufhw" or "pshuflw" or "pextrw" or "movq2dq" or "movdq2q")
        {
            All(partial && ops[0].Kind != OperandKind.Memory ? AsmRole.ReadWrite : AsmRole.Write, AsmRole.Read);
            return f;
        }
        if (mn.StartsWith("cvt", StringComparison.Ordinal))
        {
            // Into an XMM register's low part (a scalar, or two singles), the rest kept.
            bool keeps = mn is "cvtsi2ss" or "cvtsi2sd" or "cvtss2sd" or "cvtsd2ss" or "cvtpi2ps";
            All(keeps ? AsmRole.ReadWrite : AsmRole.Write, AsmRole.Read);
            return f;
        }
        if (mn is "comiss" or "ucomiss" or "comisd" or "ucomisd") { All(AsmRole.Read, AsmRole.Read); return f; }
        if (mn is "sqrtps" or "sqrtpd" or "rcpps" or "rsqrtps") { All(AsmRole.Write, AsmRole.Read); return f; }
        // Everything else -- the arithmetic, the logic, the compares, the
        // shuffles that read their destination, the packed integers --
        // reads its first operand and writes it.
        All(AsmRole.ReadWrite, AsmRole.Read);
        return f;
    }

    // ---- the block as a whole ---------------------------------------------------

    /// <summary>What Analyze found: each local's part, and what the block uses and destroys.</summary>
    public sealed class Analysis
    {
        public required List<AsmStatement> Statements { get; init; }
        public required List<string> EndLabels { get; init; }
        public required AsmLocal[] Locals { get; init; }
        public required int Bits { get; init; }
        /// <summary>Per local: the block needs its value (it reads it before writing it on some path, or writes it only on some).</summary>
        public required bool[] Read { get; init; }
        /// <summary>Per local: the block writes it.</summary>
        public required bool[] Written { get; init; }
        /// <summary>The general registers the block writes, named or not (hardware numbers 0-15), and those it reads.</summary>
        public uint GprWritten, GprRead;
        /// <summary>The XMM registers it writes and reads.</summary>
        public uint XmmWritten, XmmRead;
        public bool UsesX87, UsesMmx, UsesSse;
        /// <summary>Whether the block runs any float or packed code -- x87, MMX or SSE -- which the x87 facts and the packed registers care about.</summary>
        public bool UsesFloatState => UsesX87 || UsesMmx || UsesSse;
    }

    /// <summary>A general register's number (0-15) for an operand that is one, the high bytes AH..BH mapped to their words.</summary>
    internal static int GprNumber(Operand o)
    {
        if (o.Kind != OperandKind.Register) return -1;
        if (o.Size == 1 && !o.Rex8 && o.Reg is >= 4 and <= 7) return o.Reg - 4;
        return o.Reg & 15;
    }

    /// <summary>
    /// The block's facts and its rules (InlineAsm's summary), every path
    /// followed. `locals` are the names the block may name whole; any other
    /// name left in the text is an error. Throws AsmException with the
    /// statement's line.
    /// </summary>
    public static Analysis Analyze(string text, int bits, AsmLocal[] locals)
    {
        (List<AsmStatement> statements, List<string> endLabels, List<AsmName> names) = Scan(text, bits);
        Dictionary<string, int> localIndex = new(StringComparer.Ordinal);
        for (int k = 0; k < locals.Length; k++) localIndex[locals[k].Name] = k;
        if (locals.Length > 64) throw new AsmException("<asm>", 1, "an asm block names at most 64 locals");

        foreach (AsmStatement s in statements)
        {
            s.Local = new int[s.Operands.Count];
            s.SizeWords = new string?[s.Operands.Count];
            Array.Fill(s.Local, -1);
        }
        foreach (AsmName name in names)
        {
            if (!localIndex.TryGetValue(name.Name, out int k))
                throw new AsmException("<asm>", name.Line, $"'{name.Name}' is not a local, a parameter, a constant or a label of this block");
            if (!name.Whole)
                throw new AsmException("<asm>", name.Line, $"'{name.Name}' is a local; it must be a whole operand, not part of an address or an expression -- load it into a register first");
            AsmStatement s = statements[name.Statement];
            s.Local[name.Operand] = k;
            s.SizeWords[name.Operand] = SizeWordOf(s.Operands[name.Operand]);
            string? said = s.SizeWords[name.Operand];
            if (said is not null && said != locals[k].SizeWord && !(said is "tword" or "tbyte"))
                throw new AsmException("<asm>", name.Line, $"'{name.Name}' is {locals[k].Width * 8} bits; '{said}' says otherwise");
        }

        Dictionary<string, int> labelAt = new(StringComparer.Ordinal);
        for (int k = 0; k < statements.Count; k++)
            foreach (string label in statements[k].Labels) labelAt[label] = k;
        foreach (string label in endLabels) labelAt[label] = statements.Count;

        Facts[] facts = new Facts[statements.Count];
        Analysis a = new()
        {
            Statements = statements, EndLabels = endLabels, Locals = locals, Bits = bits,
            Read = new bool[locals.Length], Written = new bool[locals.Length],
        };
        int[] target = new int[statements.Count];
        for (int k = 0; k < statements.Count; k++)
        {
            AsmStatement s = statements[k];
            target[k] = -1;
            if (!s.IsInstruction) { facts[k] = new Facts(); continue; }
            Operand[] ops = new Operand[s.Operands.Count];
            bool branch = IsBranch(s.Mnemonic);
            for (int o = 0; o < ops.Length; o++)
            {
                if (branch)
                {
                    ops[o] = new Operand { Kind = OperandKind.Immediate, Text = s.Operands[o] };
                    continue;
                }
                string parsed = s.Local[o] >= 0 ? locals[s.Local[o]].SizeWord + " [0]" : s.Operands[o];
                ops[o] = Operands.Parse(parsed, bits, "<asm>", s.Line);
            }
            Facts f = InstructionFacts(s.Prefix, s.Mnemonic, ops, bits);
            if (f.Refused is not null) throw new AsmException("<asm>", s.Line, f.Refused);
            facts[k] = f;
            s.Roles = f.Roles;
            s.Fact = f;
            s.Parsed = ops;
            if (f.Branch != 0)
            {
                string label = s.Operands[0];
                string bare = label;
                foreach (string w in new[] { "short ", "near " })
                    if (bare.StartsWith(w, StringComparison.OrdinalIgnoreCase)) bare = bare[w.Length..].Trim();
                if (!labelAt.TryGetValue(bare, out int to))
                    throw new AsmException("<asm>", s.Line, $"'{label}' is not a label of this block: an asm block may not jump out of itself");
                target[k] = to;
            }
            // The registers it names, by what it does with them.
            for (int o = 0; o < ops.Length; o++)
            {
                Operand op = ops[o];
                AsmRole role = f.Roles.Length > o ? f.Roles[o] : AsmRole.Read;
                if (op.Kind == OperandKind.Memory)
                {
                    if (op.Base >= 0) a.GprRead |= 1u << (op.Base & 15);
                    if (op.Index >= 0) a.GprRead |= 1u << (op.Index & 15);
                    continue;
                }
                if (op.Kind == OperandKind.Register)
                {
                    int r = GprNumber(op);
                    if ((role & AsmRole.Read) != 0) a.GprRead |= 1u << r;
                    if ((role & AsmRole.Write) != 0)
                    {
                        if (r == Esp) throw new AsmException("<asm>", s.Line, "an asm block may not write ESP but by PUSH and POP, which must pair up");
                        if (r == Ebp) throw new AsmException("<asm>", s.Line, "an asm block may not write EBP: the method's frame is reached through it");
                        a.GprWritten |= 1u << r;
                    }
                }
                else if (op.Kind == OperandKind.Xmm)
                {
                    if ((role & AsmRole.Read) != 0) a.XmmRead |= 1u << op.Reg;
                    if ((role & AsmRole.Write) != 0) a.XmmWritten |= 1u << op.Reg;
                }
                else if (op.Kind == OperandKind.Segment && (role & AsmRole.Write) != 0 && op.Reg is 1 or 2)
                    throw new AsmException("<asm>", s.Line, "an asm block may not load CS or SS");
            }
            a.GprRead |= f.ImplicitRead;
            if ((f.ImplicitWritten & (1u << Ebp)) != 0)
                throw new AsmException("<asm>", s.Line, "an asm block may not write EBP: the method's frame is reached through it");
            a.GprWritten |= f.ImplicitWritten & ~(1u << Esp);
            a.UsesX87 |= f.UsesX87;
            a.UsesMmx |= f.UsesMmx || f.EmptiesMmx;
            a.UsesSse |= f.UsesSse;
        }

        // EVERY PATH: the x87's depth, ESP's, whether MMX is in use, the
        // direction flag, and which locals are written on every path so far.
        int count = statements.Count;
        int[] x87 = new int[count + 1], esp = new int[count + 1];
        bool[] mmx = new bool[count + 1], df = new bool[count + 1], seen = new bool[count + 1];
        ulong[] must = new ulong[count + 1];
        Stack<int> work = new();
        void Arrive(int at, int x, int e, bool m, bool d, ulong w, int line)
        {
            if (!seen[at])
            {
                seen[at] = true; x87[at] = x; esp[at] = e; mmx[at] = m; df[at] = d; must[at] = w;
                work.Push(at);
                return;
            }
            if (x87[at] != x) throw new AsmException("<asm>", line, $"the x87 stack is {x} deep on one path to here and {x87[at]} on another");
            if (esp[at] != e) throw new AsmException("<asm>", line, $"ESP is {e} bytes from where the block found it on one path to here and {esp[at]} on another");
            bool changed = false;
            if (m && !mmx[at]) { mmx[at] = true; changed = true; }
            if (d && !df[at]) { df[at] = true; changed = true; }
            if ((must[at] & w) != must[at]) { must[at] &= w; changed = true; }
            if (changed) work.Push(at);
        }
        Arrive(0, 0, 0, false, false, 0, statements.Count > 0 ? statements[0].Line : 1);
        while (work.Count > 0)
        {
            int at = work.Pop();
            if (at == count) continue;
            AsmStatement s = statements[at];
            Facts f = facts[at];
            int x = x87[at], e = esp[at];
            bool m = mmx[at], d = df[at];
            ulong w = must[at];
            if (s.IsInstruction)
            {
                if (f.UsesMmx && x != 0) throw new AsmException("<asm>", s.Line, "an MMX instruction with values on the x87 stack: the MMX registers are the x87's");
                if (f.UsesX87 && m) throw new AsmException("<asm>", s.Line, "an x87 instruction after MMX with no EMMS between: the MMX registers are the x87's");
                x = f.ResetsX87 ? 0 : x + f.X87;
                if (x < 0) throw new AsmException("<asm>", s.Line, "this pops more off the x87 stack than the block pushed");
                if (x > 8) throw new AsmException("<asm>", s.Line, "the x87 stack holds eight");
                e += f.Esp;
                if (e > 0) throw new AsmException("<asm>", s.Line, "this pops more off the stack than the block pushed");
                if (f.UsesMmx) m = true;
                if (f.EmptiesMmx) m = false;
                if (f.SetsDf) d = true;
                if (f.ClearsDf) d = false;
                for (int o = 0; o < s.Local.Length; o++)
                    if (s.Local[o] >= 0 && (s.Roles[o] & AsmRole.Write) != 0) w |= 1UL << s.Local[o];
            }
            if (f.Branch != 0) Arrive(target[at], x, e, m, d, w, s.Line);
            if (f.Branch != 2) Arrive(at + 1, x, e, m, d, w, s.Line);
        }

        int endLine = statements.Count > 0 ? statements[^1].Line : 1;
        if (seen[count])
        {
            if (x87[count] != 0) throw new AsmException("<asm>", endLine, $"the block ends with {x87[count]} more on the x87 stack than it found: it must leave the stack as it was");
            if (esp[count] != 0) throw new AsmException("<asm>", endLine, $"the block ends with ESP {-esp[count]} bytes below where it found it: every PUSH needs its POP");
            if (mmx[count]) throw new AsmException("<asm>", endLine, "the block ends with MMX in use: EMMS (or FEMMS) after the last MMX instruction, on every path");
            if (df[count]) throw new AsmException("<asm>", endLine, "the block ends with the direction flag set: CLD before the end, on every path");
        }

        // WHAT THE BLOCK NEEDS: a local read where it is not yet written on
        // every path, and one written only on some, whose value then stays.
        for (int k = 0; k < count; k++)
        {
            AsmStatement s = statements[k];
            if (!s.IsInstruction || !seen[k]) continue;
            for (int o = 0; o < s.Local.Length; o++)
            {
                int l = s.Local[o];
                if (l < 0) continue;
                AsmRole role = s.Roles[o];
                // An address taken may be read or written through: both.
                if ((role & AsmRole.Address) != 0) { a.Read[l] = true; a.Written[l] = true; continue; }
                if ((role & AsmRole.Read) != 0 && (must[k] & (1UL << l)) == 0) a.Read[l] = true;
                if ((role & AsmRole.Write) != 0) a.Written[l] = true;
            }
        }
        for (int l = 0; l < locals.Length; l++)
            if (a.Written[l] && (!seen[count] || (must[count] & (1UL << l)) == 0)) a.Read[l] = true;
        return a;
    }

    // ---- bindings, and the text assembled ----------------------------------------

    /// <summary>
    /// How a block meets its method (InlineAsm's summary: optimised, not a
    /// chain of moves): the head's `mov reg, local`s and the tail's `mov
    /// local, reg`s, which become register bindings and are not assembled,
    /// and the locals named anywhere else, which are memory operands.
    /// </summary>
    public sealed class Plan
    {
        /// <summary>The head's bindings: the register (0-15) and the local it is loaded with before the block.</summary>
        public required List<(int Gpr, int Local)> Inputs { get; init; }
        /// <summary>The tail's: the local and the register it is taken from after the block.</summary>
        public required List<(int Local, int Gpr)> Outputs { get; init; }
        /// <summary>The statements the bindings replace, not assembled.</summary>
        public required HashSet<int> Bound { get; init; }
        /// <summary>Per local: named by an instruction that stays, and so given a frame slot.</summary>
        public required bool[] InMemory { get; init; }
        /// <summary>Per local: its slot filled before the block.</summary>
        public required bool[] StoreBefore { get; init; }
        /// <summary>Per local: read back from its slot after the block.</summary>
        public required bool[] LoadAfter { get; init; }
        /// <summary>The general registers the block destroys: those it writes, and those a binding takes a local from.</summary>
        public required uint Clobbered { get; init; }
    }

    /// <summary>Whether a register may carry a binding: a general one, its low part as wide as the local, not the stack's or the frame's.</summary>
    private static bool Bindable(Operand reg, AsmLocal local, int bits, bool output)
    {
        if (reg.Kind != OperandKind.Register || local.IsFloat) return false;
        int r = GprNumber(reg);
        if (r is Esp or Ebp) return false;
        if (reg.Size == 1 && (reg.Reg is >= 4 and <= 7) && !reg.Rex8) return false; // AH..BH: not the low byte
        if (reg.Size != local.Width) return false;
        // Out, only a register written whole: a narrower local's value from
        // a byte or a word register would be its low part with what was
        // above it still there.
        if (output) return reg.Size == 4 || reg.Size == 8 && bits == 64;
        return true;
    }

    public static Plan Bind(Analysis a)
    {
        List<AsmStatement> st = a.Statements;
        int bits = a.Bits;
        List<(int Gpr, int Local)> inputs = new();
        List<(int Local, int Gpr)> outputs = new();
        HashSet<int> bound = new();
        uint usedIn = 0;
        // The head: `mov reg, local` from the first statement on, each to a
        // register not taken yet, until anything else or a label.
        for (int k = 0; k < st.Count; k++)
        {
            AsmStatement s = st[k];
            if (s.Labels.Count > 0) break;
            if (!s.IsInstruction) continue;
            if (s.Mnemonic != "mov" || s.Prefix != "" || s.Operands.Count != 2 || s.Local[0] >= 0 || s.Local[1] < 0) break;
            Operand reg = Operands.Parse(s.Operands[0], bits, "<asm>", s.Line);
            AsmLocal local = a.Locals[s.Local[1]];
            if (!Bindable(reg, local, bits, output: false)) break;
            int r = GprNumber(reg);
            if ((usedIn & (1u << r)) != 0) break;
            usedIn |= 1u << r;
            inputs.Add((r, s.Local[1]));
            bound.Add(k);
        }
        // The tail: `mov local, reg` back from the last statement, each into
        // a local not taken yet, with no label at or after it -- every path
        // to the end then runs them.
        uint tailRegs = 0;
        HashSet<int> takenOut = new();
        if (a.EndLabels.Count == 0)
        {
            for (int k = st.Count - 1; k >= 0; k--)
            {
                AsmStatement s = st[k];
                // A labelled statement is a jump's target: a path reaches the
                // end through it without what comes before it.
                if (bound.Contains(k) || s.Labels.Count > 0) break;
                if (!s.IsInstruction) continue;
                if (s.Mnemonic != "mov" || s.Prefix != "" || s.Operands.Count != 2 || s.Local[0] < 0 || s.Local[1] >= 0) break;
                Operand reg = Operands.Parse(s.Operands[1], bits, "<asm>", s.Line);
                AsmLocal local = a.Locals[s.Local[0]];
                if (!Bindable(reg, local, bits, output: true) || takenOut.Contains(s.Local[0])) break;
                takenOut.Add(s.Local[0]);
                int r = GprNumber(reg);
                outputs.Insert(0, (s.Local[0], r));
                tailRegs |= 1u << r;
                bound.Add(k);
            }
        }

        bool[] inMemory = new bool[a.Locals.Length], store = new bool[a.Locals.Length], load = new bool[a.Locals.Length];
        bool[] writtenKept = new bool[a.Locals.Length];
        for (int k = 0; k < st.Count; k++)
        {
            if (bound.Contains(k) || !st[k].IsInstruction) continue;
            AsmStatement s = st[k];
            for (int o = 0; o < s.Local.Length; o++)
            {
                int l = s.Local[o];
                if (l < 0) continue;
                inMemory[l] = true;
                if ((s.Roles[o] & (AsmRole.Write | AsmRole.Address)) != 0) writtenKept[l] = true;
            }
        }
        for (int l = 0; l < a.Locals.Length; l++)
        {
            store[l] = inMemory[l] && a.Read[l];
            load[l] = inMemory[l] && writtenKept[l] && !takenOut.Contains(l);
        }
        // WHAT THE INSTRUCTIONS KEPT WRITE: a head binding's own `mov eax, x`
        // is the move before the block, which the allocator sees as such.
        uint written = tailRegs;
        for (int k = 0; k < st.Count; k++)
        {
            if (bound.Contains(k) || !st[k].IsInstruction || st[k].Fact is not { } f) continue;
            written |= f.ImplicitWritten;
            for (int o = 0; o < st[k].Parsed.Length; o++)
                if (st[k].Parsed[o].Kind == OperandKind.Register && o < f.Roles.Length && (f.Roles[o] & AsmRole.Write) != 0)
                    written |= 1u << GprNumber(st[k].Parsed[o]);
        }
        uint clobbered = written & ~((1u << Esp) | (1u << Ebp));
        return new Plan
        {
            Inputs = inputs, Outputs = outputs, Bound = bound, InMemory = inMemory, StoreBefore = store, LoadAfter = load,
            Clobbered = clobbered,
        };
    }

    /// <summary>
    /// The text to assemble: the statements the plan keeps, on their own
    /// lines, each local named by `memory(local)` -- its slot, `[ebp - 12]`
    /// -- behind its size word.
    /// </summary>
    public static string Render(Analysis a, Plan? plan, Func<int, string> memory)
    {
        StringBuilder b = new();
        int line = 1;
        for (int k = 0; k < a.Statements.Count; k++)
        {
            AsmStatement s = a.Statements[k];
            while (line < s.Line) { b.Append('\n'); line++; }
            foreach (string label in s.Labels) b.Append(label).Append(": ");
            if (plan is not null && plan.Bound.Contains(k)) continue;
            if (!s.IsInstruction) { b.Append(s.Directive); continue; }
            if (s.Prefix.Length > 0) b.Append(s.Prefix).Append(' ');
            b.Append(s.Mnemonic);
            for (int o = 0; o < s.Operands.Count; o++)
            {
                b.Append(o == 0 ? " " : ", ");
                int l = s.Local[o];
                if (l < 0) { b.Append(s.Operands[o]); continue; }
                b.Append(a.Locals[l].SizeWord).Append(' ').Append(memory(l));
            }
        }
        if (a.EndLabels.Count > 0)
        {
            b.Append('\n');
            foreach (string label in a.EndLabels) b.Append(label).Append(": ");
        }
        return b.ToString();
    }

    /// <summary>
    /// The processor a block is assembled for: the 386 -- with an x87 where
    /// the target's profile has one -- and nothing newer unless its own
    /// `.runtime` says so; long mode's own for a 64-bit block.
    /// </summary>
    public static X86Cpu Baseline(X86Cpu profile, int bits)
        => bits == 64 ? profile : X86Cpu.Parse(new[] { "--cpu=386", "--fpu=" + (profile.Fpu == "none" ? "none" : "387") });

    /// <summary>
    /// The block's bytes, as they go into the method wherever it lands: it is
    /// assembled at two origins, and bytes that differ mean an absolute
    /// address -- a label's, which moves with the code -- which is refused.
    /// Throws AsmException with the line in the rendered text.
    /// </summary>
    public static byte[] Assemble(string rendered, int bits, X86Cpu baseline)
    {
        X86Assembler.Result low, high;
        try
        {
            // `.base` on the line before the block's first, which every line
            // number then counts: the message's is put back.
            low = X86Assembler.Assemble(".base 0x1000\n" + rendered, "<asm>", bits: bits, cpu: baseline);
            high = X86Assembler.Assemble(".base 0x31415000\n" + rendered, "<asm>", bits: bits, cpu: baseline);
        }
        catch (AsmException e)
        {
            throw new AsmException("<asm>", Math.Max(1, e.Line - 1), e.Message);
        }
        if (!low.Bytes.AsSpan().SequenceEqual(high.Bytes))
            throw new AsmException("<asm>", MovingLine(rendered, bits, baseline, low.Bytes, high.Bytes),
                "the block takes an absolute address of a label of its own, which moves with the method: an asm block's code must not depend on where it is");
        return low.Bytes;
    }

    /// <summary>
    /// The line whose bytes differ between the two origins: the text again
    /// with a label of its own on every line, the last of them at or before
    /// the first byte that moved.
    /// </summary>
    private static int MovingLine(string rendered, int bits, X86Cpu baseline, byte[] low, byte[] high)
    {
        int first = 0;
        while (first < low.Length && first < high.Length && low[first] == high[first]) first++;
        string[] lines = rendered.Split('\n');
        for (int k = 0; k < lines.Length; k++)
            if (lines[k].Trim().Length > 0) lines[k] = $"__corsac_line_{k + 1}: " + lines[k];
        try
        {
            X86Assembler.Result marked = X86Assembler.Assemble(".base 0x1000\n" + string.Join("\n", lines), "<asm>", bits: bits, cpu: baseline);
            int line = 1;
            long best = -1;
            foreach ((string label, long at) in marked.Labels)
            {
                if (!label.StartsWith("__corsac_line_", StringComparison.Ordinal)) continue;
                long offset = at - 0x1000;
                if (offset <= first && offset > best || offset == best && int.Parse(label[14..]) > line)
                {
                    best = offset;
                    line = int.Parse(label[14..]);
                }
            }
            return line;
        }
        catch (AsmException)
        {
            return 1;
        }
    }
}
