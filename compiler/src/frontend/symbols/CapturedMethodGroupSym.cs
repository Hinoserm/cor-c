#nullable enable
namespace Corsac.Lang;

public sealed record CapturedMethodGroupSym(FieldSymbol Holder, List<MethodSymbol> Methods) : Sym;
