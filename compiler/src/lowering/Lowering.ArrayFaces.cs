#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using Type = Corsac.Lang.Type;

/// <summary>
/// AN ARRAY IS ITS SEQUENCE INTERFACES, as the CLR's arrays are: a T[] is an
/// IList&lt;T&gt;, ICollection&lt;T&gt;, IReadOnlyList&lt;T&gt;,
/// IReadOnlyCollection&lt;T&gt;, IEnumerable&lt;T&gt; and IEnumerable -- and,
/// its element a reference, those of every type the element converts to, by
/// array covariance -- one object seen through all of them: GetType() is the
/// array's, `(T[])list` the array again, `new List&lt;T&gt;(array)` finds an
/// ICollection&lt;T&gt; and copies it.
///
/// THE ARRAY'S OWN DESCRIPTOR ANSWERS THEM (SequenceDescriptor), and names
/// none of them, so that every unit's copy of it is one table though only
/// some units make IList&lt;Tok&gt; -- as a box answers its system
/// interfaces (BoxedFaces). Their slots, the families' own (the binder
/// numbers them over the declarations, the same in every unit), hold the
/// routines written here over the array as `this`, named by its element.
/// A test of one of them asks the array (TypeTest, Runtime.DescribedAs and
/// ShapedAs): each such interface's descriptor says so in its flags and
/// names the array of its argument in word 10 (InterfaceDescriptor).
/// GetEnumerator hands out a walker made the same way -- an index into the
/// array, which is IEnumerator&lt;T&gt; by the same rule -- and the members
/// that would change an array's size refuse with .NET's
/// NotSupportedException, as its SZArrayHelper does.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>What a library interface is to an array (ArrayFaceOf).</summary>
    private enum ArrayFace
    {
        None,
        /// <summary>A sequence interface an array is: IEnumerable, IEnumerable&lt;T&gt;, ICollection&lt;T&gt;, IList&lt;T&gt;, IReadOnlyCollection&lt;T&gt;, IReadOnlyList&lt;T&gt;.</summary>
        Sequence,
        /// <summary>IEnumerator&lt;T&gt;, which the walker an array hands out is.</summary>
        Walker,
    }

    // An interface's flags (DescFlags): a sequence interface an array is, the
    // IEnumerator<T> its walker is. A walker's own, and an array's whose
    // elements are references -- what every object[] is (Runtime.DescribedAs).
    // Past the bits a box keeps its kind in (BoxKindShift).
    private const int TypeFlagArrayFace = 2048, TypeFlagWalkerFace = 0x100000, TypeFlagArrayWalker = 0x200000, TypeFlagReferenceElements = 0x400000;

    // Runtime.ShapedAs's flags, beside the argument count: the family is one of them.
    private const long ArrayFaceShape = 1024, WalkerFaceShape = 2048;

    /// <summary>The families an array or its walker answers, by plain name and arity, and what each is.</summary>
    private static readonly (string Plain, int Arity, ArrayFace Face)[] ArrayFamilies =
    {
        ("IEnumerable", 0, ArrayFace.Sequence), ("IEnumerable", 1, ArrayFace.Sequence),
        ("ICollection", 1, ArrayFace.Sequence), ("IList", 1, ArrayFace.Sequence),
        ("IReadOnlyCollection", 1, ArrayFace.Sequence), ("IReadOnlyList", 1, ArrayFace.Sequence),
        ("IEnumerator", 1, ArrayFace.Walker),
    };

    /// <summary>The library's interface of one of these families, or None for any other type.</summary>
    private static ArrayFace ArrayFaceOf(TypeSymbol t)
    {
        if (t.Kind != TypeKind.Interface || !IsLibrary(t) || t.Decl is not TypeDecl d || d.Outer is not null) return ArrayFace.None;
        (string name, int arity) = d.Template is string template ? (template, d.TemplateArgs.Count) : (t.Name, d.TypeParams.Count);
        if (name.Contains('.')) return ArrayFace.None;
        foreach ((string plain, int a, ArrayFace face) in ArrayFamilies)
            if (a == arity && plain == name) return face;
        return ArrayFace.None;
    }

    /// <summary>
    /// The element an array must have to be this interface (IList&lt;U&gt;'s U),
    /// where it is a type an array can be made of -- not over a shared copy's
    /// word, whose test is the copy's (ShapeTest) -- or null; null too for the
    /// non-generic IEnumerable, which every array is.
    /// </summary>
    private static Type? ArrayFaceElement(TypeSymbol t)
    {
        if (t.TemplateArgTypes.Count != 1) return null;
        Type u = t.TemplateArgTypes[0];
        return u.IsError || u.ParamName is not null || u.IsPointer || u.Function is not null || MentionsCanon(u)
            || u.Prim == Prim.Void && u.Symbol is null && !u.IsArray ? null : u;
    }

    /// <summary>Whether an interface is answered by an array or its walker for whatever it is over: its flags say so (TypeFlagArrayFace, TypeFlagWalkerFace).</summary>
    private static bool AnsweredByArrays(TypeSymbol t)
        => ArrayFaceOf(t) is ArrayFace face && face != ArrayFace.None
        && (t.TemplateArgTypes.Count == 0 ? face == ArrayFace.Sequence : ArrayFaceElement(t) is not null);

    /// <summary>
    /// Each member of a family with its slot, in the family's order: the
    /// binder's numbering, the members' names read from any copy of the
    /// family -- every IList&lt;X&gt; has IList's members in IList's order --
    /// and from the declarations, written below, where this unit has none.
    /// </summary>
    private List<(string Member, int Slot)> FamilySlots(string plain, int arity)
    {
        if (_familySlots.TryGetValue((plain, arity), out List<(string, int)>? known)) return known;
        SortedDictionary<int, int> slots = new();
        foreach (var ((template, a, member), slot) in _b.InterfaceFamilySlots)
            if (a == arity && !template.Contains('.') && BoxedFaces.Plain(template) == plain) slots[member] = slot;
        List<string>? names = null;
        foreach (TypeSymbol t in _b.Types.Values)
        {
            if (t.Kind != TypeKind.Interface || !IsLibrary(t) || t.Decl is not TypeDecl d || d.Outer is not null) continue;
            if (!(d.Template is string template ? template == plain && d.TemplateArgs.Count == arity : arity == 0 && d.TypeParams.Count == 0 && t.Name == plain)) continue;
            names = t.Methods.Where(m => m.Decl?.LocalCopy != true).Select(m => m.Name).ToList();
            break;
        }
        names ??= DeclaredMembers(plain, arity);
        List<(string, int)> made = new();
        foreach (var (member, slot) in slots)
            if (member < names.Count) made.Add((names[member], slot));
        return _familySlots[(plain, arity)] = made;
    }

    private readonly Dictionary<(string, int), List<(string, int)>> _familySlots = new();

    /// <summary>The members of these families as stdlib declares them, in order, for a unit that made no copy of one.</summary>
    private static List<string> DeclaredMembers(string plain, int arity) => (plain, arity) switch
    {
        ("IEnumerable", _) => new() { "GetEnumerator" },
        ("ICollection", 1) => new() { "get_Count", "get_IsReadOnly", "Add", "Clear", "Contains", "CopyTo", "Remove" },
        ("IList", 1) => new() { "get_Item", "set_Item", "IndexOf", "Insert", "RemoveAt" },
        ("IReadOnlyCollection", 1) => new() { "get_Count" },
        ("IReadOnlyList", 1) => new() { "get_Item" },
        ("IEnumerator", 1) => new() { "get_Current" },
        ("IEnumerator", 0) => new() { "get_Current", "MoveNext", "Reset" },
        ("IDisposable", 0) => new() { "Dispose" },
        _ => new(),
    };

    /// <summary>
    /// The slot of a construction's form where the form has a block of its
    /// own (InterfaceForms): IEnumerable&lt;KeyValuePair&lt;string, int&gt;&gt;
    /// beside IEnumerable&lt;Group&gt; in one class, each in slots of its own,
    /// which an array of the element answers too.
    /// </summary>
    private int? ElementFormSlot(string plain, Type element, string member, int index)
        => ElementForm(element) is string inner && _b.InterfaceFormSlots.TryGetValue((InterfaceForms.Family(plain, 1) + "<" + inner + ">", index), out int slot)
            ? slot : null;

    /// <summary>An element's form as InterfaceForms spells one: every reference the one word, a value type by its name and arguments.</summary>
    private static string? ElementForm(Type e, int depth = 0)
    {
        if (depth > 16) return null;
        if (e.IsArray || e.IsPointer || e.IsReference || e.Prim is Prim.String || e.Prim == Prim.Any && e.Symbol is null) return InterfaceForms.Word;
        if (e.IsNullableValue) return ElementForm(e.Underlying, depth + 1) is string inner && inner != InterfaceForms.Word ? "Nullable`1<" + inner + ">" : null;
        if (e.Symbol is null) return InterfaceForms.OfPrimitive("X", e.Prim) is string primitive ? primitive["X`1<".Length..^1] : null;
        if (e.Symbol.Kind is not (TypeKind.Struct or TypeKind.Enum) || IsTupleShape(e.Symbol)) return null;
        string plain = InterfaceForms.Plain(e.Symbol.Decl?.Template ?? e.Symbol.Name);
        if (e.Symbol.TemplateArgTypes.Count == 0) return plain;
        List<string> args = new();
        foreach (Type a in e.Symbol.TemplateArgTypes)
        {
            if (ElementForm(a, depth + 1) is not string form) return null;
            args.Add(form);
        }
        return plain + "`" + args.Count + "<" + string.Join(",", args) + ">";
    }

    /// <summary>
    /// What an array of this element holds in the sequence interfaces' slots:
    /// each family member's slot, in its form's block too where it has one,
    /// with the routine that answers it.
    /// </summary>
    private List<(int Slot, string Target)> ArrayFaceSlots(Type element, string descriptor)
    {
        string key = descriptor["q_array_".Length..];
        // ONE SET FOR EVERY ARRAY OF REFERENCES: its elements are words
        // whatever they are, compared as objects are and stored as into an
        // object[] that may be any array of references (ArrayStoreChecked).
        (Type code, string shared) = Shared(element, key);
        List<(int, string)> filled = new();
        void Fill(string plain, int arity, Func<string, string?> target)
        {
            List<(string Member, int Slot)> members = FamilySlots(plain, arity);
            for (int k = 0; k < members.Count; k++)
            {
                if (target(members[k].Member) is not string routine) continue;
                filled.Add((members[k].Slot, routine));
                if (arity == 1 && ElementFormSlot(plain, element, members[k].Member, k) is int own && own != members[k].Slot) filled.Add((own, routine));
            }
        }
        Fill("IEnumerable", 0, member => member == "GetEnumerator" ? ArrayEnumerate(element, key, descriptor) : null);
        Fill("IEnumerable", 1, member => member == "GetEnumerator" ? ArrayEnumerate(element, key, descriptor) : null);
        Fill("IReadOnlyCollection", 1, member => member == "get_Count" ? ArrayCount() : null);
        Fill("IReadOnlyList", 1, member => member == "get_Item" ? ArrayGet(code, shared) : null);
        Fill("ICollection", 1, member => member switch
        {
            "get_Count" => ArrayCount(),
            "get_IsReadOnly" => ArrayReadOnly(),
            "Add" => ArrayRefusal("add$" + Passed(code), Type.Void, "ArrayFixedSize", code),
            "Clear" => ArrayRefusal("clear", Type.Void, "ArrayReadOnly"),
            "Contains" => ArraySearch(code, shared, contains: true),
            "CopyTo" => ArrayCopyTo(code, shared),
            "Remove" => ArrayRefusal("remove$" + Passed(code), Type.Bool, "ArrayFixedSize", code),
            _ => null,
        });
        Fill("IList", 1, member => member switch
        {
            "get_Item" => ArrayGet(code, shared),
            "set_Item" => ArraySet(code, shared),
            "IndexOf" => ArraySearch(code, shared, contains: false),
            "Insert" => ArrayRefusal("insert$" + Passed(code), Type.Void, "ArrayFixedSize", Type.I32, code),
            "RemoveAt" => ArrayRefusal("removeat", Type.Void, "ArrayFixedSize", Type.I32),
            _ => null,
        });
        return filled;
    }

    /// <summary>
    /// What an array's routines are written over and named by: object and
    /// `ref` for every array of references; a number's width and sign for
    /// every array of numbers, bools, characters or enums of it, which read,
    /// write and compare alike; else the element itself and its key.
    /// </summary>
    private static (Type Code, string Key) Shared(Type element, string key)
    {
        if (ReferenceElements(element) || element.ParamName is not null) return (Type.Any, "ref");
        if (element.IsArray || element.IsPointer || element.IsNullableValue || element.Symbol is { Kind: not TypeKind.Enum }) return (element, key);
        Prim p = element.Symbol is { Kind: TypeKind.Enum } e ? e.EnumUnderlying : element.Prim;
        return p switch
        {
            Prim.Bool or Prim.U8 => (Type.U8, "u1"), Prim.I8 => (Type.I8, "s1"),
            Prim.Char or Prim.U16 => (Type.U16, "u2"), Prim.I16 => (Type.I16, "s2"),
            Prim.I32 or Prim.U32 => (Type.I32, "i4"), Prim.I64 or Prim.U64 => (Type.I64, "i8"),
            Prim.NInt or Prim.NUInt or Prim.Type => (Type.NInt, "iw"),
            Prim.F32 => (Type.F32, "f4"), Prim.F64 => (Type.F64, "f8"),
            _ => (element, key),
        };
    }

    /// <summary>
    /// A routine of an array's (or its walker's), `this` the array (the
    /// walker) and these parameters, built by `body` with the builder set on
    /// it: made once a name, the same in every unit.
    /// </summary>
    private string ArrayRoutine(string name, Type returns, IEnumerable<Type> parameters, Action<Function, VReg, List<VReg>, VReg?> body)
    {
        if (!_structHelpers.Add(name)) return name;
        bool buffered = IsStructValue(returns) && StructOf(returns).HeldInline;
        Function f = new(name, IrTypes.Of(returns)) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        List<VReg> args = new();
        foreach (Type p in parameters)
        {
            VReg arg = f.NewReg(IrTypes.Of(p), "arg");
            f.Params.Add(arg);
            args.Add(arg);
        }
        VReg? buffer = null;
        if (buffered)
        {
            buffer = f.NewReg(IrTypes.Word, "retbuf");
            f.Params.Add(buffer);
        }
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail; HelperContext context = EnterHelper();
        // A FRESH FUNCTION HAS NO FAILURE BLOCK YET: a bounds check's is
        // cached per function, and the last function's is not this one's.
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;
        body(f, self, args, buffer);
        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail; LeaveHelper(context);
        return name;
    }

    /// <summary>The method a shared helper was asked for in, set aside while it is built.</summary>
    private readonly record struct HelperContext(MethodSymbol? Method, VReg[]? TypeArgs, bool InBarrier, int CheckedDepth);

    /// <summary>
    /// A SHARED HELPER IS BUILT AS NOBODY'S: every unit that needs one makes
    /// it and the link keeps one copy, so it must come out the same whoever
    /// asked for it first. Built inside the method that asked, it took that
    /// method's rules -- no card mark in the collector's and the runtime's
    /// classes (CardMarkAt), no barrier inside a barrier, a generic method's
    /// type arguments -- and the uint array walker the class library's unit
    /// first wanted in Runtime.CrcTable marked no cards where every other
    /// unit's did, and the compiler's own project link refused the two.
    /// </summary>
    private HelperContext EnterHelper()
    {
        // And no `checked` of the asker's: a shared helper's arithmetic is unchecked.
        HelperContext saved = new(_method, _typeArgs, _inBarrier, _checkedDepth);
        _method = null;
        _typeArgs = null;
        _inBarrier = false;
        _checkedDepth = 0;
        return saved;
    }

    private void LeaveHelper(HelperContext saved)
    {
        _method = saved.Method;
        _typeArgs = saved.TypeArgs;
        _inBarrier = saved.InBarrier;
        _checkedDepth = saved.CheckedDepth;
    }

    /// <summary>How an element is passed, as a refusal is named by it: its machine type.</summary>
    private static string Passed(Type element) => IrTypes.Of(element).ToString().ToLowerInvariant();

    /// <summary>A node to hang what the routines make on, for code nobody wrote.</summary>
    private static MethodDecl ArrayAt(string name) => new() { Name = name, Line = 0, Col = 0 };

    /// <summary>A call of the runtime's that throws: never returns.</summary>
    private void ArrayThrow(string refusal, params Operand[] args)
    {
        if (RuntimeMethod(refusal, args.Length) is MethodSymbol thrower)
        {
            Require(thrower);
            Operand[] passed = new Operand[args.Length];
            for (int i = 0; i < args.Length; i++)
                passed[i] = args[i] is RegOperand { Reg: var r } ? R(AsParam(r, thrower.Params[i].Type)) : args[i];
            _e.Call(CallLabel(thrower), IrType.Void, passed);
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
    }

    /// <summary>A value as `returns` hands it back: a struct into the caller's buffer or a copy, anything else itself.</summary>
    private void ArrayReturn(Type of, VReg value, VReg? buffer, string name)
    {
        if (buffer is not null)
        {
            _e.Emit(Opcode.MemCopy, null, R(buffer), R(value), Imm(Math.Max(1, StructOf(of).InstanceSize), IrTypes.Word));
            _e.Ret(R(buffer));
            return;
        }
        _e.Ret(R(IsStructValue(of) ? CopyStruct(ArrayAt(name), value, StructOf(of)) : value));
    }

    /// <summary>Count, of every array: its length.</summary>
    private string ArrayCount()
        => ArrayRoutine("__array_count", Type.I32, Array.Empty<Type>(), (f, self, args, buffer) => _e.Ret(R(CountOf(_e, self))));

    /// <summary>ICollection&lt;T&gt;.IsReadOnly, of every array: true, as .NET's arrays answer it.</summary>
    private string ArrayReadOnly()
        => ArrayRoutine("__array_readonly", Type.Bool, Array.Empty<Type>(), (f, self, args, buffer) => _e.Ret(Imm(1, IrTypes.Of(Type.Bool))));

    /// <summary>
    /// A member that would change the array's size: refused, `refusal` the
    /// runtime's sentence. Named by how an element is passed where a
    /// parameter is one (Passed): one routine for every element passed alike.
    /// </summary>
    private string ArrayRefusal(string member, Type returns, string refusal, params Type[] parameters)
        => ArrayRoutine("__array_" + member, returns, parameters, (f, self, args, buffer) => ArrayThrow(refusal));

    /// <summary>
    /// The indexer read through IList&lt;T&gt; or IReadOnlyList&lt;T&gt;: an
    /// index outside the array is an ArgumentOutOfRangeException, as
    /// SZArrayHelper's is, not the IndexOutOfRangeException of `a[i]`.
    /// </summary>
    private string ArrayGet(Type element, string key)
    {
        string name = "__array_get$" + key;
        return ArrayRoutine(name, element, new[] { Type.I32 }, (f, self, args, buffer) =>
        {
            ArrayIndexChecked(self, args[0]);
            VReg value = LoadElement(self, args[0], element, ArrayAt(name));
            ArrayReturn(element, value, buffer, name);
        });
    }

    /// <summary>Refuses an index outside the array as IList&lt;T&gt;'s indexer does (Runtime.ArrayFaceIndex).</summary>
    private void ArrayIndexChecked(VReg array, VReg index)
    {
        Block good = _f.NewBlock("faceinrange");
        Block bad = _f.NewBlock("faceoutofrange");
        _e.Branch(_e.Binary(Opcode.LtU, R(index), R(CountOf(_e, array)), IrType.I32), good, bad);
        _e.SetBlock(bad);
        ArrayThrow("ArrayFaceIndex");
        _e.SetBlock(good);
    }

    /// <summary>
    /// A reference stored into an array that may be any array of references
    /// held as this one: the value's type asked of the array's element, as
    /// every store into a covariant array is (Runtime.ArrayStoreCheck) --
    /// what is stored through an IList&lt;object&gt; may be anything, and the
    /// array a string[]. CopyTo's is Array.Copy's (Runtime.ArrayCopyCheck).
    /// </summary>
    private void ArrayStoreChecked(VReg array, VReg value, string checker)
    {
        if (RuntimeMethod(checker, 2) is not MethodSymbol check) return;
        int w = _t.WordSize;
        Block some = _f.NewBlock("facestore");
        Block typed = _f.NewBlock("facetyped");
        Block slow = _f.NewBlock("faceslow");
        Block done = _f.NewBlock("facestored");
        _e.Branch(value, some, done);
        _e.SetBlock(some);
        VReg held = _e.Load(IrTypes.Word, _e.Load(IrTypes.Word, array, 0), (long)DescElement * w - _t.DescriptorBytes);
        _e.Branch(held, typed, done);
        _e.SetBlock(typed);
        VReg described = _e.Binary(Opcode.Sub, _e.Load(IrTypes.Word, value, 0), _t.DescriptorBytes);
        _e.Branch(_e.Binary(Opcode.Eq, R(held), R(described), IrType.I32), done, slow);
        _e.SetBlock(slow);
        Require(check);
        _e.Call(CallLabel(check), IrType.Void, R(AsParam(array, check.Params[0].Type)), R(AsParam(value, check.Params[1].Type)));
        _e.Jump(done);
        _e.SetBlock(done);
    }

    /// <summary>Whether an array's elements are references, of an array, a class, an interface, a string or object.</summary>
    private static bool ReferenceElements(Type e)
        => !e.IsPointer && !e.IsNullableValue
        && (e.IsArray || e.Symbol is null && e.Prim is Prim.String or Prim.Any || e.Symbol is { Kind: TypeKind.Class or TypeKind.Interface });

    /// <summary>One element stored, through a place that checks nothing more: the reference's type checked first (ArrayStoreChecked).</summary>
    private void ArrayStore(VReg array, VReg index, VReg value, Type element, string name, string checker = "ArrayStoreCheck")
    {
        if (ReferenceElements(element)) ArrayStoreChecked(array, value, checker);
        MemPlace place = ElementPlace(array, index, Type.ArrayOf(element), element, ArrayAt(name));
        StorePlace(place with { CovariantArray = null }, value);
    }

    /// <summary>The indexer written through IList&lt;T&gt;: the element replaced, the index refused as the read's is.</summary>
    private string ArraySet(Type element, string key)
    {
        string name = "__array_set$" + key;
        return ArrayRoutine(name, Type.Void, new[] { Type.I32, element }, (f, self, args, buffer) =>
        {
            ArrayIndexChecked(self, args[0]);
            ArrayStore(self, args[0], args[1], element, name);
            _e.Ret(null);
        });
    }

    /// <summary>
    /// IndexOf: the first element equal to the value, as .NET's arrays
    /// compare two of them (ElementsEqual); Contains: whether IndexOf finds one.
    /// </summary>
    private string ArraySearch(Type element, string key, bool contains)
    {
        if (contains)
        {
            string indexOf = ArraySearch(element, key, contains: false);
            return ArrayRoutine("__array_contains$" + key, Type.Bool, new[] { element }, (f, self, args, buffer) =>
            {
                VReg at = _e.Call(indexOf, IrType.I32, R(self), R(args[0]))!;
                _e.Ret(R(_e.Binary(Opcode.GeS, R(at), Imm(0, IrType.I32), IrType.I32)));
            });
        }
        string name = "__array_indexof$" + key;
        return ArrayRoutine(name, Type.I32, new[] { element }, (f, self, args, buffer) =>
        {
            VReg count = CountOf(_e, self);
            VReg i = f.NewReg(IrType.I32, "i");
            _e.CopyTo(i, Imm(0, IrType.I32));
            Block loop = f.NewBlock("search");
            Block body = f.NewBlock("searchat");
            Block next = f.NewBlock("searchnext");
            Block found = f.NewBlock("found");
            Block none = f.NewBlock("notfound");
            _e.Jump(loop);
            _e.SetBlock(loop);
            _e.Branch(_e.Binary(Opcode.LtS, R(i), R(count), IrType.I32), body, none);
            _e.SetBlock(body);
            VReg held = LoadElement(self, i, element, ArrayAt(name));
            _e.Branch(ElementsEqual(held, args[0], element, name), found, next);
            _e.SetBlock(next);
            _e.CopyTo(i, R(_e.Binary(Opcode.Add, i, 1)));
            _e.Jump(loop);
            _e.SetBlock(found);
            _e.Ret(R(i));
            _e.SetBlock(none);
            _e.Ret(Imm(-1, IrType.I32));
        });
    }

    /// <summary>
    /// Whether an element equals a value, as .NET's array search finds it:
    /// a struct by EqualityComparer&lt;T&gt;.Default (StructEquals); a
    /// reference by Equals(object) -- .NET's arrays answer IList&lt;T&gt; of
    /// every reference T with the shared copy's comparer, object's, so an
    /// IEquatable class's typed Equals is not asked -- and a Nullable as its
    /// box compares; a float by its own Equals, a NaN equal to a NaN; any
    /// other value by its bits.
    /// </summary>
    private VReg ElementsEqual(VReg held, VReg value, Type element, string name)
    {
        if (IsStructValue(element))
        {
            return _e.Call(StructEquals(StructOf(element)), IrType.I32, R(held), R(value))!;
        }
        if (CouldBeObject(element) || element.IsNullableValue)
        {
            return _e.Call(KeyEqualsStub(element, table: false), IrType.I32, R(held), R(value))!;
        }
        if (held.Type.IsFloat())
        {
            VReg equal = _e.Binary(Opcode.FEq, R(held), R(value), IrType.I32);
            VReg bothNan = _e.Binary(Opcode.And, _e.Binary(Opcode.FNe, R(held), R(held), IrType.I32), _e.Binary(Opcode.FNe, R(value), R(value), IrType.I32));
            return _e.Binary(Opcode.Or, equal, bothNan);
        }
        return _e.Binary(Opcode.Eq, R(held), R(value), IrType.I32);
    }

    /// <summary>
    /// ICollection&lt;T&gt;.CopyTo: every element into `destination` from
    /// `index` on, as Array.Copy(this, 0, destination, index, Length) does
    /// -- its refusals (Runtime.ArrayCopyToCheck), and its InvalidCastException
    /// for an element the destination cannot hold. An array copied onto
    /// itself is copied from index 0, which moves nothing: from any later
    /// index there is no room.
    /// </summary>
    private string ArrayCopyTo(Type element, string key)
    {
        string name = "__array_copyto$" + key;
        Type array = Type.ArrayOf(element);
        return ArrayRoutine(name, Type.Void, new[] { array, Type.I32 }, (f, self, args, buffer) =>
        {
            VReg destination = args[0], index = args[1];
            VReg count = CountOf(_e, self);
            VReg room = f.NewReg(IrType.I32, "room");
            Block measured = f.NewBlock("copyroom");
            Block checkedBlock = f.NewBlock("copychecked");
            _e.CopyTo(room, Imm(-1, IrType.I32));
            _e.Branch(destination, measured, checkedBlock);
            _e.SetBlock(measured);
            _e.CopyTo(room, R(CountOf(_e, destination)));
            _e.Jump(checkedBlock);
            _e.SetBlock(checkedBlock);
            if (RuntimeMethod("ArrayCopyToCheck", 3) is MethodSymbol check)
            {
                Require(check);
                _e.Call(CallLabel(check), IrType.Void, R(AsParam(room, check.Params[0].Type)), R(AsParam(index, check.Params[1].Type)),
                    R(AsParam(count, check.Params[2].Type)));
            }
            VReg i = f.NewReg(IrType.I32, "i");
            Block loop = f.NewBlock("copyloop");
            Block body = f.NewBlock("copybody");
            Block done = f.NewBlock("copied");
            _e.CopyTo(i, Imm(0, IrType.I32));
            _e.Jump(loop);
            _e.SetBlock(loop);
            _e.Branch(_e.Binary(Opcode.LtS, R(i), R(count), IrType.I32), body, done);
            _e.SetBlock(body);
            ArrayStore(destination, _e.Binary(Opcode.Add, index, i), LoadElement(self, i, element, ArrayAt(name)), element, name, "ArrayCopyCheck");
            _e.CopyTo(i, R(_e.Binary(Opcode.Add, i, 1)));
            _e.Jump(loop);
            _e.SetBlock(done);
            _e.Ret(null);
        });
    }

    // ---- the walker -------------------------------------------------------

    /// <summary>
    /// GetEnumerator, generic and not: a new walker over the array, before its
    /// first element (-1), as .NET's SZGenericArrayEnumerator starts.
    /// </summary>
    private string ArrayEnumerate(Type element, string key, string descriptor)
    {
        string name = "__array_enumerate$" + key;
        string walker = ArrayWalker(element, key, descriptor);
        return ArrayRoutine(name, Type.Any, Array.Empty<Type>(), (f, self, args, buffer) =>
        {
            int header = _t.ObjectHeaderBytes;
            VReg made = Allocate(ArrayAt(name), header + _t.WordSize + 4, described: true);
            _e.Store(R(made), new SymOperand(walker, _t.DescriptorBytes), 0, _t.WordSize);
            StoreNewReference(made, self, header);
            _e.Store(R(made), Imm(-1, IrType.I32), header + _t.WordSize, 4);
            _e.Ret(R(made));
        });
    }

    /// <summary>
    /// The walker's descriptor: an object of two fields, the array and where
    /// it stands in it, that is an IEnumerator, an IDisposable -- which its
    /// table names -- and the IEnumerator&lt;T&gt; of its array's element,
    /// which it answers as the array answers its own (TypeFlagArrayWalker,
    /// word 10 the array's descriptor). One per element, shared by every unit.
    /// </summary>
    private string ArrayWalker(Type element, string key, string arrayDescriptor)
    {
        string sym = "t_ArrayEnumerator$0024" + key;
        if (!_structHelpers.Add(sym)) return sym;

        int w = _t.WordSize;
        int header = _t.ObjectHeaderBytes;
        int size = header + w + 4;
        List<(int Slot, string Target)> slots = new()
        {
            (_b.ToStringSlot, ObjectToStringStub()),
            (_b.EqualsSlot, ObjectEqualsStub()),
            (_b.HashSlot, ObjectHashStub()),
            (_b.CompareSlot, ObjectCompareStub()),
        };
        void Fill(string plain, int arity, Func<string, string?> target)
        {
            List<(string Member, int Slot)> members = FamilySlots(plain, arity);
            for (int k = 0; k < members.Count; k++)
            {
                if (target(members[k].Member) is not string routine) continue;
                slots.Add((members[k].Slot, routine));
                if (arity == 1 && ElementFormSlot(plain, element, members[k].Member, k) is int own && own != members[k].Slot) slots.Add((own, routine));
            }
        }
        (Type code, string shared) = Shared(element, key);
        // The boxed one by the element itself: a bool's box is no byte's.
        Fill("IEnumerator", 1, member => member == "get_Current" ? WalkerCurrent(code, shared, boxed: false) : null);
        Fill("IEnumerator", 0, member => member switch
        {
            "get_Current" => Boxable(element) ? WalkerCurrent(element, key, boxed: true) : WalkerCurrent(code, shared, boxed: true),
            "MoveNext" => WalkerMoveNext(),
            "Reset" => WalkerReset(),
            _ => null,
        });
        Fill("IDisposable", 0, member => member == "Dispose" ? WalkerDispose() : null);

        int count = slots.Max(s => s.Slot) + 1;
        byte[] block = new byte[_t.DescriptorBytes + count * w];
        WriteWord(block, DescSize * w, size);
        WriteWord(block, DescDepth * w, 0);
        WriteWord(block, DescFlags * w, TypeFlagArrayWalker);
        WriteWord(block, DescPayload * w, header);
        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, FromLibrary = true, Coalescible = true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString("System.SZGenericArrayEnumerator`1[" + DotNetTypeName(element) + "]"), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));
        item.Relocs.Add(new DataReloc(DescElement * w, arrayDescriptor, 0));
        if (ReferenceMap("ArrayEnumerator_" + key, new List<(int, Type)> { (header, Type.ArrayOf(element)) }, size) is string map)
        {
            item.Relocs.Add(new DataReloc(DescRefMap * w, map, 0));
        }

        DataItem display = new("d_ArrayEnumerator_" + key, new byte[w]) { ReadOnly = true, Exported = false };
        display.Relocs.Add(new DataReloc(0, sym, 0));
        _m.Data.Add(display);
        item.Relocs.Add(new DataReloc(DescDisplay * w, display.Name, 0));

        // THE INTERFACES ITS TABLE NAMES: the two that are no family over
        // the element, which every unit has.
        List<string> faces = new();
        foreach (string plain in new[] { "IDisposable", "IEnumerator" })
            if (_b.Types.TryGetValue(plain, out TypeSymbol? face) && face.Kind == TypeKind.Interface && IsLibrary(face)) faces.Add(InterfaceDescriptor(face));
        faces.Sort(string.CompareOrdinal);
        DataItem list = new("f_ArrayEnumerator_" + key, new byte[(faces.Count + 1) * w]) { ReadOnly = true, Exported = false };
        for (int i = 0; i < faces.Count; i++) list.Relocs.Add(new DataReloc(i * w, faces[i], 0));
        _m.Data.Add(list);
        item.Relocs.Add(new DataReloc(DescInterfaces * w, list.Name, 0));

        foreach (var (slot, target) in slots) item.Relocs.Add(new DataReloc(_t.DescriptorBytes + slot * w, target, 0));
        return sym;
    }

    /// <summary>
    /// Current, the element where the walker stands -- boxed for the
    /// non-generic IEnumerator's -- and before the first or past the last,
    /// .NET's InvalidOperationException (Runtime.ArrayWalkerOff).
    /// </summary>
    private string WalkerCurrent(Type element, string key, bool boxed)
    {
        string name = (boxed ? "__arraywalk_object$" : "__arraywalk_current$") + key;
        return ArrayRoutine(name, boxed ? Type.Any : element, Array.Empty<Type>(), (f, self, args, buffer) =>
        {
            int header = _t.ObjectHeaderBytes;
            VReg items = _e.Load(IrTypes.Word, self, header);
            VReg at = Numbered(_e, _e.Load(IrType.I32, self, header + _t.WordSize));
            Block on = f.NewBlock("walkon");
            Block off = f.NewBlock("walkoff");
            _e.Branch(_e.Binary(Opcode.LtU, R(at), R(CountOf(_e, items)), IrType.I32), on, off);
            _e.SetBlock(off);
            ArrayThrow("ArrayWalkerOff", R(at));
            _e.SetBlock(on);
            VReg value = LoadElement(items, at, element, ArrayAt(name));
            if (boxed)
            {
                _e.Ret(R(Boxable(element) ? BoxValue(ArrayAt(name), value, element) : value));
                return;
            }
            ArrayReturn(element, value, buffer, name);
        });
    }

    /// <summary>MoveNext, .NET's: one on while there is one, else past the last for good.</summary>
    private string WalkerMoveNext()
        => ArrayRoutine("__arraywalk_next", Type.Bool, Array.Empty<Type>(), (f, self, args, buffer) =>
        {
            int header = _t.ObjectHeaderBytes;
            VReg items = _e.Load(IrTypes.Word, self, header);
            VReg count = CountOf(_e, items);
            VReg next = _e.Binary(Opcode.Add, Numbered(_e, _e.Load(IrType.I32, self, header + _t.WordSize)), 1);
            Block more = f.NewBlock("walkmore");
            Block end = f.NewBlock("walkend");
            _e.Branch(_e.Binary(Opcode.LtU, R(next), R(count), IrType.I32), more, end);
            _e.SetBlock(more);
            _e.Store(R(self), R(next), header + _t.WordSize, 4);
            _e.Ret(Imm(1, IrTypes.Of(Type.Bool)));
            _e.SetBlock(end);
            _e.Store(R(self), R(count), header + _t.WordSize, 4);
            _e.Ret(Imm(0, IrTypes.Of(Type.Bool)));
        });

    /// <summary>Reset: before the first element again.</summary>
    private string WalkerReset()
        => ArrayRoutine("__arraywalk_reset", Type.Void, Array.Empty<Type>(), (f, self, args, buffer) =>
        {
            _e.Store(R(self), Imm(-1, IrType.I32), _t.ObjectHeaderBytes + _t.WordSize, 4);
            _e.Ret(null);
        });

    /// <summary>Dispose: nothing to give back.</summary>
    private string WalkerDispose()
        => ArrayRoutine("__arraywalk_dispose", Type.Void, Array.Empty<Type>(), (f, self, args, buffer) => _e.Ret(null));
}
