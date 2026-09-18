#nullable enable
using System.Text;

namespace Corsac.Asm;

internal enum OperandKind
{
    Register,
    Segment,
    Control,
    Memory,
    Immediate,
    FarPointer,
}

/// <summary>
/// One parsed operand. Intel syntax, so the destination comes first and a
/// memory reference is written in brackets.
///
/// Sizes are in BYTES rather than bits because every encoding decision below
/// compares them against a displacement or an immediate, which are counted in
/// bytes; carrying bits and dividing at each use invited the one place it was
/// forgotten.
/// </summary>
internal sealed class Operand
{
    public OperandKind Kind;

    /// <summary>1, 2 or 4. Zero when a memory reference carried no size keyword.</summary>
    public int Size;

    /// <summary>True when a <c>byte</c>/<c>word</c>/<c>dword</c> keyword said the size outright.</summary>
    public bool SizeGiven;

    /// <summary>Register number, segment register number, or control register number.</summary>
    public int Reg;

    // ---- memory ------------------------------------------------------------
    public int Base = -1;
    public int Index = -1;
    public int Scale = 1;
    public int AddrSize;             // 16 or 32
    public int Segment = -1;         // segment override, -1 for none
    public string Disp = "";         // displacement expression, "" for none
    public bool HasDisp;

    // ---- immediate and far pointer ----------------------------------------
    public string Value = "";
    public string FarSegment = "";

    /// <summary>The operand as written, so an error can quote it back.</summary>
    public string Text = "";

    public bool IsReg8 => Kind == OperandKind.Register && Size == 1;
    public bool IsRegOrMem => Kind is OperandKind.Register or OperandKind.Memory;
}

