#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A relocation inside a data item: the word at Offset holds the address of
/// Symbol plus Addend.
/// </summary>
public readonly record struct DataReloc(int Offset, string Symbol, long Addend);
