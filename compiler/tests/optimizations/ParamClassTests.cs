using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;
using Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Tests.Opt;

/// <summary>
/// Devirtualize's tests of a parameter its declared class answers
/// (Function.ParamClasses, ParamInterfaces), on the shapes Lowering.TypeTest
/// emits: a depth compared with the class's own, and the interface scan --
/// its cursor started at the parameter's descriptor's interface list -- made
/// a jump to its yes where the class lists the interface, and left where it
/// does not, or where yes takes a phi.
/// </summary>
internal static class ParamClassTests
{
    static void Assert(bool cond, string what) { if (!cond) throw new Exception(what); }

    static readonly int Word = IrTypes.Word.Bytes();

    // The parameter's descriptor word, as the object's first word names it (DescriptorBytes in).
    static VReg Descriptor(Block at, Function f, VReg p)
    {
        VReg d = f.NewReg(IrTypes.Word);
        at.Instrs.Add(new Instr { Op = Opcode.Load, Dest = d, Operands = { RegOperand.Of(p) }, Offset = 0, Size = Word });
        return d;
    }

    static (Module Module, Function F, Block Pre, Block Yes) Scan(string wanted, bool phiInYes)
    {
        Module module = new("param-class");
        module.Data.Add(new DataItem("t_List", new byte[8]) { ReadOnly = true });
        Function f = new("walk", IrType.Void);
        VReg p = f.NewReg(IrTypes.Word); f.Params.Add(p);
        f.ParamClasses = new() { [p] = new[] { "t_Object", "t_List" } };
        f.ParamInterfaces = new() { [p] = new HashSet<string>(StringComparer.Ordinal) { "i_ICollection" } };
        Block pre = f.NewBlock("pre"), loop = f.NewBlock("loop"), more = f.NewBlock("more"), advance = f.NewBlock("advance"),
            yes = f.NewBlock("yes"), no = f.NewBlock("no");
        VReg descriptor = Descriptor(pre, f, p), list = f.NewReg(IrTypes.Word), cursor = f.NewReg(IrTypes.Word);
        pre.Instrs.Add(new Instr { Op = Opcode.Load, Dest = list, Operands = { RegOperand.Of(descriptor) },
            Offset = Lowering.DescriptorInterfacesWord * Word - Target.Current.DescriptorBytes, Size = Word });
        pre.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = cursor, Operands = { RegOperand.Of(list) } });
        new Builder(f, pre).Jump(loop);
        VReg entry = f.NewReg(IrTypes.Word), hit = f.NewReg(IrType.I32);
        loop.Instrs.Add(new Instr { Op = Opcode.Load, Dest = entry, Operands = { RegOperand.Of(cursor) }, Offset = 0, Size = Word });
        new Builder(f, loop).Branch(entry, more, no);
        more.Instrs.Add(new Instr { Op = Opcode.Eq, Dest = hit, Operands = { RegOperand.Of(entry), new SymOperand(wanted, 0) } });
        new Builder(f, more).Branch(hit, yes, advance);
        advance.Instrs.Add(new Instr { Op = Opcode.Add, Dest = cursor, Operands = { RegOperand.Of(cursor), new ImmOperand(Word, IrTypes.Word) } });
        new Builder(f, advance).Jump(loop);
        Builder y = new(f, yes);
        if (phiInYes)
        {
            VReg merged = f.NewReg(IrType.I32);
            yes.Instrs.Add(new Instr { Op = Opcode.Phi, Dest = merged, Operands = { RegOperand.Of(hit) } });
        }
        y.Store(new SymOperand("sink", 0), new ImmOperand(1, IrType.I32));
        y.Ret();
        Builder n = new(f, no);
        n.Store(new SymOperand("sink", 0), new ImmOperand(0, IrType.I32));
        n.Ret();
        module.Functions.Add(f); module.Entry = "walk";
        return (module, f, pre, yes);
    }

    public static void InterfaceScan()
    {
        (Module listed, _, Block pre, Block yes) = Scan("i_ICollection", phiInYes: false);
        new Devirtualize().Run(listed);
        Assert(pre.Terminator is { Op: Opcode.Jump, Targets: [Block to] } && ReferenceEquals(to, yes),
            "an interface the parameter's class lists is a yes from the start");

        (Module other, _, Block preOther, Block yesOther) = Scan("i_IOther", phiInYes: false);
        new Devirtualize().Run(other);
        Assert(preOther.Terminator is { Op: Opcode.Jump, Targets: [Block loopOther] } && !ReferenceEquals(loopOther, yesOther),
            "an interface the class does not list is left to the scan: a derived class may list it");

        (Module phied, _, Block prePhi, Block yesPhi) = Scan("i_ICollection", phiInYes: true);
        new Devirtualize().Run(phied);
        Assert(prePhi.Terminator is { Op: Opcode.Jump, Targets: [Block loopPhi] } && !ReferenceEquals(loopPhi, yesPhi),
            "a yes that takes a phi is not given an edge it had no value for");
    }

    public static void DepthTest()
    {
        Module module = new("param-depth");
        module.Data.Add(new DataItem("t_List", new byte[8]) { ReadOnly = true });
        Function f = new("deep", IrType.I32);
        VReg p = f.NewReg(IrTypes.Word); f.Params.Add(p);
        f.ParamClasses = new() { [p] = new[] { "t_Object", "t_List" } };
        Block entry = f.NewBlock("entry");
        VReg descriptor = Descriptor(entry, f, p), depth = f.NewReg(IrType.I32), deepEnough = f.NewReg(IrType.I32), deeper = f.NewReg(IrType.I32);
        entry.Instrs.Add(new Instr { Op = Opcode.Load, Dest = depth, Operands = { RegOperand.Of(descriptor) },
            Offset = Lowering.DescriptorDepthWord * Word - Target.Current.DescriptorBytes, Size = 4 });
        entry.Instrs.Add(new Instr { Op = Opcode.GeS, Dest = deepEnough, Operands = { RegOperand.Of(depth), new ImmOperand(1, IrType.I32) } });
        entry.Instrs.Add(new Instr { Op = Opcode.GeS, Dest = deeper, Operands = { RegOperand.Of(depth), new ImmOperand(2, IrType.I32) } });
        Builder b = new(f, entry);
        b.Ret(RegOperand.Of(b.Binary(Opcode.Add, deepEnough, deeper)));
        module.Functions.Add(f); module.Entry = "deep";
        new Devirtualize().Run(module);
        Assert(entry.Instrs.Any(i => i.Dest == deepEnough && i is { Op: Opcode.Copy, Operands: [ImmOperand { Value: 1 }] }),
            "deep enough for the parameter's own class: every object it holds is");
        Assert(entry.Instrs.Any(i => i.Dest == deeper && i.Op == Opcode.GeS),
            "deeper than its class: only the object can say");
    }
}
