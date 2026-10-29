#nullable enable
namespace Corsac.Lang;

/// <summary>
/// C# 14'S NULL-CONDITIONAL ASSIGNMENT, and `??=`, as the parser and the
/// binder both build them: one rule for where a `?.` chain is, how it is cut
/// at its first link, and how `x ??= y` is held, so a statement (Parser.
/// Statement, the receiver in a local) and an expression (Binder, the
/// receiver a pattern's subject) mean the same thing.
///
/// `P?.A = B` as a statement is `if (P is not null) P.A = B;`; anywhere else
/// it is `(P is null) ? (T?)null : (P.A = B)` -- P evaluated once, B only
/// where P is not null -- and `a?.b ??= c` is `a.b ??= c` past the same test.
/// </summary>
internal static class NullConditionalAssignment
{
    /// <summary>
    /// Whether a target has a `?.` or `?[` along it. Through a `!`, which
    /// silences a warning and ends nothing (`a?.B!.C = v` is skipped where a
    /// is null); not into a group, which the parser holds whole (Parser's
    /// group, a PatternExpr), and which C# ends a chain at.
    /// </summary>
    public static bool Linked(Expr target) => target switch
    {
        MemberExpr m => m.NullConditional || Linked(m.Target),
        IndexExpr ix => ix.NullConditional || Linked(ix.Target),
        CallExpr call => Linked(call.Target),
        SuppressExpr s => Linked(s.Operand),
        _ => false,
    };

    /// <summary>
    /// The first `?.` or `?[` of a target, from the receiver out: what it is
    /// applied to, and the target with that link read through what
    /// <paramref name="holder"/> makes (a local's name, a pattern's subject)
    /// instead. Null when the target has none.
    /// </summary>
    public static (Expr Receiver, Expr Through)? Unlinked(Expr target, Func<Node, Expr> holder)
    {
        switch (target)
        {
            case MemberExpr m:
            {
                if (Unlinked(m.Target, holder) is (Expr receiver, Expr inner))
                {
                    MemberExpr kept = new() { Target = inner, Name = m.Name, NullConditional = m.NullConditional, Line = m.Line, Col = m.Col };
                    kept.WritableTypeArgs.AddRange(m.TypeArgs);
                    return (receiver, kept);
                }
                if (!m.NullConditional) return null;
                MemberExpr through = new() { Target = holder(m), Name = m.Name, Line = m.Line, Col = m.Col };
                through.WritableTypeArgs.AddRange(m.TypeArgs);
                return (m.Target, through);
            }
            case IndexExpr ix:
            {
                Expr? receiver = null, inner = null;
                if (Unlinked(ix.Target, holder) is (Expr r, Expr i)) { receiver = r; inner = i; }
                else if (ix.NullConditional)
                {
                    receiver = ix.Target;
                    inner = holder(ix);
                }
                if (receiver is null) return null;
                IndexExpr kept = new() { Target = inner!, NullConditional = receiver == ix.Target ? false : ix.NullConditional, Line = ix.Line, Col = ix.Col };
                kept.Args.AddRange(ix.Args);
                return (receiver, kept);
            }
            case CallExpr call when Unlinked(call.Target, holder) is (Expr receiver, Expr inner):
            {
                CallExpr kept = new() { Target = inner, Line = call.Line, Col = call.Col };
                kept.Args.AddRange(call.Args);
                kept.WritableArgNames.AddRange(call.ArgNames);
                return (receiver, kept);
            }
            case SuppressExpr s when Unlinked(s.Operand, holder) is (Expr receiver, Expr inner):
                return (receiver, new SuppressExpr { Operand = inner, Line = s.Line, Col = s.Col });
            default:
                return null;
        }
    }

    /// <summary>
    /// `left ??= right`, held as `left = left ?? right` and marked so
    /// (AssignExpr.Coalescing). WHAT THE TARGET IS READ THROUGH IS EVALUATED
    /// ONCE, as C# has it: `Use(n).Loads ??= new()` calls Use once, and the
    /// field of what it answered is read and written. The object -- and an
    /// element's index -- is put somewhere first (SubjectExpr), and the
    /// target names it there. A target with a `?.` or `?[` is left whole, to
    /// be cut at its link (Parser.Statement, Binder's null-conditional
    /// assignment) and built again on what is past it.
    /// </summary>
    public static Expr Coalescing(Expr left, Expr right, string file, int line, int col)
    {
        AssignExpr Held(Expr target) => new()
        {
            Target = target,
            Value = new BinaryExpr { Op = BinOp.Coalesce, Left = target, Right = right, Line = line, Col = col },
            Coalescing = true,
            Line = line, Col = col,
        };
        if (Parser.Rereadable(left) || Linked(left)) return Held(left);

        if (left is MemberExpr { Target: { } owner } field)
        {
            MemberExpr through = new() { Target = new SubjectExpr { Line = line, Col = col }, Name = field.Name, Line = field.Line, Col = field.Col };
            return new PatternExpr { Subject = owner, Test = Held(through), Line = line, Col = col };
        }
        if (left is IndexExpr { Target: { } array, Args.Count: 1 } element)
        {
            IndexExpr through = new() { Target = new SubjectExpr { Outer = 1, Line = line, Col = col }, Line = element.Line, Col = element.Col };
            through.Args.Add(new SubjectExpr { Line = line, Col = col });
            PatternExpr index = new() { Subject = element.Args[0], Test = Held(through), Line = line, Col = col };
            return new PatternExpr { Subject = array, Test = index, Line = line, Col = col };
        }
        throw new CompileError(file, line, col, "the left of '??=' must be a name, a field or an element");
    }
}
