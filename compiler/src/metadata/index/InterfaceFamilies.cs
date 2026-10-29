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
        writer.Write(0); writer.Write(encoded.Length); writer.Write(encoded);
        writer.Write(type.TypeParams.Count);
        writer.Write(type.Members.Sum(member => member is MethodDecl ? 1 : member is PropertyDecl property ? 1 + (property.HasSetter ? 1 : 0) : 0));
        writer.Write(library);
        return new DeclarationRecord("I:" + key[2..], stream.ToArray());
    }

    /// <summary>
    /// The slots a project's family of this many methods is numbered in
    /// (Binder's numbering): the next power of two, four at least, so that
    /// most edits to one interface leave where every other starts.
    /// </summary>
    public static int Block(int methods) => methods <= 0 ? 0 : Math.Max(4, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)methods));

    /// <summary>
    /// The interface plan as a unit reading it depends on it
    /// (DeclarationFingerprint, DeclarationCatalog.QueryFingerprint): each
    /// family by its name, arity and tier, and the slots it is numbered in
    /// -- a library family's count, a project family's block -- as the
    /// binder numbers them from Read's sums. The methods themselves, and
    /// their order, are its interface's declaration, read by the units that
    /// name it.
    /// </summary>
    public static byte[] PlanSaid(IEnumerable<DeclarationRecord> records)
    {
        SortedDictionary<(string Name, int Arity), (int Methods, bool Library)> families = new();
        foreach (DeclarationRecord record in records)
        {
            (string name, int arity, int methods, bool library) = Parse(record);
            (int had, bool wasLibrary) = families.GetValueOrDefault((name, arity));
            families[(name, arity)] = (checked(had + methods), wasLibrary || library);
        }
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var ((name, arity), (methods, library)) in families)
        {
            writer.Write(name); writer.Write(arity); writer.Write(library);
            writer.Write(library ? methods : Block(methods));
        }
        return stream.ToArray();
    }

    /// <summary>The families that are library ABI (see Record).</summary>
    public static IReadOnlySet<(string Name, int Arity)> ReadLibrary(DeclarationIndex index, string assembly)
    {
        HashSet<(string, int)> result = new();
        foreach (DeclarationRecord record in index.WithPrefix("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n"))
        {
            (string name, int arity, _, bool library) = Parse(record);
            if (library) result.Add((name, arity));
        }
        return result;
    }

    /// <summary>
    /// A CONFLICTED FORM of the project's declarations (InterfaceForms.Conflicts):
    /// its key the form, its payload whether a library declaration has it.
    /// Every unit compiled against the index numbers a block for each.
    /// </summary>
    public static DeclarationRecord Form(string assembly, string form, bool library)
        => new("X:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + form, new[] { library ? (byte)1 : (byte)0 });

    /// <summary>The conflicted forms an index records, with whether each is the library's.</summary>
    public static IReadOnlyDictionary<string, bool> ReadForms(DeclarationIndex index, string assembly)
    {
        string prefix = "X:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n";
        Dictionary<string, bool> result = new(StringComparer.Ordinal);
        foreach (DeclarationRecord record in index.WithPrefix(prefix))
        {
            if (record.Payload.Length != 1) throw new InvalidDataException("Invalid interface form record");
            string form = record.Key[prefix.Length..];
            result[form] = result.GetValueOrDefault(form) || record.Payload[0] != 0;
        }
        return result;
    }

    public static IReadOnlyDictionary<(string Name, int Arity), int> Read(DeclarationIndex index, string assembly)
    {
        Dictionary<(string, int), int> result = new();
        foreach (DeclarationRecord record in index.WithPrefix("I:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n"))
        {
            (string name, int arity, int methods, _) = Parse(record);
            result[(name, arity)] = checked(result.GetValueOrDefault((name, arity)) + methods);
        }
        return result;
    }

    /// <summary>One record as Record wrote it: version 0, the name, its arity, its methods, whether it is library ABI.</summary>
    static (string Name, int Arity, int Methods, bool Library) Parse(DeclarationRecord record)
    {
        using MemoryStream stream = new(record.Payload, writable: false);
        using BinaryReader reader = new(stream, DeclarationIndex.Utf8);
        try
        {
            if (reader.ReadInt32() != 0) throw new InvalidDataException("Unsupported interface family record");
            int length = reader.ReadInt32();
            // Version, length, arity and methods are four words, the library flag one byte.
            if (length < 1 || length > record.Payload.Length - 17) throw new InvalidDataException("Invalid interface family name");
            string name = DeclarationIndex.Utf8.GetString(DeclarationIndex.ReadBytes(reader, length));
            int arity = reader.ReadInt32(), methods = reader.ReadInt32();
            byte library = reader.ReadByte();
            if (arity < 0 || methods < 0 || library > 1 || stream.Position != stream.Length) throw new InvalidDataException("Invalid interface family counts");
            return (name, arity, methods, library == 1);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated interface family record", error); }
    }
}
