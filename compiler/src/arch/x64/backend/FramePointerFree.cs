#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// A FUNCTION WITHOUT A FRAME POINTER made so, last before it is written: a
/// leaf the allocator gave RBP a value in (MFunction.FramePointerFree). Every
/// pass until here took an operand based on RBP for the frame and the value
/// in RBP as MReg.RbpData; now the frame is addressed off RSP -- which
/// nothing moves between the prologue and the epilogue, RSP + K being where
/// RBP would point, K the frame the prologue makes (Encoder.FrameBytes) --
/// and the value is in RBP. The prologue still sets RBP before anything is
/// written to it, and marks the function so a fault finds its frame
/// (Encoder.PrologueL).
/// </summary>
internal static class FramePointerFree
{
    public static void Run(MFunction m)
    {
        if (!m.FramePointerFree) return;
        int k = Encoder.FrameBytes(m);
        Corsac.Lang.X86.FramePointerFree.Readdress(m, disp => disp + k, true);
    }
}
