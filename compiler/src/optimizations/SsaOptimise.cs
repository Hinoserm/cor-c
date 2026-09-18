#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>
/// The SSA section of the optimiser as one pass: into SSA, the passes
/// that want it, and out again. Packaged this way so the pipeline cannot
/// be arranged with a phi still in the function when the backend starts;
/// whatever runs after this pass sees ordinary IR.
/// </summary>
public sealed class SsaOptimise : IPass
{
    public string Name => "ssa-opt";

    /// <summary>The passes run while the function is in SSA form, in order.</summary>
    public List<IPass> Passes { get; } = new()
    {
        new Sccp(),
        new ConstantAndCopyPropagation(ssa: true),
        new Peephole(ssa: true),
        new Gvn(),
        new Dse(),
        new ConstantAndCopyPropagation(ssa: true),
        new ConstantFold(),
        new DeadCodeElimination(),
        new BranchSimplify(),
    };

    /// <summary>Verify after each inner pass; on by default in debug builds, like the pipeline.</summary>
    public bool Verify { get; set; }
#if DEBUG
        = true;
#endif

    public void Run(Function f)
    {
        new Ssa().Run(f);
        Check(f, "ssa");
        foreach (IPass p in Passes)
        {
            p.Run(f);
            Check(f, p.Name);
        }
        new OutOfSsa().Run(f);
        Check(f, "unssa");
        // The edge splits left by out-of-SSA are mostly mergeable again.
        new BranchSimplify().Run(f);
        Check(f, "branches");
    }

    private void Check(Function f, string after)
    {
        if (Verify)
        {
            Verifier.Check(f, "after " + after);
        }
    }
}
