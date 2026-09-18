#nullable enable

namespace Corsac.Lang.Ir;

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
