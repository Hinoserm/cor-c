#nullable enable
namespace Corsac.Lang;

/// <summary>
/// AN EXPRESSION IN A SHARED GENERIC COPY THAT ASKS WHAT ITS TYPE ARGUMENT
/// IS: `new T[n]`, `typeof(T)`, `x is T`, `x as T`. The copy serves every
/// reference type argument, and its T is a machine word; what T is for the
/// object at hand is in that object's descriptor (Lowering.CanonEntry), read
/// through CanonSelf -- `this`, written in by the monomorphiser so that a
/// lambda captures it as it captures any other. CanonSlot is the entry: two
/// per type parameter, the argument's descriptor and its array's.
/// </summary>
public interface ICanonSlot
{
    int CanonSlot { get; set; }
    Expr? CanonSelf { get; set; }
}
