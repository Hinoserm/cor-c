using System.Buffers.Binary;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Ir;

/// <summary>Native calling/TLS contract; assembly/type identity is separate metadata.</summary>
public sealed class TargetContract
{
    public const string SectionName = ".corsac.abi";
    public uint TlsModel { get; }
    public TargetContract(uint tlsModel)
    {
        if (tlsModel > 2) throw new ElfFormatException("unsupported TLS/platform contract");
        TlsModel = tlsModel;
    }
    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(s => s.Name == SectionName)) throw new ElfFormatException("duplicate target contract");
        byte[] bytes = new byte[24];
        "CABI"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4); // Pointer bytes.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 486);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1); // i386 cdecl, x87 floating results.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), TlsModel);
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
            Section[] contracts = input.Object.Sections.Where(s => s.Name == SectionName).ToArray();
            if (contracts.Length == 0) continue; // Neutral native/assembly objects.
            if (contracts.Length != 1) throw new ElfFormatException(input.Name + ": duplicate target contract");
            byte[] bytes = contracts[0].Bytes.ToArray();
            if (bytes.Length != 24 || !bytes.AsSpan(0, 4).SequenceEqual("CABI"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != 4
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != 486
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 1)
                throw new ElfFormatException(input.Name + ": unsupported native ABI contract");
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
            if (current > 2) throw new ElfFormatException(input.Name + ": unsupported TLS/platform contract");
            if (model is not null && current != model)
                throw new LinkException(new[] { input.Name + ": TLS/platform contract conflicts with " + owner });
            model = current;
            owner = input.Name;
        }
    }
}
