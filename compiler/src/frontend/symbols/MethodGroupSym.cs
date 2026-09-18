#nullable enable
namespace Corsac.Lang;

public sealed record MethodGroupSym(List<MethodSymbol> Methods) : Sym;
