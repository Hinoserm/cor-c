using System.Text;
using System.Security.Cryptography;

namespace Corsac.Lang.Metadata;

/// <summary>Declaration syntax and lexical scope, independent of implementation bodies.</summary>
public sealed class SourceDeclaration
{
    public required string Key { get; init; }
    public required string Path { get; init; }
    public required string Text { get; init; }
    public required string Namespace { get; init; }
    public required string Outer { get; init; }
    public required byte[] SourceHash { get; init; }
    public required byte[] DeclarationHash { get; init; }
    public required FileScope Scope { get; init; }
    public IReadOnlyList<string> ConditionalSymbols { get; init; } = Array.Empty<string>();
    public int From { get; init; }
    public int To { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    /// <summary>
    /// Reads the file this declaration was cut from and checks it is the one
    /// the index was built against.
    ///
    /// Prefer <see cref="DeclarationCatalog.ReadSource"/>, which does this
    /// once per file for a whole project. This is the unshared path: reading
    /// and hashing a whole file to reach one declaration in it.
    /// </summary>
    public string ReadSource()
    {
        string text = File.ReadAllText(Path);
        if (!SHA256.HashData(Encoding.UTF8.GetBytes(text)).SequenceEqual(SourceHash))
            throw new InvalidDataException("Declaration source generation is stale: " + Path);
        Verify(text);
        return text;
    }

    /// <summary>This declaration's own text, cut out of its verified file.</summary>
    public string ReadImplementation() => ReadSource()[From..To];

    /// <summary>Checks this declaration's span against already verified text.</summary>
    public void Verify(string text)
    {
        if (From < 0 || To < From || To > text.Length) throw new InvalidDataException("Invalid implementation source span");
    }

    public DeclarationRecord Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        void String(string value)
        {
            byte[] bytes = DeclarationIndex.Utf8.GetBytes(value);
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        writer.Write(0x43454443u); writer.Write(2u); // CDEC
        String(Path); String(Text); String(Namespace); String(Outer);
        writer.Write(From); writer.Write(To); writer.Write(Line); writer.Write(Column);
        if (SourceHash.Length != 32 || DeclarationHash.Length != 32) throw new InvalidDataException("Invalid declaration fingerprint");
        writer.Write(SourceHash); writer.Write(DeclarationHash);
        writer.Write(Scope.Imports.Count);
        foreach (var import in Scope.Imports) { String(import.In); String(import.Namespace); }
        writer.Write(Scope.Aliases.Count);
        foreach (var alias in Scope.Aliases) { String(alias.In); String(alias.Alias); String(alias.Target); }
        writer.Write(ConditionalSymbols.Count);
        foreach (string symbol in ConditionalSymbols) String(symbol);
        return new DeclarationRecord(Key, stream.ToArray());
    }

    public static SourceDeclaration Decode(DeclarationRecord record)
    {
        using MemoryStream stream = new(record.Payload, writable: false);
        using BinaryReader reader = new(stream, DeclarationIndex.Utf8);
        int Count()
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > stream.Length - stream.Position) throw new InvalidDataException("Invalid declaration field length");
            return count;
        }
        string String() => DeclarationIndex.Utf8.GetString(DeclarationIndex.ReadBytes(reader, Count()));
        try
        {
            if (reader.ReadUInt32() != 0x43454443 || reader.ReadUInt32() != 2)
                throw new InvalidDataException("Unsupported source declaration");
            string path = String(), text = String(), ns = String(), outer = String();
            int from = reader.ReadInt32(), to = reader.ReadInt32(), line = reader.ReadInt32(), column = reader.ReadInt32();
            if (from < 0 || to < from || line < 1 || column < 1) throw new InvalidDataException("Invalid source declaration location");
            byte[] sourceHash = DeclarationIndex.ReadBytes(reader, 32), declarationHash = DeclarationIndex.ReadBytes(reader, 32);
            FileScope scope = new();
            int count = Count();
            for (int i = 0; i < count; i++) scope.Imports.Add((String(), String()));
            count = Count();
            for (int i = 0; i < count; i++) scope.Aliases.Add((String(), String(), String()));
            count = Count();
            List<string> symbols = new();
            for (int i = 0; i < count; i++) symbols.Add(String());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing source declaration data");
            return new SourceDeclaration { Key = record.Key, Path = path, Text = text, Namespace = ns, Outer = outer,
                From = from, To = to, Line = line, Column = column, Scope = scope,
                SourceHash = sourceHash, DeclarationHash = declarationHash, ConditionalSymbols = symbols.AsReadOnly() };
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated source declaration", error); }
    }
}
