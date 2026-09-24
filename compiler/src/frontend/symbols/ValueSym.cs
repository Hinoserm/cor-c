#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A capture whose source is a value, evaluated where the closure is made: the
/// receiver of a method group, `workers[i].Run`, which C# evaluates once, when
/// the delegate is created, and not each time it is called.
/// </summary>
public sealed record ValueSym(Expr Value) : Sym;
