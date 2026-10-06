using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using Type = Corsac.Lang.Type;

/// <summary>
/// What makes two keys the same key.
///
/// .NET asks the key: a table calls its Equals and its GetHashCode, and a
/// type that overrides them -- a tuple, a string, a type that is made of
/// other types -- is found again by a key that is EQUAL to the one it went in
/// under, not only by that very object. Every class here already answers
/// Equals at a slot all of them share; GetHashCode has the one beside it, and
/// these are the routines that reach both from a value known only to be a
/// word.
/// </summary>
public sealed partial class Lowering
{
    private bool _hasKeyEquals, _hasKeyHash, _hasObjectHash;

    /// <summary>object.GetHashCode: which object it is. Shared by every class that declares none.</summary>
    private string ObjectHashStub()
    {
        const string name = "__object_gethashcode";

        if (!_hasObjectHash)
        {
            _hasObjectHash = true;
            Function f = new(name, IrType.I32) { Coalescible = true };
            VReg self = f.NewReg(IrTypes.Word, "this");

            f.Params.Add(self);
            Builder e = new(f, f.NewBlock("entry"));

            // Objects are eight apart at the least, so the low three bits say
            // nothing and would fill one slot in eight of a table.
            VReg word = IrTypes.Word == IrType.I64 ? e.Unary(Opcode.Trunc64, self) : self;

            e.Ret(RegOperand.Of(e.Binary(Opcode.ShrU, word, 3)));
            _m.Functions.Add(f);
        }
        return name;
    }

    // ---- the key questions, by the type they are asked of ---------------------------
    //
    // ONE ROUTINE FOR EVERY KEY OF EVERY TYPE named object's Equals, and so
    // every Equals in the program, at every table: a Dictionary<Symbol, X>
    // asked whether two Symbols are equal reached, for all the escape graphs
    // and the link could tell, a Token's Equals and a Tuple's and all eleven
    // hundred others -- which call KeyEquals on their own fields, and so the
    // compiler's call graph was one cycle of thirteen thousand functions.
    //
    // The key's static type says what can answer: a T is a T or something
    // derived from it, and its Equals is one of theirs. So each question has
    // a routine per type, `__key_equals$T`, whose call through the slot names
    // T's descriptor (DispatchType) rather than object's; a string's and an
    // array's need no call at all; a sealed class's is a direct call. What is
    // only known to be an object -- a generic's shared copy, a boxed value --
    // goes through the shared one (`__key_equals`), as before.
    //
    // Not narrowed, because the static type does not bound what answers:
    // - an interface a boxed number could be held as (PrimitiveMayImplement):
    //   a boxed int's descriptor lists no interfaces, so IComparable's
    //   implementers never include the box that answers;
    // - a shared copy's class: what canonical code makes is stamped with the
    //   canonical copy's descriptor, not this one's (SharedCopy);
    // - object, and the classes a boxed value, a string or an array is held
    //   as -- ValueType, Enum, Array, String, Delegate.

    /// <summary>The type a key question may be narrowed to: a class or an interface the static type bounds what answers by; null for the shared routine.</summary>
    private static TypeSymbol? NarrowKey(Type? t)
        => t is { IsArray: false, IsPointer: false, ParamName: null, IsNullableValue: false, IsError: false, Symbol: TypeSymbol s }
           && t.Prim is not (Prim.Any or Prim.String or Prim.NullLiteral) ? NarrowSymbol(s) : null;

    /// <summary><see cref="NarrowKey"/> for a type symbol: the class or interface itself, or null when what holds one may be something it does not bound.</summary>
    private static TypeSymbol? NarrowSymbol(TypeSymbol? s)
    {
        if (s is null || s.Kind is not (TypeKind.Class or TypeKind.Interface) || s.Decl is null || s.Decl.IsDelegate || SharedCopy(s))
        {
            return null;
        }
        if (s.Name is "object" or "Object" or "ValueType" or "Enum" or "Array" or "String" or "Delegate" or "MulticastDelegate")
        {
            return null;
        }
        return s.Kind == TypeKind.Interface && PrimitiveMayImplement(s) ? null : s;
    }

    /// <summary>
    /// Whether a boxed number, enum or bool could be held as this interface:
    /// any the system library declares -- IComparable, IEquatable,
    /// IFormattable, IConvertible and the rest are .NET's numbers' own, and a
    /// box's descriptor lists none of them -- and any of their names wherever
    /// declared.
    /// </summary>
    private static bool PrimitiveMayImplement(TypeSymbol s)
    {
        if (SystemType(s) || IsLibrary(s) || s.Decl is not TypeDecl d
            || d.Namespace == "System" || d.Namespace.StartsWith("System.", StringComparison.Ordinal))
        {
            return true;
        }
        string bare = d.Template ?? s.Name;
        bare = bare[(bare.LastIndexOf('.') + 1)..];
        int cut = bare.IndexOfAny(new[] { '$', '<', '`' });
        if (cut >= 0) bare = bare[..cut];
        return bare is "IComparable" or "IEquatable" or "IFormattable" or "ISpanFormattable" or "IUtf8SpanFormattable" or "IConvertible";
    }

    /// <summary>
    /// A narrowed routine's function: exported and shared between units like
    /// any other key routine, except for a type only its own unit has --
    /// another unit may have one of the same name.
    /// </summary>
    private static Function KeyFunction(string name, TypeSymbol? of)
    {
        bool local = of?.Decl?.LocalOnly == true;
        return new Function(name, IrType.I32) { Coalescible = !local, Exported = !local };
    }

    /// <summary>What fills slot `slot` of a class's vtable when `filled` is the method its chain puts there (ClassDescriptor).</summary>
    private string? SlotTarget(TypeSymbol t, int slot, MethodSymbol? filled)
    {
        // object's own members are symbols with no body (the binder's
        // Rooted): a slot that still holds one -- `new object()`, or a
        // class that overrides none of them -- gets the stub below, as a
        // slot nobody filled does.
        if (OwnSlotMethod(filled) is MethodSymbol m)
        {
            Require(m);
            return CallLabel(m);
        }
        if (t.Kind != TypeKind.Class)
        {
            return null;
        }
        // A closure's are its delegate's: the same method on the same target
        // is equal, whichever closure class made it (DelegateEqualsStub).
        if (slot == _b.EqualsSlot) return (DelegateClosure(t) ? DelegateEqualsStub(t) : null) ?? EqualsGuard(t) ?? ObjectEqualsStub();
        if (slot == _b.HashSlot) return (DelegateClosure(t) ? DelegateHashStub(t) : null) ?? ObjectHashStub();
        if (slot == _b.CompareSlot) return OwnCompare(t) ?? ObjectCompareStub();
        if (slot == _b.ToStringSlot) return ObjectToStringStub();
        return null;      // abstract: calling it is a null call, which traps
    }

