#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// The general registers, numbered as the hardware numbers them in a ModRM
/// byte. ESP and EBP are never allocated: EBP is the frame pointer in every
/// function and ESP is the stack pointer.
/// </summary>
public enum Gpr : byte
{
    Eax = 0, Ecx = 1, Edx = 2, Ebx = 3, Esp = 4, Ebp = 5, Esi = 6, Edi = 7,
}
