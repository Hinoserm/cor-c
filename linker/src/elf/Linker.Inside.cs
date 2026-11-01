#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

public static partial class Linker
{
    /// <summary>
    /// The suffix of a method's copy that runs inside its caller's kept
    /// state: no save or give-back of its own (the compiler's
    /// Lowering.CopiesInside and InsideLabel).
    /// </summary>
    public const string InsideSuffix = "$inside";


    /// <summary>
    /// WHAT AN IMAGE FINDS AT ITS START, as its loader finds it: the exports
    /// of every library the image needs (DT_NEEDED) and every library those
    /// need -- a library the link was given by its soname, else one beside
    /// them (the COR-C# ones, where the image's RUNPATH points), else one in
    /// the system's library directories (a [DllImport]'s C library,
    /// NativeNeeded), each of the image's own class (Elf32 or Elf64). NULL
    /// WHERE A NEEDED LIBRARY IS NOT HERE TO READ -- a C library only the
    /// target has: what it would answer cannot be told, and nothing is judged.
    /// </summary>
    public static HashSet<string>? ExportsAtStart(byte[] image, IEnumerable<string> libraries, bool longMode)
    {
        byte elfClass = longMode ? (byte)2 : (byte)1;
        Dictionary<string, string> given = new(StringComparer.Ordinal);
        List<string> beside = new();
        foreach (string library in libraries)
        {
            string full = Path.GetFullPath(library);
            string dir = Path.GetDirectoryName(full) ?? ".";
            if (!beside.Contains(dir)) beside.Add(dir);
            given.TryAdd(Path.GetFileName(full), full);
            try { if (ElfReader.ReadSharedImage(File.ReadAllBytes(full)).Soname is string soname) given.TryAdd(soname, full); }
            catch (Exception e) when (e is ElfFormatException or IOException or InvalidDataException) { }
        }
        string[] system = longMode ? LibraryDirectories64 : LibraryDirectories32;
        HashSet<string> exported = new(StringComparer.Ordinal), read = new(StringComparer.Ordinal);
        Queue<string> pending = new(ElfReader.ReadSharedImage(image).Needed);
        while (pending.Count > 0)
        {
            string needed = pending.Dequeue();
            IEnumerable<string> candidates = needed.Contains('/') ? new[] { needed }
                : (given.TryGetValue(needed, out string? named) ? new[] { named } : Array.Empty<string>())
                    .Concat(beside.Concat(system).Select(dir => Path.Combine(dir, needed)));
            byte[]? bytes = null;
            string? path = null;
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                byte[] read1 = File.ReadAllBytes(candidate);
                // Of the image's own class: a usrmerge host's /usr/lib holds the
                // 64-bit libc an i386 image is never given.
                if (read1.Length < 5 || read1[4] != elfClass) continue;
                (bytes, path) = (read1, Path.GetFullPath(candidate));
                break;
            }
            if (bytes is null || path is null) return null;
            if (!read.Add(path)) continue;
            try
            {
                exported.UnionWith(ElfReader.ExportsOf(bytes));
                foreach (string next in ElfReader.ReadSharedImage(bytes).Needed) pending.Enqueue(next);
            }
            catch (Exception e) when (e is ElfFormatException or IOException or InvalidDataException) { return null; }
        }
        return exported;
    }

    /// <summary>
    /// A CALL INTO ANOTHER IMAGE MADE INSIDE A KEPT STATE names the callee's
    /// $inside copy where the images the unit was compiled against exported
    /// one (Lowering.InsideLabel): the kernel's, a module this one is built
    /// on. Where nothing this link has defines that copy -- no input, nothing
    /// `elsewhere` says is exported -- because a later link of theirs pruned
    /// it, the call is to the method itself, which keeps the state of its
    /// own: right inside a kept state as anywhere, only dearer. Done before
    /// anything is resolved, on the symbols and on the relocations naming
    /// them -- the objects themselves, so linked again they call the method.
    /// Nothing to do, and nothing read, where no unit names a copy it lacks.
    /// </summary>
    internal static void InsideOrItself(IEnumerable<ObjectFile> objects, Func<string, bool>? elsewhere = null)
    {
        List<ObjectFile> all = objects.ToList();
        HashSet<string> defined = new(StringComparer.Ordinal);
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (ObjectFile o in all)
            foreach (Symbol s in o.Symbols)
            {
                if (s.IsDefined) defined.Add(s.Name);
                else if (s.Name.EndsWith(InsideSuffix, StringComparison.Ordinal)) named.Add(s.Name);
            }
        named.RemoveWhere(name => defined.Contains(name) || elsewhere?.Invoke(name) == true);
        if (named.Count == 0) return;
        foreach (ObjectFile o in all)
        {
            HashSet<string> has = new(o.Symbols.Select(s => s.Name), StringComparer.Ordinal);
            for (int i = o.Symbols.Count - 1; i >= 0; i--)
            {
                Symbol s = o.Symbols[i];
                if (s.IsDefined || !named.Contains(s.Name)) continue;
                string itself = s.Name[..^InsideSuffix.Length];
                // One undefined name a method: the object may name it already.
                if (has.Contains(itself)) o.Symbols.RemoveAt(i);
                else
                {
                    o.Symbols[i] = new Symbol { Name = itself, Offset = s.Offset, Size = s.Size, IsFunction = s.IsFunction, Global = s.Global };
                    has.Add(itself);
                }
            }
            foreach (Section section in o.Sections)
                for (int r = 0; r < section.Relocs.Count; r++)
                    if (named.Contains(section.Relocs[r].Symbol))
                        section.Relocs[r] = section.Relocs[r] with { Symbol = section.Relocs[r].Symbol[..^InsideSuffix.Length] };
        }
    }

    /// <summary>What the shared objects at `paths` export, read once, the first time a name is asked about.</summary>
    private static Func<string, bool> ExportedBy(IEnumerable<string> paths)
    {
        HashSet<string>? exported = null;
        return name =>
        {
            if (exported is null)
            {
                exported = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in paths)
                {
                    try { exported.UnionWith(ElfReader.ExportsOf(File.ReadAllBytes(path))); }
                    catch (Exception e) when (e is IOException or ElfFormatException)
                    {
                        throw new LinkException(new[] { path + ": its exports cannot be read: " + e.Message });
                    }
                }
            }
            return exported.Contains(name);
        };
    }
}
