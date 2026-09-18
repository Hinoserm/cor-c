#nullable enable
namespace Corsac.Lang;

/// <summary>A declared type: class, interface, struct or enum.</summary>
public sealed class TypeSymbol
{
    public required string Name { get; init; }

    /// <summary>
    /// What this type is called when it has to be told apart from every other
    /// type: `Section` at the top level, `ImageFile.Section` for one written
    /// inside ImageFile.
    ///
    /// <see cref="Name"/> stays SIMPLE because that is what C# reflection
    /// answers and what a diagnostic should read like. This is the identity --
    /// the key in the table of types, and the owner part of an emitted method
    /// symbol, so that two nested types sharing a simple name do not compile
    /// down to one set of labels.
    /// </summary>
    /// <remarks>
    /// Unset, it IS the name: everything that is not a nested type -- and that
    /// is nearly everything, including every type the prelude declares and
    /// every tuple, closure and specialisation made up along the way -- has
    /// nothing to qualify with.
    /// </remarks>
    public string Key
    {
        get => _key ?? Name;
        init => _key = value;
    }

    private readonly string? _key;

    public required TypeKind Kind { get; init; }
    public TypeDecl? Decl { get; init; }
    /// <summary>Compiler-created closed tuple/array adapter shape, shared only after semantic certification.</summary>
    public bool Structural { get; init; }
    public TypeSymbol? Base { get; set; }
    public List<TypeSymbol> Interfaces { get; } = new();
    public List<FieldSymbol> Fields { get; } = new();
    public List<MethodSymbol> Methods { get; } = new();
    /// <summary>One implementation can occupy several distinct interface slots.</summary>
    public Dictionary<int, MethodSymbol> InterfaceImplementations { get; } = new();
    public List<string> TypeParams { get; } = new();
    /// <summary>Enum member values, when this is an enum.</summary>
    public Dictionary<string, long> EnumValues { get; } = new();

    /// <summary>
    /// Whether an enum was written `[Flags]`: a SET OF BITS rather than a
    /// list of alternatives. C# gives it one behaviour and one only, and it
    /// is the one people notice -- `(Read | Run).ToString()` says
    /// "Read, Run" where an ordinary enum would say the number.
    /// </summary>
    public bool IsFlags { get; set; }

    /// <summary>Byte size of an instance, filled in during layout.</summary>
    public int InstanceSize { get; set; }

    /// <summary>
    /// This type's bit in the ancestor mask. A test like <c>x is Foo</c> is
    /// then two loads and an AND: an object's vtable carries the mask of every
    /// type it derives from, so no hierarchy walk happens at run time.
    /// </summary>
    public int TypeBit { get; set; } = -1;

    /// <summary>
    /// How deep this class sits in the single-inheritance chain: a class with
    /// no base is 0, its subclass 1, and so on. An INTERFACE is -1, because
    /// interfaces do not form a chain and are searched rather than indexed.
    ///
    /// This is what replaces <see cref="TypeBit"/>. The difference that matters
    /// is not the number but where it comes FROM: a type bit is handed out by
    /// counting every type in the program, so it changes when an unrelated file
    /// declares a class, and a library and its consumers must agree on the
    /// count. A depth is a property of the type and its own bases, so it is the
    /// same number whoever compiles it and whatever else is compiled with it.
    /// </summary>
    public int Depth { get; set; } = -1;

    /// <summary>
    /// For the class a TUPLE shape became: the element names, when every place
    /// that wrote this shape wrote the same ones.
    ///
    /// Names belong to the TYPE and not to the class -- `(int a, int b)` and
    /// `(int x, int y)` are one class -- and the type is where they are read
    /// from. This is the fallback for when they were lost on the way through a
    /// generic: `List&lt;(int At, string Label)&gt;` hands its element back
    /// through a substituted type parameter, and what comes out the other side
    /// is the shape without the names.
    ///
    /// Set to null the moment a second, DIFFERENT naming of the same shape is
    /// seen, so a guess is never made between two of them.
    /// </summary>
    public IReadOnlyList<string>? TupleNames { get; set; }

    /// <summary>Whether TupleNames has been settled once already.</summary>
    public bool TupleNamesSeen { get; set; }

    /// <summary>
    /// EVERY NAMING OF THIS SHAPE THAT HAS BEEN SEEN, which is what is left to
    /// answer with once two of them disagree.
    ///
    /// A name is taken from these only when every set that has it has it at the
    /// SAME position -- `(int FreeFrom, int Slot)` and `(int vreg, int instr)`
    /// are one class here and `.FreeFrom` can only mean the first element, so
    /// there is nothing to guess between. A name at two different positions is
    /// refused, as it was before there was a list at all.
    ///
    /// This is wider than C#, which keeps the names on the REFERENCE and so
    /// knows exactly which naming a value was written with; a monomorphised
    /// generic hands its element back through one shared copy and the naming
    /// does not survive the trip. What is never done is answer with the wrong
    /// element.
    /// </summary>
    public List<IReadOnlyList<string>> TupleNamings { get; } = new();

    /// <summary>The element this name can only mean, or -1 when it could mean two.</summary>
    public int TupleElement(string name)
    {
        int found = -1;

        foreach (IReadOnlyList<string> naming in TupleNamings)
        {
            for (int i = 0; i < naming.Count; i++)
            {
                if (naming[i] != name)
                {
                    continue;
                }

                if (found >= 0 && found != i)
                {
                    return -1;
                }
                found = i;
            }
        }
        return found;
    }

    public bool DerivesFrom(TypeSymbol other)
    {
        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            if (ReferenceEquals(t, other))
            {
                return true;
            }

            foreach (TypeSymbol i in t.Interfaces)
            {
                if (ReferenceEquals(i, other) || i.DerivesFrom(other))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public FieldSymbol? FindField(string name)
    {
        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            FieldSymbol? f = t.Fields.Find(f => f.Name == name);

            if (f != null)
            {
                return f;
            }
        }
        return null;
    }

    public List<MethodSymbol> FindMethods(string name)
    {
        List<MethodSymbol> found = new();

        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            found.AddRange(t.Methods.Where(m => m.Name == name));
        }
        return found;
    }

    public override string ToString() => Name;
}
