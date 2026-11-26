#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Calls;

/// <summary>Where a value is at a call: a general register, a float register, the x87's stack top, or the stack.</summary>
internal enum PlaceKind : byte { General, Float, FloatStack, Stack }

/// <summary>
/// ONE VALUE'S PLACE AT A CALL (or one word's of it): the register, by the
/// target's own number in its file, or the bytes at `Offset` from the
/// first stack argument's address; and how many bytes of it are there.
/// </summary>
internal readonly record struct ArgPlace(PlaceKind Kind, int Register, int Offset, int Bytes)
{
    public static ArgPlace InGeneral(int register, int bytes) => new(PlaceKind.General, register, 0, bytes);
    public static ArgPlace InFloat(int register, int bytes) => new(PlaceKind.Float, register, 0, bytes);
    public static ArgPlace OnStack(int offset, int bytes) => new(PlaceKind.Stack, -1, offset, bytes);
    public bool InRegister => Kind is PlaceKind.General or PlaceKind.Float;
}

/// <summary>A call's arguments placed (Convention.Arguments): each one's place, in order, and what they take in all.</summary>
internal sealed class CallPlan
{
    /// <summary>The place of each argument, the first first.</summary>
    public required ArgPlace[] Places { get; init; }
    /// <summary>The stack arguments' bytes, all of them.</summary>
    public required int StackBytes { get; init; }
    /// <summary>How many general and how many float registers carry arguments.</summary>
    public required int Generals { get; init; }
    public required int Floats { get; init; }
}

/// <summary>
/// A CALLING CONVENTION AS A DESCRIPTION: where each argument of a
/// signature goes and where a result comes back, said once, for the code
/// at a function's entry that takes them and the code at a call that
/// gives them -- which were two loops a mode, each counting registers its
/// own way, and would be two more for every architecture added.
///
/// As LLVM's CCState assigns each value a CCValAssign (a register, or a
/// stack offset) from its convention's rules and its lowering reads only
/// those, and Cranelift's ABIMachineSpec::compute_arg_locs answers a list
/// of ABIArg slots its one caller and callee implementation consumes: the
/// WHERE is the target's description, the moving of values the
/// selector's.
///
/// What a call changes and keeps is the register allocator's question
/// (the target's Implicit tables), not this one's.
/// </summary>
internal abstract class Convention
{
    public abstract string Name { get; }

    /// <summary>What the stack pointer is a multiple of at a call, in bytes.</summary>
    public abstract int StackAlignment { get; }

    /// <summary>The first stack argument's distance above the callee's frame base, once its prologue has run: past the saved base and the return address.</summary>
    public abstract int StackArgumentsAt { get; }

    /// <summary>
    /// Where each argument goes. `registers` false is the convention's
    /// stack-only form where it has one (i386's calls into C, a method
    /// marked to take its arguments from the stack); a convention with
    /// one form ignores it.
    /// </summary>
    public abstract CallPlan Arguments(IReadOnlyList<IrType> types, bool registers = true);

    /// <summary>Where a result of this type comes back: one place, or two for a value of two words; none for no value.</summary>
    public abstract ArgPlace[] Result(IrType type);
}
