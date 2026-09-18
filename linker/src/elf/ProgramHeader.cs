#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

internal readonly record struct ProgramHeader(
    uint Type,
    uint Offset,
    uint VAddr,
    uint FileSize,
    uint MemSize,
    uint Flags,
    uint Align)
{
    /// <summary>
    /// Writes the header. <paramref name="loadBias"/> is how far the link
    /// address runs ahead of the load address: a higher-half kernel is linked
    /// at 0xC0100000 and loaded at 0x00100000, so its bias is 0xC0000000 and
    /// its p_paddr is the address a loader with no MMU yet can actually copy
    /// to. Zero -- every hosted program -- leaves p_paddr equal to p_vaddr.
    /// </summary>
    public void WriteTo(ElfBuffer b, uint loadBias = 0)
    {
        b.U32(Type);
        b.U32(Offset);
        b.U32(VAddr);
        // Physical address: meaningless to Linux, but a bare-metal loader
        // copies by it, and only a loaded segment has one worth biasing.
        b.U32(Type == Elf.PtLoad ? VAddr - loadBias : VAddr);
        b.U32(FileSize);
        b.U32(MemSize);
        b.U32(Flags);
        b.U32(Align);
    }
}
