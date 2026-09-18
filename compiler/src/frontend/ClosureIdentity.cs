using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang;

internal static class ClosureIdentity
{
    public static string Of(MethodSymbol method)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
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
