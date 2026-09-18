#nullable enable
namespace Corsac.Lang.Ir;

/// <summary>
/// What a backend produces from a module: a relocatable object the linker
/// combines into an executable. One per target.
/// </summary>
public sealed class ObjectFile
{
    public List<Section> Sections { get; } = new();
    public List<Symbol> Symbols { get; } = new();

    public Section Section(string name)
    {
        foreach (Section s in Sections)
        {
            if (s.Name == name)
            {
                return s;
            }
        }
        throw new KeyNotFoundException(name);
    }
}

public enum SectionKind : byte
{
    Code,
    ReadOnlyData,
    Data,
    /// <summary>Zero-filled, occupies no bytes in the file.</summary>
    Uninitialised,
    /// <summary>Metadata a loader may read; not mapped for execution.</summary>
    Note,
}

public sealed class Section
{
    public string Name { get; }
    public SectionKind Kind { get; }
    public List<byte> Bytes { get; } = new();
    public int Align { get; set; } = 4;
    /// <summary>For an uninitialised section: how many zero bytes it stands for.</summary>
    public int ZeroBytes { get; set; }
    public List<Relocation> Relocs { get; } = new();

    public Section(string name, SectionKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public int Size => Kind == SectionKind.Uninitialised ? ZeroBytes : Bytes.Count;
}

public enum RelocKind : byte
{
    /// <summary>The 32-bit word at Offset gets the absolute address of Symbol plus Addend.</summary>
    Abs32,
    /// <summary>The 32-bit word at Offset gets Symbol plus Addend minus the address of the word: a call or jump displacement.</summary>
    Rel32,
    /// <summary>R_386_GOT32: the offset of Symbol's GOT slot from the GOT base (PIC data access).</summary>
    Got32,
    /// <summary>R_386_PLT32: like Rel32 but through the PLT entry for Symbol when it lives in another object.</summary>
    Plt32,
    /// <summary>R_386_GLOB_DAT: a GOT slot the dynamic linker fills with Symbol's address.</summary>
    GlobData,
    /// <summary>R_386_JUMP_SLOT: a PLT GOT slot the dynamic linker fills with Symbol's address, lazily.</summary>
    JumpSlot,
    /// <summary>R_386_RELATIVE: the word gets the load base plus Addend; no symbol.</summary>
    Relative,
    /// <summary>R_386_GOTOFF: Symbol plus Addend minus the GOT base (PIC local data access).</summary>
    GotOff,
    /// <summary>R_386_GOTPC: the GOT base plus Addend minus the address of the word: the thunk that loads EBX.</summary>
    GotPc,

    /// <summary>
    /// The ABSOLUTE ADDRESS of Symbol's GOT slot, plus Addend. Not an ELF
    /// relocation at all: a non-position-independent executable knows where
    /// its own GOT is, so this is resolved here and the word it lands in is
    /// a displacement in an instruction that loads the slot.
    ///
    /// This is how an executable reaches a shared library's DATA -- a type
    /// descriptor, a static field, the thread block -- without a copy
    /// relocation (which would give it a second copy of something whose
    /// address is its identity) and without a text relocation (which would
    /// cost the shared text segment).
    /// </summary>
    GotAddr,
}

public readonly record struct Relocation(int Offset, string Symbol, long Addend, RelocKind Kind);

public sealed class Symbol
{
    public required string Name { get; init; }
    /// <summary>Null for an undefined symbol the linker must supply.</summary>
    public Section? Section { get; init; }
    public long Offset { get; init; }
    public long Size { get; init; }
    public bool IsFunction { get; init; }
    public bool Global { get; init; } = true;
    public bool IsDefined => Section is not null;
}

/// <summary>
/// A code generator for one target. Given IR, produce an object file. It
/// owns instruction selection, register allocation, the calling convention
/// and the encoding, and nothing above it knows any of those exist.
/// </summary>
public interface IBackend
{
    Target Target { get; }

    /// <summary>
    /// Generate. Diagnostics -- an instruction the target refuses, a
    /// function too large -- go to <paramref name="errors"/> as messages
    /// naming the function.
    /// </summary>
    ObjectFile Generate(Module module, List<string> errors);

    /// <summary>
    /// The same, as readable assembly, for `corc compile --asm` and for
    /// anyone debugging the backend. Not fed to any assembler.
    /// </summary>
    string Assembly(Module module);
}
