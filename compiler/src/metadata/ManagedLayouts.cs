using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;

namespace Corsac.Lang;

public static class ManagedLayouts
{
    public static void Attach(ObjectFile obj, BindResult bound)
    {
        IEnumerable<ManagedTypeLayout> Records()
        {
            foreach (TypeSymbol type in bound.Types.Values.Distinct().Where(type => type.Decl is { Specialised: false, LocalOnly: false, TypeParams.Count: 0 }))
            {
                using MemoryStream stream = new();
                using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
                void TypeName(Type value)
                {
                    writer.Write(value.ToString());
                    writer.Write(value.Symbol?.Key ?? "");
                }
                writer.Write((int)type.Kind); writer.Write(type.InstanceSize); writer.Write(type.Depth);
                writer.Write(type.Decl?.IsDelegate == true);
                writer.Write((int)(type.Decl?.Mods ?? Mods.None));
                writer.Write(type.Base?.Key ?? "");
                foreach (TypeSymbol face in type.Interfaces.OrderBy(face => face.Key, StringComparer.Ordinal)) writer.Write(face.Key);
                writer.Write("");
                foreach (FieldSymbol field in type.Fields.Where(field => !field.Static).OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    writer.Write(field.Name); TypeName(field.Type);
                    writer.Write(field.Static); writer.Write(field.Static ? 0 : field.Offset);
                    writer.Write(field.Boxed); writer.Write(field.Volatile); writer.Write(field.Required);
                }
                writer.Write("");
                foreach (var implementation in type.InterfaceImplementations.OrderBy(pair => pair.Key))
                { writer.Write(implementation.Key); writer.Write(Lowering.Label(implementation.Value)); }
                writer.Write(-1);
                foreach (MethodSymbol method in type.Methods.Where(method => method.VtableSlot >= 0 && method.Decl?.LocalCopy != true)
                    .OrderBy(method => method.VtableSlot).ThenBy(method => method.Name, StringComparer.Ordinal)
                    .ThenBy(method => method.Signature, StringComparer.Ordinal))
                {
                    writer.Write(method.Name); writer.Write(method.VtableSlot); TypeName(method.Returns);
                    writer.Write(method.Static); writer.Write(method.IsCtor); writer.Write(method.Abstract);
                    writer.Write(method.Params.Count);
                    foreach (ParamSymbol parameter in method.Params)
                    { TypeName(parameter.Type); writer.Write(parameter.ByRef); writer.Write(parameter.ReadOnly); }
                }
                if (Environment.GetEnvironmentVariable("CORC_DUMP_LAYOUT") is string want && want == type.Key)
                {
                    Console.Error.WriteLine("layout " + type.Key + " kind=" + (int)type.Kind + " size=" + type.InstanceSize
                        + " depth=" + type.Depth + " base=" + (type.Base?.Key ?? "")
                        + " interfaces=[" + string.Join(",", type.Interfaces.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key)) + "]"
                        + " impls=[" + string.Join(",", type.InterfaceImplementations.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=>" + Lowering.Label(pair.Value))) + "]");
                    foreach (MethodSymbol method in type.Methods.Where(m => m.VtableSlot >= 0).OrderBy(m => m.VtableSlot))
                        Console.Error.WriteLine("  slot " + method.VtableSlot + " " + method.Name + "/" + method.Params.Count);
                }
                yield return new ManagedTypeLayout("type:" + type.Key, SHA256.HashData(stream.ToArray()));
                foreach (FieldSymbol field in type.Fields.Where(field => field.Static))
                {
                    stream.SetLength(0); stream.Position = 0;
                    TypeName(field.Type); writer.Write(field.Volatile); writer.Write(field.Required);
                    yield return new ManagedTypeLayout("field:" + type.Key + "." + field.Name, SHA256.HashData(stream.ToArray()));
                }
                foreach (MethodSymbol method in type.Methods.Where(method => method.Decl?.LocalCopy != true && method.TypeParams.Count == 0))
                {
                    stream.SetLength(0); stream.Position = 0;
                    TypeName(method.Returns); writer.Write(method.Static); writer.Write(method.IsCtor);
                    writer.Write((int)(method.Decl?.Mods ?? Mods.None)); writer.Write(method.Params.Count);
                    foreach (ParamSymbol parameter in method.Params)
                    { TypeName(parameter.Type); writer.Write(parameter.ByRef); writer.Write(parameter.ReadOnly); }
                    yield return new ManagedTypeLayout("method:" + Lowering.Label(method), SHA256.HashData(stream.ToArray()));
                }
            }
        }
        ManagedLayoutContract.Attach(obj, Records());
    }
}
