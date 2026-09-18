#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Opt;

public static partial class Program
{
    private static void FrameAddressBoundaries()
    {
        foreach (long offset in new long[] { -1, 0, 4, 12, 13, 16, long.MaxValue })
        foreach (bool reassigned in new[] { false, true })
        {
            Function f = new("frame-address-proof", IrType.I32);
            var slot = f.NewSlot(16, 4); var block = f.NewBlock("entry");
            VReg pointer = f.NewReg(IrType.I32), adjusted = f.NewReg(IrType.I32), value = f.NewReg(IrType.I32);
            block.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = pointer, Operands = { new SlotOperand(slot) } });
            block.Instrs.Add(new Instr { Op = Opcode.Add, Dest = adjusted,
                Operands = { new RegOperand(pointer), new ImmOperand(offset, IrType.I32) } });
            if (reassigned) block.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = adjusted,
                Operands = { new ImmOperand(0, IrType.I32) } });
            Instr read = new() { Op = Opcode.Load, Dest = value, Size = 4, Operands = { new RegOperand(adjusted) } };
            block.Instrs.Add(read); new Builder(f, block).Ret(new RegOperand(value));
            Verifier.Check(f, "before frame address fold");
            new FrameAddressFold().Run(f);
            Verifier.Check(f, "after frame address fold");
            read = block.Instrs.Single(i => i.Op == Opcode.Load && i.Dest == value);
            bool folded = !reassigned && offset >= 0 && offset <= 12;
            Assert((read.Operands[0] is SlotOperand) == folded, $"frame offset={offset} reassigned={reassigned}");
            if (folded) Assert(read.Offset == offset, "frame displacement retained exactly");
            Assert(block.Instrs.Contains(read), "address folding keeps the read");
        }
    }

    private static void LoadReuseBoundaries()
    {
        foreach (bool backEdge in new[] { false, true })
        {
            Function f = new("load-entry-proof", IrType.I32);
            VReg address = f.NewReg(IrType.I32), condition = f.NewReg(IrType.I32);
            f.Params.Add(address); f.Params.Add(condition);
            var entry = f.NewBlock("entry"); var body = f.NewBlock("body"); var exit = f.NewBlock("exit");
            VReg first = f.NewReg(IrType.I32), second = f.NewReg(IrType.I32);
            entry.Instrs.Add(new Instr { Op = Opcode.Load, Dest = first, Size = 4,
                Operands = { new RegOperand(address) } });
            new Builder(f, entry).Branch(condition, body, exit);
            body.Instrs.Add(new Instr { Op = Opcode.Load, Dest = second, Size = 4,
                Operands = { new RegOperand(address) } });
            if (backEdge) body.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { entry } });
            else new Builder(f, body).Ret(new RegOperand(second));
            new Builder(f, exit).Ret(new RegOperand(first));
            Verifier.Check(f, "before entry load reuse");
            new LoadReuse().Run(f);
            Verifier.Check(f, "after entry load reuse");
            Assert(body.Instrs.Any(i => i.Op == Opcode.Load) == backEdge,
                "entry results can cross blocks only under the non-reentry proof");
        }
        foreach (string barrier in new[] { "none", "address", "value", "store", "call", "fence", "width", "signed" })
        {
            Function f = new("load-reuse-proof", IrType.I32);
            VReg address = f.NewReg(IrType.I32); f.Params.Add(address);
            var block = f.NewBlock("entry"); Builder b = new(f, block);
            VReg first = f.NewReg(IrType.I32), second = f.NewReg(IrType.I32);
            Instr original = new() { Op = Opcode.Load, Dest = first, Size = 1,
                Operands = { new RegOperand(address) } };
            block.Instrs.Add(original);
            if (barrier is "address" or "value") block.Instrs.Add(new Instr { Op = Opcode.Copy,
                Dest = barrier == "address" ? address : first, Operands = { new ImmOperand(16, IrType.I32) } });
            if (barrier == "store") block.Instrs.Add(new Instr { Op = Opcode.Store, Size = 1,
                Operands = { new RegOperand(address), new ImmOperand(7, IrType.I32) } });
            if (barrier == "call") b.Call("effect", IrType.Void);
            if (barrier == "fence") block.Instrs.Add(new Instr { Op = Opcode.Fence });
            block.Instrs.Add(new Instr { Op = Opcode.Load, Dest = second,
                Size = barrier == "width" ? 2 : 1, Signed = barrier == "signed",
                Operands = { new RegOperand(address) } });
            b.Ret(new RegOperand(second));
            Verifier.Check(f, "before load reuse");
            new LoadReuse().Run(f);
            Verifier.Check(f, "after load reuse");
            Assert(block.Instrs.Contains(original), "first potentially faulting read stays");
            Assert(block.Instrs.Count(i => i.Op == Opcode.Load) == (barrier == "none" ? 1 : 2),
                $"load reuse barrier={barrier}");
        }
    }

    private static void StoreBackBoundaries()
    {
        foreach (int width in new[] { 1, 2, 4, 8 })
        foreach (string barrier in new[] { "none", "call", "fence", "mutate", "overlap", "escape", "outside" })
        {
            Function f = new("store-back-proof", IrType.Void);
            var slot = f.NewSlot(16, 8);
            var block = f.NewBlock("entry");
            Builder b = new(f, block);
            VReg value = f.NewReg(width == 8 ? IrType.I64 : IrType.I32);
            long offset = barrier == "outside" ? 16 : 0;
            block.Instrs.Add(new Instr { Op = Opcode.Load, Dest = value, Size = width, Offset = offset,
                Operands = { new SlotOperand(slot) } });
            if (barrier == "call") b.Call("effect", IrType.Void);
            if (barrier == "fence") block.Instrs.Add(new Instr { Op = Opcode.Fence });
            if (barrier == "mutate") block.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = value,
                Operands = { new ImmOperand(42, value.Type) } });
            if (barrier == "overlap") block.Instrs.Add(new Instr { Op = Opcode.Store, Size = 1,
                Operands = { new SlotOperand(slot), new ImmOperand(7, IrType.I32) } });
            if (barrier == "escape") b.Call("capture", IrType.Void, new SlotOperand(slot));
            Instr writeBack = new() { Op = Opcode.Store, Size = width, Offset = offset,
                Operands = { new SlotOperand(slot), new RegOperand(value) } };
            block.Instrs.Add(writeBack);
            b.Ret();
            new StoreBackElimination().Run(f);
            Verifier.Check(f, "store back barriers");
            Assert(block.Instrs.Contains(writeBack) == (barrier != "none"),
                $"write-back width={width} barrier={barrier}");
            Assert(block.Instrs.Any(i => i.Op == Opcode.Load), "pass does not erase the original read");
        }
    }

    private static void EdgePredicateDifferential()
    {
        Opcode[] comparisons = { Opcode.Eq, Opcode.Ne, Opcode.LtS, Opcode.LeS,
            Opcode.GtS, Opcode.GeS, Opcode.LtU, Opcode.LeU, Opcode.GtU, Opcode.GeU };
        long[] values = { 0, 1, -1, int.MinValue, int.MaxValue, uint.MaxValue, long.MinValue, long.MaxValue };
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (Opcode first in comparisons)
        foreach (Opcode second in comparisons)
        foreach (bool mutate in new[] { false, true })
        {
            Function f = new("edge-proof", IrType.I32);
            VReg x = f.NewReg(type), y = f.NewReg(type); f.Params.Add(x); f.Params.Add(y);
            var entry = f.NewBlock("entry"); var yes = f.NewBlock("yes"); var no = f.NewBlock("no");
            Builder b = new(f, entry);
            VReg condition = b.Binary(first, new RegOperand(x), new RegOperand(y), IrType.I32);
            b.Branch(condition, yes, no);
            foreach (var block in new[] { yes, no })
            {
                Builder arm = new(f, block);
                if (mutate) block.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = x,
                    Operands = { new ImmOperand(-1, type) } });
                // Reverse the compared operands to exercise relation inversion.
                VReg result = arm.Binary(second, new RegOperand(y), new RegOperand(x), IrType.I32);
                arm.Ret(new RegOperand(result));
            }
            Verifier.Check(f, "before edge proof");
            long[] expected = (from a in values from c in values select new Interp().Run(f, a, c)).ToArray();
            new EdgePredicateSimplify().Run(f);
            Verifier.Check(f, "after edge proof");
            int index = 0;
            foreach (long a in values) foreach (long c in values)
                Assert(new Interp().Run(f, a, c) == expected[index++],
                    $"edge preserves {type} {first}/{second} mutation={mutate} x={a} y={c}");
        }
    }

    private static void BitFactsDifferential()
    {
        long[] values = { 0, 1, -1, 127, 128, 255, 256, int.MinValue,
            int.MaxValue, uint.MaxValue, long.MinValue, long.MaxValue, 0x123456789abcdef };
        long[] masks = { 0, 15, 255, 0xffff, 0x7fffffff, -1, long.MinValue };
        Opcode[] operations = { Opcode.Add, Opcode.Sub, Opcode.Mul, Opcode.And,
            Opcode.Or, Opcode.Xor, Opcode.Shl, Opcode.ShrU, Opcode.ShrS,
            Opcode.Eq, Opcode.Ne, Opcode.LtS, Opcode.LeS, Opcode.GtS, Opcode.GeS,
            Opcode.LtU, Opcode.LeU, Opcode.GtU, Opcode.GeU };
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (long mask in masks)
        foreach (Opcode op in operations)
        foreach (long constant in new long[] { 0, 1, 31, 32, 63, 64, -1 })
        {
            Function f = new("bit-proof", IrInfo.IsIntCompare(op) ? IrType.I32 : type);
            VReg input = f.NewReg(type); f.Params.Add(input);
            Builder b = new(f, f.NewBlock("entry"));
            VReg bounded = b.Binary(Opcode.And, new RegOperand(input), new ImmOperand(mask, type), type);
            IrType rightType = op is Opcode.Shl or Opcode.ShrU or Opcode.ShrS ? IrType.I32 : type;
            VReg result = b.Binary(op, new RegOperand(bounded), new ImmOperand(constant, rightType), f.Returns);
            b.Ret(new RegOperand(result));
            Verifier.Check(f, "before bit fact proof");
            long[] expected = values.Select(value => new Interp().Run(f, value)).ToArray();
            var bits = new IntegerBitFacts(f).Get(new RegOperand(result));
            Assert((bits.Zero & bits.One) == 0, "known zero and one never conflict");
            foreach (long value in expected)
            {
                ulong actual = unchecked((ulong)value) & IntegerBitFacts.Mask(f.Returns);
                Assert((actual & bits.Zero) == 0 && (actual & bits.One) == bits.One,
                    $"sound facts: {type} {op} mask={mask} constant={constant}");
            }
            new BitFactSimplify().Run(f);
            Verifier.Check(f, "bit fact proof");
            for (int n = 0; n < values.Length; n++)
                Assert(new Interp().Run(f, values[n]) == expected[n],
                    $"rewrite preserves {type} {op} mask={mask} constant={constant} input={values[n]}");
        }
    }

    private static void BitFactsBarriers()
    {
        Function f = new("bit-mutation", IrType.I32);
        VReg input = f.NewReg(IrType.I32); f.Params.Add(input);
        Builder b = new(f, f.NewBlock("entry"));
        VReg before = b.Binary(Opcode.And, input, 255);
        // Even a constant reassignment must not describe the entry value.
        f.Entry.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = input,
            Operands = { new ImmOperand(0, IrType.I32) } });
        b.Ret(new RegOperand(before));
        Assert(new IntegerBitFacts(f).Get(new RegOperand(input)) == default,
            "mutable parameter retains unknown entry facts");
        new BitFactSimplify().Run(f);
        Assert(new Interp().Run(f, 171) == 171, "parameter snapshot preserved");

        Function read = new("bit-read", IrType.I32);
        VReg address = read.NewReg(IrType.I32); read.Params.Add(address);
        VReg loaded = read.NewReg(IrType.I32);
        Builder rb = new(read, read.NewBlock("entry"));
        read.Entry.Instrs.Add(new Instr { Op = Opcode.Load, Dest = loaded, Size = 1,
            Operands = { new RegOperand(address) } });
        VReg zero = rb.Binary(Opcode.And, loaded, 0);
        rb.Ret(new RegOperand(zero));
        new BitFactSimplify().Run(read);
        new DeadCodeElimination().Run(read);
        Verifier.Check(read, "bit fact fault barrier");
        Assert(read.Entry.Instrs.Any(i => i.Op == Opcode.Load), "faulting byte read must survive constant consumer");
    }
}
