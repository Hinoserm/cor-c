using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang;

internal static class ClosureIdentity
{
    /// <summary>
    /// A method group's closure class's identity: the method, and the
    /// delegate type it is converted to -- one class implements one delegate's
    /// Invoke, and a Func and a Predicate of one method shared the first one
    /// made, so the second was called through a slot it does not have.
    /// </summary>
    public static string Of(MethodSymbol method, string? delegateKey = null)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        if (delegateKey is not null) writer.Write(delegateKey);
        writer.Write(method.Owner.Key); writer.Write(method.Name); writer.Write(method.TypeParams.Count);
        writer.Write(method.Static); writer.Write(method.IsCtor); writer.Write(method.Params.Count);
        foreach (ParamSymbol parameter in method.Params)
        {
            writer.Write(parameter.Type.ToString()); writer.Write(parameter.Type.Symbol?.Key ?? "");
            writer.Write(parameter.ByRef); writer.Write(parameter.ReadOnly);
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
