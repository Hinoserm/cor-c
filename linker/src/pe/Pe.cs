#nullable enable
namespace Corsac.Lang.Pe;

/// <summary>
/// The numbers of Microsoft's PE/COFF format that the image writer uses, as
/// the specification names them (pecoff.docx, "Image Only"). A number here is
/// the format's, not a choice.
/// </summary>
public static class PeFormat
{
    /// <summary>Where the PE signature is, in the MS-DOS header: e_lfanew.</summary>
    public const int NewHeaderPointer = 0x3C;

    /// <summary>The MS-DOS header and its stub program: the PE header follows at this offset.</summary>
    public const int DosBytes = 0x80;

    public const int CoffHeaderBytes = 20;
    /// <summary>PE32's optional header with its 16 data directories; PE32+'s is OptionalHeader64Bytes.</summary>
    public const int OptionalHeaderBytes = 224;

    /// <summary>
    /// PE32+'s: no BaseOfData, an 8-byte ImageBase and 8-byte stack and heap
    /// sizes, so the data directories start at 112 and the header is 240.
    /// </summary>
    public const int OptionalHeader64Bytes = 240;
    public const int SectionHeaderBytes = 40;
    public const int DataDirectoryCount = 16;

    /// <summary>PE32's magic, beside PE32+'s 0x20B.</summary>
    public const ushort Pe32Magic = 0x10B;

    /// <summary>PE32+'s magic: an image for a 64-bit machine, its addresses eight bytes.</summary>
    public const ushort Pe32PlusMagic = 0x20B;

    /// <summary>The sections' alignment in memory: a page, as every Windows wants.</summary>
    public const uint SectionAlignment = 0x1000;

    /// <summary>The sections' alignment in the file: the smallest the format allows, a disc sector.</summary>
    public const uint FileAlignment = 0x200;

    /// <summary>
    /// The base an executable is linked to. Every 32-bit Windows maps an
    /// executable here when it can; Windows 95 always does (it never moves
    /// an executable), and Vista's ASLR moves it with the image's .reloc.
    /// </summary>
    public const uint DefaultImageBase = 0x400000;

    /// <summary>
    /// The base a DLL is linked to when it names none: Microsoft's linker's
    /// default. Where another DLL is there already, the loader moves this one
    /// by its .reloc, as every Windows does.
    /// </summary>
    public const uint DefaultDllBase = 0x10000000;

    /// <summary>
    /// The bases a 64-bit image is linked to when it names none: Microsoft's
    /// linker's and LLD's (getDefaultImageBase), above four gigabytes so that
    /// a pointer truncated to 32 bits faults rather than works by chance.
    /// ASLR moves either by its .reloc (DynamicBase, HighEntropyVa).
    /// </summary>
    public const ulong DefaultImageBase64 = 0x140000000, DefaultDllBase64 = 0x180000000;

    /// <summary>
    /// The subsystem (and operating system) version a 64-bit image names:
    /// 5.2, Windows XP x64 and Server 2003 x64, the first 64-bit Windows,
    /// which refuses a higher one. MinGW's linker writes the same.
    /// </summary>
    public const ushort Subsystem64Major = 5, Subsystem64Minor = 2;

    /// <summary>A RUNTIME_FUNCTION in .pdata: begin, end and unwind information, as RVAs.</summary>
    public const uint RuntimeFunctionBytes = 12;

    /// <summary>
    /// The size of the load configuration directory's data directory entry,
    /// whatever the structure's own Size says: 64 for an x86 image, "for
    /// compatibility with Windows XP and earlier versions of Windows".
    /// </summary>
    public const uint LoadConfigDirectoryBytes = 64;

    /// <summary>
    /// IMAGE_LOAD_CONFIG_DIRECTORY32 up to and including SEHandlerCount: the
    /// fields an image with a SafeSEH table has and nothing after them
    /// (GuardCFCheckFunctionPointer and on are Windows 8.1's and absent).
    /// </summary>
    public const uint LoadConfigBytes = 0x48;

    /// <summary>IMAGE_TLS_DIRECTORY32: four VAs, the zero fill and the characteristics.</summary>
    public const uint TlsDirectoryBytes = 24;

    /// <summary>IMAGE_IMPORT_DESCRIPTOR.</summary>
    public const uint ImportDescriptorBytes = 20;