    /// <summary>A method a vtable slot holds, unless it is one of object's own (which has no body: the slot gets a stub).</summary>
    private static MethodSymbol? OwnSlotMethod(MethodSymbol? filled)
        => filled is { } m && !(m.Decl is null && m.Owner?.Name == "object" && m.Owner.Kind == TypeKind.Class) ? m : null;

    /// <summary>
    /// A SEALED CLASS'S SLOT IS KNOWN, and called directly: the method it or
    /// its nearest ancestor puts there, or object's own hash or text. Only
    /// what every unit decides alike from the declarations alone -- a routine
    /// made in two units is one routine only when the two made it the same
    /// (DefinitionSemantics), and another unit's class comes without its
    /// bodies, which the stubs for Equals and CompareTo look at (EqualsGuard,
    /// OwnCompare): those, and any other type, are asked through the slot.
    /// </summary>
    private string? SealedTarget(TypeSymbol? t, int slot)
    {
        if (t is not { Kind: TypeKind.Class, Decl: TypeDecl d } || !d.Mods.HasFlag(Mods.Sealed) || t.Structural || SharedCopy(t))
        {
            return null;
        }
        MethodSymbol? filled = null;
        bool found = false;
        for (TypeSymbol? s = t; s is not null && !found; s = s.Base)
        {
            if (s.Methods.FirstOrDefault(x => x.VtableSlot == slot) is MethodSymbol own)
            {
                (filled, found) = (own, true);
            }
            else if (s.InterfaceImplementations.TryGetValue(slot, out MethodSymbol? implementation))
            {
                (filled, found) = (implementation, true);
            }
        }
        if (filled is { Abstract: true })
        {
            return null;
        }
        if (OwnSlotMethod(filled) is MethodSymbol m)
        {
            Require(m);
            return CallLabel(m);
        }
        if (DelegateClosure(t) && (slot == _b.HashSlot || slot == _b.EqualsSlot)) return null;
        return slot == _b.HashSlot ? ObjectHashStub() : slot == _b.ToStringSlot ? ObjectToStringStub() : null;
    }

    /// <summary>
    /// Asks the object `a` through slot `slot` -- directly when the class is
    /// sealed, through the slot naming the type it is narrowed to otherwise,
    /// and object's when it is not narrowed.
    /// </summary>
    private VReg AskSlot(Builder e, VReg a, VReg vt, int slot, TypeSymbol? narrow, IrType returns, params Operand[] args)
    {
        if (SealedTarget(narrow, slot) is string direct)
        {
            return e.Call(direct, returns, args)!;
        }
        VReg fn = e.Load(IrTypes.Word, vt, (long)slot * _t.WordSize);
        VReg answered = e.CallIndirect(R(fn), returns, args)!;
        e.Block.Instrs[^1].DispatchType = narrow is null ? ObjectDispatch : DescriptorOf(narrow);
        return answered;
    }

    /// <summary>
    /// object.Equals(a, b), as .NET defines it: the same reference, or neither
    /// of them null and a's own Equals says so. A string and an array have no
    /// slots to ask through: two strings compare as text, and anything else
    /// without slots is only ever itself. `of` is a's static type, when it is
    /// known: the routine for it (see the section's head).
    /// </summary>
    private string KeyEqualsStub(Type? of = null)
    {
        if (of is { Prim: Prim.String, IsArray: false, IsPointer: false })
        {
            return StringKeyEquals();
        }
        if (of is { IsArray: true, IsPointer: false })
        {
            return ArrayKeyEquals();
        }
        TypeSymbol? narrow = NarrowKey(of);
        string name = narrow is null ? "__key_equals" : "__key_equals$" + TypeKey(narrow);

        if (narrow is null ? _hasKeyEquals : !_structHelpers.Add(name))
        {
            return name;
        }
        _hasKeyEquals |= narrow is null;

        Function f = KeyFunction(name, narrow);
        VReg a = f.NewReg(IrTypes.Word, "a");
        VReg b = f.NewReg(IrTypes.Word, "b");

        f.Params.Add(a);
        f.Params.Add(b);
        Builder e = new(f, f.NewBlock("entry"));
        Block yes = f.NewBlock("keyes");
        Block differ = f.NewBlock("kediffer");
        Block first = f.NewBlock("kefirst");
        Block both = f.NewBlock("keboth");
        Block ask = f.NewBlock("keask");
        Block no = f.NewBlock("keno");

        e.Branch(e.Binary(Opcode.Eq, a, b), yes, differ);
        e.SetBlock(differ);
        e.Branch(a, first, no);
        e.SetBlock(first);
        e.Branch(b, both, no);
        e.SetBlock(both);

        VReg vt = e.Load(IrTypes.Word, a, 0);

        // A class is never a string, an array or a box: nothing to tell apart
        // before asking. What an interface holds may be any of them.
        if (narrow is { Kind: TypeKind.Class })
        {
            e.Jump(ask);
        }
        else
        {
            Block flat = f.NewBlock("keflat");
            Block text = f.NewBlock("ketext");
            Block texts = f.NewBlock("ketexts");
            VReg flags = Numbered(e, e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize));

            e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
            e.SetBlock(flat);
            e.Branch(e.Binary(Opcode.And, flags, 2), text, no);
            e.SetBlock(text);
            TextsEqual(e, a, b, texts, no);
        }

        e.SetBlock(ask);
        e.Ret(RegOperand.Of(AskSlot(e, a, vt, _b.EqualsSlot, narrow, IrType.I32, R(a), R(b))));
        e.SetBlock(yes);
        e.Ret(new ImmOperand(1, IrType.I32));
        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>
    /// From a block where `a` is known to be a string and `b` an object: the
    /// answer, 1 when `b` is a string of the same text.
    /// </summary>
    private void TextsEqual(Builder e, VReg a, VReg b, Block texts, Block no)
    {
        VReg other = e.Load(IrTypes.Word, b, 0);
        VReg otherFlags = Numbered(e, e.Load(IrType.I32, other, -_t.DescriptorBytes + DescFlags * _t.WordSize));

        e.Branch(e.Binary(Opcode.And, otherFlags, 2), texts, no);
        e.SetBlock(texts);

        MethodSymbol? equals = StringEqualsRoutine();
        MethodSymbol? compare = equals is null ? StringRoutine(Prelude.CompareMethod, 2) : null;

        if (equals is not null)
        {
            e.Ret(RegOperand.Of(e.Call(CallLabel(equals), IrTypes.Of(equals.Returns), R(a), R(b))!));
        }
        else if (compare is not null)
        {
            VReg order = e.Call(CallLabel(compare), IrTypes.Of(compare.Returns), R(a), R(b))!;

            e.Ret(RegOperand.Of(e.Binary(Opcode.Eq, R(order), new ImmOperand(0, order.Type), IrType.I32)));
        }
        else
        {
            e.Ret(new ImmOperand(0, IrType.I32));
        }
    }

