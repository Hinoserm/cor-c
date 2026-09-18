namespace Corsac.Lang.Lto;

/// <summary>Relocation field offset of a compiler-certified zero-argument i32 call.</summary>
public sealed record DirectCall(int Offset, string Symbol);
