#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// WHICH MODE OF THE MACHINE THE CODE IS FOR: i386 or long mode. One
/// instruction set in two modes -- as LLVM's and GCC's x86 backends, one
/// each, treat it -- so the two backends share one machine IR (MReg, MMem,
/// MImm, MInstr, MBlock, MFunction, Frame) and every pass that is the same
/// algorithm on both, each asking here where the modes differ: how wide a
/// word is, how many general and vector registers there are, what a frame's
/// slots are rounded to.
///
/// A process compiles for one target (Target.Current), so this is one
/// setting a process, made by the backend that starts generating
/// (X86Backend, X64Backend) before any machine code exists.
/// </summary>
public static class X86Mode
{
    /// <summary>Long mode (x86-64) rather than i386.</summary>
    public static bool Long { get; set; }

    /// <summary>A machine word, a frame slot's least size and an instruction's default width: 4 or 8 bytes.</summary>
    public static int Word => Long ? 8 : 4;

    /// <summary>How many general registers: 8, or 16 with REX.</summary>
    public static int Gprs => Long ? 16 : 8;

    private static readonly string[] Names32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
    private static readonly string[] Names64 =
    {
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
    };

    /// <summary>A general register's name in this mode (the enum has two names a number: Gpr).</summary>
    public static string GprName(int id) => Long ? Names64[id] : Names32[id];
}
