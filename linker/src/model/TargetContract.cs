using System.Buffers.Binary;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

/// <summary>Native calling/TLS contract; assembly/type identity is separate metadata.</summary>
public sealed class TargetContract
{
    public const string SectionName = ".corsac.abi";
    public uint TlsModel { get; }
    public bool RequiresManagedLayouts { get; }
    public TargetContract(uint tlsModel, bool requiresManagedLayouts = false)
    {
        if (tlsModel > 2) throw new ElfFormatException("unsupported TLS/platform contract");
        TlsModel = tlsModel;
        RequiresManagedLayouts = requiresManagedLayouts;
    }
    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(s => s.Name == SectionName)) throw new ElfFormatException("duplicate target contract");
        byte[] bytes = new byte[28];
        "CABI"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4); // Pointer bytes.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 486);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1); // i386 cdecl, x87 floating results.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), TlsModel);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), RequiresManagedLayouts ? 1u : 0u);
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(bytes);
        obj.Sections.Add(section);
    }
    public static void Validate(IEnumerable<(string Name, ObjectFile Object)> inputs)
    {
        uint? model = null;
        string owner = "";
        foreach (var input in inputs)
        {
            _ = X86CodeGenerationContract.Read(input.Object);
            Section[] contracts = input.Object.Sections.Where(s => s.Name == SectionName).ToArray();
            if (contracts.Length == 0) continue; // Neutral native/assembly objects.
            if (contracts.Length != 1) throw new ElfFormatException(input.Name + ": duplicate target contract");
            byte[] bytes = contracts[0].Bytes.ToArray();
            if (bytes.Length != 28 || !bytes.AsSpan(0, 4).SequenceEqual("CABI"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 3
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != 4
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != 486
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 1)
                throw new ElfFormatException(input.Name + ": unsupported native ABI contract");
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
            uint required = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
            if ((required & ~1u) != 0) throw new ElfFormatException(input.Name + ": unknown required ABI metadata");
            if ((required & 1) != 0 && !input.Object.Sections.Any(section => section.Name == ManagedLayoutContract.SectionName))
                throw new ElfFormatException(input.Name + ": required managed layout contract is missing");
            if (current > 2) throw new ElfFormatException(input.Name + ": unsupported TLS/platform contract");
            if (model is not null && current != model)
                throw new LinkException(new[] { input.Name + ": TLS/platform contract conflicts with " + owner });
            model = current;
            owner = input.Name;
        }
    }
}
