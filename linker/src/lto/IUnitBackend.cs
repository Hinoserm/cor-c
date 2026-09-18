using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>Compiler-owned code generation invoked without a linker/frontend dependency.</summary>
public interface IUnitBackend
{
    ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports);
}
