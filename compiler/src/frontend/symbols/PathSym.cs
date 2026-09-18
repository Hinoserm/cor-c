#nullable enable
namespace Corsac.Lang;

/// <summary>A member PATH proved non-null -- see Binder.Path.</summary>
public sealed record PathSym(string Spelt) : Sym;
