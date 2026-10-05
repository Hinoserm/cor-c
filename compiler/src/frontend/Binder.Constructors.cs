#nullable enable
namespace Corsac.Lang;

public sealed partial class Binder
{
    // Check supplied arguments before inserting defaults: a same-arity
    // overload with unrelated types must not suppress an applicable optional
    // overload. Named arguments retain their original evaluation order.
    private void NormalizeConstructorArguments(NewExpr expression, List<MethodSymbol> methods, List<Type> types)
    {
        bool named = expression.ArgNames.Any(n => n is not null);
        if (!named && methods.Any(m => m.IsCtor && m.Params.Count == types.Count
            && types.Where((a, i) => !Fits(a, m.Params[i].Type, expression.Args[i])).Count() == 0))
            return;
        MethodSymbol? best = null;
        int[]? bestSlots = null;
        int bestExact = -1;
        int bestDefaults = int.MaxValue;
        foreach (MethodSymbol candidate in methods)
        {
            if (!candidate.IsCtor || candidate.Params.Count < types.Count) continue;
            int[] slots = new int[types.Count];
            bool[] used = new bool[candidate.Params.Count];
            bool fits = true;
            int exact = 0;
            for (int i = 0; i < types.Count; i++)
            {
                string? name = i < expression.ArgNames.Count ? expression.ArgNames[i] : null;
                int slot = name is null ? i : candidate.Params.FindIndex(p => p.Name == name);
                if (slot < 0 || slot >= used.Length || used[slot]
                    || !Fits(types[i], candidate.Params[slot].Type, expression.Args[i]))
                { fits = false; break; }
                slots[i] = slot;
                used[slot] = true;
                if (types[i].Equals(candidate.Params[slot].Type)) exact++;
            }
            for (int i = 0; i < used.Length && fits; i++)
                if (!used[i] && candidate.Decl?.Params.ElementAtOrDefault(i)?.Default is null)
                    fits = false;
            int defaults = candidate.Params.Count - types.Count;
            if (!fits || exact < bestExact || (exact == bestExact && defaults >= bestDefaults)) continue;
            best = candidate;
            bestSlots = slots;
            bestExact = exact;
            bestDefaults = defaults;
        }
        if (best is null || bestSlots is null) return; // expanded params handled by the caller
        if (!named && bestDefaults == 0) return;
        Expr?[] ordered = new Expr?[best.Params.Count];
        Type[] orderedTypes = new Type[best.Params.Count];
        List<int> evaluation = new();
        int[] from = new int[best.Params.Count];
        Array.Fill(from, -1);
        for (int i = 0; i < bestSlots.Length; i++)
        {
            int slot = bestSlots[i];
            ordered[slot] = expression.Args[i];
            orderedTypes[slot] = types[i];
            evaluation.Add(slot);
            from[slot] = i;
        }
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] is not null) continue;
            Expr value = CallerValue(best.Decl!.Params[i], best.Decl.Params, expression.Line,
                             k => from[k] < 0 ? null : SpanText(expression.Spans, expression.Source, from[k] + 1))
                         ?? Written(best, best.Decl.Params[i]);
            ordered[i] = value;
            orderedTypes[i] = CheckExpr(value);
            evaluation.Add(i);
        }
        expression.Args.Clear();
        expression.Args.AddRange(ordered!);
        expression.WritableArgNames.Clear();
        if (named)
        {
            expression.WritableArgumentOrder.Clear();
            expression.WritableArgumentOrder.AddRange(evaluation);
        }
        types.Clear();
        types.AddRange(orderedTypes);
    }
}
