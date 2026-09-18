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

    public DeclarationCatalog(string path, long budgetBytes = 2 * 1024 * 1024)
    {
        if (budgetBytes < 4096) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        index = new DeclarationIndex(path);
        budget = budgetBytes;
    }

    public DeclarationLease? Acquire(string assembly, string metadataName)
        => AcquireKey("T:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + metadataName);

    public string? BindingKey(string assembly, string bindingName)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            string? result = null;
            foreach (DeclarationRecord record in index.Find("B:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + bindingName))
            {
                string found = DeclarationIndex.Utf8.GetString(record.Payload);
                if (result is not null && result != found) throw new InvalidDataException("Ambiguous indexed type identity: " + bindingName);
                result = found;
            }
            return result;
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
