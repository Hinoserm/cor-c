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
    // Long mode's names for the same eight, and its eight more (REX). Two
    // names a number: never printed by ToString -- X86Mode.GprName says
    // which the mode calls it.
    Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rbp = 5, Rsi = 6, Rdi = 7,
    R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15,
}
