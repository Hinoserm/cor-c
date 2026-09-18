#nullable enable
namespace Corsac.Lang;

/// <summary>What a lambda was turned into: a class, its captured fields, and its Invoke.</summary>
public sealed record ClosureInfo(TypeSymbol Type, List<(FieldSymbol Field, Sym Source)> Captures, MethodSymbol Invoke);
