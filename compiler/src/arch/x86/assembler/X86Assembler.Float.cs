namespace Corsac.Asm;

public sealed partial class X86Assembler
{
    private static readonly Dictionary<string, ushort> FloatBare = new(StringComparer.Ordinal)
    {
        ["f2xm1"] = 0xd9f0, ["fabs"] = 0xd9e1, ["fchs"] = 0xd9e0, ["fnclex"] = 0xdbe2,
        ["fcompp"] = 0xded9, ["fcos"] = 0xd9ff, ["fdecstp"] = 0xd9f6, ["fincstp"] = 0xd9f7,
        ["fninit"] = 0xdbe3, ["fld1"] = 0xd9e8, ["fldl2t"] = 0xd9e9, ["fldl2e"] = 0xd9ea,
        ["fldpi"] = 0xd9eb, ["fldlg2"] = 0xd9ec, ["fldln2"] = 0xd9ed, ["fldz"] = 0xd9ee,
        ["fnop"] = 0xd9d0, ["fpatan"] = 0xd9f3, ["fprem"] = 0xd9f8, ["fprem1"] = 0xd9f5,
        ["fptan"] = 0xd9f2, ["frndint"] = 0xd9fc, ["fscale"] = 0xd9fd, ["fsin"] = 0xd9fe,
        ["fsincos"] = 0xd9fb, ["fsqrt"] = 0xd9fa, ["ftst"] = 0xd9e4, ["fucompp"] = 0xdae9,
        ["fxam"] = 0xd9e5, ["fxtract"] = 0xd9f4, ["fyl2x"] = 0xd9f1, ["fyl2xp1"] = 0xd9f9,
    };
    private static readonly HashSet<string> FloatNames = new(StringComparer.Ordinal)
    {
        "wait", "fwait", "finit", "fclex", "fld", "fst", "fstp", "fild", "fist", "fistp", "fbld", "fbstp",
        "fadd", "fmul", "fsub", "fsubr", "fdiv", "fdivr", "fcom", "fcomp", "fucom", "fucomp",
        "fiadd", "fimul", "fisub", "fisubr", "fidiv", "fidivr", "ficom", "ficomp",
        "faddp", "fmulp", "fsubp", "fsubrp", "fdivp", "fdivrp", "fxch", "ffree",
        "fldcw", "fnstcw", "fstcw", "fnstsw", "fstsw", "fldenv", "fnstenv", "fstenv", "fnsave", "fsave", "frstor",
    };

