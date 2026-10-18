#nullable enable
namespace Corsac.Lang;

/// <summary>
/// WHAT A CONSTRUCTION OF A GENERIC INTERFACE IS AT RUN TIME, and which of
/// them need slots of their own.
///
/// Every construction of one interface template shares the template's slots
/// (Binder's numbering): the code generic over T is compiled once for every
/// reference T, and calls IEnumerable&lt;__canon&gt;.GetEnumerator whatever T
/// is. So IEnumerable&lt;Group&gt; and IEnumerable&lt;KeyValuePair&lt;string,
/// Group&gt;&gt; were one slot, and a class implementing both -- .NET's
/// GroupCollection, a C : IComparable&lt;int&gt;, IComparable&lt;string&gt; --
/// had one member's call run the other's.
///
/// A construction's FORM is the construction with every argument that is a
/// reference, at any depth, read as the one machine word the shared code sees:
/// IEnumerable`1&lt;KeyValuePair`2&lt;__canon,__canon&gt;&gt; for every
/// dictionary's entries, IComparable`1&lt;int&gt; for an int's,
/// IEnumerable`1&lt;__canon&gt; for every sequence of objects. Code that
/// calls through a construction calls through its form, generic or not, and
/// one form is one set of slots: those of the template where the form is all
/// words, and a block of its own where the form is CONFLICTED -- some class
/// or struct implements it beside another form of the same template.
///
/// Which forms are conflicted is read from the declarations alone, here, by
/// their syntax: the same answer in a compile of the whole program and in
/// every unit compiled against an index (SourceIndexBuilder records it), so
/// every unit numbers the same blocks. Two constructions of one form that a
/// class answers with two members (IFoo&lt;string&gt; and IFoo&lt;Uri&gt;)
/// cannot be told apart by the code shared between them, and are refused
/// (Binder.AssignSlots).
/// </summary>
internal static class InterfaceForms
{
    /// <summary>What every reference argument reads as.</summary>
    public const string Word = Monomorphiser.CanonName;

    /// <summary>
    /// What a named type argument is: a reference, or a value type with the
    /// name its form spells and the arguments it was made with.
    /// </summary>
    public readonly record struct Shape(bool Reference, string Plain, IReadOnlyList<TypeRef> Args);

    /// <summary>A name with its namespace, outer type and mangling cut off.</summary>
    public static string Plain(string name)
    {
        int cut = name.IndexOfAny(new[] { '`', '$', '<' });
        string bare = cut < 0 ? name : name[..cut];
        int dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare[(dot + 1)..];
    }

    /// <summary>A form's template, as the form spells it: `IEnumerable`1`.</summary>
    public static string Family(string template, int arity) => Plain(template) + "`" + arity;

    /// <summary>Whether a form is all words: the template's own slots answer it.</summary>
    public static bool AllWords(string form)
    {
        string family = FamilyOf(form);
        int arity = int.Parse(family[(family.LastIndexOf('`') + 1)..]);
        return form == family || form == family + "<" + string.Join(",", Enumerable.Repeat(Word, arity)) + ">";
    }

    /// <summary>The value types a form's arguments name: `KeyValuePair` and `Money` of `IEnumerable`1<KeyValuePair`2<Money,__canon>>`.</summary>
    public static IEnumerable<string> Names(string form)
    {
        int open = form.IndexOf('<');
        if (open < 0) yield break;
        foreach (string part in form[(open + 1)..].Split('<', '>', ','))
        {
            if (part.Length == 0 || part == Word) continue;
            int tick = part.IndexOf('`');
            yield return tick < 0 ? part : part[..tick];
        }
    }

    /// <summary>The template a form is of: `IEnumerable`1`.</summary>
    public static string FamilyOf(string form)
    {
        int open = form.IndexOf('<');
        return open < 0 ? form : form[..open];
    }

    /// <summary>
    /// The form of `template` made with `args`, or null where an argument's
    /// shape is not known -- a type parameter of the declaration it is
    /// written in, a name that is no type, a tuple. `shape` answers a named
    /// argument; `parameters` what the declaration's own type parameters are
    /// (their forms, null for one that may be anything).
    /// </summary>
    public static string? Of(string template, IReadOnlyList<TypeRef> args, Func<TypeRef, Shape?> shape,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        if (args.Count == 0) return null;
        List<string> forms = new(args.Count);
        foreach (TypeRef a in args)
        {
            if (Argument(a, shape, parameters, 0) is not string form) return null;
            forms.Add(form);
        }
        return Family(template, args.Count) + "<" + string.Join(",", forms) + ">";
    }