    /// <summary>
    /// `__key_equals$$string`: a string asked, as the shared routine asks one --
    /// the same reference, or neither null and the other a string of the
    /// same text (object.Equals(s, o) may hand anything as the other).
    /// </summary>
    private string StringKeyEquals()
    {
        const string name = "__key_equals$$string";

        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = KeyFunction(name, null);
        VReg a = f.NewReg(IrTypes.Word, "a");
        VReg b = f.NewReg(IrTypes.Word, "b");

        f.Params.Add(a);
        f.Params.Add(b);
        Builder e = new(f, f.NewBlock("entry"));
        Block yes = f.NewBlock("skyes");
        Block differ = f.NewBlock("skdiffer");
        Block first = f.NewBlock("skfirst");
        Block both = f.NewBlock("skboth");
        Block texts = f.NewBlock("sktexts");
        Block no = f.NewBlock("skno");

        e.Branch(e.Binary(Opcode.Eq, a, b), yes, differ);
        e.SetBlock(differ);
        e.Branch(a, first, no);
        e.SetBlock(first);
        e.Branch(b, both, no);
        e.SetBlock(both);
        TextsEqual(e, a, b, texts, no);
        e.SetBlock(yes);
        e.Ret(new ImmOperand(1, IrType.I32));
        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>`__key_equals$$array`: an array is only ever itself, as the shared routine finds.</summary>
    private string ArrayKeyEquals()
    {
        const string name = "__key_equals$$array";

        if (_structHelpers.Add(name))
        {
            Function f = KeyFunction(name, null);
            VReg a = f.NewReg(IrTypes.Word, "a");
            VReg b = f.NewReg(IrTypes.Word, "b");

            f.Params.Add(a);
            f.Params.Add(b);
            Builder e = new(f, f.NewBlock("entry"));
            e.Ret(RegOperand.Of(e.Binary(Opcode.Eq, a, b)));
            _m.Functions.Add(f);
        }
        return name;
    }

    /// <summary>
    /// The hash that goes with <see cref="KeyEqualsStub"/>: nothing for null,
    /// the text's for a string, the object's own GetHashCode through its slot,
    /// and for anything without slots the word it is. `of` as KeyEqualsStub's.
    /// </summary>
    private string KeyHashStub(Type? of = null)
    {
        bool text = of is { Prim: Prim.String, IsArray: false, IsPointer: false };
        bool array = of is { IsArray: true, IsPointer: false };
        TypeSymbol? narrow = text || array ? null : NarrowKey(of);
        string name = text ? "__key_hash$$string" : array ? "__key_hash$$array" : narrow is null ? "__key_hash" : "__key_hash$" + TypeKey(narrow);

        if (text || array || narrow is not null ? !_structHelpers.Add(name) : _hasKeyHash)
        {
            return name;
        }
        _hasKeyHash |= !text && !array && narrow is null;

        Function f = KeyFunction(name, narrow);
        VReg a = f.NewReg(IrTypes.Word, "a");

        f.Params.Add(a);
        Builder e = new(f, f.NewBlock("entry"));
        Block some = f.NewBlock("khsome");
        Block word = f.NewBlock("khword");
        Block none = f.NewBlock("khnone");

        e.Branch(a, some, none);
        e.SetBlock(some);

        if (text)
        {
            TextHash(e, a, word);
        }
        else if (array)
        {
            e.Jump(word);
        }
        else
        {
            Block ask = f.NewBlock("khask");
            VReg vt = e.Load(IrTypes.Word, a, 0);

            // A class is never a string, an array or a box (KeyEqualsStub).
            if (narrow is { Kind: TypeKind.Class })
            {
                e.Jump(ask);
            }
            else
            {
                Block flat = f.NewBlock("khflat");
                Block texts = f.NewBlock("khtext");
                VReg flags = Numbered(e, e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize));

                e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
                e.SetBlock(flat);
                e.Branch(e.Binary(Opcode.And, flags, 2), texts, word);
                e.SetBlock(texts);
                TextHash(e, a, word);
            }

            e.SetBlock(ask);
            e.Ret(RegOperand.Of(AskSlot(e, a, vt, _b.HashSlot, narrow, IrType.I32, R(a))));
        }

        // IDENTITY, AS object.GetHashCode ANSWERS IT (ObjectHashStub): the
        // address without its low three bits. The address itself was what
        // this answered, which disagreed with an array's own GetHashCode --
        // and, handed back, made every key a comparer hashed look like an
        // argument returned to the caller, which the lifetime rules take for
        // an escape. A shifted address is a number.
        e.SetBlock(word);
        e.Ret(RegOperand.Of(e.Binary(Opcode.ShrU, IrTypes.Word == IrType.I64 ? e.Unary(Opcode.Trunc64, a) : a, 3)));
        e.SetBlock(none);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>A string's hash, from a block where `a` is known to be one; the word's (`word`) where the library has none.</summary>
    private void TextHash(Builder e, VReg a, Block word)
    {
        MethodSymbol? hash = StringRoutine("KeyHash", 1);

        if (hash is not null)
        {
            e.Ret(RegOperand.Of(e.Call(CallLabel(hash), IrTypes.Of(hash.Returns), R(a))!));
        }
        else
        {
            e.Jump(word);
        }
    }

    /// <summary>A static routine of the library's String, if the library compiled has it.</summary>
    private MethodSymbol? StringRoutine(string method, int argCount)
    {
        if (!_b.Types.TryGetValue(Prelude.StringType, out TypeSymbol? type))
        {
            return null;
        }

        MethodSymbol? found = type.Methods
            .FirstOrDefault(m => m.Name == method && m.Static && m.Params.Count == argCount);

        if (found is not null)
        {
            Require(found);
        }
        return found;
    }

    // ---- a struct as a key -------------------------------------------------------------

    /// <summary>
    /// `__struct_equals$T(a, b)`: whether two T values are equal, as .NET's
    /// EqualityComparer&lt;T&gt;.Default has it -- T's own Equals(T); else its
    /// Equals(object), handed b boxed; else field by field (ValueType.Equals):
    /// numbers by their bits, strings and objects as the key stub compares
    /// them, a struct inside by its own. A struct is a block reached by
    /// pointer, and comparing the pointers made two equal values unequal
    /// after any copy: a Dictionary&lt;Guid, X&gt; never found a key again.
    /// </summary>
    private string StructEquals(TypeSymbol sym)
    {
        string name = "__struct_equals$" + TypeKey(sym);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "a"), b = f.NewReg(IrTypes.Word, "b");
        f.Params.Add(a);
        f.Params.Add(b);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail;
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;
        Node at = new MethodDecl { Name = name, Line = 0, Col = 0 };

        // A tuple's own Equals is written from this (EmitTupleMethod): its
        // items are compared here, never by asking it.
        bool tuple = IsTupleShape(sym);
        MethodSymbol? typed = tuple ? null : sym.Methods.FirstOrDefault(m => m.Name == "Equals" && !m.Static && m.Params.Count == 1
                                                            && !m.Params[0].ByRef && m.Params[0].Type.Symbol == sym);
        MethodSymbol? untyped = tuple ? null : sym.Methods.FirstOrDefault(m => m.Name == "Equals" && !m.Static && m.Params.Count == 1
                                                              && m.Params[0].Type.Prim == Prim.Any);
        if (typed is not null || untyped is not null)
        {
            MethodSymbol eq = typed ?? untyped!;
            Require(eq);
            VReg other = typed is not null ? b : Box(at, b, Type.Plain(sym, Prim.Void));
            VReg said = CallDirect(eq, IrTypes.Of(eq.Returns), new List<Operand> { R(a), R(other) })!;
            _e.Ret(R(_e.Binary(Opcode.Ne, R(said), Imm(0, said.Type), IrType.I32)));
        }
        else
        {
            Block differ = _f.NewBlock("sdiffer");
            foreach (FieldSymbol field in sym.Fields)
            {
                if (field.Static || field.Boxed)
                {
                    continue;
                }
                VReg same = SameField(at, a, b, field);
                Block next = _f.NewBlock("snext");
                _e.Branch(same, next, differ);
                _e.SetBlock(next);
            }
            _e.Ret(Imm(1, IrType.I32));
            _e.SetBlock(differ);
            _e.Ret(Imm(0, IrType.I32));
        }

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail;
        return name;
    }

