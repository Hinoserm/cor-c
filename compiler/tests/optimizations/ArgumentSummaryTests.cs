using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Tests.Opt;

/// <summary>
/// What a unit says of its calls through words nothing names
/// (ArgumentSummary.Of, ArgumentHints): each by where its target comes
/// from -- a static cell, a parameter, a slot of the type it dispatches on,
/// anything else -- and which cells it writes with what is not a number or
/// a named address, or uses the address of.
/// </summary>
internal static class ArgumentSummaryTests
{
    static void Assert(bool cond, string what) { if (!cond) throw new Exception(what); }

    static readonly int Word = IrTypes.Word.Bytes();

    static VReg Load(Block at, Function f, Operand from, int offset, int size = 0)
    {
        VReg d = f.NewReg(IrTypes.Word);
        at.Instrs.Add(new Instr { Op = Opcode.Load, Dest = d, Operands = { from }, Offset = offset, Size = size == 0 ? Word : size });
        return d;
    }

    static void Store(Block at, Operand to, Operand value, int size = 0, int offset = 0)
        => at.Instrs.Add(new Instr { Op = Opcode.Store, Operands = { to, value }, Size = size == 0 ? Word : size, Offset = offset });

    static Instr CallThrough(Block at, Operand target, params Operand[] arguments)
    {
        Instr call = new() { Op = Opcode.CallIndirect, Operands = { target } };
        call.Operands.AddRange(arguments);
        at.Instrs.Add(call);
        return call;
    }

    static (Function F, Block Body) Make(Module m, string name, int parameters)
    {
        Function f = new(name, IrType.Void);
        for (int k = 0; k < parameters; k++) f.Params.Add(f.NewReg(IrTypes.Word));
        Block body = f.NewBlock("entry");
        m.Functions.Add(f);
        return (f, body);
    }

    public static void UnnamedCalls()
    {
        Module m = new("argument-summary");
        ImmOperand One = new(1, IrTypes.Word), Two = new(2, IrTypes.Word);

        // Setter(p): s_Set = p -- a parameter's value, said by the parameter.
        (Function setter, Block b) = Make(m, "Setter", 1);
        Store(b, new SymOperand("s_Set"), setter.Params[0]);
        // Named(): s_Named = &Handler; s_Number = 5 -- nothing said.
        (_, b) = Make(m, "Named", 0);
        Store(b, new SymOperand("s_Named"), new SymOperand("Handler"));
        Store(b, new SymOperand("s_Number"), new ImmOperand(5, IrTypes.Word));
        // Writes(o): s_Loaded = o.next -- a loaded word: written.
        (Function writes, b) = Make(m, "Writes", 1);
        Store(b, new SymOperand("s_Loaded"), Load(b, writes, writes.Params[0], 8));
        // Takes(): t = &s_Taken -- its address used: written.
        (Function takes, b) = Make(m, "Takes", 0);
        VReg taken = takes.NewReg(IrTypes.Word);
        b.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = taken, Operands = { new SymOperand("s_Taken") } });
        // Narrow(): one byte stored into s_Narrow -- part of something: written.
        (_, b) = Make(m, "Narrow", 0);
        Store(b, new SymOperand("s_Narrow"), new ImmOperand(1, IrType.I32), size: 1);
        // Away from the start, by the store's offset or the symbol's, or wider than a word: written.
        Store(b, new SymOperand("s_Off"), new ImmOperand(1, IrTypes.Word), offset: Word);
        Store(b, new SymOperand("s_SymOff", Word), new ImmOperand(1, IrTypes.Word));
        Store(b, new SymOperand("s_Wide"), new ImmOperand(1, IrType.I64), size: 2 * Word);

        // CallCell(): s_Named's word called with two arguments.
        (Function callCell, b) = Make(m, "CallCell", 0);
        CallThrough(b, Load(b, callCell, new SymOperand("s_Named"), 0), One, Two);
        // CallParam(fp): fp called with one argument.
        (Function callParam, b) = Make(m, "CallParam", 1);
        CallThrough(b, callParam.Params[0], One);
        // CallSlot(o): o's descriptor's word at 52 called on another object, dispatching on Op.
        (Function callSlot, b) = Make(m, "CallSlot", 2);
        VReg method = Load(b, callSlot, Load(b, callSlot, callSlot.Params[0], 0), 52);
        CallThrough(b, method, callSlot.Params[1]).DispatchType = "Op";
        // CallOther(o): a word of o's called -- anything.
        (Function callOther, b) = Make(m, "CallOther", 1);
        CallThrough(b, Load(b, callOther, callOther.Params[0], 8));
        // CallWritten(p): p overwritten, then called with three arguments -- anything.
        (Function callWritten, b) = Make(m, "CallWritten", 1);
        b.Instrs.Add(new Instr { Op = Opcode.Add, Dest = callWritten.Params[0], Operands = { callWritten.Params[0], One } });
        CallThrough(b, callWritten.Params[0], One, One, One);
        foreach (Function f in m.Functions) new Builder(f, f.Blocks[0]).Ret();

        ArgumentHints hints = ArgumentSummary.Of(m);
        Assert(hints.CellParams.TryGetValue("s_Set", out SortedSet<string>? set) && set.SetEquals(new[] { "0:Setter" }), "a cell a parameter is stored in is not said by the parameter");
        Assert(!hints.CellsWritten.Contains("s_Set") && !hints.CellsWritten.Contains("s_Named") && !hints.CellsWritten.Contains("s_Number"),
            "a cell written with a parameter, a named address or a number is said written");
        Assert(hints.CellsWritten.Contains("s_Loaded"), "a cell written with a loaded word is not said written");
        Assert(hints.CellsWritten.Contains("s_Taken"), "a cell whose address is used is not said written");
        Assert(hints.CellsWritten.Contains("s_Narrow"), "a cell written a byte of is not said written");
        Assert(hints.CellsWritten.Contains("s_Off") && hints.CellsWritten.Contains("s_SymOff") && hints.CellsWritten.Contains("s_Wide"),
            "a cell written away from its start, or wider than a word, is not said written");
        Assert(hints.CellCalls.TryGetValue("s_Named", out SortedSet<int>? cell) && cell.SetEquals(new[] { 2 }), "a call through a cell's word is not said by the cell");
        Assert(hints.ParamCalls.TryGetValue("0:CallParam", out SortedSet<int>? param) && param.SetEquals(new[] { 1 }), "a call through a parameter is not said by the parameter");
        Assert(hints.SlotCalls.TryGetValue(Escape.VirtualCallee("Op", 52), out SortedSet<int>? slot) && slot.SetEquals(new[] { 1 }),
            "a call through a slot's word read raw is not said by the slot");
        Assert(hints.Unnamed.SetEquals(new[] { 0, 3 }), "anything else is not said by its count: " + string.Join(",", hints.Unnamed));
    }
}
