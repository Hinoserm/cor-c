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

            e.Ret(new RegOperand(e.Binary(Opcode.ShrU, word, 3)));
            _m.Functions.Add(f);
        }
        return name;
    }

    /// <summary>
    /// object.Equals(a, b), as .NET defines it: the same reference, or neither
    /// of them null and a's own Equals says so. A string and an array have no
    /// slots to ask through: two strings compare as text, and anything else
    /// without slots is only ever itself.
    /// </summary>
    private string KeyEqualsStub()
    {
        const string name = "__key_equals";

        if (_hasKeyEquals)
        {
            return name;
        }

        _hasKeyEquals = true;
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "a");
        VReg b = f.NewReg(IrTypes.Word, "b");

        f.Params.Add(a);
        f.Params.Add(b);
        Builder e = new(f, f.NewBlock("entry"));
        Block yes = f.NewBlock("keyes");
        Block differ = f.NewBlock("kediffer");
        Block first = f.NewBlock("kefirst");
        Block both = f.NewBlock("keboth");
        Block flat = f.NewBlock("keflat");
        Block text = f.NewBlock("ketext");
        Block texts = f.NewBlock("ketexts");
        Block ask = f.NewBlock("keask");
        Block no = f.NewBlock("keno");

        e.Branch(e.Binary(Opcode.Eq, a, b), yes, differ);
        e.SetBlock(differ);
        e.Branch(a, first, no);
        e.SetBlock(first);
        e.Branch(b, both, no);
        e.SetBlock(both);

        VReg vt = e.Load(IrTypes.Word, a, 0);
        VReg flags = e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);

        e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
        e.SetBlock(flat);
        e.Branch(e.Binary(Opcode.And, flags, 2), text, no);
        e.SetBlock(text);

        VReg other = e.Load(IrTypes.Word, b, 0);
        VReg otherFlags = e.Load(IrType.I32, other, -_t.DescriptorBytes + DescFlags * _t.WordSize);

        e.Branch(e.Binary(Opcode.And, otherFlags, 2), texts, no);
        e.SetBlock(texts);

        MethodSymbol? equals = StringEqualsRoutine();
        MethodSymbol? compare = equals is null ? StringRoutine(Prelude.CompareMethod, 2) : null;

        if (equals is not null)
        {
            e.Ret(new RegOperand(e.Call(CallLabel(equals), IrTypes.Of(equals.Returns), R(a), R(b))!));
        }
        else if (compare is not null)
        {
            VReg order = e.Call(CallLabel(compare), IrTypes.Of(compare.Returns), R(a), R(b))!;

            e.Ret(new RegOperand(e.Binary(Opcode.Eq, R(order), new ImmOperand(0, order.Type), IrType.I32)));
        }
        else
        {
            e.Ret(new ImmOperand(0, IrType.I32));
        }

        e.SetBlock(ask);
        VReg fn = e.Load(IrTypes.Word, vt, (long)_b.EqualsSlot * _t.WordSize);

        e.Ret(new RegOperand(e.CallIndirect(R(fn), IrType.I32, new Operand[] { R(a), R(b) })!));
        e.SetBlock(yes);
        e.Ret(new ImmOperand(1, IrType.I32));
        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>
    /// The hash that goes with <see cref="KeyEqualsStub"/>: nothing for null,
    /// the text's for a string, the object's own GetHashCode through its slot,
    /// and for anything without slots the word it is.
    /// </summary>
    private string KeyHashStub()
    {
        const string name = "__key_hash";

        if (_hasKeyHash)
        {
            return name;
        }

        _hasKeyHash = true;
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "a");

        f.Params.Add(a);
        Builder e = new(f, f.NewBlock("entry"));
        Block some = f.NewBlock("khsome");
        Block flat = f.NewBlock("khflat");
        Block text = f.NewBlock("khtext");
        Block word = f.NewBlock("khword");
        Block ask = f.NewBlock("khask");
        Block none = f.NewBlock("khnone");

        e.Branch(a, some, none);
        e.SetBlock(some);

        VReg vt = e.Load(IrTypes.Word, a, 0);
        VReg flags = e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);

        e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
        e.SetBlock(flat);
        e.Branch(e.Binary(Opcode.And, flags, 2), text, word);
        e.SetBlock(text);

        MethodSymbol? hash = StringRoutine("KeyHash", 1);

        if (hash is not null)
        {
            e.Ret(new RegOperand(e.Call(CallLabel(hash), IrTypes.Of(hash.Returns), R(a))!));
        }
        else
        {
            e.Jump(word);
        }

        e.SetBlock(word);
        e.Ret(new RegOperand(IrTypes.Word == IrType.I64 ? e.Unary(Opcode.Trunc64, a) : a));

        e.SetBlock(ask);
        VReg fn = e.Load(IrTypes.Word, vt, (long)_b.HashSlot * _t.WordSize);

        e.Ret(new RegOperand(e.CallIndirect(R(fn), IrType.I32, new Operand[] { R(a) })!));
        e.SetBlock(none);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return name;
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

        MethodSymbol? typed = sym.Methods.FirstOrDefault(m => m.Name == "Equals" && !m.Static && m.Params.Count == 1
                                                            && !m.Params[0].ByRef && m.Params[0].Type.Symbol == sym);
        MethodSymbol? untyped = sym.Methods.FirstOrDefault(m => m.Name == "Equals" && !m.Static && m.Params.Count == 1
                                                              && m.Params[0].Type.Prim == Prim.Any);
        if (typed is not null || untyped is not null)
        {
            MethodSymbol eq = typed ?? untyped!;
            Require(eq);
            VReg other = typed is not null ? b : Box(at, b, new Type { Symbol = sym });
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

    /// <summary>One field of two structs compared: 1 when they are the same.</summary>
    private VReg SameField(Node at, VReg a, VReg b, FieldSymbol field)
    {
        if (IsStructValue(field.Type))
        {
            VReg x = _e.Load(IrTypes.Word, a, field.Offset), y = _e.Load(IrTypes.Word, b, field.Offset);
            return _e.Call(StructEquals(field.Type.Symbol!), IrType.I32, R(x), R(y))!;
        }
        if (CouldBeObject(field.Type) || field.Type.IsNullableValue)
        {
            VReg x = _e.Load(IrTypes.Word, a, field.Offset), y = _e.Load(IrTypes.Word, b, field.Offset);
            return _e.Call(KeyEqualsStub(), IrType.I32, R(x), R(y))!;
        }
        // A number by its bits (a float's too, as ValueType.Equals compares
        // a struct with no references), read at its own width.
        Type raw = field.Type.Prim == Prim.F32 ? Type.I32 : field.Type.Prim == Prim.F64 ? Type.I64 : field.Type;
        VReg p = LoadPlace(new MemPlace(R(a), field.Offset, raw)), q = LoadPlace(new MemPlace(R(b), field.Offset, raw));
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

        MethodSymbol? own = sym.Methods.FirstOrDefault(m => m.Name == "GetHashCode" && !m.Static && m.Params.Count == 0);
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
                    v = _e.Call(StructHash(field.Type.Symbol!), IrType.I32, R(_e.Load(IrTypes.Word, a, field.Offset)))!;
                }
                else if (CouldBeObject(field.Type) || field.Type.IsNullableValue)
                {
                    v = _e.Call(KeyHashStub(), IrType.I32, R(_e.Load(IrTypes.Word, a, field.Offset)))!;
                }
                else
                {
                    Type raw = field.Type.Prim == Prim.F32 ? Type.I32 : field.Type.Prim == Prim.F64 ? Type.I64 : field.Type;
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
    /// A struct's order for the default comparer: its own CompareTo(T), else
    /// its CompareTo(object) handed b boxed; null when it has neither -- an
    /// unordered struct sorts as all equal.
    /// </summary>
    private MethodSymbol? StructCompareTo(TypeSymbol sym, out bool boxed)
    {
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
        _e.Ret(new RegOperand(_e.Call(CallLabel(typed), IrType.I32, R(self), R(other))!));
        _e.SetBlock(no);
        _e.Ret(new ImmOperand(0, IrType.I32));

        _f = savedF;
        _e = savedE;
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

    private string TupleEquals(TypeSymbol shape)
    {
        string label = "__tuple_equals_" + Safe(shape.Name);

        if (_m.Functions.Any(had => had.Name == label))
        {
            return label;
        }

        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");

        f.Params.Add(self);
        f.Params.Add(other);
        Builder e = new(f, f.NewBlock("entry"));
        Block no = f.NewBlock("tqno");
        Block some = f.NewBlock("tqsome");
        Block shaped = f.NewBlock("tqshaped");

        e.Branch(other, some, no);
        e.SetBlock(some);
        e.Branch(e.Binary(Opcode.Eq, e.Load(IrTypes.Word, self, 0), e.Load(IrTypes.Word, other, 0)), shaped, no);
        e.SetBlock(shaped);

        foreach (FieldSymbol field in shape.Fields.Where(fd => !fd.Static))
        {
            Block next = f.NewBlock("tqnext");
            Type of = field.Type;

            if (CouldBeObject(of))
            {
                VReg x = e.Load(IrTypes.Word, self, field.Offset);
                VReg y = e.Load(IrTypes.Word, other, field.Offset);

                e.Branch(e.Call(KeyEqualsStub(), IrType.I32, R(x), R(y))!, next, no);
            }
            else if (BoxedBlock(of) || of.IsNullableValue)
            {
                // A struct held in line is its bytes. A nullable value is a
                // cell and is compared as the cell it is, which says equal for
                // two empty ones and for the same one; two cells holding the
                // same number are not yet the same key.
                int bytes = of.IsNullableValue ? _t.WordSize : Math.Max(1, of.Size);

                for (int at = 0; at < bytes; at++)
                {
                    Block more = f.NewBlock("tqbyte");
                    VReg x = e.Load(IrType.I32, self, field.Offset + at, 1, false);
                    VReg y = e.Load(IrType.I32, other, field.Offset + at, 1, false);

                    e.Branch(e.Binary(Opcode.Eq, x, y), more, no);
                    e.SetBlock(more);
                }
                e.Jump(next);
            }
            else
            {
                int size = Math.Max(1, of.Size);
                bool signed = !of.IsUnsigned && of.Prim != Prim.Bool;
                VReg x = e.Load(BoxSlot(of), self, field.Offset, size, signed);
                VReg y = e.Load(BoxSlot(of), other, field.Offset, size, signed);

                e.Branch(e.Binary(Opcode.Eq, R(x), R(y), IrType.I32), next, no);
            }

            e.SetBlock(next);
        }

        e.Ret(new ImmOperand(1, IrType.I32));
        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return label;
    }

    private string TupleHash(TypeSymbol shape)
    {
        string label = "__tuple_hash_" + Safe(shape.Name);

        if (_m.Functions.Any(had => had.Name == label))
        {
            return label;
        }

        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");

        f.Params.Add(self);
        Builder e = new(f, f.NewBlock("entry"));
        VReg hash = f.NewReg(IrType.I32, "hash");

        e.CopyTo(hash, new ImmOperand(17, IrType.I32));

        foreach (FieldSymbol field in shape.Fields.Where(fd => !fd.Static))
        {
            Type of = field.Type;
            VReg part;

            if (CouldBeObject(of))
            {
                part = e.Call(KeyHashStub(), IrType.I32, R(e.Load(IrTypes.Word, self, field.Offset)))!;
            }
            else if (BoxedBlock(of) || of.IsNullableValue)
            {
                // Equal ones hash alike, which is all a hash has to promise.
                part = e.Load(IrType.I32, self, field.Offset, 1, false);
            }
            else
            {
                part = e.Load(IrType.I32, self, field.Offset, Math.Min(4, Math.Max(1, of.Size)), false);
            }

            e.CopyTo(hash, new RegOperand(e.Binary(Opcode.Add, e.Binary(Opcode.Mul, hash, 31), part)));
        }

        e.Ret(new RegOperand(hash));
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

        e.Branch(e.Binary(unsigned ? Opcode.LtU : Opcode.LtS, new RegOperand(a), new RegOperand(b), IrType.I32), less, rest);
        e.SetBlock(less);
        e.Ret(new ImmOperand(-1, IrType.I32));
        e.SetBlock(rest);
        e.Branch(e.Binary(Opcode.Eq, new RegOperand(a), new RegOperand(b), IrType.I32), same, more);
        e.SetBlock(same);
        e.Ret(new ImmOperand(0, IrType.I32));
        e.SetBlock(more);
        e.Ret(new ImmOperand(1, IrType.I32));
    }

    /// <summary>
    /// The default order of two keys, as .NET's default comparer has it: null
    /// before anything, strings as text, and otherwise the first one's own
    /// CompareTo through the shared slot.
    /// </summary>
    private string KeyCompareStub()
    {
        const string name = "__key_compare";

        if (_hasKeyCompare)
        {
            return name;
        }

        _hasKeyCompare = true;
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "a");
        VReg b = f.NewReg(IrTypes.Word, "b");

        f.Params.Add(a);
        f.Params.Add(b);
        Builder e = new(f, f.NewBlock("entry"));
        Block same = f.NewBlock("kcsame");
        Block differ = f.NewBlock("kcdiffer");
        Block first = f.NewBlock("kcfirst");
        Block both = f.NewBlock("kcboth");
        Block flat = f.NewBlock("kcflat");
        Block text = f.NewBlock("kctext");
        Block word = f.NewBlock("kcword");
        Block ask = f.NewBlock("kcask");
        Block less = f.NewBlock("kcless");
        Block more = f.NewBlock("kcmore");

        e.Branch(e.Binary(Opcode.Eq, a, b), same, differ);
        e.SetBlock(differ);
        e.Branch(a, first, less);
        e.SetBlock(first);
        e.Branch(b, both, more);
        e.SetBlock(both);

        VReg vt = e.Load(IrTypes.Word, a, 0);
        VReg flags = e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);

        e.Branch(e.Binary(Opcode.And, flags, 1), flat, ask);
        e.SetBlock(flat);
        e.Branch(e.Binary(Opcode.And, flags, 2), text, word);
        e.SetBlock(text);

        MethodSymbol? compare = StringRoutine(Prelude.CompareMethod, 2);

        if (compare is not null)
        {
            e.Ret(new RegOperand(e.Call(CallLabel(compare), IrTypes.Of(compare.Returns), R(a), R(b))!));
        }
        else
        {
            e.Jump(word);
        }

        e.SetBlock(word);
        EmitOrder(f, e, a, b, unsigned: true);

        e.SetBlock(ask);
        VReg fn = e.Load(IrTypes.Word, vt, (long)_b.CompareSlot * _t.WordSize);

        e.Ret(new RegOperand(e.CallIndirect(R(fn), IrType.I32, new Operand[] { R(a), R(b) })!));
        e.SetBlock(same);
        e.Ret(new ImmOperand(0, IrType.I32));
        e.SetBlock(less);
        e.Ret(new ImmOperand(-1, IrType.I32));
        e.SetBlock(more);
        e.Ret(new ImmOperand(1, IrType.I32));
        _m.Functions.Add(f);
        return name;
    }

    /// <summary>A tuple before another when its first differing element is.</summary>
    private string TupleCompare(TypeSymbol shape)
    {
        string label = "__tuple_compare_" + Safe(shape.Name);

        if (_m.Functions.Any(had => had.Name == label))
        {
            return label;
        }

        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");

        f.Params.Add(self);
        f.Params.Add(other);
        Builder e = new(f, f.NewBlock("entry"));
        Block some = f.NewBlock("tcsome");
        Block none = f.NewBlock("tcnone");

        e.Branch(other, some, none);
        e.SetBlock(none);
        e.Ret(new ImmOperand(1, IrType.I32));
        e.SetBlock(some);

        foreach (FieldSymbol field in shape.Fields.Where(fd => !fd.Static))
        {
            Type of = field.Type;
            Block next = f.NewBlock("tcnext");
            Block less = f.NewBlock("tcless");
            Block more = f.NewBlock("tcmore");

            if (CouldBeObject(of))
            {
                VReg x = e.Load(IrTypes.Word, self, field.Offset);
                VReg y = e.Load(IrTypes.Word, other, field.Offset);
                VReg order = e.Call(KeyCompareStub(), IrType.I32, R(x), R(y))!;
                Block decided = f.NewBlock("tcdecided");

                e.Branch(order, decided, next);
                e.SetBlock(decided);
                e.Ret(new RegOperand(order));
            }
            else if (!BoxedBlock(of) && !of.IsNullableValue && !of.IsFloat)
            {
                int size = Math.Max(1, of.Size);
                bool unsigned = of.IsUnsigned || of.Prim == Prim.Bool;
                VReg x = e.Load(BoxSlot(of), self, field.Offset, size, !unsigned);
                VReg y = e.Load(BoxSlot(of), other, field.Offset, size, !unsigned);
                Block rest = f.NewBlock("tcrest");

                e.Branch(e.Binary(unsigned ? Opcode.LtU : Opcode.LtS, R(x), R(y), IrType.I32), less, rest);
                e.SetBlock(rest);
                e.Branch(e.Binary(Opcode.Eq, R(x), R(y), IrType.I32), next, more);
            }
            else
            {
                e.Jump(next);
            }

            e.SetBlock(less);
            e.Ret(new ImmOperand(-1, IrType.I32));
            e.SetBlock(more);
            e.Ret(new ImmOperand(1, IrType.I32));
            e.SetBlock(next);
        }

        e.Ret(new ImmOperand(0, IrType.I32));
        _m.Functions.Add(f);
        return label;
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
            VReg bits = e.Load(bitsType, self, _t.ObjectHeaderBytes, wide ? 8 : 4, false);
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

        e.Ret(new RegOperand(e.Load(IrType.I32, self, _t.ObjectHeaderBytes,
                                    BoxedBlock(of) ? 1 : Math.Min(4, Math.Max(1, of.Size)), false)));
        _m.Functions.Add(f);
        return label;
    }
}
