#nullable enable
using System.Text;
using Corsac.Lang;
using Corsac.Lang.Ir;
using Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

namespace Corsac.Tests.Opt;

/// <summary>
/// Builds small functions by hand, runs passes over them, and checks the
/// dump. Each test says which pass it exercises; the last few run the
/// whole default pipeline. Usage: OptTests [-v] -- -v prints every dump.
/// </summary>
public static partial class Program
{
    private static int _failures;
    private static int _passes;
    private static bool _verbose;

    public static int Main(string[] args)
    {
        _verbose = args.Contains("-v");
        Target.Current = Target.X86;
        if (args.Contains("--frame-address-only"))
        {
            Try("frame addresses: bounds and captured pointer definitions", FrameAddressBoundaries);
            return _failures == 0 ? 0 : 1;
        }
        Try("frame addresses: bounds and captured pointer definitions", FrameAddressBoundaries);
        if (args.Contains("--load-reuse-only"))
        {
            Try("load reuse: captured addresses, values and memory barriers", LoadReuseBoundaries);
            return _failures == 0 ? 0 : 1;
        }
        Try("load reuse: captured addresses, values and memory barriers", LoadReuseBoundaries);
        if (args.Contains("--store-back-only"))
        {
            Try("store back: private widths and effect barriers", StoreBackBoundaries);
            return _failures == 0 ? 0 : 1;
        }
        Try("store back: private widths and effect barriers", StoreBackBoundaries);
        if (args.Contains("--bit-facts-only"))
        {
            Try("bit facts: modular values and rewrites agree", BitFactsDifferential);
            Try("bit facts: mutable parameters and faulting reads stay conservative", BitFactsBarriers);
            Try("edge predicates: signedness, branch polarity and mutation", EdgePredicateDifferential);
            return _failures == 0 ? 0 : 1;
        }
        Try("bit facts: modular values and rewrites agree", BitFactsDifferential);
        Try("bit facts: mutable parameters and faulting reads stay conservative", BitFactsBarriers);
        Try("edge predicates: signedness, branch polarity and mutation", EdgePredicateDifferential);
        if (args.Contains("--branch-cost-only"))
        {
            Try("inline: branch cost preserves single-site opportunities", BranchCostInlining);
            return _failures == 0 ? 0 : 1;
        }
        Try("inline: branch cost preserves single-site opportunities", BranchCostInlining);
        if (args.Contains("--call-cost-only"))
        {
            Try("inline: argument cost respects width and growth limits", ArgumentCostInlining);
            return _failures == 0 ? 0 : 1;
        }
        Try("inline: argument cost respects width and growth limits", ArgumentCostInlining);
        Try("escape: loop objects win the bounded promotion budget", HotAllocationBudget);
        if (args.Contains("--fresh-owner-only"))
        {
            Try("inline: fresh-owner context respects body and growth budgets", FreshOwnerInlining);
            return _failures == 0 ? 0 : 1;
        }
        Try("inline: fresh-owner context respects body and growth budgets", FreshOwnerInlining);
        if (args.Contains("--unary-only"))
        {
            Try("peephole: cancelling integer unary operations and mutation guards", UnaryCancellation);
            return _failures == 0 ? 0 : 1;
        }
        Try("peephole: cancelling integer unary operations and mutation guards", UnaryCancellation);

        Try("inline: constant branch budget preserves runtime calls and growth limits", ConstantBranchInlining);
        Try("scalar objects: widths, initialization and conservative escape barriers", ScalarObjectBoundaries);
        Try("escape: owned allocations preserve long free-call ABI", OwnedFreeAbi);
        Try("local copies: reassignment, branches, joins and loops preserve values", LocalCopyBoundaries);
        Try("division reuse: signed results and redefinition barriers", DivisionReuseBoundaries);
        Try("integer chains: modular constants and mutable-source barriers", IntegerChainBoundaries);
        Try("integer reuse: dominating paths and mutable-result barriers", IntegerReuseBoundaries);
        Try("owned fields: callee retention and field overlap keep children on heap", OwnedFieldBoundaries);
        Try("owned fields: indirect ancestor aliases do not hide escaping grandchildren", NestedOwnedFieldBoundary);
        Try("carry recognition: wraparound, operand order and mutation guards", CarryRecognitionBoundaries);
        Try("wide product sharing: full partial graph and mutable-input guards", WideProductBoundaries);
        Try("integer reuse: fresh aliases feed dependent expressions without stale reads", EagerReuseBoundaries);
        Try("signed powers: signed extremes, helper calls and exceptional divisors", SignedPowerBoundaries);

        Try("fold: 2+3 then *4 becomes ret 20", FoldArithmetic);
        Try("fold: I32 wraps, shifts mask, unsigned compares", FoldWidths);
        Try("fold: division by zero and MinValue/-1 stay", FoldDivision);
        Try("fold: floats are not folded", FoldFloatsStay);
        Try("fold: branch and switch on immediates become jumps", FoldBranchSwitch);
        Try("propagate: copy chain collapses to the parameter", CopyChain);
        Try("propagate: multi-def register is left alone", MultiDefStays);
        Try("propagate: loop-carried copy is not forwarded", LoopCarriedCopy);
        Try("propagate: copy before its source's redefinition in a loop", CopyBeforeRedefinition);
        Try("propagate: value defined in a landing pad stays in the pad", LandingPadValue);
        Try("dce: dead loads and arithmetic vanish, stores and calls stay", DeadCode);
        Try("dce: unused call result loses its register", DeadCallResult);
        Try("branches: constant branch threads to one block", ConstantBranchThreads);
        Try("branches: jump-only block threaded through switch", SwitchThreading);
        Try("branches: landing pad kept without predecessors", LandingPadKept);
        Try("branches: entry loop and self-jump survive", LoopsSurvive);
        Try("peephole: algebraic identities", Algebra);
        Try("peephole: powers of two", PowersOfTwo);
        Try("peephole: compare of compare", CompareOfCompare);
        Try("peephole: compare inversion refused across a redefinition", CompareInversionRefused);
        Try("verify: missing terminator and bad types are reported", VerifierCatches);
        Try("liveness: loop-carried register is live around the loop", LivenessLoop);
        Try("cfg: dominators and reverse postorder", CfgQueries);
        Try("pipeline: trace hook fires per pass", PipelineTrace);
        Try("ssa: loop gets header phis and every register one definition", SsaLoop);
        Try("ssa: loop-carried single definition gets a phi", SsaLoopCarriedSingleDef);
        Try("ssa: round trip preserves results (fib, swap, switch)", SsaRoundTrip);
        Try("ssa: critical edge is split and merged back", SsaCriticalEdge);
        Try("ssa: landing pad uses stay undefined and separate", SsaLandingPad);
        Try("ssa-opt: multi-def copies are forwarded through phis", SsaOptimiseFib);
        Try("branches: phi entries follow threaded and merged edges", SsaBranchSimplify);
        Try("sccp: constant through a phi from a decided branch", SccpPhi);
        Try("sccp: loop-invariant phi folds, real induction does not", SccpLoop);
        Try("gvn: repeated expressions computed once, commutatively", GvnExpressions);
        Try("gvn: loads forwarded from stores and earlier loads", GvnLoads);
        Try("gvn: stores, calls and register stores block forwarding", GvnBarriers);
        Try("gvn: memory facts reach an if arm but not a loop header", GvnScopes);
        Try("dse: overwritten and unread-before-return stores go", DseBasic);
        Try("dse: reads, calls, register stores and partial overlap keep stores", DseKept);

        Console.WriteLine($"{_passes} passed, {_failures} failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void SignedPowerBoundaries()
    {
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (Opcode operation in new[] { Opcode.DivS, Opcode.RemS })
        foreach (long divisor in new long[] { 2, -2, 8, -8, type == IrType.I32 ? int.MinValue : long.MinValue })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(type, type);
                b.Ret(new RegOperand(b.Binary(operation, new RegOperand(f.Params[0]), new ImmOperand(divisor, type), type)));
                return f;
            }
            long[][] inputs = new long[] { 0, 1, -1, 7, -7, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue }
                .Select(n => new[] { n }).ToArray();
            SameResults(Build, inputs, new SignedPowerOfTwo());
        }
        foreach (long divisor in new long[] { 0, 1, -1, 3, 8, -8, long.MinValue })
        foreach (string callee in new[] { "m_Runtime_DivS_2_V$I64_V$I64", "m_Runtime_RemS_2_V$I64_V$I64", "user_divide" })
        {
            (Function f, Builder b) = Fn(IrType.I64, IrType.I64);
            b.Ret(new RegOperand(b.Call(callee, IrType.I64, new RegOperand(f.Params[0]), new ImmOperand(divisor, IrType.I64))!));
            Run(f, new SignedPowerOfTwo());
            bool eligible = callee != "user_divide" && divisor is 8 or -8 or long.MinValue;
            Assert(f.Blocks.SelectMany(block => block.Instrs).Any(i => i.Op == Opcode.Call) != eligible,
                "only known non-exceptional power helpers disappear");
        }
    }

    private static void UnaryCancellation()
    {
        foreach (IrType type in new[] { IrType.I32, IrType.I64, IrType.F64 })
        foreach (Opcode op in new[] { Opcode.Neg, Opcode.Not, Opcode.ByteSwap })
        foreach (bool mutate in new[] { false, true })
        {
            if (type == IrType.F64 && op != Opcode.Neg) continue;
            (Function f, Builder b) = Fn(type, type);
            VReg inner = f.NewReg(type), outer = f.NewReg(type);
            Block block = f.Blocks[0];
            block.Instrs.Add(new Instr { Op = op, Dest = inner, Operands = { new RegOperand(f.Params[0]) } });
            if (mutate) b.CopyTo(f.Params[0], new ImmOperand(13, type));
            block.Instrs.Add(new Instr { Op = op, Dest = outer, Operands = { new RegOperand(inner) } });
            b.Ret(new RegOperand(outer));
            new Peephole().Run(f);
            Instr result = block.Instrs.Single(i => ReferenceEquals(i.Dest, outer));
            bool cancel = !mutate && type != IrType.F64;
            Assert(result.Op == (cancel ? Opcode.Copy : op), "unary cancellation legality");
            if (cancel) Assert(result.Operands[0] is RegOperand r && ReferenceEquals(r.Reg, f.Params[0]),
                "cancellation reads the original source");
        }
    }

