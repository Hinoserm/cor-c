using System.Security.Cryptography;
using System.Text;
namespace Corsac.Lang.Metadata;

/// <summary>Bounded shared declaration cache; keys come from the language's name resolver.</summary>
public sealed class DeclarationCatalog : IDisposable
{
    private sealed class Entry
    {
        public required string Key;
        public required IReadOnlyList<SourceDeclaration> Records;
        public long Bytes;
        public int Pins;
        public long Used;
    }

    private readonly DeclarationIndex index;
    private readonly long budget;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private long resident, clock, loads;
    private bool disposed;
    public long ResidentBytes { get { lock (gate) return resident; } }
    public long PayloadLoads { get { lock (gate) return loads; } }
    public IReadOnlyDictionary<(string Name, int Arity), int> Interfaces(string assembly)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            return InterfaceFamilies.Read(index, assembly);
        }
    }

    public IReadOnlySet<(string Name, int Arity)> LibraryInterfaces(string assembly)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            return InterfaceFamilies.ReadLibrary(index, assembly);
        }
    }

    /// <summary>
    /// The text of the file a declaration was cut from, read and checked once
    /// for the whole project.
    ///
    /// A record carries the hash of its file, and reading one used to read
    /// that whole file, encode it to UTF-8 again and hash it again -- per
    /// declaration, per discovery pass, per source of the project. One
    /// library file holding thirty generic types was read and hashed thirty
    /// times to compile one unit, and again for each of the next hundred.
    /// The files do not change while a project is being compiled, so the
    /// reading and the checking happen once and every later caller is handed
    /// the same string.
    ///
    /// Bounded like everything else here: the library text of a project is a
    /// couple of megabytes, and a file evicted under pressure is simply read
    /// again.
    /// </summary>
    public string ReadSource(SourceDeclaration source)
    {
        lock (sourceGate)
        {
            if (sources.TryGetValue(source.Path, out Source? held))
            {
                held.Used = ++sourceClock;
                if (!held.Hash.AsSpan().SequenceEqual(source.SourceHash))
                    throw new InvalidDataException("Declaration source generation is stale: " + source.Path);
                source.Verify(held.Text);
                return held.Text;
            }
        }
        string text = File.ReadAllText(source.Path);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        if (!hash.AsSpan().SequenceEqual(source.SourceHash))
            throw new InvalidDataException("Declaration source generation is stale: " + source.Path);
        source.Verify(text);
        long size = 128L + source.Path.Length * 2L + text.Length * 2L + hash.Length;
        lock (sourceGate)
        {
            if (!sources.ContainsKey(source.Path) && size <= sourceBudget)
            {
                while (sourceBytes + size > sourceBudget && sources.Count != 0)
                {
                    var oldest = sources.MinBy(pair => pair.Value.Used);
                    sourceBytes -= oldest.Value.Bytes;
                    sources.Remove(oldest.Key);
                }
                sources[source.Path] = new Source { Text = text, Hash = hash, Bytes = size, Used = ++sourceClock };
                sourceBytes += size;
            }
        }
        return text;
    }

    private sealed class Source
    {
        public required string Text;
        public required byte[] Hash;
        public required long Bytes;
        public long Used;
    }
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly object sourceGate = new();
    private long sourceBytes, sourceClock;
    private readonly long sourceBudget;

    /// <summary>What the verified source texts are holding right now.</summary>
    public long ResidentSourceBytes { get { lock (sourceGate) return sourceBytes; } }

    public DeclarationCatalog(string path, long budgetBytes = 2 * 1024 * 1024,
        long sourceBudgetBytes = 16 * 1024 * 1024)
    {
        if (budgetBytes < 4096) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        if (sourceBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(sourceBudgetBytes));
        index = new DeclarationIndex(path);
        budget = budgetBytes;
        sourceBudget = sourceBudgetBytes;
    }

    public DeclarationLease? Acquire(string assembly, string metadataName)
        => AcquireKey("T:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + metadataName);

    public IReadOnlyList<string> ExtensionKeys(string assembly, string space, string method)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            return index.Find("E:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + space + "\n" + method)
                .Select(record => DeclarationIndex.Utf8.GetString(record.Payload))
                .Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// What a binding name resolves to, remembered for the life of the
    /// catalog.
    ///
    /// The binder asks this for EVERY name it resolves, and the answer is a
    /// binary search over the index file under the catalog's one lock. With
    /// eight workers, seven of them sat in Monitor.Enter here in every stack
    /// sample taken: the whole frontend was serialised on name lookups, and
    /// eight workers finished the kernel no sooner than one. The index does
    /// not change while a catalog is open, so an answer is an answer for
    /// good; a hit takes a hash lookup under a lock nobody holds for long.
    /// </summary>
    private readonly Dictionary<string, string?> bindingKeys = new(StringComparer.Ordinal);
    private readonly object bindingGate = new();

    public string? BindingKey(string assembly, string bindingName)
    {
        string query = "B:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + bindingName;
        lock (bindingGate)
        {
            if (bindingKeys.TryGetValue(query, out string? known)) return known;
        }
        string? result = null;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            foreach (DeclarationRecord record in index.Find(query))
            {
                string found = DeclarationIndex.Utf8.GetString(record.Payload);
                if (result is not null && result != found) throw new InvalidDataException("Ambiguous indexed type identity: " + bindingName);
                result = found;
            }
        }
        lock (bindingGate) bindingKeys[query] = result;
        return result;
    }

    public byte[] QueryFingerprint(string key)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            bool interfacePlan = key.StartsWith("I:", StringComparison.Ordinal) && key.EndsWith('\n');
            foreach (DeclarationRecord record in interfacePlan ? index.WithPrefix(key) : index.Find(key))
            {
                if (interfacePlan) writer.Write(record.Key);
                writer.Write(record.Payload.Length); writer.Write(record.Payload);
            }
            return System.Security.Cryptography.SHA256.HashData(stream.ToArray());
        }
    }

    public DeclarationLease? AcquireKey(string key)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            if (!entries.TryGetValue(key, out Entry? entry))
            {
                List<SourceDeclaration> records = new();
                long bytes = 0;
                foreach (DeclarationRecord record in index.Find(key))
                {
                    // Conservative accounting includes decoded UTF-16 text,
                    // lists/records and temporary binary decoding. One bounded
                    // index record is additional transient reader workspace.
                    long needed = checked(record.Payload.LongLength * 4 + 512);
                    MakeRoom(checked(bytes + needed));
                    records.Add(SourceDeclaration.Decode(record));
                    bytes += needed;
                    loads++;
                }
                if (records.Count == 0) return null;
                entry = new Entry { Key = key, Records = records.AsReadOnly(), Bytes = bytes };
                entries.Add(key, entry);
                resident += bytes;
            }
            entry.Pins++;
            entry.Used = ++clock;
            return new DeclarationLease(entry.Records, () =>
            {
                lock (gate) { entry.Pins--; entry.Used = ++clock; }
            });
        }
    }

    private void MakeRoom(long required)
    {
        if (required > budget) throw new InvalidDataException("One declaration family exceeds the metadata cache budget");
        while (resident > budget - required)
        {
            Entry? victim = entries.Values.Where(entry => entry.Pins == 0).OrderBy(entry => entry.Used).FirstOrDefault();
            if (victim is null) throw new InvalidDataException("Metadata cache budget is exhausted by pinned declarations");
            entries.Remove(victim.Key);
            resident -= victim.Bytes;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (entries.Values.Any(entry => entry.Pins != 0)) throw new InvalidOperationException("Cannot dispose a catalog with live declaration leases");
            disposed = true;
            entries.Clear(); resident = 0;
            index.Dispose();
        }
    }
}
