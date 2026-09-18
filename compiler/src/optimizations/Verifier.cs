#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>A structural or typing fault in the IR, naming the function and instruction.</summary>
public sealed class IrVerifyException : Exception
{
    public IrVerifyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Checks the invariants Ir.cs states in prose: one terminator per block
/// and it is last, targets belong to the function, operand and result
/// types follow the opcode. Cheap enough to run between every pass in a
/// debug build, which is where it earns its keep: a pass that produces an
/// ill-typed instruction fails here with the instruction named, instead of
/// in instruction selection with a register that has the wrong width.
///
/// It checks shape, not meaning: whether a register is defined before use
/// is not a question this IR can answer without reaching definitions, and
/// the backend tolerates the answer being no on dead paths.
/// </summary>
public sealed class Verifier : IPass
{
    public string Name => "verify";

    public void Run(Function f) => Check(f, "verify");

    public static void Check(Function f, string when)
    {
        if (f.Blocks.Count == 0)
        {
            throw new IrVerifyException($"{f.Name} ({when}): no blocks");
        }
        HashSet<Block> blocks = new(f.Blocks, ReferenceEqualityComparer.Instance);
        if (blocks.Count != f.Blocks.Count)
        {
            throw new IrVerifyException($"{f.Name} ({when}): a block is listed twice");
        }

        foreach (Block b in f.Blocks)
        {
            if (b.Instrs.Count == 0 || !b.Instrs[^1].IsTerminator)
            {
                throw new IrVerifyException($"{f.Name} ({when}): block {b.Label} does not end in a terminator");
            }
            bool pastPhis = false;
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Phi)
                {
                    pastPhis = true;
                }
                else if (pastPhis)
                {
                    throw new IrVerifyException($"{f.Name} ({when}): block {b.Label}: {i}: phi after a non-phi");
                }
            }
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.IsTerminator && k != b.Instrs.Count - 1)
                {
                    throw new IrVerifyException($"{f.Name} ({when}): block {b.Label} has a terminator before its end: {i}");
                }
                try
                {
                    CheckInstr(f, i, blocks);
                }
                catch (IrVerifyException e)
                {
                    throw new IrVerifyException($"{f.Name} ({when}): block {b.Label}: {i}: {e.Message}");
                }
            }
        }

        CheckPhis(f, when);
    }

    /// <summary>
    /// A phi names each predecessor exactly once and nothing else: that is
    /// what keeps the passes that redirect edges honest.
    /// </summary>
    private static void CheckPhis(Function f, string when)
    {
        Cfg? cfg = null;
        foreach (Block b in f.Blocks)
        {
            foreach (Instr phi in Phi.Of(b))
            {
                cfg ??= new Cfg(f);
                string where = $"{f.Name} ({when}): block {b.Label}: {phi}";
                if (phi.Dest is null)
                {
                    throw new IrVerifyException($"{where}: phi without a destination");
                }
                if (phi.Operands.Count != phi.Targets.Count)
                {
                    throw new IrVerifyException($"{where}: {phi.Operands.Count} operands for {phi.Targets.Count} predecessors");
                }
                IReadOnlyList<Block> preds = cfg.Preds(b);
                if (phi.Targets.Count != preds.Count)
                {
                    throw new IrVerifyException($"{where}: names {phi.Targets.Count} predecessors, block has {preds.Count}");
                }
                HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
                for (int k = 0; k < phi.Targets.Count; k++)
                {
                    if (!seen.Add(phi.Targets[k]))
                    {
                        throw new IrVerifyException($"{where}: predecessor {phi.Targets[k].Label} named twice");
                    }
                    if (!preds.Contains(phi.Targets[k]))
                    {
                        throw new IrVerifyException($"{where}: {phi.Targets[k].Label} is not a predecessor");
                    }
                    if (phi.Operands[k].Type != phi.Dest.Type)
                    {
                        throw new IrVerifyException($"{where}: operand {k} is {phi.Operands[k].Type}, destination is {phi.Dest.Type}");
                    }
                }
            }
        }
    }

    private static void CheckInstr(Function f, Instr i, HashSet<Block> blocks)
    {
        foreach (Block t in i.Targets)
        {
            if (!blocks.Contains(t))
            {
                throw new IrVerifyException($"target {t.Label} is not a block of this function");
            }
        }
        if (i.Default is not null && !blocks.Contains(i.Default))
        {
            throw new IrVerifyException($"default target {i.Default.Label} is not a block of this function");
        }
        if (i.Op is not (Opcode.Jump or Opcode.Branch or Opcode.Switch or Opcode.LabelAddr or Opcode.Phi) && i.Targets.Count > 0)
        {
            throw new IrVerifyException("has targets");
        }
        foreach (Operand o in i.Operands)
        {
            if (o.Type == IrType.Void)
            {
                throw new IrVerifyException("void operand");
            }
        }

        switch (i.Op)
        {
            case Opcode.Copy:
                Operands(i, 1);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.Phi:
                break;      // checked against the graph in CheckPhis

            case Opcode.Add:
            case Opcode.Sub:
            case Opcode.Mul:
            case Opcode.DivS:
            case Opcode.DivU:
            case Opcode.RemS:
            case Opcode.RemU:
            case Opcode.And:
            case Opcode.Or:
            case Opcode.Xor:
                Operands(i, 2);
                Int(i, 0);
                Same(i, 0, 1);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.Shl:
            case Opcode.ShrS:
            case Opcode.ShrU:
                Operands(i, 2);
                Int(i, 0);
                Is(i, 1, IrType.I32);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.Neg:
            case Opcode.Not:
            case Opcode.ByteSwap:
            case Opcode.SExt8:
            case Opcode.SExt16:
            case Opcode.ZExt8:
            case Opcode.ZExt16:
                Operands(i, 1);
                Int(i, 0);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.Eq:
            case Opcode.Ne:
            case Opcode.LtS:
            case Opcode.LeS:
            case Opcode.GtS:
            case Opcode.GeS:
            case Opcode.LtU:
            case Opcode.LeU:
            case Opcode.GtU:
            case Opcode.GeU:
                Operands(i, 2);
                Int(i, 0);
                Same(i, 0, 1);
                Dest(i, IrType.I32);
                break;

            case Opcode.FAdd:
            case Opcode.FSub:
            case Opcode.FMul:
            case Opcode.FDiv:
                Operands(i, 2);
                Float(i, 0);
                Same(i, 0, 1);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.FNeg:
            case Opcode.FSqrt:
                Operands(i, 1);
                Float(i, 0);
                Dest(i, i.Operands[0].Type);
                break;

            case Opcode.FEq:
            case Opcode.FNe:
            case Opcode.FLt:
            case Opcode.FLe:
            case Opcode.FGt:
            case Opcode.FGe:
                Operands(i, 2);
                Float(i, 0);
                Same(i, 0, 1);
                Dest(i, IrType.I32);
                break;

            case Opcode.Trunc64:
                Operands(i, 1);
                Is(i, 0, IrType.I64);
                Dest(i, IrType.I32);
                break;

            case Opcode.SExt32:
            case Opcode.ZExt32:
                Operands(i, 1);
                Is(i, 0, IrType.I32);
                Dest(i, IrType.I64);
                break;

            case Opcode.FConv:
                Operands(i, 1);
                Float(i, 0);
                Dest(i, i.Operands[0].Type == IrType.F32 ? IrType.F64 : IrType.F32);
                break;

            case Opcode.IToF:
            case Opcode.UToF:
                Operands(i, 1);
                Int(i, 0);
                DestFloat(i);
                break;

            case Opcode.FToI:
            case Opcode.FToU:
                Operands(i, 1);
                Float(i, 0);
                DestInt(i);
                break;

            case Opcode.Bits:
                Operands(i, 1);
                Dest(i, i.Operands[0].Type switch
                {
                    IrType.I32 => IrType.F32,
                    IrType.F32 => IrType.I32,
                    IrType.I64 => IrType.F64,
                    _ => IrType.I64,
                });
                break;

            case Opcode.ArrayLength:
                Operands(i, 1);
                Address(i, 0);
                Dest(i, IrType.I32);
                break;

            case Opcode.InitArrayLength:
                Operands(i, 2);
                Address(i, 0);
                Is(i, 1, IrType.I32);
                NoDest(i);
                break;

            case Opcode.Load:
                Operands(i, 1);
                Address(i, 0);
                if (i.Size is not (1 or 2 or 4 or 8))
                {
                    throw new IrVerifyException($"load size {i.Size}");
                }
                if (i.Dest is null || i.Dest.Type == IrType.Void)
                {
                    throw new IrVerifyException("load without a destination");
                }
                if (i.Size > i.Dest.Type.Bytes())
                {
                    throw new IrVerifyException($"load of {i.Size} bytes into {i.Dest.Type}");
                }
                break;

            case Opcode.Store:
                Operands(i, 2);
                Address(i, 0);
                NoDest(i);
                if (i.Size is not (1 or 2 or 4 or 8))
                {
                    throw new IrVerifyException($"store size {i.Size}");
                }
                if (i.Size > i.Operands[1].Type.Bytes())
                {
                    throw new IrVerifyException($"store of {i.Size} bytes from {i.Operands[1].Type}");
                }
                break;

            case Opcode.MemCopy:
                Operands(i, 3);
                Address(i, 0);
                Address(i, 1);
                Int(i, 2);
                NoDest(i);
                break;

            case Opcode.MemSet:
                Operands(i, 3);
                Address(i, 0);
                Is(i, 1, IrType.I32);
                Int(i, 2);
                NoDest(i);
                break;

            case Opcode.AtomicSwap:
            case Opcode.AtomicAdd:
            case Opcode.AtomicAnd:
            case Opcode.AtomicOr:
            case Opcode.AtomicXor:
                Operands(i, 2);
                Address(i, 0);
                Int(i, 1);
                Dest(i, i.Operands[1].Type);
                break;

            case Opcode.AtomicCas:
                Operands(i, 3);
                Address(i, 0);
                Int(i, 1);
                Same(i, 1, 2);
                Dest(i, i.Operands[1].Type);
                break;

            case Opcode.Fence:
            case Opcode.Trap:
            case Opcode.Pause:
            case Opcode.Unreachable:
                Operands(i, 0);
                NoDest(i);
                break;

            case Opcode.Call:
                if (i.Callee is null)
                {
                    throw new IrVerifyException("call without a callee");
                }
                break;

            case Opcode.CallIndirect:
                if (i.Operands.Count < 1)
                {
                    throw new IrVerifyException("indirect call without a target");
                }
                Address(i, 0);
                break;

            case Opcode.Ret:
                NoDest(i);
                if (f.Returns == IrType.Void)
                {
                    Operands(i, 0);
                }
                else
                {
                    Operands(i, 1);
                    Is(i, 0, f.Returns);
                }
                break;

            case Opcode.Jump:
                Operands(i, 0);
                NoDest(i);
                if (i.Targets.Count != 1)
                {
                    throw new IrVerifyException("jump needs exactly one target");
                }
                break;

            case Opcode.Branch:
                Operands(i, 1);
                Is(i, 0, IrType.I32);
                NoDest(i);
                if (i.Targets.Count != 2)
                {
                    throw new IrVerifyException("branch needs exactly two targets");
                }
                break;

            case Opcode.Switch:
                Operands(i, 1);
                Is(i, 0, IrType.I32);
                NoDest(i);
                if (i.Default is null)
                {
                    throw new IrVerifyException("switch without a default");
                }
                break;

            case Opcode.Unwind:
                Operands(i, 2);
                Address(i, 0);
                Is(i, 1, IrTypes.Word);
                NoDest(i);
                break;

            case Opcode.LabelAddr:
                Operands(i, 0);
                Dest(i, IrTypes.Word);
                if (i.Targets.Count != 1)
                {
                    throw new IrVerifyException("labeladdr needs exactly one target");
                }
                break;

            case Opcode.StackPointer:
            case Opcode.FramePointer:
                Operands(i, 0);
                Dest(i, IrTypes.Word);
                break;

            case Opcode.Syscall:
                if (i.Operands.Count < 1)
                {
                    throw new IrVerifyException("syscall without a number");
                }
                Dest(i, IrTypes.Word);
                break;

            default:
                throw new IrVerifyException($"unknown opcode {i.Op}");
        }
    }

    private static void Operands(Instr i, int n)
    {
        if (i.Operands.Count != n)
        {
            throw new IrVerifyException($"expected {n} operands, has {i.Operands.Count}");
        }
    }

    private static void Is(Instr i, int k, IrType t)
    {
        if (i.Operands[k].Type != t)
        {
            throw new IrVerifyException($"operand {k} is {i.Operands[k].Type}, expected {t}");
        }
    }

    private static void Int(Instr i, int k)
    {
        if (!i.Operands[k].Type.IsInt())
        {
            throw new IrVerifyException($"operand {k} is {i.Operands[k].Type}, expected an integer");
        }
    }

    private static void Float(Instr i, int k)
    {
        if (!i.Operands[k].Type.IsFloat())
        {
            throw new IrVerifyException($"operand {k} is {i.Operands[k].Type}, expected a float");
        }
    }

    private static void Address(Instr i, int k) => Is(i, k, IrTypes.Word);

    private static void Same(Instr i, int a, int b)
    {
        if (i.Operands[a].Type != i.Operands[b].Type)
        {
            throw new IrVerifyException($"operands {a} and {b} differ: {i.Operands[a].Type} and {i.Operands[b].Type}");
        }
    }

    private static void Dest(Instr i, IrType t)
    {
        if (i.Dest is null)
        {
            throw new IrVerifyException("no destination");
        }
        if (i.Dest.Type != t)
        {
            throw new IrVerifyException($"destination is {i.Dest.Type}, expected {t}");
        }
    }

    private static void DestFloat(Instr i)
    {
        if (i.Dest is null || !i.Dest.Type.IsFloat())
        {
            throw new IrVerifyException("destination must be a float");
        }
    }

    private static void DestInt(Instr i)
    {
        if (i.Dest is null || !i.Dest.Type.IsInt())
        {
            throw new IrVerifyException("destination must be an integer");
        }
    }

    private static void NoDest(Instr i)
    {
        if (i.Dest is not null)
        {
            throw new IrVerifyException("has a destination");
        }
    }
}
