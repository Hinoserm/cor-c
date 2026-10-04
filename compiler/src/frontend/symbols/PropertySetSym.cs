#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A property with a setter and no getter: it can be assigned and not read
/// (CS0154). `Holder` is the captured `this` a local function reaches it
/// through, as CapturedPropertyGetSym's.
/// </summary>
public sealed record PropertySetSym(MethodSymbol Setter, FieldSymbol? Holder = null) : Sym;