    private static void EagerReuseBoundaries()
    {
        foreach (bool mutate in new[] { false, true })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(IrType.I64, IrType.I64, IrType.I64);
                VReg first = b.Binary(Opcode.Xor, f.Params[0], f.Params[1]);
                VReg repeated = b.Binary(Opcode.Xor, f.Params[0], f.Params[1]);
                VReg left = b.Binary(Opcode.Add, first, f.Params[1]);
                if (mutate) b.CopyTo(first, new ImmOperand(13, IrType.I64));
                VReg right = b.Binary(Opcode.Add, repeated, f.Params[1]);
                b.Ret(new RegOperand(b.Binary(Opcode.Add, left, right)));
                return f;
            }
            SameResults(Build, Inputs2, new IntegerValueReuse());
            Function changed = Build(); new IntegerValueReuse().Run(changed);
            Assert(changed.Blocks.SelectMany(b => b.Instrs).Count(i => i.Op == Opcode.Add) == (mutate ? 3 : 2),
                "dependent CSE and canonical-source mutation");
        }
    }

    private static void WideProductBoundaries()
    {
        foreach (string mode in new[] { "full", "missing", "mutated", "square" })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(IrType.I64, IrType.I64, IrType.I64);
                VReg a = f.Params[0], c = mode == "square" ? a : f.Params[1];
                VReg whole = b.Binary(Opcode.Mul, a, c);
                if (mode == "mutated") b.CopyTo(c, new ImmOperand(19, IrType.I64));
                VReg al = b.Binary(Opcode.And, a, 4294967295);
                VReg ah = b.Binary(Opcode.ShrU, new RegOperand(a), new ImmOperand(32, IrType.I32), IrType.I64);
                VReg bl = b.Binary(Opcode.And, c, 4294967295);
                VReg bh = b.Binary(Opcode.ShrU, new RegOperand(c), new ImmOperand(32, IrType.I32), IrType.I64);
                VReg p00 = b.Binary(Opcode.Mul, al, bl), p01 = b.Binary(Opcode.Mul, al, bh);
                VReg p10 = b.Binary(Opcode.Mul, ah, bl);
                VReg mix = b.Binary(Opcode.Xor, whole, b.Binary(Opcode.Xor, p00, b.Binary(Opcode.Xor, p01, p10)));
                if (mode != "missing") mix = b.Binary(Opcode.Xor, mix, b.Binary(Opcode.Mul, ah, bh));
                b.Ret(new RegOperand(mix));
                return f;
            }
            long[][] inputs = { new long[] { 0, 0 }, new long[] { -1, -1 }, new long[] { long.MinValue, long.MaxValue },
                new long[] { 4294967295, 4294967295 }, new long[] { -7, 19 }, new long[] { long.MaxValue, long.MaxValue } };
            SameResults(Build, inputs, new WideProductSharing(), new IntegerValueReuse());
            Function changed = Build(); VReg original = changed.Entry.Instrs[0].Dest!;
            new WideProductSharing().Run(changed);
            Assert(changed.Entry.Instrs.Single(i => i.Dest == original).Op ==
                (mode is "full" or "square" ? Opcode.Or : Opcode.Mul), mode + " product sharing eligibility");
        }
    }

    private static void CarryRecognitionBoundaries()
    {
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (string mode in new[] { "plain", "swap", "logical", "wrong-sum", "mutated-source", "shared" })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(type, type, type);
                VReg a = f.Params[0], c = f.Params[1];
                VReg sum = b.Binary(mode == "wrong-sum" ? Opcode.Sub : Opcode.Add, a, c);
                VReg generate = b.Binary(Opcode.And, a, c);
                VReg either = b.Binary(Opcode.Or, mode == "swap" ? c : a, mode == "swap" ? a : c);
                VReg inverse = b.Unary(Opcode.Not, sum);
                VReg propagate = b.Binary(Opcode.And, either, inverse);
                VReg combined = b.Binary(Opcode.Or, generate, propagate);
                if (mode == "shared") Sink(b, combined);
                if (mode == "mutated-source") b.CopyTo(a, new ImmOperand(0, type));
                VReg shift = b.Binary(mode == "logical" ? Opcode.ShrU : Opcode.ShrS,
                    new RegOperand(combined), new ImmOperand(type == IrType.I64 ? 63 : 31, IrType.I32), type);
                b.Ret(new RegOperand(b.Binary(Opcode.And, shift, 1)));
                return f;
            }
            long[] edges = { 0, 1, -1, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue };
            long[][] inputs = edges.SelectMany(a => edges.Select(b => new[] { a, b })).ToArray();
            SameResults(Build, inputs, new CarryRecognition());
            Function transformed = Build(); new CarryRecognition().Run(transformed);
            Assert(transformed.Blocks.SelectMany(b => b.Instrs).Any(i => i.Op == Opcode.LtU)
                == (mode is "plain" or "swap" or "logical"), mode + " carry recognition guard");
        }
    }

    private static void NestedOwnedFieldBoundary()
    {
        foreach (string mode in new[] { "read", "return", "retain", "unknown", "return-middle" })
        {
        Module module = new("nested-owner");
        (Function f, Builder b) = Fn(IrType.I32);
        module.Functions.Add(f); module.Entry = f.Name;
        VReg outer = b.Unary(Opcode.Trunc64, b.Call(Escape.Allocator, IrType.I64, new ImmOperand(24, IrType.I64))!);
        VReg middle = b.Unary(Opcode.Trunc64, b.Call(Escape.Allocator, IrType.I64, new ImmOperand(24, IrType.I64))!);
        b.Store(new RegOperand(outer), new RegOperand(middle), 8, 4);
        VReg inner = b.Unary(Opcode.Trunc64, b.Call(Escape.Allocator, IrType.I64, new ImmOperand(64, IrType.I64))!);
        b.Store(new RegOperand(middle), new RegOperand(inner), 8, 4);
        b.Ret(new RegOperand(b.Call("return-grandchild", IrType.I32, new RegOperand(outer))!));
        Function callee = new("return-grandchild", IrType.I32);
        VReg parameter = callee.NewReg(IrType.I32); callee.Params.Add(parameter);
        Builder cb = new(callee, callee.NewBlock("entry"));
        VReg parent = cb.Load(IrType.I32, parameter, 8, 4, false);
        VReg reference = cb.Load(IrType.I32, parent, 8, 4, false);
        if (mode == "retain") cb.Store(new SymOperand("retained-grandchild"), new RegOperand(reference), 0, 4);
        if (mode == "unknown") cb.Call("unknown-reader", IrType.Void, new RegOperand(reference));
        cb.Ret(new RegOperand(mode == "return-middle" ? parent : mode == "return" ? reference
            : cb.Load(IrType.I32, reference, 0, 1, false)));
        module.Functions.Add(callee);
        Escape pass = new(); pass.Run(module);
        Verifier.Check(f, "nested owner caller");
        int expected = mode == "read" ? 3 : mode == "return-middle" ? 1 : 2;
        Assert(pass.Promoted == expected, mode + " respects ancestor-visible retention");
        Assert(f.Blocks.SelectMany(block => block.Instrs).Count(i => i.Callee == Escape.Allocator) == 3 - expected,
            mode + " keeps every escaping allocation reachable");
        }
    }

    private static void OwnedFieldBoundaries()
    {
        foreach (string mode in new[] { "read", "return", "retain", "overlap", "owner-earlier-block", "wrap-return",
            "nested-read", "nested-retain", "loop-persistent", "loop-renewed" })
        {
            Module module = new("owned-field-" + mode);
            (Function f, Builder b) = Fn(IrType.I32);
            module.Functions.Add(f); module.Entry = f.Name;
            VReg remaining = b.Const(2, IrType.I32);
            Block ownerBlock = b.Block;
            if (mode == "loop-renewed")
            {
                ownerBlock = f.NewBlock("owner"); b.Jump(ownerBlock); b.SetBlock(ownerBlock);
            }
            VReg owner = b.Call(Escape.Allocator, IrType.I64, new ImmOperand(24, IrType.I64))!;
            VReg address = b.Unary(Opcode.Trunc64, owner);
            if (mode is "owner-earlier-block" or "loop-persistent" or "loop-renewed")
            {
                Block body = f.NewBlock("body"); b.Jump(body); b.SetBlock(body);
            }
            VReg child = b.Call(Escape.Allocator, IrType.I64, new ImmOperand(64, IrType.I64))!;
            VReg childAddress = b.Unary(Opcode.Trunc64, child);
            b.Store(new RegOperand(address), new RegOperand(childAddress), 8, 4);
            VReg result = b.Call("read-owned-field", IrType.I32, new RegOperand(address))!;
            if (mode is "loop-persistent" or "loop-renewed")
            {
                Block body = b.Block, exit = f.NewBlock("exit");
                b.CopyTo(remaining, new RegOperand(b.Binary(Opcode.Sub, remaining, 1)));
                b.Branch(remaining, mode == "loop-renewed" ? ownerBlock : body, exit);
                b.SetBlock(exit);
            }
            b.Ret(new RegOperand(result));
            Function callee = new("read-owned-field", IrType.I32);
            VReg receiver = callee.NewReg(IrType.I32); callee.Params.Add(receiver);
            Builder cb = new(callee, callee.NewBlock("entry"));
            if (mode.StartsWith("nested-"))
            {
                Function helper = new("read-owned-field-helper", IrType.I32);
                VReg parameter = helper.NewReg(IrType.I32); helper.Params.Add(parameter);
                Builder hb = new(helper, helper.NewBlock("entry"));
                VReg reference = hb.Load(IrType.I32, parameter, 8, 4, false);
                if (mode == "nested-retain") hb.Store(new SymOperand("escaped-child"), new RegOperand(reference), 0, 4);
                hb.Ret(new RegOperand(hb.Load(IrType.I32, reference, 0, 1, false)));
                module.Functions.Add(helper);
                cb.Call(helper.Name, IrType.I32, new RegOperand(receiver));
            }
            if (mode == "wrap-return") receiver = cb.Binary(Opcode.Add, new RegOperand(receiver),
                new ImmOperand(4294967296L, IrType.I32), IrType.I32);
            VReg pointer = cb.Load(IrType.I32, receiver, mode == "overlap" ? 9 : 8, mode == "overlap" ? 1 : 4, false);
            if (mode == "retain") cb.Store(new SymOperand("escaped-child"), new RegOperand(pointer), 0, 4);
            cb.Ret(new RegOperand(mode is "return" or "overlap" or "wrap-return" ? pointer : cb.Load(IrType.I32, pointer, 0, 1, false)));
            module.Functions.Add(callee);
            Escape pass = new(); pass.Run(module);
            Verifier.Check(f, "owned field caller"); Verifier.Check(callee, "owned field callee");
            Assert(pass.Promoted == (mode is "read" or "nested-read" or "owner-earlier-block" or "loop-renewed" ? 2 : 1),
                mode + " promotion count");
        }
    }

    private static void IntegerChainBoundaries()
    {
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (Opcode op in new[] { Opcode.Add, Opcode.Sub, Opcode.Mul, Opcode.And, Opcode.Or, Opcode.Xor })
        foreach (string mode in new[] { "plain", "source", "result", "self" })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(type, type);
                VReg x = f.Params[0];
                VReg first = b.Binary(op, new RegOperand(x), new ImmOperand(long.MaxValue, type), type);
                if (mode == "source") b.CopyTo(x, new ImmOperand(17, type));
                if (mode == "result") b.CopyTo(first, new ImmOperand(19, type));
                VReg last = mode == "self" ? x : f.NewReg(type);
                b.Block.Instrs.Add(new Instr { Op = op, Dest = last,
                    Operands = { new RegOperand(first), new ImmOperand(13, type) } });
                b.Ret(new RegOperand(last));
                return f;
            }
            long[][] values = { new long[] { 0 }, new long[] { 1 }, new long[] { -1 },
                new long[] { int.MinValue }, new long[] { int.MaxValue },
                new long[] { long.MinValue }, new long[] { long.MaxValue } };
            SameResults(Build, values, new IntegerReassociate());
            SameResults(Build, values, new IntegerReassociate(), new IntegerReassociate());
            Function transformed = Build(); new IntegerReassociate().Run(transformed);
            Instr last = transformed.Blocks[0].Instrs[^2];
            Assert((last.Operands[0] is RegOperand r && r.Reg == transformed.Params[0])
                == (mode is "plain" or "self"), "chain forwarding respects " + mode);
        }
    }

    private static void IntegerReuseBoundaries()
    {
        foreach (string mode in new[] { "plain", "source", "result", "arm", "join" })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(IrType.I32, IrType.I32, IrType.I32);
                VReg x = f.Params[0], y = f.Params[1];
                VReg first = b.Binary(Opcode.Xor, x, y);
                if (mode == "source") b.CopyTo(x, new ImmOperand(17, IrType.I32));
                if (mode == "result") b.CopyTo(first, new ImmOperand(19, IrType.I32));
                if (mode is "arm" or "join")
                {
                    Block yes = f.NewBlock("yes"), no = f.NewBlock("no"), join = f.NewBlock("join");
                    b.Branch(y, yes, no);
                    b.SetBlock(no); b.CopyTo(x, new ImmOperand(23, IrType.I32)); b.Jump(join);
                    b.SetBlock(yes);
                    if (mode == "arm")
                    {
                        VReg repeated = b.Binary(Opcode.Xor, y, x);
                        b.Ret(new RegOperand(b.Binary(Opcode.Add, repeated, first)));
                    }
                    else b.Jump(join);
                    b.SetBlock(join);
                }
                VReg second = b.Binary(Opcode.Xor, y, x);
                b.Ret(new RegOperand(b.Binary(Opcode.Add, first, second)));
                return f;
            }
            SameResults(Build, Inputs2, new IntegerValueReuse());
            SameResults(Build, Inputs2, new IntegerValueReuse(), new IntegerValueReuse());
        }
        SameResults(Fib, Inputs, new IntegerValueReuse());
        SameResults(Swap, Inputs, new IntegerValueReuse());
        SameResults(SwitchLoop, Inputs, new IntegerValueReuse());
    }

    private static void DivisionReuseBoundaries()
    {
        long[][] inputs = { new long[] { 7, 3 }, new long[] { -7, 3 }, new long[] { 7, -3 },
            new long[] { -7, -3 }, new long[] { int.MinValue, 3 }, new long[] { int.MaxValue, 5 } };
        foreach (string mode in new[] { "reuse", "numerator", "divisor", "quotient", "block" })
        {
            Function Build()
            {
                (Function f, Builder b) = Fn(IrType.I32, IrType.I32, IrType.I32);
                VReg q = b.Binary(Opcode.DivS, f.Params[0], f.Params[1]);
                if (mode == "numerator") b.CopyTo(f.Params[0], new ImmOperand(21, IrType.I32));
                if (mode == "divisor") b.CopyTo(f.Params[1], new ImmOperand(11, IrType.I32));
                if (mode == "quotient") b.CopyTo(q, new ImmOperand(91, IrType.I32));
                if (mode == "block") { Block next = f.NewBlock("next"); b.Jump(next); b.SetBlock(next); }
                VReg remainder = b.Binary(Opcode.RemS, f.Params[0], f.Params[1]);
                b.Ret(new RegOperand(b.Binary(Opcode.Add, q, remainder)));
                return f;
            }
            SameResults(Build, inputs, new DivRemReuse());
            Function transformed = Build(); new DivRemReuse().Run(transformed);
            Assert(transformed.Blocks.SelectMany(b => b.Instrs).Any(i => i.Op == Opcode.RemS) == (mode != "reuse"),
                mode + " preserves required remainder operations");
            Assert(transformed.Blocks.SelectMany(b => b.Instrs).Any(i => i.Op == Opcode.DivS),
                "original division and its trap point remain");
        }
    }

    private static void LocalCopyBoundaries()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32, IrType.I32);
            VReg source = b.Copy(f.Params[0]);
            VReg captured = b.Copy(source);
            VReg chain = b.Copy(captured);
            b.CopyTo(source, new ImmOperand(91, IrType.I32));
            Block yes = f.NewBlock("yes"), no = f.NewBlock("no"), join = f.NewBlock("join");
            b.Branch(f.Params[1], yes, no);
            b.SetBlock(yes); b.CopyTo(captured, new RegOperand(source)); b.Jump(join);
            b.SetBlock(no); b.CopyTo(source, new RegOperand(chain)); b.Jump(join);
            b.SetBlock(join);
            b.Ret(new RegOperand(b.Binary(Opcode.Add, b.Binary(Opcode.Add, source, captured), chain)));
            return f;
        }
        SameResults(Build, Inputs2, new LocalCopies());
        SameResults(Build, Inputs2, new LocalCopies(), new LocalCopies());
        SameResults(Fib, Inputs, new LocalCopies());
        SameResults(Swap, Inputs, new LocalCopies());
        SameResults(SwitchLoop, Inputs, new LocalCopies());
    }

    private static void OwnedFreeAbi()
    {
        Module module = new("owned-abi");
        (Function f, Builder b) = Fn(IrType.I64, IrType.I64);
        module.Functions.Add(f); module.Entry = f.Name;
        VReg allocation = b.Call(Escape.Allocator, IrType.I64, new RegOperand(f.Params[0]))!;
        VReg address = b.Unary(Opcode.Trunc64, allocation);
        b.Store(new RegOperand(address), new ImmOperand(123, IrType.I64), 0, 8);
        b.Ret(new RegOperand(b.Load(IrType.I64, address, 0, 8)));
        Function free = new(Escape.Freer, IrType.Void);
        free.Params.Add(free.NewReg(IrType.I64));
        new Builder(free, free.NewBlock("entry")).Ret();
        module.Functions.Add(free);
        Escape pass = new(); pass.Run(module);
        Assert(pass.Owned == 1, "dynamic local allocation is owned");
        Verifier.Check(f, "owned allocation ABI");
        Instr[] calls = f.Blocks.SelectMany(x => x.Instrs).Where(i => i.Callee == Escape.Freer).ToArray();
        Assert(calls.Length == 2, "free before replacement and at return");
        Assert(calls.All(i => i.Operands.Count == 1 && i.Operands[0].Type == IrType.I64),
            "every compiler-inserted free receives a complete long address");
    }

    private static void ScalarObjectBoundaries()
    {
        foreach (string mode in new[] { "plain", "byte", "short", "zero", "call", "escape", "overlap", "identity" })
        {
            Module module = new("scalar-" + mode);
            (Function f, Builder b) = Fn(IrType.I32);
            module.Functions.Add(f);
            VReg pointer = b.Call(Escape.Allocator, IrType.I64, new ImmOperand(32, IrType.I64))!;
            VReg address = b.Unary(Opcode.Trunc64, pointer);
            int width = mode == "byte" ? 1 : mode == "short" ? 2 : 4;
            if (mode != "zero") b.Store(new RegOperand(address), new ImmOperand(-1, IrType.I32), 8, width);
            if (mode == "call") b.Call("unknown", IrType.Void, new RegOperand(address));
            if (mode == "escape") b.Store(new SymOperand("escaped"), new RegOperand(address), 0, 4);
            if (mode == "overlap") b.Store(new RegOperand(address), new ImmOperand(7, IrType.I32), 9, 1);
            if (mode == "identity") b.Store(new SymOperand("identity"),
                new RegOperand(b.Binary(Opcode.Eq, address, 0)), 0, 4);
            VReg value = b.Load(IrType.I32, address, 8, width, true);
            b.Ret(new RegOperand(value));
            string before = Dump(f);
            ScalarObjects pass = new(); pass.Run(module);
            bool accepted = mode is "plain" or "byte" or "short" or "zero";
            Assert(pass.Replaced == (accepted ? 1 : 0), mode + " replacement decision");
            Verifier.Check(f, "scalar " + mode);
            if (!accepted) Assert(before == Dump(f), mode + " keeps original object semantics");
            else
            {
                Assert(!f.Blocks.SelectMany(x => x.Instrs).Any(i => i.Op is Opcode.Call or Opcode.Load or Opcode.Store),
                    mode + " allocation and field memory removed");
                if (mode == "byte" || mode == "short")
                {
                    Opcode store = mode == "byte" ? Opcode.ZExt8 : Opcode.ZExt16;
                    Opcode load = mode == "byte" ? Opcode.SExt8 : Opcode.SExt16;
                    Assert(f.Blocks.SelectMany(x => x.Instrs).Any(i => i.Op == store), "store truncates");
                    Assert(f.Blocks.SelectMany(x => x.Instrs).Any(i => i.Op == load), "load sign extends");
                }
            }
        }
    }

    private static void HotAllocationBudget()
    {
        Module module = new("hot-budget");
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        module.Functions.Add(f); module.Entry = f.Name;
        VReg cold = b.Unary(Opcode.Trunc64, b.Call(Escape.Allocator, IrType.I64, new ImmOperand(32, IrType.I64))!);
        b.Store(new RegOperand(cold), new ImmOperand(7, IrType.I32), 0, 4);
        Block loop = f.NewBlock("loop"), exit = f.NewBlock("exit");
        b.Jump(loop); b.SetBlock(loop);
        VReg hot = b.Unary(Opcode.Trunc64, b.Call(Escape.Allocator, IrType.I64, new ImmOperand(32, IrType.I64))!);
        b.Store(new RegOperand(hot), new ImmOperand(3, IrType.I32), 0, 4);
        b.Load(IrType.I32, hot, 0, 4);
        b.Branch(f.Params[0], loop, exit);
        b.SetBlock(exit); b.Ret(new RegOperand(b.Load(IrType.I32, cold, 0, 4)));
        Escape pass = new() { FrameBudget = 32 }; pass.Run(module);
        Verifier.Check(f, "hot allocation budget");
        Assert(pass.Promoted == 1, "budget is not enlarged");
        Assert(f.Blocks[0].Instrs.Any(i => i.Op == Opcode.Call && i.Callee == Escape.Allocator),
            "one-time object stays on heap when budget is consumed");
        Assert(!loop.Instrs.Any(i => i.Op == Opcode.Call && i.Callee == Escape.Allocator),
            "loop object gets the frame slot");
    }

    private static void ArgumentCostInlining()
    {
        foreach (IrType type in new[] { IrType.I32, IrType.I64 })
        foreach (int credit in new[] { 0, 3 })
        foreach (int limit in new[] { 1, 100 })
        {
            Module module = new("argument-cost");
            Function callee = new("mix", type);
            VReg a = callee.NewReg(type), c = callee.NewReg(type);
            callee.Params.Add(a); callee.Params.Add(c);
            Builder cb = new(callee, callee.NewBlock("entry"));
            VReg result = a;
            for (int n = 0; n < 10; n++) result = cb.Binary(Opcode.Add, result, c);
            cb.Ret(new RegOperand(result));
            Function caller = new("main", type);
            VReg x = caller.NewReg(type), y = caller.NewReg(type);
            caller.Params.Add(x); caller.Params.Add(y);
            Builder b = new(caller, caller.NewBlock("entry"));
            VReg first = b.Call("mix", type, new RegOperand(x), new RegOperand(y))!;
            VReg second = b.Call("mix", type, new RegOperand(y), new RegOperand(x))!;
            b.Ret(new RegOperand(b.Binary(Opcode.Add, first, second)));
            module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
            new Inline { SmallBody = 1, ArgumentWordCredit = credit, GrowthLimit = limit }.Run(module);
            Verifier.Check(caller, "argument cost inline");
            bool expanded = type == IrType.I64 && credit == 3 && limit == 100;
            Assert(caller.Blocks.SelectMany(x => x.Instrs).Count(i => i.Op == Opcode.Call && i.Callee == "mix")
                == (expanded ? 0 : 2), "argument width affects cost, not growth legality");
        }
    }

    private static void BranchCostInlining()
    {
        foreach (int penalty in new[] { 0, 2 })
        foreach (int sites in new[] { 1, 2 })
        {
            Module module = new("branch-cost");
            Function callee = new("choose", IrType.I32);
            VReg condition = callee.NewReg(IrType.I32); callee.Params.Add(condition);
            Block entry = callee.NewBlock("entry"), yes = callee.NewBlock("yes"), no = callee.NewBlock("no");
            new Builder(callee, entry).Branch(condition, yes, no);
            new Builder(callee, yes).Ret(new ImmOperand(1, IrType.I32));
            new Builder(callee, no).Ret(new ImmOperand(2, IrType.I32));
            Function caller = new("main", IrType.I32);
            VReg input = caller.NewReg(IrType.I32); caller.Params.Add(input);
            Builder b = new(caller, caller.NewBlock("entry"));
            VReg value = input;
            for (int n = 0; n < sites; n++) value = b.Call("choose", IrType.I32, new RegOperand(value))!;
            b.Ret(new RegOperand(value));
            module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
            new Inline { SmallBody = 3, ConditionalBranchCost = penalty, GrowthLimit = 100 }.Run(module);
            Verifier.Check(caller, "branch cost inline");
            bool expanded = penalty == 0 || sites == 1;
            Assert(caller.Blocks.SelectMany(block => block.Instrs).Count(i => i.Op == Opcode.Call && i.Callee == "choose")
                == (expanded ? 0 : sites), "branch duplication penalty does not block single-site elimination");
        }
    }

    private static void FreshOwnerInlining()
    {
        foreach (bool fresh in new[] { false, true })
        foreach (int limit in new[] { 1, 100 })
        foreach (int bodyLimit in new[] { 1, 100 })
        foreach (int freshLimit in new[] { 0, 100 })
        {
            Module module = new("fresh-owner");
            Function callee = new("initialize", IrType.I64);
            VReg receiver = callee.NewReg(IrType.I64); callee.Params.Add(receiver);
            Builder cb = new(callee, callee.NewBlock("entry"));
            VReg child = cb.Call(Escape.Allocator, IrType.I64, new ImmOperand(32, IrType.I64))!;
            cb.Ret(new RegOperand(child));
            Function caller = new("main", IrType.I64);
            VReg input = caller.NewReg(IrType.I64); caller.Params.Add(input);
            Builder b = new(caller, caller.NewBlock("entry"));
            VReg owner = fresh ? b.Call(Escape.Allocator, IrType.I64, new ImmOperand(16, IrType.I64))! : input;
            VReg first = b.Call("initialize", IrType.I64, new RegOperand(owner))!;
            VReg second = b.Call("initialize", IrType.I64, new RegOperand(owner))!;
            b.Ret(new RegOperand(b.Binary(Opcode.Add, first, second)));
            module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
            new Inline { SmallBody = 1, FreshOwnerBody = bodyLimit, GrowthLimit = limit,
                FreshOwnerGrowthLimit = freshLimit }.Run(module);
            Verifier.Check(caller, "fresh-owner inline");
            bool expanded = fresh && (limit == 100 || freshLimit == 100) && bodyLimit == 100;
            Assert(caller.Blocks.SelectMany(x => x.Instrs).Count(i => i.Op == Opcode.Call && i.Callee == "initialize")
                == (expanded ? 0 : 2), "only fresh owner with both budgets expands");
        }
    }

    private static void ConstantBranchInlining()
    {
        foreach (bool constant in new[] { false, true })
        foreach (int limit in new[] { 1, 100 })
        {
            Module module = new("constant-branch");
            Function callee = new("choose", IrType.I32);
            VReg condition = callee.NewReg(IrType.I32); callee.Params.Add(condition);
            Block entry = callee.NewBlock("entry"), yes = callee.NewBlock("yes"), no = callee.NewBlock("no");
            Builder cb = new(callee, entry); cb.Branch(condition, yes, no);
            cb.SetBlock(yes); cb.Ret(new ImmOperand(11, IrType.I32));
            cb.SetBlock(no); cb.Ret(new ImmOperand(22, IrType.I32));
            Function caller = new("main", IrType.I32);
            VReg input = caller.NewReg(IrType.I32); caller.Params.Add(input);
            Builder b = new(caller, caller.NewBlock("entry"));
            Operand arg = constant ? new ImmOperand(1, IrType.I32) : new RegOperand(input);
            VReg first = b.Call("choose", IrType.I32, arg)!;
            VReg second = b.Call("choose", IrType.I32, arg)!;
            b.Ret(new RegOperand(b.Binary(Opcode.Add, first, second)));
            module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
            new Inline { SmallBody = 1, ConstantBranchBody = 100, GrowthLimit = limit }.Run(module);
            Verifier.Check(caller, "constant branch inline");
            bool expanded = constant && limit == 100;
            Assert(caller.Blocks.SelectMany(x => x.Instrs).Count(i => i.Op == Opcode.Call) == (expanded ? 0 : 2),
                "only constant-controlled calls inside growth budget expand");
            Interp interpreter = new(); interpreter.Calls["choose"] = a => a[0] != 0 ? 11 : 22;
            Assert(interpreter.Run(caller, 0) == (constant ? 22 : 44), "zero input retains semantics");
            Assert(interpreter.Run(caller, 1) == 22, "nonzero input retains semantics");
        }
    }

    // ---- Helpers -------------------------------------------------------

    private static (Function F, Builder B) Fn(IrType returns, params IrType[] ps)
    {
        Function f = new("t", returns);
        foreach (IrType t in ps)
        {
            f.Params.Add(f.NewReg(t, "p" + f.Params.Count));
        }
        Block entry = f.NewBlock("entry");
        return (f, new Builder(f, entry));
    }

    private static string Dump(Function f)
    {
        StringBuilder sb = new();
        f.Dump(sb);
        return sb.ToString();
    }

    /// <summary>The instructions of the function, one per line, without labels or the header.</summary>
    private static string Body(Function f)
        => string.Join("\n", f.Blocks.SelectMany(b => b.Instrs).Select(i => i.ToString()));

    private static void Run(Function f, params IPass[] passes)
    {
        Verifier.Check(f, "before");
        foreach (IPass p in passes)
        {
            p.Run(f);
            Verifier.Check(f, "after " + p.Name);
            if (_verbose)
            {
                Console.WriteLine($"--- after {p.Name}\n{Dump(f)}");
            }
        }
    }

    private static void RunAll(Function f)
    {
        Pipeline p = Pipeline.Default();
        p.Verify = true;
        if (_verbose)
        {
            p.Trace = (pass, fn, dump) => Console.WriteLine($"--- after {pass.Name}\n{dump}");
        }
        p.Run(f);
    }

    private static void Expect(Function f, string body)
    {
        string got = Body(f);
        if (got != body)
        {
            throw new Exception($"expected:\n{body}\ngot:\n{got}\nfull dump:\n{Dump(f)}");
        }
    }

    private static void ExpectContains(Function f, string line)
    {
        if (!Body(f).Split('\n').Contains(line))
        {
            throw new Exception($"expected a line `{line}` in:\n{Dump(f)}");
        }
    }

    private static void ExpectMissing(Function f, string fragment)
    {
        if (Body(f).Contains(fragment))
        {
            throw new Exception($"did not expect `{fragment}` in:\n{Dump(f)}");
        }
    }

    /// <summary>A store to a symbol, so a value stays alive without a register holding the address.</summary>
    private static void Sink(Builder b, VReg v) => b.Store(new SymOperand("sink"), new RegOperand(v));

    private static void Assert(bool cond, string what)
    {
        if (!cond)
        {
            throw new Exception(what);
        }
    }

    private static void Try(string name, Action test)
    {
        try
        {
            test();
            _passes++;
            Console.WriteLine($"ok   {name}");
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}\n     {e.Message.Replace("\n", "\n     ")}");
        }
    }

    private static int DefCount(Function f, VReg r)
        => f.Blocks.SelectMany(b => b.Instrs).Count(i => ReferenceEquals(i.Dest, r)) + (f.Params.Contains(r) ? 1 : 0);

    private static bool AllSingleDef(Function f)
        => f.Blocks.SelectMany(b => b.Instrs).Where(i => i.Dest is not null).GroupBy(i => i.Dest!).All(g => g.Count() == 1 && !f.Params.Contains(g.Key));

    private static int PhiCount(Function f) => f.Blocks.SelectMany(b => b.Instrs).Count(Phi.IsPhi);

    /// <summary>Runs the function through the interpreter for each argument set, before and after the passes, and compares.</summary>
    private static void SameResults(Func<Function> build, IEnumerable<long[]> inputs, params IPass[] passes)
    {
        Function before = build();
        Function after = build();
        Run(after, passes);
        foreach (long[] args in inputs)
        {
            long x = new Interp().Run(before, args);
            long y = new Interp().Run(after, args);
            if (x != y)
            {
                throw new Exception($"args [{string.Join(", ", args)}]: before {x}, after {y}\n{Dump(after)}");
            }
        }
    }

    // ---- Constant folding ----------------------------------------------

    private static void FoldArithmetic()
    {
        (Function f, Builder b) = Fn(IrType.I32);
        VReg two = b.Const(2, IrType.I32);
        VReg three = b.Const(3, IrType.I32);
        VReg x = b.Binary(Opcode.Add, two, three);
        VReg y = b.Binary(Opcode.Mul, x, 4);
        b.Ret(new RegOperand(y));
        RunAll(f);
        Expect(f, "ret 20");
    }

    private static void FoldWidths()
    {
        (Function f, Builder b) = Fn(IrType.Void);
        VReg max = b.Const(int.MaxValue, IrType.I32);
        VReg wrapped = b.Binary(Opcode.Add, max, 1);             // wraps to int.MinValue
        VReg wide = b.Const(int.MaxValue, IrType.I64);
        VReg notWrapped = b.Binary(Opcode.Add, wide, 1);         // 2147483648 in I64
        VReg one = b.Const(1, IrType.I32);
        VReg shifted = b.Binary(Opcode.Shl, new RegOperand(one), new ImmOperand(33, IrType.I32), IrType.I32); // count masked to 1
        VReg minus = b.Const(-1, IrType.I32);
        VReg ltu = b.Binary(Opcode.LtU, minus, 0);               // 0xffffffff <u 0 is false
        VReg lts = b.Binary(Opcode.LtS, minus, 0);               // -1 <s 0 is true
        VReg shru = b.Binary(Opcode.ShrU, minus, 28);            // 0xffffffff >>u 28 = 15
        VReg sext = b.Unary(Opcode.SExt8, b.Const(0x80, IrType.I32));
        VReg trunc = b.Unary(Opcode.Trunc64, b.Const(0x1_0000_0005, IrType.I64));
        VReg zext = b.Unary(Opcode.ZExt32, b.Const(-1, IrType.I32));
        // Keep every result alive through a store so DCE cannot remove it.
        foreach (VReg r in new[] { wrapped, shifted, ltu, lts, shru, sext, trunc, notWrapped, zext })
        {
            Sink(b, r);
        }
        b.Ret();
        RunAll(f);
        ExpectContains(f, "store.u4 @sink -2147483648");
        ExpectContains(f, "store.u4 @sink 2");
        ExpectContains(f, "store.u4 @sink 0");
        ExpectContains(f, "store.u4 @sink 1");
        ExpectContains(f, "store.u4 @sink 15");
        ExpectContains(f, "store.u4 @sink -128");
        ExpectContains(f, "store.u4 @sink 5");
        ExpectContains(f, "store.u8 @sink 2147483648");
        ExpectContains(f, "store.u8 @sink 4294967295");
    }

    private static void FoldDivision()
    {
        (Function f, Builder b) = Fn(IrType.Void);
        Sink(b, b.Binary(Opcode.DivS, b.Const(7, IrType.I32), 0));
        Sink(b, b.Binary(Opcode.RemU, b.Const(7, IrType.I32), 0));
        Sink(b, b.Binary(Opcode.DivS, b.Const(int.MinValue, IrType.I32), -1));
        Sink(b, b.Binary(Opcode.DivS, b.Const(-7, IrType.I32), 2));
        Sink(b, b.Binary(Opcode.DivU, b.Const(-7, IrType.I32), 2));
        b.Ret();
        RunAll(f);
        ExpectContains(f, "%1 = divs 7 0");
        ExpectContains(f, "%3 = remu 7 0");
        ExpectContains(f, "%5 = divs -2147483648 -1");
        ExpectContains(f, "store.u4 @sink -3");
        ExpectContains(f, "store.u4 @sink 2147483644");
    }

    private static void FoldFloatsStay()
    {
        (Function f, Builder b) = Fn(IrType.F32, IrType.F32);
        VReg x = b.Binary(Opcode.FAdd, f.Params[0], f.Params[0]);
        VReg y = b.Binary(Opcode.FMul, x, f.Params[0]);
        b.Ret(new RegOperand(y));
        RunAll(f);
        Expect(f, "%1 = fadd %0.p0 %0.p0\n%2 = fmul %1 %0.p0\nret %2");
    }

    private static void FoldBranchSwitch()
    {
        (Function f, Builder b) = Fn(IrType.I32);
        Block t = f.NewBlock("t");
        Block e = f.NewBlock("e");
        Block s0 = f.NewBlock("s0");
        Block s1 = f.NewBlock("s1");
        Block d = f.NewBlock("d");
        b.Branch(b.Const(0, IrType.I32), t, e);
        b.SetBlock(t);
        b.Ret(new ImmOperand(1, IrType.I32));
        b.SetBlock(e);
        b.Switch(new RegOperand(b.Const(1, IrType.I32)), new[] { s0, s1 }, d);
        b.SetBlock(s0);
        b.Ret(new ImmOperand(10, IrType.I32));
        b.SetBlock(s1);
        b.Ret(new ImmOperand(11, IrType.I32));
        b.SetBlock(d);
        b.Ret(new ImmOperand(12, IrType.I32));
        RunAll(f);
        Expect(f, "ret 11");
        Assert(f.Blocks.Count == 1, "expected one block");
    }

    // ---- Propagation ---------------------------------------------------

    private static void CopyChain()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        VReg a = b.Copy(f.Params[0]);
        VReg c = b.Copy(a);
        VReg d = b.Copy(c);
        b.Ret(new RegOperand(d));
        RunAll(f);
        Expect(f, "ret %0.p0");
    }

    private static void MultiDefStays()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block t = f.NewBlock("t");
        Block j = f.NewBlock("j");
        VReg x = b.Reg(IrType.I32, "x");
        b.CopyTo(x, new ImmOperand(1, IrType.I32));
        b.Branch(f.Params[0], t, j);
        b.SetBlock(t);
        b.CopyTo(x, new ImmOperand(2, IrType.I32));
        b.Jump(j);
        b.SetBlock(j);
        VReg y = b.Binary(Opcode.Add, x, 1);
        b.Ret(new RegOperand(y));
        RunAll(f);
        ExpectContains(f, "%2 = add %1.x 1");
        ExpectContains(f, "%1.x = copy 2");
    }

    /// <summary>
    /// entry: jump W. W: w = load p; branch p -> D, B. D: v = copy w; jump W.
    /// B: ret v. The ret sees the copy from the previous trip round, while
    /// w has been reloaded since; ret v must not become ret w.
    /// </summary>
    private static void LoopCarriedCopy()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block w = f.NewBlock("W");
        Block d = f.NewBlock("D");
        Block r = f.NewBlock("B");
        VReg v = b.Reg(IrType.I32, "v");
        b.Jump(w);
        b.SetBlock(w);
        VReg loaded = b.Load(IrType.I32, f.Params[0]);
        b.Branch(f.Params[0], d, r);
        b.SetBlock(d);
        b.CopyTo(v, new RegOperand(loaded));
        b.Jump(w);
        b.SetBlock(r);
        b.Ret(new RegOperand(v));
        RunAll(f);
        ExpectContains(f, "ret %1.v");
        ExpectContains(f, "%1.v = copy %2");
    }

    /// <summary>
    /// L: v = copy w; x = add v, 1; w = load p; branch -> L, exit. The use
    /// of v between the copy and w's redefinition may read w; a use after
    /// the redefinition may not.
    /// </summary>
    private static void CopyBeforeRedefinition()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block l = f.NewBlock("L");
        Block exit = f.NewBlock("X");
        VReg w = b.Reg(IrType.I32, "w");
        VReg v = b.Reg(IrType.I32, "v");
        b.CopyTo(w, new ImmOperand(0, IrType.I32));      // w has two defs: not a candidate at all
        b.Jump(l);
        b.SetBlock(l);
        b.CopyTo(v, new RegOperand(w));
        VReg x = b.Binary(Opcode.Add, v, 1);
        b.CopyTo(w, new RegOperand(b.Load(IrType.I32, f.Params[0])));
        VReg y = b.Binary(Opcode.Add, v, 2);
        b.Store(f.Params[0], x);
        b.Store(f.Params[0], y);
        b.Branch(f.Params[0], l, exit);
        b.SetBlock(exit);
        b.Ret(new RegOperand(v));
        Run(f, new ConstantAndCopyPropagation());
        ExpectContains(f, "%3 = add %2.v 1");
        ExpectContains(f, "%5 = add %2.v 2");
        ExpectContains(f, "ret %2.v");

        // Now the single-def shape: w defined once, after the copy, in the loop.
        (f, b) = Fn(IrType.I32, IrType.I32);
        l = f.NewBlock("L");
        exit = f.NewBlock("X");
        w = b.Reg(IrType.I32, "w");
        v = b.Reg(IrType.I32, "v");
        b.Jump(l);
        b.SetBlock(l);
        b.CopyTo(v, new RegOperand(w));
        x = b.Binary(Opcode.Add, v, 1);
        b.CopyTo(w, new RegOperand(b.Load(IrType.I32, f.Params[0])));
        y = b.Binary(Opcode.Add, v, 2);
        b.Store(f.Params[0], x);
        b.Store(f.Params[0], y);
        b.Branch(f.Params[0], l, exit);
        b.SetBlock(exit);
        b.Ret(new RegOperand(v));
        Run(f, new ConstantAndCopyPropagation());
        // The chain goes one further: at that point the load's register
        // still holds the previous trip's value, which is what w was.
        ExpectContains(f, "%3 = add %4 1");
        ExpectContains(f, "%5 = add %2.v 2");
        ExpectContains(f, "ret %2.v");
    }

    /// <summary>
    /// pad: e = call __exception; v = copy e; jump after. after: ret v. A
    /// second unwind into the pad redefines e along an edge the graph does
    /// not have, so ret v must not become ret e; inside the pad it may.
    /// </summary>
    private static void LandingPadValue()
    {
        (Function f, Builder b) = Fn(IrType.I32);
        Block pad = f.NewBlock("pad");
        Block after = f.NewBlock("after");
        pad.IsLandingPad = true;
        b.Ret(new ImmOperand(0, IrType.I32));
        b.SetBlock(pad);
        VReg e = b.Call("__exception", IrType.I32)!;
        VReg v = b.Copy(e);
        Sink(b, b.Binary(Opcode.Add, v, 1));
        b.Jump(after);
        b.SetBlock(after);
        b.Ret(new RegOperand(v));
        Run(f, new ConstantAndCopyPropagation());
        ExpectContains(f, "%2 = add %0 1");
        ExpectContains(f, "ret %1");
    }

    // ---- Dead code -----------------------------------------------------

    private static void DeadCode()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32);
        FrameSlot slot = f.NewSlot(4, 4, "s");
        VReg dead1 = b.Load(IrType.I32, new SlotOperand(slot));  // frame memory: cannot fault
        b.Binary(Opcode.Add, dead1, 1);
        b.Load(IrType.I32, new SymOperand("g"));                 // a static: cannot fault
        b.Load(IrType.I32, f.Params[0]);                         // through a register: may fault, stays
        b.Binary(Opcode.DivS, f.Params[0], f.Params[0]);        // may trap: stays
        b.Call("side_effect", IrType.I32);                       // stays, loses its dest
        b.Store(f.Params[0], f.Params[0]);
        b.Ret();
        Run(f, new DeadCodeElimination());
        Expect(f, "%4 = load.s4 %0.p0\n%5 = divs %0.p0 %0.p0\ncall side_effect\nstore.u4 %0.p0 %0.p0\nret");
    }

    private static void DeadCallResult()
    {
        (Function f, Builder b) = Fn(IrType.Void);
        VReg r = b.Call("f", IrType.I32)!;
        VReg s = b.Copy(r);
        b.Binary(Opcode.Add, s, 1);
        b.Ret();
        RunAll(f);
        Expect(f, "call f\nret");
    }

    // ---- Branches ------------------------------------------------------

    private static void ConstantBranchThreads()
    {
        (Function f, Builder b) = Fn(IrType.I32);
        Block t = f.NewBlock("t");
        Block e = f.NewBlock("e");
        Block join = f.NewBlock("join");
        VReg r = b.Reg(IrType.I32, "r");
        b.Branch(b.Const(1, IrType.I32), t, e);
        b.SetBlock(t);
        b.CopyTo(r, new ImmOperand(1, IrType.I32));
        b.Jump(join);
        b.SetBlock(e);
        b.CopyTo(r, new ImmOperand(2, IrType.I32));
        b.Jump(join);
        b.SetBlock(join);
        b.Ret(new RegOperand(r));
        RunAll(f);
        // r had two definitions before the dead branch went; the second
        // round of the pipeline sees one and finishes the job.
        Expect(f, "ret 1");
        Assert(f.Blocks.Count == 1, "expected one block");
    }

    private static void SwitchThreading()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block s0 = f.NewBlock("s0");
        Block hop = f.NewBlock("hop");
        Block d = f.NewBlock("d");
        Block target = f.NewBlock("target");
        b.Switch(new RegOperand(f.Params[0]), new[] { s0, hop }, hop);
        b.SetBlock(s0);
        b.Ret(new ImmOperand(0, IrType.I32));
        b.SetBlock(hop);
        b.Jump(target);
        b.SetBlock(d);                          // never reached: removed
        b.Ret(new ImmOperand(99, IrType.I32));
        b.SetBlock(target);
        b.Ret(new ImmOperand(7, IrType.I32));
        Run(f, new BranchSimplify());
        ExpectContains(f, "switch %0.p0 ->s01 ->target4 default->target4");
        Assert(!f.Blocks.Contains(hop) && !f.Blocks.Contains(d), "hop and d should be gone");
        Assert(f.Entry.Label == "entry0", "entry stays first");
    }

    private static void LandingPadKept()
    {
        (Function f, Builder b) = Fn(IrType.Void);
        Block pad = f.NewBlock("pad");
        Block after = f.NewBlock("after");
        Block padOnly = f.NewBlock("padjump");
        pad.IsLandingPad = true;
        padOnly.IsLandingPad = true;
        FrameSlot rec = f.NewSlot(16, 4, "handler");
        b.Store(b.SlotAddress(rec), b.LabelAddress(pad), 4);
        b.Jump(after);
        b.SetBlock(after);
        b.Ret();
        b.SetBlock(pad);
        VReg ex = b.Call("__exception", IrTypes.Word)!;
        b.Emit(Opcode.Unwind, null, new RegOperand(ex), new RegOperand(ex));
        b.SetBlock(padOnly);
        b.Jump(after);
        RunAll(f);
        Assert(f.Blocks.Contains(pad), "pad removed");
        Assert(f.Blocks.Contains(padOnly), "jump-only pad removed");
        ExpectContains(f, "%2 = call __exception");
        // after has two predecessors (entry and the pad), so it cannot merge into entry.
        Assert(f.Blocks.Contains(after), "after merged despite the pad's edge");
    }

    private static void LoopsSurvive()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32);
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        Block spin = f.NewBlock("spin");
        b.Jump(body);
        b.SetBlock(body);
        VReg n = b.Load(IrType.I32, f.Params[0]);
        b.Store(f.Params[0], b.Binary(Opcode.Sub, n, 1));
        b.Branch(n, body, exit);
        b.SetBlock(exit);
        b.Branch(f.Params[0], spin, exit);
        b.SetBlock(spin);
        b.Jump(spin);
        RunAll(f);
        Assert(f.Blocks.Count == 4, $"expected 4 blocks, have {f.Blocks.Count}");
        Assert(ReferenceEquals(f.Entry, f.Blocks[0]) && f.Entry.Label == "entry0", "entry not first");
        ExpectContains(f, "jump ->spin3");
    }

    // ---- Peephole ------------------------------------------------------

    private static void Algebra()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32, IrType.I64);
        VReg p = f.Params[0];
        VReg q = f.Params[1];
        Sink(b, b.Binary(Opcode.Add, p, 0));
        Sink(b, b.Binary(Opcode.Sub, p, 0));
        Sink(b, b.Binary(Opcode.Mul, p, 1));
        Sink(b, b.Binary(Opcode.Mul, p, 0));
        Sink(b, b.Binary(Opcode.And, p, 0));
        Sink(b, b.Binary(Opcode.Or, p, 0));
        Sink(b, b.Binary(Opcode.Xor, p, 0));
        Sink(b, b.Binary(Opcode.Shl, new RegOperand(q), new ImmOperand(64, IrType.I32), IrType.I64));
        Sink(b, b.Binary(Opcode.Sub, p, p));
        Sink(b, b.Binary(Opcode.And, p, -1));
        Sink(b, b.Binary(Opcode.DivS, p, 2));                   // not an uncorrected shift
        b.Ret();
        // Keep the peephole contract separate from the later signed-bias pass.
        Run(f, new Peephole(), new ConstantAndCopyPropagation(), new DeadCodeElimination());
        Expect(f, string.Join("\n",
            "store.u4 @sink %0.p0",
            "store.u4 @sink %0.p0",
            "store.u4 @sink %0.p0",
            "store.u4 @sink 0",
            "store.u4 @sink 0",
            "store.u4 @sink %0.p0",
            "store.u4 @sink %0.p0",
            "store.u8 @sink %1.p1",
            "store.u4 @sink 0",
            "store.u4 @sink %0.p0",
            "%12 = divs %0.p0 2",
            "store.u4 @sink %12",
            "ret"));
        RunAll(f);
        ExpectMissing(f, "divs");
    }

    private static void PowersOfTwo()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32, IrType.I64);
        VReg p = f.Params[0];
        VReg q = f.Params[1];
        Sink(b, b.Binary(Opcode.Mul, p, 8));
        Sink(b, b.Binary(Opcode.Mul, new ImmOperand(4, IrType.I64), new RegOperand(q), IrType.I64));
        Sink(b, b.Binary(Opcode.DivU, p, 4));
        Sink(b, b.Binary(Opcode.RemU, p, 16));
        Sink(b, b.Binary(Opcode.RemS, p, 16));                  // this peephole leaves signed remainder alone
        Sink(b, b.Binary(Opcode.Mul, p, 6));                    // not a power: stays
        b.Ret();
        Run(f, new Peephole(), new ConstantAndCopyPropagation(), new DeadCodeElimination());
        ExpectContains(f, "%2 = shl %0.p0 3");
        ExpectContains(f, "%3 = shl %1.p1 2");
        ExpectContains(f, "%4 = shru %0.p0 2");
        ExpectContains(f, "%5 = and %0.p0 15");
        ExpectContains(f, "%6 = rems %0.p0 16");
        ExpectContains(f, "%7 = mul %0.p0 6");
        RunAll(f);
        ExpectMissing(f, "rems");
    }

    private static void CompareOfCompare()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32, IrType.I32, IrType.F32);
        VReg p = f.Params[0];
        VReg q = f.Params[1];
        VReg lt = b.Binary(Opcode.LtS, p, q);
        Sink(b, b.Binary(Opcode.Ne, lt, 0));                     // -> lt itself
        Sink(b, b.Binary(Opcode.Eq, lt, 0));                     // -> ges p q
        Sink(b, b.Binary(Opcode.Eq, new ImmOperand(0, IrType.I32), new RegOperand(lt), IrType.I32));
        VReg flt = b.Binary(Opcode.FLt, f.Params[2], f.Params[2]);
        Sink(b, b.Binary(Opcode.Eq, flt, 0));                    // -> xor flt 1, never fge
        b.Ret();
        // Inspect both operand-order rewrites before later CSE merges their
        // identical results. Keep the original peephole assertions intact.
        Run(f, new Peephole(), new ConstantAndCopyPropagation(), new DeadCodeElimination());
        ExpectContains(f, "%3 = lts %0.p0 %1.p1");
        ExpectContains(f, "store.u4 @sink %3");
        ExpectContains(f, "%5 = ges %0.p0 %1.p1");
        ExpectContains(f, "%6 = ges %0.p0 %1.p1");
        ExpectContains(f, "%7 = flt %2.p2 %2.p2");
        ExpectContains(f, "%8 = xor %7 1");
        RunAll(f);
        ExpectMissing(f, "%6 = ges");
        Assert(Body(f).Split('\n').Count(line => line == "store.u4 @sink %5") == 2,
            "full pipeline reuses the inverse comparison for both sinks");
    }

    /// <summary>
    /// H: t = lt a b; a = load p; c = eq t 0; ... -- `a` moves between the
    /// compare and the test, so the test cannot become `ge a b` and falls
    /// back to `t xor 1`.
    /// </summary>
    private static void CompareInversionRefused()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32);
        VReg a = b.Reg(IrType.I32, "a");
        b.CopyTo(a, new RegOperand(b.Load(IrType.I32, f.Params[0])));
        Block h = f.NewBlock("H");
        Block x = f.NewBlock("X");
        b.Jump(h);
        b.SetBlock(h);
        VReg t = b.Binary(Opcode.LtS, a, f.Params[0]);
        b.CopyTo(a, new RegOperand(b.Load(IrType.I32, f.Params[0], 4)));
        VReg c = b.Binary(Opcode.Eq, t, 0);
        b.Store(f.Params[0], c);
        b.Branch(c, h, x);
        b.SetBlock(x);
        b.Ret();
        Run(f, new Peephole());
        ExpectContains(f, "%5 = xor %3 1");
    }

    // ---- Verifier, liveness, cfg, pipeline -----------------------------

    private static void VerifierCatches()
    {
        (Function f, Builder b) = Fn(IrType.I32);
        b.Emit(Opcode.Add, f.NewReg(IrType.I32), new ImmOperand(1, IrType.I32), new ImmOperand(1, IrType.I32));
        try
        {
            Verifier.Check(f, "test");
            throw new Exception("missing terminator not reported");
        }
        catch (IrVerifyException e)
        {
            Assert(e.Message.Contains("terminator"), e.Message);
        }

        b.Ret(new ImmOperand(0, IrType.I32));
        Verifier.Check(f, "test");

        b.Block.Instrs.Insert(0, new Instr
        {
            Op = Opcode.Add, Dest = f.NewReg(IrType.I64),
            Operands = { new ImmOperand(1, IrType.I32), new ImmOperand(1, IrType.I32) },
        });
        try
        {
            Verifier.Check(f, "test");
            throw new Exception("bad dest type not reported");
        }
        catch (IrVerifyException e)
        {
            Assert(e.Message.Contains("destination is I64"), e.Message);
        }

        b.Block.Instrs.RemoveAt(0);
        b.Block.Instrs.Insert(0, new Instr { Op = Opcode.Branch, Operands = { new ImmOperand(1, IrType.I32) } });
        try
        {
            Verifier.Check(f, "test");
            throw new Exception("terminator in the middle not reported");
        }
        catch (IrVerifyException e)
        {
            Assert(e.Message.Contains("before its end"), e.Message);
        }
    }

    private static void LivenessLoop()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        VReg i = b.Reg(IrType.I32, "i");
        VReg acc = b.Reg(IrType.I32, "acc");
        b.CopyTo(i, new ImmOperand(0, IrType.I32));
        b.CopyTo(acc, new ImmOperand(0, IrType.I32));
        b.Jump(body);
        b.SetBlock(body);
        VReg tmp = b.Binary(Opcode.Add, acc, i);
        b.CopyTo(acc, new RegOperand(tmp));
        b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(exit);
        b.Ret(new RegOperand(acc));

        Liveness live = new(f);
        Assert(live.IsLiveIn(body, i) && live.IsLiveIn(body, acc) && live.IsLiveIn(body, f.Params[0]), "loop inputs live-in");
        Assert(!live.IsLiveIn(body, tmp), "tmp is not live-in to body");
        Assert(live.IsLiveOut(body, acc) && live.IsLiveOut(body, i), "loop-carried live-out");
        Assert(live.IsLiveIn(exit, acc) && !live.IsLiveIn(exit, i), "exit needs acc only");
        Assert(!live.IsLiveIn(f.Entry, i), "i is defined before use in entry");
        Assert(live.LiveOut(exit).Count() == 0, "nothing live after ret");
        Assert(live.LiveIn(body).Count() == 3, "body live-in count");
    }

    private static void CfgQueries()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32);
        Block l = f.NewBlock("L");
        Block r = f.NewBlock("R");
        Block j = f.NewBlock("J");
        Block dead = f.NewBlock("dead");
        b.Branch(f.Params[0], l, r);
        b.SetBlock(l);
        b.Jump(j);
        b.SetBlock(r);
        b.Jump(j);
        b.SetBlock(j);
        b.Branch(f.Params[0], l, j);
        b.SetBlock(dead);
        b.Jump(j);
        b.Ret();

        Cfg cfg = new(f);
        Assert(cfg.Dominates(f.Entry, j) && cfg.Dominates(j, j), "entry dominates j");
        Assert(!cfg.Dominates(l, j) && !cfg.Dominates(r, j), "neither arm dominates the join");
        Assert(cfg.Dominates(dead, dead), "self");
        Assert(cfg.InCycle(j) && cfg.InCycle(l) && !cfg.InCycle(r) && !cfg.InCycle(f.Entry), "cycles");
        Assert(cfg.ReachesWithoutReentering(l, r) == false && cfg.ReachesWithoutReentering(j, l), "reachability");
        Assert(cfg.Preds(j).Count == 4 && cfg.Preds(j).Contains(dead) && cfg.Preds(j).Contains(j), "preds include the unreachable block and the self-edge");
        List<Block> rpo = cfg.ReversePostorder.ToList();
        Assert(ReferenceEquals(rpo[0], f.Entry) && !rpo.Contains(dead) && rpo.Count == 4, "rpo");
        Assert(rpo.IndexOf(j) > rpo.IndexOf(l) && rpo.IndexOf(j) > rpo.IndexOf(r), "join after both arms");
        Assert(Cfg.RemoveUnreachable(f) && !f.Blocks.Contains(dead), "dead block removed");
    }

    private static void PipelineTrace()
    {
        Module m = new("m");
        (Function f, Builder b) = Fn(IrType.I32);
        b.Ret(new RegOperand(b.Binary(Opcode.Add, b.Const(1, IrType.I32), 1)));
        m.Functions.Add(f);
        List<string> names = new();
        Pipeline p = Pipeline.Default(rounds: 2);
        p.Verify = true;
        p.Trace = (pass, fn, dump) =>
        {
            names.Add(pass.Name);
            Assert(dump.StartsWith("function t("), "dump is the function's");
        };
        p.Run(m);
        // The module runner may sweep the function passes more than once
        // (before and after the module passes); each sweep is whole rounds.
        Assert(names.Count >= 2 * p.Passes.Count && names.Count % p.Passes.Count == 0,
            $"trace fired {names.Count} times for {p.Passes.Count} passes over two rounds");
        Assert(names[0] == "fold" && names[p.Passes.Count - 1] == "branches", "pass order");
        Expect(f, "ret 2");
    }

    // ---- SSA -----------------------------------------------------------

    /// <summary>acc = 0; for (i = 0; i &lt; n; i++) acc += i; return acc.</summary>
    private static Function SumLoop()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        VReg i = b.Reg(IrType.I32, "i");
        VReg acc = b.Reg(IrType.I32, "acc");
        b.CopyTo(i, new ImmOperand(0, IrType.I32));
        b.CopyTo(acc, new ImmOperand(0, IrType.I32));
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(body);
        b.CopyTo(acc, new RegOperand(b.Binary(Opcode.Add, acc, i)));
        b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        b.Ret(new RegOperand(acc));
        return f;
    }

    /// <summary>a = 0; b = 1; for n times: (a, b) = (b, a + b); return a.</summary>
    private static Function Fib()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        VReg i = b.Reg(IrType.I32, "i");
        VReg x = b.Reg(IrType.I32, "a");
        VReg y = b.Reg(IrType.I32, "b");
        b.CopyTo(i, new ImmOperand(0, IrType.I32));
        b.CopyTo(x, new ImmOperand(0, IrType.I32));
        b.CopyTo(y, new ImmOperand(1, IrType.I32));
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(body);
        VReg t = b.Binary(Opcode.Add, x, y);
        b.CopyTo(x, new RegOperand(y));
        b.CopyTo(y, new RegOperand(t));
        b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        VReg result = b.Copy(x);
        b.Ret(new RegOperand(b.Binary(Opcode.And, result, 255)));
        return f;
    }

    /// <summary>Swaps two registers each trip: the parallel copy cycle.</summary>
    private static Function Swap()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block exit = f.NewBlock("exit");
        VReg i = b.Reg(IrType.I32, "i");
        VReg x = b.Reg(IrType.I32, "x");
        VReg y = b.Reg(IrType.I32, "y");
        b.CopyTo(i, new ImmOperand(0, IrType.I32));
        b.CopyTo(x, new ImmOperand(3, IrType.I32));
        b.CopyTo(y, new ImmOperand(7, IrType.I32));
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(body);
        VReg t = b.Copy(x);
        b.CopyTo(x, new RegOperand(y));
        b.CopyTo(y, new RegOperand(t));
        b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        b.Ret(new RegOperand(b.Binary(Opcode.Sub, x, y)));
        return f;
    }

    /// <summary>A switch inside a loop assigning one register in several arms, and a store to memory.</summary>
    private static Function SwitchLoop()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block head = f.NewBlock("head");
        Block body = f.NewBlock("body");
        Block c0 = f.NewBlock("c0");
        Block c1 = f.NewBlock("c1");
        Block dflt = f.NewBlock("d");
        Block join = f.NewBlock("join");
        Block exit = f.NewBlock("exit");
        VReg i = b.Reg(IrType.I32, "i");
        VReg v = b.Reg(IrType.I32, "v");
        b.CopyTo(i, new ImmOperand(0, IrType.I32));
        b.CopyTo(v, new ImmOperand(0, IrType.I32));
        b.Jump(head);
        b.SetBlock(head);
        b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
        b.SetBlock(body);
        b.Switch(new RegOperand(b.Binary(Opcode.RemS, i, 3)), new[] { c0, c1 }, dflt);
        b.SetBlock(c0);
        b.CopyTo(v, new RegOperand(b.Binary(Opcode.Add, v, 10)));
        b.Jump(join);
        b.SetBlock(c1);
        b.CopyTo(v, new RegOperand(b.Binary(Opcode.Mul, v, 2)));
        b.Jump(join);
        b.SetBlock(dflt);
        b.Jump(join);
        b.SetBlock(join);
        b.Store(new SymOperand("g"), new RegOperand(v));
        b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
        b.Jump(head);
        b.SetBlock(exit);
        b.Ret(new RegOperand(b.Binary(Opcode.Add, v, b.Load(IrType.I32, new SymOperand("g")))));
        return f;
    }

    private static readonly long[][] Inputs = { new long[] { 0 }, new long[] { 1 }, new long[] { 2 }, new long[] { 5 }, new long[] { 20 } };

    private static void SsaLoop()
    {
        Function f = SumLoop();
        Run(f, new Ssa());
        Assert(AllSingleDef(f), "every register defined once:\n" + Dump(f));
        Block head = f.Blocks[1];
        Assert(Phi.Of(head).Count() == 2, "two phis in the header:\n" + Dump(f));
        Assert(PhiCount(f) == 2, "phis only in the header:\n" + Dump(f));
        foreach (Instr phi in Phi.Of(head))
        {
            Assert(phi.Operands.Count == 2 && phi.Targets.Count == 2, "phi has an entry per predecessor");
        }
        Assert(new Interp().Run(f, 5) == 10, "sum of 0..4 in SSA form");
        Run(f, new OutOfSsa());
        Assert(PhiCount(f) == 0, "no phis after out-of-SSA");
        Assert(new Interp().Run(f, 5) == 10, "sum of 0..4 after out-of-SSA");
    }

    /// <summary>L: use v (previous trip); v = load; branch L/exit -- one static definition, but a loop-carried read.</summary>
    private static void SsaLoopCarriedSingleDef()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            Block l = f.NewBlock("L");
            Block exit = f.NewBlock("X");
            VReg v = b.Reg(IrType.I32, "v");
            VReg i = b.Reg(IrType.I32, "i");
            b.CopyTo(v, new ImmOperand(0, IrType.I32));
            b.CopyTo(i, new ImmOperand(0, IrType.I32));
            b.Jump(l);
            b.SetBlock(l);
            Sink(b, v);                                              // reads the previous trip's v
            b.CopyTo(v, new RegOperand(b.Binary(Opcode.Add, i, 100)));
            b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
            b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), l, exit);
            b.SetBlock(exit);
            b.Ret(new RegOperand(b.Load(IrType.I32, new SymOperand("sink"))));
            return f;
        }
        SameResults(Build, Inputs, new Ssa());
        SameResults(Build, Inputs, new Ssa(), new OutOfSsa());
        SameResults(Build, Inputs, new SsaOptimise());
        Function f = Build();
        Run(f, new Ssa());
        Assert(Phi.Of(f.Blocks[1]).Any(p => p.Dest!.Name == "v"), "v has a header phi:\n" + Dump(f));
    }

    private static void SsaRoundTrip()
    {
        foreach (Func<Function> build in new Func<Function>[] { SumLoop, Fib, Swap, SwitchLoop })
        {
            SameResults(build, Inputs, new Ssa());
            SameResults(build, Inputs, new Ssa(), new OutOfSsa());
            SameResults(build, Inputs, new SsaOptimise());
            SameResults(build, Inputs, new SsaOptimise(), new SsaOptimise());
        }
        // The swap cycle needs a temporary and nothing else.
        Function s = Swap();
        Run(s, new Ssa(), new OutOfSsa());
        Assert(new Interp().Run(s, 3) == 4, "swap three times: x=7, y=3");
    }

    /// <summary>
    /// head: branch -> body, exit; body: ... branch -> head, exit. The edge
    /// body -> exit is critical (body has two successors, exit two
    /// predecessors) and exit has a phi; the copies need their own block,
    /// and the whole thing must still compute the same.
    /// </summary>
    private static void SsaCriticalEdge()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            Block head = f.NewBlock("head");
            Block body = f.NewBlock("body");
            Block exit = f.NewBlock("exit");
            VReg i = b.Reg(IrType.I32, "i");
            VReg r = b.Reg(IrType.I32, "r");
            b.CopyTo(i, new ImmOperand(0, IrType.I32));
            b.CopyTo(r, new ImmOperand(-1, IrType.I32));
            b.Jump(head);
            b.SetBlock(head);
            b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), body, exit);
            b.SetBlock(body);
            b.CopyTo(r, new RegOperand(b.Binary(Opcode.Mul, i, 3)));
            b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
            b.Branch(b.Binary(Opcode.Eq, r, 6), exit, head);
            b.SetBlock(exit);
            b.Ret(new RegOperand(r));
            return f;
        }
        SameResults(Build, Inputs, new Ssa(), new OutOfSsa());
        Function f = Build();
        Run(f, new Ssa());
        Assert(Phi.Of(f.Blocks[3]).Any(), "exit has a phi:\n" + Dump(f));
        Run(f, new OutOfSsa());
        Assert(f.Blocks.Any(x => x.Label.StartsWith("phi")), "edge was split:\n" + Dump(f));
        Run(f, new BranchSimplify());
        // Both exits and the header keep two predecessors, so the three
        // split blocks are all genuinely needed.
        Assert(f.Blocks.Count == 7, "three split blocks remain:\n" + Dump(f));
        SameResults(Build, Inputs, new Ssa(), new OutOfSsa(), new BranchSimplify());
    }

    private static void SsaLandingPad()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        Block pad = f.NewBlock("pad");
        Block after = f.NewBlock("after");
        pad.IsLandingPad = true;
        VReg x = b.Reg(IrType.I32, "x");
        b.CopyTo(x, new ImmOperand(1, IrType.I32));
        b.CopyTo(x, new ImmOperand(2, IrType.I32));
        b.Jump(after);
        b.SetBlock(pad);
        VReg e = b.Call("__exception", IrType.I32)!;
        b.CopyTo(x, new RegOperand(e));                          // x in the pad
        Sink(b, b.Binary(Opcode.Add, x, f.Params[0]));           // uses the pad's x and the parameter
        b.Jump(after);
        b.SetBlock(after);
        b.Ret(new RegOperand(x));
        Run(f, new Ssa());
        Assert(AllSingleDef(f), "single defs:\n" + Dump(f));
        Assert(Phi.Of(after).Count() == 1, "after joins the entry's x and the pad's x:\n" + Dump(f));
        Instr add = pad.Instrs.First(i => i.Op == Opcode.Add);
        Assert(add.Operands[1] is RegOperand p && ReferenceEquals(p.Reg, f.Params[0]), "parameter keeps its name in the pad:\n" + Dump(f));
        Run(f, new OutOfSsa());
        Assert(PhiCount(f) == 0, "no phis remain");
        Verifier.Check(f, "pad round trip");
    }

    private static void SsaOptimiseFib()
    {
        SameResults(Fib, Inputs, new SsaOptimise());
        Function f = Fib();
        Run(f, new SsaOptimise());
        // The `result = copy a` before the mask is gone: the mask reads a's phi directly.
        ExpectMissing(f, ".result = copy");
        Assert(PhiCount(f) == 0, "no phis reach the backend");
        Assert(new Interp().Run(f, 20) == 109, "fib(20) & 255");
    }

    /// <summary>
    /// In SSA form: entry branches to A or B; A is a jump-only block to J;
    /// J has a phi (from A: 1, from B: 2). Threading A away must re-key the
    /// phi to entry, and merging must turn a single-entry phi into a copy.
    /// </summary>
    private static void SsaBranchSimplify()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            Block a = f.NewBlock("A");
            Block c = f.NewBlock("B");
            Block j = f.NewBlock("J");
            VReg r = b.Reg(IrType.I32, "r");
            b.Branch(f.Params[0], a, c);
            b.SetBlock(a);
            b.Jump(j);
            b.SetBlock(c);
            b.Jump(j);
            b.SetBlock(j);
            b.CopyTo(r, new ImmOperand(0, IrType.I32));
            b.Ret(new RegOperand(r));
            return f;
        }
        Function f = Build();
        Block jb = f.Blocks[3];
        VReg d = f.NewReg(IrType.I32, "phi");
        jb.Instrs.Insert(0, new Instr
        {
            Op = Opcode.Phi, Dest = d,
            Operands = { new ImmOperand(1, IrType.I32), new ImmOperand(2, IrType.I32) },
            Targets = { f.Blocks[1], f.Blocks[2] },
        });
        jb.Instrs[^1].Operands[0] = new RegOperand(d);
        Verifier.Check(f, "built");
        Assert(new Interp().Run(f, 1) == 1 && new Interp().Run(f, 0) == 2, "phi picks by path");
        Run(f, new BranchSimplify());
        Assert(new Interp().Run(f, 1) == 1 && new Interp().Run(f, 0) == 2, "still picks by path:\n" + Dump(f));
        Instr phi = Phi.Of(f.Blocks.Last()).Single();
        Assert(phi.Targets.Contains(f.Entry), "phi re-keyed to entry:\n" + Dump(f));
        Run(f, new OutOfSsa(), new BranchSimplify());
        Assert(PhiCount(f) == 0 && new Interp().Run(f, 1) == 1 && new Interp().Run(f, 0) == 2, "after out-of-SSA:\n" + Dump(f));
    }

    // ---- SCCP ----------------------------------------------------------

    /// <summary>
    /// flag = 1; if (flag) x = 5 else x = 7; y = x * 2; return y + p.
    /// Plain folding sees a two-way phi; SCCP sees only the taken arm.
    /// </summary>
    private static void SccpPhi()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            Block t = f.NewBlock("t");
            Block e = f.NewBlock("e");
            Block j = f.NewBlock("j");
            VReg x = b.Reg(IrType.I32, "x");
            VReg flag = b.Const(1, IrType.I32);
            b.Branch(flag, t, e);
            b.SetBlock(t);
            b.CopyTo(x, new ImmOperand(5, IrType.I32));
            b.Jump(j);
            b.SetBlock(e);
            b.CopyTo(x, new ImmOperand(7, IrType.I32));
            b.Jump(j);
            b.SetBlock(j);
            VReg y = b.Binary(Opcode.Mul, x, 2);
            b.Ret(new RegOperand(b.Binary(Opcode.Add, y, f.Params[0])));
            return f;
        }
        SameResults(Build, Inputs, new SsaOptimise());
        Function f = Build();
        Run(f, new SsaOptimise());
        Expect(f, "%8 = add 10 %0.p0\nret %8");
    }

    /// <summary>
    /// k = 3; i = 0; loop: k = k (a phi of 3 and itself); i = i + 1; until i == n.
    /// k folds to 3 through its own phi; i must stay a real induction variable.
    /// </summary>
    private static void SccpLoop()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            Block head = f.NewBlock("head");
            Block exit = f.NewBlock("exit");
            VReg i = b.Reg(IrType.I32, "i");
            VReg k = b.Reg(IrType.I32, "k");
            b.CopyTo(i, new ImmOperand(0, IrType.I32));
            b.CopyTo(k, new ImmOperand(3, IrType.I32));
            b.Jump(head);
            b.SetBlock(head);
            b.CopyTo(k, new RegOperand(b.Binary(Opcode.Add, k, 0)));
            b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
            b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), head, exit);
            b.SetBlock(exit);
            b.Ret(new RegOperand(b.Binary(Opcode.Mul, k, i)));
            return f;
        }
        SameResults(Build, Inputs, new SsaOptimise());
        Function f = Build();
        Run(f, new SsaOptimise());
        ExpectMissing(f, ".k");
        ExpectContains(f, "ret %16");
        Assert(f.Blocks.SelectMany(x => x.Instrs).Any(i => i.Op == Opcode.Mul && i.Operands[0] is ImmOperand { Value: 3 }), "k is 3 in the multiply:\n" + Dump(f));
    }

    // ---- GVN -----------------------------------------------------------

    private static Function ExprTwice()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32, IrType.I32);
        VReg p = f.Params[0];
        VReg q = f.Params[1];
        VReg s1 = b.Binary(Opcode.Add, p, q);
        VReg s2 = b.Binary(Opcode.Add, q, p);
        VReg m1 = b.Binary(Opcode.Mul, s1, 3);
        VReg m2 = b.Binary(Opcode.Mul, s2, 3);
        VReg d = b.Binary(Opcode.Sub, p, q);                    // not commutative: sub q p differs
        VReg d2 = b.Binary(Opcode.Sub, q, p);
        b.Ret(new RegOperand(b.Binary(Opcode.Add, b.Binary(Opcode.Add, m1, m2), b.Binary(Opcode.Xor, d, d2))));
        return f;
    }

    private static readonly long[][] Inputs2 = { new long[] { 0, 0 }, new long[] { 3, 5 }, new long[] { -7, 2 }, new long[] { 100, -100 } };

    private static void GvnExpressions()
    {
        SameResults(ExprTwice, Inputs2, new SsaOptimise());
        Function f = ExprTwice();
        Run(f, new SsaOptimise());
        Assert(f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Add) == 3, "one add of p and q, plus the two sums:\n" + Dump(f));
        Assert(f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Mul) == 1, "one multiply:\n" + Dump(f));
        Assert(f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Sub) == 2, "both subtractions stay:\n" + Dump(f));
    }

    private static Function LoadsAfterStore()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        SymOperand g = new("g");
        SymOperand h = new("h");
        FrameSlot slot = f.NewSlot(8, 4, "s");
        SlotOperand sl = new(slot);
        b.Store(g, new RegOperand(f.Params[0]));
        VReg a = b.Load(IrType.I32, g);                            // forwarded: p
        b.Store(h, new ImmOperand(9, IrType.I32));
        VReg c = b.Load(IrType.I32, g);                            // still p: h is distinct
        VReg d = b.Load(IrType.I32, h);                            // 9
        VReg e = b.Load(IrType.I32, sl, 4);                        // unknown, first load
        VReg e2 = b.Load(IrType.I32, sl, 4);                       // same as e
        b.Store(sl, new ImmOperand(1, IrType.I32), 0);             // slot+0 does not overlap slot+4
        VReg e3 = b.Load(IrType.I32, sl, 4);                       // still e
        VReg n = b.Load(IrType.I32, g, 0, 1, true);                // narrow: not forwarded from the 4-byte store
        b.Ret(new RegOperand(b.Binary(Opcode.Add, b.Binary(Opcode.Add, b.Binary(Opcode.Add, a, c), b.Binary(Opcode.Add, d, e)),
            b.Binary(Opcode.Add, b.Binary(Opcode.Add, e2, e3), n))));
        return f;
    }

    private static void GvnLoads()
    {
        SameResults(LoadsAfterStore, Inputs, new SsaOptimise());
        Function f = LoadsAfterStore();
        Run(f, new SsaOptimise());
        List<Instr> loads = f.Blocks[0].Instrs.Where(i => i.Op == Opcode.Load).ToList();
        Assert(loads.Count == 2, "only the first slot load and the narrow load remain:\n" + Dump(f));
        Assert(loads.Any(l => l.Size == 1), "the narrow load stays:\n" + Dump(f));
        Assert(loads.Any(l => l.Operands[0] is SlotOperand && l.Offset == 4), "the slot load stays:\n" + Dump(f));
    }

    private static Function Barriers()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        SymOperand g = new("g");
        VReg a = b.Load(IrType.I32, g);
        b.Call("touch", IrType.Void);
        VReg c = b.Load(IrType.I32, g);                            // after a call: reloaded
        VReg addr = b.Address("g");
        b.Store(new RegOperand(addr), new RegOperand(f.Params[0]));                // through a register: could be g
        VReg d = b.Load(IrType.I32, g);                            // reloaded
        b.Store(g, new ImmOperand(4, IrType.I32));
        b.Emit(Opcode.Fence, null);
        VReg e = b.Load(IrType.I32, g);                            // fence: reloaded
        b.Ret(new RegOperand(b.Binary(Opcode.Add, b.Binary(Opcode.Add, a, c), b.Binary(Opcode.Add, d, e))));
        return f;
    }

    private static void GvnBarriers()
    {
        Function f = Barriers();
        Run(f, new SsaOptimise());
        Assert(f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Load) == 4, "every load stays:\n" + Dump(f));
        Interp before = new();
        before.Calls["touch"] = _ => 0;
        Interp after = new();
        after.Calls["touch"] = _ => 0;
        Assert(before.Run(Barriers(), 5) == after.Run(f, 5), "same result");
    }

    /// <summary>
    /// store g = p; if (p) { x = load g } else { x = load g }; loop { y = load g; store g = y+1 } -- the
    /// arm loads see the store; the loop header's load does not.
    /// </summary>
    private static void GvnScopes()
    {
        Function Build()
        {
            (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
            SymOperand g = new("g");
            Block t = f.NewBlock("t");
            Block e = f.NewBlock("e");
            Block head = f.NewBlock("head");
            Block exit = f.NewBlock("exit");
            VReg x = b.Reg(IrType.I32, "x");
            VReg i = b.Reg(IrType.I32, "i");
            b.Store(g, new RegOperand(f.Params[0]));
            b.CopyTo(i, new ImmOperand(0, IrType.I32));
            b.Branch(f.Params[0], t, e);
            b.SetBlock(t);
            b.CopyTo(x, new RegOperand(b.Binary(Opcode.Add, b.Load(IrType.I32, g), 1)));
            b.Jump(head);
            b.SetBlock(e);
            b.CopyTo(x, new RegOperand(b.Binary(Opcode.Add, b.Load(IrType.I32, g), 2)));
            b.Jump(head);
            b.SetBlock(head);
            VReg y = b.Load(IrType.I32, g);
            b.Store(g, new RegOperand(b.Binary(Opcode.Add, y, 1)));
            b.CopyTo(i, new RegOperand(b.Binary(Opcode.Add, i, 1)));
            b.Branch(b.Binary(Opcode.LtS, i, f.Params[0]), head, exit);
            b.SetBlock(exit);
            b.Ret(new RegOperand(b.Binary(Opcode.Add, x, b.Load(IrType.I32, g))));
            return f;
        }
        SameResults(Build, Inputs, new SsaOptimise());
        Function f = Build();
        Run(f, new SsaOptimise());
        Assert(!f.Blocks[1].Instrs.Any(i => i.Op == Opcode.Load) && !f.Blocks[2].Instrs.Any(i => i.Op == Opcode.Load), "arm loads forwarded:\n" + Dump(f));
        Assert(f.Blocks.First(x => x.Label.StartsWith("head")).Instrs.Any(i => i.Op == Opcode.Load), "header load stays:\n" + Dump(f));
    }

    // ---- DSE -----------------------------------------------------------

    private static void DseBasic()
    {
        (Function f, Builder b) = Fn(IrType.I32, IrType.I32);
        SymOperand g = new("g");
        FrameSlot slot = f.NewSlot(8, 4, "s");
        SlotOperand sl = new(slot);
        b.Store(g, new ImmOperand(1, IrType.I32));               // overwritten below: dead
        b.Store(g, new RegOperand(f.Params[0]));                 // read by the ret: stays
        b.Store(sl, new ImmOperand(2, IrType.I32), 0);           // never read before ret: dead
        b.Store(sl, new ImmOperand(3, IrType.I32), 4);           // read below: stays
        VReg x = b.Load(IrType.I32, sl, 4);
        b.Store(sl, new ImmOperand(4, IrType.I32), 4);           // after the last read: dead
        b.Store(new SymOperand("h"), new ImmOperand(5, IrType.I32), 0, 2);   // 2 bytes, covered by the 4 below
        b.Store(new SymOperand("h"), new ImmOperand(6, IrType.I32));
        b.Ret(new RegOperand(b.Binary(Opcode.Add, x, b.Load(IrType.I32, g))));
        Interp before = new();
        long r0 = before.Run(f, 7);
        Run(f, new Dse());
        List<Instr> stores = f.Blocks[0].Instrs.Where(i => i.Op == Opcode.Store).ToList();
        Assert(stores.Count == 3, "three stores remain:\n" + Dump(f));
        Assert(stores.Any(s => s.Operands[0] is SymOperand { Name: "g" } && s.Operands[1] is RegOperand), "g = p stays");
        Assert(stores.Any(s => s.Operands[0] is SlotOperand && s.Offset == 4 && s.Operands[1] is ImmOperand { Value: 3 }), "slot+4 = 3 stays");
        Assert(stores.Any(s => s.Operands[0] is SymOperand { Name: "h" } && s.Operands[1] is ImmOperand { Value: 6 }), "h = 6 stays");
        Interp after = new();
        Assert(after.Run(f, 7) == r0 && after.ReadSymbol("g") == 7 && after.ReadSymbol("h") == 6, "same observable result");
    }

    private static void DseKept()
    {
        (Function f, Builder b) = Fn(IrType.Void, IrType.I32);
        SymOperand g = new("g");
        FrameSlot slot = f.NewSlot(8, 4, "s");
        SlotOperand sl = new(slot);
        b.Store(g, new ImmOperand(1, IrType.I32));
        b.Call("read_g", IrType.Void);                           // may read g: the store above stays
        b.Store(g, new ImmOperand(2, IrType.I32));
        VReg addr = b.Address("g");
        b.Store(new RegOperand(addr), new ImmOperand(3, IrType.I32));   // through a register: unknown
        b.Store(g, new ImmOperand(4, IrType.I32));
        b.Load(IrType.I32, g);                                   // reads 4, so the store stays even before another
        b.Store(g, new ImmOperand(5, IrType.I32));
        b.Store(g, new ImmOperand(6, IrType.I32), 2, 2);         // partial overlap: neither covers the other
        b.Store(sl, new ImmOperand(7, IrType.I32));
        b.Call("read_slot", IrType.Void, new SlotOperand(slot));  // the callee may read the slot
        b.Store(sl, new ImmOperand(8, IrType.I32));
        VReg p = b.SlotAddress(slot);
        b.Load(IrType.I32, p);                                   // through a register: the slot is read
        b.Ret();
        int before = f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Store);
        Run(f, new Dse());
        int after = f.Blocks[0].Instrs.Count(i => i.Op == Opcode.Store);
        Assert(before == 8 && after == 8, $"all {before} stores stay, {after} remain:\n" + Dump(f));
    }
}
