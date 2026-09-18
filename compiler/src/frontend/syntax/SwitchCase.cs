#nullable enable
namespace Corsac.Lang;

public sealed class SwitchCase : Node
{
    /// <summary>
    /// The whole label, as a condition over the switch subject. Null for
    /// <c>default</c>, which is the only label that asks nothing.
    ///
    /// ONE FIELD, BECAUSE A CASE LABEL IS A PATTERN AND NOTHING ELSE. This was
    /// five fields -- Value, Type, Binding, Property and Accepts -- plus a
    /// guard, plus three side tables in the binder, plus eighty lines of its
    /// own code generation. All of it was a SECOND, SMALLER pattern language
    /// that had been reimplemented here: one member only, constants only, no
    /// nesting, no `and`, no relational patterns, no paths. The comment
    /// admitting that said "the rest of the pattern language can follow without
    /// changing any of this", and the rest of the pattern language could not,
    /// because this was not the pattern language.
    ///
    /// So `case MemberExpr { Target: NameExpr type } m:` -- an ordinary line in
    /// this compiler's own Binder.cs -- was a syntax error, while the identical
    /// test written as `is` was fine.
    ///
    /// Now the label is parsed by the same function `is` uses and checked and
    /// emitted as the ordinary boolean expression it is, so every shape of
    /// pattern works here the moment it works anywhere. The `when` guard folds
    /// in as an `&amp;&amp;`, which is exactly its meaning: it is tested after
    /// the pattern matched, and failing it falls through to the next label
    /// rather than out of the switch.
    /// </summary>
    public Expr? Pattern { get; init; }

    public List<Stmt> Body { get; } = new();
}
