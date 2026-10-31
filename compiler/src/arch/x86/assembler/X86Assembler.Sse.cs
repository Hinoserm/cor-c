#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Asm;

/// <summary>
/// SSE AND SSE2: the XMM registers, the Pentium III's single precision and
/// the Pentium 4's and Athlon 64's double precision and integers in them,
/// and the integer half of SSE on the MMX registers (AMD's extended MMX).
/// Every form is [mandatory prefix] 0F op /r, sometimes with an immediate
/// byte after the operand, and the prefix sits between the legacy prefixes
/// and REX (SsePrefixes): a 66, F2 or F3 that says which of four
/// instructions the opcode is, not an operand size. The integer forms on XMM
/// are the MMX table's own opcodes behind a 66.
/// </summary>
public sealed partial class X86Assembler
{
    /// <summary>What an arithmetic suffix says: the mandatory prefix, the feature, the memory operand's bytes.</summary>
    private static (byte Prefix, X86Feature Feature, int Bytes)? FloatSuffix(string suffix) => suffix switch
    {
        "ps" => (0, X86Feature.Sse, 16),
        "pd" => (0x66, X86Feature.Sse2, 16),
        "ss" => (0xF3, X86Feature.Sse, 4),
        "sd" => (0xF2, X86Feature.Sse2, 8),
        _ => null,
    };

    /// <summary>The float arithmetic, two operands, ps/pd/ss/sd: add, mul, sub, min, div, max, sqrt.</summary>
    private static readonly Dictionary<string, byte> SseArithmetic = new(StringComparer.Ordinal)
    {
        ["add"] = 0x58, ["mul"] = 0x59, ["sub"] = 0x5C, ["min"] = 0x5D, ["div"] = 0x5E, ["max"] = 0x5F, ["sqrt"] = 0x51,
    };

    /// <summary>The ps-and-ss-only ones (SSE): the reciprocal estimates.</summary>
    private static readonly Dictionary<string, byte> SseSingleOnly = new(StringComparer.Ordinal)
    {
        ["rcp"] = 0x53, ["rsqrt"] = 0x52,
    };

    /// <summary>The bitwise ones and the unpacks, ps and pd only: sixteen bytes always.</summary>
    private static readonly Dictionary<string, byte> SsePackedOnly = new(StringComparer.Ordinal)
    {
        ["and"] = 0x54, ["andn"] = 0x55, ["or"] = 0x56, ["xor"] = 0x57, ["unpckl"] = 0x14, ["unpckh"] = 0x15,
    };

    /// <summary>The compare predicates CMPPS and its kin take as their immediate, by the names the pseudo-forms spell.</summary>
    private static readonly string[] ComparePredicates = ["eq", "lt", "le", "unord", "neq", "nlt", "nle", "ord"];

    /// <summary>
    /// The SSE2 integer operations that are not MMX's own (paddq and its
    /// kin, the qword unpacks) and AMD's extended MMX's, which SSE2 widens to
    /// XMM: the opcode behind 0F, and whether an MMX register may take it.
    /// </summary>
    private static readonly Dictionary<string, (byte Op, bool OnMmx, X86Feature MmxFeature)> SseIntegerExtra = new(StringComparer.Ordinal)
    {
        ["paddq"] = (0xD4, true, X86Feature.Sse2), ["psubq"] = (0xFB, true, X86Feature.Sse2), ["pmuludq"] = (0xF4, true, X86Feature.Sse2),
        ["punpcklqdq"] = (0x6C, false, X86Feature.None), ["punpckhqdq"] = (0x6D, false, X86Feature.None),
        ["pavgb"] = (0xE0, true, X86Feature.MmxExtensions), ["pavgw"] = (0xE3, true, X86Feature.MmxExtensions),
        ["pmaxsw"] = (0xEE, true, X86Feature.MmxExtensions), ["pmaxub"] = (0xDE, true, X86Feature.MmxExtensions),
        ["pminsw"] = (0xEA, true, X86Feature.MmxExtensions), ["pminub"] = (0xDA, true, X86Feature.MmxExtensions),
        ["pmulhuw"] = (0xE4, true, X86Feature.MmxExtensions), ["psadbw"] = (0xF6, true, X86Feature.MmxExtensions),
    };

