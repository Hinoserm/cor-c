#nullable enable
namespace Corsac.Lang.Ir;

/// <summary>
/// A CALL INTO C. The IR has no opcode of its own for one, for the reason
/// MachineIntrinsics gives: every pass already treats a call as something
/// that may do anything and may not be moved, duplicated or dropped, which
/// is what a foreign function needs, and a new opcode would be a thing each
/// pass had to be taught. So it is an ordinary call whose callee says so:
///
///   - a direct call to `@native:NAME` calls the C symbol NAME;
///   - an indirect call whose Callee is `@native` calls a C function pointer.
///
/// No mangled name begins with '@', and a pass that looks callees up by
/// name finds none by this one and treats the call as unknown -- which it
/// is. The backends read it: a C function may be variadic (x86-64 then
/// wants the vector argument count in AL) and may assume the stack aligned
/// to sixteen at the call (i386 keeps only four for its own calls).
/// </summary>
public static class NativeCall
{
    public const string Prefix = "@native:";
    public const string Indirect = "@native";

    /// <summary>The callee name a direct call to C symbol <paramref name="symbol"/> carries.</summary>
    public static string Of(string symbol) => Prefix + symbol;

    /// <summary>Whether this call enters C.</summary>
    public static bool Is(Instr i)
        => (i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(Prefix, StringComparison.Ordinal))
        || (i.Op == Opcode.CallIndirect && i.Callee == Indirect);

    /// <summary>The symbol a callee name stands for: the C name for a native call, the name itself otherwise.</summary>
    public static string Symbol(string callee)
        => callee.StartsWith(Prefix, StringComparison.Ordinal) ? callee[Prefix.Length..] : callee;
}
