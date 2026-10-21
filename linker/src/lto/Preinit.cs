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
    const int Version = 1;

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

    /// <summary>A unit's notes: the target's array layout, its constructors and its candidates.</summary>
    public sealed record Notes(int WordSize, int ArrayHeaderBytes, int ArrayCountOffset, List<Constructor> Constructors, List<Candidate> Candidates);

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
                all.Add(new Notes(word, header, countAt, constructors, candidates));
            }
            if (take) obj.Sections.RemoveAll(s => s.Name == SectionName);
        }
        return all;
    }

    // ---- the solve -----------------------------------------------------------------

    /// <summary>What the link made of the notes: the object defining every candidate's storage and flag, and the steps that never run.</summary>
    public sealed record Solution(ObjectFile? Data, HashSet<string> FrozenSteps, int Frozen, int Candidates);

    /// <summary>
    /// The notes of these objects solved, the sections taken out of them:
    /// null when no unit wrote any.
    /// </summary>
    public static Solution? Solve(IEnumerable<ObjectFile> objects, bool longMode) => Solve(Take(objects), longMode);

    /// <summary>The same solve, the notes left where they are: what the interrupt check asks before the link lays anything down.</summary>
    public static Solution? Peek(IEnumerable<ObjectFile> objects, bool longMode) => Solve(Read(objects, take: false), longMode);

    private static Solution? Solve(List<Notes> notes, bool longMode)
    {
        if (notes.Count == 0 || notes.All(n => n.Candidates.Count == 0)) return null;
        return new Solver(notes, longMode).Run();
    }

    /// <summary>
    /// The link's data for these inputs, added as an input of its own: every
    /// candidate's storage and flag defined, what could be made laid down.
    /// Every image a link makes asks, a program's and a shared object's
    /// alike: a unit's candidate storage is defined nowhere else.
    /// </summary>
    public static ObjectFile? Lay(IEnumerable<ObjectFile> objects, bool longMode) => Solve(objects, longMode)?.Data;

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

        public Solver(List<Notes> notes, bool longMode)
        {
            _notes = notes;
            _longMode = longMode;
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
            }
            foreach (List<Candidate> list in _byOwner.Values) list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        public Solution Run()
        {
            foreach (string owner in _byOwner.Keys.Order(StringComparer.Ordinal)) SolveOwner(owner);
            return Emit();
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

        /// <summary>
        /// The data: every object laid down, reached from a candidate, then
        /// each candidate's storage -- the object, or nothing -- and its flag.
        /// </summary>
        private Solution Emit()
        {
            // Only what a laid-down candidate reaches: an attempt given up on
            // part of the way left objects nothing names.
            HashSet<Frozen> kept = new();
            Stack<Frozen> walk = new();
            foreach (Value? v in _solved.Values) if (v is Made m && kept.Add(m.Object)) walk.Push(m.Object);
            while (walk.TryPop(out Frozen? f))
                foreach ((int _, Value target) in f.References)
                    if (target is Made m && kept.Add(m.Object)) walk.Push(m.Object);

            ObjectFile obj = new();
            Section data = new(".data", SectionKind.Data) { Align = 16 };
            int n = 0;
            foreach (Frozen f in _objects.Where(kept.Contains))
            {
                Pad(data, 16);
                f.Symbol = "preinit$object" + n++;
                obj.Symbols.Add(new Symbol { Name = f.Symbol, Section = data, Offset = data.Bytes.Count, Size = f.Bytes.Length, Global = false });
                data.Bytes.AddRange(f.Bytes);
            }
            foreach (Frozen f in _objects.Where(kept.Contains))
            {
                long at = obj.Symbols.First(s => s.Name == f.Symbol).Offset;
                foreach ((int offset, Value target) in f.References) Reference(data, at + offset, target);
            }
            HashSet<string> frozenSteps = new(StringComparer.Ordinal);
            int frozen = 0;
            foreach (Candidate c in _byStorage.Values.OrderBy(c => c.Storage, StringComparer.Ordinal))
            {
                // As wide as the field, aligned as the statics' own are (to its size, at most eight).
                Pad(data, System.Math.Min(8, c.Width >= 8 ? 8 : c.Width >= 4 ? 4 : c.Width >= 2 ? 2 : 1));
                obj.Symbols.Add(new Symbol { Name = c.Storage, Section = data, Offset = data.Bytes.Count, Size = c.Width });
                long at = data.Bytes.Count;
                data.Bytes.AddRange(new byte[c.Width]);
                if (_solved.GetValueOrDefault(c.Storage) is Made m)
                {
                    Reference(data, at, m);
                    frozenSteps.Add(c.Step);
                    frozen++;
                }
            }
            foreach (Candidate c in _byStorage.Values.OrderBy(c => c.Storage, StringComparer.Ordinal))
            {
                obj.Symbols.Add(new Symbol { Name = c.Flag, Section = data, Offset = data.Bytes.Count, Size = 1 });
                data.Bytes.Add(_solved.GetValueOrDefault(c.Storage) is Made ? (byte)1 : (byte)0);
            }
            obj.Sections.Add(data);
            return new Solution(obj, frozenSteps, frozen, _byStorage.Count);
        }

        private void Reference(Section data, long at, Value target)
        {
            (string symbol, long addend) = target switch
            {
                At a => (a.Symbol, a.Addend),
                Made m => (m.Object.Symbol!, 0L),
                _ => throw new InvalidOperationException("preinit: a reference to nothing"),
            };
            data.Relocs.Add(new Relocation((int)at, symbol, addend, _longMode ? RelocKind.Abs64 : RelocKind.Abs32));
        }

        private static void Pad(Section data, int align)
        {
            while (data.Bytes.Count % align != 0) data.Bytes.Add(0);
        }
    }
}