    /// <summary>The conversions: destination kind, source kind ('x' XMM, 'm' MMX, 'r' general), memory bytes, prefix, opcode, feature.</summary>
    private static readonly Dictionary<string, (char To, char From, int Bytes, byte Prefix, byte Op, X86Feature Feature)> SseConversions = new(StringComparer.Ordinal)
    {
        ["cvtpi2ps"] = ('x', 'm', 8, 0, 0x2A, X86Feature.Sse),
        ["cvtps2pi"] = ('m', 'x', 8, 0, 0x2D, X86Feature.Sse),
        ["cvttps2pi"] = ('m', 'x', 8, 0, 0x2C, X86Feature.Sse),
        ["cvtsi2ss"] = ('x', 'r', 4, 0xF3, 0x2A, X86Feature.Sse),
        ["cvtss2si"] = ('r', 'x', 4, 0xF3, 0x2D, X86Feature.Sse),
        ["cvttss2si"] = ('r', 'x', 4, 0xF3, 0x2C, X86Feature.Sse),
        ["cvtsi2sd"] = ('x', 'r', 4, 0xF2, 0x2A, X86Feature.Sse2),
        ["cvtsd2si"] = ('r', 'x', 8, 0xF2, 0x2D, X86Feature.Sse2),
        ["cvttsd2si"] = ('r', 'x', 8, 0xF2, 0x2C, X86Feature.Sse2),
        ["cvtps2pd"] = ('x', 'x', 8, 0, 0x5A, X86Feature.Sse2),
        ["cvtpd2ps"] = ('x', 'x', 16, 0x66, 0x5A, X86Feature.Sse2),
        ["cvtss2sd"] = ('x', 'x', 4, 0xF3, 0x5A, X86Feature.Sse2),
        ["cvtsd2ss"] = ('x', 'x', 8, 0xF2, 0x5A, X86Feature.Sse2),
        ["cvtdq2ps"] = ('x', 'x', 16, 0, 0x5B, X86Feature.Sse2),
        ["cvtps2dq"] = ('x', 'x', 16, 0x66, 0x5B, X86Feature.Sse2),
        ["cvttps2dq"] = ('x', 'x', 16, 0xF3, 0x5B, X86Feature.Sse2),
        ["cvtdq2pd"] = ('x', 'x', 8, 0xF3, 0xE6, X86Feature.Sse2),
        ["cvtpd2dq"] = ('x', 'x', 16, 0xF2, 0xE6, X86Feature.Sse2),
        ["cvttpd2dq"] = ('x', 'x', 16, 0x66, 0xE6, X86Feature.Sse2),
        ["cvtpi2pd"] = ('x', 'm', 8, 0x66, 0x2A, X86Feature.Sse2),
        ["cvtpd2pi"] = ('m', 'x', 16, 0x66, 0x2D, X86Feature.Sse2),
        ["cvttpd2pi"] = ('m', 'x', 16, 0x66, 0x2C, X86Feature.Sse2),
    };

    /// <summary>
    /// The moves: the load form's opcode and the store form's (0 where it
    /// has none), the prefix, the memory operand's bytes, the feature, and
    /// whether a register-to-register form exists (movlps and its kin are
    /// memory forms; movhlps and movlhps are their register forms).
    /// </summary>
    private static readonly Dictionary<string, (byte Load, byte Store, byte Prefix, int Bytes, X86Feature Feature, bool Registers)> SseMoves = new(StringComparer.Ordinal)
    {
        ["movaps"] = (0x28, 0x29, 0, 16, X86Feature.Sse, true),
        ["movups"] = (0x10, 0x11, 0, 16, X86Feature.Sse, true),
        ["movapd"] = (0x28, 0x29, 0x66, 16, X86Feature.Sse2, true),
        ["movupd"] = (0x10, 0x11, 0x66, 16, X86Feature.Sse2, true),
        ["movdqa"] = (0x6F, 0x7F, 0x66, 16, X86Feature.Sse2, true),
        ["movdqu"] = (0x6F, 0x7F, 0xF3, 16, X86Feature.Sse2, true),
        ["movss"] = (0x10, 0x11, 0xF3, 4, X86Feature.Sse, true),
        ["movsd"] = (0x10, 0x11, 0xF2, 8, X86Feature.Sse2, true),
        ["movlps"] = (0x12, 0x13, 0, 8, X86Feature.Sse, false),
        ["movhps"] = (0x16, 0x17, 0, 8, X86Feature.Sse, false),
        ["movlpd"] = (0x12, 0x13, 0x66, 8, X86Feature.Sse2, false),
        ["movhpd"] = (0x16, 0x17, 0x66, 8, X86Feature.Sse2, false),
        ["movntps"] = (0, 0x2B, 0, 16, X86Feature.Sse, false),
        ["movntpd"] = (0, 0x2B, 0x66, 16, X86Feature.Sse2, false),
        ["movntdq"] = (0, 0xE7, 0x66, 16, X86Feature.Sse2, false),
    };

