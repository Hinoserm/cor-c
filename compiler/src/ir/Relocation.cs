#nullable enable

namespace Corsac.Lang.Ir;

public readonly record struct Relocation(int Offset, string Symbol, long Addend, RelocKind Kind);
