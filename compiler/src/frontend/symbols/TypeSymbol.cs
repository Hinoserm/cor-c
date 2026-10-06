#nullable enable
namespace Corsac.Lang;

/// <summary>A declared type: class, interface, struct or enum.</summary>
public sealed class TypeSymbol
{
    /// <summary>Its key spelled as a symbol (Lowering.TypeKey), made once.</summary>
    internal string? TypeKeyMade;
    public required string Name { get => _name; init => _name = Interned.Name(value); }
    private readonly string _name = "";

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
        init => _key = value is null ? null : Interned.Name(value);
    }

    private readonly string? _key;

    public required TypeKind Kind { get; init; }
    public TypeDecl? Decl { get; init; }
    /// <summary>Virtual slots have been numbered, which for a class from a library is separate from knowing its size.</summary>
    public bool SlotsAssigned { get; set; }

    /// <summary>
    /// Whether its instance fields' FieldSymbol.Inline has been decided, and
    /// -- a struct's -- the alignment it is held in line at.
    /// </summary>
    public bool InlineDecided { get; set; }
    public int InlineAlign { get; set; } = 1;
    /// <summary>
    /// A struct's: whether it is held in line wherever it is held -- a field
    /// (FieldSymbol.Inline) and an array's element alike -- its fields all
    /// numbers, references, or structs held in line themselves.
    /// </summary>
    public bool HeldInline { get; set; }
    /// <summary>A tuple shape's: whether it has been given ValueTuple's interfaces (Binder.TupleFaces).</summary>
    public bool TupleFacesGiven { get; set; }

    /// <summary>
    /// A closure's, made of a method group: the identity of the method it
    /// calls (ClosureIdentity), the same wherever the group was converted. A
    /// delegate of it is equal to any other of the same method on the same
    /// target (Runtime.GroupEquals), which lowering arranges through the
    /// closure's Equals slot. Null for a lambda's closure and every other type.
    /// </summary>
    public string? DelegateGroup { get; set; }
    // EVERY COLLECTION BELOW IS MADE ON ITS FIRST WRITE: the plain property
    // reads one shared empty instance until then, never written through, and
    // every writer goes by its Writable twin. A symbol for a tuple, a closure
    // or an interface has no fields, methods, enum values or template
    // arguments, and eight empty collections each were a large share of the
    // symbols' memory in a big unit. The read stays the concrete type so a
    // foreach over it takes no enumerator object.
    /// <summary>A specialisation's type arguments, resolved where it was written: what .NET's name of it spells out.</summary>
    public List<Type> TemplateArgTypes => _templateArgTypes ?? NoTemplateArgTypes;
    public List<Type> WritableTemplateArgTypes => _templateArgTypes ??= new();
    private List<Type>? _templateArgTypes;
    private static readonly List<Type> NoTemplateArgTypes = new();
    /// <summary>The classes a shared copy's code makes for this copy's arguments (TypeDecl.CanonMade); null where one did not resolve.</summary>
    public List<TypeSymbol?> CanonMadeTypes => _canonMadeTypes ?? NoCanonMadeTypes;
    public List<TypeSymbol?> WritableCanonMadeTypes => _canonMadeTypes ??= new();
    private List<TypeSymbol?>? _canonMadeTypes;
    private static readonly List<TypeSymbol?> NoCanonMadeTypes = new();
    /// <summary>Compiler-created closed tuple/array adapter shape, shared only after semantic certification.</summary>
    public bool Structural { get; init; }

    /// <summary>
    /// Whether this compilation actually reached for this type, as opposed to
    /// merely having its declaration loaded. What a unit DESCRIBES in its
    /// managed layout follows this rather than the set of declarations that
    /// happened to be imported: a declaration nobody asked about cannot be
    /// disagreed about. See ManagedLayouts.
    /// </summary>
    public bool Used { get; set; }
    public TypeSymbol? Base { get; set; }
    public List<TypeSymbol> Interfaces => _interfaces ?? NoInterfaces;
    public List<TypeSymbol> WritableInterfaces => _interfaces ??= new();
    private List<TypeSymbol>? _interfaces;
    private static readonly List<TypeSymbol> NoInterfaces = new();
    public List<FieldSymbol> Fields => _fields ?? NoFields;
    public List<FieldSymbol> WritableFields => _fields ??= new();
    private List<FieldSymbol>? _fields;
    private static readonly List<FieldSymbol> NoFields = new();
    public List<MethodSymbol> Methods => _methods ?? NoMethods;
    public List<MethodSymbol> WritableMethods => _methods ??= new();
    private List<MethodSymbol>? _methods;
    private static readonly List<MethodSymbol> NoMethods = new();
    /// <summary>One implementation can occupy several distinct interface slots.</summary>
    public Dictionary<int, MethodSymbol> InterfaceImplementations => _interfaceImplementations ?? NoInterfaceImplementations;
    public Dictionary<int, MethodSymbol> WritableInterfaceImplementations => _interfaceImplementations ??= new();
    private Dictionary<int, MethodSymbol>? _interfaceImplementations;
    private static readonly Dictionary<int, MethodSymbol> NoInterfaceImplementations = new();
    // Made only when written: most have none, and a list each was the collector's.
    private static readonly List<string> NoTypeParams = new();
    private List<string>? _typeParams;
    /// <summary>To read: one shared empty list when there are none, never written through.</summary>
    public List<string> TypeParams => _typeParams ?? NoTypeParams;
    /// <summary>To write: made on first use.</summary>
    public List<string> WritableTypeParamNames => _typeParams ??= new();
    /// <summary>Enum member values, when this is an enum.</summary>
    public Dictionary<string, long> EnumValues => _enumValues ?? NoEnumValues;
    public Dictionary<string, long> WritableEnumValues => _enumValues ??= new();
    private Dictionary<string, long>? _enumValues;
    private static readonly Dictionary<string, long> NoEnumValues = new();

    /// <summary>
    /// What an enum is STORED AS: `enum E : long` is eight bytes and `enum
    /// E : byte` is one, exactly as C# says. Int32 when none was written,
    /// which is C#'s default and by far the common case.
    /// </summary>
    public Prim EnumUnderlying { get; set; } = Prim.I32;

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
    ///
    /// It is DERIVED, never stored: a specialisation made after the binder's
    /// last pass (a tuple shape first needed while lowering one unit) has the
    /// same depth as one made up front in another unit, or the two objects
    /// disagree about the type's layout at link.
    /// </summary>
    public int Depth
    {
        get
        {
            if (Kind != TypeKind.Class)
            {
                return -1;
            }

            int deep = 0;

            for (TypeSymbol? a = Base; a != null; a = a.Base)
            {
                deep++;
            }

            return deep;
        }
    }

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
    public List<IReadOnlyList<string>> TupleNamings => _tupleNamings ?? NoTupleNamings;
    public List<IReadOnlyList<string>> WritableTupleNamings => _tupleNamings ??= new();
    private List<IReadOnlyList<string>>? _tupleNamings;
    private static readonly List<IReadOnlyList<string>> NoTupleNamings = new();

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
            foreach (MethodSymbol m in t.Methods)
                if (m.Name == name) found.Add(m);
        }
        return found;
    }

    public override string ToString() => Name;
}
