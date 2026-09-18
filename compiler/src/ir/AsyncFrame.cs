#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// Where an async method's state lives: the state machine object is the
/// function's first parameter, its resumption index is a 4-byte field, and
/// everything that must survive a suspension -- registers live across it and
/// frame memory -- is laid out from FieldsStart. The object's final size is
/// written into the data item named SizeSymbol, which the kickoff reads.
/// </summary>
public sealed class AsyncFrame
{
    public required VReg StateMachine { get; init; }
    public required int StateOffset { get; init; }
    public required int FieldsStart { get; init; }
    public required string SizeSymbol { get; init; }

    /// <summary>The markers lowering puts around the continuation registration.</summary>
    public const string Suspend = "__suspend";
    public const string Resume = "__resume";
}
