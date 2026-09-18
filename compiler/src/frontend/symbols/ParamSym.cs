#nullable enable
namespace Corsac.Lang;

public sealed record ParamSym(int Index, Type Type, string Name, bool ByRef = false,
                             bool ReadOnly = false) : Sym;
