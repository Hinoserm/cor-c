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
            string mnemonic = head.ToLowerInvariant();
            if (Prefixes.Contains(mnemonic))
            {
                string after = code[bodyAt..];
                string inner = X86Assembler.HeadText(after.TrimStart());
                bodyAt += (after.Length - after.TrimStart().Length) + inner.Length;
                mnemonic = inner.ToLowerInvariant();
            }
            // Not a `.runtime` line, whose words are features, nor a
            // branch's label: neither is a name of the method's.
            if (mnemonic.StartsWith('.') || IsBranch(mnemonic)) continue;
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
        /// <summary>
        /// The general registers it may read without needing them set: a
        /// trap's arguments, CPUID's ECX (a subleaf only some leaves read),
        /// a push's register (saved as it is, to be put back).
        /// </summary>
        public uint MayRead;
        /// <summary>The x87 stack's change in depth; ResetsX87: FNINIT and its kin leave it empty.</summary>
        public int X87;
        public bool ResetsX87, UsesX87, UsesMmx, EmptiesMmx, UsesSse, SetsDf, ClearsDf;
        /// <summary>What ESP moves by: a push's -size, a pop's +size.</summary>
        public int Esp;
        /// <summary>
        /// What kind of push it is, for the rules a pop of flags or of every
        /// register meets (Analyze): 'v' a value, 'f' PUSHF, 'A' PUSHAD,
        /// 'a' PUSHAW; and of pop, the same. Its bytes are Esp's.
        /// </summary>
        public char Pushes;
        public char Pops;
        /// <summary>
        /// The parts (Parts) of the general registers it reads and writes
        /// without naming them where that is less than the machine's word --
        /// AL by CBW, AH by LAHF and SAHF, AX by a word's MUL. The registers
        /// are in ImplicitRead and ImplicitWritten too; for any other there,
        /// the word.
        /// </summary>
        public ulong ReadBytes, WrittenBytes;
        /// <summary>0 no branch, 1 a conditional one, 2 an unconditional one; the label is the first operand.</summary>
        public int Branch;
        /// <summary>Why the block may not have this instruction, or null.</summary>
        public string? Refused;
    }

    private static uint G(params int[] regs) { uint m = 0; foreach (int r in regs) m |= 1u << r; return m; }

    /// <summary>
    /// A GENERAL REGISTER'S PARTS, four bits of a set to each register
    /// (register r's at 4r): bits 0-7, 8-15 (AH's), 16-31 and 32-63, which
    /// a read of a byte, a word, 32 or 64 bits needs set and a write of as
    /// much sets. A 32-bit write sets all four (long mode clears the top).
    /// </summary>
    internal static ulong Parts(int reg, int size, bool high, bool write) => (size switch
    {
        1 => high ? 2UL : 1UL,
        2 => 3UL,
        4 => write ? 15UL : 7UL,
        _ => 15UL,
    }) << (4 * reg);

    /// <summary>The parts of a register an operand names: AH..BH a high byte.</summary>
    internal static ulong Parts(Operand o, bool write)
        => Parts(GprNumber(o), o.Size, o.Size == 1 && !o.Rex8 && o.Reg is >= 4 and <= 7, write);

    /// <summary>Each register of a mask as the machine's word, read or written.</summary>
    private static ulong WordParts(uint regs, int bits, bool write)
    {
        ulong m = 0;
        for (int r = 0; r < 16; r++) if ((regs & (1u << r)) != 0) m |= Parts(r, bits / 8, false, write);
        return m;
    }

    /// <summary>The registers any of whose parts are in a set.</summary>
    private static uint PartRegisters(ulong parts)
    {
        uint m = 0;
        for (int r = 0; r < 16; r++) if (((parts >> (4 * r)) & 15) != 0) m |= 1u << r;
        return m;
    }
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
        // THE ACCUMULATOR'S WIDTH IS THE OPERATION'S: a byte's MUL reads AL
        // and writes AX -- parts of registers (Parts), not their words.
        ulong Acc(int size, bool write) => Parts(Eax, size, false, write);
        void Implicit(ulong read, ulong written)
        {
            f.ReadBytes |= read;
            f.WrittenBytes |= written;
            f.ImplicitRead |= PartRegisters(read);
            f.ImplicitWritten |= PartRegisters(written);
        }
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
            f.MayRead |= G(Eax, Ecx, Edx, Ebx, Esi, Edi);
            f.ImplicitWritten |= G(Eax);
            return f;
        }
        if (mn == "syscall")
        {
            if (bits != 64) { f.Refused = "syscall is long mode's; use int in 32-bit code"; return f; }
            f.ImplicitRead |= G(Eax, Edi, Esi, Edx, 10, 8, 9);
            f.MayRead |= G(Edi, Esi, Edx, 10, 8, 9);
            f.ImplicitWritten |= G(Eax, Ecx, 11);
            return f;
        }

        // ---- the stack ----
        // A segment register is pushed and popped as a word of the machine's,
        // whatever its own two bytes; a general register or memory as its size.
        // An immediate is the machine's word but where `word` says two bytes.
        int StackSize(Operand o) => o.Kind is OperandKind.Register or OperandKind.Memory && o.Size != 0 ? o.Size
            : o.Kind == OperandKind.Immediate && o.SizeGiven && o.Size == 2 ? 2 : word;
        if (mn == "push")
        {
            All(AsmRole.Read, AsmRole.Read);
            f.Esp = -(n == 1 ? StackSize(ops[0]) : word);
            f.Pushes = 'v';
            if (n == 1 && GprNumber(ops[0]) is int saved and >= 0) f.MayRead |= G(saved);
            return f;
        }
        if (mn == "pop") { All(AsmRole.Write, AsmRole.Write); f.Esp = n == 1 ? StackSize(ops[0]) : word; f.Pops = 'v'; return f; }
        if (mn is "pushf" or "pushfd" or "pushfq" or "pushfw") { f.Esp = -(mn == "pushfw" ? 2 : word); f.Pushes = 'f'; return f; }
        if (mn is "popf" or "popfd" or "popfq" or "popfw") { f.Esp = mn == "popfw" ? 2 : word; f.Pops = 'f'; return f; }
        if (mn is "pusha" or "pushad" or "pushaw")
        {
            if (bits == 64) { f.Refused = $"{mn} is not a long-mode instruction"; return f; }
            f.ImplicitRead |= G(Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi);
            f.MayRead |= G(Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi);
            f.Esp = mn == "pushaw" ? -16 : -32;
            // Its eight, which a POP may take back a register at a time, and
            // a POPA only of the same width: POPAW over PUSHAD's would load
            // BP from half of ESI.
            f.Pushes = mn == "pushaw" ? 'a' : 'A';
            return f;
        }
        if (mn is "popa" or "popad" or "popaw")
        {
            // EBP comes back as PUSHA left it, which the stack's rule checks
            // (Analyze: POPA only where its PUSHA's frame is on top, whole and in place).
            if (bits == 64) { f.Refused = $"{mn} is not a long-mode instruction"; return f; }
            if (mn == "popaw")
                Implicit(0, Parts(Eax, 2, false, true) | Parts(Ecx, 2, false, true) | Parts(Edx, 2, false, true)
                    | Parts(Ebx, 2, false, true) | Parts(Esi, 2, false, true) | Parts(Edi, 2, false, true));
            else f.ImplicitWritten |= G(Eax, Ecx, Edx, Ebx, Esi, Edi);
            f.Esp = mn == "popaw" ? 16 : 32;
            f.Pops = mn == "popaw" ? 'a' : 'A';
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
        if (mn == "cmpxchg")
        {
            // The accumulator as wide as the source register.
            All(AsmRole.ReadWrite, AsmRole.Read);
            int size = n == 2 && ops[1].Size != 0 ? ops[1].Size : n > 0 && ops[0].Size != 0 ? ops[0].Size : word;
            Implicit(Acc(size, false), Acc(size, true));
            return f;
        }
        if (mn == "cmpxchg8b")
        {
            All(AsmRole.ReadWrite, AsmRole.Read);
            f.ImplicitRead |= G(Eax, Edx, Ebx, Ecx);
            f.ImplicitWritten |= G(Eax, Edx);
            return f;
        }
        if (mn is "mul" or "div" or "idiv" || mn == "imul" && n == 1)
        {
            // A byte's MUL reads AL and writes AX; a byte's DIV reads and
            // writes AX. Wider, the pair DX:AX (EDX:EAX, RDX:RAX): MUL reads
            // the accumulator, DIV both; each writes both.
            All(AsmRole.Read, AsmRole.Read);
            int size = n == 1 && ops[0].Size != 0 ? ops[0].Size : word;
            bool divides = mn is "div" or "idiv";
            if (size == 1) Implicit(Acc(divides ? 2 : 1, false), Acc(2, true));
            else Implicit(Acc(size, false) | (divides ? Parts(Edx, size, false, false) : 0), Acc(size, true) | Parts(Edx, size, false, true));
            return f;
        }
        if (mn == "imul") { All(n == 3 ? AsmRole.Write : AsmRole.ReadWrite, AsmRole.Read); return f; }
        if (mn == "cbw") { Implicit(Acc(1, false), Acc(2, true)); return f; }
        if (mn == "cwde") { Implicit(Acc(2, false), Acc(4, true)); return f; }
        if (mn == "cdqe") { Implicit(Acc(4, false), Acc(8, true)); return f; }
        if (mn == "cwd") { Implicit(Acc(2, false), Parts(Edx, 2, false, true)); return f; }
        if (mn == "cdq") { Implicit(Acc(4, false), Parts(Edx, 4, false, true)); return f; }
        if (mn == "cqo") { Implicit(Acc(8, false), Parts(Edx, 8, false, true)); return f; }
        // AH from the flags, and back: the rest of EAX neither read nor changed.
        if (mn == "lahf") { Implicit(0, Parts(Eax, 1, true, true)); return f; }
        if (mn == "sahf") { Implicit(Parts(Eax, 1, true, false), 0); return f; }
        // AL looked up at [EBX + AL]: EBX the word, AL in and out.
        if (mn is "xlat" or "xlatb") { Implicit(Acc(1, false), Acc(1, true)); f.ImplicitRead |= G(Ebx); return f; }
        if (mn == "cpuid") { f.ImplicitRead |= G(Eax, Ecx); f.MayRead |= G(Ecx); f.ImplicitWritten |= G(Eax, Ebx, Ecx, Edx); return f; }
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
            // The accumulator as wide as the suffix: AL for b, AX for w, EAX
            // for d, RAX for q; the port in DX, a word.
            int size = mn[^1] switch { 'b' => 1, 'w' => 2, 'd' => 4, _ => 8 };
            switch (mn.StartsWith("ins", StringComparison.Ordinal) ? "ins" : mn[..4])
            {
                case "movs": f.ImplicitRead |= G(Esi, Edi); f.ImplicitWritten |= G(Esi, Edi); break;
                case "cmps": f.ImplicitRead |= G(Esi, Edi); f.ImplicitWritten |= G(Esi, Edi); break;
                case "stos": Implicit(Acc(size, false), 0); f.ImplicitRead |= G(Edi); f.ImplicitWritten |= G(Edi); break;
                case "lods": Implicit(0, Acc(size, true)); f.ImplicitRead |= G(Esi); f.ImplicitWritten |= G(Esi); break;
                case "scas": Implicit(Acc(size, false), 0); f.ImplicitRead |= G(Edi); f.ImplicitWritten |= G(Edi); break;
                case "ins": Implicit(Parts(Edx, 2, false, false), 0); f.ImplicitRead |= G(Edi); f.ImplicitWritten |= G(Edi); break;
                case "outs": Implicit(Parts(Edx, 2, false, false), 0); f.ImplicitRead |= G(Esi); f.ImplicitWritten |= G(Esi); break;
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
            if (mn is "frstor" or "fldenv")
            {
                f.Refused = $"{mn} loads an x87 state whose stack depth the block cannot know; save and restore it outside the block";
                return f;
            }
            if (mn is "finit" or "fninit" or "fsave" or "fnsave") f.ResetsX87 = true;
            f.X87 = X87Depth.GetValueOrDefault(mn);
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
        /// <summary>Whether it pushes or pops, or names ESP or EBP -- an operand of either, or an address off either: what a method without a frame pointer could not give it.</summary>
        public bool UsesStack;
        /// <summary>Whether the block runs any float or packed code -- x87, MMX or SSE -- which the x87 facts and the packed registers care about.</summary>
        public bool UsesFloatState => UsesX87 || UsesMmx || UsesSse;
    }

    /// <summary>A general register by the parts of it a read needs (Parts): `al`, `ah`, `ax`, `eax`, `rax`, `r9b`.</summary>
    private static string PartName(int r, ulong parts, int bits)
    {
        if (r >= 8) return parts switch { 1 => $"r{r}b", 3 => $"r{r}w", 7 => $"r{r}d", _ => $"r{r}" };
        return parts switch
        {
            1 => r < 4 ? Operands.Reg8[r] : Operands.Reg8Rex[r - 4],
            2 => Operands.Reg8[r + 4],
            3 => Operands.Reg16[r],
            15 when bits == 64 => Operands.Reg64[r],
            _ => Operands.Reg32[r],
        };
    }

    private const string EspCopied = "an asm block may not read ESP as a value (`mov ebx, esp`, `cmp esp, ebx`, `lea ebx, [esp + 4]`, `push esp`): "
        + "what it pushed is followed through ESP alone -- address the stack as [esp + n]";

    private const string StackUnsetRead = "this reads off the stack what the block pushed from a register it had not set, or the ESP PUSHA saved: "
        + "a block's registers hold nothing of the method's, and ESP's value is not the block's to take";

    // THE STACK'S BYTES as Analyze follows them (its `stack`): 'v' a value,
    // 'u' a value pushed from a register the block had not set, 'c' and 's'
    // PUSHF's DF byte with DF clear or set; and from PushaPlace up, a byte
    // PUSHAD or PUSHAW left, its place in the frame in the low bits and
    // PushaUnset where its register was not set.
    private const char PushaPlace = (char)0x1000;
    private const char PushaUnset = (char)0x0800;

    private static bool UnsetByte(char c) => c == 'u' || c >= PushaPlace && (c & PushaUnset) != 0;

    /// <summary>PUSHAD's ('A') or PUSHAW's ('a') byte at a place in its frame (0 the top), set or not.</summary>
    private static char PushaByte(char kind, int place, bool unset)
        => (char)((kind == 'A' ? 0x1000 : 0x2000) + place | (unset ? PushaUnset : 0));

    /// <summary>The part (Parts) of a register a byte of it is in: 0, 1, 2 for bytes 2-3, 3 for 4-7.</summary>
    private static int PartOfByte(int b) => b switch { 0 => 0, 1 => 1, 2 or 3 => 2, _ => 3 };

    /// <summary>A register's `size` bytes pushed, the top last: each unset where its part was not set in `g`.</summary>
    private static string Pushed(int r, int size, ulong g)
    {
        char[] c = new char[size];
        for (int b = 0; b < size; b++) c[size - 1 - b] = ((g >> (4 * r + PartOfByte(b))) & 1) != 0 ? 'v' : 'u';
        return new string(c);
    }

    /// <summary>The parts of register r a pop leaves unset: those any byte of which was unset on the stack.</summary>
    private static ulong UnsetParts(int r, string taken)
    {
        ulong m = 0;
        for (int b = 0; b < taken.Length; b++)
            if (UnsetByte(taken[^(b + 1)])) m |= 1UL << (4 * r + PartOfByte(b));
        return m;
    }

    /// <summary>PUSHA's eight, top first: EDI, ESI, EBP, ESP, EBX, EDX, ECX, EAX.</summary>
    private static readonly int[] PushaOrder = { Edi, Esi, Ebp, Esp, Ebx, Edx, Ecx, Eax };

    /// <summary>
    /// What PUSHAD ('A', 32 bytes) or PUSHAW ('a', 16) leaves on the stack:
    /// each byte its own place in the frame, the top (place 0) last, so that
    /// POPA finds its frame whole and in place; unset where its register's
    /// part was not set in `g`, and the saved ESP always unset -- a value
    /// the block may not take (EspCopied).
    /// </summary>
    private static string Pusha(char kind, ulong g)
    {
        int width = kind == 'A' ? 4 : 2, size = 8 * width;
        char[] c = new char[size];
        for (int place = 0; place < size; place++)
        {
            int r = PushaOrder[place / width];
            bool unset = r == Esp || ((g >> (4 * r + PartOfByte(place % width))) & 1) == 0;
            c[size - 1 - place] = PushaByte(kind, place, unset);
        }
        return new string(c);
    }

    /// <summary>
    /// The registers after POPA from its frame: each as set as it was when
    /// PUSHA saved it -- POPAD writes all of each, POPAW the low word.
    /// </summary>
    private static ulong Popa(ulong g, string taken, char kind)
    {
        int width = kind == 'A' ? 4 : 2;
        for (int slot = 0; slot < 8; slot++)
        {
            int r = PushaOrder[slot];
            if (r is Esp or Ebp) continue;
            ulong parts = width == 4 ? 15UL : 3UL, set = parts;
            for (int b = 0; b < width; b++)
                if (UnsetByte(taken[^(slot * width + b + 1)])) set &= ~(1UL << PartOfByte(b));
            // Bits 32-63 follow 16-31: POPAD is no long-mode instruction.
            if (width == 4 && (set & 4) == 0) set &= ~8UL;
            g = g & ~(parts << (4 * r)) | set << (4 * r);
        }
        return g;
    }

    /// <summary>
    /// A memory operand's displacement as a number, as the assembler reads
    /// it (Expr), or null where it names anything: a block knows no names
    /// but its labels, and a label's address is no offset into the stack.
    /// </summary>
    private static int? Displacement(Operand o)
    {
        if (!o.HasDisp) return 0;
        try
        {
            long value = Expr.Eval(o.Disp, NoSymbols.Instance, "<asm>", 0, out bool resolved);
            return resolved && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
        }
        catch (Exception e) when (e is AsmException or OverflowException or FormatException)
        {
            return null;
        }
    }

    private sealed class NoSymbols : ISymbols
    {
        public static readonly NoSymbols Instance = new();
        public bool TryLookup(string name, out long value) { value = 0; return false; }
    }

    /// <summary>
    /// A block reaches through ESP only what it pushed: below ESP an
    /// interrupt or a signal may write at any time, and past its pushes is
    /// the method's frame, which the compiler lays out (its locals are named).
    /// </summary>
    private static void StackRange(int at, int size, int pushed, int line)
    {
        if (at < 0) throw new AsmException("<asm>", line, "this reaches below ESP, where an interrupt or a signal may write at any time");
        if ((long)at + size > pushed)
            throw new AsmException("<asm>", line, $"this reaches {(long)at + size - pushed} bytes past what the block pushed, into the method's frame, "
                + "which the compiler lays out: name the local instead");
    }

    /// <summary>
    /// How many bytes a memory operand covers: its size word's, or where it
    /// has none, the instruction's -- a scalar SSE's four or eight, a packed
    /// one's sixteen, MMX's eight, a general register's width -- or 0 where
    /// that is not known.
    /// </summary>
    private static int MemoryWidth(string mn, Operand[] ops, int o, int bits)
    {
        if (ops[o].Size != 0) return ops[o].Size;
        switch (mn)
        {
            case "fxsave" or "fxrstor": return 512;
            case "fnsave" or "fsave": return 108;
            case "fnstenv" or "fstenv": return 28;
            case "ldmxcsr" or "stmxcsr" or "movd" or "cvtsi2ss" or "cvtsi2sd" or "movsxd": return 4;
            case "pinsrw": return 2;
            case "movq" or "movlps" or "movhps" or "movlpd" or "movhpd" or "cvtpi2ps" or "cvtps2pi" or "cvttps2pi" or "cvtpi2pd"
                or "cvtpd2pi" or "cvttpd2pi" or "cvtps2pd" or "cvtdq2pd" or "movntq": return 8;
            case "lgdt" or "lidt": return bits == 64 ? 10 : 6;
            case "movzx" or "movsx": return 0;
            // A conversion reads its source's width, not its destination's.
            case "cvtss2sd" or "cvtss2si" or "cvttss2si": return 4;
            case "cvtsd2ss" or "cvtsd2si" or "cvttsd2si": return 8;
        }
        if (mn.EndsWith("ss", StringComparison.Ordinal)) return 4;
        if (mn.EndsWith("sd", StringComparison.Ordinal)) return 8;
        for (int k = 0; k < ops.Length; k++)
        {
            if (k == o) continue;
            switch (ops[k].Kind)
            {
                case OperandKind.Xmm: return 16;
                case OperandKind.Mmx: return 8;
                case OperandKind.Register: return ops[k].Size;
            }
        }
        return 0;
    }

    /// <summary>The words of a block's operands that name registers, as written: a local spelt as one could not be named (Binder.CheckAsm).</summary>
    public static HashSet<string> RegisterWords(string text)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = X86Assembler.StripCommentText(raw);
            foreach ((string word, int _) in Words(line))
                if (Operands.IsRegisterName(word.ToLowerInvariant())) words.Add(word);
        }
        return words;
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
            if (said is not null && said != locals[k].SizeWord)
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
            // A block of memory bigger than any local: no local is it.
            if (s.Mnemonic is "fxsave" or "fxrstor" or "lgdt" or "lidt" or "fnsave" or "fsave" or "fnstenv" or "fstenv"
                && Array.Exists(s.Local, l => l >= 0))
                throw new AsmException("<asm>", s.Line, $"{s.Mnemonic} takes memory larger than a local: give it an address, as a nuint in a register");
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
                    if (s.Mnemonic == "lea" && op.Base >= 0 && (op.Base & 15) == Esp)
                        throw new AsmException("<asm>", s.Line, EspCopied);
                    continue;
                }
                if (op.Kind == OperandKind.Register)
                {
                    int r = GprNumber(op);
                    if ((role & AsmRole.Write) != 0)
                    {
                        if (r == Esp) throw new AsmException("<asm>", s.Line, "an asm block may not write ESP but by PUSH and POP, which must pair up");
                        if (r == Ebp) throw new AsmException("<asm>", s.Line, "an asm block may not write EBP: the method's frame is reached through it");
                        a.GprWritten |= 1u << r;
                    }
                    if ((role & AsmRole.Read) != 0)
                    {
                        if (r == Esp) throw new AsmException("<asm>", s.Line, EspCopied);
                        a.GprRead |= 1u << r;
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
            a.UsesStack |= f.Esp != 0;
            if ((f.ImplicitWritten & (1u << Ebp)) != 0)
                throw new AsmException("<asm>", s.Line, "an asm block may not write EBP: the method's frame is reached through it");
            a.GprWritten |= f.ImplicitWritten & ~(1u << Esp);
            a.UsesX87 |= f.UsesX87;
            a.UsesMmx |= f.UsesMmx || f.EmptiesMmx;
            a.UsesSse |= f.UsesSse;
        }

        // EVERY PATH: the x87's depth, ESP's and what the stack holds,
        // whether MMX is in use, the direction flag, the registers set, and
        // which locals are written on every path so far.
        int count = statements.Count;
        int[] x87 = new int[count + 1], esp = new int[count + 1];
        bool[] mmx = new bool[count + 1], df = new bool[count + 1], seen = new bool[count + 1];
        ulong[] must = new ulong[count + 1];
        // WHAT THE BLOCK'S PUSHES LEFT ON THE STACK, a char to each byte, the
        // top (the lowest address) last (PushaPlace's summary): a value, set
        // or not as its register was; of PUSHF's, its DF byte (the second)
        // 'c' or 's' as DF was clear or set; of PUSHAD's and PUSHAW's, each
        // byte its place in the frame (Pusha). POPF takes DF back from a
        // PUSHF's DF byte, and from anything else may set it; POPA takes only
        // a whole frame of its own width, in place, since EBP comes back from
        // it; a register popped is set as what it takes was. A write through
        // ESP makes the bytes it covers values.
        string[] stack = new string[count + 1];
        // THE REGISTERS SET ON EVERY PATH SO FAR: the general ones by their
        // parts (Parts), the XMM ones in bits 0-15 of a set of their own and
        // the MMX ones in 16-23. ESP and EBP are the method's, set at the
        // start; anything else read before the block sets it holds nothing of
        // the method's (only a head's binding puts a local in a register, and
        // that is a `mov` the block sets it by).
        ulong[] gset = new ulong[count + 1], vset = new ulong[count + 1];
        // Per statement: the parts and the vector registers it needs set and
        // those it sets; and the bytes it reads and writes through ESP --
        // from the top, so many (stackAt, stackSize; readAt, readSize), the
        // size -1 where they are not known (an index, a displacement not a
        // number, a bit string's offset in a register, a write's size),
        // Unsized for a read whose size is not known, and 0 for none.
        ulong[] gneeds = new ulong[count], vneeds = new ulong[count], gsets = new ulong[count], vsets = new ulong[count];
        int[] stackAt = new int[count], stackSize = new int[count], readAt = new int[count], readSize = new int[count];
        const int Unsized = int.MaxValue / 2;
        for (int k = 0; k < count; k++)
        {
            AsmStatement s = statements[k];
            if (!s.IsInstruction || facts[k] is not { } f) continue;
            Operand[] ops = s.Parsed;
            // A register combined with itself to make zero (or every bit set)
            // reads nothing of what it held.
            bool idiom = ops.Length == 2 && ops[0].Kind == ops[1].Kind && ops[0].Kind is OperandKind.Register or OperandKind.Xmm or OperandKind.Mmx
                && ops[0].Reg == ops[1].Reg && ops[0].Size == ops[1].Size
                && s.Mnemonic is "xor" or "sub" or "sbb" or "pxor" or "xorps" or "xorpd" or "pandn" or "andnps" or "andnpd"
                    or "psubb" or "psubw" or "psubd" or "psubq" or "psubsb" or "psubsw" or "psubusb" or "psubusw"
                    or "pcmpeqb" or "pcmpeqw" or "pcmpeqd" or "pcmpgtb" or "pcmpgtw" or "pcmpgtd";
            // A destination these keep the rest of, and so read, without
            // needing it set: a scalar into the low lane, half of a register.
            // After, it counts as set: its lanes are followed as one, and the
            // lane written is the one scalar code goes on to read.
            bool merges = s.Mnemonic is "cvtsi2ss" or "cvtsi2sd" or "cvtss2sd" or "cvtsd2ss" or "cvtpi2ps" or "sqrtss" or "sqrtsd"
                or "rcpss" or "rsqrtss" or "movlps" or "movhps" or "movlpd" or "movhpd" or "movhlps" or "movlhps" or "pinsrw"
                || s.Mnemonic is "movss" or "movsd" && ops.Length == 2 && ops[1].Kind == OperandKind.Xmm;
            // The registers it does not name: by their parts where it gave
            // them (ReadBytes, WrittenBytes), the rest as the machine's word.
            ulong gneed = WordParts(f.ImplicitRead & ~f.MayRead & ~PartRegisters(f.ReadBytes), bits, false) | f.ReadBytes;
            ulong gmade = WordParts(f.ImplicitWritten & ~PartRegisters(f.WrittenBytes), bits, true) | f.WrittenBytes;
            ulong vneed = 0, vmade = 0;
            for (int o = 0; o < ops.Length; o++)
            {
                AsmRole role = f.Roles.Length > o ? f.Roles[o] : AsmRole.Read;
                Operand op = ops[o];
                if (op.Kind == OperandKind.Memory)
                {
                    if (op.Base >= 0) gneed |= Parts(op.Base & 15, bits / 8, false, false);
                    if (op.Index >= 0) gneed |= Parts(op.Index & 15, bits / 8, false, false);
                    if (op.Base >= 0 && (op.Base & 15) == Esp)
                    {
                        int size = MemoryWidth(s.Mnemonic, ops, o, bits);
                        // LGDT and LIDT read their table through the address.
                        if (s.Mnemonic is "lgdt" or "lidt") role |= AsmRole.Read;
                        // BT's kin with the bit's offset in a register reach
                        // past the operand, by the offset over eight.
                        bool known = op.Index < 0 && Displacement(op) is not null
                            && !(s.Mnemonic is "bt" or "bts" or "btr" or "btc" && ops.Length == 2 && ops[1].Kind == OperandKind.Register);
                        // A write of a size not known is a write of no byte known;
                        // a read, of every byte from where it starts.
                        int span = !known ? -1 : size > 0 ? size : Unsized;
                        if ((role & AsmRole.Write) != 0) { stackSize[k] = span == Unsized ? -1 : span; stackAt[k] = Displacement(op) ?? 0; }
                        if ((role & AsmRole.Read) != 0) { readSize[k] = span; readAt[k] = Displacement(op) ?? 0; }
                    }
                    continue;
                }
                bool reads = (role & AsmRole.Read) != 0 && !idiom && !(merges && o == 0);
                bool writes = (role & AsmRole.Write) != 0;
                if (op.Kind == OperandKind.Register)
                {
                    // A byte's or a word's write sets that much (in long mode
                    // too: only a 32-bit write clears the top), and a byte's
                    // or a word's read needs only that much.
                    if (reads && (f.MayRead & (1u << GprNumber(op))) == 0) gneed |= Parts(op, false);
                    if (writes) gmade |= Parts(op, true);
                }
                else if (op.Kind is OperandKind.Xmm or OperandKind.Mmx)
                {
                    ulong bit = 1UL << (op.Kind == OperandKind.Xmm ? op.Reg : 16 + op.Reg);
                    if (reads) vneed |= bit;
                    if (writes) vmade |= bit;
                }
            }
            gneeds[k] = gneed;
            vneeds[k] = vneed;
            gsets[k] = gmade;
            vsets[k] = vmade;
        }
        Stack<int> work = new();
        // Two paths' stacks, as one: where they differ the flags may hold DF
        // set, a byte PUSHA left in the same place is unset where it is
        // unset on either, and any other byte is a value -- unset if it is on
        // either.
        static string Join(string a, string b)
        {
            char[] c = new char[a.Length];
            for (int k = 0; k < a.Length; k++)
                c[k] = a[k] == b[k] ? a[k]
                    : a[k] is 'c' or 's' && b[k] is 'c' or 's' ? 's'
                    : a[k] >= PushaPlace && (a[k] | PushaUnset) == (b[k] | PushaUnset) ? (char)(a[k] | PushaUnset)
                    : UnsetByte(a[k]) || UnsetByte(b[k]) ? 'u' : 'v';
            return new string(c);
        }
        void Arrive(int at, int x, int e, bool m, bool d, ulong w, string st, ulong g, ulong v, int line)
        {
            if (!seen[at])
            {
                seen[at] = true; x87[at] = x; esp[at] = e; mmx[at] = m; df[at] = d; must[at] = w; stack[at] = st; gset[at] = g; vset[at] = v;
                work.Push(at);
                return;
            }
            if (x87[at] != x) throw new AsmException("<asm>", line, $"the x87 stack is {x} deep on one path to here and {x87[at]} on another");
            if (esp[at] != e || stack[at].Length != st.Length)
                throw new AsmException("<asm>", line, $"ESP is {e} bytes from where the block found it on one path to here and {esp[at]} on another");
            bool changed = false;
            if (m && !mmx[at]) { mmx[at] = true; changed = true; }
            if (d && !df[at]) { df[at] = true; changed = true; }
            if ((must[at] & w) != must[at]) { must[at] &= w; changed = true; }
            string joined = Join(stack[at], st);
            if (joined != stack[at]) { stack[at] = joined; changed = true; }
            // Set after both where both set it: a whole write on one path and
            // a byte's on the other leave the byte.
            if ((gset[at] & g) != gset[at]) { gset[at] &= g; changed = true; }
            if ((vset[at] & v) != vset[at]) { vset[at] &= v; changed = true; }
            if (changed) work.Push(at);
        }
        Arrive(0, 0, 0, false, false, 0, "", WordParts(G(Esp, Ebp), 64, true), 0, statements.Count > 0 ? statements[0].Line : 1);
        while (work.Count > 0)
        {
            int at = work.Pop();
            if (at == count) continue;
            AsmStatement s = statements[at];
            Facts f = facts[at];
            int x = x87[at], e = esp[at];
            bool m = mmx[at], d = df[at];
            ulong w = must[at];
            string st = stack[at];
            ulong g = gset[at], v = vset[at];
            if (s.IsInstruction)
            {
                string? unset = null;
                bool some = false;
                if ((gneeds[at] & ~g) is var gu and not 0)
                {
                    int r = System.Numerics.BitOperations.TrailingZeroCount(gu) / 4;
                    ulong wanted = (gneeds[at] >> (4 * r)) & 15;
                    unset = PartName(r, wanted, bits);
                    some = ((g >> (4 * r)) & wanted) != 0;
                }
                else if ((vneeds[at] & ~v) is var vu and not 0)
                {
                    int b = System.Numerics.BitOperations.TrailingZeroCount(vu);
                    unset = b < 16 ? "xmm" + b : "mm" + (b - 16);
                }
                if (unset != null)
                    throw new AsmException("<asm>", s.Line, $"this reads {unset}, which the block has {(some ? "set only part of" : "not set")} on every path to here: "
                        + "a block's registers hold nothing of the method's but what its own instructions -- its head's `mov reg, local` among them -- put there");
                ulong before = g;
                g |= gsets[at];
                v |= vsets[at];
                // A read through ESP of what the block pushed: nothing it had
                // not set, nor the ESP PUSHA saved (EspCopied), and nothing
                // but what it pushed; where it is not known which bytes, none
                // such on the stack at all.
                if (readSize[at] != 0)
                {
                    if (readSize[at] > 0) StackRange(readAt[at], readSize[at] == Unsized ? 1 : readSize[at], st.Length, s.Line);
                    int from = readSize[at] < 0 ? 0 : readAt[at], to = readSize[at] < 0 ? st.Length : (int)Math.Min((long)from + readSize[at], st.Length);
                    for (int b = Math.Max(0, from); b < to; b++)
                        if (UnsetByte(st[^(b + 1)])) throw new AsmException("<asm>", s.Line, StackUnsetRead);
                }
                if (f.Pops != '\0')
                {
                    if (st.Length < f.Esp) throw new AsmException("<asm>", s.Line, "this pops more off the stack than the block pushed");
                    string taken = st[^f.Esp..];
                    st = st[..^f.Esp];
                    // A value popped anywhere but a general register -- memory,
                    // a segment register, the flags: nothing unset (a register
                    // popped is unset as it was, below).
                    if ((f.Pops == 'f' || f.Pops == 'v' && s.Parsed is not [{ Kind: OperandKind.Register }]) && taken.Any(UnsetByte))
                        throw new AsmException("<asm>", s.Line, StackUnsetRead);
                    // DF is bit 10: in the second byte from the top.
                    if (f.Pops == 'f') d = taken[^2] != 'c';
                    if (f.Pops is 'a' or 'A')
                    {
                        for (int b = 0; b < taken.Length; b++)
                            if (taken[^(b + 1)] < PushaPlace || (taken[^(b + 1)] | PushaUnset) != PushaByte(f.Pops, b, true))
                                throw new AsmException("<asm>", s.Line, "POPA here takes what PUSHA did not push, or not in place: EBP would be loaded from it");
                        g = Popa(g, taken, f.Pops);
                    }
                    // A register popped holds what was pushed: unset where that was.
                    if (f.Pops == 'v' && s.Parsed is [{ Kind: OperandKind.Register } dest])
                        g &= ~UnsetParts(GprNumber(dest), taken);
                }
                // A write through ESP: the bytes it covers are set values (what
                // it stores the block had set), still PUSHA's in their places
                // -- but for the saved EBP, which POPA would load; where it is
                // not known which, no byte is PUSHA's or the flags' any more.
                // A CMPXCHG's write may not happen, but its read (above) has
                // already found every byte it covers set.
                if (stackSize[at] > 0)
                {
                    StackRange(stackAt[at], stackSize[at], st.Length, s.Line);
                    char[] c = st.ToCharArray();
                    for (int b = stackAt[at]; b < stackAt[at] + stackSize[at]; b++)
                    {
                        char was = c[^(b + 1)];
                        if (was >= PushaPlace && PushaOrder[(was & 0xFF) / ((was & 0x2000) != 0 ? 2 : 4)] == Ebp)
                            throw new AsmException("<asm>", s.Line, "this writes the EBP PUSHA saved, which POPA would load: the method's frame is reached through it");
                        c[^(b + 1)] = was >= PushaPlace ? (char)(was & ~PushaUnset) : 'v';
                    }
                    st = new string(c);
                }
                else if (stackSize[at] < 0)
                    st = string.Concat(st.Select(c => UnsetByte(c) ? 'u' : 'v'));
                if (f.Pushes != '\0')
                {
                    int size = -f.Esp;
                    st += f.Pushes switch
                    {
                        'f' => new string('v', size - 2) + (d ? 's' : 'c') + 'v',
                        'a' or 'A' => Pusha(f.Pushes, before),
                        _ => s.Parsed is [{ Kind: OperandKind.Register } pushed] ? Pushed(GprNumber(pushed), size, before) : new string('v', size),
                    };
                }
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
            if (f.Branch != 0) Arrive(target[at], x, e, m, d, w, st, g, v, s.Line);
            if (f.Branch != 2) Arrive(at + 1, x, e, m, d, w, st, g, v, s.Line);
        }

        a.UsesStack |= ((a.GprRead | a.GprWritten) & ((1u << Esp) | (1u << Ebp))) != 0;
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
        /// <summary>The general registers the block destroys: those its kept instructions write, and those the tail takes a local from.</summary>
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
