#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The general registers, numbered as the hardware numbers them in a ModRM
/// byte. ESP is the stack pointer and never allocated. EBP is the frame
/// pointer, allocated only in a function that runs without one
/// (MFunction.FramePointerFree), as MReg.EbpData until FramePointerFree.Run.
/// </summary>
public enum Gpr : byte
{
    Eax = 0, Ecx = 1, Edx = 2, Ebx = 3, Esp = 4, Ebp = 5, Esi = 6, Edi = 7,
}
