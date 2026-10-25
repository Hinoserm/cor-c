using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// STATIC PREINITIALISATION, as NativeAOT freezes statics: a static whose
/// initialiser only builds objects and arrays out of constants is laid down
/// in the image, made, so that no type initialiser makes it at run time --
/// nothing allocated, and nothing for an interrupt handler that is a type's
/// first touch to make (Hino 2026-10-21).
///
/// Decided at the link, over the whole program: a unit sees another unit's
/// types without their bodies, and a static of one unit is built by
/// constructors of others. Each unit writes what its static initialisers
/// and its constructors do, where they do nothing but store constants,
/// arguments and objects so made (Recipe); the link evaluates those over
/// every unit, and lays down what it could make. The compiler makes each
/// candidate's initialiser a step of its own (Candidate.Step), run by the
/// type initialiser only when the field's flag says the link made nothing
/// for it; and the field's storage is the link's -- a word holding the
/// object made, or nothing -- so a unit is never compiled again for it.
///
/// IN .NET'S ORDER: a type's initialisers run in the order written, and one
/// may read a field another sets later -- null then, in .NET. So a field is
/// laid down only when every candidate before it in its type was: the first
/// that cannot be made, and every one after it, run as written. A static of
/// another type is read only when that one was laid down and is readonly: a
/// field anything may assign is no constant, nor one only some runs make.
/// </summary>
public static class Preinit
{
    public const string SectionName = ".corsac.preinit";
    // One layout, 0, until a release: each type's initialiser named, for the
    // link to run whole (TypeNote).
    const int Version = 0;

    /// <summary>The most bytes the link lays down, and the deepest a recipe is followed.</summary>
    const long MostBytes = 4 << 20;
    const int Deepest = 64;

    /// <summary>What an initialiser or a constructor's argument evaluates to, as far as the link can make it.</summary>
    public abstract record Recipe;
    /// <summary>Anything the link cannot make: a call, a read of a mutable static, a branch.</summary>
    public sealed record Opaque : Recipe;
    public sealed record Null : Recipe;
    /// <summary>A number, stored at the width of what holds it.</summary>
    public sealed record Bits(long Value) : Recipe;
    /// <summary>A symbol's address: a string literal's object, a table laid down as data.</summary>
    public sealed record Address(string Symbol, long Addend) : Recipe;
    /// <summary>A constructor's argument.</summary>
    public sealed record Argument(int Index) : Recipe;
    /// <summary>`new T[N]`: an array of N default elements, its descriptor's vtable at word 0.</summary>
    public sealed record Zeroed(string Vtable, long VtableAddend, int Stride, int Length) : Recipe;
    /// <summary>An array of these elements, each stored at the stride.</summary>
    public sealed record Elements(string Vtable, long VtableAddend, int Stride, Recipe[] Items) : Recipe;
    /// <summary>`new C(arguments)`: an object of Size bytes, its class's vtable at word 0, the constructor run on it.</summary>
    public sealed record Make(string Constructor, string Vtable, long VtableAddend, int Size, Recipe[] Arguments) : Recipe;
    /// <summary>Another candidate's value: the same object, when that one was laid down and is readonly.</summary>
    public sealed record StaticOf(string Storage) : Recipe;

    /// <summary>One thing a constructor does, in order.</summary>
    public abstract record Act;
    /// <summary>A field of the object stored: Width bytes at Offset (a reference at the word).</summary>
    public sealed record Store(int Offset, int Width, Recipe Value) : Act;
    /// <summary>Another constructor run on the same object: the base's, or `this(...)`.</summary>
    public sealed record Chain(string Constructor, Recipe[] Arguments) : Act;

    /// <summary>A constructor the link may run: its symbol, and what it does -- null when it does anything else.</summary>
    public sealed record Constructor(string Name, int Parameters, Act[]? Acts);

    /// <summary>
    /// A static field whose initialiser the link may run: its type's key and
    /// the initialiser's place among the type's, the field's storage and
    /// flag (the link defines both), the step that runs the initialiser (its
    /// interrupt facts' name), the storage's width, whether the field is
    /// readonly, and the value.
    /// </summary>
    public sealed record Candidate(string Owner, int Order, string Storage, string Flag, string Step, int Width, bool ReadOnly, Recipe Value);

    /// <summary>
    /// A TYPE WHOSE WHOLE INITIALISER THE LINK MAY RUN (stage 2): its key, its
    /// StaticInit$ (what a touch calls), its StaticInitBody$ (what the link
    /// runs), its state word, whether it is beforefieldinit -- no static
    /// constructor of its own, so .NET may run its initialiser at startup --
    /// and the two routines' names as the interrupt facts give them.
    /// </summary>
    public sealed record TypeNote(string Owner, string Initializer, string Body, string Ready, bool BeforeFieldInit,
        string InitializerFact, string BodyFact);

    /// <summary>
    /// A STATIC'S STORAGE AND WHOSE IT IS: its symbol (a field's word, a
    /// table laid down for one), its type's key, and whether it is readonly
    /// -- what a run may read of another type's (PreinitWorld).
    /// </summary>
    public sealed record StaticNote(string Symbol, string Owner, bool ReadOnly);

