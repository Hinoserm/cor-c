#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    /// <summary>Expressions whose rewrite is being emitted right now, so a rewrite containing itself terminates.</summary>
    private readonly HashSet<Expr> _rewriting = new(ReferenceEqualityComparer.Instance);

    private static RegOperand R(VReg v) => new(v);
    private static ImmOperand Imm(long v, IrType t) => new(v, t);

    /// <summary>
    /// Evaluates an expression to a register holding its canonical value:
    /// narrow integers extended to 32 bits per their signedness, bools 0 or
    /// 1, references as words, and anything the binder marked boxed or viewed
    /// already wrapped.
    /// </summary>
    private VReg Eval(Expr e)
    {
        VReg v;
        if (_b.Rewrites.TryGetValue(e, out Expr? instead) && _rewriting.Add(e))
        {
            // THROUGH Eval AND NOT EvalCore, so that whatever the checker
            // recorded about the expression it wrote -- a box, a view -- is
            // applied to it. A conversion that both rewrites a node and wraps
            // the result has nowhere else to put the wrapping: hanging it on
            // the ORIGINAL node would wrap it again inside its own replacement,
            // where the original is still evaluated for what it is.
            v = Eval(instead);
            _rewriting.Remove(e);
        }
        else
        {
            v = EvalCore(e);
        }

        if (_b.Boxes.Contains(e))
        {
            // Into the Nullable it is wanted as, converted first: an int
            // argument for a ulong? is a ulong in a ulong?'s shape.
            Type had = _b.TypeOf(e);
            if (_b.BoxedAs.TryGetValue(e, out Type? into) && into.IsNullableValue && !into.Underlying.Equals(had.AsNonNullable()))
            {
                v = Box(e, Convert(e, v, had, into.Underlying), into);
            }
            else
            {
                v = Box(e, v, had);
            }
        }

        if (_b.Views.TryGetValue(e, out TypeSymbol? view))
        {
            // NO ARRAY IS NO LIST. A null `string[]?` becoming an
            // `IReadOnlyList<string>?` is null, not a helper holding nothing:
            // wrapped regardless, `names != null` was true of it and the first
            // `.Count` read the length of an array that was not there.
            VReg held = _f.NewReg(IrTypes.Word, "view");
            Block wrap = _f.NewBlock("viewwrap");
            Block done = _f.NewBlock("viewdone");

            // Null on the way round, not the array: a view held as "the
            // array, or what was made" was never the caller's own to free,
            // and every conversion in a loop was left to the collector.
            _e.CopyTo(held, Imm(0, IrTypes.Word));
            _e.Branch(v, wrap, done);
            _e.SetBlock(wrap);

            VReg made = Allocate(e, view.InstanceSize, described: true);
            _e.Store(R(made), VtableOf(view), 0, _t.WordSize);
            StoreNewReference(made, v, _t.ObjectHeaderBytes);
            _e.CopyTo(held, R(made));
            _e.Jump(done);
            _e.SetBlock(done);
            v = held;
        }

        return v;
    }

    /// <summary>Evaluates and converts to the type a slot, parameter or return wants.</summary>
    /// <summary>An argument evaluated for the parameter it fills in.</summary>
    /// <summary>The class a non-nullable tuple type is made as, or null.</summary>
    private static TypeSymbol? TupleShapeOf(Type t)
        => t is { Nullable: false, IsPointer: false, IsArray: false, Symbol: { Kind: TypeKind.Struct } shape } && IsTupleShape(shape) ? shape : null;

    private VReg EvalAs(Expr e, ParamSymbol p) => EvalAs(e, p.Type, p.ByRef, p.ReadOnly);

    private VReg EvalAs(Expr e, Type target, bool byRef = false, bool readOnly = false)
    {
        // `default` WANTED AS A TUPLE is one: typed by the checker as the null
        // literal it looks like, it was converted to the tuple's class as null.
        if (!byRef && e is DefaultExpr && TupleShapeOf(target) is TypeSymbol tupleShape)
        {
            return NewStruct(e, tupleShape);
        }

        if (byRef)
        {
            // `in` TAKES A VALUE WHERE `ref` TAKES A VARIABLE. C# passes the
            // address either way, so an argument with no address of its own --
            // a literal, the result of a call -- is copied into a temporary of
            // this frame and the temporary's address is what the callee gets.
            // `ref` and `out` have no such rule: what they point at is the
            // caller's, and a value has nowhere for the callee to write.
            // A STRUCT BY REFERENCE IS THE ADDRESS OF ITS BYTES: the block a
            // local, an element or a field of its own points at, or where one
            // held in line in its object is (FieldSymbol.Inline). The callee
            // reads and writes through it (PlaceOfSym's by-reference struct is
            // an inline place), so every one of those is passed the same way,
            // and `in` with a value passes a copy of it.
            if (IsStructValue(target))
            {
                if (readOnly && !HasAddress(e)) return EvalAs(e, target);
                return StructReference(e is RefArgExpr sra ? sra.Target : e, target, fresh: e is RefArgExpr { Name: not null });
            }

            if (readOnly && !HasAddress(e))
            {
                VReg value = EvalAs(e, target);
                int size = Math.Max(4, target.Size);
                FrameSlot temp = _f.NewSlot(size, Math.Min(size, _t.Align64), "in");

                _e.Store(new SlotOperand(temp), new RegOperand(value), 0, LoadSize(target));
                return RegOf(new SlotOperand(temp));
            }

            // `out x` and `ref x` wrap the variable; the address wanted is the
            // variable's. A bare expression against a by-reference parameter
            // (the binder allows an implicit ref) is asked for directly.
            return e is RefArgExpr ra ? AddressOf(ra, ra.Target) : AddressOf(e, e);
        }

        VReg v = Eval(e);

        if (_b.Boxes.Contains(e) || _b.Views.ContainsKey(e))
        {
            return v;
        }

        // A STRUCT IS A VALUE: assigning it, passing it or returning it makes
        // a copy, as C# promises. The representation is still a pointer to a
        // block, so a copy is a fresh block with the bytes moved over -- unless
        // the expression already produced a block nobody else holds.
        // Only a struct already: anything else is made one by the conversion
        // below -- a number or a null into a Nullable<T> -- and is new.
        if (IsStructValue(target) && IsStructValue(_b.TypeOf(e)) && !IsFreshStruct(e)
            && ReferenceEquals(StructOf(_b.TypeOf(e)), StructOf(target)))
        {
            v = CopyStruct(e, v, StructOf(target));
        }

        // A USER-DEFINED CONVERSION already produced the operator's result;
        // what is left to convert is from that, not from what was written.
        // So did a TUPLE REBUILT in another shape (Binder.CheckAssignable):
        // the value is already the wider tuple, and converting it again from
        // the arm's own narrower shape read its fields at the wrong widths.
        Type had = _b.Rewrites.TryGetValue(e, out Expr? made)
                   && (_b.UserConversions.Contains(made) || made is PatternExpr { Test: TupleExpr })
                 ? _b.TypeOf(made) : _b.TypeOf(e);
        return Convert(e, v, had, target);
    }

    /// <summary>
    /// `ref x` where a reference is held rather than passed: the address of
    /// the variable, made as a by-reference argument's is -- a struct's the
    /// address of its bytes (StructReference).
    /// </summary>
    private VReg Reference(RefArgExpr referred, Type type) => EvalAs(referred, type, byRef: true);

    /// <summary>
    /// Whether an expression denotes STORAGE rather than a value -- a variable,
    /// a field, an array element, what is at a pointer. C# calls it a variable,
    /// and it is exactly what AddressOf below can answer for.
    /// </summary>
    private bool HasAddress(Expr e)
        => e switch
        {
            RefArgExpr => true,
            IndexExpr ix => ix.Args.Count == 1 && !_b.Indexers.ContainsKey(ix),
            MemberExpr me => _b.Resolved.TryGetValue(me, out Sym? s) && s is FieldSym,
            UnaryExpr { Op: UnOp.Deref } => true,
            NameExpr n => _b.Resolved.ContainsKey(n),
            CallExpr call => RefCallee(call) is not null,
            _ => false,
        };

    /// <summary>
    /// A struct held by value: copied at every boundary. Not a pointer to one.
    /// A nullable value is one too -- Nullable&lt;T&gt;, whose struct is its
    /// shape (StructOf).
    /// </summary>
    private static bool IsStructValue(Type t)
        => !t.IsPointer && !t.IsArray && (t.IsNullableValue || t.Symbol is { Kind: TypeKind.Struct } && !t.Nullable);

    /// <summary>
    /// AN ARRAY'S STRUCT ELEMENTS HELD IN LINE (TypeSymbol.HeldInline): each
    /// element is its bytes, at the stride ElementStride says, as .NET lays a
    /// struct array out -- not a word pointing at a block of its own, which
    /// cost an allocation for every element made, stored or copied.
    /// </summary>
    private bool InlineElement(Type element) => IsStructValue(element) && StructOf(element).HeldInline;

    /// <summary>The bytes from one element of an array to the next.</summary>
    private int ElementStride(Type element)
    {
        if (!InlineElement(element)) return Math.Max(1, element.Size);
        TypeSymbol held = StructOf(element);
        int align = Math.Max(1, held.InlineAlign);
        return (Math.Max(1, held.InstanceSize) + align - 1) / align * align;
    }

    /// <summary>Whether a struct held in line has a reference anywhere in its bytes.</summary>
    private bool InlineHasReferences(TypeSymbol held) => TracedFields(held, 0).Any(f => MayHoldReference(f.Type));

    /// <summary>
    /// An element stored into an array just made: a struct held in line is
    /// copied in, its references' cards marked; anything else is StoreNew's.
    /// </summary>
    private void StoreNewElement(VReg array, long offset, Type element, VReg value)
    {
        if (!InlineElement(element))
        {
            StoreNew(array, value, offset, element);
            return;
        }
        TypeSymbol held = StructOf(element);
        VReg into = _e.Binary(Opcode.Add, array, offset);
        _e.Emit(Opcode.MemCopy, null, R(into), R(value), Imm(Math.Max(1, held.InstanceSize), IrTypes.Word));
        foreach ((int at, Type type) in TracedFields(held, 0))
        {
            if (MayHoldReference(type)) CardMarkAt(R(into), at);
        }
    }

    /// <summary>
    /// Whether an expression's struct result is a block nobody else can
    /// reach, so copying it again would only cost. A constructor, a call
    /// (its return already copied), a default, a `with`, a tuple.
    /// </summary>
    private bool IsFreshStruct(Expr e)
    {
        if (_b.Rewrites.TryGetValue(e, out Expr? instead))
        {
            e = instead;
        }
        // Not a call that returns by reference: what it answers is a variable
        // somewhere else, and taking its value is a copy like any other's.
        if (e is CallExpr call && RefCallee(call) is not null) return false;
        return e is NewExpr or CallExpr or DefaultExpr or WithExpr or TupleExpr or ConditionalExpr or SwitchExpr;
    }

    /// <summary>
    /// A new block holding the same value: the copy a value type's assignment
    /// means. A struct with a struct inside it is copied through its type's
    /// copier (StructCopier), which copies the inner block too rather than
    /// sharing it -- `b = a; b.Inner.X = 1;` must leave `a.Inner` alone. A
    /// plain struct is its bytes, moved here in line, where escape analysis
    /// can still put the copy on the stack.
    /// </summary>
    private VReg CopyStruct(Node at, VReg src, TypeSymbol sym)
    {
        if (HasStructFields(sym))
        {
            VReg deep = _e.Call(StructCopier(at, sym), IrTypes.Word, R(src))!;
            _heapStructs.Add(deep);
            return deep;
        }
        int size = Math.Max(1, sym.InstanceSize);
        VReg made = Allocate(at, size);
        _e.Emit(Opcode.MemCopy, null, R(made), R(src), Imm(size, IrTypes.Word));
        _heapStructs.Add(made);
        return made;
    }

    /// <summary>
    /// A struct value about to be kept as a pointer in the heap -- a closure's
    /// cell, a struct not held in line -- as a block of the heap: one that may
    /// be its caller's frame (a call's result buffer) is copied first.
    /// </summary>
    private VReg HeapStruct(Node at, VReg value, Type type)
        => IsStructValue(type) && !_heapStructs.Contains(value) && value.Type == IrTypes.Word
            ? CopyStruct(at, value, StructOf(type)) : value;

    /// <summary>
    /// A struct's zero value, made: a zeroed block, and in each of its
    /// struct-typed fields a zeroed block of that field's own. A field of a
    /// struct type is a VALUE, and with structs as blocks reached by pointer
    /// the value has to exist before anything reads it, copies it or writes
    /// through it -- an unassigned one was a null pointer, and reading it
    /// read address 0.
    /// </summary>
    private VReg NewStruct(Node at, TypeSymbol sym)
    {
        VReg made = HasStructFields(sym) ? _e.Call(StructMaker(at, sym), IrTypes.Word)! : Allocate(at, Math.Max(1, sym.InstanceSize));
        _heapStructs.Add(made);
        return made;
    }

    /// <summary>
    /// Each struct-typed instance field of an object or struct block just
    /// made, given its zero value (NewStruct): what C# says every field of a
    /// new object is before a constructor or initialiser writes it. The
    /// block is fresh, so the stores need no write barrier, as the elements
    /// AllocateArray fills need none.
    /// </summary>
    private void InitStructFields(Node at, VReg obj, TypeSymbol sym)
    {
        for (TypeSymbol? t = sym; t is not null; t = t.Base)
        {
            foreach (FieldSymbol f in t.Fields)
            {
                if (f.Static || f.Boxed || f.Inline || !IsStructValue(f.Type))
                {
                    continue;
                }
                VReg inner = NewStruct(at, StructOf(f.Type));
                StoreNewReference(obj, inner, f.Offset);
            }
        }
    }

    /// <summary>Whether a struct holds a struct by value: then making or copying one is more than its bytes.</summary>
    private static bool HasStructFields(TypeSymbol sym)
        => sym.Fields.Any(f => !f.Static && !f.Boxed && !f.Inline && IsStructValue(f.Type));

    private readonly HashSet<string> _structHelpers = new(StringComparer.Ordinal);

    /// <summary>
    /// `__struct_new$T`: T's zero value (NewStruct), one function per struct
    /// type that holds another, so that each site making one is a call.
    /// </summary>
    private string StructMaker(Node at, TypeSymbol sym)
    {
        string name = "__struct_new$" + TypeKey(sym);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, IrTypes.Word) { Coalescible = true };
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail;
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;

        VReg made = Allocate(at, Math.Max(1, sym.InstanceSize));
        InitStructFields(at, made, sym);
        _e.Ret(R(made));

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail;
        return name;
    }

    /// <summary>
    /// `__struct_copy$T(src)`: a copy of a T that holds another struct -- the
    /// bytes moved, then each struct field replaced by a copy of the block
    /// it still shares with the source, all the way down.
    /// </summary>
    private string StructCopier(Node at, TypeSymbol sym)
    {
        string name = "__struct_copy$" + TypeKey(sym);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, IrTypes.Word) { Coalescible = true };
        VReg src = f.NewReg(IrTypes.Word, "src");
        f.Params.Add(src);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail;
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;

        int size = Math.Max(1, sym.InstanceSize);
        VReg made = Allocate(at, size);
        _e.Emit(Opcode.MemCopy, null, R(made), R(src), Imm(size, IrTypes.Word));
        foreach (FieldSymbol field in sym.Fields)
        {
            if (field.Static || field.Boxed || field.Inline || !IsStructValue(field.Type))
            {
                continue;
            }
            VReg shared = _e.Load(IrTypes.Word, made, field.Offset);
            VReg own = CopyStruct(at, shared, StructOf(field.Type));
            StoreNewReference(made, own, field.Offset);
        }
        _e.Ret(R(made));

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail;
        return name;
    }

    /// <summary>
    /// A value of one type as another, at an assignment or call boundary.
    /// Only numeric conversions do anything; a reference stays the word it
    /// is. Widening an unsigned narrow value is a no-op because the register
    /// is canonical; narrowing re-extends so the register stays canonical.
    /// </summary>
    private VReg Convert(Node at, VReg v, Type from, Type to)
    {
        if (from.IsError || to.IsError || from.Equals(to))
        {
            return v;
        }

        // A LIFTED CONVERSION. `long? folded = n;` for an `int? n` converts
        // what is IN the cell and leaves an empty one empty, which is what C#
        // does with a nullable on either side.
        //
        // A cell is an ADDRESS here, so without this the address itself was
        // sign-extended from four bytes to eight and then read through as
        // though it were the cell -- a silent wrong answer, and the one thing
        // this tree refuses to ship.
        if (from.IsNullableValue && to.IsNullableValue
            && !from.Underlying.Equals(to.Underlying))
        {
            Type inner = from.Underlying;
            Type wanted = to.Underlying;
            VReg lifted = _f.NewReg(IrTypes.Word, "lift");
            Block liftSome = _f.NewBlock("liftsome");
            Block liftEnd = _f.NewBlock("liftend");

            _e.CopyTo(lifted, R(NullableEmpty(at, to)));
            _e.Branch(HasValue(v), liftSome, liftEnd);
            _e.SetBlock(liftSome);

            VReg inside = NullableRead(v, from);

            _e.CopyTo(lifted, R(Box(at, Convert(at, inside, inner, wanted), wanted)));
            _e.Jump(liftEnd);
            _e.SetBlock(liftEnd);
            return lifted;
        }

        // AND A VALUE BECOMING A NULLABLE ONE GOES INTO A CELL, holding what
        // the cell is declared to hold: `long? x = anInt` converts the int to a
        // long first and then puts it there.
        //
        // The checker marks most of these and Eval boxes them; this is the ones
        // a conversion reaches directly -- a switch expression's arm, converted
        // to the type the whole expression came out as, above all.
        if (!from.IsNullableValue && to.IsNullableValue
            && from.Prim != Prim.NullLiteral && Boxable(from))
        {
            return Box(at, Convert(at, v, from, to.Underlying), to.Underlying);
        }

        // BOXING AND UNBOXING. A value converted to object is copied onto the
        // heap behind an object header -- see Lowering.Box -- and a cast back
        // out checks the box is of exactly the type asked for.
        if (to.Prim == Prim.Any && Boxable(from))
        {
            return BoxValue(at, v, from);
        }
        // A NULLABLE VALUE BOXED is its value boxed, or null when it has
        // none (C# 10.2.9): the cell itself is no object, and handed over
        // as one it was printed as though it were.
        if (from.IsNullableValue && (to.Prim == Prim.Any || to.Symbol is { Kind: TypeKind.Interface })
            && Boxable(from.Underlying))
        {
            VReg boxed = _f.NewReg(IrTypes.Word, "nbox");
            Block some = _f.NewBlock("nbsome");
            Block end = _f.NewBlock("nbend");
            _e.CopyTo(boxed, Imm(0, IrTypes.Word));
            _e.Branch(HasValue(v), some, end);
            _e.SetBlock(some);
            VReg inside = NullableRead(v, from);
            _e.CopyTo(boxed, R(BoxValue(at, inside, from.Underlying)));
            _e.Jump(end);
            _e.SetBlock(end);
            return boxed;
        }
        if (from.Prim == Prim.Any && Boxable(to))
        {
            return Unbox(at, v, to);
        }
        // A STRUCT OR ENUM TO AN INTERFACE IT IMPLEMENTS IS A BOX too, and an
        // interface cast back to the struct an unboxing.
        if (to.Symbol is { Kind: TypeKind.Interface } && Boxable(from))
        {
            return BoxValue(at, v, from);
        }
        if (from.Symbol is { Kind: TypeKind.Interface } && Boxable(to))
        {
            return Unbox(at, v, to);
        }

        // A NULLABLE VALUE PAST HERE IS A CELL'S ADDRESS, or null: a word,
        // whatever the cell holds. The rules below read its Prim -- a bool?'s
        // is bool -- and narrowed the address to 32 bits: nothing on i386,
        // where a word is 32 bits, and in long mode a null arm of `x?.M() ==
        // true` copied as an I32 into the cell register, which the verifier
        // refused at every link.
        // AN OBJECT INTO A NULLABLE VALUE (C# 10.3.7): null is one with no
        // value, anything else unboxed as T -- a box of another type refused
        // as any unboxing is -- and the value wrapped.
        if ((from.Prim == Prim.Any || from.Symbol is { Kind: TypeKind.Interface }) && to.IsNullableValue && Boxable(to.Underlying))
        {
            VReg made = _f.NewReg(IrTypes.Word, "unbn");
            Block some = _f.NewBlock("unbnsome"), end = _f.NewBlock("unbnend");
            _e.CopyTo(made, R(NullableEmpty(at, to)));
            _e.Branch(v, some, end);
            _e.SetBlock(some);
            _e.CopyTo(made, R(Box(at, Unbox(at, v, to.Underlying), to)));
            _e.Jump(end);
            _e.SetBlock(end);
            return made;
        }

        // NULL AS A NULLABLE VALUE is one with no value: all zero, as
        // default(T?) is, and not a null pointer.
        if (from.Prim == Prim.NullLiteral && to.IsNullableValue)
        {
            return NullableEmpty(at, to);
        }

        // A NULLABLE VALUE TO WHAT IT HOLDS is its Value (C# 10.3.4): what is
        // in the cell, and InvalidOperationException when there is none. Taken
        // as a word it handed over the cell's address as the value.
        if (from.IsNullableValue && !to.IsNullableValue && to.Prim != Prim.Any && to.Symbol is not { Kind: TypeKind.Interface })
        {
            return Convert(at, NullableValue(v, from.Underlying), from.Underlying, to);
        }

        if (from.IsNullableValue || to.IsNullableValue)
        {
            return v;
        }

        // AN ENUM CONVERTS AS ITS UNDERLYING TYPE, whatever that is: taken as
        // int, `(long)Status.Error` of a `: uint` enum sign-extended
        // 0xC0000001 into a negative number, which is not what C# gives.
        Type f = from.IsEnumValue && from.Symbol is { } enumFrom ? new Type { Prim = enumFrom.EnumUnderlying } : from;
        Type t = to.IsEnumValue && to.Symbol is { } enumTo ? new Type { Prim = enumTo.EnumUnderlying } : to;

        if (f.Prim == Prim.Bool)
            f = Type.I32;
        if (t.Prim == Prim.Bool)
            return v.Type == IrType.I32 ? v : Narrow(_e, v, f, Type.I32);

        if (!f.IsNumeric || !t.IsNumeric)
        {
            return v;
        }

        if (f.IsInteger && t.IsInteger)
        {
            return Narrow(_e, v, f, t);
        }

        if (f.IsFloat && t.IsFloat)
        {
            return _e.Unary(Opcode.FConv, R(v), IrTypes.Of(t));
        }

        if (f.IsInteger && t.IsFloat)
        {
            Opcode op = f.IsUnsigned ? Opcode.UToF : Opcode.IToF;
            return _e.Unary(op, R(v), IrTypes.Of(t));
        }

        if (f.IsFloat && t.IsInteger)
        {
            // Truncates toward zero, as C# specifies, whatever the FPU's
            // rounding mode says. To a 64-bit register for the wide types and
            // to 32 for the rest, then narrowed to canonical.
            bool wide = IsWideInteger(t);
            Opcode op = t.IsUnsigned ? Opcode.FToU : Opcode.FToI;
            VReg i = _e.Unary(op, R(v), wide ? IrType.I64 : IrType.I32);
            return wide ? i : Narrow(_e, i, t.IsUnsigned ? Type.U32 : Type.I32, t);
        }

        Error(at, $"cannot convert '{from}' to '{to}'");
        return v;
    }

    /// <summary>An integer of one width and signedness as another, canonical in the register.</summary>
    private static VReg Narrow(Builder e, VReg v, Type from, Type to)
    {
        bool fromWide = v.Type == IrType.I64;
        bool toWide = IsWideInteger(to);

        if (toWide)
        {
            if (fromWide)
                return v;
            return e.Unary(from.IsUnsigned ? Opcode.ZExt32 : Opcode.SExt32, v);
        }

        if (fromWide)
        {
            v = e.Unary(Opcode.Trunc64, v);
        }

        switch (to.Prim)
        {
            case Prim.I8: return e.Unary(Opcode.SExt8, v);
            case Prim.U8: return e.Unary(Opcode.ZExt8, v);
            case Prim.I16: return e.Unary(Opcode.SExt16, v);
            case Prim.U16 or Prim.Char: return e.Unary(Opcode.ZExt16, v);
            default: return v;
        }
    }

    /// <summary>An integer held in a 64-bit register: long, ulong, and nint and nuint in long mode.</summary>
    internal static bool IsWideInteger(Type t) =>
        t.Prim is Prim.I64 or Prim.U64 || (t.IsNative && Target.Current.WordSize == 8);

    /// <summary>The address to which a narrowing must return after arithmetic: the operand type's own width.</summary>
    private VReg Canonical(VReg v, Type t) => Narrow(_e, v, t, t);

    // ---- control-flow conditions -------------------------------------------------

    /// <summary>
    /// Branches on a condition without materialising it where a compare can
    /// feed the branch directly, and with the short-circuit operators as the
    /// control flow they are.
    /// </summary>
    private void BranchOn(Expr cond, Block ifTrue, Block ifFalse)
    {
        if (Fold.TryConst(cond, out long known))
        {
            _e.Jump(known != 0 ? ifTrue : ifFalse);
            return;
        }

        switch (cond)
        {
            case BinaryExpr { Op: BinOp.AndAlso } and:
            {
                Block mid = _f.NewBlock("and");
                BranchOn(and.Left, mid, ifFalse);
                _e.SetBlock(mid);
                BranchOn(and.Right, ifTrue, ifFalse);
                return;
            }
            case BinaryExpr { Op: BinOp.OrElse } or:
            {
                Block mid = _f.NewBlock("or");
                BranchOn(or.Left, ifTrue, mid);
                _e.SetBlock(mid);
                BranchOn(or.Right, ifTrue, ifFalse);
                return;
            }
            case UnaryExpr { Op: UnOp.Not } not:
                BranchOn(not.Operand, ifFalse, ifTrue);
                return;
        }

        VReg v = Eval(cond);
        _e.Branch(v, ifTrue, ifFalse);
    }

    // ---- the expressions --------------------------------------------------------------

    /// <summary>
    /// A const's value. A floating-point one is kept as a double's bits
    /// (Binder.RealBits), and made as a float or a double from them.
    /// </summary>
    private VReg ConstValue(ConstSym k)
    {
        if (k.Type.Prim == Prim.F32)
        {
            float single = (float)BitConverter.Int64BitsToDouble(k.Value);
            return _e.Unary(Opcode.Bits, R(_e.Const(BitConverter.SingleToInt32Bits(single), IrType.I32)), IrType.F32);
        }
        if (k.Type.Prim == Prim.F64)
            return _e.Unary(Opcode.Bits, R(_e.Const(k.Value, IrType.I64)), IrType.F64);
        return _e.Const(k.Value, IrTypes.Of(k.Type));
    }

    private VReg EvalCore(Expr e)
    {
        switch (e)
        {
            case LiteralExpr { Kind: Lit.Str } str:
                return _e.Address(InternString(str.Text));

            case LiteralExpr { Kind: Lit.Real } real:
            {
                Type t = _b.TypeOf(real);
                if (t.Prim == Prim.F32)
                {
                    VReg bits = _e.Const(BitConverter.SingleToInt32Bits((float)real.RealValue), IrType.I32);
                    return _e.Unary(Opcode.Bits, R(bits), IrType.F32);
                }
                VReg bits64 = _e.Const(BitConverter.DoubleToInt64Bits(real.RealValue), IrType.I64);
                return _e.Unary(Opcode.Bits, R(bits64), IrType.F64);
            }

            case LiteralExpr l:
            {
                Type t = _b.TypeOf(l);
                long value = l.Kind switch
                {
                    Lit.Int or Lit.Char or Lit.Bool => l.IntValue,
                    _ => 0,
                };
                return _e.Const(value, IrTypes.Of(t));
            }

            case NameExpr n:
                return LoadName(n);

            case AssignExpr a:
                return EmitAssign(a);

            case BinaryExpr b:
                return EmitBinary(b);

            case UnaryExpr u:
                return EmitUnary(u);

            case ConditionalExpr c:
            {
                Type result = _b.TypeOf(c);
                VReg dest = _f.NewReg(IrTypes.Of(result), "cond");
                Block then = _f.NewBlock("qthen");
                Block els = _f.NewBlock("qelse");
                Block end = _f.NewBlock("qend");
                BranchOn(c.Cond, then, els);
                _e.SetBlock(then);
                _e.CopyTo(dest, R(EvalAs(c.Then, result)));
                _e.Jump(end);
                _e.SetBlock(els);
                _e.CopyTo(dest, R(EvalAs(c.Else, result)));
                _e.Jump(end);
                _e.SetBlock(end);
                return dest;
            }

            case CallExpr call when _b.GetTypes.Contains(call) && call.Target is MemberExpr on:
            {
                // GetType(): the descriptor sits before the vtable the object points at.
                VReg obj = Eval(on.Target);
                VReg vt = _e.Load(IrTypes.Word, obj, 0);
                return _e.Binary(Opcode.Sub, vt, _t.DescriptorBytes);
            }

            // A CALL THAT RETURNS BY REFERENCE answers where the variable is;
            // as a value, it is what is there.
            case CallExpr call when RefCallee(call) is MethodSymbol answers:
                return LoadPlace(RefCallPlace(call, answers));

            case CallExpr call:
                return EmitCall(call);

            case AwaitExpr aw:
                return EmitAwait(aw);

            case CastExpr cast:
            {
                Type from = _b.TypeOf(cast.Operand);
                Type to = _b.TypeOf(cast);
                VReg v = Eval(cast.Operand);

                // `(int?)5` PUTS THE FIVE IN A CELL, and the checker marked the
                // OPERAND so that Eval did it just above. Converting again here
                // would put the cell in a cell.
                if (_b.Boxes.Contains(cast.Operand))
                {
                    return v;
                }

                // A DOWNCAST ASKS, from object as from any other reference:
                // `(Cat)o` over an object holding a Dog is InvalidCastException,
                // not a Dog read as a Cat. object is a word here, not IsReference,
                // and its casts went through unchecked.
                if ((from.IsReference || from.Prim == Prim.Any) && to.IsReference && to.Symbol is TypeSymbol wanted
                    && !(from.Symbol is not null && from.Symbol.DerivesFrom(wanted)))
                {
                    return CheckedCast(cast, v, wanted);
                }

                // `(byte[])o` ASKS, as any downcast does: C# throws
                // InvalidCastException for an object that is not a byte array,
                // and handing back the reference unchecked let a string be
                // indexed as bytes.
                if (to.IsArray && (from.Prim == Prim.Any || from.Symbol is { Kind: TypeKind.Interface }))
                {
                    return CheckedArrayCast(cast, v, to);
                }
                if (_checkedDepth > 0) CheckedNarrow(cast, v, from, to);
                return Convert(cast, v, from, to);
            }

            case ThisExpr:
                // In a closure's body: the instance it captured, from its field.
                if (_b.Resolved.TryGetValue(e, out Sym? captured) && captured is FieldSym { Field.Static: false } thisField && _this is not null)
                    return LoadPlace(PlaceOfField(thisField.Field, _this, e));
                return _this ?? Fail(e, "'this' outside an instance method");

            // 'base' names the same object as 'this'; only member lookup and
            // dispatch (viaBase in EmitCall) treat it differently. Without
            // this case every base.Member/base.Method()/base[i] fell to the
            // default arm below: a compile error plus a zero receiver.
            case BaseExpr:
                return _this ?? Fail(e, "'base' outside an instance method");

            case MemberExpr m:
                return EmitMember(m);

            case SuppressExpr sure:
                return Eval(sure.Operand);

            case NewExpr nw:
                return EmitNew(nw);

            case ThrowExpr th:
                EmitThrow(th.Value, th);
                return _e.Const(0, IrTypes.Of(_b.TypeOf(th)));

            case TupleExpr tup when _b.Tuples.TryGetValue(tup, out NewExpr? built):
                return EmitNew(built);

            case LambdaExpr lam when _b.Closures.TryGetValue(lam, out ClosureInfo? made):
                return EmitLambda(lam, made);

            case WithExpr copy when _b.TypeOf(copy).Symbol is TypeSymbol shape:
                return EmitWith(copy, shape);

            case SizeOfExpr so:
                return _e.Const(_b.SizeOfType(so), IrType.I32);

            // AN ENUM'S OR A STRUCT'S TYPE IS ITS BOX'S, as a primitive's is:
            // what GetType() on one reads is the box, and typeof has to be
            // that same descriptor for the two to compare equal.
            case TypeOfExpr { CanonSlot: >= 0 } canonType:
                return CanonEntry(canonType);

            case TypeOfExpr to when _b.TypeOfs.TryGetValue(to, out TypeSymbol? named):
                return _e.Address(named.Kind is TypeKind.Enum or TypeKind.Struct
                    ? BoxDescriptor(new Type { Symbol = named }) : DescriptorOf(named));

            case TypeOfExpr to when _b.PrimitiveTypeOfs.TryGetValue(to, out Prim prim):
                return _e.Address(PrimitiveDescriptor(prim));

            case TypeOfExpr to when _b.ArrayTypeOfs.TryGetValue(to, out Type? element):
                return _e.Address(SequenceDescriptor(ElementKey(element), ElementStride(element), isString: false, elementType: element));

            case TypeOfExpr:
                return _e.Const(0, IrTypes.Word);

            case DefaultExpr df when IsStructValue(_b.TypeOf(df)):
                return NewStruct(df, StructOf(_b.TypeOf(df)));

            case DefaultExpr df:
            {
                IrType t = IrTypes.Of(_b.TypeOf(df));
                if (t.IsFloat())
                {
                    VReg z = _e.Const(0, t == IrType.F32 ? IrType.I32 : IrType.I64);
                    return _e.Unary(Opcode.Bits, R(z), t);
                }
                return _e.Const(0, t == IrType.Void ? IrTypes.Word : t);
            }

            case RefArgExpr ra:
                return AddressOf(ra, ra.Target);

            case SwitchExpr sx:
                return EmitSwitchExpr(sx);

            case PatternExpr pat when _b.PatternSubject.TryGetValue(pat, out int held):
            {
                VReg subject = Eval(pat.Subject);
                _e.CopyTo(SlotReg(held, IrTypes.Of(_b.TypeOf(pat.Subject))), R(subject));
                return Eval(pat.Test);
            }

            // A STATEMENT, THEN A VALUE: a positional pattern taking its subject
            // apart before its elements are tested.
            case SequenceExpr seq:
                EmitStmt(seq.Effect);
                return Eval(seq.Value);

            case SubjectExpr subject when _b.Resolved.TryGetValue(subject, out Sym? where):
            {
                Place? p = PlaceOfSym(where, subject);
                return p is null ? _e.Const(0, IrType.I32) : LoadPlace(p);
            }

            case IsExpr isx:
                return EmitIs(isx);

            case AsExpr { CanonSlot: >= 0 } canonAs when HeldByReference(_b.TypeOf(canonAs.Operand)):
            {
                VReg v = Eval(canonAs.Operand);
                VReg test = DescribedTest(v, CanonEntry(canonAs));
                VReg result = _f.NewReg(IrTypes.Word, "ascanon");
                Block yes = _f.NewBlock("ascyes");
                Block end = _f.NewBlock("ascend");
                _e.CopyTo(result, Imm(0, IrTypes.Word));
                _e.Branch(test, yes, end);
                _e.SetBlock(yes);
                _e.CopyTo(result, R(v));
                _e.Jump(end);
                _e.SetBlock(end);
                return result;
            }

            case AsExpr asx:
            {
                VReg v = Eval(asx.Operand);
                if (_b.StringTests.Contains(asx))
                {
                    return AsStringValue(v, _b.TypeOf(asx.Operand));
                }
                if (_b.TestedArrays.TryGetValue(asx, out Type? asArray))
                {
                    return AsArray(v, asArray);
                }
                // Any object is an object.
                if (_b.TypeOf(asx) is { Prim: Prim.Any, Symbol: null } && HeldByReference(_b.TypeOf(asx.Operand)))
                {
                    return v;
                }
                TypeSymbol? want = _b.TestedTypes.TryGetValue(asx, out TypeSymbol? resolved) ? resolved
                                 : _b.Types.TryGetValue(asx.Type.Name, out TypeSymbol? found) ? found : null;
                if (want is null)
                {
                    Error(asx, "this type cannot be tested at run time");
                    return v;
                }
                return AsType(v, want);
            }

            case IndexExpr ix when _b.Indexers.TryGetValue(ix, out MethodSymbol? getter):
                return EmitInstanceCall(getter, ix.Target, ix.Args);

            case IndexExpr ix:
            {
                Type seq = _b.TypeOf(ix.Target);
                Type element = _b.TypeOf(ix);
                VReg basis = Eval(ix.Target);
                VReg index = EvalAs(ix.Args[0], Type.I32);
                return LoadPlace(ElementPlace(basis, index, seq, element, ix));
            }

            default:
                Error(e, $"{e.GetType().Name} is not implemented by the lowering yet");
                return _e.Const(0, IrTypes.Word);
        }
    }

    private VReg Fail(Node at, string message)
    {
        Error(at, message);
        return _e.Const(0, IrTypes.Word);
    }

    // ---- names and members --------------------------------------------------------------

    private VReg LoadName(NameExpr n)
    {
        if (!_b.Resolved.TryGetValue(n, out Sym? sym))
        {
            return Fail(n, $"'{n.Name}' did not resolve");
        }

        switch (sym)
        {
            case ConstSym k:
                return ConstValue(k);

            case PropertyGetSym pg:
                return CallAccessor(pg.Getter, self: true, target: null);

            case CapturedPropertyGetSym captured:
            {
                VReg env = _e.Load(IrTypes.Word, _this!, captured.Holder.Offset);
                return CallAccessor(captured.Getter, self: false, target: null, receiver: env);
            }

            default:
            {
                Place? p = PlaceOfSym(sym, n);
                return p is null ? _e.Const(0, IrTypes.Word) : LoadPlace(p);
            }
        }
    }

    private VReg EmitMember(MemberExpr m)
    {
        if (m.NullConditional)
        {
            return EmitConditionalMember(m);
        }

        Type target = _b.TypeOf(m.Target);

        // FURTHER ALONG A `?.` CHAIN IS STILL IN IT. `d?.TypeParams.Count` is
        // an `int?` to the checker, and nothing when `d` was nothing: read as
        // an ordinary member it called the getter on whatever `d?.TypeParams`
        // came to and handed back a plain number where a cell was promised --
        // and `!=` then read the count as though it were an address.
        if (InConditionalChain(m.Target) && !target.IsNullableValue && target.IsReference
            && ((_b.Resolved.TryGetValue(m, out Sym? chained)
                 && chained is FieldSym { Field.Static: false } or PropertyGetSym { Getter.Static: false })
                || IsIntrinsicMember(m, target)))
        {
            return EmitConditionalMember(m);
        }

        // The two members of Nullable<T>: is there a cell, and what is in it.
        if (target.IsNullableValue)
        {
            VReg cell = Eval(m.Target);
            if (m.Name == "HasValue")
            {
                return HasValue(cell);
            }
            return NullableValue(cell, target.Underlying);
        }

        if (_b.Resolved.TryGetValue(m, out Sym? sym))
        {
            switch (sym)
            {
                case PropertyGetSym pg:
                    return CallAccessor(pg.Getter, self: false, target: m.Target);
                case ConstSym k:
                    return ConstValue(k);
                case FieldSym f when f.Field.Static:
                    return LoadPlace(PlaceOfField(f.Field, null, m));
                case FieldSym f:
                    return LoadPlace(PlaceOfField(f.Field, Eval(m.Target), m));
            }
        }

        if (IsIntrinsicMember(m, target)) return IntrinsicMember(m, target, Eval(m.Target));

        return Fail(m, $"'{m.Name}' cannot be read here yet");
    }

    /// <summary>A member the compiler answers itself: an array's or a string's Length, a type's Name and FullName.</summary>
    private static bool IsIntrinsicMember(MemberExpr m, Type target)
        => (m.Name == "Length" && (target.IsArray || target.Prim == Prim.String))
        || (m.Name == "LongLength" && target.IsArray)
        || (m.Name is "Name" or "FullName" or "IsValueType" or "IsEnum" or "IsInterface" or "IsPrimitive" or "IsArray" or "IsClass" && target.Prim == Prim.Type);

    /// <summary>
    /// Such a member of the value `obj` holds.
    ///
    /// A TYPE'S NAME AND FULL NAME: the descriptor holds the name ToString
    /// answers -- `System.Object`, `System.Int32` -- and Name is its last
    /// part, as .NET's Type.Name is, where the library can say so.
    /// </summary>
    private VReg IntrinsicMember(MemberExpr m, Type target, VReg obj)
    {
        // WHAT KIND OF TYPE, as System.Type answers it, from the flags its
        // descriptor carries: a sequence is an array (and a string, a class),
        // a box's descriptor a value type's, an interface's its own.
        if (target.Prim == Prim.Type && m.Name is "IsValueType" or "IsEnum" or "IsInterface" or "IsPrimitive" or "IsArray" or "IsClass")
        {
            VReg flags = _e.Load(IrType.I32, obj, DescFlags * _t.WordSize, 4, false);
            int bits = m.Name switch
            {
                "IsValueType" => TypeFlagValue, "IsEnum" => TypeFlagEnum, "IsInterface" => TypeFlagInterface,
                "IsPrimitive" => TypeFlagPrimitive, "IsArray" => 1, _ => TypeFlagValue | TypeFlagInterface,
            };
            VReg masked = m.Name == "IsArray"
                ? _e.Binary(Opcode.Eq, R(_e.Binary(Opcode.And, flags, 3)), Imm(1, IrType.I32), IrType.I32)
                : _e.Binary(Opcode.Ne, R(_e.Binary(Opcode.And, flags, bits)), Imm(0, IrType.I32), IrType.I32);
            return m.Name == "IsClass" ? _e.Binary(Opcode.Eq, R(masked), Imm(0, IrType.I32), IrType.I32) : masked;
        }
        if (m.Name == "Length")
            return target.IsArray ? _e.Unary(Opcode.ArrayLength, R(obj), IrType.I32) : _e.Load(IrType.I32, obj, _t.ArrayCountOffset);
        if (m.Name == "LongLength")
            return _e.Unary(Opcode.SExt32, R(_e.Unary(Opcode.ArrayLength, R(obj), IrType.I32)), IrType.I64);
        VReg full = _e.Load(IrTypes.Word, obj, DescName * _t.WordSize);
        if (m.Name == "FullName" || !HasStringMethod(Prelude.TypeNameMethod)) return full;
        MethodSymbol? simple = StringMethod(m, Prelude.TypeNameMethod, 1, "a type's name");
        return simple is null ? full : _e.Call(CallLabel(simple), IrTypes.Word, R(full))!;
    }

    /// <summary>
    /// What .NET calls a type, which is what object.ToString answers for one
    /// that declares no ToString of its own: `System.Byte`, not `byte`.
    /// </summary>
    private static string RuntimeName(Type t) => t.Prim switch
    {
        Prim.Bool => "System.Boolean",
        Prim.I8 => "System.SByte",   Prim.U8 => "System.Byte",
        Prim.I16 => "System.Int16",  Prim.U16 => "System.UInt16",
        Prim.I32 => "System.Int32",  Prim.U32 => "System.UInt32",
        Prim.I64 => "System.Int64",  Prim.U64 => "System.UInt64",
        Prim.F32 => "System.Single", Prim.F64 => "System.Double",
        Prim.Char => "System.Char",  Prim.String => "System.String",
        Prim.Any => "System.Object",
        Prim.NInt => "System.IntPtr", Prim.NUInt => "System.UIntPtr",
        Prim.Void => "System.Void",
        _ => t.Symbol?.Key ?? t.ToString(),
    };

    /// <summary>`x?.Member`: the member when x is something, and null when it is not.</summary>
    // THROUGH CALLS AND INDEXERS TOO, as the checker's HasConditionalMember
    // walks it: `n?.Self().Label` is still in n's chain after the call, and
    // read plainly it asked Label of the null the call came to.
    private static bool InConditionalChain(Expr e) => e switch
    {
        MemberExpr member => member.NullConditional || InConditionalChain(member.Target),
        CallExpr call => InConditionalChain(call.Target),
        IndexExpr index => index.NullConditional || InConditionalChain(index.Target),
        _ => false,
    };

    private VReg EmitConditionalMember(MemberExpr m)
    {
        Type result = _b.TypeOf(m);
        VReg dest = _f.NewReg(IrTypes.Of(result), "qm");
        VReg obj = Eval(m.Target);
        Block some = _f.NewBlock("qmsome");
        Block end = _f.NewBlock("qmend");

        // An empty Nullable when the answer is one: what `?.` gives for null.
        // A ZEROED SLOT OF THIS FRAME, not a block of the heap: the answer is
        // read, never written through -- the other arm is the member's own
        // cell inside the object -- so a cell nobody else holds is all the
        // null arm needs, and a heap one joined with an object's interior
        // was a block per `x?.Flag == false` that nothing could free. In an
        // iterator's or async body, whose frame a suspension ends, the heap.
        if (result.IsNullableValue && _stateMachine is null)
        {
            TypeSymbol shape = StructOf(result);
            int size = Math.Max(4, shape.InstanceSize);
            FrameSlot slot = _f.NewSlot((size + 7) & ~7, Math.Min(_t.Align64, Math.Max(4, shape.InlineAlign)), "noval");
            VReg cell = RegOf(new SlotOperand(slot));
            _e.Emit(Opcode.MemSet, null, R(cell), new ImmOperand(0, IrType.I32), new ImmOperand(size, IrTypes.Word));
            _e.CopyTo(dest, R(cell));
        }
        else
        {
            _e.CopyTo(dest, result.IsNullableValue ? R(NullableEmpty(m, result)) : Imm(0, dest.Type));
        }
        _e.Branch(obj, some, end);
        _e.SetBlock(some);

        // A PROPERTY IS AS ORDINARY A MEMBER AS A FIELD, and `?.` reads either.
        // The receiver has already been evaluated and tested, so the getter is
        // called with it rather than with the expression -- which would
        // evaluate the left-hand side a second time, and C# evaluates it once.
        _b.Resolved.TryGetValue(m, out Sym? sym);

        (VReg? value, Type held) = sym switch
        {
            FieldSym { Field.Static: false } f
                => (LoadPlace(PlaceOfField(f.Field, obj, m)), f.Field.Type),
            PropertyGetSym { Getter.Static: false } p
                => (CallAccessor(p.Getter, self: false, target: null, receiver: obj), p.Getter.Returns),
            _ when IsIntrinsicMember(m, _b.TypeOf(m.Target))
                => (IntrinsicMember(m, _b.TypeOf(m.Target), obj), m.Name switch { "Length" => Type.I32, "LongLength" => Type.I64, _ => Type.String }),
            _ => ((VReg?)null, result),
        };

        if (value is null)
        {
            Error(m, $"'?.' cannot read '{m.Name}' here yet");
            _e.Jump(end);
            _e.SetBlock(end);
            return dest;
        }

        if (result.IsNullableValue && !held.IsNullableValue)
        {
            value = Box(m, value, held);
        }
        _e.CopyTo(dest, R(value));
        _e.Jump(end);
        _e.SetBlock(end);
        return dest;
    }

    // ---- allocation, boxing, objects ------------------------------------------------------

    /// <summary>
    /// Memory for an object, from the runtime's allocator. Zeroed by the
    /// allocator: every field of a new object is its default, which the
    /// language promises and the code generator relies on.
    /// </summary>
    private VReg Allocate(Node at, long bytes, bool described = false)
    {
        return AllocateDynamic(at, _e.Const(bytes, IrTypes.Word), described: described);
    }

    private VReg AllocateDynamic(Node at, VReg bytes, bool leaf = false, bool described = false)
    {
        // Memory that can hold no reference -- a string, an array of bytes,
        // characters or floating-point numbers -- comes from AllocLeaf where
        // the runtime has it: the collector marks it and never scans it.
        //
        // AN OBJECT WHOSE VTABLE IS STORED NEXT comes from AllocObject: the
        // collector finds its references through the descriptor that vtable
        // names, not by trying every word (Gc.KindObject). Only where the
        // vtable really is the first thing written -- a class instance, a box,
        // a closure, an array -- and never a struct's block, a cell or a
        // coroutine's frame, whose words no descriptor describes.
        MethodSymbol? alloc = (leaf ? RuntimeMethod("AllocLeaf", 1) : null)
                           ?? (described ? RuntimeMethod("AllocObject", 1) : null)
                           ?? RuntimeMethod("Alloc", 1);
        if (alloc is null)
        {
            Error(at, $"allocation needs {RuntimeType}.Alloc, which no compiled source provides; compile with the system library");
            return _e.Const(0, IrTypes.Word);
        }
        Require(alloc);
        VReg arg = Convert(at, bytes, IrTypes.Word == IrType.I64 ? Type.I64 : Type.I32, alloc.Params[0].Type);
        VReg r = _e.Call(CallLabel(alloc), IrTypes.Of(alloc.Returns), R(arg))!;
        return r.Type == IrTypes.Word ? r : _e.Unary(Opcode.Trunc64, r);
    }

    /// <summary>
    /// What a Nullable&lt;T&gt; holds: its value, or, when it has none,
    /// InvalidOperationException as .NET's Value throws it.
    /// </summary>
    private VReg NullableValue(VReg cell, Type inner)
    {
        Block some = _f.NewBlock("nvsome"), none = _f.NewBlock("nvnone");
        _e.Branch(HasValue(cell), some, none);
        _e.SetBlock(none);
        if (RuntimeMethod("NoValue", 0) is MethodSymbol fail)
        {
            Require(fail);
            _e.Call(CallLabel(fail), IrType.Void);
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
        _e.SetBlock(some);
        return NullableRead(cell, inner.AsNullable());
    }

    /// <summary>The struct a value of this type is (BindResult.StructOf): a nullable value's Nullable&lt;T&gt; shape too.</summary>
    private TypeSymbol StructOf(Type t) => _b.StructOf(t);

    /// <summary>Whether a Nullable&lt;T&gt; has a value: its first byte, hasValue.</summary>
    private VReg HasValue(VReg nullable) => _e.Load(IrType.I32, nullable, 0, 1, false);

    /// <summary>Where a Nullable&lt;T&gt;'s value is, after hasValue at T's alignment.</summary>
    private MemPlace NullableValuePlace(VReg nullable, Type type)
    {
        FieldSymbol value = StructOf(type.IsNullableValue ? type : type.AsNullable()).Fields[1];
        return new MemPlace(R(nullable), value.Offset, value.Type, Inline: value.Inline);
    }

    /// <summary>A Nullable&lt;T&gt;'s value, read without asking whether it has one.</summary>
    private VReg NullableRead(VReg nullable, Type type) => LoadPlace(NullableValuePlace(nullable, type));

    /// <summary>A Nullable&lt;T&gt; with no value: all of it zero, as default(T?) is.</summary>
    private VReg NullableEmpty(Node at, Type type) => NewStruct(at, StructOf(type.IsNullableValue ? type : type.AsNullable()));

    /// <summary>
    /// A value made a Nullable&lt;T&gt; holding it (C# 10.2.6): hasValue set and
    /// the value after it, a struct like any other -- held in line where it
    /// is held, and on the stack where it goes no further.
    /// </summary>
    private VReg Box(Node at, VReg value, Type held)
    {
        Type nullable = held.IsNullableValue ? held : held.AsNullable();
        // A reference's `?` is an annotation: the reference is the value.
        if (!nullable.IsNullableValue)
        {
            return value;
        }
        TypeSymbol shape = StructOf(nullable);
        VReg made = NewStruct(at, shape);
        _e.Store(R(made), Imm(1, IrType.I32), 0, 1);
        FieldSymbol slot = shape.Fields[1];
        Type inner = nullable.Underlying;
        if (slot.Inline)
        {
            StoreNewElement(made, slot.Offset, inner, value);
        }
        else
        {
            if (IsStructValue(inner)) value = CopyStruct(at, value, StructOf(inner));
            StoreNew(made, value, slot.Offset, inner);
        }
        return made;
    }

    private VReg EmitNew(NewExpr nw)
    {
        Type type = _b.TypeOf(nw);
        TypeSymbol? sym = type.Symbol;

        if (nw.Utf8Bytes is { } utf8)
        {
            return _e.Address(Utf8Data(utf8));
        }

        if (nw.Elements is { } written)
        {
            Type element = type.Element ?? Type.I32;
            VReg count = _e.Const(written.Count, IrType.I32);
            VReg array = AllocateArray(nw, count, element, nw.CanonSlot >= 0 ? CanonEntry(nw) : null);
            int stride = ElementStride(element);
            for (int i = 0; i < written.Count; i++)
            {
                VReg value = InlineElement(element) ? InlineValue(new MemPlace(R(array), 0, element, Inline: true), written[i], element) : EvalAs(written[i], element);
                StoreNewElement(array, _t.ArrayHeaderBytes + (long)i * stride, element, value);
            }
            return array;
        }

        if (nw.ArraySize is not null)
        {
            Type element = type.Element ?? Type.I32;
            VReg count = EvalAs(nw.ArraySize, Type.I32);
            return AllocateArray(nw, count, element, nw.CanonSlot >= 0 ? CanonEntry(nw) : null);
        }

        // `new object()`: a header and nothing else, the thing to lock on.
        if (sym is null && type.Prim == Prim.Any && nw.Args.Count == 0)
        {
            VReg bare = Allocate(nw, _t.ObjectHeaderBytes, described: true);
            _e.Store(R(bare), new SymOperand(ObjectDescriptor(), _t.DescriptorBytes), 0, _t.WordSize);
            return bare;
        }

        if (sym is null)
        {
            return Fail(nw, $"'{type}' cannot be allocated");
        }

        // MAKING ONE TOUCHES THE TYPE. Before the allocation, because the
        // initialiser may be what fills in whatever the constructor reads.
        // AND EVERY TYPE IT DERIVES FROM: their own methods read their
        // statics untested (TouchType, from inside the type), on the ground
        // that making one touched the type -- and a MethodDecl made is a
        // MemberDecl nobody made, whose shared empty list read null.
        for (TypeSymbol? made = sym; made is not null; made = made.Base) TouchType(made);

        int size = Math.Max(sym.Kind == TypeKind.Class ? _t.ObjectHeaderBytes : 1, sym.InstanceSize);
        VReg obj = Allocate(nw, size, described: sym.Kind == TypeKind.Class);

        if (sym.Kind == TypeKind.Class && nw.CanonSlot >= 0)
        {
            // Judged as the shared class it is in every way but its type (Instr.StoredValue).
            _e.StoreDescribed(obj, VtableOf(sym), CanonMadeVtable(nw, sym), _t.WordSize);
        }
        else if (sym.Kind == TypeKind.Class)
        {
            _e.Store(R(obj), VtableOf(sym), 0, _t.WordSize);
        }

        // ITS STRUCT FIELDS ARE ZERO VALUES, not null pointers, before the
        // constructor or the initialiser runs: `new XformMatrix { M11 = one }`
        // left M12 null and Clone read address 0 copying it.
        InitStructFields(nw, obj, sym);

        MethodSymbol? ctor = _b.NewConstructors.TryGetValue(nw, out MethodSymbol? selected)
                           ? selected
                           : sym.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == nw.Args.Count);

        // A class that declares no constructor has C#'s implicit one, which
        // chains to the base's parameterless constructor -- so a Puppy with
        // no constructor still runs Dog's, which runs Animal's.
        if (ctor is null && nw.Args.Count == 0)
        {
            ctor = ImplicitConstructor(sym);
        }

        if (ctor is not null)
        {
            List<Operand> args = new() { R(obj) };
            if (nw.ArgumentOrder.Count != 0)
            {
                Operand[] prepared = new Operand[nw.Args.Count];
                foreach (int index in nw.ArgumentOrder)
                    prepared[index] = R(EvalAs(nw.Args[index], ctor.Params[index]));
                args.AddRange(prepared);
            }
            else
                for (int i = 0; i < nw.Args.Count; i++)
                    args.Add(R(EvalAs(nw.Args[i], ctor.Params[i])));
            CallDirect(ctor, IrType.Void, args);
        }

        EmitInitialiser(nw, obj);
        return obj;
    }

    /// <summary>
    /// The parameterless constructor a class without one inherits: the first
    /// declared parameterless constructor up the base chain, which is what
    /// each implicit constructor in between would have called.
    /// </summary>
    private static MethodSymbol? ImplicitConstructor(TypeSymbol sym)
    {
        for (TypeSymbol? t = sym.Base; t is not null; t = t.Base)
        {
            MethodSymbol? found = t.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == 0);
            if (found is not null)
            {
                return found;
            }
            if (t.Methods.Any(c => c.IsCtor))
            {
                return null;    // only parameterised constructors: the binder would have refused
            }
        }
        return null;
    }

    /// <summary>An array: header, count, and count times the stride, zeroed.</summary>
    /// <summary>
    /// A new array of `element`, described as such -- or by `described`, the
    /// descriptor a shared copy read for its `T[]` (CanonEntry), which says
    /// what the instantiation's element is where `element` is the machine word.
    /// </summary>
    private VReg AllocateArray(Node at, VReg count, Type element, VReg? described = null)
    {
        int stride = ElementStride(element);
        CheckArrayCount(count, stride);
        VReg bytes = stride == 1 ? count : _e.Binary(Opcode.Mul, count, stride);
        VReg total = _e.Binary(Opcode.Add, WordOf(bytes), _t.ArrayHeaderBytes);
        bool inline = InlineElement(element);
        bool references = inline && InlineHasReferences(StructOf(element));
        VReg array = AllocateDynamic(at, total, inline ? !references : LeafElement(element), described: true);
        string desc = SequenceDescriptor(ElementKey(element), stride, isString: false, inline ? references : null, elementType: element);
        if (described is not null)
        {
            // Judged as the machine word's array, which it is in every way
            // but the element type it reports (Instr.StoredValue).
            _e.StoreDescribed(array, new SymOperand(desc, _t.DescriptorBytes), _e.Binary(Opcode.Add, described, _t.DescriptorBytes), _t.WordSize);
        }
        else
        {
            _e.Store(R(array), new SymOperand(desc, _t.DescriptorBytes), 0, _t.WordSize);
        }
        _e.Emit(Opcode.InitArrayLength, null, R(array), R(count));

        // Held in line, every element is zero already: its default.
        if (inline)
        {
            return array;
        }

        // AN ARRAY OF STRUCTS HOLDS VALUES, each zero until written: `arr[1].X
        // = 5` on a fresh array is legal C#. With structs as blocks that means
        // one block per element, allocated here. Inline layout would make
        // this one allocation; that is the later design, and this is what
        // keeps the semantics right until then.
        if (IsStructValue(element))
        {
            int size = Math.Max(1, StructOf(element).InstanceSize);
            VReg index = _f.NewReg(IrType.I32, "ei");
            _e.CopyTo(index, Imm(0, IrType.I32));
            Block top = _f.NewBlock("eloop");
            Block body = _f.NewBlock("ebody");
            Block done = _f.NewBlock("edone");
            _e.Jump(top);
            _e.SetBlock(top);
            _e.Branch(_e.Binary(Opcode.LtS, index, count), body, done);
            _e.SetBlock(body);
            VReg block = NewStruct(at, StructOf(element));
            VReg slot = _e.Binary(Opcode.Add, array, WordOf(_e.Binary(Opcode.Mul, index, stride)));
            StoreNewReference(slot, block, _t.ArrayHeaderBytes);
            _e.CopyTo(index, R(_e.Binary(Opcode.Add, index, 1)));
            _e.Jump(top);
            _e.SetBlock(done);
        }
        return array;
    }

    /// <summary>
    /// THE COUNT OF A NEW ARRAY, checked before its size is worked out: at
    /// most as many elements as fit the largest allocation, compared unsigned
    /// so that a negative count fails too. Unchecked, `new byte[-1]` asked for
    /// fifteen bytes and got a length word every index passed, and a count
    /// whose size wrapped 32 bits got a 16-byte block the same way -- and an
    /// array of structs then had its element pointers written past the end.
    /// A constant count in range folds the test away.
    /// </summary>
    private void CheckArrayCount(VReg count, int stride)
    {
        long limit = (0x7FFFFFF0L - _t.ArrayHeaderBytes) / stride;
        Block ok = _f.NewBlock("acount");
        Block bad = _f.NewBlock("abad");
        _e.Branch(_e.Binary(Opcode.LeU, count, (int)limit), ok, bad);
        _e.SetBlock(bad);
        MethodSymbol? fail = RuntimeMethod("ArraySize", 1);
        if (fail is not null)
        {
            Require(fail);
            _e.Call(CallLabel(fail), IrType.Void, R(count));
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
        _e.SetBlock(ok);
    }

    /// <summary>
    /// Whether an array of these holds nothing the collector need follow:
    /// elements too narrow to be an address, or floating point. Arrays of
    /// int, long and the pointer-sized types stay scanned -- this code keeps
    /// addresses in them -- as do structs (each element is a block), every
    /// reference type, and every NULLABLE one: a `bool?` or `double?` element
    /// is the address of its cell, with the Prim of the value inside.
    /// </summary>
    private static bool LeafElement(Type element) => !element.IsNullableValue && !element.IsReference && !element.IsArray
        && !element.IsPointer && element.Symbol is null
        && element.Prim is Prim.U8 or Prim.I8 or Prim.Bool or Prim.Char or Prim.I16 or Prim.U16 or Prim.F32 or Prim.F64;

    /// <summary>The `{ A = 1, B = 2 }` after a constructor, and collection initialisers.</summary>
    private void EmitInitialiser(NewExpr nw, VReg obj) => EmitInitBody(nw.Body, obj);

    private void EmitInitBody(InitBody body, VReg obj)
    {
        foreach (InitAdd add in body.Adds)
        {
            if (!_b.InitAdder.TryGetValue(add, out MethodSymbol? adder))
            {
                continue;
            }
            List<Operand> args = new() { R(obj) };
            for (int i = 0; i < add.Args.Count; i++)
            {
                args.Add(R(EvalAs(add.Args[i], adder.Params[i])));
            }
            CallMethod(adder, obj, args);
        }

        foreach (InitIndex one in body.Indexes)
        {
            if (!_b.InitIndexer.TryGetValue(one, out MethodSymbol? setter))
            {
                continue;
            }
            List<Operand> args = new() { R(obj) };
            for (int i = 0; i < one.Args.Count; i++)
            {
                args.Add(R(EvalAs(one.Args[i], setter.Params[i])));
            }
            ParamSymbol vp = setter.Params[one.Args.Count];
            args.Add(R(EvalAs(one.Value, vp)));
            CallMethod(setter, obj, args);
        }

        foreach (InitAssign init in body.Inits)
        {
            // `Name = { … }` reads the member and fills in what it finds there,
            // so nothing is assigned: the nested elements run against that.
            if (init.Nested is InitBody nested)
            {
                VReg inner;

                if (_b.InitField.TryGetValue(init, out FieldSymbol? held))
                {
                    inner = LoadPlace(PlaceOfField(held, obj, init));
                }
                else if (_b.InitGetter.TryGetValue(init, out MethodSymbol? getter))
                {
                    VReg? got = CallMethod(getter, obj, new List<Operand> { R(obj) });

                    if (got is null)
                    {
                        continue;
                    }
                    inner = got;
                }
                else
                {
                    continue;
                }

                EmitInitBody(nested, inner);
                continue;
            }

            if (_b.InitField.TryGetValue(init, out FieldSymbol? field))
            {
                Place fieldPlace = PlaceOfField(field, obj, init);
                StorePlace(fieldPlace, InlineValue(fieldPlace, init.Value!, field.Type));
                continue;
            }

            if (!_b.InitSetter.TryGetValue(init, out MethodSymbol? setter))
            {
                continue;
            }
            ParamSymbol p = setter.Params[0];
            VReg v = EvalAs(init.Value!, p);
            CallMethod(setter, obj, new List<Operand> { R(obj), R(v) });
        }
    }

    /// <summary>A lambda is an object made where it was written, holding what it captured.</summary>
    private VReg EmitLambda(LambdaExpr lam, ClosureInfo made)
    {
        VReg obj = Allocate(lam, Math.Max(_t.ObjectHeaderBytes, made.Type.InstanceSize), described: true);
        _e.Store(R(obj), VtableOf(made.Type), 0, _t.WordSize);

        foreach ((FieldSymbol f, Sym from) in made.Captures)
        {
            VReg value;

            // A boxed local hands over its CELL, not the value in it: sharing
            // the address is the whole of capture by reference.
            //
            // AND ONE WITHOUT A DECLARATION HANDS OVER THE CELL TOO. A
            // pattern's binding, an `out var`, a foreach cursor: no statement
            // declared them, so their cell was made on entry and is held here
            // rather than in a local's register. Handing over the VALUE instead
            // left the closure with a field marked as a cell and a number in
            // it, and the lambda then read whatever that number addressed.
            // A VALUE TAKEN NOW: a method group's receiver, evaluated where
            // the delegate is made (Binder.MethodGroupLambda).
            if (from is ValueSym taken)
            {
                value = Eval(taken.Value);
            }
            else if (from is LocalSym { Boxed: true } cell && DeclOf(cell) is LocalDecl d)
            {
                value = LocalReg(d);
            }
            else if (from is LocalSym { Boxed: true } made2 && DeclOf(made2) is null
                     && _symCells.TryGetValue(made2, out VReg? held) && held is not null)
            {
                value = held;
            }
            else if (from is FieldSym { Field.Boxed: true } cellField)
            {
                value = _e.Load(IrTypes.Word, _this!, cellField.Field.Offset);
            }
            else if (from is ParamSym { Boxed: true } parameter && _paramCells.TryGetValue(parameter.Index, out VReg? parameterCell))
            {
                value = parameterCell;
            }
            else
            {
                Place? p = PlaceOfSym(from, lam);
                if (p is null)
                {
                    continue;
                }
                value = LoadPlace(p);
            }

            if (f.Boxed)
            {
                StoreNewReference(obj, value, f.Offset);
            }
            else
            {
                StoreNew(obj, value, f.Offset, f.Type, f);
            }
        }

        return obj;
    }

    /// <summary>`x with { A = 1 }`: allocate, copy every field, then override.</summary>
    private VReg EmitWith(WithExpr copy, TypeSymbol shape)
    {
        VReg src = Eval(copy.Source);
        VReg obj = Allocate(copy, Math.Max(_t.ObjectHeaderBytes, shape.InstanceSize), described: shape.Kind == TypeKind.Class);
        if (shape.Kind == TypeKind.Class)
        {
            _e.Store(R(obj), VtableOf(shape), 0, _t.WordSize);
        }

        for (TypeSymbol? walk = shape; walk is not null; walk = walk.Base)
        {
            foreach (FieldSymbol f in walk.Fields.Where(f => !f.Static))
            {
                // One held in line is its bytes, copied as they are.
                if (f.Inline)
                {
                    VReg from = f.Offset == 0 ? src : _e.Binary(Opcode.Add, src, f.Offset);
                    VReg to = f.Offset == 0 ? obj : _e.Binary(Opcode.Add, obj, f.Offset);
                    _e.Emit(Opcode.MemCopy, null, R(to), R(from), Imm(Math.Max(1, StructOf(f.Type).InstanceSize), IrTypes.Word));
                    continue;
                }
                VReg v = LoadPlace(new MemPlace(R(src), f.Offset, f.Type));
                // A struct field is copied, not shared with the source.
                if (!f.Boxed && IsStructValue(f.Type))
                {
                    v = CopyStruct(copy, v, StructOf(f.Type));
                }
                StoreNew(obj, v, f.Offset, f.Type, f);
            }
        }

        foreach (InitAssign init in copy.Inits)
        {
            if (init.Value is Expr given && _b.InitField.TryGetValue(init, out FieldSymbol? field))
            {
                VReg v = EvalAs(given, field.Type);
                if (field.Inline) StorePlace(new MemPlace(R(obj), field.Offset, field.Type, false, true), v);
                else
                {
                    _e.Store(R(obj), R(v), field.Offset, LoadSize(field.Type));
                    // Tagged as every other store to the field is: the field
                    // proof must see what a `with` puts there.
                    if (TagsField(field)) _e.Block.Instrs[^1].Field = FieldKey(field);
                }
            }
        }
        return obj;
    }

    // ---- type tests ------------------------------------------------------------------

    /// <summary>
    /// Whether an object is of a type. For a class: the object's descriptor
    /// display, indexed by the wanted type's depth, names the wanted type.
    /// For an interface: the object's interface array contains it. Null is
    /// never anything.
    /// </summary>
    /// <summary>
    /// Whether a reference is a string, read from the string bit in its
    /// descriptor -- the same bit Sys.IsString reads.
    ///
    /// String has no TypeSymbol to compare a descriptor against, so a type test
    /// against it cannot go through TypeTest; the bit is what the runtime has
    /// instead, and it is exact.
    /// </summary>
    private VReg StringTest(VReg obj, Type operand)
    {
        // A value type is never a string, and a load through it would read a
        // number as a pointer: decided here, from the static type.
        if (!CouldHoldReference(operand))
        {
            return _e.Const(0, IrType.I32);
        }

        VReg result = _f.NewReg(IrType.I32, "isstr");
        Block some = _f.NewBlock("strsome");
        Block end = _f.NewBlock("strend");
        _e.CopyTo(result, Imm(0, IrType.I32));
        _e.Branch(obj, some, end);
        _e.SetBlock(some);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg flags = _e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);
        VReg bit = _e.Binary(Opcode.And, flags, 2);
        _e.CopyTo(result, R(_e.Binary(Opcode.Ne, R(bit), Imm(0, IrType.I32), IrType.I32)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>The same reference when it is a string, null when it is not.</summary>
    private VReg AsStringValue(VReg obj, Type operand)
    {
        VReg test = StringTest(obj, operand);
        VReg result = _f.NewReg(IrTypes.Word, "asstr");
        Block yes = _f.NewBlock("asstryes");
        Block no = _f.NewBlock("asstrno");
        Block end = _f.NewBlock("asstrend");
        _e.Branch(test, yes, no);
        _e.SetBlock(yes);
        _e.CopyTo(result, R(obj));
        _e.Jump(end);
        _e.SetBlock(no);
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>Whether a value of this static type can carry an object header at all.</summary>
    private static bool CouldHoldReference(Type t)
    {
        return t.Prim is Prim.String or Prim.Any or Prim.NullLiteral
            || t.ParamName is not null
            || t.Symbol is { Kind: TypeKind.Class or TypeKind.Interface }
            || t.IsArray;
    }

    private VReg TypeTest(VReg obj, TypeSymbol want)
    {
        VReg result = _f.NewReg(IrType.I32, "is");
        Block notNull = _f.NewBlock("tnn");
        Block yes = _f.NewBlock("tyes");
        Block no = _f.NewBlock("tno");
        Block end = _f.NewBlock("tend");
        int w = _t.WordSize;

        _e.Branch(obj, notNull, no);
        _e.SetBlock(notNull);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg desc = _e.Binary(Opcode.Sub, vt, _t.DescriptorBytes);

        if (want.Kind == TypeKind.Interface)
        {
            // Scan the zero-terminated interface array.
            VReg wanted = _e.Address(InterfaceDescriptor(want));
            VReg cursor = _f.NewReg(IrTypes.Word, "ifc");
            _e.CopyTo(cursor, R(_e.Load(IrTypes.Word, desc, DescInterfaces * w)));
            Block loop = _f.NewBlock("tscan");
            Block more = _f.NewBlock("tmore");
            _e.Jump(loop);
            _e.SetBlock(loop);
            VReg entry = _e.Load(IrTypes.Word, cursor, 0);
            _e.Branch(entry, more, no);
            _e.SetBlock(more);
            Block advance = _f.NewBlock("tnext");
            _e.Branch(_e.Binary(Opcode.Eq, entry, wanted), yes, advance);
            _e.SetBlock(advance);
            _e.CopyTo(cursor, R(_e.Binary(Opcode.Add, cursor, w)));
            _e.Jump(loop);
        }
        else
        {
            VReg depth = _e.Load(IrType.I32, desc, DescDepth * w);
            Block deep = _f.NewBlock("tdeep");
            _e.Branch(_e.Binary(Opcode.GeS, depth, want.Depth), deep, no);
            _e.SetBlock(deep);
            VReg display = _e.Load(IrTypes.Word, desc, DescDisplay * w);
            VReg entry = _e.Load(IrTypes.Word, display, (long)want.Depth * w);
            VReg wanted = _e.Address(ClassDescriptor(want));
            _e.Branch(_e.Binary(Opcode.Eq, entry, wanted), yes, no);
        }

        _e.SetBlock(yes);
        _e.CopyTo(result, Imm(1, IrType.I32));
        _e.Jump(end);
        _e.SetBlock(no);
        _e.CopyTo(result, Imm(0, IrType.I32));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>The object when it is of the type, null otherwise: what `as` answers.</summary>
    private VReg AsType(VReg obj, TypeSymbol want)
    {
        VReg test = TypeTest(obj, want);
        VReg result = _f.NewReg(IrTypes.Word, "as");
        Block yes = _f.NewBlock("asyes");
        Block no = _f.NewBlock("asno");
        Block end = _f.NewBlock("asend");
        _e.Branch(test, yes, no);
        _e.SetBlock(yes);
        _e.CopyTo(result, R(obj));
        _e.Jump(end);
        _e.SetBlock(no);
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// Whether an object is an array of this type: `o is byte[]`.
    ///
    /// AN ARRAY'S TYPE IS ITS DESCRIPTOR, one per element type and stride and
    /// shared by every unit (SequenceDescriptor), so the test is one address
    /// compared with another -- which is also why `(object)"ab" is byte[]` is
    /// false although a string is laid out as bytes: strings have a
    /// descriptor of their own.
    ///
    /// C#'s ARRAY COVARIANCE for `object[]`: any array whose elements are
    /// references is one, `string[]` included, and an array of values is not.
    /// The descriptor's element flag says exactly that. Covariance between
    /// two class element types (`Dog[] is Animal[]`) would need the element's
    /// own descriptor in the array's, which it does not carry; such a test
    /// answers for the exact type only.
    /// </summary>
    private VReg ArrayTest(VReg obj, Type array)
    {
        Type element = array.Element!;
        int w = _t.WordSize;
        VReg result = _f.NewReg(IrType.I32, "isarr");
        Block some = _f.NewBlock("arrsome");
        Block end = _f.NewBlock("arrend");
        _e.CopyTo(result, Imm(0, IrType.I32));
        _e.Branch(obj, some, end);
        _e.SetBlock(some);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg wanted = _e.Address(SequenceDescriptor(ElementKey(element), ElementStride(element), isString: false, elementType: element),
                                 _t.DescriptorBytes);
        _e.CopyTo(result, R(_e.Binary(Opcode.Eq, R(vt), R(wanted), IrType.I32)));
        if (element.Prim == Prim.Any && element.ArrayRank == 0)
        {
            Block other = _f.NewBlock("arrcov");
            _e.Branch(result, end, other);
            _e.SetBlock(other);
            VReg flags = _e.Load(IrTypes.Word, vt, DescFlags * w - _t.DescriptorBytes);
            VReg gc = _e.Load(IrTypes.Word, vt, DescGcFlags * w - _t.DescriptorBytes);
            VReg sequence = _e.Binary(Opcode.Eq, R(flags), Imm(1, IrTypes.Word), IrType.I32);
            VReg refs = _e.Binary(Opcode.And, gc, GcElementsAreReferences);
            VReg anyRefs = _e.Binary(Opcode.Ne, R(refs), Imm(0, IrTypes.Word), IrType.I32);
            _e.CopyTo(result, R(_e.Binary(Opcode.And, R(sequence), R(anyRefs), IrType.I32)));
        }
        // COVARIANCE BETWEEN REFERENCE ELEMENTS: a Dog[] is an Animal[]. Not
        // the exact descriptor, then asked of the element's (Runtime.ArrayOf).
        else if (ElementDescriptor(element.AsNonNullable()) is string wantedElement && RuntimeMethod("ArrayOf", 2) is MethodSymbol arrayOf)
        {
            Block other = _f.NewBlock("arrcov");
            _e.Branch(result, end, other);
            _e.SetBlock(other);
            Require(arrayOf);
            VReg answer = _e.Call(CallLabel(arrayOf), IrTypes.Of(arrayOf.Returns),
                R(AsParam(obj, arrayOf.Params[0].Type)), R(AsParam(_e.Address(wantedElement), arrayOf.Params[1].Type)))!;
            _e.CopyTo(result, R(answer.Type == IrType.I32 ? answer : _e.Unary(Opcode.Trunc64, R(answer), IrType.I32)));
        }
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>The object when it is an array of the type, null otherwise.</summary>
    private VReg AsArray(VReg obj, Type array)
    {
        VReg test = ArrayTest(obj, array);
        VReg result = _f.NewReg(IrTypes.Word, "asarr");
        Block yes = _f.NewBlock("asarryes");
        Block no = _f.NewBlock("asarrno");
        Block end = _f.NewBlock("asarrend");
        _e.Branch(test, yes, no);
        _e.SetBlock(yes);
        _e.CopyTo(result, R(obj));
        _e.Jump(end);
        _e.SetBlock(no);
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>`(T[])o`: null passes, anything else must be such an array.</summary>
    private VReg CheckedArrayCast(Node at, VReg obj, Type array)
    {
        Block check = _f.NewBlock("acastck");
        Block ok = _f.NewBlock("acastok");
        Block bad = _f.NewBlock("acastbad");
        _e.Branch(obj, check, ok);
        _e.SetBlock(check);
        _e.Branch(ArrayTest(obj, array), ok, bad);
        _e.SetBlock(bad);
        CastFailed(obj, array);
        _e.SetBlock(ok);
        return obj;
    }

    /// <summary>A cast that is not a reinterpretation: null passes, anything else must be the type.</summary>
    private VReg CheckedCast(Node at, VReg obj, TypeSymbol want)
    {
        Block check = _f.NewBlock("castck");
        Block ok = _f.NewBlock("castok");
        Block bad = _f.NewBlock("castbad");
        _e.Branch(obj, check, ok);
        _e.SetBlock(check);
        _e.Branch(TypeTest(obj, want), ok, bad);
        _e.SetBlock(bad);
        CastFailed(obj, new Type { Prim = Prim.Void, Symbol = want });
        _e.SetBlock(ok);
        return obj;
    }

    /// <summary>
    /// The end of a cast that failed: InvalidCastException as .NET words it,
    /// "Unable to cast object of type 'X' to type 'Y'." -- the object's type
    /// read at run time, the one wanted written here -- and nothing after it.
    /// </summary>
    private void CastFailed(VReg obj, Type want)
    {
        if (RuntimeMethod("InvalidCastTo", 2) is MethodSymbol named)
        {
            Require(named);
            _e.Call(CallLabel(named), IrType.Void, R(obj), R(_e.Address(InternString(ShortTypeName(want)))));
        }
        else if (RuntimeMethod("InvalidCast", 1) is MethodSymbol fail)
        {
            Require(fail);
            _e.Call(CallLabel(fail), IrType.Void, R(obj));
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
    }

    /// <summary>A type as Type.ToString writes it: its full name, a generic's arguments without their assemblies.</summary>
    private string ShortTypeName(Type t) => TypeText(DotNetTypeName(t));

    /// <summary>The library's String.TypeText, here for names written at compile time.</summary>
    private static string TypeText(string full)
    {
        int open = full.IndexOf("[[", StringComparison.Ordinal);
        if (open < 0) return full;
        System.Text.StringBuilder text = new(full[..open]);
        text.Append('[');
        int at = open + 1;
        bool first = true;
        while (at < full.Length && full[at] == '[')
        {
            int depth = 0, end = at, comma = -1;
            for (; end < full.Length; end++)
            {
                char c = full[end];
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) break; }
                else if (c == ',' && depth == 1 && comma < 0) comma = end;
            }
            if (!first) text.Append(',');
            text.Append(TypeText(full.Substring(at + 1, (comma >= 0 ? comma : end) - at - 1)));
            first = false;
            at = end + 1;
            if (at < full.Length && full[at] == ',') at++;
        }
        return text.Append(']').Append(at + 1 <= full.Length ? full[(at + 1)..] : "").ToString();
    }

    /// <summary>
    /// THE ENTRY A SHARED COPY READS FOR ITS TYPE ARGUMENT (ICanonSlot):
    /// through its `this`'s descriptor, the table at the copy's own depth in
    /// that object's class chain (TypeContext).
    /// </summary>
    private VReg CanonEntry<T>(T at) where T : Expr, ICanonSlot
    {
        int w = _t.WordSize;
        TypeSymbol shared = _b.TypeOf(at.CanonSelf!).Symbol ?? throw new InvalidOperationException("a shared copy's `this` has no class");
        VReg self = Eval(at.CanonSelf!);
        VReg vt = _e.Load(IrTypes.Word, self, 0);
        VReg context = _e.Load(IrTypes.Word, vt, (long)DescTypeContext * w - _t.DescriptorBytes);
        VReg table = _e.Load(IrTypes.Word, context, (long)shared.Depth * w);
        return _e.Load(IrTypes.Word, table, (long)(at.CanonSlot + 1) * w);
    }

    /// <summary>
    /// The vtable `new X&lt;T&gt;()` in a shared copy stamps its object with
    /// (TypeDecl.CanonMade): the context's entry where it has one, and the
    /// shared class's own -- what it always was -- where it has none.
    /// </summary>
    private VReg CanonMadeVtable(NewExpr at, TypeSymbol shared)
    {
        int w = _t.WordSize;
        TypeSymbol self = _b.TypeOf(at.CanonSelf!).Symbol ?? throw new InvalidOperationException("a shared copy's `this` has no class");
        VReg obj = Eval(at.CanonSelf!);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg context = _e.Load(IrTypes.Word, vt, (long)DescTypeContext * w - _t.DescriptorBytes);
        VReg table = _e.Load(IrTypes.Word, context, (long)self.Depth * w);
        VReg count = _e.Load(IrTypes.Word, table, 0);
        VReg result = _f.NewReg(IrTypes.Word, "madevt");
        Block read = _f.NewBlock("maderead");
        Block own = _f.NewBlock("madeown");
        Block done = _f.NewBlock("madedone");
        _e.Branch(_e.Binary(Opcode.LtU, Imm(at.CanonSlot, IrTypes.Word), R(count), IrType.I32), read, own);
        _e.SetBlock(read);
        VReg entry = _e.Load(IrTypes.Word, table, (long)(at.CanonSlot + 1) * w);
        Block present = _f.NewBlock("madehave");
        _e.Branch(entry, present, own);
        _e.SetBlock(present);
        _e.CopyTo(result, R(_e.Binary(Opcode.Add, entry, _t.DescriptorBytes)));
        _e.Jump(done);
        _e.SetBlock(own);
        _e.CopyTo(result, VtableOf(shared));
        _e.Jump(done);
        _e.SetBlock(done);
        return result;
    }

    /// <summary>Whether a value is an object's address: a reference type, or object -- which a shared copy's T is.</summary>
    private static bool HeldByReference(Type t) => t.IsReference || t.Prim == Prim.Any && t.Symbol is null && !t.IsNullableValue;

    /// <summary>Whether an object, null being none, is of the type a descriptor read at run time names (Runtime.DescribedAs).</summary>
    private VReg DescribedTest(VReg obj, VReg want)
    {
        MethodSymbol described = RuntimeMethod("DescribedAs", 2) ?? throw new InvalidOperationException("the runtime has no DescribedAs");
        VReg result = _f.NewReg(IrType.I32, "isdesc");
        Block some = _f.NewBlock("isdsome");
        Block end = _f.NewBlock("isdend");
        _e.CopyTo(result, Imm(0, IrType.I32));
        _e.Branch(obj, some, end);
        _e.SetBlock(some);
        VReg desc = _e.Binary(Opcode.Sub, _e.Load(IrTypes.Word, obj, 0), _t.DescriptorBytes);
        Require(described);
        VReg answer = _e.Call(CallLabel(described), IrTypes.Of(described.Returns),
            R(AsParam(desc, described.Params[0].Type)), R(AsParam(want, described.Params[1].Type)))!;
        _e.CopyTo(result, R(answer.Type == IrType.I32 ? answer : _e.Unary(Opcode.Trunc64, R(answer), IrType.I32)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    private VReg EmitIs(IsExpr isx)
    {
        // `x is T` IN A SHARED COPY: of what T is for the object at hand.
        if (isx.CanonSlot >= 0 && HeldByReference(_b.TypeOf(isx.Operand)))
        {
            VReg tested = Eval(isx.Operand);
            VReg answer = DescribedTest(tested, CanonEntry(isx));
            if (_b.PatternSlot.TryGetValue(isx, out int canonNamed))
            {
                BindPattern(isx, canonNamed, Type.Any, tested);
            }
            return answer;
        }

        if (_b.NullablePatterns.Contains(isx))
        {
            // `nullable is T value`: null is false; a present cell is opened.
            Type whole = _b.TypeOf(isx.Operand);
            Type held = whole.Underlying;
            VReg cell = Eval(isx.Operand);
            VReg result = _f.NewReg(IrType.I32, "isn");
            Block some = _f.NewBlock("isnsome");
            Block end = _f.NewBlock("isnend");
            _e.CopyTo(result, Imm(0, IrType.I32));
            _e.Branch(HasValue(cell), some, end);
            _e.SetBlock(some);
            if (_b.PatternSlot.TryGetValue(isx, out int slot))
            {
                VReg v = NullableRead(cell, whole);
                BindPattern(isx, slot, held, v);
            }
            _e.CopyTo(result, Imm(1, IrType.I32));
            _e.Jump(end);
            _e.SetBlock(end);
            return result;
        }

        if (_b.NonNullPatterns.Contains(isx))
        {
            VReg present = Eval(isx.Operand);
            if (_b.PatternSlot.TryGetValue(isx, out int named))
            {
                BindPattern(isx, named, Type.Any, present);
            }
            return _e.Binary(Opcode.Ne, R(present), Imm(0, present.Type), IrType.I32);
        }

        if (_b.BoxPatterns.Contains(isx))
        {
            Type held = _b.TypeOf(isx.Operand);
            VReg value = Eval(isx.Operand);
            if (_b.PatternSlot.TryGetValue(isx, out int named))
            {
                BindPattern(isx, named, Type.Any, BoxValue(isx, value, held));
            }
            return _e.Const(1, IrType.I32);
        }

        if (_b.ValuePatterns.Contains(isx))
        {
            Type held = _b.TypeOf(isx.Operand);
            VReg v = Eval(isx.Operand);
            if (_b.PatternSlot.TryGetValue(isx, out int valueSlot))
            {
                BindPattern(isx, valueSlot, held, v);
            }
            return _e.Const(1, IrType.I32);
        }

        // `is var y` asks nothing at all: it names the subject and is true,
        // whatever the subject turned out to be -- null included.
        if (isx.Type.Name == TypeRef.Anything)
        {
            Type held = _b.TypeOf(isx.Operand);
            VReg value = Eval(isx.Operand);

            if (_b.PatternSlot.TryGetValue(isx, out int anything))
            {
                BindPattern(isx, anything, held, value);
            }
            return _e.Const(1, IrType.I32);
        }

        VReg obj = Eval(isx.Operand);

        // `is { } y` asks only that the subject is there.
        if (isx.Type.Name == TypeRef.Same && _b.TypeOf(isx.Operand).IsNullableValue)
        {
            // Over a nullable value: whether it has one, and the value named.
            Type whole = _b.TypeOf(isx.Operand);
            if (_b.PatternSlot.TryGetValue(isx, out int present))
            {
                BindPattern(isx, present, whole.Underlying, NullableRead(obj, whole));
            }
            return HasValue(obj);
        }
        if (isx.Type.Name == TypeRef.Same)
        {
            if (_b.PatternSlot.TryGetValue(isx, out int same))
            {
                BindPattern(isx, same, _b.TypeOf(isx.Operand).AsNonNullable(), obj);
            }
            return _e.Binary(Opcode.Ne, R(obj), Imm(0, obj.Type), IrType.I32);
        }

        if (_b.StringTests.Contains(isx))
        {
            VReg asString = AsStringValue(obj, _b.TypeOf(isx.Operand));
            if (_b.PatternSlot.TryGetValue(isx, out int text))
            {
                BindPattern(isx, text, Type.String, asString);
            }
            return _e.Binary(Opcode.Ne, R(asString), Imm(0, asString.Type), IrType.I32);
        }

        // A BOXED VALUE TESTED FOR ITS OWN TYPE: `o is int`. The box carries a
        // descriptor of its own, so the test is that descriptor and nothing
        // else -- `(object)(short)5 is int` is false in C#, and there is no
        // widening on the way out of a box.
        if (_b.TypeOf(isx.Operand).Prim == Prim.Any
            && isx.Type is { Args.Count: 0, ArrayRank: 0 }
            && ValueTypeNamed(isx.Type.Name) is { } boxed)
        {
            return BoxPattern(isx, obj, boxed,
                              _b.PatternSlot.TryGetValue(isx, out int held) ? held : null);
        }

        if (_b.TestedArrays.TryGetValue(isx, out Type? array))
        {
            VReg held = AsArray(obj, array);
            if (_b.PatternSlot.TryGetValue(isx, out int named))
            {
                BindPattern(isx, named, array, held);
            }
            return _e.Binary(Opcode.Ne, R(held), Imm(0, held.Type), IrType.I32);
        }

        TypeSymbol? want = _b.TestedTypes.TryGetValue(isx, out TypeSymbol? resolved) ? resolved
                         : _b.Types.TryGetValue(isx.Type.Name, out TypeSymbol? found) ? found : null;
        if (want is null)
        {
            return Fail(isx, "this type cannot be tested at run time");
        }

        VReg matched = AsType(obj, want);
        if (_b.PatternSlot.TryGetValue(isx, out int bound))
        {
            BindPattern(isx, bound, new Type { Prim = Prim.Void, Symbol = want }, matched);
        }
        return _e.Binary(Opcode.Ne, R(matched), Imm(0, matched.Type), IrType.I32);
    }

    /// <summary>
    /// A BOXED VALUE TESTED FOR ITS OWN TYPE and, when the pattern named it,
    /// taken out of the box into that name's slot.
    ///
    /// Shared by `o is int n` and by a switch expression's `int n` arm,
    /// because the two have to agree about what the pattern means -- the arm
    /// had only the reference test, matched anything at all, and bound the
    /// box's address.
    /// </summary>
    private VReg BoxPattern(Node at, VReg obj, Type boxed, int? slot)
    {
        VReg is_ = BoxTest(obj, boxed);

        if (slot is not int held)
        {
            return is_;
        }

        Block open = _f.NewBlock("unbnd");
        Block after = _f.NewBlock("unbndend");

        _e.Branch(is_, open, after);
        _e.SetBlock(open);

        // A STRUCT IS A BLOCK, and what the pattern names is a COPY of it --
        // the same copy a cast out of the box makes, and for the same reason:
        // writing to what came out must not write through to the box somebody
        // else may still be holding. Reading it as one word instead gave the
        // name the struct's first field and called it the struct.
        if (BoxedBlock(boxed))
        {
            VReg inside = _e.Binary(Opcode.Add, obj, _t.ObjectHeaderBytes);

            // Into the cell when a lambda captures the binding (BindPattern).
            BindPattern(at, held, boxed, CopyStruct(at, inside, boxed.Symbol!));
        }
        else
        {
            BindPattern(at, held, boxed,
                        _e.Load(BoxSlot(boxed), obj, _t.ObjectHeaderBytes,
                                Math.Max(1, boxed.Size),
                                !boxed.IsUnsigned && boxed.Prim != Prim.Bool));
        }
        _e.Jump(after);
        _e.SetBlock(after);
        return is_;
    }

    // ---- switch expressions ---------------------------------------------------------------

    private VReg EmitSwitchExpr(SwitchExpr sx)
    {
        Type resultType = _b.TypeOf(sx);
        Type of = _b.TypeOf(sx.Subject);
        VReg result = _f.NewReg(IrTypes.Of(resultType), "swx");
        Block end = _f.NewBlock("swxend");

        VReg subject = Eval(sx.Subject);
        if (_b.SwitchSlot.TryGetValue(sx, out int held))
        {
            _e.CopyTo(SlotReg(held, IrTypes.Of(of)), R(subject));
        }

        foreach (SwitchArm arm in sx.Arms)
        {
            Block next = _f.NewBlock("swarm");
            Block body = _f.NewBlock("swbody");

            if (arm.Type is not null && _b.StringTests.Contains(arm))
            {
                VReg asString = AsStringValue(subject, of);
                if (_b.ArmSlot.TryGetValue(arm, out int text))
                {
                    BindPattern(arm, text, Type.String, asString);
                }
                _e.Branch(asString, body, next);
            }
            else if (arm.Type is not null && _b.ArmTests.Contains(arm)
                     && of.Prim == Prim.Any && arm.Type is { Args.Count: 0, ArrayRank: 0 }
                     && ValueTypeNamed(arm.Type.Name) is { } boxed)
            {
                VReg matched = BoxPattern(arm, subject, boxed,
                                          _b.ArmSlot.TryGetValue(arm, out int into) ? into : null);
                _e.Branch(matched, body, next);
            }
            else if (arm.Type is not null && _b.ArmTests.Contains(arm)
                     && _b.TestedArrays.TryGetValue(arm, out Type? array))
            {
                VReg matched = AsArray(subject, array);
                if (_b.ArmSlot.TryGetValue(arm, out int named))
                {
                    BindPattern(arm, named, array, matched);
                }
                _e.Branch(matched, body, next);
            }
            else if (arm.Type is not null && _b.ArmTests.Contains(arm))
            {
                TypeSymbol? want = _b.TestedTypes.TryGetValue(arm, out TypeSymbol? resolved) ? resolved
                                 : _b.Types.TryGetValue(arm.Type.Name, out TypeSymbol? found) ? found : null;
                if (want is null)
                {
                    Error(arm, "this type cannot be tested at run time");
                    _e.Jump(next);
                    _e.SetBlock(next);
                    continue;
                }
                VReg matched = AsType(subject, want);
                if (_b.ArmSlot.TryGetValue(arm, out int bound))
                {
                    BindPattern(arm, bound, new Type { Prim = Prim.Void, Symbol = want }, matched);
                }
                _e.Branch(matched, body, next);
            }
            else if (arm.Type is not null)
            {
                if (_b.ArmSlot.TryGetValue(arm, out int named))
                {
                    BindPattern(arm, named, of, subject);
                }
                _e.Jump(body);
            }
            else if (!arm.Discard)
            {
                VReg want = EvalAs(arm.Value!, of);
                VReg same;
                if (of.Prim == Prim.String)
                {
                    same = StringEquals(arm, subject, want);
                }
                else if (of.IsFloat)
                {
                    same = _e.Binary(Opcode.FEq, subject, want);
                }
                else
                {
                    same = _e.Binary(Opcode.Eq, subject, want);
                }
                _e.Branch(same, body, next);
            }
            else
            {
                // `var big => …` names the subject and asks nothing of it.
                if (_b.ArmSlot.TryGetValue(arm, out int anything))
                {
                    BindPattern(arm, anything, of, subject);
                }
                _e.Jump(body);
            }

            _e.SetBlock(body);
            if (arm.When is not null)
            {
                Block guarded = _f.NewBlock("swwhen");
                BranchOn(arm.When, guarded, next);
                _e.SetBlock(guarded);
            }

            _e.CopyTo(result, R(EvalAs(arm.Result, resultType)));
            _e.Jump(end);
            _e.SetBlock(next);
        }

        // Nothing matched: the binder refused this unless there is a `_`, so
        // this path is unreachable, but the register needs a definition.
        _e.CopyTo(result, Imm(0, result.Type.IsFloat() ? IrType.I32 : result.Type));
        if (result.Type.IsFloat())
        {
            VReg z = _e.Const(0, result.Type == IrType.F32 ? IrType.I32 : IrType.I64);
            _e.CopyTo(result, R(_e.Unary(Opcode.Bits, R(z), result.Type)));
        }
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    // ---- assignment -------------------------------------------------------------------

    private VReg EmitAssign(AssignExpr a)
    {
        if (_b.DiscardAssignments.Contains(a))
        {
            return Eval(a.Value);
        }

        Type targetType = _b.TypeOf(a.Target);

        // `r = ref other;` points the ref local at another variable: its
        // register takes the new address, and nothing is stored through it.
        if (a.Op is null && a.Value is RefArgExpr { IsOut: false, Name: null } referred
            && a.Target is NameExpr pointed && _b.Resolved.TryGetValue(pointed, out Sym? pointedSym)
            && pointedSym is LocalSym { IsRef: true } refLocal && DeclOf(refLocal) is LocalDecl refDecl)
        {
            _e.CopyTo(LocalReg(refDecl), R(Reference(referred, refLocal.Type)));
            return LoadPlace(PlaceOfSym(refLocal, a)!);
        }

        if (a.Op is null)
        {
            if (_b.PropertySetters.TryGetValue(a.Target, out MethodSymbol? propertySetter))
            {
                return EmitPropertyAssignment(a, propertySetter);
            }

            if (a.Target is IndexExpr index && _b.IndexSetters.TryGetValue(index, out MethodSymbol? setter))
            {
                return EmitIndexerAssignment(a, index, setter);
            }

            // The location first, then the value: `a[i] = f()` evaluates a
            // and i before f, and a place captures its address exactly once.
            Place? place = PlaceOf(a.Target);

            // THE PLACE KNOWS WHAT IT HOLDS, and for a plain `name = value`
            // the binder does not: it resolves the target symbol without ever
            // checking the target expression, so nothing records a type for it
            // and `TypeOf` answers Error. Converting to Error is a no-op, and
            // `long b; b = anInt;` therefore stored a 32-bit value into a
            // 64-bit location, leaving the high half whatever was there --
            // the int was neither sign- nor zero-extended. The declared type
            // of the location is the one the conversion has to target.
            Type storedAs = place is not null && targetType.IsError ? place.Type : targetType;
            VReg value = InlineValue(place, a.Value, storedAs);
            if (place is not null)
            {
                StorePlace(place, value);
            }
            return value;
        }

        // Compound: read, combine, write back, through the one place.
        BinOp op = a.Op.Value;
        Type valueType = _b.TypeOf(a.Value);

        // A delegate += or -= is the multicast Combine or Remove the binder
        // synthesised, stored back into the same place.
        if (_b.DelegateCompounds.TryGetValue(a, out CallExpr? delegateCall))
        {
            // THE PLACE IS WORKED OUT ONCE AND USED FOR BOTH HALVES. The
            // binder synthesised Combine(target, value) over the assignment's
            // own target expression, so evaluating that call would evaluate
            // the target a second time: `Items.Add("x").Click += h` called
            // Add twice and put the item in the list twice. The current value
            // is loaded from the place instead, which the receiver has
            // already been evaluated for.
            Place? delegatePlace = PlaceOf(a.Target);
            if (delegatePlace is not null
                && _b.Calls.TryGetValue(delegateCall, out MethodSymbol? combine)
                && combine.Static && combine.Params.Count == 2)
            {
                TouchType(combine.Owner);
                VReg had = LoadPlace(delegatePlace);
                VReg operand = EvalAs(a.Value, combine.Params[1].Type);
                VReg made = CallMethod(combine, null, new List<Operand> { R(had), R(operand) })!;
                StorePlace(delegatePlace, made);
                return made;
            }
            VReg combined = Eval(delegateCall);
            if (delegatePlace is not null) StorePlace(delegatePlace, combined);
            return combined;
        }

        if (_b.PropertySetters.TryGetValue(a.Target, out MethodSymbol? ps)
            || (a.Target is IndexExpr ix2 && _b.IndexSetters.ContainsKey(ix2)))
        {
            // Accessor pair: get, combine, set. Receiver and indices once.
            return EmitAccessorCompound(a, op);
        }

        Place? p = PlaceOf(a.Target);
        if (p is null)
        {
            return Eval(a.Value);
        }

        // Same recovery as the plain form above: `x += y` on a simple name has
        // no recorded type for the name, and the place's declared type is it.
        if (targetType.IsError)
        {
            targetType = p.Type;
        }

        VReg current = LoadPlace(p);
        VReg result;

        if (targetType.Prim == Prim.String && op == BinOp.Add)
        {
            VReg right = Stringify(a.Value, Eval(a.Value), valueType);
            result = StringBinary(a, BinOp.Add, current, right);
        }
        else
        {
            result = Combine(a, op, current, targetType, a.Value, valueType, targetType);
        }

        StorePlace(p, result);
        return result;
    }

    /// <summary>
    /// `a OP= b` for numeric and bool operands: promote as C# does, combine,
    /// and convert back to the target's type, which is where a compound
    /// assignment on a byte wraps.
    /// </summary>
    private VReg Combine(Node at, BinOp op, VReg left, Type leftType, Expr rightExpr, Type rightType, Type resultType)
    {
        // LIFTED, as `a + b` is: `n += 1` for an `int? n` stays null when n
        // is, and adds to what is in the cell when it is not.
        if (leftType.IsNullableValue || rightType.IsNullableValue)
        {
            VReg right = Eval(rightExpr);
            if (leftType.Prim == Prim.Bool && rightType.Prim == Prim.Bool && op is BinOp.And or BinOp.Or)
                return NullableLogic(at, op == BinOp.And, left, leftType, right, rightType);
            return NullableArith(at, op, left, leftType, right, rightType, resultType.IsNullableValue ? resultType : resultType.AsNullable());
        }

        Type promoted = OperandPromotion(op, leftType, rightType);
        Type rightPromoted = RightOperandPromotion(op, rightType, promoted);
        VReg l = Convert(at, left, leftType, promoted);
        VReg r = EvalAs(rightExpr, rightPromoted);
        VReg combined = Arith(at, op, l, r, promoted);
        return Convert(at, combined, ResultTypeOf(op, promoted), resultType);
    }

    private VReg EmitAccessorCompound(AssignExpr a, BinOp op)
    {
        Type targetType = _b.TypeOf(a.Target);
        Type valueType = _b.TypeOf(a.Value);

        if (a.Target is IndexExpr ix && _b.IndexSetters.TryGetValue(ix, out MethodSymbol? setter)
            && _b.Indexers.TryGetValue(ix, out MethodSymbol? getter))
        {
            VReg receiver = Eval(ix.Target);
            List<Operand> indices = new();
            for (int i = 0; i < ix.Args.Count; i++)
            {
                indices.Add(R(EvalAs(ix.Args[i], getter.Params[i])));
            }
            List<Operand> getArgs = new() { R(receiver) };
            getArgs.AddRange(indices);
            VReg current = CallMethod(getter, receiver, getArgs, ix.Target is BaseExpr)!;
            VReg result = targetType.Prim == Prim.String && op == BinOp.Add
                ? StringBinary(a, BinOp.Add, current, Stringify(a.Value, Eval(a.Value), valueType))
                : Combine(a, op, current, targetType, a.Value, valueType, targetType);
            List<Operand> setArgs = new() { R(receiver) };
            setArgs.AddRange(indices);
            setArgs.Add(R(result));
            CallMethod(setter, receiver, setArgs, ix.Target is BaseExpr);
            return result;
        }

        if (_b.PropertySetters.TryGetValue(a.Target, out MethodSymbol? ps))
        {
            VReg? receiver = null;
            MethodSymbol? pg = null;
            switch (a.Target)
            {
                case MemberExpr member:
                    receiver = ps.Static ? null : Eval(member.Target);
                    if (_b.Resolved.TryGetValue(member, out Sym? s) && s is PropertyGetSym g)
                        pg = g.Getter;
                    break;
                case NameExpr name:
                    receiver = ps.Static ? null : _this;
                    if (_b.Resolved.TryGetValue(name, out Sym? s2))
                    {
                        if (s2 is PropertyGetSym g2)
                            pg = g2.Getter;
                        if (s2 is CapturedPropertyGetSym cg)
                        {
                            pg = cg.Getter;
                            receiver = _e.Load(IrTypes.Word, _this!, cg.Holder.Offset);
                        }
                    }
                    break;
            }
            if (pg is null)
            {
                return Fail(a, "this property has no getter to read for a compound assignment");
            }
            List<Operand> getArgs = new();
            if (receiver is not null)
                getArgs.Add(R(receiver));
            VReg current = CallMethod(pg, receiver, getArgs, a.Target is MemberExpr { Target: BaseExpr })!;
            VReg result = targetType.Prim == Prim.String && op == BinOp.Add
                ? StringBinary(a, BinOp.Add, current, Stringify(a.Value, Eval(a.Value), valueType))
                : Combine(a, op, current, targetType, a.Value, valueType, targetType);
            List<Operand> setArgs = new();
            if (receiver is not null)
                setArgs.Add(R(receiver));
            setArgs.Add(R(result));
            CallMethod(ps, receiver, setArgs, a.Target is MemberExpr { Target: BaseExpr });
            return result;
        }

        return Fail(a, "this compound assignment target is not implemented");
    }

    private VReg EmitIndexerAssignment(AssignExpr a, IndexExpr index, MethodSymbol setter)
    {
        VReg receiver = Eval(index.Target);
        List<Operand> args = new() { R(receiver) };
        for (int i = 0; i < index.Args.Count; i++)
        {
            args.Add(R(EvalAs(index.Args[i], setter.Params[i])));
        }
        ParamSymbol vp = setter.Params[^1];
        VReg value = EvalAs(a.Value, vp);
        args.Add(R(value));
        CallMethod(setter, receiver, args, index.Target is BaseExpr);
        return value;
    }

    private VReg EmitPropertyAssignment(AssignExpr a, MethodSymbol setter)
    {
        VReg? receiver = null;
        if (!setter.Static)
        {
            switch (a.Target)
            {
                case MemberExpr member:
                    receiver = Eval(member.Target);
                    break;
                case NameExpr when _b.Resolved.TryGetValue(a.Target, out Sym? resolved)
                                   && resolved is CapturedPropertyGetSym captured:
                    receiver = _e.Load(IrTypes.Word, _this!, captured.Holder.Offset);
                    break;
                case NameExpr:
                    receiver = _this;
                    break;
                default:
                    return Fail(a.Target, "this property receiver is not implemented");
            }
        }

        ParamSymbol vp = setter.Params[^1];
        VReg value = EvalAs(a.Value, vp);
        List<Operand> args = new();
        if (receiver is not null)
            args.Add(R(receiver));
        args.Add(R(value));
        CallMethod(setter, receiver, args, a.Target is MemberExpr { Target: BaseExpr });
        return value;
    }

    // ---- unary ---------------------------------------------------------------------------

    private VReg EmitUnary(UnaryExpr u)
    {
        Type type = _b.TypeOf(u);

        switch (u.Op)
        {
            case UnOp.Checked:
            {
                _checkedDepth++;
                VReg v = Eval(u.Operand);
                _checkedDepth--;
                return v;
            }

            case UnOp.Unchecked:
            {
                int outside = _checkedDepth;
                _checkedDepth = 0;
                VReg v = Eval(u.Operand);
                _checkedDepth = outside;
                return v;
            }

            case UnOp.Deref:
            {
                VReg addr = Eval(u.Operand);
                return LoadPlace(new MemPlace(R(addr), 0, type));
            }

            case UnOp.AddressOf when _b.MethodAddresses.TryGetValue(u, out MethodSymbol? addressed):
                // `&Method`: the function pointer is the method's address.
                Require(addressed);
                return _e.Address(CallLabel(addressed));

            case UnOp.AddressOf:
                return AddressOf(u, u.Operand);

            case UnOp.Neg or UnOp.BitNot when _b.TypeOf(u.Operand).IsNullableValue:
                return LiftedUnary(u, _b.TypeOf(u.Operand), _b.TypeOf(u));

            case UnOp.Neg:
            {
                Type operand = _b.TypeOf(u.Operand);
                Type promoted = NumericRules.Unary(operand);
                // A uint is negated as the long it widens to (C# 12.9.3).
                if (promoted.Prim == Prim.U32 && promoted.Symbol is null) promoted = Type.I64;
                VReg v = EvalAs(u.Operand, promoted);
                if (promoted.IsFloat)
                {
                    return _e.Unary(Opcode.FNeg, v);
                }
                if (_checkedDepth > 0 && !promoted.IsUnsigned)
                {
                    return CheckedArith(u, BinOp.Sub, _e.Const(0, v.Type), v, promoted);
                }
                return _e.Unary(Opcode.Neg, v);
            }

            case UnOp.Not:
            {
                VReg v = Eval(u.Operand);
                return _e.Binary(Opcode.Eq, R(v), Imm(0, v.Type), IrType.I32);
            }

            // `+x`: the value, promoted (a lifted one through its cell).
            case UnOp.Plus when _b.TypeOf(u.Operand).IsNullableValue:
                return Eval(u.Operand);

            case UnOp.Plus:
                return EvalAs(u.Operand, NumericRules.Unary(_b.TypeOf(u.Operand)));

            case UnOp.BitNot:
            {
                Type operand = _b.TypeOf(u.Operand);
                Type promoted = NumericRules.Unary(operand);
                VReg v = EvalAs(u.Operand, promoted);
                return Canonical(_e.Unary(Opcode.Not, v), promoted);
            }

            case UnOp.PreInc or UnOp.PreDec or UnOp.PostInc or UnOp.PostDec:
            {
                bool inc = u.Op is UnOp.PreInc or UnOp.PostInc;
                bool post = u.Op is UnOp.PostInc or UnOp.PostDec;
                Type operand = _b.TypeOf(u.Operand);

                if (_b.PropertySetters.ContainsKey(u.Operand)
                    || (u.Operand is IndexExpr ixp && _b.IndexSetters.ContainsKey(ixp)))
                {
                    // Through accessors: rewrite as x = x + 1 with the value
                    // fetched once. The old value is the post result.
                    LiteralExpr one = new() { Kind = Lit.Int, Text = "1", IntValue = 1, Line = u.Line, Col = u.Col };
                    AssignExpr rewritten = new() { Op = inc ? BinOp.Add : BinOp.Sub, Target = u.Operand, Value = one, Line = u.Line, Col = u.Col };
                    _b.ExprType[one] = Type.I32;
                    _b.ExprType[rewritten] = operand;
                    VReg after = EmitAccessorCompound(rewritten, inc ? BinOp.Add : BinOp.Sub);
                    if (!post)
                    {
                        return after;
                    }
                    // The pre-value is after minus the delta.
                    if (operand.IsFloat)
                    {
                        VReg floatDelta = FloatConst(1.0, operand);
                        return _e.Binary(inc ? Opcode.FSub : Opcode.FAdd, after, floatDelta);
                    }
                    VReg delta = _e.Const(1, after.Type);
                    return Canonical(_e.Binary(inc ? Opcode.Sub : Opcode.Add, after, delta), operand);
                }

                Place? p = PlaceOf(u.Operand);
                if (p is null)
                {
                    return _e.Const(0, IrTypes.Of(operand));
                }

                // A NULLABLE ONE IS LIFTED: a null stays null, and a value is
                // stepped in a new cell (a cell is a value, and whoever holds
                // the old one keeps it).
                if (operand.IsNullableValue)
                {
                    // The value before, kept apart from the place the
                    // stepped one is copied into.
                    VReg had = CopyStruct(u, LoadPlace(p), StructOf(operand));
                    VReg stepped = NullableArith(u, inc ? BinOp.Add : BinOp.Sub, had, operand, _e.Const(1, IrType.I32), Type.I32, operand);
                    StorePlace(p, stepped);
                    return post ? had : stepped;
                }

                // A copy, because a register-resident variable's place IS its
                // register, and the store below would overwrite the old value
                // that a post-increment has to hand back.
                VReg old = _e.Copy(LoadPlace(p));
                VReg updated;
                if (operand.IsFloat)
                {
                    VReg one = FloatConst(1.0, operand);
                    updated = _e.Binary(inc ? Opcode.FAdd : Opcode.FSub, old, one);
                }
                else if (operand.IsPointer)
                {
                    int stride = Math.Max(1, operand.Pointee?.Size ?? 1);
                    updated = _e.Binary(inc ? Opcode.Add : Opcode.Sub, old, stride);
                }
                else
                {
                    Type promoted = NumericRules.Unary(operand);
                    VReg wide = Convert(u, old, operand, promoted);
                    VReg one = _e.Const(1, wide.Type);
                    VReg sum = _checkedDepth > 0
                        ? CheckedArith(u, inc ? BinOp.Add : BinOp.Sub, wide, one, promoted)
                        : _e.Binary(inc ? Opcode.Add : Opcode.Sub, wide, one);
                    updated = Convert(u, sum, promoted, operand);
                }
                StorePlace(p, updated);
                return post ? old : updated;
            }

            default:
                return Fail(u, $"unary '{u.Op}' is not implemented");
        }
    }

    /// <summary>
    /// An explicit numeric conversion in a checked context: OverflowException
    /// when the value is outside what the target holds, as C# 12.8.20 has it
    /// -- an integer outside the range, a floating value outside it after
    /// truncation, or NaN. A conversion that always fits asks nothing.
    /// </summary>
    private void CheckedNarrow(Node at, VReg v, Type from, Type to)
    {
        if (from.Symbol is not null || to.Symbol is not null || from.Nullable || to.Nullable) return;
        if (!(to.IsInteger || to.Prim == Prim.Char)) return;
        // A range as its least value, signed, and its greatest, unsigned:
        // between them every integer type's bounds are exact.
        static (long Min, ulong Max)? Range(Prim p, int word) => p switch
        {
            Prim.I8 => (sbyte.MinValue, (ulong)sbyte.MaxValue),
            Prim.U8 => (0L, byte.MaxValue),
            Prim.I16 => (short.MinValue, (ulong)short.MaxValue),
            Prim.U16 or Prim.Char => (0L, ushort.MaxValue),
            Prim.I32 => (int.MinValue, (ulong)int.MaxValue),
            Prim.U32 => (0L, uint.MaxValue),
            Prim.I64 => (long.MinValue, (ulong)long.MaxValue),
            Prim.U64 => (0L, ulong.MaxValue),
            Prim.NInt => word == 8 ? (long.MinValue, (ulong)long.MaxValue) : (int.MinValue, (ulong)int.MaxValue),
            Prim.NUInt => word == 8 ? (0L, ulong.MaxValue) : (0L, uint.MaxValue),
            _ => null,
        };
        if (Range(to.Prim, _t.WordSize) is not (long min, ulong max)) return;
        VReg bad;
        if (from.Prim is Prim.F32 or Prim.F64)
        {
            Type dbl = Type.F64;
            VReg d = from.Prim == Prim.F32 ? _e.Unary(Opcode.FConv, R(v), IrType.F64) : v;
            // Truncation keeps anything above min - 1 and below max + 1; at 64
            // bits min - 1 is no double, and min itself is the bound. An
            // unsigned target's bound is -1, exclusive, at any width.
            bool wide = max > uint.MaxValue && min < 0;
            VReg low = FloatConst(wide ? (double)min : (double)min - 1.0, dbl);
            VReg high = FloatConst((double)max + 1.0, dbl);
            VReg aboveLow = _e.Binary(wide ? Opcode.FGe : Opcode.FGt, R(d), R(low), IrType.I32);
            VReg belowHigh = _e.Binary(Opcode.FLt, R(d), R(high), IrType.I32);
            VReg fits = _e.Binary(Opcode.And, aboveLow, belowHigh);
            bad = _e.Binary(Opcode.Eq, R(fits), Imm(0, IrType.I32), IrType.I32);
        }
        else if (from.IsInteger || from.Prim == Prim.Char)
        {
            if (Range(from.Prim, _t.WordSize) is not (long fromMin, ulong fromMax)) return;
            if (fromMin >= min && fromMax <= max) return;
            if (from.Prim == Prim.U64 || from.Prim == Prim.NUInt && _t.WordSize == 8)
            {
                // Above long's range a ulong has no signed reading: compared unsigned.
                VReg u = v.Type == IrType.I64 ? v : _e.Unary(Opcode.ZExt32, R(v), IrType.I64);
                bad = _e.Binary(Opcode.GtU, R(u), Imm((long)max, IrType.I64), IrType.I32);
            }
            else
            {
                VReg w = v.Type == IrType.I64 ? v : _e.Unary(from.IsUnsigned ? Opcode.ZExt32 : Opcode.SExt32, R(v), IrType.I64);
                long hi = max > (ulong)long.MaxValue ? long.MaxValue : (long)max;
                VReg under = _e.Binary(Opcode.LtS, R(w), Imm(min, IrType.I64), IrType.I32);
                VReg over = _e.Binary(Opcode.GtS, R(w), Imm(hi, IrType.I64), IrType.I32);
                bad = _e.Binary(Opcode.Or, under, over);
            }
        }
        else
        {
            return;
        }
        Block fine = _f.NewBlock("cvok");
        Block fail = _f.NewBlock("cvovf");
        _e.Branch(bad, fail, fine);
        _e.SetBlock(fail);
        MethodSymbol? thrower = RuntimeMethod("Overflow", 0);
        if (thrower is not null)
        {
            Require(thrower);
            _e.Call(CallLabel(thrower), IrType.Void);
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
        _e.SetBlock(fine);
    }

    private VReg FloatConst(double value, Type t)
    {
        if (t.Prim == Prim.F32)
        {
            VReg bits = _e.Const(BitConverter.SingleToInt32Bits((float)value), IrType.I32);
            return _e.Unary(Opcode.Bits, R(bits), IrType.F32);
        }
        VReg bits64 = _e.Const(BitConverter.DoubleToInt64Bits(value), IrType.I64);
        return _e.Unary(Opcode.Bits, R(bits64), IrType.F64);
    }

    /// <summary>
    /// The address of a local, parameter, field or element: what `&x` and a
    /// by-reference argument both ask.
    /// </summary>
    private VReg AddressOf(Node at, Expr target)
    {
        if (target is IndexExpr index && index.Args.Count == 1 && !_b.Indexers.ContainsKey(index))
        {
            Type sequence = _b.TypeOf(index.Target);
            Type element = _b.TypeOf(index);
            VReg basis = Eval(index.Target);
            VReg i = EvalAs(index.Args[0], Type.I32);
            MemPlace p = ElementPlace(basis, i, sequence, element, at);
            return p.Offset == 0 ? RegOf(p.Address) : _e.Binary(Opcode.Add, RegOf(p.Address), p.Offset);
        }

        if (target is MemberExpr me && _b.Resolved.TryGetValue(me, out Sym? ms) && ms is FieldSym fs)
        {
            Place p = fs.Field.Static ? PlaceOfField(fs.Field, null, at) : PlaceOfField(fs.Field, Eval(me.Target), at);
            return AddressOfPlace(p, at);
        }

        if (target is UnaryExpr { Op: UnOp.Deref } deref)
        {
            return Eval(deref.Operand);
        }

        // A ref-returning call's answer is the address.
        if (target is CallExpr call && RefCallee(call) is not null)
        {
            return EmitCall(call);
        }

        if (target is NameExpr n && _b.Resolved.TryGetValue(n, out Sym? sym))
        {
            if (sym is ParamSym { ByRef: true } p)
            {
                // Already an address: pass it on rather than the address of it.
                return _params[p.Index];
            }
            if (sym is LocalSym { IsRef: true } && PlaceOfSym(sym, at) is MemPlace { Address: RegOperand held })
            {
                // So is a ref local.
                return held.Reg;
            }
            Place? place = PlaceOfSym(sym, at);
            if (place is null)
            {
                return _e.Const(0, IrTypes.Word);
            }
            if (place is RegPlace)
            {
                return Fail(at, "this variable's address was not planned for; it should have been found by the scan");
            }
            return AddressOfPlace(place, at);
        }

        return Fail(at, "only a local, a parameter, a field or an array element has an address");
    }

    /// <summary>
    /// A struct variable passed by reference: the address of its bytes. A
    /// by-reference parameter passed on is one already. One held in line is
    /// where it is; any other holds a pointer to its block, which is the
    /// address -- made first, zero, for a variable that has none yet (an
    /// `out` local not assigned).
    /// </summary>
    /// <summary>
    /// The value to store into `place`: for a struct held in line, the
    /// struct as it is -- StorePlace copies its bytes in, so the copy EvalAs
    /// makes of a struct it did not just make would be made for nothing;
    /// for anything else, EvalAs's.
    /// </summary>
    private VReg InlineValue(Place? place, Expr value, Type storedAs)
    {
        if (place is MemPlace { Inline: true } && IsStructValue(storedAs))
        {
            // Already made what it is stored as -- a number the checker marked
            // to go into a Nullable -- it is not converted a second time.
            VReg made = Eval(value);
            return _b.Boxes.Contains(value) || _b.Views.ContainsKey(value) ? made : Convert(value, made, _b.TypeOf(value), storedAs);
        }
        return EvalAs(value, storedAs);
    }

    /// <summary>
    /// <paramref name="fresh"/> for a variable the argument DECLARES -- `out
    /// var d`, `out Pair d`, `out _` -- which C# makes new each time the
    /// declaration runs: it gets a zeroed block of its own rather than
    /// whatever its slot last held, which in a loop is the previous
    /// iteration's struct and before any, whatever was in the frame.
    /// </summary>
    private VReg StructReference(Expr target, Type type, bool fresh = false)
    {
        if (target is NameExpr n && _b.Resolved.TryGetValue(n, out Sym? s) && s is ParamSym { ByRef: true } passed)
        {
            return _params[passed.Index];
        }
        Place? place = PlaceOf(target);
        if (place is null)
        {
            return _e.Const(0, IrTypes.Word);
        }
        if (place is MemPlace { Inline: true })
        {
            return LoadPlace(place);
        }
        if (fresh)
        {
            VReg made = NewStruct(target, StructOf(type));
            StorePlace(place, made);
            return made;
        }
        return StructBlock(place, target, type);
    }

    /// <summary>
    /// The block a struct variable held as a pointer points at, made first,
    /// zero, when it has none yet.
    /// </summary>
    private VReg StructBlock(Place place, Node target, Type type)
    {
        VReg held = LoadPlace(place);
        VReg result = _f.NewReg(IrTypes.Word, "sref");
        Block make = _f.NewBlock("srefmake");
        Block done = _f.NewBlock("srefdone");
        _e.CopyTo(result, R(held));
        _e.Branch(held, done, make);
        _e.SetBlock(make);
        // The parameter's struct type, not the argument's: an argument may
        // name the variable through a type that carries no symbol of its own.
        VReg zero = NewStruct(target, StructOf(type));
        StorePlace(place, zero);
        _e.CopyTo(result, R(zero));
        _e.Jump(done);
        _e.SetBlock(done);
        return result;
    }

    private VReg AddressOfPlace(Place p, Node at)
    {
        if (p is not MemPlace m)
        {
            return Fail(at, "this value has no address");
        }
        if (m.Inline)
        {
            return Fail(at, "a struct field held in line in its object (FieldSymbol.Inline) has no pointer to it to pass by reference");
        }
        VReg basis = RegOf(m.Address);
        if (m.Offset == 0) return basis;
        VReg address = _e.Binary(Opcode.Add, basis, m.Offset);
        // A reference field's address carries the field, as its loads and
        // stores do: whatever is written through it (ref, out, Interlocked)
        // is seen by no field proof, and the escape pass refuses to own it.
        if (m.Field is FieldSymbol field && TagsField(field)) _e.Block.Instrs[^1].Field = FieldKey(field);
        return address;
    }

    private VReg RegOf(Operand o)
    {
        if (o is RegOperand r)
        {
            return r.Reg;
        }
        VReg v = _f.NewReg(o.Type);
        _e.CopyTo(v, o);
        return v;
    }

    // ---- binary ---------------------------------------------------------------------------

    private VReg EmitBinary(BinaryExpr b)
    {
        Type whole = _b.TypeOf(b);

        if (_checkedDepth == 0 && Fold.TryConst(b, out long folded) && !whole.IsFloat && Fold.Fits(folded, whole))
        {
            return _e.Const(folded, IrTypes.Of(whole));
        }

        if (b.Op is BinOp.AndAlso or BinOp.OrElse)
        {
            VReg result = _f.NewReg(IrType.I32, "sc");
            Block yes = _f.NewBlock("sctrue");
            Block no = _f.NewBlock("scfalse");
            Block end = _f.NewBlock("scend");
            BranchOn(b, yes, no);
            _e.SetBlock(yes);
            _e.CopyTo(result, Imm(1, IrType.I32));
            _e.Jump(end);
            _e.SetBlock(no);
            _e.CopyTo(result, Imm(0, IrType.I32));
            _e.Jump(end);
            _e.SetBlock(end);
            return result;
        }

        Type left = _b.TypeOf(b.Left);
        Type right = _b.TypeOf(b.Right);

        if (b.Op is BinOp.Eq or BinOp.Ne
            && (left.IsNullableValue || right.IsNullableValue)
            && left.Prim != Prim.NullLiteral && right.Prim != Prim.NullLiteral)
        {
            return NullableEquality(b, left, right);
        }

        // A NULLABLE VALUE AGAINST NULL asks whether it has a value: it is a
        // struct, and never a null pointer.
        if (b.Op is BinOp.Eq or BinOp.Ne
            && (left.IsNullableValue && right.Prim == Prim.NullLiteral || right.IsNullableValue && left.Prim == Prim.NullLiteral))
        {
            VReg some = HasValue(left.IsNullableValue ? Eval(b.Left) : Eval(b.Right));
            if (left.IsNullableValue) Eval(b.Right); else Eval(b.Left);
            return b.Op == BinOp.Ne ? some : _e.Binary(Opcode.Eq, R(some), Imm(0, IrType.I32), IrType.I32);
        }

        // AND THE ORDERED ONES ARE LIFTED TOO: `n > 3` for an `int? n` is false
        // when there is no n, and the comparison of what is IN the cell when
        // there is. Left to the ordinary path it compared the cell's address.
        if (b.Op is BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
            && (left.IsNullableValue || right.IsNullableValue)
            && left.Prim != Prim.NullLiteral && right.Prim != Prim.NullLiteral)
        {
            return NullableOrder(b, left, right);
        }

        if (b.Op == BinOp.Coalesce)
        {
            return Coalesce(b, left, right, whole);
        }

        bool againstNull = b.Op is BinOp.Eq or BinOp.Ne
                        && (left.Prim == Prim.NullLiteral || right.Prim == Prim.NullLiteral);

        if (!againstNull && (left.Prim == Prim.String || right.Prim == Prim.String))
        {
            // A CHAIN IS ONE JOIN. `a + b + c + d` was three calls, each making
            // a string only to copy it into the next; flattened, the parts are
            // made text left to right, adjacent literals are joined now -- so
            // `"ab" + "cd"` IS the literal "abcd", as C# makes it -- an empty
            // one is dropped (`"" + e` for an enum is its name, the table's own
            // string, and `"" + s` is s), and one Concat makes the rest in one
            // allocation (EmitConcat).
            if (b.Op == BinOp.Add)
            {
                List<Expr> parts = new();
                ConcatParts(b, parts);
                return EmitConcat(b, parts);
            }

            // EQUALITY TAKES A NULL STRING AS IT IS. String.Equals answers for
            // one, as .NET's does; made into "" first, no name and the empty
            // name were the same name.
            bool equality = b.Op is BinOp.Eq or BinOp.Ne;
            VReg l = equality && left.Prim == Prim.String ? Eval(b.Left) : Stringify(b.Left, Eval(b.Left), left);
            VReg r = equality && right.Prim == Prim.String ? Eval(b.Right) : Stringify(b.Right, Eval(b.Right), right);
            return StringBinary(b, b.Op, l, r);
        }

        // Pointer arithmetic: an address plus a scaled count.
        if (left.IsPointer && b.Op is BinOp.Add or BinOp.Sub && right.IsInteger)
        {
            VReg p = Eval(b.Left);
            VReg n = WordOf(EvalAs(b.Right, Type.I32));
            int stride = Math.Max(1, left.Pointee?.Size ?? 1);
            VReg scaled = stride == 1 ? n : _e.Binary(Opcode.Mul, n, stride);
            return _e.Binary(b.Op == BinOp.Add ? Opcode.Add : Opcode.Sub, p, scaled);
        }

        // AND ARITHMETIC IS LIFTED: `a + b` over an `int?` is null when either
        // side is, and the sum, in a new cell, when both have a value.
        if (b.Op is BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Rem
                or BinOp.And or BinOp.Or or BinOp.Xor or BinOp.Shl or BinOp.Shr or BinOp.UShr
            && (left.IsNullableValue || right.IsNullableValue)
            && left.Prim != Prim.NullLiteral && right.Prim != Prim.NullLiteral
            && !(left.Prim == Prim.String || right.Prim == Prim.String))
        {
            return left.Prim == Prim.Bool && right.Prim == Prim.Bool && b.Op is BinOp.And or BinOp.Or
                ? NullableLogic(b, left, right)
                : NullableArith(b, left, right, _b.TypeOf(b));
        }

        if (left.IsPointer && right.IsPointer && b.Op == BinOp.Sub)
        {
            VReg diff = _e.Binary(Opcode.Sub, Eval(b.Left), Eval(b.Right));
            int stride = Math.Max(1, left.Pointee?.Size ?? 1);
            VReg elems = stride == 1 ? diff : _e.Binary(Opcode.DivS, diff, stride);
            return elems.Type == IrType.I64 ? elems : _e.Unary(Opcode.SExt32, elems);
        }

        // References, pointers, bools, enums and Type values compare as words.
        if (b.Op is BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge
            && !(left.IsNumeric && right.IsNumeric))
        {
            VReg l = Eval(b.Left);
            VReg r = Eval(b.Right);
            (l, r) = SameWidth(l, r);
            bool unsigned = left.IsPointer || right.IsPointer || left.IsReference || right.IsReference;
            return Compare(b.Op, l, r, unsigned, floating: false);
        }

        Type promoted = OperandPromotion(b.Op, left, right);
        Type rightPromoted = RightOperandPromotion(b.Op, right, promoted);
        VReg lv = EvalAs(b.Left, promoted);
        VReg rv = EvalAs(b.Right, rightPromoted);
        return Arith(b, b.Op, lv, rv, promoted);
    }

    private (VReg, VReg) SameWidth(VReg l, VReg r)
    {
        if (l.Type == r.Type)
            return (l, r);
        if (l.Type == IrType.I64)
            return (l, _e.Unary(Opcode.ZExt32, r));
        if (r.Type == IrType.I64)
            return (_e.Unary(Opcode.ZExt32, l), r);
        return (l, r);
    }

    /// <summary>`a ?? b` for references and for nullable cells.</summary>
    private VReg Coalesce(BinaryExpr b, Type left, Type right, Type result)
    {
        VReg dest = _f.NewReg(IrTypes.Of(result), "nc");
        Block got = _f.NewBlock("ncgot");
        Block miss = _f.NewBlock("ncmiss");
        Block end = _f.NewBlock("ncend");

        VReg l = Eval(b.Left);
        _e.Branch(left.IsNullableValue ? HasValue(l) : l, got, miss);

        _e.SetBlock(got);
        if (left.IsNullableValue && !result.IsNullableValue)
        {
            _e.CopyTo(dest, R(Convert(b, NullableRead(l, left), left.Underlying, result)));
        }
        else
        {
            _e.CopyTo(dest, R(l));
        }
        _e.Jump(end);

        _e.SetBlock(miss);
        VReg r = Eval(b.Right);
        if (right.Prim != Prim.String && left.Prim == Prim.String)
        {
            r = Stringify(b.Right, r, right);
        }
        else if (!_b.Boxes.Contains(b.Right))
        {
            r = Convert(b, r, right, result);
        }
        _e.CopyTo(dest, R(r));
        _e.Jump(end);
        _e.SetBlock(end);
        return dest;
    }

    /// <summary>Two nullable cells are equal when both are empty or both hold the same value.</summary>
    private VReg NullableEquality(BinaryExpr b, Type left, Type right)
    {
        Type inner = (left.IsNullableValue ? left : right).Underlying;
        VReg result = _f.NewReg(IrType.I32, "nq");
        Block same = _f.NewBlock("nqsame");
        Block differ = _f.NewBlock("nqdiff");
        Block compare = _f.NewBlock("nqcmp");
        Block end = _f.NewBlock("nqend");

        VReg l = Eval(b.Left);
        VReg r = Eval(b.Right);
        VReg lv, rv;

        if (left.IsNullableValue && right.IsNullableValue)
        {
            Block lSome = _f.NewBlock("nqls");
            Block lNone = _f.NewBlock("nqln");
            _e.Branch(HasValue(l), lSome, lNone);
            _e.SetBlock(lNone);
            _e.Branch(HasValue(r), differ, same);
            _e.SetBlock(lSome);
            _e.Branch(HasValue(r), compare, differ);
            _e.SetBlock(compare);
            lv = NullableRead(l, left);
            rv = NullableRead(r, right);
        }
        else if (left.IsNullableValue)
        {
            _e.Branch(HasValue(l), compare, differ);
            _e.SetBlock(compare);
            lv = NullableRead(l, left);
            rv = Convert(b, r, right, inner);
        }
        else
        {
            _e.Branch(HasValue(r), compare, differ);
            _e.SetBlock(compare);
            lv = Convert(b, l, left, inner);
            rv = NullableRead(r, right);
        }

        VReg eq = inner.IsFloat ? _e.Binary(Opcode.FEq, lv, rv) : _e.Binary(Opcode.Eq, lv, rv);
        _e.Branch(eq, same, differ);

        _e.SetBlock(same);
        _e.CopyTo(result, Imm(b.Op == BinOp.Eq ? 1 : 0, IrType.I32));
        _e.Jump(end);
        _e.SetBlock(differ);
        _e.CopyTo(result, Imm(b.Op == BinOp.Eq ? 0 : 1, IrType.I32));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// `<`, `>`, `<=`, `>=` with a Nullable&lt;T&gt; on either side: false unless
    /// both have a value, and then the comparison of the two values under the
    /// same promotion the plain operator would use.
    /// </summary>
    private VReg NullableOrder(BinaryExpr b, Type left, Type right)
    {
        Type leftInner = left.IsNullableValue ? left.Underlying : left;
        Type rightInner = right.IsNullableValue ? right.Underlying : right;
        Type promoted = OperandPromotion(b.Op, leftInner, rightInner);
        VReg result = _f.NewReg(IrType.I32, "no");
        Block leftHas = _f.NewBlock("nolhas");
        Block compare = _f.NewBlock("nocmp");
        Block end = _f.NewBlock("noend");

        VReg l = Eval(b.Left);
        VReg r = Eval(b.Right);

        _e.CopyTo(result, Imm(0, IrType.I32));

        if (left.IsNullableValue)
        {
            _e.Branch(HasValue(l), leftHas, end);
        }
        else
        {
            _e.Jump(leftHas);
        }

        _e.SetBlock(leftHas);

        if (right.IsNullableValue)
        {
            _e.Branch(HasValue(r), compare, end);
        }
        else
        {
            _e.Jump(compare);
        }

        _e.SetBlock(compare);

        VReg lv = left.IsNullableValue ? NullableRead(l, left) : l;
        VReg rv = right.IsNullableValue ? NullableRead(r, right) : r;

        lv = Convert(b.Left, lv, leftInner, promoted);
        rv = Convert(b.Right, rv, rightInner, promoted);
        _e.CopyTo(result, R(Compare(b.Op, lv, rv, promoted.IsUnsigned, promoted.IsFloat)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// A lifted arithmetic operator: null (the null cell) when either operand
    /// is, else the operation on what is in them, in a new cell of the result
    /// type.
    /// </summary>
    private VReg NullableArith(BinaryExpr b, Type left, Type right, Type whole)
    {
        VReg l = Eval(b.Left);
        VReg r = Eval(b.Right);
        return NullableArith(b, b.Op, l, left, r, right, whole);
    }

    private VReg NullableArith(Node b, BinOp op, VReg l, Type left, VReg r, Type right, Type whole)
    {
        Type leftInner = left.IsNullableValue ? left.Underlying : left;
        Type rightInner = right.IsNullableValue ? right.Underlying : right;
        Type promoted = OperandPromotion(op, leftInner, rightInner);
        Type rightPromoted = RightOperandPromotion(op, rightInner, promoted);
        VReg result = _f.NewReg(IrTypes.Word, "na");
        Block leftHas = _f.NewBlock("nalhas");
        Block both = _f.NewBlock("naboth");
        Block end = _f.NewBlock("naend");

        _e.CopyTo(result, R(NullableEmpty(b, whole)));

        if (left.IsNullableValue) _e.Branch(HasValue(l), leftHas, end); else _e.Jump(leftHas);
        _e.SetBlock(leftHas);
        if (right.IsNullableValue) _e.Branch(HasValue(r), both, end); else _e.Jump(both);
        _e.SetBlock(both);

        VReg lv = left.IsNullableValue ? NullableRead(l, left) : l;
        VReg rv = right.IsNullableValue ? NullableRead(r, right) : r;
        lv = Convert(b, lv, leftInner, promoted);
        rv = Convert(b, rv, rightInner, rightPromoted);
        VReg value = Convert(b, Arith(b, op, lv, rv, promoted), ResultTypeOf(op, promoted), whole.Underlying);
        _e.CopyTo(result, R(Box(b, value, whole)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// `-x` and `~x` for a nullable x: null stays null, a value is negated
    /// or complemented in a new cell.
    /// </summary>
    private VReg LiftedUnary(UnaryExpr u, Type operand, Type whole)
    {
        Type inner = operand.Underlying;
        Type promoted = NumericRules.Unary(inner);
        VReg cell = Eval(u.Operand);
        VReg result = _f.NewReg(IrTypes.Word, "nu");
        Block has = _f.NewBlock("nuhas");
        Block end = _f.NewBlock("nuend");
        _e.CopyTo(result, R(NullableEmpty(u, whole.IsNullableValue ? whole : promoted.AsNullable())));
        _e.Branch(HasValue(cell), has, end);
        _e.SetBlock(has);
        VReg v = Convert(u, NullableRead(cell, operand), inner, promoted);
        VReg made = u.Op == UnOp.Neg
            ? (promoted.IsFloat ? _e.Unary(Opcode.FNeg, v) : _e.Unary(Opcode.Neg, v))
            : Canonical(_e.Unary(Opcode.Not, v), promoted);
        Type held = whole.IsNullableValue ? whole.Underlying : promoted;
        _e.CopyTo(result, R(Box(u, Convert(u, made, promoted, held), held.AsNullable())));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// `bool?` &amp; and |, three-valued as C# has them: false &amp; anything is
    /// false and true | anything is true, even null; otherwise null if either
    /// is null. The answer is a cell, or null.
    /// </summary>
    private VReg NullableLogic(BinaryExpr b, Type left, Type right)
    {
        VReg lv = Eval(b.Left);
        VReg rv = Eval(b.Right);
        return NullableLogic(b, b.Op == BinOp.And, lv, left, rv, right);
    }

    private VReg NullableLogic(Node b, bool and, VReg lv, Type left, VReg rv, Type right)
    {
        // Each side as 0 false, 1 true, 2 null.
        VReg l = Tri(lv, left);
        VReg r = Tri(rv, right);
        VReg result = _f.NewReg(IrTypes.Word, "nl");
        Block decided = _f.NewBlock("nldec");
        Block nulled = _f.NewBlock("nlnull");
        Block end = _f.NewBlock("nlend");
        VReg answer = _f.NewReg(IrType.I32, "nlval");

        // The deciding value on either side settles it: false for &, true for |.
        VReg decisive = _e.Const(and ? 0 : 1, IrType.I32);
        VReg leftDecides = _e.Binary(Opcode.Eq, R(l), R(decisive), IrType.I32);
        VReg rightDecides = _e.Binary(Opcode.Eq, R(r), R(decisive), IrType.I32);
        VReg either = _e.Binary(Opcode.Or, leftDecides, rightDecides);
        Block check = _f.NewBlock("nlchk");
        _e.CopyTo(answer, R(decisive));
        _e.Branch(either, decided, check);

        _e.SetBlock(check);
        VReg leftNull = _e.Binary(Opcode.Eq, R(l), Imm(2, IrType.I32), IrType.I32);
        VReg rightNull = _e.Binary(Opcode.Eq, R(r), Imm(2, IrType.I32), IrType.I32);
        VReg anyNull = _e.Binary(Opcode.Or, leftNull, rightNull);
        _e.CopyTo(answer, R(_e.Const(and ? 1 : 0, IrType.I32)));
        _e.Branch(anyNull, nulled, decided);

        _e.SetBlock(nulled);
        _e.CopyTo(result, R(NullableEmpty(b, Type.Bool.AsNullable())));
        _e.Jump(end);

        _e.SetBlock(decided);
        _e.CopyTo(result, R(Box(b, answer, Type.Bool.AsNullable())));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// A bool or bool? as 0 (false), 1 (true) or 2 (null).
    private VReg Tri(VReg v, Type t)
    {
        if (!t.IsNullableValue) return v.Type == IrType.I32 ? v : Narrow(_e, v, Type.I32, Type.I32);
        VReg tri = _f.NewReg(IrType.I32, "tri");
        Block some = _f.NewBlock("trisome");
        Block end = _f.NewBlock("triend");
        _e.CopyTo(tri, Imm(2, IrType.I32));
        _e.Branch(HasValue(v), some, end);
        _e.SetBlock(some);
        _e.CopyTo(tri, R(NullableRead(v, t)));
        _e.Jump(end);
        _e.SetBlock(end);
        return tri;
    }

    /// <summary>The C# binary numeric promotion, with shifts taking their width from the left alone.</summary>
    private static Type OperandPromotion(BinOp op, Type left, Type right)
    {
        Type l = left.IsEnumValue && left.Symbol is { } enumLeft ? new Type { Prim = enumLeft.EnumUnderlying } : left;
        Type r = right.IsEnumValue && right.Symbol is { } enumRight ? new Type { Prim = enumRight.EnumUnderlying } : right;
        if (l.Prim == Prim.Bool)
            l = Type.I32;
        if (r.Prim == Prim.Bool)
            r = Type.I32;

        if (op is BinOp.Shl or BinOp.Shr or BinOp.UShr)
        {
            return NumericRules.Unary(l);
        }
        if (NumericRules.TryBinary(l, r, out Type promoted))
        {
            return promoted;
        }
        if (l.IsNumeric)
            return NumericRules.Unary(l);
        return Type.I32;
    }

    private static Type RightOperandPromotion(BinOp op, Type right, Type promoted)
        => op is BinOp.Shl or BinOp.Shr or BinOp.UShr ? Type.I32 : promoted;

    private static Type ResultTypeOf(BinOp op, Type promoted)
        => op is BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge ? Type.Bool : promoted;

    /// <summary>One arithmetic or comparison over promoted operands.</summary>
    private VReg Arith(Node at, BinOp op, VReg l, VReg r, Type operand)
    {
        bool fp = operand.IsFloat;
        bool un = operand.IsUnsigned;

        if (op is BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge)
        {
            return Compare(op, l, r, un, fp);
        }

        if (fp)
        {
            Opcode fop = op switch
            {
                BinOp.Add => Opcode.FAdd,
                BinOp.Sub => Opcode.FSub,
                BinOp.Mul => Opcode.FMul,
                BinOp.Div => Opcode.FDiv,
                _ => Opcode.FAdd,
            };
            if (op == BinOp.Rem)
            {
                MethodSymbol? rem = RequireRuntime(at, operand.Prim == Prim.F32 ? "RemainderSingle" : "Remainder", 2, "'%' on floating point");
                return rem is null ? l : _e.Call(CallLabel(rem), l.Type, R(l), R(r))!;
            }
            return _e.Binary(fop, l, r);
        }

        if (_checkedDepth > 0 && op is BinOp.Add or BinOp.Sub or BinOp.Mul)
        {
            return CheckedArith(at, op, l, r, operand);
        }

        bool wide = l.Type == IrType.I64;

        switch (op)
        {
            case BinOp.Add: return Canonical(_e.Binary(Opcode.Add, l, r), operand);
            case BinOp.Sub: return Canonical(_e.Binary(Opcode.Sub, l, r), operand);
            case BinOp.Mul: return Canonical(_e.Binary(Opcode.Mul, l, r), operand);
            case BinOp.And: return _e.Binary(Opcode.And, l, r);
            case BinOp.Or: return _e.Binary(Opcode.Or, l, r);
            case BinOp.Xor: return _e.Binary(Opcode.Xor, l, r);
            case BinOp.Shl: return Canonical(_e.Binary(Opcode.Shl, R(l), R(r), l.Type), operand);
            case BinOp.Shr: return _e.Binary(un ? Opcode.ShrU : Opcode.ShrS, R(l), R(r), l.Type);
            case BinOp.UShr: return _e.Binary(Opcode.ShrU, R(l), R(r), l.Type);

            case BinOp.Div:
            case BinOp.Rem:
            {
                DivideByZeroCheck(at, r);
                Opcode dop = op == BinOp.Div ? (un ? Opcode.DivU : Opcode.DivS) : (un ? Opcode.RemU : Opcode.RemS);
                if (!un && op == BinOp.Div && _checkedDepth > 0)
                    CheckedDivisionOverflow(l, r);
                if (!un && op == BinOp.Rem)
                {
                    // IDIV traps on MinValue/-1, but the language remainder
                    // is zero. Handle -1 before either machine or helper path.
                    VReg answer = _f.NewReg(l.Type, "remainder");
                    Block zero = _f.NewBlock("remone"), normal = _f.NewBlock("remnormal"), done = _f.NewBlock("remdone");
                    _e.Branch(_e.Binary(Opcode.Eq, R(r), Imm(-1, r.Type), IrType.I32), zero, normal);
                    _e.SetBlock(zero); _e.CopyTo(answer, Imm(0, l.Type)); _e.Jump(done);
                    _e.SetBlock(normal); _e.CopyTo(answer, R(Divide())); _e.Jump(done);
                    _e.SetBlock(done);
                    return answer;
                }
                return Divide();

                VReg Divide()
                {
                if (wide && !_t.NativeI64)
                {
                    string helper = dop switch
                    {
                        Opcode.DivS => "DivS", Opcode.DivU => "DivU", Opcode.RemS => "RemS", _ => "RemU",
                    };
                    MethodSymbol? h = RequireRuntime(at, helper, 2, "64-bit division");
                    return h is null ? l : _e.Call(CallLabel(h), IrType.I64, R(l), R(r))!;
                }
                return Canonical(_e.Binary(dop, l, r), operand);
                }
            }

            default:
                return Fail(at, $"binary '{op}' is not implemented");
        }
    }

    private void CheckedDivisionOverflow(VReg numerator, VReg divisor)
    {
        long minimum = numerator.Type == IrType.I64 ? long.MinValue : int.MinValue;
        VReg atMinimum = _e.Binary(Opcode.Eq, R(numerator), Imm(minimum, numerator.Type), IrType.I32);
        VReg minusOne = _e.Binary(Opcode.Eq, R(divisor), Imm(-1, divisor.Type), IrType.I32);
        Block fail = _f.NewBlock("divoverflow"), fine = _f.NewBlock("divfits");
        _e.Branch(_e.Binary(Opcode.And, atMinimum, minusOne), fail, fine);
        _e.SetBlock(fail);
        MethodSymbol? thrower = RuntimeMethod("Overflow", 0);
        if (thrower is not null) { Require(thrower); _e.Call(CallLabel(thrower), IrType.Void); }
        _e.Emit(Opcode.Trap, null); _e.Unreachable();
        _e.SetBlock(fine);
    }

    /// <summary>C# throws on integer division by zero; the machine would trap, but a trap is not an exception.</summary>
    private void DivideByZeroCheck(Node at, VReg divisor)
    {
        Block ok = _f.NewBlock("divok");
        Block zero = _f.NewBlock("divzero");
        _e.Branch(_e.Binary(Opcode.Ne, R(divisor), Imm(0, divisor.Type), IrType.I32), ok, zero);
        _e.SetBlock(zero);
        MethodSymbol? fail = RuntimeMethod("DivideByZero", 0);
        if (fail is not null)
        {
            Require(fail);
            _e.Call(CallLabel(fail), IrType.Void);
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
        _e.SetBlock(ok);
    }

    private VReg Compare(BinOp op, VReg l, VReg r, bool unsigned, bool floating)
    {
        Opcode oc = (op, unsigned, floating) switch
        {
            (BinOp.Eq, _, false) => Opcode.Eq,
            (BinOp.Ne, _, false) => Opcode.Ne,
            (BinOp.Lt, false, false) => Opcode.LtS,
            (BinOp.Le, false, false) => Opcode.LeS,
            (BinOp.Gt, false, false) => Opcode.GtS,
            (BinOp.Ge, false, false) => Opcode.GeS,
            (BinOp.Lt, true, false) => Opcode.LtU,
            (BinOp.Le, true, false) => Opcode.LeU,
            (BinOp.Gt, true, false) => Opcode.GtU,
            (BinOp.Ge, true, false) => Opcode.GeU,
            (BinOp.Eq, _, true) => Opcode.FEq,
            (BinOp.Ne, _, true) => Opcode.FNe,
            (BinOp.Lt, _, true) => Opcode.FLt,
            (BinOp.Le, _, true) => Opcode.FLe,
            (BinOp.Gt, _, true) => Opcode.FGt,
            _ => Opcode.FGe,
        };
        return _e.Binary(oc, R(l), R(r), IrType.I32);
    }

    /// <summary>
    /// Checked add, subtract and multiply: the operation, then the overflow
    /// test in plain arithmetic -- signed add overflows when the operands
    /// agree in sign and the result disagrees; multiply is checked by
    /// dividing back. On overflow the runtime throws.
    /// </summary>
    private VReg CheckedArith(Node at, BinOp op, VReg l, VReg r, Type operand)
    {
        bool un = operand.IsUnsigned;
        IrType t = l.Type;
        VReg result;
        VReg overflow;

        switch (op)
        {
            case BinOp.Add:
                result = _e.Binary(Opcode.Add, l, r);
                if (un)
                {
                    overflow = _e.Binary(Opcode.LtU, result, l);
                }
                else
                {
                    VReg a = _e.Binary(Opcode.Xor, result, l);
                    VReg b = _e.Binary(Opcode.Xor, result, r);
                    overflow = _e.Binary(Opcode.LtS, R(_e.Binary(Opcode.And, a, b)), Imm(0, t), IrType.I32);
                }
                break;

            case BinOp.Sub:
                result = _e.Binary(Opcode.Sub, l, r);
                if (un)
                {
                    overflow = _e.Binary(Opcode.LtU, l, r);
                }
                else
                {
                    VReg a = _e.Binary(Opcode.Xor, l, r);
                    VReg b = _e.Binary(Opcode.Xor, result, l);
                    overflow = _e.Binary(Opcode.LtS, R(_e.Binary(Opcode.And, a, b)), Imm(0, t), IrType.I32);
                }
                break;

            default:
            {
                result = _e.Binary(Opcode.Mul, l, r);
                // If r is zero the product is zero and cannot overflow; otherwise
                // dividing back must recover l exactly. The signed case also
                // refuses MIN * -1, which the division would trap on.
                VReg dest = _f.NewReg(IrType.I32, "ovf");
                Block rz = _f.NewBlock("mulrz");
                Block rn = _f.NewBlock("mulrn");
                Block end = _f.NewBlock("mulend");
                _e.Branch(_e.Binary(Opcode.Eq, R(r), Imm(0, t), IrType.I32), rz, rn);
                _e.SetBlock(rz);
                _e.CopyTo(dest, Imm(0, IrType.I32));
                _e.Jump(end);
                _e.SetBlock(rn);
                // Check before the reverse division: an I32 IDIV would
                // otherwise fault before the later overflow predicate runs.
                if (!un) CheckedDivisionOverflow(result, r);
                VReg back;
                if (t == IrType.I64 && !_t.NativeI64)
                {
                    MethodSymbol? h = RequireRuntime(at, un ? "DivU" : "DivS", 2, "checked multiplication");
                    back = h is null ? result : _e.Call(CallLabel(h), IrType.I64, R(result), R(r))!;
                }
                else
                {
                    back = _e.Binary(un ? Opcode.DivU : Opcode.DivS, result, r);
                }
                VReg bad = _e.Binary(Opcode.Ne, back, l);
                if (!un)
                {
                    long min = t == IrType.I64 ? long.MinValue : int.MinValue;
                    VReg lMin = _e.Binary(Opcode.Eq, R(l), Imm(min, t), IrType.I32);
                    VReg rNeg = _e.Binary(Opcode.Eq, R(r), Imm(-1, t), IrType.I32);
                    bad = _e.Binary(Opcode.Or, bad, _e.Binary(Opcode.And, lMin, rNeg));
                }
                _e.CopyTo(dest, R(bad));
                _e.Jump(end);
                _e.SetBlock(end);
                overflow = dest;
                break;
            }
        }

        // Narrow types overflow when the canonical value does not round-trip.
        if (operand.Prim is Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char)
        {
            VReg narrowed = Canonical(result, operand);
            overflow = _e.Binary(Opcode.Or, overflow, _e.Binary(Opcode.Ne, narrowed, result));
            result = narrowed;
        }

        Block fine = _f.NewBlock("ckok");
        Block fail = _f.NewBlock("ckovf");
        _e.Branch(overflow, fail, fine);
        _e.SetBlock(fail);
        MethodSymbol? thrower = RuntimeMethod("Overflow", 0);
        if (thrower is not null)
        {
            Require(thrower);
            _e.Call(CallLabel(thrower), IrType.Void);
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();
        _e.SetBlock(fine);
        return result;
    }

    // ---- strings ----------------------------------------------------------------------------

    /// <summary>The standard library's one-character-as-a-string, what a char joined to a string becomes.</summary>
    private const string FromCharMethod = "FromByte";

    /// <summary>Whether the linked String provides this, without complaining
    /// when it does not.</summary>
    private bool HasStringMethod(string method)
        => _b.Types.TryGetValue(Prelude.StringType, out TypeSymbol? type)
        && type.Methods.Any(m => m.Name == method && m.Static && m.Params.Count == 1);

    private MethodSymbol? StringMethod(Node at, string method, int argCount, string because)
    {
        if (_b.Types.TryGetValue(Prelude.StringType, out TypeSymbol? type))
        {
            // THE ALL-STRING OVERLOAD when there are several of the arity:
            // Concat has (string, string) beside (object?, object?) and a
            // span pair, and `a + b` means the first.
            List<MethodSymbol> named = type.Methods
                .Where(m => m.Name == method && m.Static && m.Params.Count == argCount).ToList();
            MethodSymbol? found = named.FirstOrDefault(m => m.Params.All(p => p.Type.Prim == Prim.String))
                                  ?? named.FirstOrDefault();
            if (found is not null)
            {
                Require(found);
                return found;
            }
        }
        Error(at, $"{because} needs {Prelude.StringType}.{method}, which no compiled source provides; compile with the standard library (lib/std.cor)");
        return null;
    }

    /// <summary>'+' and the comparisons on strings are calls into the standard library's String.</summary>
    private VReg StringBinary(Node at, BinOp op, VReg l, VReg r)
    {
        if (op is not (BinOp.Add or BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Gt or BinOp.Le or BinOp.Ge))
        {
            return Fail(at, $"'{op}' has no meaning for strings");
        }

        string what = op == BinOp.Add ? "joining strings with '+'" : $"comparing strings with '{op}'";
        MethodSymbol? routine = StringMethod(at, op == BinOp.Add ? Prelude.ConcatMethod : Prelude.CompareMethod, 2, what);
        if (routine is null)
        {
            return l;
        }

        if (op is BinOp.Eq or BinOp.Ne && StringEqualsRoutine() is MethodSymbol equals)
        {
            VReg same = _e.Call(CallLabel(equals), IrTypes.Of(equals.Returns), R(l), R(r))!;

            return op == BinOp.Eq ? same : _e.Binary(Opcode.Eq, R(same), Imm(0, same.Type), IrType.I32);
        }

        VReg answer = _e.Call(CallLabel(routine), IrTypes.Of(routine.Returns), R(l), R(r))!;
        if (op == BinOp.Add)
        {
            return answer;
        }

        // The routine answers -1, 0 or 1; the operator turns that into a bool.
        VReg zero = _e.Const(0, answer.Type);
        return Compare(op, answer, zero, unsigned: false, floating: false);
    }

    /// <summary>
    /// String.Equals(string, string), when the library compiled has one: the
    /// routine that takes a null. A freestanding program with its own String
    /// and no Equals falls back to Compare, as it always did.
    /// </summary>
    private MethodSymbol? StringEqualsRoutine()
    {
        if (!_b.Types.TryGetValue(Prelude.StringType, out TypeSymbol? type))
        {
            return null;
        }

        MethodSymbol? found = type.Methods.FirstOrDefault(
            m => m.Name == "Equals" && m.Static && m.Params.Count == 2
              && m.Params.All(p => p.Type.Prim == Prim.String));

        if (found is not null)
        {
            Require(found);
        }
        return found;
    }

    private VReg StringEquals(Node at, VReg a, VReg b)
    {
        if (StringEqualsRoutine() is MethodSymbol equals)
        {
            return _e.Call(CallLabel(equals), IrTypes.Of(equals.Returns), R(a), R(b))!;
        }

        MethodSymbol? routine = StringMethod(at, Prelude.CompareMethod, 2, "comparing strings in a switch arm");
        if (routine is null)
        {
            return _e.Const(0, IrType.I32);
        }
        VReg answer = _e.Call(CallLabel(routine), IrTypes.Of(routine.Returns), R(a), R(b))!;
        return _e.Binary(Opcode.Eq, R(answer), Imm(0, answer.Type), IrType.I32);
    }

    /// <summary>
    /// The operands of a chain of string joins, left to right: a `+` whose
    /// either side is a string and that the checker did not make a call of (a
    /// user-defined operator) is a join, and its operands are the chain's.
    /// </summary>
    private void ConcatParts(Expr e, List<Expr> parts)
    {
        if (e is BinaryExpr { Op: BinOp.Add } join && !_b.Rewrites.ContainsKey(join)
            && (_b.TypeOf(join.Left).Prim == Prim.String || _b.TypeOf(join.Right).Prim == Prim.String))
        {
            ConcatParts(join.Left, parts);
            ConcatParts(join.Right, parts);
            return;
        }
        parts.Add(e);
    }

    /// <summary>
    /// A flattened chain of joins (ConcatParts): adjacent string literals
    /// joined here, as C# joins constant strings; an empty literal dropped;
    /// every other part made text in order (Stringify); and the whole made
    /// once -- String.Concat of two, three or four, or of an array beyond
    /// that, which does not outlive the call and so is a frame slot, not a
    /// heap object (Escape).
    /// </summary>
    private VReg EmitConcat(Node at, List<Expr> parts)
    {
        List<object> pieces = new();       // string: a literal's text; VReg: a part made text
        foreach (Expr part in parts)
        {
            if (part is LiteralExpr { Kind: Lit.Str } literal && !_b.Rewrites.ContainsKey(part))
            {
                if (pieces.Count > 0 && pieces[^1] is string before)
                    pieces[^1] = before + literal.Text;
                else
                    pieces.Add(literal.Text);
                continue;
            }
            pieces.Add(Stringify(part, Eval(part), _b.TypeOf(part)));
        }
        pieces.RemoveAll(p => p is "");
        if (pieces.Count == 0)
        {
            return _e.Address(InternString(""));
        }
        List<VReg> made = pieces.Select(p => p is string text ? _e.Address(InternString(text)) : (VReg)p).ToList();
        if (made.Count == 1)
        {
            return made[0];
        }
        if (made.Count == 2)
        {
            return StringBinary(at, BinOp.Add, made[0], made[1]);
        }
        if (made.Count <= 4 && StringMethod(at, Prelude.ConcatMethod, made.Count, "joining strings with '+'") is MethodSymbol some)
        {
            return _e.Call(CallLabel(some), IrTypes.Of(some.Returns), made.Select(R).ToArray())!;
        }
        MethodSymbol? many = _b.Types.TryGetValue(Prelude.StringType, out TypeSymbol? strings)
            ? strings.Methods.FirstOrDefault(m => m.Name == Prelude.ConcatMethod && m.Static && m.Params.Count == 1
                                                && m.Params[0].Type is { IsArray: true, Element.Prim: Prim.String })
            : null;
        if (many is null)
        {
            VReg joined = made[0];
            for (int i = 1; i < made.Count; i++) joined = StringBinary(at, BinOp.Add, joined, made[i]);
            return joined;
        }
        Require(many);
        VReg array = AllocateArray(at, _e.Const(made.Count, IrType.I32), Type.String);
        for (int i = 0; i < made.Count; i++)
        {
            _e.Store(R(array), R(made[i]), _t.ArrayHeaderBytes + i * _t.WordSize, _t.WordSize);
        }
        return _e.Call(CallLabel(many), IrTypes.Of(many.Returns), R(array))!;
    }


    /// <summary>A non-string operand of a string expression rendered as text.</summary>
    private VReg Stringify(Expr at, VReg v, Type type)
    {
        if (type.Prim == Prim.String)
        {
            // A NULL STRING JOINED TO ANOTHER IS THE EMPTY STRING, which is
            // what C# prints -- `"x " + (string)null` is "x ", not a fault on
            // reading null's length.
            //
            // Asked HERE and not inside String.Concat, because nullability is
            // explicit in this language and the type already says which of the
            // two this is. Concat is small enough to be inlined at every join,
            // so two tests inside it are two tests at every join in the
            // program: on the kernel that was twenty-three kilobytes of code
            // to ask a question the type had already answered. A plain
            // `string` now costs nothing and a `string?` costs the one test
            // C# would have made you write.
            return type.Nullable ? OrEmpty(v) : v;
        }

        if (type.Prim == Prim.Any || type.ParamName is not null
            || type.Symbol is { Kind: TypeKind.Class or TypeKind.Interface })
        {
            return ObjectString(v);
        }

        // A NULLABLE VALUE IS ITS VALUE, OR NOTHING AT ALL. `"n " + n` for an
        // empty `int?` is "n " in C#, and for a full one the number -- and
        // a Nullable<T> here is a CELL, so without this the address of the
        // cell was rendered as a number and an empty one rendered as zero.
        if (type.IsNullableValue)
        {
            Type inner = type.Underlying;
            VReg result = _f.NewReg(IrTypes.Word, "nts");
            Block some = _f.NewBlock("ntssome");
            Block none = _f.NewBlock("ntsnone");
            Block end = _f.NewBlock("ntsend");

            _e.Branch(HasValue(v), some, none);
            _e.SetBlock(some);
            _e.CopyTo(result, R(Stringify(at, NullableRead(v, type), inner)));
            _e.Jump(end);
            _e.SetBlock(none);
            _e.CopyTo(result, R(_e.Address(InternString(""))));
            _e.Jump(end);
            _e.SetBlock(end);
            return result;
        }

        // A STRUCT SAYS WHAT ITS OWN ToString SAYS, and its type's name when
        // it declared none -- which is what object.ToString answers for
        // everything else here. `"at " + point` is ordinary C# and was
        // refused outright; a boxed struct already reached the struct's
        // ToString, and an unboxed one has to reach the same one.
        //
        // The call is direct because a struct has no vtable to dispatch
        // through, and the register holding a struct holds its address, which
        // is what the method wants for `this`.
        if (type.Symbol is { Kind: TypeKind.Struct } shape && !type.IsNullableValue)
        {
            MethodSymbol? own = shape.FindMethods("ToString")
                                     .FirstOrDefault(m => !m.Static && m.Params.Count == 0);

            if (own is null)
            {
                return _e.Address(InternString(shape.Name));
            }

            Require(own);
            return _e.Call(CallLabel(own), IrTypes.Word, R(v))!;
        }

        if (!type.IsInteger && type.Prim != Prim.Bool && !type.IsFloat
            && type.Symbol is not { Kind: TypeKind.Enum })
        {
            // AN ARRAY SAYS WHAT IT IS, which is the whole of what
            // object.ToString has to say about one: .NET prints
            // `System.Byte[]`, and a record with an array member prints that
            // in the ToString the compiler writes for it. Nothing on the heap
            // here carries a name for an array and nothing has to -- the type
            // is known where the join is written.
            if (type.IsArray && type.Element is Type element)
            {
                string spelt = RuntimeName(element);

                for (int i = 0; i < Math.Max(1, type.ArrayRank); i++)
                {
                    spelt += "[]";
                }
                return _e.Address(InternString(spelt));
            }

            return Fail(at, $"'{type}' cannot be joined to a string" + (!Switches.TraceJoin ? "" : " in " + _f.Name + " prim=" + type.Prim + " sym=" + type.Symbol?.Name + " param=" + type.ParamName + " nullable=" + type.Nullable));
        }

        // AN ENUM IS ITS MEMBER'S NAME, which .NET reads out of the type's
        // metadata and this reads out of the table the code generator wrote
        // for it -- see Lowering.Enum.cs. Before the integer arm, because an
        // enum IS an integer here and would otherwise print as one, which is
        // exactly how `"x " + Colour.Green` came to say "x 5".
        if (type.Symbol is { Kind: TypeKind.Enum } named && EnumText(at, v, named) is VReg text)
        {
            return text;
        }

        // A CHAR CONTRIBUTES ITS CHARACTER, NOT ITS CODE. `"x" + c` is "xb" in
        // C#, not "x98": char is an integer type here, so without this it would
        // take the number path and render the code point as digits.
        bool asChar = type.Prim == Prim.Char;
        bool asBool = type.Prim == Prim.Bool;
        bool asReal = type.IsFloat;

        // AN UNSIGNED SIXTY-FOUR-BIT NUMBER IS NOT A SIGNED ONE. Everything
        // narrower fits in a long with its value intact, so the signed
        // formatter is exact for it; a ulong with its top bit set does not,
        // and reads as negative. Falls back where no library provides the
        // unsigned formatter, which is what a freestanding build with its own
        // minimal String does.
        bool asUnsigned = type.Prim is Prim.U64
                       || (type.Prim is Prim.NUInt && _t.WordSize == 8);
        string method = asChar ? FromCharMethod
                      : asBool ? Prelude.FromBoolMethod
                      : asReal && type.Prim == Prim.F32 && HasStringMethod(Prelude.FromSingleMethod) ? Prelude.FromSingleMethod
                      : asReal ? Prelude.FromDoubleMethod
                      : asUnsigned && HasStringMethod(Prelude.FromUIntMethod) ? Prelude.FromUIntMethod
                      : Prelude.FromIntMethod;
        string because = asChar ? "joining a char to a string"
                       : asBool ? "joining a bool to a string"
                       : asReal ? "joining a floating-point number to a string"
                       : "joining a number to a string";
        MethodSymbol? render = StringMethod(at, method, 1, because);
        if (render is null)
        {
            return v;
        }
        VReg arg = Convert(at, v, type, render.Params[0].Type);
        return _e.Call(CallLabel(render), IrTypes.Word, R(arg))!;
    }

    /// <summary>A string, or the empty string when it is null.</summary>
    private VReg OrEmpty(VReg v)
    {
        VReg result = _f.NewReg(IrTypes.Word, "oes");
        Block none = _f.NewBlock("oesnone");
        Block end = _f.NewBlock("oesend");

        _e.CopyTo(result, R(v));
        _e.Branch(v, end, none);
        _e.SetBlock(none);
        _e.CopyTo(result, R(_e.Address(InternString(""))));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>What an object's ToString says, through the shared slot; null renders as "".</summary>
    private VReg ObjectString(VReg obj)
    {
        VReg result = _f.NewReg(IrTypes.Word, "ts");
        Block some = _f.NewBlock("tssome");
        Block none = _f.NewBlock("tsnone");
        Block text = _f.NewBlock("tstext");
        Block call = _f.NewBlock("tscall");
        Block end = _f.NewBlock("tsend");
        _e.Branch(obj, some, none);
        _e.SetBlock(some);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);

        // A STRING HELD AS AN OBJECT IS ITS OWN ToString, which is what .NET
        // says and what this has to say too: a string has no vtable at all --
        // its first word points at the descriptor every string shares, and
        // the slot behind that is somebody else's data. Reading it and calling
        // through it is how `object b = "hi"; "b " + b` crashed.
        VReg flags = _e.Load(IrType.I32, vt, -_t.DescriptorBytes + DescFlags * _t.WordSize);
        _e.Branch(_e.Binary(Opcode.And, flags, 2), text, call);
        _e.SetBlock(text);
        _e.CopyTo(result, R(obj));
        _e.Jump(end);

        _e.SetBlock(call);
        VReg fn = _e.Load(IrTypes.Word, vt, (long)_b.ToStringSlot * _t.WordSize);
        VReg said = _e.CallIndirect(R(fn), IrTypes.Word, new Operand[] { R(obj) })!;
        // Declared on object: every type's ToString slot (Escape.IndirectTargets).
        _e.Block.Instrs[^1].DispatchType = ObjectDispatch;
        _e.CopyTo(result, R(said));
        _e.Jump(end);
        _e.SetBlock(none);
        _e.CopyTo(result, R(_e.Address(InternString(""))));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }
}
