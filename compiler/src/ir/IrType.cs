#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// The type of a value in the IR. Only what a machine register can hold:
/// everything with structure is an address, and an address is I32 on a
/// 32-bit target and I64 on a 64-bit one -- see <see cref="IrTypes.Word"/>.
/// </summary>
public enum IrType : byte
{
    Void,
    I32,
    I64,
    F32,
    F64,
}