    /// <summary>A unit's notes: the target's array layout, its constructors, its candidates, its types, its statics and its strings' descriptor.</summary>
    public sealed record Notes(int WordSize, int ArrayHeaderBytes, int ArrayCountOffset, List<Constructor> Constructors, List<Candidate> Candidates,
        List<TypeNote> Types, List<StaticNote> Statics, string StringDescriptor);

    /// <summary>
    /// WHAT RUNS A ROUTINE OVER THE WORLD: the compiler's, which reads each
    /// unit's IR (Corsac.Lang.Lto has none of its own). The routine is a
    /// static one of no parameters; anything it cannot do throws
    /// PreinitWorld.Refused.
    /// </summary>
    public interface IMachine
    {
        void Run(PreinitWorld world, string routine);
    }

    /// <summary>The machine the link runs initialisers with; none, and only stage 1 is done.</summary>
    public static IMachine? Machine { get; set; }

    // ---- the section ---------------------------------------------------------------

    public static void Attach(ObjectFile obj, Notes notes)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(notes.WordSize);
            writer.Write(notes.ArrayHeaderBytes);
            writer.Write(notes.ArrayCountOffset);
            writer.Write(notes.Constructors.Count);
            foreach (Constructor c in notes.Constructors)
            {
                writer.Write(c.Name);
                writer.Write(c.Parameters);
                writer.Write(c.Acts is not null);
                if (c.Acts is null) continue;
                writer.Write(c.Acts.Length);
                foreach (Act act in c.Acts)
                {
                    switch (act)
                    {
                        case Store store:
                            writer.Write((byte)0); writer.Write(store.Offset); writer.Write(store.Width); Write(writer, store.Value);
                            break;
                        case Chain chain:
                            writer.Write((byte)1); writer.Write(chain.Constructor); WriteAll(writer, chain.Arguments);
                            break;
                    }
                }
            }
            writer.Write(notes.Candidates.Count);
            foreach (Candidate c in notes.Candidates)
            {
                writer.Write(c.Owner); writer.Write(c.Order); writer.Write(c.Storage); writer.Write(c.Flag); writer.Write(c.Step);
                writer.Write(c.Width); writer.Write(c.ReadOnly); Write(writer, c.Value);
            }
            writer.Write(notes.Types.Count);
            foreach (TypeNote t in notes.Types)
            {
                writer.Write(t.Owner); writer.Write(t.Initializer); writer.Write(t.Body); writer.Write(t.Ready); writer.Write(t.BeforeFieldInit);
                writer.Write(t.InitializerFact); writer.Write(t.BodyFact);
            }
            writer.Write(notes.Statics.Count);
            foreach (StaticNote st in notes.Statics)
            {
                writer.Write(st.Symbol); writer.Write(st.Owner); writer.Write(st.ReadOnly);
            }
            writer.Write(notes.StringDescriptor);
        }
        Section section = new(SectionName, SectionKind.Note) { Align = 1 };
        section.Bytes.AddRange(stream.ToArray());
        obj.Sections.Add(section);
    }

    private static void WriteAll(BinaryWriter writer, Recipe[] recipes)
    {
        writer.Write(recipes.Length);
        foreach (Recipe r in recipes) Write(writer, r);
    }

    private static void Write(BinaryWriter writer, Recipe recipe)
    {
        switch (recipe)
        {
            case Null: writer.Write((byte)1); break;
            case Bits b: writer.Write((byte)2); writer.Write(b.Value); break;
            case Address a: writer.Write((byte)3); writer.Write(a.Symbol); writer.Write(a.Addend); break;
            case Argument a: writer.Write((byte)4); writer.Write(a.Index); break;
            case Zeroed z: writer.Write((byte)5); writer.Write(z.Vtable); writer.Write(z.VtableAddend); writer.Write(z.Stride); writer.Write(z.Length); break;
            case Elements e: writer.Write((byte)6); writer.Write(e.Vtable); writer.Write(e.VtableAddend); writer.Write(e.Stride); WriteAll(writer, e.Items); break;
            case Make m: writer.Write((byte)7); writer.Write(m.Constructor); writer.Write(m.Vtable); writer.Write(m.VtableAddend); writer.Write(m.Size); WriteAll(writer, m.Arguments); break;
            case StaticOf s: writer.Write((byte)8); writer.Write(s.Storage); break;
            default: writer.Write((byte)0); break;
        }
    }

    private static Recipe[] ReadAll(BinaryReader reader)
    {
        Recipe[] all = new Recipe[reader.ReadInt32()];
        for (int i = 0; i < all.Length; i++) all[i] = Read(reader);
        return all;
    }

    private static Recipe Read(BinaryReader reader) => reader.ReadByte() switch
    {
        1 => new Null(),
        2 => new Bits(reader.ReadInt64()),
        3 => new Address(reader.ReadString(), reader.ReadInt64()),
        4 => new Argument(reader.ReadInt32()),
        5 => new Zeroed(reader.ReadString(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32()),
        6 => new Elements(reader.ReadString(), reader.ReadInt64(), reader.ReadInt32(), ReadAll(reader)),
        7 => new Make(reader.ReadString(), reader.ReadString(), reader.ReadInt64(), reader.ReadInt32(), ReadAll(reader)),
        8 => new StaticOf(reader.ReadString()),
        _ => new Opaque(),
    };

    /// <summary>
    /// EVERY CANDIDATE'S STORAGE AND ITS VALUE, the notes left where they
    /// are: the statics the link will define (Lay), for the region solvers
    /// before it has (RegionConstants.ClosedStatic).
    /// </summary>
    public static Dictionary<string, Recipe> CandidateValues(IEnumerable<ObjectFile> objects)
    {
        Dictionary<string, Recipe> values = new(StringComparer.Ordinal);
        foreach (Notes notes in Read(objects, take: false))
            foreach (Candidate c in notes.Candidates) values.TryAdd(c.Storage, c.Value);
        return values;
    }

    /// <summary>Every input's notes, the sections taken out (the image carries none).</summary>
    public static List<Notes> Take(IEnumerable<ObjectFile> objects) => Read(objects, take: true);

    private static List<Notes> Read(IEnumerable<ObjectFile> objects, bool take)
    {
        List<Notes> all = new();
        foreach (ObjectFile obj in objects)
        {
            foreach (Section section in obj.Sections.Where(s => s.Name == SectionName))
            {
                using Stream stream = section.OpenRead();
                using BinaryReader reader = new(stream, Encoding.UTF8);
                if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
                int word = reader.ReadInt32(), header = reader.ReadInt32(), countAt = reader.ReadInt32();
                List<Constructor> constructors = new();
                for (int i = reader.ReadInt32(); i > 0; i--)
                {
                    string name = reader.ReadString();
                    int parameters = reader.ReadInt32();
                    if (!reader.ReadBoolean()) { constructors.Add(new Constructor(name, parameters, null)); continue; }
                    Act[] acts = new Act[reader.ReadInt32()];
                    for (int k = 0; k < acts.Length; k++)
                        acts[k] = reader.ReadByte() == 0
                            ? new Store(reader.ReadInt32(), reader.ReadInt32(), Read(reader))
                            : new Chain(reader.ReadString(), ReadAll(reader));
                    constructors.Add(new Constructor(name, parameters, acts));
                }
                List<Candidate> candidates = new();
                for (int i = reader.ReadInt32(); i > 0; i--)
                    candidates.Add(new Candidate(reader.ReadString(), reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadString(),
                        reader.ReadInt32(), reader.ReadBoolean(), Read(reader)));
                List<TypeNote> types = new();
                for (int i = reader.ReadInt32(); i > 0; i--)
                    types.Add(new TypeNote(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadBoolean(),
                        reader.ReadString(), reader.ReadString()));
                List<StaticNote> statics = new();
                for (int i = reader.ReadInt32(); i > 0; i--)
                    statics.Add(new StaticNote(reader.ReadString(), reader.ReadString(), reader.ReadBoolean()));
                all.Add(new Notes(word, header, countAt, constructors, candidates, types, statics, reader.ReadString()));
            }
            if (take) obj.Sections.RemoveAll(s => s.Name == SectionName);
        }
        return all;
    }

    // ---- the solve -----------------------------------------------------------------

    /// <summary>
    /// What the link made of the notes: the object defining every candidate's
    /// storage and flag and every unit datum a run left changed (Overrides),
    /// the steps and initialisers that never run, and what each kept run read
    /// of other types' mutable statics (Report).
    /// </summary>
    public sealed record Solution(HashSet<string> FrozenSteps, int Frozen, int Candidates, int TypesRun, List<string> Report, string Signature,
        Dictionary<string, HashSet<string>> Edges)
    {
        /// <summary>The solve itself, which lays its data down when the link knows what it keeps (Lay).</summary>
        internal object? Made { get; init; }
    }

    /// <summary>
    /// WHAT A STATIC LAID DOWN MADE REACHES, for the link-time optimiser's
    /// walk (IrReachability): a static of a type made at the link -- its
    /// word, its state word -- reaches every descriptor and routine its
    /// objects name, once it is reached itself. Named as roots outright,
    /// every type's whole library of statics was kept whether the program
    /// touched it or not, and with them every type they made counted made.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<string>>? LastEdges => _last?.Edges;

    /// <summary>
    /// The same solve, the notes left where they are: what the interrupt
    /// check asks before the link lays anything down -- and what the link
    /// then lays down. ONE SOLVE A LINK: the link-time optimiser may make
    /// units again between the check and the laying down, and what the
    /// check was told is never made at run time must be what is laid down.
    /// </summary>
    public static Solution? Peek(IEnumerable<ObjectFile> objects, bool longMode)
    {
        List<ObjectFile> inputs = objects.ToList();
        List<Notes> notes = Read(inputs, take: false);
        Solution? solved = Solve(inputs, notes, longMode || inputs.Any(TargetContract.IsLongMode));
        _last = solved;
        // SAID AT THE CHECK, which may refuse the link before anything is laid down.
        if (solved is not null) Report(solved);
        return solved;
    }

    private static void Report(Solution solved)
    {
        if (!Switches.PreinitReport) return;
        Console.Error.WriteLine($"preinit: {solved.Frozen} of {solved.Candidates} statics laid down made, {solved.TypesRun} types run whole");
        foreach (string line in solved.Report) Console.Error.WriteLine("preinit: " + line);
    }

    /// <summary>Every name the inputs' code and data refer to: what the image keeps of the link's solve.</summary>
    private static HashSet<string> Referenced(List<ObjectFile> inputs)
    {
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in inputs)
            foreach (Section section in obj.Sections)
                if (section.Kind != SectionKind.Note)
                    foreach (Relocation r in section.Relocs) named.Add(r.Symbol);
        return named;
    }

    private static Solution? _last;

    private static Solution? Solve(List<ObjectFile> inputs, List<Notes> notes, bool longMode)
    {
        if (notes.Count == 0 || notes.All(n => n.Candidates.Count == 0 && n.Types.Count == 0)) return null;
        return new Solver(notes, longMode, inputs).Run();
    }

    /// <summary>What a link's notes name: the same for the units as compiled and as made again.</summary>
    private static string Signature(List<Notes> notes)
        => string.Join("\n", notes.SelectMany(n => n.Candidates.Select(c => c.Storage).Concat(n.Types.Select(t => t.Owner))).Distinct().Order(StringComparer.Ordinal));

    /// <summary>
    /// The link's data for these inputs, added as an input of its own: every
    /// candidate's storage and flag defined, what could be made laid down,
    /// and each unit datum a kept run changed defined again there -- the
    /// unit's own definition of it renamed out of the way, so that every
    /// reference finds the link's. Every image a link makes asks, a
    /// program's and a shared object's alike: a unit's candidate storage is
    /// defined nowhere else. The check's solve when there was one over the
    /// same notes; a solve of its own otherwise.
    /// </summary>
    public static ObjectFile? Lay(IEnumerable<ObjectFile> objects, bool longMode)
    {
        List<ObjectFile> inputs = objects.ToList();
        List<Notes> notes = Take(inputs);
        Solution? solved = _last is { } last && last.Signature == Signature(notes) ? last : null;
        _last = null;
        if (solved is null && (solved = Solve(inputs, notes, longMode)) is not null) Report(solved);
        if (solved is null || solved.Made is not Solver made) return null;
        // ONLY WHAT THE IMAGE STILL NAMES: a type made at the link that no
        // kept code touches is laid down by nobody, and its unit's own data
        // stays as it was.
        HashSet<string> referenced = Referenced(inputs);
        var (data, overrides, aliases, said) = made.Emit(name => referenced.Contains(name));
        if (Switches.PreinitReport) foreach (string line in said) Console.Error.WriteLine("preinit: " + line);
        if (overrides.Count > 0)
            foreach (ObjectFile obj in inputs)
                for (int i = 0; i < obj.Symbols.Count; i++)
                {
                    Symbol s = obj.Symbols[i];
                    if (!s.IsDefined || !overrides.Contains(s.Name)) continue;
                    obj.Symbols[i] = new Symbol { Name = s.Name + "\u0001preinit-replaced", Section = s.Section, Offset = s.Offset, Size = s.Size, IsFunction = s.IsFunction, Global = false };
                }
        // A UNIT'S OWN SYMBOL the link's data names, given a name every unit
        // reaches, at the same place -- in that unit, which the link must
        // still have: one made again since the solve is another object.
        foreach ((ObjectFile unit, string local, string alias) in aliases)
        {
            if (!inputs.Any(o => ReferenceEquals(o, unit)))
                throw new LinkException(new[] { "preinit: '" + local + "', a unit's own, is named by the statics laid down, and its unit was made again since" });
            Symbol at = unit.Symbols.First(x => x.IsDefined && !x.Global && x.Name == local);
            unit.Symbols.Add(new Symbol { Name = alias, Section = at.Section, Offset = at.Offset, Size = at.Size, IsFunction = at.IsFunction });
        }
        return data;
    }

    /// <summary>A value the link made: a number, an address, or an object it lays down.</summary>
    private abstract record Value;
    private sealed record NullValue : Value;
    private sealed record Number(long Bits) : Value;
    private sealed record At(string Symbol, long Addend) : Value;
    private sealed record Made(Frozen Object) : Value;

    /// <summary>An object laid down: its bytes and the addresses in them, named in the data once placed.</summary>
    private sealed class Frozen
    {
        public required byte[] Bytes;
        public readonly List<(int Offset, Value Target)> References = new();
        public string? Symbol;
    }

    private sealed class Solver
    {
        private readonly List<Notes> _notes;
        private readonly bool _longMode;
        private readonly int _word;
        private readonly int _arrayHeader, _countAt;
        private readonly Dictionary<string, Constructor> _constructors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Candidate> _byStorage = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Candidate>> _byOwner = new(StringComparer.Ordinal);
        // Each type's candidates as solved: the value laid down, or null for one run at run time.
        private readonly Dictionary<string, Value?> _solved = new(StringComparer.Ordinal);
        private readonly HashSet<string> _solving = new(StringComparer.Ordinal);
        // The types whose candidates are being solved, for a read of one's own or another's statics.
        private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
        private readonly List<Frozen> _objects = new();
        private long _bytes;
        private readonly List<ObjectFile> _inputs;
        private readonly Dictionary<string, TypeNote> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StaticNote> _statics = new(StringComparer.Ordinal);
        private string? _stringDescriptor;

        public Solver(List<Notes> notes, bool longMode, List<ObjectFile> inputs)
        {
            _notes = notes;
            _longMode = longMode;
            _inputs = inputs;
            _word = notes[0].WordSize;
            _arrayHeader = notes[0].ArrayHeaderBytes;
            _countAt = notes[0].ArrayCountOffset;
            foreach (Notes n in notes)
            {
                if (n.WordSize != _word || n.ArrayHeaderBytes != _arrayHeader || n.ArrayCountOffset != _countAt)
                    throw new LinkException(new[] { "preinitialisation notes of two layouts in one link" });
                foreach (Constructor c in n.Constructors) _constructors.TryAdd(c.Name, c);
                foreach (Candidate c in n.Candidates)
                {
                    // A generic type's static, written by every unit that made the copy: one.
                    if (!_byStorage.TryAdd(c.Storage, c)) continue;
                    (_byOwner.TryGetValue(c.Owner, out List<Candidate>? list) ? list : _byOwner[c.Owner] = new()).Add(c);
                }
                foreach (TypeNote t in n.Types) _types.TryAdd(t.Owner, t);
                foreach (StaticNote st in n.Statics) _statics.TryAdd(st.Symbol, st);
                _stringDescriptor ??= n.StringDescriptor;
            }
            foreach (List<Candidate> list in _byOwner.Values) list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        public Solution Run()
        {
            foreach (string owner in _byOwner.Keys.Order(StringComparer.Ordinal)) SolveOwner(owner);
            PreinitWorld world = World();
            HashSet<string> frozenSteps = new(StringComparer.Ordinal);
            int typesRun = 0;
            if (Machine is IMachine machine && _types.Count > 0)
            {
                // EACH TYPE IN TURN, by its key, each run over what the runs
                // before it left: the order the image's startup would have
                // made them in, had it made them all at startup.
                foreach (TypeNote note in _types.Values.OrderBy(t => t.Owner, StringComparer.Ordinal))
                {
                    if (!note.BeforeFieldInit || world.InitializerOf(note.Initializer) is not { Frozen: false } entry) continue;
                    world.Begin();
                    try
                    {
                        world.Enter(entry, body => machine.Run(world, body));
                        foreach (PreinitWorld.TypeEntry made in world.Commit())
                        {
                            typesRun++;
                            if (Switches.PreinitReport) world.Report.Add(made.Note.Owner + " made at the link");
                            frozenSteps.Add(made.Note.InitializerFact);
                            frozenSteps.Add(made.Note.BodyFact);
                            if (_byOwner.TryGetValue(made.Note.Owner, out List<Candidate>? steps)) foreach (Candidate c in steps) frozenSteps.Add(c.Step);
                        }
                    }
                    catch (PreinitWorld.Refused refused)
                    {
                        world.Rollback();
                        if (Switches.PreinitReport) world.Report.Add(note.Owner + " runs at its first touch: " + refused.Message);
                    }
                }
            }
            return Finish(world, frozenSteps, typesRun);
        }

        /// <summary>
        /// THE WORLD STAGE 1 LEFT: each candidate's storage holding what was
        /// laid down for it, its flag set, and every object so made a region.
        /// Every type's initialiser and state word named.
        /// </summary>
        private PreinitWorld World()
        {
            PreinitWorld world = new(_inputs, _word, _countAt, _longMode) { StringDescriptor = _stringDescriptor };
            foreach (StaticNote st in _statics.Values) world.AddStatic(st);
            foreach (TypeNote t in _types.Values) world.AddType(t);
            Dictionary<Frozen, PreinitWorld.Region> regions = new(ReferenceEqualityComparer.Instance);
            PreinitWorld.Region RegionOf(Frozen f, string owner)
            {
                if (regions.TryGetValue(f, out PreinitWorld.Region? had)) return had;
                PreinitWorld.Region region = world.Adopt((byte[])f.Bytes.Clone(), owner);
                regions[f] = region;
                foreach ((int offset, Value target) in f.References) region.Refs[offset] = Of(target, owner);
                return region;
            }
            PreinitWorld.Val Of(Value v, string owner) => v switch
            {
                Made m => new PreinitWorld.Ptr(RegionOf(m.Object, owner), 0),
                At a => PreinitWorld.Lazy(a.Symbol, a.Addend),
                Number n => new PreinitWorld.Num(n.Bits),
                _ => new PreinitWorld.Num(0),
            };
            foreach (Candidate c in _byStorage.Values.OrderBy(c => c.Storage, StringComparer.Ordinal))
            {
                PreinitWorld.Region storage = world.DefineStorage(c.Storage, c.Width, c.Owner);
                PreinitWorld.Region flag = world.DefineStorage(c.Flag, 1, c.Owner);
                if (_solved.GetValueOrDefault(c.Storage) is Made m)
                {
                    storage.Refs[0] = Of(m, c.Owner);
                    flag.Bytes[0] = 1;
                }
            }
            return world;
        }

        /// <summary>A type's candidates in order, each laid down until the first that cannot be.</summary>
        private void SolveOwner(string owner)
        {
            List<Candidate> list = _byOwner[owner];
            if (list.All(c => _solved.ContainsKey(c.Storage)) || !_owners.Add(owner)) return;
            bool open = true;
            foreach (Candidate c in list)
            {
                if (_solved.ContainsKey(c.Storage)) { open &= _solved[c.Storage] is not null; continue; }
                Value? value = null;
                if (open)
                {
                    _solving.Add(c.Storage);
                    value = Evaluate(c.Value, System.Array.Empty<Value>(), 0);
                    _solving.Remove(c.Storage);
                    // Only an object or null, into a word: a number laid down is the
                    // initialiser's job no more than a constant's, and nothing
                    // allocates for one.
                    if (value is not (Made or NullValue) || c.Width != _word) value = null;
                }
                _solved[c.Storage] = value;
                open &= value is not null;
            }
            _owners.Remove(owner);
        }

        private Value? Evaluate(Recipe recipe, Value[] arguments, int depth)
        {
            if (depth > Deepest || _bytes > MostBytes) return null;
            switch (recipe)
            {
                case Null: return new NullValue();
                case Bits b: return new Number(b.Value);
                case Address a: return new At(a.Symbol, a.Addend);
                case Argument a: return a.Index >= 0 && a.Index < arguments.Length ? arguments[a.Index] : null;
                case StaticOf s:
                {
                    if (!_byStorage.TryGetValue(s.Storage, out Candidate? other) || !other.ReadOnly || _solving.Contains(s.Storage)) return null;
                    // A TYPE BEING SOLVED, its own or one whose candidate led
                    // here: what is settled already -- its earlier fields, in
                    // the order they run -- and nothing for a later one, which
                    // its initialiser would read as null.
                    if (!_owners.Contains(other.Owner)) SolveOwner(other.Owner);
                    return _solved.GetValueOrDefault(s.Storage) is Made made ? made : null;
                }
                case Zeroed z:
                {
                    if (z.Length < 0 || z.Stride <= 0) return null;
                    Frozen array = Array(z.Vtable, z.VtableAddend, z.Stride, z.Length);
                    return new Made(array);
                }
                case Elements e:
                {
                    Value?[] items = new Value?[e.Items.Length];
                    for (int i = 0; i < items.Length; i++)
                        if ((items[i] = Evaluate(e.Items[i], arguments, depth + 1)) is null) return null;
                    Frozen array = Array(e.Vtable, e.VtableAddend, e.Stride, items.Length);
                    for (int i = 0; i < items.Length; i++)
                        if (!Put(array, _arrayHeader + i * e.Stride, e.Stride, items[i]!)) return null;
                    return new Made(array);
                }
                case Make m:
                {
                    if (m.Size < _word) return null;
                    Value[] given = new Value[m.Arguments.Length];
                    for (int i = 0; i < given.Length; i++)
                        if (Evaluate(m.Arguments[i], arguments, depth + 1) is not Value v) return null; else given[i] = v;
                    Frozen made = Object(m.Vtable, m.VtableAddend, m.Size);
                    return Run(m.Constructor, made, given, depth + 1) ? new Made(made) : null;
                }
                default:
                    return null;
            }
        }

        /// <summary>A constructor run on the object being made: false when it does anything the link cannot.</summary>
        private bool Run(string name, Frozen made, Value[] arguments, int depth)
        {
            if (depth > Deepest || !_constructors.TryGetValue(name, out Constructor? c) || c.Acts is null || c.Parameters != arguments.Length) return false;
            foreach (Act act in c.Acts)
            {
                switch (act)
                {
                    case Store store:
                        if (store.Offset < _word || store.Offset + store.Width > made.Bytes.Length
                            || Evaluate(store.Value, arguments, depth + 1) is not Value v || !Put(made, store.Offset, store.Width, v)) return false;
                        break;
                    case Chain chain:
                    {
                        Value[] given = new Value[chain.Arguments.Length];
                        for (int i = 0; i < given.Length; i++)
                            if (Evaluate(chain.Arguments[i], arguments, depth + 1) is not Value argument) return false; else given[i] = argument;
                        if (!Run(chain.Constructor, made, given, depth + 1)) return false;
                        break;
                    }
                }
            }
            return true;
        }

        /// <summary>A value stored into an object: a number at its width, an address at the word.</summary>
        private bool Put(Frozen into, int offset, int width, Value value)
        {
            into.References.RemoveAll(r => r.Offset == offset);
            System.Array.Clear(into.Bytes, offset, width);
            switch (value)
            {
                case NullValue: return true;
                case Number n:
                    for (int b = 0; b < width; b++) into.Bytes[offset + b] = (byte)(n.Bits >> (8 * b));
                    return true;
                case At or Made:
                    if (width != _word) return false;
                    into.References.Add((offset, value));
                    return true;
                default:
                    return false;
            }
        }

        private Frozen Array(string vtable, long addend, int stride, int length)
        {
            int bytes = checked(_arrayHeader + stride * length);
            bytes = (bytes + _word - 1) / _word * _word;
            Frozen array = Object(vtable, addend, System.Math.Max(bytes, _arrayHeader + _word));
            for (int b = 0; b < 4; b++) array.Bytes[_countAt + b] = (byte)(length >> (8 * b));
            return array;
        }

        private Frozen Object(string vtable, long addend, int size)
        {
            Frozen made = new() { Bytes = new byte[(size + _word - 1) / _word * _word] };
            made.References.Add((0, new At(vtable, addend)));
            _objects.Add(made);
            _bytes += made.Bytes.Length;
            return made;
        }

        // What the solve left, laid down when the link knows what it keeps (Emit).
        private PreinitWorld? _world;
        private readonly Dictionary<string, PreinitWorld.TypeEntry> _frozenTypes = new(StringComparer.Ordinal);

        /// <summary>
        /// The solve's answer: the steps and initialisers that never run, the
        /// counts, and what each static laid down reaches (Solution.Edges) --
        /// a type made at the link as one: its state word and every static
        /// of it reach everything its objects name.
        /// </summary>
        private Solution Finish(PreinitWorld world, HashSet<string> frozenSteps, int typesRun)
        {
            _world = world;
            foreach (PreinitWorld.TypeEntry t in world.Types) if (t.Frozen) _frozenTypes.TryAdd(t.Note.Owner, t);
            int frozen = 0;
            foreach (Candidate c in _byStorage.Values)
                if (_solved.GetValueOrDefault(c.Storage) is Made)
                {
                    frozenSteps.Add(c.Step);
                    frozen++;
                }
            Dictionary<string, HashSet<string>> edges = new(StringComparer.Ordinal);
            List<PreinitWorld.Region> roots = world.Storages.Concat(world.Overridden).ToList();
            foreach (var owned in roots.GroupBy(r => r.Maker ?? ""))
            {
                if (_frozenTypes.TryGetValue(owned.Key, out PreinitWorld.TypeEntry? type))
                {
                    HashSet<string> named = world.Names(owned);
                    foreach (PreinitWorld.Region r in owned) edges[r.Symbol!] = named;
                    edges[type.Note.Ready] = named;
                }
                else
                    foreach (PreinitWorld.Region r in owned) edges[r.Symbol!] = world.Names(new[] { r });
            }
            return new Solution(frozenSteps, frozen, _byStorage.Count, typesRun, world.Report, Signature(_notes), edges) { Made = this };
        }

        /// <summary>
        /// THE DATA, for what the image still names (`referenced`): a type made
        /// at the link is laid down whole when anything names its state word or
        /// one of its statics, and not at all otherwise -- its unit's own
        /// definitions stay, and it is made at its first touch as before. A
        /// candidate of a type not made whole is laid down when its own word
        /// is named. Then every object those reach; each unit datum kept,
        /// defined again as the run left it; each candidate's storage -- the
        /// object, or nothing -- and its flag.
        /// </summary>
        internal (ObjectFile Data, HashSet<string> Overrides, List<(ObjectFile Unit, string Local, string Alias)> Aliases, List<string> Said)
            Emit(Func<string, bool> referenced)
        {
            PreinitWorld world = _world!;
            HashSet<string> keptTypes = new(StringComparer.Ordinal);
            List<PreinitWorld.Region> state = world.Storages.Concat(world.Overridden).ToList();
            foreach (var owned in state.GroupBy(r => r.Maker ?? ""))
                if (_frozenTypes.TryGetValue(owned.Key, out PreinitWorld.TypeEntry? type)
                    && (referenced(type.Note.Ready) || owned.Any(r => referenced(r.Symbol!))))
                    keptTypes.Add(owned.Key);
            bool Kept(PreinitWorld.Region r) => _frozenTypes.ContainsKey(r.Maker ?? "") ? keptTypes.Contains(r.Maker!) : referenced(r.Symbol!);
            List<PreinitWorld.Region> roots = state.Where(Kept).OrderBy(r => r.Symbol, StringComparer.Ordinal).ToList();
            // Only what they reach: an attempt given up on part of the way left objects nothing names.
            List<PreinitWorld.Region> objects = new();
            HashSet<PreinitWorld.Region> kept = new(ReferenceEqualityComparer.Instance);
            Stack<PreinitWorld.Region> walk = new();
            void Reach(PreinitWorld.Region r)
            {
                foreach (int offset in r.Refs.Keys.Order())
                    if (world.Target(r.Refs[offset]).Object is PreinitWorld.Region o && kept.Add(o)) { objects.Add(o); walk.Push(o); }
            }
            foreach (PreinitWorld.Region r in roots) Reach(r);
            while (walk.TryPop(out PreinitWorld.Region? o)) Reach(o);

            // WHAT IT COSTS THE IMAGE, the objects of four kilobytes and more by
            // the type that made them: each was a boot's allocation before, and
            // is file and memory both now.
            List<string> said = new();
            if (Switches.PreinitReport)
            {
                foreach (var made in objects.Where(o => o.Bytes.Length >= 4096).GroupBy(o => o.Maker ?? "?").OrderByDescending(g => g.Sum(o => o.Bytes.Length)))
                    said.Add(made.Key + " lays down " + made.Count() + " objects of " + made.Sum(o => o.Bytes.Length) + " bytes");
                said.Add(keptTypes.Count + " of " + _frozenTypes.Count + " types made at the link are named by the image; objects laid down: " + objects.Count + ", "
                    + objects.Sum(o => (long)o.Bytes.Length) + " bytes; unit data replaced: " + roots.Where(r => r.Kind == PreinitWorld.RegionKind.Data).Sum(r => (long)r.Bytes.Length) + " bytes");
            }
            ObjectFile obj = new();
            Section data = new(".data", SectionKind.Data) { Align = 16 };
            Dictionary<PreinitWorld.Region, string> names = new(ReferenceEqualityComparer.Instance);
            List<(long At, PreinitWorld.Region Region)> placed = new();
            int n = 0;
            foreach (PreinitWorld.Region o in objects)
            {
                Pad(data, 16);
                names[o] = "preinit$object" + n++;
                obj.Symbols.Add(new Symbol { Name = names[o], Section = data, Offset = data.Bytes.Count, Size = o.Bytes.Length, Global = false });
                placed.Add((data.Bytes.Count, o));
                data.Bytes.AddRange(o.Bytes);
            }
            HashSet<string> overrides = new(StringComparer.Ordinal);
            foreach (PreinitWorld.Region r in roots.Where(r => r.Kind == PreinitWorld.RegionKind.Data))
            {
                Pad(data, System.Math.Max(r.Align, _word));
                obj.Symbols.Add(new Symbol { Name = r.Symbol!, Section = data, Offset = data.Bytes.Count, Size = r.Bytes.Length });
                placed.Add((data.Bytes.Count, r));
                data.Bytes.AddRange(r.Bytes);
                overrides.Add(r.Symbol!);
            }
            HashSet<PreinitWorld.Region> keptStorage = new(roots.Where(r => r.Kind == PreinitWorld.RegionKind.Storage), ReferenceEqualityComparer.Instance);
            foreach (Candidate c in _byStorage.Values.OrderBy(c => c.Storage, StringComparer.Ordinal))
            {
                // As wide as the field, aligned as the statics' own are (to its size, at most eight).
                Pad(data, System.Math.Min(8, c.Width >= 8 ? 8 : c.Width >= 4 ? 4 : c.Width >= 2 ? 2 : 1));
                PreinitWorld.Region storage = world.DefineStorage(c.Storage, c.Width, c.Owner);
                obj.Symbols.Add(new Symbol { Name = c.Storage, Section = data, Offset = data.Bytes.Count, Size = c.Width });
                // NOT KEPT, NOTHING IN IT: the step runs and makes the field.
                if (keptStorage.Contains(storage)) placed.Add((data.Bytes.Count, storage));
                data.Bytes.AddRange(keptStorage.Contains(storage) ? storage.Bytes : new byte[c.Width]);
            }
            foreach (Candidate c in _byStorage.Values.OrderBy(c => c.Storage, StringComparer.Ordinal))
            {
                PreinitWorld.Region flag = world.DefineStorage(c.Flag, 1, c.Owner);
                PreinitWorld.Region storage = world.DefineStorage(c.Storage, c.Width, c.Owner);
                obj.Symbols.Add(new Symbol { Name = c.Flag, Section = data, Offset = data.Bytes.Count, Size = 1 });
                data.Bytes.Add(keptStorage.Contains(storage) ? flag.Bytes[0] : (byte)0);
            }
            foreach ((long at, PreinitWorld.Region r) in placed)
                foreach ((int offset, PreinitWorld.Val v) in r.Refs.OrderBy(p => p.Key))
                {
                    (string? symbol, long addend, PreinitWorld.Region? o) = world.Target(v);
                    data.Relocs.Add(new Relocation((int)(at + offset), symbol ?? names[o!], addend, _longMode ? RelocKind.Abs64 : RelocKind.Abs32));
                }
            obj.Sections.Add(data);
            return (obj, overrides, world.Aliases, said);
        }

        private static void Pad(Section data, int align)
        {
            while (data.Bytes.Count % align != 0) data.Bytes.Add(0);
        }
    }
}