    /// <summary>
    /// A struct field of the block at `a`, as the block its fields are read
    /// from: where it is, for one held in line; the pointer stored there,
    /// for one that is a block of its own.
    /// </summary>
    private VReg FieldStruct(VReg a, FieldSymbol field)
        => field.Inline ? (field.Offset == 0 ? a : _e.Binary(Opcode.Add, a, field.Offset)) : _e.Load(IrTypes.Word, a, field.Offset);

    /// <summary>One field of two structs compared: 1 when they are the same.</summary>
    private VReg SameField(Node at, VReg a, VReg b, FieldSymbol field)
    {
        if (IsStructValue(field.Type))
        {
            VReg x = FieldStruct(a, field), y = FieldStruct(b, field);
            return _e.Call(StructEquals(StructOf(field.Type)), IrType.I32, R(x), R(y))!;
        }
        if (CouldBeObject(field.Type) || field.Type.IsNullableValue)
        {
            VReg x = _e.Load(IrTypes.Word, a, field.Offset), y = _e.Load(IrTypes.Word, b, field.Offset);
            return _e.Call(KeyEqualsStub(field.Type), IrType.I32, R(x), R(y))!;
        }
        // A FLOAT AS ITS OWN Equals: every NaN equal to every other, and
        // each zero to the other -- what EqualityComparer<double>.Default,
        // which ValueTuple and ValueType.Equals ask, answers.
        if (field.Type.Prim is Prim.F32 or Prim.F64)
        {
            VReg x = LoadPlace(new MemPlace(R(a), field.Offset, field.Type)), y = LoadPlace(new MemPlace(R(b), field.Offset, field.Type));
            VReg equal = _e.Binary(Opcode.FEq, R(x), R(y), IrType.I32);
            VReg bothNan = _e.Binary(Opcode.And, _e.Binary(Opcode.FNe, R(x), R(x), IrType.I32), _e.Binary(Opcode.FNe, R(y), R(y), IrType.I32));
            return _e.Binary(Opcode.Or, equal, bothNan);
        }
        // Any other number by its bits, read at its own width.
        VReg p = LoadPlace(new MemPlace(R(a), field.Offset, field.Type)), q = LoadPlace(new MemPlace(R(b), field.Offset, field.Type));
        return _e.Binary(Opcode.Eq, R(p), R(q), IrType.I32);
    }

    /// <summary>
    /// `__struct_hash$T(a)`: T's own GetHashCode(), or its fields' hashes
    /// combined -- the same fields StructEquals compares, so equal values
    /// hash the same.
    /// </summary>
    private string StructHash(TypeSymbol sym)
    {
        string name = "__struct_hash$" + TypeKey(sym);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "a");
        f.Params.Add(a);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail;
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;

