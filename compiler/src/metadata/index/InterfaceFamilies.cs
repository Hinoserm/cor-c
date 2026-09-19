using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Compact project-wide interface slot reservations, without retaining interface ASTs.</summary>
public static class InterfaceFamilies
{
    /// <summary>
    /// `library` says the interface is one of the compiler's own library
    /// sources (stdlib, runtime), whose slots are an ABI every image shares;
    /// a project's own interfaces are numbered after that region so that
    /// declaring one never moves a library slot. See Binder's numbering.
    /// </summary>
    public static DeclarationRecord Record(string key, TypeDecl type, bool library = true)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        string name = type.Outer is null ? type.Name : type.Outer + "." + type.Name;
        byte[] encoded = DeclarationIndex.Utf8.GetBytes(name);
        writer.Write(2); writer.Write(encoded.Length); writer.Write(encoded);
        writer.Write(type.TypeParams.Count);
        writer.Write(type.Members.Sum(member => member is MethodDecl ? 1 : member is PropertyDecl property ? 1 + (property.HasSetter ? 1 : 0) : 0));
        writer.Write(library);
        return new DeclarationRecord("I:" + key[2..], stream.ToArray());
    }

    /// <summary>The families that are library ABI (see Record).</summary>
    public static IReadOnlySet<(string Name, int Arity)> ReadLibrary(DeclarationIndex index, string assembly)
    {
        HashSet<(string, int)> result = new();
        foreach (DeclarationRecord record in index.WithPrefix("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n"))
        {
            using MemoryStream stream = new(record.Payload, writable: false);
            using BinaryReader reader = new(stream, DeclarationIndex.Utf8);
            int version = reader.ReadInt32();
            int length = reader.ReadInt32();
            string name = DeclarationIndex.Utf8.GetString(DeclarationIndex.ReadBytes(reader, length));
            int arity = reader.ReadInt32(); reader.ReadInt32();
            bool library = version < 2 || reader.ReadBoolean();
            if (library) result.Add((name, arity));
        }
        return result;
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
                int version = reader.ReadInt32();
                if (version != 1 && version != 2) throw new InvalidDataException("Unsupported interface family record");
                int length = reader.ReadInt32();
                if (length < 1 || length > record.Payload.Length - 16) throw new InvalidDataException("Invalid interface family name");
                string name = DeclarationIndex.Utf8.GetString(DeclarationIndex.ReadBytes(reader, length));
                int arity = reader.ReadInt32(), methods = reader.ReadInt32();
                if (version >= 2) reader.ReadBoolean();
                if (arity < 0 || methods < 0 || stream.Position != stream.Length) throw new InvalidDataException("Invalid interface family counts");
                result[(name, arity)] = checked(result.GetValueOrDefault((name, arity)) + methods);
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated interface family record", error); }
        }
        return result;
    }
}
