#nullable enable
namespace Corsac.Lang;

public sealed record CapturedFieldSym(FieldSymbol Holder, FieldSymbol Field) : Sym;
