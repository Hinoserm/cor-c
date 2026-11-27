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

    /// <summary>
    /// LONG MODE FOR A WINDOWS IMAGE (PE32+; docs/WIN32-TARGET.md): the
    /// selection calls into Windows by Windows' x64 convention and finds the
    /// thread block through a TLS slot of the TEB; every function's frame is
    /// one Windows' unwinder can walk (no frame made after the prologue,
    /// FramePointerFree), described in unwind data (X64Backend.Unwind), and a
    /// frame of a page or more probes its pages in order as it is made
    /// (Encoder's PrologueL), as Windows grows a thread's stack one guard
    /// page at a time. Set with Long, by X64Backend.Windows.
    /// </summary>
    public static bool Windows { get; set; }

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
