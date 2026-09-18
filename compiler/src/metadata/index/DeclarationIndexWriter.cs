using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Bounded external sort with four-way merges and atomic index publication.</summary>
public static class DeclarationIndexWriter
{
    private const int FanIn = 4;

    public static void Write(string destination, IEnumerable<DeclarationRecord> records, int memoryBytes = 1024 * 1024)
    {
        if (memoryBytes < 4096) throw new ArgumentOutOfRangeException(nameof(memoryBytes));
        string full = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(parent);
        string work = Path.Combine(parent, ".declarations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        List<List<string>> levels = new();
        int sequence = 0;
        string Next() => Path.Combine(work, (++sequence).ToString() + ".run");
        void Carry(string path, int level)
        {
            if (levels.Count == level) levels.Add(new List<string>());
            levels[level].Add(path);
            if (levels[level].Count < FanIn) return;
            string merged = Next();
            WriteRun(merged, Merge(levels[level]));
            foreach (string old in levels[level]) File.Delete(old);
            levels[level].Clear();
            Carry(merged, level + 1);
        }
        try
        {
            List<DeclarationRecord> chunk = new();
            long bytes = 0;
            void Flush()
            {
                if (chunk.Count == 0) return;
                chunk.Sort(Compare);
                string path = Next();
                WriteRun(path, chunk);
                chunk.Clear(); bytes = 0;
                Carry(path, 0);
            }
            foreach (DeclarationRecord record in records)
            {
                byte[] key = DeclarationIndex.Utf8.GetBytes(record.Key);
                DeclarationIndex.CheckLengths(key.Length, record.Payload.Length);
                if (record.Key.IndexOf('\0') >= 0) throw new InvalidDataException("NUL in declaration key");
                long size = 128L + record.Key.Length * 2L + record.Payload.Length;
                if (size > memoryBytes) throw new InvalidDataException("One declaration exceeds the index sort memory budget");
                if (bytes + size > memoryBytes) Flush();
                // Own the bytes even if the producer reuses its serialization buffer.
                chunk.Add(new DeclarationRecord(record.Key, (byte[])record.Payload.Clone()));
                bytes += size;
            }
            Flush();
            List<string> runs = levels.SelectMany(level => level).ToList();
            while (runs.Count > FanIn)
            {
                List<string> batch = runs.Take(FanIn).ToList();
                string merged = Next();
                WriteRun(merged, Merge(batch));
                foreach (string old in batch) File.Delete(old);
                runs.RemoveRange(0, FanIn);
                runs.Add(merged);
            }
            string output = Path.Combine(work, "index");
            using (FileStream stream = File.Create(output))
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            using (FileStream offsets = File.Create(Path.Combine(work, "offsets")))
            using (BinaryWriter directory = new(offsets, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(new byte[DeclarationIndex.HeaderSize]);
                long count = 0;
                foreach (DeclarationRecord record in Merge(runs))
                {
                    directory.Write(stream.Position);
                    WriteRecord(writer, record);
                    count++;
                }
                long table = stream.Position;
                directory.Flush(); offsets.Position = 0; offsets.CopyTo(stream);
                long length = stream.Position;
                stream.Position = 0;
                writer.Write(0x58494443u); writer.Write(2u);
                writer.Write(count); writer.Write(table); writer.Write(length);
                writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(output, full, overwrite: true);
        }
        finally
        {
            // Only this invocation's generated directory is owned here.
            foreach (string file in Directory.EnumerateFiles(work)) File.Delete(file);
            Directory.Delete(work);
        }
    }

    private static int Compare(DeclarationRecord left, DeclarationRecord right)
    {
        int key = StringComparer.Ordinal.Compare(left.Key, right.Key);
        return key != 0 ? key : left.Payload.AsSpan().SequenceCompareTo(right.Payload);
    }

    private static void WriteRun(string path, IEnumerable<DeclarationRecord> records)
    {
        using BinaryWriter writer = new(File.Create(path));
        foreach (DeclarationRecord record in records) WriteRecord(writer, record);
    }

    private static void WriteRecord(BinaryWriter writer, DeclarationRecord record)
    {
        byte[] key = DeclarationIndex.Utf8.GetBytes(record.Key);
        writer.Write(key.Length); writer.Write(record.Payload.Length);
        writer.Write(System.Security.Cryptography.SHA256.HashData(key));
        writer.Write(DeclarationIndex.Digest(key, record.Payload));
        writer.Write(key); writer.Write(record.Payload);
    }

    private static IEnumerable<DeclarationRecord> Merge(IReadOnlyList<string> paths)
    {
        BinaryReader?[] readers = new BinaryReader?[paths.Count];
        DeclarationRecord?[] heads = new DeclarationRecord?[paths.Count];
        DeclarationRecord? Next(int index)
        {
            BinaryReader reader = readers[index]!;
            if (reader.BaseStream.Position == reader.BaseStream.Length) return null;
            int key = reader.ReadInt32(), length = reader.ReadInt32();
            DeclarationIndex.CheckLengths(key, length);
            byte[] keyDigest = DeclarationIndex.ReadBytes(reader, 32);
            byte[] digest = DeclarationIndex.ReadBytes(reader, 32);
            byte[] name = DeclarationIndex.ReadBytes(reader, key);
            byte[] payload = DeclarationIndex.ReadBytes(reader, length);
            if (!System.Security.Cryptography.SHA256.HashData(name).SequenceEqual(keyDigest)
                || !DeclarationIndex.Digest(name, payload).SequenceEqual(digest)) throw new InvalidDataException("Corrupt declaration sort run");
            return new DeclarationRecord(DeclarationIndex.Utf8.GetString(name), payload);
        }
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                readers[i] = new BinaryReader(File.OpenRead(paths[i]));
                heads[i] = Next(i);
            }
            while (true)
            {
                int best = -1;
                for (int i = 0; i < heads.Length; i++)
                    if (heads[i] is { } candidate && (best < 0 || Compare(candidate, heads[best]!) < 0)) best = i;
                if (best < 0) yield break;
                yield return heads[best]!;
                heads[best] = Next(best);
            }
        }
        finally { foreach (BinaryReader? reader in readers) reader?.Dispose(); }
    }
}
