using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Compact project-wide interface slot reservations, without retaining interface ASTs.</summary>
public static class InterfaceFamilies
{
    public static DeclarationRecord Record(string key, TypeDecl type)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        string name = type.Outer is null ? type.Name : type.Outer + "." + type.Name;
        byte[] encoded = DeclarationIndex.Utf8.GetBytes(name);
        writer.Write(1); writer.Write(encoded.Length); writer.Write(encoded);
        writer.Write(type.TypeParams.Count);
        writer.Write(type.Members.Sum(member => member is MethodDecl ? 1 : member is PropertyDecl property ? 1 + (property.HasSetter ? 1 : 0) : 0));
        return new DeclarationRecord("I:" + key[2..], stream.ToArray());
    }

    public static IReadOnlyDictionary<(string Name, int Arity), int> Read(DeclarationIndex index, string assembly)
    {
        Dictionary<(string, int), int> result = new();
        foreach (DeclarationRecord record in index.WithPrefix("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n"))
        {
            using MemoryStream stream = new(record.Payload, writable: false);
            using BinaryReader reader = new(stream, DeclarationIndex.Utf8);
            try
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported interface family record");
                int length = reader.ReadInt32();
                if (length < 1 || length > record.Payload.Length - 16) throw new InvalidDataException("Invalid interface family name");
                string name = DeclarationIndex.Utf8.GetString(DeclarationIndex.ReadBytes(reader, length));
                int arity = reader.ReadInt32(), methods = reader.ReadInt32();
                if (arity < 0 || methods < 0 || stream.Position != stream.Length) throw new InvalidDataException("Invalid interface family counts");
                result[(name, arity)] = checked(result.GetValueOrDefault((name, arity)) + methods);
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated interface family record", error); }
        }
        return result;
    }
}
