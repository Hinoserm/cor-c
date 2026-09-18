using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Consumed declaration families, not a project-wide declaration graph.</summary>
public static class UnitDependencies
{
    public static byte[] Fingerprint(IReadOnlyList<SourceDeclaration> records, bool implementations)
    {
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        foreach (SourceDeclaration record in records.OrderBy(record => record.Path, StringComparer.Ordinal).ThenBy(record => record.From))
        {
            writer.Write(record.Path); writer.Write(record.DeclarationHash);
            if (implementations) writer.Write(record.SourceHash);
        }
        return SHA256.HashData(buffer.ToArray());
    }

    public static void Write(string path, DeclarationCatalog catalog, IEnumerable<string> keys, IReadOnlySet<string> implementations)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream, Encoding.UTF8);
        string[] ordered = keys.Order(StringComparer.Ordinal).ToArray();
        writer.Write(0x50454443u); writer.Write(1); writer.Write(ordered.Length);
        foreach (string key in ordered)
        {
            using DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing dependency " + key);
            bool body = implementations.Contains(key);
            byte[] encoded = Encoding.UTF8.GetBytes(key);
            if (encoded.Length > 4096) throw new InvalidDataException("Dependency key exceeds format limit");
            writer.Write(encoded.Length); writer.Write(encoded); writer.Write(body); writer.Write(Fingerprint(lease.Records, body));
        }
    }

    public static bool IsCurrent(string path, string index)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using DeclarationCatalog catalog = new(index);
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x50454443u || reader.ReadInt32() != 1) return false;
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000) return false;
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 4096 || length > stream.Length - stream.Position - 33) return false;
                string key = Encoding.UTF8.GetString(reader.ReadBytes(length));
                byte flag = reader.ReadByte(); if (flag > 1) return false;
                bool body = flag != 0; byte[] fingerprint = reader.ReadBytes(32);
                using DeclarationLease? lease = catalog.AcquireKey(key);
                if (lease is null || !fingerprint.SequenceEqual(Fingerprint(lease.Records, body))) return false;
            }
            return stream.Position == stream.Length;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
