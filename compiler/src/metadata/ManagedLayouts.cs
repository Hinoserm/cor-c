using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Lower;

namespace Corsac.Lang;

public static class ManagedLayouts
{
    public static void Attach(ObjectFile obj, BindResult bound, bool library = true)
        => ManagedLayoutContract.Attach(obj, Capture(bound, library));

    /// <summary>
    /// The records, taken while the binding is at hand: a unit's syntax and
    /// symbols can then be let go before it is optimised and generated,
    /// and the records written into the object at the end.
    /// </summary>
    public static List<ManagedTypeLayout> Capture(BindResult bound, bool library = true)
    {
        // WHAT THIS UNIT HAS AN OPINION ABOUT. Every type the binder
        // materialised used to be described here, which made the section
        // depend on which declarations happened to be loaded rather than on
        // which were used: importing a header nobody asked about changed the
        // object file. Two units can only disagree about a type they both
        // describe, and a unit that never reached for a type has nothing to
        // disagree with, so the records follow use.
        //
        // A type this unit DEFINES is always described -- that is the side of
        // the check that publishes the layout -- and so is everything a
        // described type is built out of, because its own record names its
        // base and its interfaces and a reader is entitled to look them up.
        //
        // A CLASS LIBRARY'S TYPE IS A PROGRAM'S ONLY WHEN IT USES IT. A program
        // compiled against the library's sources (--ref) holds those
        // declarations as its own, and a type of the program's that shares a
        // library type's name replaces it there (namespaces are flattened):
        // the library's Seat, described by the program, returned the
        // program's Point and disagreed with the library's own object about a
        // type the program never touched. A library unit still describes
        // every type it defines; a program describes library types it uses.
        HashSet<TypeSymbol> described = new(ReferenceEqualityComparer.Instance);
        Queue<TypeSymbol> pending = new();
        foreach (TypeSymbol type in bound.Types.Values.Distinct())
            if (type.Used || type.Decl is { Elsewhere: false } && (library || !type.Decl.FromLibrary))
                pending.Enqueue(type);
        while (pending.Count != 0)
        {
            TypeSymbol type = pending.Dequeue();
            if (!described.Add(type)) continue;
            if (type.Base is TypeSymbol basis) pending.Enqueue(basis);
            foreach (TypeSymbol face in type.Interfaces) pending.Enqueue(face);
        }

        IEnumerable<ManagedTypeLayout> Records()
        {
            foreach (TypeSymbol type in bound.Types.Values.Distinct()
                .Where(type => described.Contains(type))
                .Where(type => type.Decl is { Specialised: false, LocalOnly: false, TypeParams.Count: 0 }))
            {
                using MemoryStream stream = new();
                using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
                void TypeName(Type value)
                {
                    // As stored: a class's '?' is an annotation, not layout.
                    writer.Write(value.LayoutText());
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
                    writer.Write(field.Boxed); writer.Write(field.Volatile); writer.Write(field.Required); writer.Write(field.Inline);
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
                    Console.Error.WriteLine("  delegate=" + (type.Decl?.IsDelegate == true) + " mods=" + (int)(type.Decl?.Mods ?? Mods.None));
                    foreach (FieldSymbol field in type.Fields.Where(field => !field.Static).OrderBy(field => field.Name, StringComparer.Ordinal))
                        Console.Error.WriteLine("  field " + field.Name + " " + field.Type + "|" + (field.Type.Symbol?.Key ?? "") + " @" + field.Offset
                            + " boxed=" + field.Boxed + " volatile=" + field.Volatile + " required=" + field.Required + " inline=" + field.Inline);
                    foreach (MethodSymbol method in type.Methods.Where(m => m.VtableSlot >= 0 && m.Decl?.LocalCopy != true).OrderBy(m => m.VtableSlot))
                        Console.Error.WriteLine("  slot " + method.VtableSlot + " " + method.Name + " " + method.Returns + "|" + (method.Returns.Symbol?.Key ?? "")
                            + " static=" + method.Static + " ctor=" + method.IsCtor + " abstract=" + method.Abstract + " ("
                            + string.Join(", ", method.Params.Select(p => p.Type + "|" + (p.Type.Symbol?.Key ?? "") + (p.ByRef ? " ref" : "") + (p.ReadOnly ? " in" : ""))) + ")");
                    foreach (FieldSymbol field in type.Fields.Where(field => field.Static))
                        Console.Error.WriteLine("  static " + field.Name + " " + field.Type + "|" + (field.Type.Symbol?.Key ?? ""));
                    foreach (MethodSymbol method in type.Methods.Where(m => m.Decl?.LocalCopy != true && m.TypeParams.Count == 0))
                        Console.Error.WriteLine("  method " + Lowering.Label(method) + " " + method.Returns + "|" + (method.Returns.Symbol?.Key ?? "")
                            + " mods=" + (int)(method.Decl?.Mods ?? Mods.None) + " ("
                            + string.Join(", ", method.Params.Select(p => p.Type + "|" + (p.Type.Symbol?.Key ?? "") + (p.ByRef ? " ref" : "") + (p.ReadOnly ? " in" : ""))) + ")");
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
        return Records().ToList();
    }
}
