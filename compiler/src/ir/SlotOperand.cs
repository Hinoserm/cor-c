#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// The address of a frame slot: memory the backend reserves in the stack
/// frame. For locals whose address is taken, for exception-handler records,
/// and for anything else that must have an address.
/// </summary>
public sealed class SlotOperand : Operand
{
    public FrameSlot Slot { get; }
    public SlotOperand(FrameSlot slot) => Slot = slot;
    public override IrType Type => IrTypes.Word;
    public override string ToString() => $"&{Slot}";
}
