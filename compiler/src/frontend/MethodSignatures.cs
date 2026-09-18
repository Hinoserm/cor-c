namespace Corsac.Lang;

internal static class MethodSignatures
{
    public static bool SameType(Type left, Type right)
    {
        bool annotationOnly = left.IsReference || left.Prim is Prim.Any or Prim.Type;
        return left.Prim == right.Prim && ReferenceEquals(left.Symbol, right.Symbol)
            && (annotationOnly || left.Nullable == right.Nullable)
            && left.ArrayRank == right.ArrayRank && left.PointerDepth == right.PointerDepth
            && left.ParamName == right.ParamName && left.Args.Count == right.Args.Count
            && (left.Element is null ? right.Element is null : right.Element is not null && SameType(left.Element, right.Element))
            && left.Args.Zip(right.Args).All(pair => SameType(pair.First, pair.Second));
    }

    public static bool SameParameters(MethodSymbol left, MethodSymbol right)
        => left.Static == right.Static && left.TypeParams.Count == right.TypeParams.Count && left.Params.Count == right.Params.Count
            && left.Params.Zip(right.Params).All(pair => SameType(pair.First.Type, pair.Second.Type)
                && pair.First.ByRef == pair.Second.ByRef && pair.First.ReadOnly == pair.Second.ReadOnly);
}
