#nullable enable
namespace Corsac.Lang.Ir;

/// <summary>
/// A CALL THROUGH A WORD OF A STATICS TABLE (Lowering.StaticTableTouch,
/// StaticWord): shared code reaching another instantiation's static -- its
/// type's touch, its static method, its thread's cell -- through the table
/// that instantiation's type context or dictionary holds. Marked so, as a
/// call into C is (NativeCall), by the Callee of the indirect call: the word
/// is read out of a type context, a chain of loads from an object's first
/// word as a method read out of a descriptor is, and taken for one
/// (RegionSummary.MethodRead) it was a call of every method of every class
/// at that slot -- the region rules gave up on every boundary of the program.
/// It is an unknown call of one function, which is what it is.
/// </summary>
public static class StaticTableCall
{
    public const string Marker = "@statics";

    /// <summary>Whether this call goes through a statics table's word.</summary>
    public static bool Is(Instr i) => i.Op == Opcode.CallIndirect && i.Callee == Marker;
}
