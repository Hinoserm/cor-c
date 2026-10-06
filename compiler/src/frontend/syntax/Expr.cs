#nullable enable
namespace Corsac.Lang;

// ---- expressions ------------------------------------------------------

public abstract class Expr : Node
{
    /// <summary>
    /// A lambda's or a method group's NATURAL TYPE (C# 10), where nothing
    /// says which delegate it is -- `var f = (int x) => x;`, `object o =
    /// Twice;` -- as the checker worked it out and spelt it, so the next
    /// expansion makes it when no source names it (Binder.NaturalTypes).
    /// </summary>
    /// KEPT WITH THE RARER FACTS (ExprFacts), which outlive the binding that
    /// wrote the rest: a field on every expression was eight bytes on half a
    /// million of them, held to the end of a unit, for the few that are a
    /// lambda or a method group with nothing to say what delegate it is.
    public TypeRef? NaturalType
    {
        get => Facts?.Natural;
        set
        {
            if (value is not null) (Facts ??= new ExprFacts()).Natural = value;
            else if (Facts is { } facts) facts.Natural = null;
        }
    }

    // What the checker found this expression's type to be, and which binding
    // found it (ExprTypes): read back only by that binding.
    internal Type? BoundType;
    internal int BoundBy;
    // And what a name or a member access resolved to (ExprSyms).
    internal Sym? BoundSym;
    internal int BoundSymBy;
    // The rarer facts a binding keeps about an expression (ExprFacts): made
    // only for the expressions that have any, so one reference is all the
    // rest of them pay.
    internal ExprFacts? Facts;
}
