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
    public const uint I386Machine = 486, I386Convention = 1;

    /// <summary>
    /// The TLS model of an object that makes no claim about one: hand-written
    /// assembly, which says what machine it is for (so a long-mode stub is
    /// not linked into an i386 image) and nothing about where a thread block
    /// is.
    /// </summary>
    public const uint NoTlsClaim = 3;
    public const uint Amd64Machine = 0x8664, Amd64Convention = 2;
    public TargetContract(uint tlsModel, bool requiresManagedLayouts = false, bool requiresCodeGenerationContract = false, bool longMode = false)
    {
        LongMode = longMode;
        if (tlsModel > NoTlsClaim) throw new ElfFormatException("unsupported TLS/platform contract");
        TlsModel = tlsModel;
        RequiresManagedLayouts = requiresManagedLayouts;
        RequiresCodeGenerationContract = requiresCodeGenerationContract;
    }
    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(s => s.Name == SectionName)) throw new ElfFormatException("duplicate target contract");
        byte[] bytes = new byte[28];
        "CABI"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), LongMode ? 8u : 4u); // Pointer bytes.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), LongMode ? Amd64Machine : I386Machine);
        // i386 cdecl with x87 floating results, or System V AMD64 with SSE ones.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), LongMode ? Amd64Convention : I386Convention);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), TlsModel);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (RequiresManagedLayouts ? 1u : 0u) | (RequiresCodeGenerationContract ? 2u : 0u));
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(bytes);
        obj.Sections.Add(section);
    }
    /// <summary>Whether an object says it is long-mode code: eight-byte pointers, System V AMD64.</summary>
    public static bool IsLongMode(ObjectFile obj)
    {
        foreach (Section s in obj.Sections)
        {
            if (s.Name == SectionName && s.Bytes.Count == 28)
            {
                byte[] bytes = s.Bytes.ToArray();
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == 8
                    && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) == Amd64Machine;
            }
        }
        return false;
    }

    public static void Validate(IEnumerable<(string Name, ObjectFile Object)> inputs)
    {
        uint? model = null;
        uint? pointer = null;
        string owner = "", pointerOwner = "";
        foreach (var input in inputs)
        {
            _ = X86CodeGenerationContract.Read(input.Object);
            Section[] contracts = input.Object.Sections.Where(s => s.Name == SectionName).ToArray();
            if (contracts.Length == 0) continue; // Neutral native/assembly objects.
            if (contracts.Length != 1) throw new ElfFormatException(input.Name + ": duplicate target contract");
            byte[] bytes = contracts[0].Bytes.ToArray();
            if (bytes.Length != 28 || !bytes.AsSpan(0, 4).SequenceEqual("CABI"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 3)
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
            if (current > NoTlsClaim) throw new ElfFormatException(input.Name + ": unsupported TLS/platform contract");
            if (current == NoTlsClaim) continue;
            if (model is not null && current != model)
                throw new LinkException(new[] { input.Name + ": TLS/platform contract conflicts with " + owner });
            model = current;
            owner = input.Name;
        }
    }
}