/// <summary>
/// The register tables and the operand parser. Split from the encoder so that
/// the encoder is a table of opcodes and nothing else.
/// </summary>
internal static class Operands
{
    public static readonly string[] Reg8 = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };
    public static readonly string[] Reg16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
    public static readonly string[] Reg32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    public static readonly string[] SegReg = { "es", "cs", "ss", "ds", "fs", "gs" };

    /// <summary>The prefix byte that selects each segment register.</summary>
    public static readonly byte[] SegPrefix = { 0x26, 0x2E, 0x36, 0x3E, 0x64, 0x65 };

    public static int Find(string[] table, string name)
    {
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>True when the text names a register of any kind.</summary>
    public static bool IsRegisterName(string name)
        => Find(Reg8, name) >= 0 || Find(Reg16, name) >= 0 || Find(Reg32, name) >= 0
           || Find(SegReg, name) >= 0 || ControlNumber(name) >= 0;

    private static int ControlNumber(string name)
        => name switch { "cr0" => 0, "cr1" => 1, "cr2" => 2, "cr3" => 3, "cr4" => 4, _ => -1 };

    /// <summary>
    /// Parses one operand. <paramref name="bits"/> is the current default size,
    /// which decides the address size of a memory reference that names no
    /// register at all -- <c>[0x1234]</c> is a 16-bit reference in 16-bit code
    /// and a 32-bit one after <c>.bits 32</c>.
    /// </summary>
    public static Operand Parse(string text, int bits, string file, int line)
    {
        string rest = text.Trim();
        Operand op = new() { Text = rest };

        // A size keyword binds to what follows it, and 'ptr' after it is
        // accepted because half the world writes it and it carries no meaning.
        while (true)
        {
            string lower = Lower(FirstWord(rest));
            int size = lower switch { "byte" => 1, "word" => 2, "dword" => 4, _ => 0 };
            if (size == 0)
            {
                break;
            }
            op.Size = size;
            op.SizeGiven = true;
            rest = rest[FirstWord(rest).Length..].TrimStart();
            if (Lower(FirstWord(rest)) == "ptr")
            {
                rest = rest[3..].TrimStart();
            }
        }

        if (rest.Length == 0)
        {
            throw new AsmException(file, line, $"'{text.Trim()}' is a size with nothing after it");
        }

        if (rest[0] == '[')
        {
            if (rest[^1] != ']')
            {
                throw new AsmException(file, line, $"'{text.Trim()}' opens a memory reference and does not close it");
            }
            ParseMemory(op, rest[1..^1], bits, file, line);
            return op;
        }

        string name = Lower(rest);

        int r = Find(Reg8, name);
        if (r >= 0)
        {
            op.Kind = OperandKind.Register;
            op.Reg = r;
            op.Size = 1;
            return op;
        }

        r = Find(Reg16, name);
        if (r >= 0)
        {
            op.Kind = OperandKind.Register;
            op.Reg = r;
            op.Size = 2;
            return op;
        }

        r = Find(Reg32, name);
        if (r >= 0)
        {
            op.Kind = OperandKind.Register;
            op.Reg = r;
            op.Size = 4;
            return op;
        }

        r = Find(SegReg, name);
        if (r >= 0)
        {
            op.Kind = OperandKind.Segment;
            op.Reg = r;
            op.Size = 2;
            return op;
        }

        r = ControlNumber(name);
        if (r >= 0)
        {
            op.Kind = OperandKind.Control;
            op.Reg = r;
            op.Size = 4;
            return op;
        }

        // A far target is written selector:offset. The colon has to be found
        // outside brackets and outside a character literal, or `mov al, ':'`
        // and `[es:di]` would both look like one.
        int colon = TopLevelColon(rest);
        if (colon > 0)
        {
            op.Kind = OperandKind.FarPointer;
            op.FarSegment = rest[..colon].Trim();
            op.Value = rest[(colon + 1)..].Trim();
            return op;
        }

        op.Kind = OperandKind.Immediate;
        op.Value = rest;
        return op;
    }

    private static int TopLevelColon(string text)
    {
        int depth = 0;
        bool inChar = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && inChar)
            {
                i++;
                continue;
            }
            if (c == '\'')
            {
                inChar = !inChar;
                continue;
            }
            if (inChar)
            {
                continue;
            }
            if (c is '[' or '(')
            {
                depth++;
            }
            else if (c is ']' or ')')
            {
                depth--;
            }
            else if (c == ':' && depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static void ParseMemory(Operand op, string inner, int bits, string file, int line)
    {
        op.Kind = OperandKind.Memory;
        inner = inner.Trim();

        // A size keyword INSIDE the brackets is the address size, not the
        // operand size: `[dword 0x12345678]` is how a 16-bit instruction
        // reaches a 32-bit address, and it is a different thing from the
        // `dword [bx]` that says the operand is four bytes wide.
        int forcedAddrSize = 0;
        while (true)
        {
            string word = Lower(FirstWord(inner));
            int size = word switch { "word" => 16, "dword" => 32, _ => 0 };
            if (size == 0)
            {
                break;
            }
            forcedAddrSize = size;
            inner = inner[word.Length..].TrimStart();
        }

        int colon = TopLevelColon(inner);
        if (colon > 0)
        {
            int seg = Find(SegReg, Lower(inner[..colon].Trim()));
            if (seg < 0)
            {
                throw new AsmException(file, line, $"'{inner[..colon].Trim()}' is not a segment register");
            }
            op.Segment = seg;
            inner = inner[(colon + 1)..].Trim();
        }

        List<int> regs = new();
        List<int> scales = new();
        int addrSize = 0;
        StringBuilder disp = new();

        foreach ((int sign, string term) in SplitTerms(inner, file, line))
        {
            string t = term.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            int scale = 1;
            string regText = t;
            int star = t.IndexOf('*');
            if (star >= 0)
            {
                string left = t[..star].Trim();
                string right = t[(star + 1)..].Trim();
                if (IsRegisterName(Lower(left)))
                {
                    regText = left;
                    scale = (int)Expr.EvalNow(right, Nothing.Instance, file, line);
                }
                else if (IsRegisterName(Lower(right)))
                {
                    regText = right;
                    scale = (int)Expr.EvalNow(left, Nothing.Instance, file, line);
                }
            }

            string lower = Lower(regText);
            int r16 = Find(Reg16, lower);
            int r32 = Find(Reg32, lower);

            if (r16 >= 0 || r32 >= 0)
            {
                if (sign < 0)
                {
                    throw new AsmException(file, line, $"a register cannot be subtracted in '{op.Text}'");
                }
                int thisSize = r32 >= 0 ? 32 : 16;
                if (addrSize != 0 && addrSize != thisSize)
                {
                    throw new AsmException(file, line, $"'{op.Text}' mixes 16-bit and 32-bit registers in one address");
                }
                addrSize = thisSize;
                regs.Add(r32 >= 0 ? r32 : r16);
                scales.Add(scale);
                continue;
            }

            if (Find(Reg8, lower) >= 0 || Find(SegReg, lower) >= 0)
            {
                throw new AsmException(file, line, $"'{regText}' cannot be part of an address");
            }

            disp.Append(sign < 0 ? "-(" : "+(").Append(t).Append(')');
        }

        if (forcedAddrSize != 0 && addrSize != 0 && forcedAddrSize != addrSize)
        {
            throw new AsmException(file, line, $"'{op.Text}' says the address is {forcedAddrSize} bits and then uses {addrSize}-bit registers");
        }
        op.AddrSize = addrSize != 0 ? addrSize : (forcedAddrSize != 0 ? forcedAddrSize : bits);
        op.HasDisp = disp.Length > 0;
        op.Disp = disp.Length > 0 ? disp.ToString() : "";

        if (regs.Count > 2)
        {
            throw new AsmException(file, line, $"'{op.Text}' names more than two registers");
        }

        if (op.AddrSize == 16)
        {
            SetSixteen(op, regs, scales, file, line);
            return;
        }

        SetThirtyTwo(op, regs, scales, file, line);
    }

    /// <summary>
    /// 16-bit addressing has no SIB byte and no general base+index: the only
    /// combinations the ModRM table can spell are bx or bp as a base with si or
    /// di as an index, and there is no scaling at all. Rejecting the rest here
    /// is the difference between a clear message and a silently wrong address.
    /// </summary>
    private static void SetSixteen(Operand op, List<int> regs, List<int> scales, string file, int line)
    {
        foreach (int s in scales)
        {
            if (s != 1)
            {
                throw new AsmException(file, line, $"'{op.Text}' scales an index, which 16-bit addressing cannot encode");
            }
        }

        const int Bx = 3, Bp = 5, Si = 6, Di = 7;

        foreach (int r in regs)
        {
            if (r is not (Bx or Bp or Si or Di))
            {
                throw new AsmException(file, line,
                    $"'{op.Text}' uses {Reg16[r]} as an address register; 16-bit addressing allows only bx, bp, si and di");
            }
        }

        if (regs.Count == 2)
        {
            int a = regs[0], b = regs[1];
            bool first = a is Bx or Bp;
            int baseReg = first ? a : b;
            int index = first ? b : a;
            if (baseReg is not (Bx or Bp) || index is not (Si or Di))
            {
                throw new AsmException(file, line,
                    $"'{op.Text}' is not one of the four 16-bit base+index forms (bx or bp, plus si or di)");
            }
            op.Base = baseReg;
            op.Index = index;
            return;
        }

        if (regs.Count == 1)
        {
            if (regs[0] is Si or Di)
            {
                op.Index = regs[0];
                return;
            }
            op.Base = regs[0];
        }
    }

    private static void SetThirtyTwo(Operand op, List<int> regs, List<int> scales, string file, int line)
    {
        const int Esp = 4;

        if (regs.Count == 2)
        {
            // Whichever one carries a scale is the index; esp can never be one,
            // because the encoding uses that slot to mean "no index".
            int first = scales[0] != 1 ? 0 : (scales[1] != 1 ? 1 : (regs[0] == Esp ? 0 : 1));
            op.Index = regs[first];
            op.Scale = scales[first];
            op.Base = regs[1 - first];
            if (op.Index == Esp)
            {
                throw new AsmException(file, line, $"'{op.Text}' uses esp as an index, which cannot be encoded");
            }
        }
        else if (regs.Count == 1)
        {
            if (scales[0] != 1)
            {
                op.Index = regs[0];
                op.Scale = scales[0];
                if (op.Index == Esp)
                {
                    throw new AsmException(file, line, $"'{op.Text}' uses esp as an index, which cannot be encoded");
                }
            }
            else
            {
                op.Base = regs[0];
            }
        }

        if (op.Scale is not (1 or 2 or 4 or 8))
        {
            throw new AsmException(file, line, $"'{op.Text}' scales by {op.Scale}; only 1, 2, 4 and 8 can be encoded");
        }
    }

    /// <summary>Splits an address on the + and - that join its terms, leaving parenthesised sub-expressions alone.</summary>
    private static List<(int Sign, string Term)> SplitTerms(string text, string file, int line)
    {
        List<(int, string)> terms = new();
        int depth = 0;
        int sign = 1;
        int start = 0;
        bool inChar = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inChar)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }
                continue;
            }
            if (c == '\'')
            {
                inChar = true;
                continue;
            }
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                depth--;
                continue;
            }
            if (depth != 0 || (c != '+' && c != '-'))
            {
                continue;
            }
            // A sign that follows an operator rather than a term belongs to the
            // term after it, so `[bx + -4]` and `[bx - -4]` both work.
            if (text[start..i].Trim().Length == 0)
            {
                continue;
            }
            terms.Add((sign, text[start..i]));
            sign = c == '+' ? 1 : -1;
            start = i + 1;
        }

        if (depth != 0)
        {
            throw new AsmException(file, line, $"unbalanced parentheses in '{text.Trim()}'");
        }
        terms.Add((sign, text[start..]));
        return terms;
    }

    private static string FirstWord(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '[')
        {
            i++;
        }
        return text[..i];
    }

    public static string Lower(string text) => text.ToLowerInvariant();

    /// <summary>A symbol table with nothing in it, for a scale factor that may only be a literal.</summary>
    private sealed class Nothing : ISymbols
    {
        public static readonly Nothing Instance = new();

        public bool TryLookup(string name, out long value)
        {
            value = 0;
            return false;
        }
    }
}