    private static string? Argument(TypeRef r, Func<TypeRef, Shape?> shape, IReadOnlyDictionary<string, string?>? parameters, int depth)
    {
        if (depth > 16) return null;
        // An array and a pointer are addresses, words whatever they address.
        if (r.ArrayRank > 0 || r.PointerDepth > 0) return Word;
        if (r.IsFunctionPointer || r.Name == TypeRef.Tuple) return null;
        string? inner = Unwrapped(r, shape, parameters, depth);
        if (!r.Nullable || inner is null or Word) return inner;
        return "Nullable`1<" + inner + ">";
    }

    // The argument without its `?`.
    private static string? Unwrapped(TypeRef r, Func<TypeRef, Shape?> shape, IReadOnlyDictionary<string, string?>? parameters, int depth)
    {
        if (r.Args.Count == 0 && parameters is not null && parameters.TryGetValue(r.Name, out string? given)) return given;
        if (r.Args.Count == 0 && Keyword(r.Name) is string keyword) return keyword;
        if (shape(r) is not Shape s) return null;
        if (s.Reference) return Word;
        IReadOnlyList<TypeRef> args = r.Args.Count > 0 ? r.Args : s.Args;
        if (args.Count == 0) return s.Plain;
        List<string> forms = new(args.Count);
        foreach (TypeRef a in args)
        {
            if (Argument(a, shape, parameters, depth + 1) is not string form) return null;
            forms.Add(form);
        }
        return s.Plain + "`" + args.Count + "<" + string.Join(",", forms) + ">";
    }

    /// <summary>The form of a built-in type, however it is spelt: `int`, `Int32`, `System.Int32`.</summary>
    private static string? Keyword(string name)
    {
        string plain = name.StartsWith("System.", StringComparison.Ordinal) ? name["System.".Length..] : name;
        return plain switch
        {
            "string" or "String" or "object" or "Object" or "dynamic" or Word => Word,
            "bool" or "Boolean" => "bool",
            "char" or "Char" => "char",
            "sbyte" or "SByte" => "sbyte",
            "byte" or "Byte" => "byte",
            "short" or "Int16" => "short",
            "ushort" or "UInt16" => "ushort",
            "int" or "Int32" => "int",
            "uint" or "UInt32" => "uint",
            "long" or "Int64" => "long",
            "ulong" or "UInt64" => "ulong",
            "float" or "Single" => "float",
            "double" or "Double" => "double",
            "nint" or "IntPtr" => "nint",
            "nuint" or "UIntPtr" => "nuint",
            "decimal" or "Decimal" => "decimal",
            _ => null,
        };
    }

    /// <summary>The form a primitive box answers a system interface's construction over its own type in.</summary>
    public static string? OfPrimitive(string template, Prim p)
    {
        string? keyword = p switch
        {
            Prim.Bool => "bool", Prim.Char => "char", Prim.I8 => "sbyte", Prim.U8 => "byte", Prim.I16 => "short", Prim.U16 => "ushort",
            Prim.I32 => "int", Prim.U32 => "uint", Prim.I64 => "long", Prim.U64 => "ulong", Prim.F32 => "float", Prim.F64 => "double",
            Prim.NInt => "nint", Prim.NUInt => "nuint", _ => null,
        };
        return keyword is null ? null : Family(template, 1) + "<" + keyword + ">";
    }

    /// <summary>
    /// What the search for conflicted forms reads of a declaration: its name,
    /// kind, type parameters and bases as written, whether it is the class
    /// library's, and which type it is -- its namespace and the types it is
    /// written in with its name (`Identity`), which its partial parts share
    /// and no other type does.
    /// </summary>
    public sealed record Declared(string Name, TypeKind Kind, string[] TypeParams, List<TypeRef> Bases, bool Library, string Identity)
    {
        public static Declared Of(TypeDecl d, bool library)
            => new(d.Name, d.Kind, d.TypeParams.Select(p => p.Name).ToArray(), d.Bases, library, d.Namespace + "|" + (d.Outer ?? "") + "|" + d.Name);
    }

