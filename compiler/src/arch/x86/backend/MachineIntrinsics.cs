#nullable enable

namespace Corsac.Lang.X86;

/// <summary>
/// The reserved callee names that stand for a machine instruction rather
/// than for a function.
///
/// Lowering emits a `Sys.PortOut8(...)` as a call to one of these instead of
/// as a new IR opcode, and the selector replaces the call with the
/// instruction. The point of the indirection is that everything in between --
/// dead-code elimination, common-subexpression elimination, store
/// elimination, the verifier -- already knows that a call may do anything and
/// therefore may not be moved, duplicated or removed, which is exactly the
/// rule these instructions need. A new opcode would have to be taught to each
/// pass, and a pass that had not been taught would be silently wrong.
///
/// The names begin with a prefix no mangled name can produce, and no symbol
/// is ever emitted for them: a target that does not recognise one is a target
/// that cannot do port I/O, and it will say so at selection.
/// </summary>
internal static class MachineIntrinsics
{
    public const string Prefix = "__x86.i.";

    public const string In8 = Prefix + "in8";
    public const string In16 = Prefix + "in16";
    public const string In32 = Prefix + "in32";
    public const string Out8 = Prefix + "out8";
    public const string Out16 = Prefix + "out16";
    public const string Out32 = Prefix + "out32";
    public const string InString16 = Prefix + "insw";
    public const string OutString16 = Prefix + "outsw";
    public const string Cli = Prefix + "cli";
    public const string Sti = Prefix + "sti";
    public const string Hlt = Prefix + "hlt";
    public const string Lidt = Prefix + "lidt";
    public const string Lgdt = Prefix + "lgdt";
    public const string Invlpg = Prefix + "invlpg";
    public const string ReadCr = Prefix + "readcr";
    public const string WriteCr = Prefix + "writecr";
    public const string LoadSegments = Prefix + "loadsegments";
    public const string ThreadBlock = Prefix + "threadblock";
    public const string SetGs = Prefix + "setgs";
}
