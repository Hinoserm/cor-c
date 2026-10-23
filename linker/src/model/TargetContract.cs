using System.Buffers.Binary;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

/// <summary>Native calling/TLS contract; assembly/type identity is separate metadata.</summary>
public sealed class TargetContract
{
    public const string SectionName = ".corsac.abi";
    public uint TlsModel { get; }
    public bool RequiresManagedLayouts { get; }
    public bool RequiresCodeGenerationContract { get; }
    /// <summary>
    /// Long mode: eight-byte pointers and the System V AMD64 convention,
    /// rather than the i386 image's four-byte pointers and cdecl with x87
    /// results. Two images of different modes cannot be linked together.
    /// </summary>
    public bool LongMode { get; }
    /// <summary>
    /// Every reference store made through the image's store sequences
    /// (--store-sequences): the stubs a kernel's handshake never stops a
    /// thread inside. One unit made without them, linked into an image made
    /// with them, stores with the barrier's test and the store as two
    /// instructions, and a thread sent to the collector's handshake between
    /// the two loses the reference it stores to the mark. Every unit of an
    /// image agrees, and a module agrees with its kernel.
    /// </summary>
    public bool StoreSequences { get; }
    public const uint I386Machine = 486, I386Convention = 1;
    /// <summary>The contract's bytes.</summary>
    public const int Size = 32;

    /// <summary>
    /// The TLS model of an object that makes no claim about one: hand-written
    /// assembly, which says what machine it is for (so a long-mode stub is
    /// not linked into an i386 image) and nothing about where a thread block
    /// is.
    /// </summary>
    public const uint NoTlsClaim = 3;

    /// <summary>
    /// A Windows image's (docs/WIN32-TARGET.md): the thread block in a TLS
    /// slot of the thread's TEB, read through FS; never mixed with a GS one.
    /// </summary>
    public const uint WindowsTlsSlot = 4;
    public const uint Amd64Machine = 0x8664, Amd64Convention = 2;
    public TargetContract(uint tlsModel, bool requiresManagedLayouts = false, bool requiresCodeGenerationContract = false, bool longMode = false, bool storeSequences = false)
    {
        LongMode = longMode;
        StoreSequences = storeSequences;
        if (tlsModel > WindowsTlsSlot) throw new ElfFormatException("unsupported TLS/platform contract");
        TlsModel = tlsModel;
        RequiresManagedLayouts = requiresManagedLayouts;
        RequiresCodeGenerationContract = requiresCodeGenerationContract;
    }
    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(s => s.Name == SectionName)) throw new ElfFormatException("duplicate target contract");
        byte[] bytes = new byte[Size];
        "CABI"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), LongMode ? 8u : 4u); // Pointer bytes.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), LongMode ? Amd64Machine : I386Machine);
        // i386 cdecl with x87 floating results, or System V AMD64 with SSE ones.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), LongMode ? Amd64Convention : I386Convention);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), TlsModel);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (RequiresManagedLayouts ? 1u : 0u) | (RequiresCodeGenerationContract ? 2u : 0u));
        // How its code was made: its reference stores through the store sequences.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), StoreSequences ? 1u : 0u);
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(bytes);
        obj.Sections.Add(section);
    }
    /// <summary>Whether an object says it is long-mode code: eight-byte pointers, System V AMD64.</summary>
    /// <summary>Whether an object was made for a Windows image (WindowsTlsSlot).</summary>
    public static bool IsWindows(ObjectFile obj)
    {
        foreach (Section s in obj.Sections)
        {
            if (s.Name == SectionName && s.Bytes.Count == Size)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(s.Bytes.ToArray().AsSpan(20)) == WindowsTlsSlot;
            }
        }
        return false;
    }

    public static bool IsLongMode(ObjectFile obj)
    {
        foreach (Section s in obj.Sections)
        {
            if (s.Name == SectionName && s.Bytes.Count == Size)
            {
                byte[] bytes = s.Bytes.ToArray();
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == 8
                    && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) == Amd64Machine;
            }
        }
        return false;
    }

    /// <summary>Whether a contract's code was made with the store sequences.</summary>
    public static bool MadeWithStoreSequences(byte[] contract) => (BinaryPrimitives.ReadUInt32LittleEndian(contract.AsSpan(28)) & 1) != 0;

    public static void Validate(IEnumerable<(string Name, ObjectFile Object)> inputs)
    {
        uint? model = null;
        uint? pointer = null;
        bool? sequences = null;
        string owner = "", pointerOwner = "", sequencesOwner = "";
        foreach (var input in inputs)
        {
            _ = X86CodeGenerationContract.Read(input.Object);
            Section[] contracts = input.Object.Sections.Where(s => s.Name == SectionName).ToArray();
            if (contracts.Length == 0) continue; // Neutral native/assembly objects.
            if (contracts.Length != 1) throw new ElfFormatException(input.Name + ": duplicate target contract");
            byte[] bytes = contracts[0].Bytes.ToArray();
            if (bytes.Length != Size || !bytes.AsSpan(0, 4).SequenceEqual("CABI"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 0)
                throw new ElfFormatException(input.Name + ": unsupported native ABI contract");
            uint bytesPerPointer = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            uint machine = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            uint convention = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            if (!(bytesPerPointer == 4 && machine == I386Machine && convention == I386Convention)
                && !(bytesPerPointer == 8 && machine == Amd64Machine && convention == Amd64Convention))
                throw new ElfFormatException(input.Name + ": unsupported native ABI contract");
            if (pointer is not null && bytesPerPointer != pointer)
                throw new LinkException(new[] { input.Name + ": a " + (bytesPerPointer * 8) + "-bit object cannot be linked with the "
                    + (pointer * 8) + "-bit " + pointerOwner });
            pointer = bytesPerPointer;
            pointerOwner = input.Name;
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
            uint required = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            if ((required & ~3u) != 0) throw new ElfFormatException(input.Name + ": unknown required ABI metadata");
            if ((required & 2) != 0 && X86CodeGenerationContract.Read(input.Object) is null)
                throw new ElfFormatException(input.Name + ": required CPU/FPU code-generation contract is missing");
            if ((required & 1) != 0 && !input.Object.Sections.Any(section => section.Name == ManagedLayoutContract.SectionName))
                throw new ElfFormatException(input.Name + ": required managed layout contract is missing");
            if (current > WindowsTlsSlot) throw new ElfFormatException(input.Name + ": unsupported TLS/platform contract");
            if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)) & ~1u) != 0) throw new ElfFormatException(input.Name + ": unknown code flags in its native ABI contract");
            // Assembly makes no claim: it stores through no barrier of the compiler's.
            if (current == NoTlsClaim) continue;
            bool made = MadeWithStoreSequences(bytes);
            if (sequences is not null && made != sequences)
                throw new LinkException(new[] { input.Name + ": its reference stores are made " + (made ? "through" : "without") + " the store sequences, and "
                    + sequencesOwner + "'s " + (made ? "without" : "through") + " them (compile every unit with --store-sequences, or none)" });
            sequences = made;
            sequencesOwner = input.Name;
            if (model is not null && current != model)
                throw new LinkException(new[] { input.Name + ": TLS/platform contract conflicts with " + owner });
            model = current;
            owner = input.Name;
        }
    }
}
