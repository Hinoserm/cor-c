#nullable enable
namespace Corsac.Lang;

/// <summary>A property with a body: reading it is a call, not a load.</summary>
public sealed record PropertyGetSym(MethodSymbol Getter) : Sym;
