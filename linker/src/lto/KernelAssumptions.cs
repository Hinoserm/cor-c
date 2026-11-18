#nullable enable
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT A MODULE ASSUMED OF THE KERNEL IT WAS BUILT AGAINST, AS NAMES THE
/// KERNEL EXPORTS.
///
/// A kernel and its modules may come from different builds: a module binds
/// by name to whatever kernel loads it, and the names carry the shape -- a
/// method's parameters, a static's type, a type's layout -- so the two work
/// together for as long as what the module uses is still there. Two things a
/// module would like to use are in no such name:
///
///   * that a type's static initialiser was run by the kernel's link
///     (Lto.Preinit), its state word laid down at "completed" -- so that a
///     module's code need not test the word before each touch of the type
///     (Lowering.TouchType);
///   * what a small function of the kernel's does -- a struct's getter, an
///     operator -- so that a module's link may inline it and not call it
///     through the GOT for one load or one add (IrLinkOptimizer).
///
/// So each is made a name. The kernel's export table says, for every type
/// its link initialised, `y_init$` + the type's state word's symbol; and for
/// every body it offers a module, `y_body$` + 32 digits of the body's
/// identity + `$` + the function's symbol (the bodies themselves beside the
/// image, `.bodies`: an object holding an IR archive of them). A module that
/// relied on either says so in a note (`.corsac.needs`), and its link turns
/// the notes into references: a word a name in the module's data, bound at
/// load like any other. A kernel that no longer runs the initialiser at its
/// link, or whose function has another body, does not export the name, and
/// the module is refused by it -- at its link against that kernel, and at
/// its load -- instead of running on what is no longer so.
/// </summary>
public static class KernelAssumptions
{
    /// <summary>A unit's or a link's notes of the names it relied on, NUL-ended (ModuleInfo.Note).</summary>
    public const string NeedsSection = ".corsac.needs";

    public const string InitPrefix = "y_init$";
    public const string BodyPrefix = "y_body$";

    /// <summary>What the bodies' file is called: the kernel image's name and this.</summary>
    public const string BodiesSuffix = ".bodies";

    /// <summary>What a type's state word's symbol ends with (BindResult.ReadyField, a native word).</summary>
    public const string ReadyWord = "_StaticReady$_t$V$NInt";

    /// <summary>A body offered a module is at most this many instructions (and calls only what Closed allows).</summary>
    public const int MostInstructions = IrLinkOptimizer.TinyBody;

    public static string InitName(string readySymbol) => InitPrefix + readySymbol;

    /// <summary>
    /// A body's name: 128 bits of its identity (IrArchiveEntry.Identity: the
    /// hash of its record with where its source was taken out, so that a
    /// line added above it, or the sources kept somewhere else, does not
    /// rename it and refuse every module built before), so that two
    /// different bodies of one function never share a name by chance -- one
    /// that did would bind a module to a body it was not built against.
    /// </summary>
    public static string BodyName(string function, BodyHash hash) => BodyPrefix + hash.A.ToString("x16") + hash.B.ToString("x16") + "$" + function;

    /// <summary>How many hex digits of its identity a body's name holds (BodyName), for what takes a name apart.</summary>
    public const int BodyDigits = 32;

    /// <summary>A body the kernel's link may offer: taken from its unit before the link makes units again.</summary>
    public sealed record Offer(string Function, IrArchiveRecord Record, BodyHash Hash);

