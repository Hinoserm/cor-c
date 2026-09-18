#nullable enable
namespace Corsac.Lang;

/// <summary>
/// What the front half of the compiler needs to know about a machine.
///
/// The binder lays out fields, frames and statics with these numbers, and
/// lowering computes offsets from them. Nothing above the backend names a
/// register or an instruction: a target is a set of sizes and a layout, and
/// the code that turns IR into instructions lives entirely in its Backend.
///
/// One instance per compilation, chosen by `corc --target`. The language
/// does not change with the target -- `long` is 64 bits everywhere and a
/// reference is one word -- only how the machine holds it does.
/// </summary>
public sealed class Target
{
    /// <summary>A short name a command line can select: "x86", "corsac".</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The processor within the family, from `--cpu`.
    /// </summary>
    public string Cpu { get; set; } = "486";
    public global::Corsac.Lang.X86.X86Cpu X86Profile { get; set; } = global::Corsac.Lang.X86.X86Cpu.Parse(Array.Empty<string>());

    /// <summary>Bytes in a machine word: the size of a reference or pointer.</summary>
    public required int WordSize { get; init; }

    /// <summary>
    /// Whether a 64-bit integer fits one register. When false the backend
    /// carries `long` as a pair and lowering routes division through
    /// runtime helpers.
    /// </summary>
    public required bool NativeI64 { get; init; }

    /// <summary>Header on every class instance: vtable, synchronisation word.</summary>
    public required int ObjectHeaderBytes { get; init; }

    /// <summary>Header on arrays and strings: the object header, then a count, padded.</summary>
    public required int ArrayHeaderBytes { get; init; }

    /// <summary>Where the element count of an array or string sits.</summary>
    public required int ArrayCountOffset { get; init; }

    /// <summary>Bytes of type descriptor sitting immediately before a vtable.</summary>
    public required int DescriptorBytes { get; init; }

    /// <summary>Bytes of static storage reserved below the first static field.</summary>
    public required int StaticBase { get; init; }

    /// <summary>Alignment of an 8-byte field or element inside an object.</summary>
    public required int Align64 { get; init; }

    /// <summary>The size of a value of the given primitive on this machine.</summary>
    public int SizeOf(Prim p) => p switch
    {
        Prim.Void => 0,
        Prim.Bool or Prim.I8 or Prim.U8 => 1,
        Prim.I16 or Prim.U16 or Prim.Char => 2,
        Prim.I32 or Prim.U32 or Prim.F32 => 4,
        Prim.I64 or Prim.U64 or Prim.F64 => 8,
        _ => WordSize,      // string, Type, Any, class references
    };

    /// <summary>
    /// The 486: four-byte words, no native 64-bit integer, eight-byte object
    /// header, sixteen-byte array header keeping 8-byte elements aligned.
    /// </summary>
    public static readonly Target X86 = new()
    {
        Name = "x86",
        WordSize = 4,
        NativeI64 = false,
        ObjectHeaderBytes = 8,
        ArrayHeaderBytes = 16,
        ArrayCountOffset = 8,
        DescriptorBytes = 48,
        StaticBase = 16,
        Align64 = 8,
    };

    /// <summary>
    /// The original 64-bit CORSAC machine, kept so a backend for it can be
    /// added back without changing anything above it.
    /// </summary>
    public static readonly Target Corsac = new()
    {
        Name = "corsac",
        WordSize = 8,
        NativeI64 = true,
        ObjectHeaderBytes = 16,
        ArrayHeaderBytes = 24,
        ArrayCountOffset = 16,
        DescriptorBytes = 96,
        StaticBase = 16,
        Align64 = 8,
    };

    /// <summary>
    /// The target of the current compilation.
    ///
    /// A process-wide setting rather than a parameter threaded through every
    /// constructor, because `Type.Size` is asked from hundreds of places that
    /// have no business knowing a target exists. Set once by the driver before
    /// anything is parsed; a compilation never changes target midway.
    /// </summary>
    public static Target Current { get; set; } = X86;

    public static Target? ByName(string name) => name switch
    {
        "x86" or "i386" or "i486" => X86,
        "corsac" => Corsac,
        _ => null,
    };
}
