#nullable enable
using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Thrown when bytes claim to be an ELF object and are not one we can use,
/// or when an in-memory object cannot be expressed as one.
/// </summary>
public sealed class ElfFormatException : Exception
{
    public ElfFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// The parts of the ELF32 specification and the i386 psABI this toolchain
/// uses, and nothing else. Anything not here is refused by the reader
/// rather than half-understood.
/// </summary>
internal static class Elf
{
    public const int HeaderSize = 52;
    public const int ProgramHeaderSize = 32;
    public const int SectionHeaderSize = 40;
    public const int SymbolSize = 16;
    public const int RelSize = 8;

    public static ReadOnlySpan<byte> Magic => "ELF"u8;

    public const byte Class32 = 1;
    public const byte Data2Lsb = 1;
    public const byte VersionCurrent = 1;
    public const byte OsAbiSysV = 0;

    public const ushort TypeRel = 1;
    public const ushort TypeExec = 2;
    public const ushort TypeDyn = 3;
    public const ushort MachineI386 = 3;

    public const uint ShtNull = 0;
    public const uint ShtProgBits = 1;
    public const uint ShtSymTab = 2;
    public const uint ShtStrTab = 3;
    public const uint ShtRela = 4;
    public const uint ShtNote = 7;
    public const uint ShtNoBits = 8;
    public const uint ShtRel = 9;
    public const uint ShtHash = 5;
    public const uint ShtDynamic = 6;
    public const uint ShtDynSym = 11;

    public const uint ShfWrite = 1;
    public const uint ShfAlloc = 2;
    public const uint ShfExecInstr = 4;
    public const uint ShfInfoLink = 0x40;

    public const ushort ShnUndef = 0;
    public const ushort ShnLoReserve = 0xff00;
    public const ushort ShnAbs = 0xfff1;
    public const ushort ShnCommon = 0xfff2;

    public const byte StbLocal = 0;
    public const byte StbGlobal = 1;
    public const byte StbWeak = 2;

    public const byte SttNoType = 0;
    public const byte SttObject = 1;
    public const byte SttFunc = 2;
    public const byte SttSection = 3;
    public const byte SttFile = 4;

    public const uint PtLoad = 1;
    public const uint PtDynamic = 2;
    public const uint PtInterp = 3;
    public const uint PtGnuStack = 0x6474e551;

    /// <summary>
    /// The dynamic array tags this linker writes. Everything the system
    /// loader needs to find the symbol table, the relocations and the
    /// libraries, and nothing about lazy binding: every PLT slot is
    /// resolved at load time (DT_BIND_NOW), which costs a little startup
    /// and saves a resolver stub in every object we emit.
    /// </summary>
    public const int DtNull = 0;
    public const int DtNeeded = 1;
    public const int DtPltRelSz = 2;
    public const int DtPltGot = 3;
    public const int DtHash = 4;
    public const int DtStrTab = 5;
    public const int DtSymTab = 6;
    public const int DtStrSz = 10;
    public const int DtSymEnt = 11;
    public const int DtInit = 12;

    /// <summary>
    /// The function DT_INIT points at in a COR-C# shared object: the one
    /// the code generator writes for every library, which tells the runtime
    /// where this image's statics and frame table are. Named here rather
    /// than in the lowering because the linker has to look it up and the
    /// linker is below the lowering.
    /// </summary>
    public const string SharedInitName = "__corsac_init";
    public const int DtDebug = 21;
    public const int DtInitArray = 25;
    public const int DtInitArraySz = 27;
    public const int DtSoName = 14;
    public const int DtRel = 17;
    public const int DtRelSz = 18;
    public const int DtRelEnt = 19;
    public const int DtPltRel = 20;
    public const int DtTextRel = 22;
    public const int DtJmpRel = 23;
    public const int DtBindNow = 24;
    public const int DtRunPath = 29;
    public const int DtFlags = 30;
    public const int DtFlags1 = 0x6ffffffb;

    public const uint DfBindNow = 0x8;
    public const uint DfTextRel = 0x4;
    public const uint DfSymbolic = 0x2;
    public const uint Df1Now = 0x1;

    /// <summary>The program interpreter a dynamically linked i386 Linux program names.</summary>
    public const string DefaultInterpreter = "/lib/ld-linux.so.2";

