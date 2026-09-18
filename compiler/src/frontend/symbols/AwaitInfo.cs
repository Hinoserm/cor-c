#nullable enable
namespace Corsac.Lang;

/// <summary>The awaiter pattern for one await: GetAwaiter on the operand, then IsCompleted, OnCompleted and GetResult on the awaiter.</summary>
public sealed record AwaitInfo(MethodSymbol GetAwaiter, MethodSymbol IsCompleted, MethodSymbol OnCompleted, MethodSymbol GetResult);