    /// <summary>IMAGE_EXPORT_DIRECTORY.</summary>
    public const uint ExportDirectoryBytes = 40;

    /// <summary>A base relocation block's header: the page's RVA and the block's size.</summary>
    public const uint BaseRelocationBlockBytes = 8;

    /// <summary>The high bit of a 32-bit lookup entry: an import by ordinal.</summary>
    public const uint ImportByOrdinal = 0x80000000;

    /// <summary>PE32+'s: bit 63 of a 64-bit lookup entry.</summary>
    public const ulong ImportByOrdinal64 = 0x8000000000000000;

    /// <summary>The high bit of a resource directory entry's offset: a subdirectory, not data.</summary>
    public const uint ResourceSubdirectory = 0x80000000;

    public static uint AlignUp(uint value, uint align) => checked((value + align - 1) / align * align);
}

/// <summary>IMAGE_FILE_MACHINE_*: the machines this writer makes images for.</summary>
public enum PeMachine : ushort
{
    I386 = 0x14C,
    Amd64 = 0x8664,
}

/// <summary>IMAGE_FILE_*: the COFF header's characteristics.</summary>
[Flags]
public enum PeCharacteristics : ushort
{
    None = 0,
    RelocsStripped = 0x0001,
    ExecutableImage = 0x0002,
    LineNumsStripped = 0x0004,
    LocalSymsStripped = 0x0008,
    LargeAddressAware = 0x0020,
    Machine32Bit = 0x0100,
    DebugStripped = 0x0200,
    Dll = 0x2000,
}

/// <summary>IMAGE_SUBSYSTEM_*: what the loader gives the program, a console or none.</summary>
public enum PeSubsystem : ushort
{
    WindowsGui = 2,
    WindowsConsole = 3,
}

/// <summary>IMAGE_DLLCHARACTERISTICS_*: what a modern loader may do with the image.</summary>
[Flags]
public enum PeDllCharacteristics : ushort
{
    None = 0,
    /// <summary>ASLR may put a 64-bit image anywhere in the 64-bit space (needs DynamicBase and LargeAddressAware).</summary>
    HighEntropyVa = 0x0020,
    /// <summary>ASLR may move it: the image has .reloc.</summary>
    DynamicBase = 0x0040,
    /// <summary>DEP may stay on: no code is written into data.</summary>
    NxCompatible = 0x0100,
    /// <summary>No structured exception handler anywhere in the image.</summary>
    NoSeh = 0x0400,
    /// <summary>No Terminal Server compatibility shims.</summary>
    TerminalServerAware = 0x8000,
}

/// <summary>IMAGE_SCN_*: a section's contents and what its pages may do.</summary>
[Flags]
public enum PeSectionFlags : uint
{
    None = 0,
    Code = 0x00000020,
    InitializedData = 0x00000040,
    UninitializedData = 0x00000080,
    Discardable = 0x02000000,
    Execute = 0x20000000,
    Read = 0x40000000,
    Write = 0x80000000,
}

/// <summary>IMAGE_DIRECTORY_ENTRY_*: the optional header's data directories, by index.</summary>
public enum PeDirectory
{
    Export = 0,
    Import = 1,
    Resource = 2,
    Exception = 3,
    Security = 4,
    BaseRelocation = 5,
    Debug = 6,
    Architecture = 7,
    GlobalPointer = 8,
    Tls = 9,
    LoadConfig = 10,
    BoundImport = 11,
    ImportAddressTable = 12,
    DelayImport = 13,
    ComDescriptor = 14,
}

/// <summary>IMAGE_REL_BASED_*: the kinds of fix-up an image's .reloc holds.</summary>
public enum PeBaseRelocation : ushort
{
    /// <summary>Padding, to keep a block a multiple of four bytes.</summary>
    Absolute = 0,
    /// <summary>The 32-bit word at the place gets the whole difference between the bases.</summary>
    HighLow = 3,
    /// <summary>PE32+'s: the 64-bit word at the place gets the whole difference.</summary>
    Dir64 = 10,
}

/// <summary>The predefined resource types (RT_*) an image carries.</summary>
public enum PeResourceType : ushort
{
    Icon = 3,
    GroupIcon = 14,
    Version = 16,
    Manifest = 24,
}
