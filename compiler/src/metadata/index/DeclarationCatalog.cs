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

    /// <summary>
    /// The interface families of the KERNEL a module is compiled against:
    /// those of the index under this one, when it was made on one, else of
    /// this index -- a module compiled whole against the kernel's index
    /// alone. What a module adds is the rest (Binder, the module's tier).
    /// </summary>
    public IReadOnlySet<(string Name, int Arity)> KernelInterfaces(string assembly)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            if (kernelInterfaces is not null) return kernelInterfaces;
            HashSet<(string Name, int Arity)> families = new();
            foreach (var family in InterfaceFamilies.Read(index.Under ?? index, assembly)) families.Add(family.Key);
            kernelInterfaces = families;
            return families;
        }
    }

    private IReadOnlySet<(string Name, int Arity)>? kernelInterfaces;

    /// <summary>The build stamp the index under this one had when this one was made on it, or null for an index made on none.</summary>
    public byte[]? UnderStamp => index.UnderStamp;

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
                    throw new InvalidDataException(Stale(source.Path));
                source.Verify(held.Text);
                return held.Text;
            }
        }
        string text = File.ReadAllText(source.Path);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        if (!hash.AsSpan().SequenceEqual(source.SourceHash))
            throw new InvalidDataException(Stale(source.Path));
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

    /// <summary>
    /// What to say when a source has moved on since the index was written.
    /// It names the file and what to do about it, because this is not a
    /// fault in the program being compiled and the reader's next question is
    /// always the same one.
    /// </summary>
    internal static string Stale(string path)
        => "declaration index is out of date: " + path + " has changed since the index was built; "
            + "rebuild the index before compiling against it";

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

    /// <summary>
    /// The hash of the text of a source file this index was made from
    /// (SourceIndexBuilder.SourceKey), or null when it holds no such file.
    /// </summary>
    public byte[]? SourceHash(string path)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            foreach (DeclarationRecord record in index.Find(SourceIndexBuilder.SourceKey(path))) return record.Payload;
            return null;
        }
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

    /// <summary>The declarations with a generic instance method of this name and arity (`Name`k).</summary>
    public IReadOnlyList<string> OverrideKeys(string assembly, string method)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            return index.Find("G:" + SourceIndexBuilder.AssemblyIdentity(assembly) + "\n" + method)
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
    //
    // BY THE BARE NAME, a table for each kind of question and assembly: keyed
    // by the whole query, every name asked kept the assembly's identity in
    // front of it -- ninety thousand strings, a fifth of what a compile kept
    // live between its units, each saying "corc, Version=0.0.0.0, ..." again.
    private readonly Dictionary<(char Kind, string Identity), Dictionary<string, string?>> remembered = new();
    private readonly object bindingGate = new();

    public string? BindingKey(string assembly, string bindingName)
        => Remembered('B', SourceIndexBuilder.AssemblyIdentity(assembly), bindingName);

    /// <summary>
    /// The one declaration whose simple name this is, among those keyed by a
    /// qualified name (a namespace or an outer type), or null when none is or
    /// more than one is -- ambiguous is no answer, as in Binder.Sole.
    /// </summary>
    public string? SoleKey(string assembly, string simpleName)
        => Remembered('S', SourceIndexBuilder.AssemblyIdentity(assembly), simpleName);

    // `kind:identity\nname` asked of the index once, and remembered. Two
    // answers to a binding name are an error in the index; two to a sole
    // name are no answer.
    private string? Remembered(char kind, string identity, string name)
    {
        Dictionary<string, string?>? table;
        lock (bindingGate)
        {
            if (!remembered.TryGetValue((kind, identity), out table))
                remembered[(kind, identity)] = table = new(StringComparer.Ordinal);
            else if (table.TryGetValue(name, out string? known)) return known;
        }
        string query = kind + ":" + identity + "\n" + name;
        string? result = null;
        bool ambiguous = false;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DeclarationCatalog));
            foreach (DeclarationRecord record in index.Find(query))
            {
                string found = DeclarationIndex.Utf8.GetString(record.Payload);
                if (result is not null && result != found)
                {
                    if (kind == 'B') throw new InvalidDataException("Ambiguous indexed type identity: " + name);
                    ambiguous = true;
                }
                result = found;
            }
        }
        if (ambiguous) result = null;
        lock (bindingGate) table[name] = result;
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