    /// <summary>Whether a mnemonic is one of this file's, for IsMnemonic.</summary>
    private static bool IsSseMnemonic(string mn)
    {
        if (SseMoves.ContainsKey(mn) || SseConversions.ContainsKey(mn) || SseIntegerExtra.ContainsKey(mn)) return true;
        if (mn is "comiss" or "ucomiss" or "comisd" or "ucomisd" or "movhlps" or "movlhps" or "movmskps" or "movmskpd"
            or "movnti" or "movntq" or "maskmovq" or "maskmovdqu" or "movq2dq" or "movdq2q" or "shufps" or "shufpd"
            or "pshufd" or "pshufhw" or "pshuflw" or "pshufw" or "pextrw" or "pinsrw" or "pmovmskb" or "pslldq" or "psrldq"
            or "ldmxcsr" or "stmxcsr" or "fxsave" or "fxrstor" or "lfence" or "mfence" or "clflush" or "pause")
            return true;
        if (mn.Length > 2 && FloatSuffix(mn[^2..]) is not null)
        {
            string stem = mn[..^2];
            if (SseArithmetic.ContainsKey(stem) || SsePackedOnly.ContainsKey(stem) || stem == "cmp") return true;
            if (SseSingleOnly.ContainsKey(stem) && mn[^2..] is "ps" or "ss") return true;
            if (stem.StartsWith("cmp", StringComparison.Ordinal) && Array.IndexOf(ComparePredicates, stem[3..]) >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// The legacy prefixes a memory operand asks for (a segment override,
    /// 67), then the mandatory one, then REX's place: REX must come right
    /// before the opcode's 0F, after the mandatory 66, F2 or F3.
    /// </summary>
    private void SsePrefixes(byte mandatory, Operand? mem, bool wide = false)
    {
        if (mem is not null && mem.Kind == OperandKind.Memory)
        {
            if (mem.Segment >= 0) Emit(Operands.SegPrefix[mem.Segment]);
            if (mem.AddrSize != _bits) Emit(0x67);
        }
        if (mandatory != 0) Emit(mandatory);
        if (_bits == 64)
        {
            if (wide) _rexW = true;
            _rexAt = _sec.Size;
        }
    }

    /// <summary>[prefix] 0F op /r [ib]: `reg` the ModRM reg field, `rm` the register or memory.</summary>
    private void SseEmit(byte mandatory, byte op, int reg, Operand rm, int? imm = null, bool wide = false)
    {
        SsePrefixes(mandatory, MemOf(rm), wide);
        Emit(0x0f, op);
        EmitRM(reg, rm);
        if (imm is int value) Emit((byte)value);
    }

    /// <summary>An XMM register, or memory of `bytes` (or unsized): a source.</summary>
    private void XmmSource(Operand o, int bytes, string mn)
    {
        if (o.Kind != OperandKind.Xmm && o.Kind != OperandKind.Memory)
            throw Error($"{mn} requires an XMM register or memory source");
        if (o.Kind == OperandKind.Memory && o.SizeGiven && o.Size != bytes)
            throw Error($"{mn} requires a {bytes * 8}-bit memory operand");
    }

    private void NeedXmm(Operand o, string mn, string what)
    {
        if (o.Kind != OperandKind.Xmm) throw Error($"{mn} requires an XMM register {what}");
    }

    private int Imm8(Operand o, string mn)
    {
        if (o.Kind != OperandKind.Immediate) throw Error($"{mn} requires an immediate byte");
        long v = Val(o.Value);
        if (v < -128 || v > 255) throw Error($"{mn}: {v} does not fit in a byte");
        return (int)(v & 0xFF);
    }

    private bool SseInstruction(string mn, string[] a)
    {
        // The string instructions MOVSD and CMPSD take no operands; the SSE2
        // ones of the same names always do.
        if (mn is "movsd" or "cmpsd" && a.Length == 0) return false;
        if (mn is "movd" or "movq") return a.Length == 2 && XmmMovdMovq(mn, P(a[0]), P(a[1]));
        if (!IsSseMnemonic(mn) && !(MmxOpcodes.ContainsKey(mn) && a.Length > 0 && P(a[0]).Kind == OperandKind.Xmm)) return false;

        // ---- moves ----
        if (SseMoves.TryGetValue(mn, out var move))
        {
            Require(Allowed(move.Feature), mn, Describe(move.Feature)); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (d.Kind == OperandKind.Xmm && move.Load != 0)
            {
                if (s.Kind == OperandKind.Xmm && !move.Registers) throw Error($"{mn} moves between memory and a register");
                XmmSource(s, move.Bytes, mn);
                SseEmit(move.Prefix, move.Load, d.Reg, s);
                return true;
            }
            if (d.Kind == OperandKind.Memory && s.Kind == OperandKind.Xmm)
            {
                if (d.SizeGiven && d.Size != move.Bytes) throw Error($"{mn} requires a {move.Bytes * 8}-bit memory operand");
                SseEmit(move.Prefix, move.Store, s.Reg, d);
                return true;
            }
            throw Error($"{mn} requires an XMM register and an XMM register or memory operand");
        }
        if (mn is "movhlps" or "movlhps")
        {
            Require(Allowed(X86Feature.Sse), mn, "SSE"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "destination"); NeedXmm(s, mn, "source");
            SseEmit(0, mn == "movhlps" ? (byte)0x12 : (byte)0x16, d.Reg, s);
            return true;
        }
        if (mn is "movmskps" or "movmskpd" or "pmovmskb")
        {
            Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (d.Kind != OperandKind.Register || d.Size is not (4 or 8)) throw Error($"{mn} requires a 32- or 64-bit general register destination");
            if (mn == "pmovmskb" && s.Kind == OperandKind.Mmx)
            {
                Require(Allowed(X86Feature.MmxExtensions), mn, "SSE or extended MMX");
                SseEmit(0, 0xD7, d.Reg, s);
                return true;
            }
            NeedXmm(s, mn, "source");
            X86Feature need = mn == "movmskps" ? X86Feature.Sse : X86Feature.Sse2;
            Require(Allowed(need), mn, Describe(need));
            SseEmit(mn == "movmskps" ? (byte)0 : (byte)0x66, mn == "pmovmskb" ? (byte)0xD7 : (byte)0x50, d.Reg, s);
            return true;
        }
        if (mn == "movnti")
        {
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (d.Kind != OperandKind.Memory || s.Kind != OperandKind.Register || s.Size is not (4 or 8)
                || d.SizeGiven && d.Size != s.Size)
                throw Error("movnti stores a 32- or 64-bit general register to memory");
            SseEmit(0, 0xC3, s.Reg, d, wide: s.Size == 8);
            return true;
        }
        if (mn is "movntq" or "maskmovq")
        {
            Require(Allowed(X86Feature.MmxExtensions), mn, "SSE or extended MMX"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (mn == "movntq")
            {
                if (d.Kind != OperandKind.Memory || s.Kind != OperandKind.Mmx) throw Error("movntq stores an MMX register to memory");
                SseEmit(0, 0xE7, s.Reg, d);
            }
            else
            {
                if (d.Kind != OperandKind.Mmx || s.Kind != OperandKind.Mmx) throw Error("maskmovq takes two MMX registers");
                SseEmit(0, 0xF7, d.Reg, s);
            }
            return true;
        }
        if (mn == "maskmovdqu")
        {
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "first"); NeedXmm(s, mn, "second");
            SseEmit(0x66, 0xF7, d.Reg, s);
            return true;
        }
        if (mn is "movq2dq" or "movdq2q")
        {
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (mn == "movq2dq" ? d.Kind != OperandKind.Xmm || s.Kind != OperandKind.Mmx : d.Kind != OperandKind.Mmx || s.Kind != OperandKind.Xmm)
                throw Error(mn == "movq2dq" ? "movq2dq moves an MMX register into an XMM register" : "movdq2q moves an XMM register into an MMX register");
            SseEmit(mn == "movq2dq" ? (byte)0xF3 : (byte)0xF2, 0xD6, d.Reg, s);
            return true;
        }

        // ---- conversions ----
        if (SseConversions.TryGetValue(mn, out var cvt))
        {
            Require(Allowed(cvt.Feature), mn, Describe(cvt.Feature)); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            bool wide = false;
            switch (cvt.To)
            {
                case 'x': NeedXmm(d, mn, "destination"); break;
                case 'm': if (d.Kind != OperandKind.Mmx) throw Error($"{mn} requires an MMX register destination"); break;
                default:
                    if (d.Kind != OperandKind.Register || d.Size is not (4 or 8)) throw Error($"{mn} requires a 32- or 64-bit general register destination");
                    wide = d.Size == 8;
                    break;
            }
            switch (cvt.From)
            {
                case 'x': XmmSource(s, cvt.Bytes, mn); break;
                case 'm':
                    if (s.Kind != OperandKind.Mmx && s.Kind != OperandKind.Memory) throw Error($"{mn} requires an MMX register or memory source");
                    if (s.Kind == OperandKind.Memory && s.SizeGiven && s.Size != 8) throw Error($"{mn} requires a 64-bit memory operand");
                    break;
                default:
                    if (s.Kind == OperandKind.Register)
                    {
                        if (s.Size is not (4 or 8)) throw Error($"{mn} requires a 32- or 64-bit general register or memory source");
                        wide = s.Size == 8;
                    }
                    else if (s.Kind == OperandKind.Memory)
                    {
                        if (s.SizeGiven && s.Size is not (4 or 8)) throw Error($"{mn} requires a 32- or 64-bit memory operand");
                        wide = s.SizeGiven && s.Size == 8;
                    }
                    else throw Error($"{mn} requires a general register or memory source");
                    break;
            }
            if (wide && _bits != 64) throw Error($"{mn} with a 64-bit integer needs .bits 64");
            SseEmit(cvt.Prefix, cvt.Op, d.Reg, s, wide: wide);
            return true;
        }

        // ---- compares ----
        if (mn is "comiss" or "ucomiss" or "comisd" or "ucomisd")
        {
            bool single = mn.EndsWith("ss", StringComparison.Ordinal);
            Require(Allowed(single ? X86Feature.Sse : X86Feature.Sse2), mn, single ? "SSE" : "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "first operand"); XmmSource(s, single ? 4 : 8, mn);
            SseEmit(single ? (byte)0 : (byte)0x66, mn.StartsWith('u') ? (byte)0x2E : (byte)0x2F, d.Reg, s);
            return true;
        }
        if (mn.Length > 5 && mn.StartsWith("cmp", StringComparison.Ordinal) && FloatSuffix(mn[^2..]) is { } compare)
        {
            string predicate = mn[3..^2];
            int fixedPredicate = predicate.Length == 0 ? -1 : Array.IndexOf(ComparePredicates, predicate);
            if (predicate.Length > 0 && fixedPredicate < 0) return false;
            Require(Allowed(compare.Feature), mn, Describe(compare.Feature));
            Need(a, fixedPredicate < 0 ? 3 : 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "destination"); XmmSource(s, compare.Bytes, mn);
            int imm = fixedPredicate >= 0 ? fixedPredicate : Imm8(P(a[2]), mn);
            if (imm > 7) throw Error($"{mn}: the predicate is 0 to 7");
            SseEmit(compare.Prefix, 0xC2, d.Reg, s, imm);
            return true;
        }

        // ---- shuffles ----
        if (mn is "shufps" or "shufpd" or "pshufd" or "pshufhw" or "pshuflw")
        {
            X86Feature need = mn == "shufps" ? X86Feature.Sse : X86Feature.Sse2;
            Require(Allowed(need), mn, Describe(need)); Need(a, 3, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "destination"); XmmSource(s, 16, mn);
            byte prefix = mn switch { "shufps" => 0, "shufpd" or "pshufd" => 0x66, "pshufhw" => 0xF3, _ => 0xF2 };
            SseEmit(prefix, mn.StartsWith("shuf", StringComparison.Ordinal) ? (byte)0xC6 : (byte)0x70, d.Reg, s, Imm8(P(a[2]), mn));
            return true;
        }
        if (mn == "pshufw")
        {
            Require(Allowed(X86Feature.MmxExtensions), mn, "SSE or extended MMX"); Need(a, 3, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (d.Kind != OperandKind.Mmx) throw Error("pshufw requires an MMX register destination");
            MmxSource(s, 8, mn);
            SseEmit(0, 0x70, d.Reg, s, Imm8(P(a[2]), mn));
            return true;
        }
        if (mn is "pextrw" or "pinsrw")
        {
            Need(a, 3, mn);
            Operand d = P(a[0]), s = P(a[1]);
            Operand vector = mn == "pextrw" ? s : d;
            bool xmm = vector.Kind == OperandKind.Xmm;
            if (!xmm && vector.Kind != OperandKind.Mmx) throw Error($"{mn} requires an MMX or XMM register");
            Require(Allowed(xmm ? X86Feature.Sse2 : X86Feature.MmxExtensions), mn, xmm ? "SSE2" : "SSE or extended MMX");
            int imm = Imm8(P(a[2]), mn);
            if (mn == "pextrw")
            {
                if (d.Kind != OperandKind.Register || d.Size is not (4 or 8)) throw Error("pextrw requires a 32- or 64-bit general register destination");
                SseEmit(xmm ? (byte)0x66 : (byte)0, 0xC5, d.Reg, s, imm);
            }
            else
            {
                if (!(s.Kind == OperandKind.Register && s.Size is 4 or 8) && !(s.Kind == OperandKind.Memory && (!s.SizeGiven || s.Size == 2)))
                    throw Error("pinsrw takes a 32-bit general register or 16-bit memory source");
                SseEmit(xmm ? (byte)0x66 : (byte)0, 0xC4, d.Reg, s, imm);
            }
            return true;
        }

        // ---- the integer operations ----
        if (mn is "pslldq" or "psrldq")
        {
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]);
            NeedXmm(d, mn, "destination");
            SseEmit(0x66, 0x73, mn == "pslldq" ? 7 : 3, d, Imm8(P(a[1]), mn));
            return true;
        }
        if (SseIntegerExtra.TryGetValue(mn, out var extra))
        {
            Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            if (d.Kind == OperandKind.Mmx && extra.OnMmx)
            {
                Require(Allowed(extra.MmxFeature), mn, Describe(extra.MmxFeature));
                MmxSource(s, 8, mn);
                SseEmit(0, extra.Op, d.Reg, s);
                return true;
            }
            NeedXmm(d, mn, "destination");
            Require(Allowed(X86Feature.Sse2), mn, "SSE2");
            XmmSource(s, 16, mn);
            SseEmit(0x66, extra.Op, d.Reg, s);
            return true;
        }
        if (MmxOpcodes.TryGetValue(mn, out byte integer))
        {
            // MMX's own, on XMM registers: SSE2, behind a 66.
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "destination");
            if (mn.StartsWith("ps", StringComparison.Ordinal) && mn.Length == 5 && (mn[2] == 'l' || mn[2] == 'r') && s.Kind == OperandKind.Immediate)
            {
                byte group = mn[4] switch { 'w' => (byte)0x71, 'd' => (byte)0x72, _ => (byte)0x73 };
                if (group == 0x73 && mn[3] == 'a') throw Error($"{mn} has no immediate form");
                SseEmit(0x66, group, mn[2] == 'l' ? 6 : mn[3] == 'a' ? 4 : 2, d, Imm8(s, mn));
                return true;
            }
            XmmSource(s, 16, mn);
            SseEmit(0x66, integer, d.Reg, s);
            return true;
        }

        // ---- state, fences, hints ----
        if (mn is "ldmxcsr" or "stmxcsr" or "fxsave" or "fxrstor" or "clflush")
        {
            X86Feature need = mn == "clflush" ? X86Feature.Sse2 : X86Feature.Sse;
            Require(Allowed(need) || _runtimeInstructions.Contains(mn), mn, Describe(need) + " or .runtime " + (mn == "clflush" ? "clflush" : mn.StartsWith("fx") ? "fxsr" : "sse"));
            Need(a, 1, mn);
            Operand m = P(a[0]);
            if (m.Kind != OperandKind.Memory) throw Error($"{mn} requires memory");
            if (mn is "ldmxcsr" or "stmxcsr" && m.SizeGiven && m.Size != 4) throw Error($"{mn} requires a 32-bit memory operand");
            SseEmit(0, 0xAE, mn switch { "fxsave" => 0, "fxrstor" => 1, "ldmxcsr" => 2, "stmxcsr" => 3, _ => 7 }, m);
            return true;
        }
        if (mn is "lfence" or "mfence")
        {
            Require(Allowed(X86Feature.Sse2), mn, "SSE2"); Need(a, 0, mn);
            Emit(0x0f, 0xae, mn == "lfence" ? (byte)0xe8 : (byte)0xf0);
            return true;
        }
        if (mn == "pause")
        {
            // REP NOP: a hint every processor runs, the older ones as a NOP.
            Need(a, 0, mn); Emit(0xf3, 0x90);
            return true;
        }

        // ---- the float arithmetic ----
        if (mn.Length > 2 && FloatSuffix(mn[^2..]) is { } form)
        {
            string stem = mn[..^2], suffix = mn[^2..];
            byte op;
            if (SseArithmetic.TryGetValue(stem, out byte arithmetic)) op = arithmetic;
            else if (SseSingleOnly.TryGetValue(stem, out byte single) && suffix is "ps" or "ss") op = single;
            else if (SsePackedOnly.TryGetValue(stem, out byte packed) && suffix is "ps" or "pd") op = packed;
            else return false;
            Require(Allowed(form.Feature), mn, Describe(form.Feature)); Need(a, 2, mn);
            Operand d = P(a[0]), s = P(a[1]);
            NeedXmm(d, mn, "destination"); XmmSource(s, form.Bytes, mn);
            SseEmit(form.Prefix, op, d.Reg, s);
            return true;
        }
        return false;
    }

    /// <summary>
    /// MOVD and MOVQ with an XMM register (SSE2): to or from a general
    /// register or memory, and MOVQ between two XMM registers. The MMX forms
    /// are ExtendedInstruction's; this answers false for them.
    /// </summary>
    private bool XmmMovdMovq(string mn, Operand d, Operand s)
    {
        if (d.Kind != OperandKind.Xmm && s.Kind != OperandKind.Xmm) return false;
        Require(Allowed(X86Feature.Sse2), mn, "SSE2");
        bool load = d.Kind == OperandKind.Xmm;
        Operand xmm = load ? d : s, other = load ? s : d;
        if (other.Kind == OperandKind.Xmm)
        {
            if (mn != "movq") throw Error("movd moves between an XMM register and a general register or memory");
            SseEmit(0xF3, 0x7E, d.Reg, s);
            return true;
        }
        if (other.Kind == OperandKind.Register)
        {
            if (other.Size == 8 && _bits != 64) throw Error($"{mn} with a 64-bit register needs .bits 64");
            if (other.Size != (mn == "movd" ? 4 : 8) && !(mn == "movq" && other.Size == 8))
                throw Error(mn == "movd" ? "movd requires a 32-bit general register" : "movq requires a 64-bit general register");
            SseEmit(0x66, load ? (byte)0x6E : (byte)0x7E, xmm.Reg, other, wide: other.Size == 8);
            return true;
        }
        if (other.Kind != OperandKind.Memory) throw Error($"{mn} requires a register or memory operand beside the XMM register");
        int bytes = mn == "movd" ? 4 : 8;
        if (other.SizeGiven && other.Size != bytes) throw Error($"{mn} requires a {bytes * 8}-bit memory operand");
        if (mn == "movd") SseEmit(0x66, load ? (byte)0x6E : (byte)0x7E, xmm.Reg, other);
        else if (load) SseEmit(0xF3, 0x7E, xmm.Reg, other);
        else SseEmit(0x66, 0xD6, xmm.Reg, other);
        return true;
    }

    private static string Describe(X86Feature feature) => feature switch
    {
        X86Feature.Sse => "SSE (Pentium III, Athlon XP)",
        X86Feature.Sse2 => "SSE2 (Pentium 4, Athlon 64)",
        X86Feature.MmxExtensions => "SSE or extended MMX (Pentium III, Athlon)",
        X86Feature.None => "nothing",
        _ => feature.ToString(),
    };
}
