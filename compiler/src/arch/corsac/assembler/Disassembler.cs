#nullable enable
using System.Text;

namespace Corsac;

public static class Disassembler
{
    /// <summary>
    /// Renders one instruction. <paramref name="pc"/> resolves relative targets,
    /// and <paramref name="labels"/> lets branch targets print as names so the
    /// output can be reassembled.
    /// </summary>
    public static string Line(ulong word, long pc, IReadOnlyDictionary<long, string>? labels = null)
    {
        Op op = Isa.OpOf(word);
        if (!Isa.IsDefined(op))
        {
            return $".quad 0x{word:x16}";
        }

        Size sz = Isa.SizeOf(word);
        string m = Isa.Mnemonic(op) + (Isa.UsesSize(op) && sz != Size.D ? "." + Isa.SizeSuffix(sz) : "");

        bool fp = Isa.ReadsFpBank(op) || Isa.WritesFpBank(op);
        // An element accessor carries a float in rd and integers everywhere else.
        bool split = Isa.FpValueIntAddress(op);
        bool srcFp = fp && !split;
        bool widenedVector = Isa.IsVectorSize(sz) && Isa.IsVectorizable(op);
        string Dst(int i) => Isa.WritesFpBank(op) || split ? Isa.FpRegName(i) : Isa.RegName(i);
        string Src(int i) => srcFp ? Isa.FpRegName(i) : Isa.RegName(i);
        string V(int i) => "v" + i.ToString();
        string VM(int i) => "vm" + i.ToString();
        string D(int i) => "d" + i.ToString();
        string Overlay(string operands)
        {
            int overlay = Isa.Imm16(word) & 0xFF;
            return overlay == 0 ? operands : $"{operands}, 0x{overlay:X2}";
        }

        string Target(long at)
        {
            if (labels is not null && labels.TryGetValue(at, out string? name))
            {
                return name;
            }
            return at.ToString();
        }

        if (op is Op.DAdd or Op.DSub or Op.DMul or Op.DDiv or Op.DRem)
            return $"{m} {D(Isa.Rd(word))}, {D(Isa.Rs1(word))}, {D(Isa.Rs2(word))}";
        if (op == Op.DCmp)
            return $"{m} {Isa.RegName(Isa.Rd(word))}, {D(Isa.Rs1(word))}, {D(Isa.Rs2(word))}";
        if (op is Op.DNeg or Op.DAbs or Op.DTrunc or Op.DFloor or Op.DCeil)
            return $"{m} {D(Isa.Rd(word))}, {D(Isa.Rs1(word))}";
        if (op is Op.DRound or Op.DScale)
            return $"{m} {D(Isa.Rd(word))}, {D(Isa.Rs1(word))}, {Isa.Wide(word)}";
        if (op == Op.DFromI)
            return $"{m} {D(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}";
        if (op == Op.DToI)
            return $"{m} {Isa.RegName(Isa.Rd(word))}, {D(Isa.Rs1(word))}";
        if (op == Op.DFromF)
            return $"{m} {D(Isa.Rd(word))}, {Isa.FpRegName(Isa.Rs1(word))}";
        if (op == Op.DToF)
            return $"{m} {Isa.FpRegName(Isa.Rd(word))}, {D(Isa.Rs1(word))}";
        if (op == Op.CvtF2I)
        {
            int rounding = Isa.Imm16(word);
            string operands = $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.FpRegName(Isa.Rs1(word))}";
            return rounding == 0 ? operands : $"{operands}, {rounding}";
        }
        if (op == Op.MTD)
            return $"{m} {D(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}";
        if (op == Op.MFD)
            return $"{m} {Isa.RegName(Isa.Rd(word))}, {D(Isa.Rs1(word))}";
        if (op is Op.DLd or Op.DSt)
            return $"{m} {D(Isa.Rd(word))}, {Isa.Wide(word)}({Isa.RegName(Isa.Rs1(word))})";
        if (op is Op.HMD5 or Op.HSHA1 or Op.HSHA2)
            return $"{m} {Isa.RegName(Isa.Rs1(word))}";
        if (op == Op.HInit)
            return $"{m} {Isa.Wide(word)}";
        if (op == Op.Rand)
            return $"{m} {Isa.RegName(Isa.Rd(word))}";
        if (op == Op.DPack)
            return $"{m} {Isa.RegName(Isa.Rd(word))}, {D(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}";
        if (op == Op.DUnpk)
            return $"{m} {D(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}";

