#nullable enable
namespace Corsac.Lang;

public sealed record CapturedPropertyGetSym(FieldSymbol Holder, MethodSymbol Getter) : Sym;
