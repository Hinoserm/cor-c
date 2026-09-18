#nullable enable

namespace Corsac.Lang.Ir;

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