    /// <summary>
    /// THE BODIES A KERNEL'S UNITS HOLD THAT A MODULE MAY INLINE: a function
    /// its unit calls importable, of at most MostInstructions, that calls
    /// nothing but others offered and the allocator -- an operator its
    /// struct's constructor, which the module's link brings in with it, and
    /// the block the struct it returns is made in -- and has no float code of
    /// its own outside a packed block (X87Notes.Fact.Floats): inlined, that
    /// code would run in a module's function whose notes were made before,
    /// outside every block. (Float code inside the body's own block comes
    /// with its block, as the class library's does, which keeps the
    /// processor's state itself.) Which of them the kernel exports is known
    /// only once the image is laid out (Write).
    /// </summary>
    public static List<Offer> Offered(IEnumerable<ObjectFile> inputs)
    {
        // ONE BODY A FUNCTION, or none: two units' copies of one symbol that
        // differ are two answers to what the kernel runs, and the link keeps
        // one of them by rules of its own. Every copy is looked at, the ones
        // that could not be offered too: a copy too large, or not importable,
        // beside a small one is as much another answer as one of the same
        // size with other contents, and the image may hold that one.
        Dictionary<string, Offer> offers = new(StringComparer.Ordinal);
        Dictionary<string, BodyHash> copies = new(StringComparer.Ordinal);
        HashSet<string> differing = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in inputs)
        {
            IrArchive? archive = IrArchive.Read(obj);
            if (archive is null) continue;
            HashSet<string>? floats = null;
            foreach (IrArchiveEntry entry in archive.Entries.Values)
            {
                if (entry.Key.Kind != IrKey.Function) continue;
                if (copies.TryGetValue(entry.Key.Name, out BodyHash seen)) { if (seen != entry.Hash) differing.Add(entry.Key.Name); }
                else copies.Add(entry.Key.Name, entry.Hash);
                if (!entry.Importable || entry.Instructions > MostInstructions || entry.Identity is not BodyHash identity) { differing.Add(entry.Key.Name); continue; }
                floats ??= X87Notes.Read(obj)?.Where(fact => fact.Floats).Select(fact => fact.Name).ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
                if (floats.Contains(entry.Key.Name)) { differing.Add(entry.Key.Name); continue; }
                if (offers.ContainsKey(entry.Key.Name)) continue;
                offers.Add(entry.Key.Name, new Offer(entry.Key.Name, new IrArchiveRecord(entry.Key.ToString(), true, entry.Instructions, entry.Calls,
                    archive.ReadBody(entry.Key), entry.References, entry.DecodeBytes, Identity: identity), identity));
            }
        }
        return Closed(offers.Values.Where(offer => !differing.Contains(offer.Function)).ToList(), null);
    }

    /// <summary>The offers that call only offers (and, with `exported`, are exported and name only what is): to a fixed point.</summary>
    private static List<Offer> Closed(List<Offer> offers, IReadOnlySet<string>? exported)
    {
        while (true)
        {
            HashSet<string> names = offers.Select(offer => offer.Function).ToHashSet(StringComparer.Ordinal);
            // (The allocator a struct's block is asked of is no body offered:
            // a module calls it already, and its link keeps that block in the
            // frame as its unit's own are kept.)
            List<Offer> kept = offers.Where(offer => offer.Record.Calls.All(call => names.Contains(call) || call is RuntimeAbi.Alloc or RuntimeAbi.AllocLeaf)
                && (exported is null || exported.Contains(offer.Function)
                    && offer.Record.References!.All(reference => exported.Contains(IrArchiveEntry.Named(reference))))).ToList();
            if (kept.Count == offers.Count) return kept;
            offers = kept;
        }
    }

    /// <summary>
    /// The offers the kernel exports, written beside its image, and the name
    /// of each for its export table: only a function that is exported itself
    /// and names nothing that is not -- inlined into a module, every symbol
    /// of the body is the module's to bind, a call it was not inlined at
    /// among them.
    /// </summary>
    public static List<(string Function, string Name)> Write(string image, IReadOnlyList<Offer> offers, IReadOnlySet<string> exported)
    {
        List<Offer> kept = Closed(offers.ToList(), exported).OrderBy(offer => offer.Function, StringComparer.Ordinal).ToList();
        string path = image + BodiesSuffix;
        if (kept.Count == 0) { File.Delete(path); return new(); }
        ObjectFile carrier = new();
        IrArchive.Attach(carrier, kept.Select(offer => offer.Record).ToList());
        ElfWriter.WriteObjectFile(carrier, path);
        return kept.Select(offer => (offer.Function, BodyName(offer.Function, offer.Hash))).ToList();
    }

    /// <summary>The bodies beside a kernel's image; null where it has none (a kernel linked before there were any).</summary>
    public static IrArchive? ReadBodies(string kernelPath)
    {
        string path = kernelPath + BodiesSuffix;
        if (!File.Exists(path)) return null;
        ObjectFile carrier = ElfReader.ReadObjectFile(path, leaveInFile: false);
        // Nothing native in the file for the archive's header to be checked
        // against: it carries the bodies and no code. Each body is checked
        // by its own hash as it is read (IrArchive.ReadBody), and offered
        // only under the name the kernel's table says of that hash.
        carrier.NativeOmitted = true;
        return IrArchive.Read(carrier);
    }

    /// <summary>
    /// A MODULE'S LINK BOUND TO WHAT ITS UNITS ASSUMED: their notes taken out
    /// (and `also`, what the link itself relied on), each name checked
    /// against the kernel linked against, and the references added as an
    /// input of their own. Every link of a module comes through here -- its
    /// units' objects linked (ObjectLinkCommand) and a module compiled and
    /// linked in one step (Linker.LinkModule) alike: a note left unread is a
    /// module that dropped a test and says nothing of it. Null, or what the
    /// kernel does not say.
    /// </summary>
    public static string? Bind(List<(string Name, ObjectFile Object)> inputs, KernelExports kernel, IEnumerable<string>? also = null)
    {
        SortedSet<string> needs = new(ModuleInfo.Take(inputs, NeedsSection), StringComparer.Ordinal);
        if (also is not null) needs.UnionWith(also);
        if (needs.Count == 0) return null;
        string[] unsaid = needs.Where(name => !kernel.Names.Contains(name)).Take(4).ToArray();
        if (unsaid.Length > 0) return "the module assumes of its kernel what " + kernel.Name + " does not say: " + string.Join(", ", unsaid);
        inputs.Add(("the module's assumptions of its kernel", NeedsObject(needs.ToList(), kernel.LongMode)));
        return null;
    }

    /// <summary>
    /// A MODULE'S ASSUMPTIONS AS REFERENCES: a word a name, in data of its
    /// own, each bound by the kernel that loads the module as every name of
    /// its is. Nothing reads the words.
    /// </summary>
    public static ObjectFile NeedsObject(IReadOnlyList<string> names, bool wide)
    {
        ObjectFile made = new();
        Section data = new(".data", SectionKind.Data) { Align = wide ? 8 : 4 };
        int word = wide ? 8 : 4;
        for (int i = 0; i < names.Count; i++)
        {
            data.Relocs.Add(new Relocation(data.Bytes.Count, names[i], 0, wide ? RelocKind.Abs64 : RelocKind.Abs32));
            data.Bytes.AddRange(new byte[word]);
        }
        made.Sections.Add(data);
        // Named, so that the link keeps the section whatever it prunes.
        made.Symbols.Add(new Symbol { Name = "__corsac_kernel_needs", Section = data, Offset = 0, Size = data.Bytes.Count, Global = false });
        return made;
    }
}
