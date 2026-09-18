#nullable enable
namespace Corsac.Lang;

/// <summary>A named number. Not storage, so every use of one is the value itself.</summary>
/// <summary>
/// A NAME FOR A VALUE, which is the whole of what a const is.
///
/// <paramref name="Text"/> is set instead of <paramref name="Value"/> when the
/// value is a string: `const string name = "__object_equals";` is an ordinary
/// local declaration in this compiler's own lowering, and a string is not a
/// number to hold in the other field.
/// </summary>
public sealed record ConstSym(long Value, Type Type, string? Text = null) : Sym;
