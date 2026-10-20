#nullable enable
namespace Corsac.Lang;

public sealed record ThisSym(Type Type) : Sym
{
    /// <summary>The context a shared struct's member is handed (Binder.StructContext), not the struct: what a lambda of it holds as $context.</summary>
    public bool Context { get; init; }
}