        if (Isa.IsVectorInstruction(op))
        {
            return op switch
            {
                Op.VLen => $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}",
                Op.VLd or Op.VSt => Overlay($"{m} {V(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}"),
                Op.VSplat => Overlay($"{m} {V(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}"),
                Op.VLdS or Op.VStS => Overlay($"{m} {V(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}"),
                Op.VGath or Op.VScat => Overlay($"{m} {V(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {V(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}"),
                Op.VSel or Op.VShuf => Overlay($"{m} {V(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {V(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}"),
                Op.VCmp => Overlay($"{m} {VM(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {V(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}"),
                Op.VRed => Overlay($"{m} {Isa.RegName(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}"),
                Op.MTV => Overlay($"{m} {V(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}"),
                Op.MFV => Overlay($"{m} {Isa.RegName(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}"),
                _ => $".quad 0x{word:x16}",
            };
        }

        switch (Isa.FormatOf(op))
        {
            case Fmt.None:
                return m;

            case Fmt.R:
                if (op == Op.TmrSet)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}";
                }
                if (op == Op.TmrGet)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}";
                }
                if (op == Op.LpicGet)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}";
                }
                if (op == Op.TlbInv)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Isa.Imm16(word)}";
                }
                if (widenedVector)
                {
                    return $"{m} {V(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {V(Isa.Rs2(word))}, 0x{Isa.Imm16(word) & 0x1F:X2}";
                }
                return $"{m} {Dst(Isa.Rd(word))}, {Src(Isa.Rs1(word))}, {Src(Isa.Rs2(word))}";

            case Fmt.R4:
            {
                if (widenedVector)
                {
                    return $"{m} {V(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {V(Isa.Rs2(word))}, {V(Isa.Rs3(word))}, 0x{Isa.Imm16(word) & 0x1F:X2}";
                }
                if (((int)op >> 9) == 0x0A)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}, {Isa.Imm16(word)}";
                }
                bool condIsInt = op is Op.CSel or Op.FCSel;
                string s3 = (fp && !split && !condIsInt) ? Isa.FpRegName(Isa.Rs3(word)) : Isa.RegName(Isa.Rs3(word));
                return $"{m} {Dst(Isa.Rd(word))}, {Src(Isa.Rs1(word))}, {Src(Isa.Rs2(word))}, {s3}";
            }

            case Fmt.R4I:
                if (op is Op.BitGet or Op.BitPut or Op.BitPeek or Op.BitFlush)
                {
                    return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, "
                         + $"{Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}, "
                         + $"{(Isa.Imm16(word) >> 3) & 0x1F}";
                }
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, "
                     + $"{Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}, {Isa.Imm16(word)}";

            case Fmt.R4S:
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, "
                     + $"{Isa.RegName(Isa.Rs2(word))}, {Isa.RegName(Isa.Rs3(word))}, {Isa.Imm16(word)}";

            case Fmt.RS:
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, "
                     + $"{Isa.RegName(Isa.Rs2(word))}, {Isa.Imm16(word)}";

            case Fmt.R1:
            {
                if (widenedVector)
                {
                    return $"{m} {V(Isa.Rd(word))}, {V(Isa.Rs1(word))}, 0x{Isa.Imm16(word) & 0x1F:X2}";
                }
                string dst = Isa.WritesFpBank(op) ? Isa.FpRegName(Isa.Rd(word)) : Isa.RegName(Isa.Rd(word));
                string src = Isa.ReadsFpBank(op)  ? Isa.FpRegName(Isa.Rs1(word)) : Isa.RegName(Isa.Rs1(word));
                return $"{m} {dst}, {src}";
            }

            case Fmt.I:
                if (widenedVector)
                {
                    return $"{m} {V(Isa.Rd(word))}, {V(Isa.Rs1(word))}, {Isa.VectorWide(word)}, 0x{Isa.Imm16(word) & 0x1F:X2}";
                }
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.RegName(Isa.Rs1(word))}, {Isa.Wide(word)}";

            case Fmt.M:
            {
                string dst = fp ? Isa.FpRegName(Isa.Rd(word)) : Isa.RegName(Isa.Rd(word));
                return $"{m} {dst}, {Isa.Wide(word)}({Isa.RegName(Isa.Rs1(word))})";
            }

            case Fmt.B:
                if (op == Op.Djnz)
                {
                    return $"{m} {Isa.RegName(Isa.Rs1(word))}, {Target(pc + 1 + Isa.BrDisp(word))}";
                }
                return $"{m} {Isa.RegName(Isa.Rs1(word))}, {Isa.RegName(Isa.Rs2(word))}, {Target(pc + 1 + Isa.BrDisp(word))}";

            case Fmt.J:
                return $"{m} {Target(pc + 1 + Isa.JmpDisp(word))}";

            case Fmt.Reg:
                return $"{m} {Isa.RegName(Isa.Rs1(word))}";

            case Fmt.OutReg:
                return $"{m} {Isa.RegName(Isa.Rd(word))}";

            case Fmt.OutImm:
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.Imm16(word)}";

            case Fmt.ImmReg:
                return $"{m} {Isa.Imm16(word)}, {Isa.RegName(Isa.Rs1(word))}";

            case Fmt.Imm:
                return $"{m} {Isa.Wide(word)}";

            case Fmt.U:
                return $"{m} {Isa.RegName(Isa.Rd(word))}, {Isa.Wide(word)}";

            case Fmt.Mask:
            {
                int mask = Isa.Mask16(word);
                int first = op is Op.PushM2 or Op.PopM2 ? 16 : 0;
                List<string> regs = new();
                for (int i = 0; i < 16; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        regs.Add(Isa.RegName(first + i));
                    }
                }
                return regs.Count == 0 ? m : $"{m} {string.Join(", ", regs)}";
            }

            default:
                return $".quad 0x{word:x16}";
        }
    }

    /// <summary>Full listing with addresses, encodings and labels.</summary>
    public static string Dump(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Dictionary<long, string> byAddress = new();
        Dictionary<long, List<string>> labelsAt = new();
        foreach ((string name, long at) in image.CodeLabels)
        {
            if (!labelsAt.TryGetValue(at, out List<string>? list))
            {
                list = new List<string>();
                labelsAt[at] = list;
            }
            list.Add(name);
            // Prefer the alphabetically first name when several share an address,
            // so disassembly is stable between runs.
            if (!byAddress.TryGetValue(at, out string? existing) || string.CompareOrdinal(name, existing) < 0)
            {
                byAddress[at] = name;
            }
        }

        StringBuilder sb = new();
        for (long pc = 0; pc < image.Code.Length; pc++)
        {
            EmitLabels(sb, labelsAt, pc);
            sb.Append($"  {pc,5}  {image.Code[pc]:x16}  {Line(image.Code[pc], pc, byAddress)}\n");
        }

        // A label one past the last instruction is a legitimate branch target
        // and would otherwise vanish from the listing.
        EmitLabels(sb, labelsAt, image.Code.Length);


        if (image.DataLabels.Count > 0)
        {
            sb.Append("\n.data\n");
            foreach ((string name, long at) in image.DataLabels.OrderBy(kv => kv.Value))
            {
                sb.Append($"  {at,5}  {name}\n");
            }
        }
        return sb.ToString();
    }

    private static void EmitLabels(StringBuilder sb, Dictionary<long, List<string>> labelsAt, long pc)
    {
        if (!labelsAt.TryGetValue(pc, out List<string>? names))
        {
            return;
        }

        foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.Append(name).Append(":\n");
        }
    }
}