    private bool FloatInstruction(string mnemonic, string[] args)
    {
        if (!FloatBare.ContainsKey(mnemonic) && !FloatNames.Contains(mnemonic)) return false;
        Require(_cpu.Fpu != "none", mnemonic, "x87/387");
        if (mnemonic is "wait" or "fwait") { Need(args, 0, mnemonic); Emit(0x9b); return true; }
        string mn = mnemonic;
        if (mn is "finit" or "fclex" or "fstcw" or "fstsw" or "fstenv" or "fsave")
        { Emit(0x9b); mn = "fn" + mn[1..]; }
        if (FloatBare.TryGetValue(mn, out ushort bare))
        { Need(args, 0, mnemonic); Emit((byte)(bare >> 8), (byte)bare); return true; }
        if (mn == "fnstsw" && args.Length == 1 && P(args[0]) is { Kind: OperandKind.Register, Size: 2, Reg: 0 })
        { Emit(0xdf, 0xe0); return true; }
        if (args.Length == 1 && P(args[0]).Kind == OperandKind.Memory)
        {
            Operand memory = P(args[0]); int size = memory.Size;
            int group = mn switch { "fadd" or "fiadd" => 0, "fmul" or "fimul" => 1,
                "fcom" or "ficom" => 2, "fcomp" or "ficomp" => 3,
                "fsub" or "fisub" => 4, "fsubr" or "fisubr" => 5,
                "fdiv" or "fidiv" => 6, "fdivr" or "fidivr" => 7, _ => -1 };
            byte opcode;
            if (group >= 0)
            {
                bool integer = mn.StartsWith("fi");
                if ((integer && size is not (2 or 4)) || (!integer && size is not (4 or 8))) throw Error(mn + " requires an explicit valid memory width");
                opcode = integer ? (size == 2 ? (byte)0xde : (byte)0xda) : (size == 4 ? (byte)0xd8 : (byte)0xdc);
            }
            else
            {
                (opcode, group) = (mn, size) switch
                {
                    ("fld", 4) => ((byte)0xd9, 0), ("fld", 8) => ((byte)0xdd, 0), ("fld", 10) => ((byte)0xdb, 5),
                    ("fst", 4) => ((byte)0xd9, 2), ("fst", 8) => ((byte)0xdd, 2),
                    ("fstp", 4) => ((byte)0xd9, 3), ("fstp", 8) => ((byte)0xdd, 3), ("fstp", 10) => ((byte)0xdb, 7),
                    ("fild", 2) => ((byte)0xdf, 0), ("fild", 4) => ((byte)0xdb, 0), ("fild", 8) => ((byte)0xdf, 5),
                    ("fist", 2) => ((byte)0xdf, 2), ("fist", 4) => ((byte)0xdb, 2),
                    ("fistp", 2) => ((byte)0xdf, 3), ("fistp", 4) => ((byte)0xdb, 3), ("fistp", 8) => ((byte)0xdf, 7),
                    ("fbld", 10) => ((byte)0xdf, 4), ("fbstp", 10) => ((byte)0xdf, 6),
                    ("fldcw", 0 or 2) => ((byte)0xd9, 5), ("fnstcw", 0 or 2) => ((byte)0xd9, 7),
                    ("fnstsw", 0 or 2) => ((byte)0xdd, 7), ("fldenv", 0) => ((byte)0xd9, 4),
                    ("fnstenv", 0) => ((byte)0xd9, 6), ("frstor", 0) => ((byte)0xdd, 4), ("fnsave", 0) => ((byte)0xdd, 6),
                    _ => throw Error(mn + " has an unsupported memory width"),
                };
            }
            Prefixes(0, memory); Emit(opcode); EmitRM(group, memory); return true;
        }
        bool pop = mn is "faddp" or "fmulp" or "fsubp" or "fsubrp" or "fdivp" or "fdivrp";
        int arithmetic = mn.TrimEnd('p') switch { "fadd" => 0, "fmul" => 1, "fsub" => 4, "fsubr" => 5, "fdiv" => 6, "fdivr" => 7, _ => -1 };
        if (arithmetic >= 0)
        {
            Operand dest, source;
            if (pop && args.Length == 0) { dest = P("st1"); source = P("st0"); }
            else if (args.Length == 1) { dest = P(pop ? args[0] : "st0"); source = P(pop ? "st0" : args[0]); }
            else { Need(args, 2, mn); dest = P(args[0]); source = P(args[1]); }
            if (dest.Kind != OperandKind.Float || source.Kind != OperandKind.Float || (dest.Reg != 0 && source.Reg != 0) || (pop && source.Reg != 0))
                throw Error(mn + " requires x87 registers with ST(0) as one operand");
            bool reversed = pop || dest.Reg != 0;
            if (reversed && arithmetic >= 4) arithmetic ^= 1;
            Emit(pop ? (byte)0xde : reversed ? (byte)0xdc : (byte)0xd8,
                (byte)(0xc0 + arithmetic * 8 + (reversed ? dest.Reg : source.Reg))); return true;
        }
        if (args.Length == 0 && mn == "fxch") args = ["st1"];
        Need(args, 1, mn); Operand register = P(args[0]);
        if (register.Kind != OperandKind.Float) throw Error(mn + " requires an x87 register");
        ushort operation = mn switch { "fld" => 0xd9c0, "fst" => 0xddd0, "fstp" => 0xddd8,
            "fxch" => 0xd9c8, "ffree" => 0xddc0, "fcom" => 0xd8d0, "fcomp" => 0xd8d8,
            "fucom" => 0xdde0, "fucomp" => 0xdde8, _ => throw Error("Unsupported x87 register form: " + mn) };
        Emit((byte)(operation >> 8), (byte)((operation & 255) + register.Reg)); return true;
    }
}