    /// <summary>The SysV hash of a symbol name, as the ELF specification defines it.</summary>
    public static uint HashName(string name)
    {
        uint h = 0;
        foreach (byte c in Encoding.UTF8.GetBytes(name))
        {
            h = (h << 4) + c;
            uint g = h & 0xf0000000;
            if (g != 0)
            {
                h ^= g >> 24;
            }
            h &= ~g;
        }
        return h;
    }

    public const uint PfX = 1;
    public const uint PfW = 2;
    public const uint PfR = 4;

    /// <summary>The i386 page: what PT_LOAD segments must be aligned to.</summary>
    public const uint PageSize = 4096;

    /// <summary>
    /// GNU ld takes a missing .note.GNU-stack as a request for an executable
    /// stack and warns about it; an empty one says "no thanks". Written by
    /// the object writer and dropped by the reader, so a round trip is exact.
    /// </summary>
    public const string GnuStackNote = ".note.GNU-stack";

    public static uint AlignUp(uint value, uint align)
    {
        if (align <= 1)
        {
            return value;
        }
        return checked((value + align - 1) / align * align);
    }

    /// <summary>
    /// How a section of a given kind is described in a section header. The
    /// reader inverts this from the flags alone, which is why the note kind
    /// is "anything not allocated" rather than SHT_NOTE.
    /// </summary>
    public static (uint Type, uint Flags) SectionTypeAndFlags(SectionKind kind)
    {
        switch (kind)
        {
            case SectionKind.Code:
                return (ShtProgBits, ShfAlloc | ShfExecInstr);
            case SectionKind.ReadOnlyData:
                return (ShtProgBits, ShfAlloc);
            case SectionKind.Data:
                return (ShtProgBits, ShfAlloc | ShfWrite);
            case SectionKind.Uninitialised:
                return (ShtNoBits, ShfAlloc | ShfWrite);
            case SectionKind.Note:
                return (ShtProgBits, 0);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>The symbol type a symbol is recorded with.</summary>
    public static byte SymbolType(Symbol symbol)
    {
        if (symbol.IsFunction)
        {
            return SttFunc;
        }
        if (symbol.IsDefined && symbol.Size > 0)
        {
            return SttObject;
        }
        return SttNoType;
    }

    /// <summary>
    /// The i386 psABI relocation numbers. The reader and writer are total
    /// over <see cref="RelocKind"/> so a dynamic object round-trips even
    /// before the linker knows what to do with a GOT.
    /// </summary>
    public static byte RelocType(RelocKind kind)
    {
        switch (kind)
        {
            case RelocKind.Abs32:
                return 1;
            case RelocKind.Rel32:
                return 2;
            case RelocKind.Got32:
                return 3;
            case RelocKind.Plt32:
                return 4;
            case RelocKind.GlobData:
                return 6;
            case RelocKind.JumpSlot:
                return 7;
            case RelocKind.Relative:
                return 8;
            case RelocKind.GotOff:
                return 9;
            case RelocKind.GotPc:
                return 10;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public static RelocKind? RelocKindOf(byte type)
    {
        switch (type)
        {
            case 1:
                return RelocKind.Abs32;
            case 2:
                return RelocKind.Rel32;
            case 3:
                return RelocKind.Got32;
            case 4:
                return RelocKind.Plt32;
            case 6:
                return RelocKind.GlobData;
            case 7:
                return RelocKind.JumpSlot;
            case 8:
                return RelocKind.Relative;
            case 9:
                return RelocKind.GotOff;
            case 10:
                return RelocKind.GotPc;
            default:
                return null;
        }
    }

    public static void WriteHeader(ElfBuffer b, ushort type, uint entry, uint phoff, uint shoff, ushort phnum, ushort shnum, ushort shstrndx)
    {
        b.Bytes(Magic);
        b.U8(Class32);
        b.U8(Data2Lsb);
        b.U8(VersionCurrent);
        b.U8(OsAbiSysV);
        b.U8(0);
        b.Zeros(7);
        b.U16(type);
        b.U16(MachineI386);
        b.U32(VersionCurrent);
        b.U32(entry);
        b.U32(phoff);
        b.U32(shoff);
        b.U32(0);
        b.U16(HeaderSize);
        // as and ld leave e_phentsize zero in an object with no program
        // headers; matching them keeps `cmp` against their output honest.
        b.U16(phnum == 0 ? (ushort)0 : (ushort)ProgramHeaderSize);
        b.U16(phnum);
        b.U16(SectionHeaderSize);
        b.U16(shnum);
        b.U16(shstrndx);
    }
}

/// <summary>A growable little-endian byte sink with the few operations ELF layout needs.</summary>
internal sealed class ElfBuffer
{
    private readonly List<byte> _bytes = new();

    public int Length => _bytes.Count;

    public void U8(byte v)
    {
        _bytes.Add(v);
    }

    public void U16(ushort v)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        _bytes.AddRange(s);
    }

    public void U32(uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        _bytes.AddRange(s);
    }

    public void Bytes(ReadOnlySpan<byte> s)
    {
        _bytes.AddRange(s);
    }

    public void Zeros(int count)
    {
        _bytes.AddRange(new byte[count]);
    }

    /// <summary>Zero-fill up to an offset computed in advance. Overshooting is a layout bug, so it throws.</summary>
    public void PadTo(uint offset)
    {
        if (offset < _bytes.Count)
        {
            throw new InvalidOperationException($"layout error: at 0x{_bytes.Count:x}, asked to pad back to 0x{offset:x}");
        }
        Zeros((int)(offset - _bytes.Count));
    }

    public uint AlignTo(uint align)
    {
        uint at = Elf.AlignUp((uint)_bytes.Count, align);
        PadTo(at);
        return at;
    }

    public void PatchU32(int offset, uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        for (int i = 0; i < 4; i++)
        {
            _bytes[offset + i] = s[i];
        }
    }

    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }
}

/// <summary>
/// An ELF string table: NUL-terminated names, offset 0 the empty string.
/// Duplicates share one entry; the writer is deterministic either way.
/// </summary>
internal sealed class StringTable
{
    private readonly List<byte> _bytes = new() { 0 };
    private readonly Dictionary<string, uint> _at = new();

    public uint Add(string s)
    {
        if (s.Length == 0)
        {
            return 0;
        }
        if (_at.TryGetValue(s, out uint at))
        {
            return at;
        }
        at = (uint)_bytes.Count;
        _bytes.AddRange(Encoding.UTF8.GetBytes(s));
        _bytes.Add(0);
        _at[s] = at;
        return at;
    }

    public byte[] ToArray()
    {
        return _bytes.ToArray();
    }

    public static string Read(ReadOnlySpan<byte> table, uint offset, string what)
    {
        if (offset >= table.Length)
        {
            throw new ElfFormatException($"{what}: string offset 0x{offset:x} is past the end of its string table");
        }
        ReadOnlySpan<byte> rest = table[(int)offset..];
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            throw new ElfFormatException($"{what}: unterminated string at 0x{offset:x}");
        }
        return Encoding.UTF8.GetString(rest[..end]);
    }
}

internal readonly record struct SectionHeader(
    uint Name,
    uint Type,
    uint Flags,
    uint Addr,
    uint Offset,
    uint Size,
    uint Link,
    uint Info,
    uint AddrAlign,
    uint EntSize)
{
    public void WriteTo(ElfBuffer b)
    {
        b.U32(Name);
        b.U32(Type);
        b.U32(Flags);
        b.U32(Addr);
        b.U32(Offset);
        b.U32(Size);
        b.U32(Link);
        b.U32(Info);
        b.U32(AddrAlign);
        b.U32(EntSize);
    }

    public static SectionHeader Read(ReadOnlySpan<byte> s)
    {
        return new SectionHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(s[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
    }
}

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

internal readonly record struct SymbolEntry(
    uint Name,
    uint Value,
    uint Size,
    byte Info,
    byte Other,
    ushort Shndx)
{
    public byte Bind => (byte)(Info >> 4);
    public byte Type => (byte)(Info & 0xf);

    public static byte MakeInfo(byte bind, byte type)
    {
        return (byte)((bind << 4) | (type & 0xf));
    }

    public void WriteTo(ElfBuffer b)
    {
        b.U32(Name);
        b.U32(Value);
        b.U32(Size);
        b.U8(Info);
        b.U8(Other);
        b.U16(Shndx);
    }

    public static SymbolEntry Read(ReadOnlySpan<byte> s)
    {
        return new SymbolEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(s[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            s[12],
            s[13],
            BinaryPrimitives.ReadUInt16LittleEndian(s[14..]));
    }
}