    /// <summary>
    /// THE CONFLICTED FORMS OF A SET OF DECLARATIONS: every form, not all
    /// words, that a class or struct implements -- itself, through the
    /// interfaces those extend or through its base classes, each with its
    /// type arguments put in -- beside another form of the same template.
    /// Each with whether a declaration of the class library's has it, whose
    /// blocks are numbered among the library's (Binder). Types are found by
    /// their plain name and arity; a namesake of the other kind makes a name
    /// unknown, and a form with an unknown argument is left out. `made`: the
    /// specialisations a compile has made, by the names its bases are written
    /// with once made (`IComparable$int`), each with its template and arguments.
    /// </summary>
    public static Dictionary<string, bool> Conflicts(IEnumerable<Declared> declarations,
        IReadOnlyDictionary<string, (string Template, IReadOnlyList<TypeRef> Args)>? made = null)
    {
        List<Declared> all = declarations.ToList();
        Dictionary<(string, int), List<Declared>> named = new();
        foreach (Declared d in all)
        {
            (string, int) key = (d.Name, d.TypeParams.Length);
            (named.TryGetValue(key, out List<Declared>? l) ? l : named[key] = new()).Add(d);
        }
        // A reference as written, or as its specialisation was made of.
        (string Name, IReadOnlyList<TypeRef> Args) Expand(TypeRef r)
            => r.Args.Count == 0 && made is not null && made.TryGetValue(r.Name, out var m) ? (m.Template, m.Args) : (r.Name, r.Args);
        IEnumerable<Declared> Find(string name, int arity) => named.GetValueOrDefault((Plain(name), arity)) ?? Enumerable.Empty<Declared>();
        Shape? ShapeOf(TypeRef r)
        {
            var (name, args) = Expand(r);
            bool reference = false, value = false;
            foreach (Declared d in Find(name, args.Count))
            {
                if (d.Kind is TypeKind.Class or TypeKind.Interface) reference = true;
                else value = true;
            }
            if (reference == value) return null;
            return new Shape(reference, Plain(name), args);
        }

        Dictionary<string, bool> conflicted = new(StringComparer.Ordinal);
        // A partial class's parts together: one names an interface, another
        // a second construction of it. NEVER A LIBRARY'S CLASS WITH A
        // PROGRAM'S: no type has parts in both, and a program's class named
        // as one of the library's (Named, beside System.Threading's) is a
        // class of its own, whose conflicts are the program's. Read as one,
        // the program's IName<int> was numbered among the library's slots,
        // which moved every library class's virtuals one up in that unit.
        // NOR TWO TYPES THAT SHARE A NAME: parts of one type are written in
        // one namespace and one outer type. Grouped by name alone, the
        // linker's ChunkedBytes.Enumerator, an IEnumerator<byte>, and
        // SortedRelocations.Range.Enumerator, an IEnumerator<Relocation>,
        // were one class with two constructions, IEnumerator<byte> a
        // conflicted form in every unit that read both, and the class
        // library's unit, which kept it, put its Current in a slot of its
        // own: List<byte>'s and the byte array walker's routines were two
        // in the compiler's own project, which its link refused.
        foreach (IGrouping<(string, int, bool), Declared> side in all
                     .Where(d => d.Kind is TypeKind.Class or TypeKind.Struct)
                     .GroupBy(d => (d.Identity, d.TypeParams.Length, d.Library)))
        {
            List<Declared> classes = side.ToList();
            bool library = side.Key.Item3;
            // Its own type parameters may be anything.
            Dictionary<string, string?> own = new(StringComparer.Ordinal);
            foreach (string p in classes[0].TypeParams) own[p] = null;
            Dictionary<string, HashSet<string>> byFamily = new(StringComparer.Ordinal);
            HashSet<string> seen = new(StringComparer.Ordinal);
            Stack<(TypeRef Base, IReadOnlyDictionary<string, string?> Parameters)> work = new();
            foreach (Declared d in classes)
                foreach (TypeRef b in d.Bases) work.Push((b, own));
            while (work.Count > 0)
            {
                var (b, parameters) = work.Pop();
                var (name, args) = Expand(b);
                foreach (Declared over in Find(name, args.Count))
                {
                    if (over.Kind is not (TypeKind.Class or TypeKind.Interface)) continue;
                    // What its type parameters are here.
                    Dictionary<string, string?> given = new(StringComparer.Ordinal);
                    List<string> spelt = new();
                    for (int i = 0; i < over.TypeParams.Length; i++)
                    {
                        string? form = Argument(args[i], ShapeOf, parameters, 0);
                        given[over.TypeParams[i]] = form;
                        spelt.Add(form ?? "?");
                    }
                    string family = Family(over.Name, over.TypeParams.Length);
                    string construction = over.TypeParams.Length == 0 ? family : family + "<" + string.Join(",", spelt) + ">";
                    if (!seen.Add(over.Kind + " " + construction)) continue;
                    if (over.Kind == TypeKind.Interface && over.TypeParams.Length > 0)
                        (byFamily.TryGetValue(family, out HashSet<string>? forms) ? forms : byFamily[family] = new(StringComparer.Ordinal))
                            .Add(spelt.Contains("?") ? "?" : construction);
                    foreach (TypeRef up in over.Bases) work.Push((up, given));
                }
            }
            foreach (HashSet<string> forms in byFamily.Values)
            {
                if (forms.Count < 2) continue;
                foreach (string form in forms)
                    if (form != "?" && !AllWords(form)) conflicted[form] = conflicted.GetValueOrDefault(form) || library;
            }
        }
        return conflicted;
    }
}
