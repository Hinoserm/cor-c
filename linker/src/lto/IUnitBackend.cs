using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// Compiler-owned code generation invoked without a linker/frontend dependency.
/// `archive` is the unit's IR archive as the link read and checked it from this
/// same object: a backend in the link's process need not read it again.
/// </summary>
public interface IUnitBackend
{
    ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null,
        LifetimeFacts? facts = null, IrArchive? archive = null);
}
