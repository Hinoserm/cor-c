using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using AstBlock = Corsac.Lang.Block;

/// <summary>
/// A METHOD'S SYNTAX AND BINDING, LET GO ONCE IT IS LOWERED.
///
/// Lowering ran with the whole unit's syntax and everything the binder said
/// about it alive from the first method to the last, beside the IR it was
/// making: a large unit's bound trees were the biggest thing alive at the end
/// of lowering, and every method's were kept although each method is lowered
/// once and its tree read by nothing after. The facts the binder keeps on the
/// nodes go with them; the rows of the tables keyed by node are taken out
/// (BindResult.Forget); and the body's statements are dropped from its block.
///
/// ONLY A BODY NOTHING WILL READ AGAIN. Some are, and those keep theirs:
/// - a constructor: every one of a type shares the statements its field
///   initialisers became (Binder.Initialisers), and a later one lowers them
///   again; and a name the compiler made (`$`), whose body may be built from
///   another's statements (a type's static initialiser takes its fields');
/// - an async method or an iterator, whose state machine's MoveNext is
///   lowered later from the same body (EmitKickoff, EmitIteratorKickoff);
/// - a body with a lambda, a local function, an await or a yield anywhere in
///   it: a closure's or a hoisted function's method is lowered later from
///   syntax inside this body;
/// - a method of a class the compiler made for a closure or a state machine
///   (TypeDecl.LocalOnly): its body is the lambda's own block, which the
///   method the lambda is written in walks again when it is lowered, and
///   which more than one symbol may be made over;
/// - a declaration more than one method symbol of its type has.
/// The block itself stays, empty: what asks whether a method HAS a body
/// (Emits, EqualsGuard, NoteUnlowered) still hears that it has.
/// </summary>
public sealed partial class Lowering
{
    private readonly List<Node> _released = new();
    private readonly Stack<Node> _releasing = new();

    private void ReleaseBody(MethodSymbol m, MethodDecl decl)
    {
        if (decl.Body is not AstBlock body || body.Iterator || m.Async || m.IsCtor || decl.IsCtor || decl.LocalCopy || decl.LocalGenerics.Count > 0
            || m.Name.Contains('$') || decl.Name.Contains('$') || m.Owner.Decl?.LocalOnly == true
            || _moveNext.ContainsKey(m) || _iterators.ContainsKey(m))
        {
            return;
        }
        int sharing = 0;
        foreach (MethodSymbol other in m.Owner.Methods)
            if (ReferenceEquals(other.Decl, decl) && ++sharing > 1) return;

        // EVERY NODE FIRST, and nothing forgotten until the whole body is
        // known to be one nothing will revisit.
        _released.Clear();
        _releasing.Clear();
        _releasing.Push(body);
        while (_releasing.TryPop(out Node? n))
        {
            if (n is LambdaExpr or AwaitExpr or YieldStmt || n is AstBlock { GenericLocals.Count: > 0 })
            {
                _released.Clear();
                _releasing.Clear();
                return;
            }
            _released.Add(n);
            switch (n)
            {
                case NewExpr made: Initialisers(made.Body); break;
                case WithExpr with: Initialisers(with.Body); break;
                case SwitchExpr choice: foreach (SwitchArm arm in choice.Arms) _released.Add(arm); break;
            }
            List<Node> children = Children(n);
            foreach (Node child in children) _releasing.Push(child);
            ReturnChildren(children);
        }

        foreach (Node n in _released) _b.Forget(n);
        _released.Clear();
        body.WritableStatements.Clear();
        body.WritableStatements.TrimExcess();
    }

    /// <summary>The elements of a pair of initialiser braces, as nodes the tables key by (InitField, InitAdder, InitIndexer).</summary>
    private void Initialisers(InitBody? braces)
    {
        if (braces is null) return;
        foreach (InitAssign init in braces.Inits)
        {
            _released.Add(init);
            Initialisers(init.Nested);
        }
        foreach (InitAdd add in braces.Adds) _released.Add(add);
        foreach (InitIndex index in braces.Indexes) _released.Add(index);
    }
}
