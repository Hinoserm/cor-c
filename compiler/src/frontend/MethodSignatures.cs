namespace Corsac.Lang;

internal static class MethodSignatures
{
    public static bool SameType(Type left, Type right)
        => SameType(left, right, null, null);

    private static bool SameType(Type left, Type right, MethodSymbol? leftMethod, MethodSymbol? rightMethod)
    {
        bool annotationOnly = left.IsReference || left.Prim is Prim.Any or Prim.Type;
        int leftParameter = left.ParamName is null ? -1 : leftMethod?.TypeParams.IndexOf(left.ParamName) ?? -1;
        int rightParameter = right.ParamName is null ? -1 : rightMethod?.TypeParams.IndexOf(right.ParamName) ?? -1;
        bool sameParameter = leftParameter >= 0 || rightParameter >= 0
            ? leftParameter >= 0 && leftParameter == rightParameter
            : left.ParamName == right.ParamName;
        return left.Prim == right.Prim && ReferenceEquals(left.Symbol, right.Symbol)
            && (annotationOnly || left.Nullable == right.Nullable)
            && left.ArrayRank == right.ArrayRank && left.PointerDepth == right.PointerDepth
            && sameParameter && left.Args.Count == right.Args.Count
            && (left.Element is null ? right.Element is null : right.Element is not null && SameType(left.Element, right.Element, leftMethod, rightMethod))
            && (left.Pointee is null ? right.Pointee is null : right.Pointee is not null && SameType(left.Pointee, right.Pointee, leftMethod, rightMethod))
            && left.Args.Zip(right.Args).All(pair => SameType(pair.First, pair.Second, leftMethod, rightMethod));
    }

    public static bool SameParameters(MethodSymbol left, MethodSymbol right)
        => left.Static == right.Static && left.TypeParams.Count == right.TypeParams.Count && left.Params.Count == right.Params.Count
            && left.Params.Zip(right.Params).All(pair => SameType(pair.First.Type, pair.Second.Type, left, right)
                && pair.First.ByRef == pair.Second.ByRef && pair.First.ReadOnly == pair.Second.ReadOnly);

    public static bool Implements(MethodSymbol implementation, MethodSymbol contract)
        => SameParameters(implementation, contract)
            && SameType(implementation.Returns, contract.Returns, implementation, contract);
}
