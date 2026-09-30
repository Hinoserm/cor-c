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
    /// <summary>A short name a command line can select: "x86", "x86-64".</summary>
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
    /// x86-64 in long mode, from the first AMD64 processors on (K8): eight-byte
    /// words and native 64-bit integers, SSE2 floating point, the System V
    /// AMD64 calling convention. The layout numbers are the 64-bit ones the
    /// front half already scales by the word: a sixteen-byte object header
    /// (vtable and synchronisation word), a twenty-four-byte array header with
    /// the count at sixteen, a twelve-word descriptor before each vtable.
    /// </summary>
    public static readonly Target X86_64 = new()
    {
        Name = "x86-64",
        Cpu = "k8",
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
        "x86-64" or "x86_64" or "amd64" or "x64" => X86_64,
        _ => null,
    };
}
