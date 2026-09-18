#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>A branch target.</summary>
public sealed class MLabel : MOperand
{
    public MBlock Target { get; }
    public MLabel(MBlock target) => Target = target;
    public override string ToString() => Target.Name;
}