        MethodSymbol? own = IsTupleShape(sym) ? null : sym.Methods.FirstOrDefault(m => m.Name == "GetHashCode" && !m.Static && m.Params.Count == 0);
        if (own is not null)
        {
            Require(own);
            VReg said = CallDirect(own, IrTypes.Of(own.Returns), new List<Operand> { R(a) })!;
            _e.Ret(R(said.Type == IrType.I32 ? said : _e.Unary(Opcode.Trunc64, R(said), IrType.I32)));
        }
        else
        {
            VReg h = _f.NewReg(IrType.I32, "h");
            _e.CopyTo(h, Imm(17, IrType.I32));
            foreach (FieldSymbol field in sym.Fields)
            {
                if (field.Static || field.Boxed)
                {
                    continue;
                }
                VReg v;
                if (IsStructValue(field.Type))
                {
                    v = _e.Call(StructHash(StructOf(field.Type)), IrType.I32, R(FieldStruct(a, field)))!;
                }
                else if (CouldBeObject(field.Type) || field.Type.IsNullableValue)
                {
                    v = _e.Call(KeyHashStub(field.Type), IrType.I32, R(_e.Load(IrTypes.Word, a, field.Offset)))!;
                }
                else if (field.Type.Prim is Prim.F32 or Prim.F64)
                {
                    bool wide = field.Type.Prim == Prim.F64;
                    v = FloatHash(_f, _e, Numbered(_e, _e.Load(wide ? IrType.I64 : IrType.I32, a, field.Offset, wide ? 8 : 4, false)), wide);
                }
                else
                {
                    Type raw = field.Type;
                    VReg w = LoadPlace(new MemPlace(R(a), field.Offset, raw));
                    if (w.Type == IrType.I64)
                    {
                        VReg high = _e.Unary(Opcode.Trunc64, R(_e.Binary(Opcode.ShrU, R(w), Imm(32, IrType.I32), IrType.I64)), IrType.I32);
                        v = _e.Binary(Opcode.Xor, R(_e.Unary(Opcode.Trunc64, R(w), IrType.I32)), R(high), IrType.I32);
                    }
                    else
                    {
                        v = w.Type == IrType.I32 ? w : _e.Unary(Opcode.Trunc64, R(w), IrType.I32);
                    }
                }
                _e.CopyTo(h, R(_e.Binary(Opcode.Add, R(_e.Binary(Opcode.Mul, R(h), Imm(31, IrType.I32), IrType.I32)), R(v), IrType.I32)));
            }
            _e.Ret(R(h));
        }

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail;
        return name;
    }

    /// <summary>
    /// A float's hash from its bits, as its Equals sees it: both zeros hash
    /// as one, and every NaN as one, so that equal values hash alike.
    /// </summary>
    private static VReg FloatHash(Function f, Builder e, VReg bits, bool wide)
    {
        IrType bitsType = wide ? IrType.I64 : IrType.I32;
        VReg result = f.NewReg(IrType.I32, "fhash");
        VReg magnitude = e.Binary(Opcode.And, R(bits), new ImmOperand(wide ? long.MaxValue : int.MaxValue, bitsType), bitsType);
        Block zero = f.NewBlock("fhzero"), nonzero = f.NewBlock("fhnonzero"), nan = f.NewBlock("fhnan"), finite = f.NewBlock("fhbits"), done = f.NewBlock("fhdone");
        e.Branch(e.Binary(Opcode.Eq, R(magnitude), new ImmOperand(0, bitsType), IrType.I32), zero, nonzero);
        e.SetBlock(zero);
        e.CopyTo(result, new ImmOperand(0, IrType.I32));
        e.Jump(done);
        e.SetBlock(nonzero);
        e.Branch(e.Binary(Opcode.GtU, R(magnitude), new ImmOperand(wide ? 0x7ff0000000000000L : 0x7f800000L, bitsType), IrType.I32), nan, finite);
        e.SetBlock(nan);
        e.CopyTo(result, new ImmOperand(wide ? 0x7ff00000 : 0x7f800000, IrType.I32));
        e.Jump(done);
        e.SetBlock(finite);
        e.CopyTo(result, R(wide
            ? e.Unary(Opcode.Trunc64, e.Binary(Opcode.Xor, bits, e.Binary(Opcode.ShrU, R(bits), new ImmOperand(32, IrType.I32), IrType.I64)))
            : bits));
        e.Jump(done);
        e.SetBlock(done);
        return result;
    }

    /// <summary>
    /// A struct's order for the default comparer: its own CompareTo(T), else
    /// its CompareTo(object) handed b boxed; null when it has neither -- an
    /// unordered struct sorts as all equal.
    /// </summary>
    private MethodSymbol? StructCompareTo(TypeSymbol sym, out bool boxed)
    {
        // A struct that cannot have one is not declared to say so (TypeSymbol.MayHave).
        if (!sym.MayHave("CompareTo"))
        {
            boxed = true;
            return null;
        }
        MethodSymbol? typed = sym.Methods.FirstOrDefault(m => m.Name == "CompareTo" && !m.Static && m.Params.Count == 1
                                                            && !m.Params[0].ByRef && m.Params[0].Type.Symbol == sym);
        boxed = typed is null;
        return typed ?? sym.Methods.FirstOrDefault(m => m.Name == "CompareTo" && !m.Static && m.Params.Count == 1
                                                     && m.Params[0].Type.Prim == Prim.Any);
    }

    /// <summary>Whether a value of this type could be an object with slots to ask.</summary>
    private static bool CouldBeObject(Type t)
        => t.Prim is Prim.String or Prim.Any or Prim.NullLiteral || t.ParamName is not null
        || t.Symbol is { Kind: TypeKind.Class or TypeKind.Interface } || t.IsArray;

    /// <summary>
    /// For a class that wrote `Equals(T)` and no `Equals(object)`: what object's
    /// slot holds. C# writes this for a record and .NET's default comparer
    /// finds the typed one through IEquatable; either way the typed method is
    /// only ever handed a T. Here anything at all comes through the slot, so
    /// it is asked whether it IS one first -- a LocalSym compared with a
    /// FieldSym is not equal to it, rather than read as though it were one.
    /// </summary>
    private string? EqualsGuard(TypeSymbol t)
    {
        MethodSymbol? typed = null;

        for (TypeSymbol? at = t; at is not null && typed is null; at = at.Base)
        {
            typed = at.Methods.FirstOrDefault(
                m => m.Name == "Equals" && !m.Static && m.Params.Count == 1 && m.Decl?.Body is not null
                  && m.Returns.Prim == Prim.Bool && m.Params[0].Type.Symbol is { Kind: TypeKind.Class });
        }

        if (typed is null)
        {
            return null;
        }

        string label = "__equals_as_" + Safe(t.Name);

        if (_m.Functions.Any(had => had.Name == label))
        {
            return label;
        }

        Require(typed);

        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");

        f.Params.Add(self);
        f.Params.Add(other);

        Function savedF = _f;
        Builder savedE = _e;

        _f = f;
        _e = new Builder(f, f.NewBlock("entry"));

        Block ask = f.NewBlock("egask");
        Block no = f.NewBlock("egno");

        _e.Branch(TypeTest(other, typed.Params[0].Type.Symbol!), ask, no);
        _e.SetBlock(ask);
        _e.Ret(RegOperand.Of(_e.Call(CallLabel(typed), IrType.I32, R(self), R(other))!));
        _e.SetBlock(no);
        _e.Ret(new ImmOperand(0, IrType.I32));

        _f = savedF;
        _e = savedE;
        _m.Functions.Add(f);
        return label;
    }

    // ---- delegates ----------------------------------------------------------------
    //
    // A DELEGATE IS EQUAL TO ANOTHER OF THE SAME METHOD ON THE SAME TARGET,
    // which is .NET's Delegate.Equals and what `-=` finds the delegate to take
    // out by. A closure the checker writes for a lambda or a method group is a
    // class with fields and an Invoke and nothing else, so what its Equals and
    // GetHashCode slots hold is written here: the runtime's comparison
    // (Runtime.DelegateEquals, Runtime.GroupEquals, Runtime.DelegateHash).

    /// <summary>A closure the checker made of a lambda or a method group, as a delegate.</summary>
    private static bool DelegateClosure(TypeSymbol t)
        => t.Decl?.LocalOnly == true && t.Name.StartsWith("Lambda$", StringComparison.Ordinal)
        && t.Interfaces.Any(face => face.Decl?.IsDelegate == true);

    /// <summary>
    /// A LAMBDA THAT HOLDS A COPY OF A PARAMETER is a delegate of one call's
    /// worth of that parameter. .NET hoists a captured parameter into a
    /// display object made when the method is entered, and the delegate's
    /// target is that object: a lambda made in two calls of the method is
    /// two targets, and the two are not equal whatever the values. A captured
    /// local is a cell here, one per scope entered, and its address says the
    /// same of it; a parameter nothing writes is copied into the closure
    /// instead (Binder.SettleCapturedCells), and the copy says nothing of
    /// which call made it -- so such a closure is equal only to itself, and
    /// hashes as itself.
    /// </summary>
    private static bool CapturesByValue(TypeSymbol t)
        => t.DelegateGroup is null
        && t.Fields.Any(f => !f.Static && !f.Boxed && f.Name != "$this" && f.Name != "$target");

    /// <summary>
    /// What a delegate closure's Equals slot holds. A lambda's: equal to a
    /// closure of the same class holding the same words (the same lambda over
    /// the same captures), Runtime.DelegateEquals. A method group's: one
    /// routine for each METHOD, `__group_equals$` and its identity, shared by
    /// every closure of that method whichever class converted it -- one class
    /// per converting type, and per unit -- so that the routine's address in
    /// the slot is the method's identity, which Runtime.GroupEquals compares
    /// along with the target. Null without the runtime's routines.
    /// </summary>
    private string? DelegateEqualsStub(TypeSymbol t)
    {
        if (CapturesByValue(t))
        {
            return null;
        }
        bool group = t.DelegateGroup is not null;
        MethodSymbol? same = group ? RuntimeMethod("GroupEquals", 3) : RuntimeMethod("DelegateEquals", 2);
        if (same is null)
        {
            return null;
        }
        string label = group ? "__group_equals$" + t.DelegateGroup : "__delegate_equals";
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        Require(same);
        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");
        f.Params.Add(self);
        f.Params.Add(other);
        Builder e = new(f, f.NewBlock("entry"));
        VReg said = group
            ? e.Call(CallLabel(same), IrType.I32, R(self), R(other), new ImmOperand((long)_b.EqualsSlot * _t.WordSize, IrType.I32))!
            : e.Call(CallLabel(same), IrType.I32, R(self), R(other))!;
        e.Ret(RegOperand.Of(said));
        _m.Functions.Add(f);
        return label;
    }

    /// <summary>
    /// What a delegate closure's GetHashCode slot holds: Runtime.DelegateHash
    /// of it, told -- for a method group's -- the address of its method's
    /// Equals routine (DelegateEqualsStub), so that equal delegates of two
    /// classes hash alike. Null without the runtime's routine.
    /// </summary>
    private string? DelegateHashStub(TypeSymbol t)
    {
        if (CapturesByValue(t))
        {
            return null;
        }
        MethodSymbol? hash = RuntimeMethod("DelegateHash", 2);
        string? identity = t.DelegateGroup is null ? null : DelegateEqualsStub(t);
        if (hash is null || (t.DelegateGroup is not null && identity is null))
        {
            return null;
        }
        string label = identity is null ? "__delegate_hash" : "__group_hash$" + t.DelegateGroup;
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        Require(hash);
        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        Builder e = new(f, f.NewBlock("entry"));
        Operand which = identity is null ? (Operand)new ImmOperand(0, IrType.I64) : R(WordAddress(e, identity));
        e.Ret(RegOperand.Of(e.Call(CallLabel(hash), IrType.I32, R(self), which)!));
        _m.Functions.Add(f);
        return label;
    }

    // ---- tuples -----------------------------------------------------------------
    //
    // A TUPLE IS EQUAL TO A TUPLE OF EQUAL THINGS, which is the whole reason to
    // key a table by one: `_shortReg[(vreg, instr)]` is looked up with a tuple
    // made for the question, never the one that went in. The class the checker
    // writes for a shape has fields and nothing else, so its two routines are
    // written here, field by field.

    private static bool IsTupleShape(TypeSymbol t)
        => t.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal);

    /// <summary>
    /// A tuple's text, as ValueTuple writes it: `(1, two, 3.5)`, each item as
    /// `"" + item` would show it -- a null one as nothing at all.
    /// </summary>
    private string TupleToString(TypeSymbol shape)
    {
        string label = "__tuple_tostring_" + Safe(shape.Name);

        if (_m.Functions.Any(had => had.Name == label))
        {
            return label;
        }

        // A SHAPE NO VALUE IS EVER BUILT OF -- an element that is a pointer, a
        // function pointer, a type that failed to resolve, or a bare type
        // parameter (a template's `ValueTuple$T$U`; shared code holds __canon
        // instead) -- says its type's name, as any object does.
        if (shape.Fields.Any(fd => !fd.Static && (fd.Type.IsError || fd.Type.IsPointer || fd.Type.Function is not null
                                                  || fd.Type.Prim == Prim.Void && fd.Type.Symbol is null && !fd.Type.IsArray)))
        {
            return ObjectToStringStub();
        }

        Function f = new(label, IrTypes.Word) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        Builder e = new(f, f.NewBlock("entry"));

        Function savedF = _f;
        Builder savedE = _e;
        _f = f;
        _e = e;

        // `this` is the tuple's bytes: a ValueTuple is a struct.
        LiteralExpr at = new() { Kind = Lit.Int, Text = "0", Line = 0, Col = 0 };
        VReg text = e.Address(InternString("("));
        List<FieldSymbol> items = shape.Fields.Where(fd => !fd.Static).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                text = StringBinary(at, BinOp.Add, text, e.Address(InternString(", ")));
            }
            VReg item = LoadPlace(PlaceOfField(items[i], self, at));
            if (CouldBeObject(items[i].Type))
            {
                // A null item is no text, as ValueTuple's `Item?.ToString()`.
                VReg said = f.NewReg(IrTypes.Word, "titem");
                Block some = f.NewBlock("tisome"), none = f.NewBlock("tinone"), joined = f.NewBlock("tijoin");
                e.Branch(item, some, none);
                e.SetBlock(some);
                e.CopyTo(said, R(Stringify(at, item, items[i].Type)));
                e.Jump(joined);
                e.SetBlock(none);
                e.CopyTo(said, R(e.Address(InternString(""))));
                e.Jump(joined);
                e.SetBlock(joined);
                text = StringBinary(at, BinOp.Add, text, said);
                continue;
            }
            text = StringBinary(at, BinOp.Add, text, Stringify(at, item, items[i].Type));
        }
        text = StringBinary(at, BinOp.Add, text, e.Address(InternString(")")));
        e.Ret(RegOperand.Of(text));

        _f = savedF;
        _e = savedE;
        _m.Functions.Add(f);
        return label;
    }

    // ---- order ------------------------------------------------------------------

    private bool _hasKeyCompare, _hasObjectCompare;

    /// <summary>
    /// The CompareTo a class wrote, reached at the shared slot as well as at
    /// whatever interface it was written for. The one that takes its own type
    /// when there are two.
    /// </summary>
    private string? OwnCompare(TypeSymbol t)
    {
        for (TypeSymbol? at = t; at is not null; at = at.Base)
        {
            List<MethodSymbol> found = at.Methods
                .Where(m => m.Name == "CompareTo" && !m.Static && m.Params.Count == 1
                         && m.Decl?.Body is not null && m.Returns.Prim == Prim.I32
                         && CouldBeObject(m.Params[0].Type)).ToList();

            if (found.Count > 0)
            {
                MethodSymbol chosen = found.FirstOrDefault(m => m.Params[0].Type.Symbol == at) ?? found[0];

                Require(chosen);
                return CallLabel(chosen);
            }
        }
        return null;
    }

    /// <summary>A class with no order of its own: by where it is, which is at least stable.</summary>
    private string ObjectCompareStub()
    {
        const string name = "__object_compareto";

        if (!_hasObjectCompare)
        {
            _hasObjectCompare = true;
            Function f = new(name, IrType.I32) { Coalescible = true };
            VReg a = f.NewReg(IrTypes.Word, "this");
            VReg b = f.NewReg(IrTypes.Word, "other");

            f.Params.Add(a);
            f.Params.Add(b);
            Builder e = new(f, f.NewBlock("entry"));

            EmitOrder(f, e, a, b, unsigned: true);
            _m.Functions.Add(f);
        }
        return name;
    }

    /// <summary>Returns -1, 0 or 1 for two registers of one width.</summary>
    private static void EmitOrder(Function f, Builder e, VReg a, VReg b, bool unsigned)
    {
        Block less = f.NewBlock("orless");
        Block rest = f.NewBlock("orrest");
        Block more = f.NewBlock("ormore");
        Block same = f.NewBlock("orsame");

        e.Branch(e.Binary(unsigned ? Opcode.LtU : Opcode.LtS, RegOperand.Of(a), RegOperand.Of(b), IrType.I32), less, rest);
        e.SetBlock(less);
        e.Ret(new ImmOperand(-1, IrType.I32));
        e.SetBlock(rest);
        e.Branch(e.Binary(Opcode.Eq, RegOperand.Of(a), RegOperand.Of(b), IrType.I32), same, more);
        e.SetBlock(same);
        e.Ret(new ImmOperand(0, IrType.I32));
        e.SetBlock(more);
        e.Ret(new ImmOperand(1, IrType.I32));
    }

    /// <summary>
    /// The default order of two keys, as .NET's default comparer has it: null
    /// before anything, strings as text, and otherwise the first one's own
    /// CompareTo through the shared slot. `of` as KeyEqualsStub's.
    /// </summary>
    private string KeyCompareStub(Type? of = null)
    {
        bool text = of is { Prim: Prim.String, IsArray: false, IsPointer: false };
        bool array = of is { IsArray: true, IsPointer: false };
        TypeSymbol? narrow = text || array ? null : NarrowKey(of);
        string name = text ? "__key_compare$$string" : array ? "__key_compare$$array" : narrow is null ? "__key_compare" : "__key_compare$" + TypeKey(narrow);

        if (text || array || narrow is not null ? !_structHelpers.Add(name) : _hasKeyCompare)
        {
            return name;
        }
        _hasKeyCompare |= !text && !array && narrow is null;

        Function f = KeyFunction(name, narrow);
        VReg a = f.NewReg(IrTypes.Word, "a");
        VReg b = f.NewReg(IrTypes.Word, "b");

        f.Params.Add(a);
        f.Params.Add(b);
        Builder e = new(f, f.NewBlock("entry"));
        Block same = f.NewBlock("kcsame");
        Block differ = f.NewBlock("kcdiffer");
        Block first = f.NewBlock("kcfirst");
        Block both = f.NewBlock("kcboth");
        Block word = f.NewBlock("kcword");
        Block less = f.NewBlock("kcless");
        Block more = f.NewBlock("kcmore");

        e.Branch(e.Binary(Opcode.Eq, a, b), same, differ);
        e.SetBlock(differ);
        e.Branch(a, first, less);
        e.SetBlock(first);
        e.Branch(b, both, more);
        e.SetBlock(both);

        if (text)
        {
            TextOrder(e, a, b, word);
        }
        else if (array)
        {
            e.Jump(word);
        }
        else
        {
            Block ask = f.NewBlock("kcask");
            VReg vt = e.Load(IrTypes.Word, a, 0);

            // A class is never a string, an array or a box (KeyEqualsStub).
            if (narrow is { Kind: TypeKind.Class })
            {
                e.Jump(ask);
            }
            else
            {
                Block flat = f.NewBlock("kcflat");
                Block texts = f.NewBlock("kctext");
                VReg flags = Numbered(e, e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize));

                e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
                e.SetBlock(flat);
                e.Branch(e.Binary(Opcode.And, flags, 2), texts, word);
                e.SetBlock(texts);
                TextOrder(e, a, b, word);
            }

            e.SetBlock(ask);
            e.Ret(RegOperand.Of(AskSlot(e, a, vt, _b.CompareSlot, narrow, IrType.I32, R(a), R(b))));
        }

        e.SetBlock(word);
        EmitOrder(f, e, a, b, unsigned: true);
        e.SetBlock(same);
        e.Ret(new ImmOperand(0, IrType.I32));
        e.SetBlock(less);
        e.Ret(new ImmOperand(-1, IrType.I32));
        e.SetBlock(more);
        e.Ret(new ImmOperand(1, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>Two strings' order, from a block where `a` is known to be one; by their words (`word`) where the library has no Compare.</summary>
    private void TextOrder(Builder e, VReg a, VReg b, Block word)
    {
        MethodSymbol? compare = StringRoutine(Prelude.CompareMethod, 2);

        if (compare is not null)
        {
            e.Ret(RegOperand.Of(e.Call(CallLabel(compare), IrTypes.Of(compare.Returns), R(a), R(b))!));
        }
        else
        {
            e.Jump(word);
        }
    }

    /// <summary>
    /// `__tuple_compare_T(a, b)`: ValueTuple's CompareTo over two tuples'
    /// bytes -- each item in turn by Comparer&lt;T&gt;.Default (ItemOrder), the
    /// first that differs deciding.
    /// </summary>
    private string TupleCompare(TypeSymbol shape)
    {
        string label = "__tuple_compare_" + Safe(shape.Name);
        if (!_structHelpers.Add(label))
        {
            return label;
        }

        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");
        f.Params.Add(self);
        f.Params.Add(other);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail;
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;
        Node at = new MethodDecl { Name = label, Line = 0, Col = 0 };

        foreach (FieldSymbol field in shape.Fields.Where(fd => !fd.Static))
        {
            VReg order = ItemOrder(at, LoadPlace(PlaceOfField(field, self, at)), LoadPlace(PlaceOfField(field, other, at)), field.Type);
            Block answer = f.NewBlock("tcanswer"), next = f.NewBlock("tcnext");
            _e.Branch(order, answer, next);
            _e.SetBlock(answer);
            _e.Ret(R(order));
            _e.SetBlock(next);
        }
        _e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail;
        return label;
    }

    /// <summary>
    /// Two values in Comparer&lt;T&gt;.Default's order, as -1, 0 or 1: an object
    /// by its CompareTo (null first), a nullable value with no value first
    /// and then by its value, a struct by its own CompareTo (none: equal), a
    /// float with NaN before everything and equal to itself, an integer by
    /// its value, signed or not.
    /// </summary>
    private VReg ItemOrder(Node at, VReg x, VReg y, Type of)
    {
        VReg order = _f.NewReg(IrType.I32, "order");
        Block done = _f.NewBlock("orddone");
        if (CouldBeObject(of))
        {
            _e.CopyTo(order, R(_e.Call(KeyCompareStub(of), IrType.I32, R(x), R(y))!));
        }
        else if (of.IsNullableValue)
        {
            VReg hasX = HasValue(x), hasY = HasValue(y);
            Block both = _f.NewBlock("ordboth");
            _e.CopyTo(order, R(_e.Binary(Opcode.Sub, R(hasX), R(hasY), IrType.I32)));
            _e.Branch(_e.Binary(Opcode.And, R(hasX), R(hasY), IrType.I32), both, done);
            _e.SetBlock(both);
            _e.CopyTo(order, R(ItemOrder(at, NullableRead(x, of), NullableRead(y, of), of.Underlying)));
        }
        else if (IsStructValue(of))
        {
            _e.CopyTo(order, Imm(0, IrType.I32));
            if (StructCompareTo(StructOf(of), out bool boxed) is MethodSymbol own)
            {
                Require(own);
                VReg them = boxed ? BoxValue(at, y, of) : y;
                VReg said = CallDirect(own, IrType.I32, new List<Operand> { R(x), R(them) })!;
                _e.CopyTo(order, R(said.Type == IrType.I32 ? said : _e.Unary(Opcode.Trunc64, R(said), IrType.I32)));
            }
        }
        else if (of.Prim is Prim.F32 or Prim.F64)
        {
            Block notLess = _f.NewBlock("ordfnl"), notMore = _f.NewBlock("ordfnm"), unordered = _f.NewBlock("ordfnan");
            _e.CopyTo(order, Imm(-1, IrType.I32));
            _e.Branch(_e.Binary(Opcode.FLt, R(x), R(y), IrType.I32), done, notLess);
            _e.SetBlock(notLess);
            _e.CopyTo(order, Imm(1, IrType.I32));
            _e.Branch(_e.Binary(Opcode.FGt, R(x), R(y), IrType.I32), done, notMore);
            _e.SetBlock(notMore);
            _e.CopyTo(order, Imm(0, IrType.I32));
            _e.Branch(_e.Binary(Opcode.FEq, R(x), R(y), IrType.I32), done, unordered);
            // One or both NaN: NaN first, and equal to NaN.
            _e.SetBlock(unordered);
            VReg nanX = _e.Binary(Opcode.FNe, R(x), R(x), IrType.I32), nanY = _e.Binary(Opcode.FNe, R(y), R(y), IrType.I32);
            _e.CopyTo(order, R(_e.Binary(Opcode.Sub, R(nanY), R(nanX), IrType.I32)));
        }
        else
        {
            bool unsigned = of.IsUnsigned || of.Prim is Prim.Bool or Prim.Char;
            VReg less = _e.Binary(unsigned ? Opcode.LtU : Opcode.LtS, R(x), R(y), IrType.I32);
            VReg more = _e.Binary(unsigned ? Opcode.GtU : Opcode.GtS, R(x), R(y), IrType.I32);
            _e.CopyTo(order, R(_e.Binary(Opcode.Sub, R(more), R(less), IrType.I32)));
        }
        _e.Jump(done);
        _e.SetBlock(done);
        return order;
    }

    /// <summary>A boxed value's hash: the value, so that equal boxes hash alike.</summary>
    private string BoxHash(Type of, string name)
    {
        string label = "__box_hash_" + Safe(name);
        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");

        f.Params.Add(self);
        Builder e = new(f, f.NewBlock("entry"));

        if (BoxSlot(of).IsFloat())
        {
            // Equality canonicalizes signed zero and every NaN payload;
            // hashing must do the same. Hash values are implementation
            // details, but equal boxed values must always share one.
            bool wide = BoxSlot(of) == IrType.F64;
            IrType bitsType = wide ? IrType.I64 : IrType.I32;
            VReg bits = Numbered(e, e.Load(bitsType, self, _t.ObjectHeaderBytes, wide ? 8 : 4, false));
            VReg magnitude = e.Binary(Opcode.And, R(bits),
                new ImmOperand(wide ? long.MaxValue : int.MaxValue, bitsType), bitsType);
            Block zero = f.NewBlock("hashzero"), nonzero = f.NewBlock("hashnonzero");
            e.Branch(e.Binary(Opcode.Eq, R(magnitude), new ImmOperand(0, bitsType), IrType.I32), zero, nonzero);
            e.SetBlock(zero);
            e.Ret(new ImmOperand(0, IrType.I32));
            e.SetBlock(nonzero);
            Block nan = f.NewBlock("hashnan"), finite = f.NewBlock("hashbits");
            e.Branch(e.Binary(Opcode.GtU, R(magnitude),
                new ImmOperand(wide ? 0x7ff0000000000000L : 0x7f800000L, bitsType), IrType.I32), nan, finite);
            e.SetBlock(nan);
            e.Ret(new ImmOperand(wide ? 0x7ff00000 : 0x7f800000, IrType.I32));
            e.SetBlock(finite);
            if (wide)
            {
                VReg high = e.Binary(Opcode.ShrU, R(bits), new ImmOperand(32, IrType.I32), IrType.I64);
                e.Ret(R(e.Unary(Opcode.Trunc64, e.Binary(Opcode.Xor, bits, high))));
            }
            else e.Ret(R(bits));
            _m.Functions.Add(f);
            return label;
        }

        if (BoxedBlock(of))
        {
            // A struct by its fields, as its Equals is (StructHash).
            e.Ret(RegOperand.Of(e.Call(StructHash(of.Symbol!), IrType.I32, R(e.Binary(Opcode.Add, self, _t.ObjectHeaderBytes)))!));
            _m.Functions.Add(f);
            return label;
        }
        e.Ret(RegOperand.Of(Numbered(e, e.Load(IrType.I32, self, _t.ObjectHeaderBytes, Math.Min(4, Math.Max(1, of.Size)), false), NeverAddress(of))));
        _m.Functions.Add(f);
        return label;
    }
}
