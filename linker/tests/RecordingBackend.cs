using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

internal sealed class RecordingBackend : IUnitBackend
{
    public List<string> Imports { get; } = new();
    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null)
    { Imports.AddRange(imports.Select(import => import.Symbol)); return original; }
}
