#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using Type = Corsac.Lang.Type;

/// <summary>
/// BOXING: a value type becoming an object, and coming back out again.
///
/// `object` is a machine word here, and for a long time that is all it was:
/// `object a = 5` put five in the word and `"x " + a` read five as an address
/// and walked off the end of the world. C# says a value converted to object is
/// COPIED ONTO THE HEAP behind an ordinary object header, so that the thing in
/// the word is a real object -- one with a type, a ToString, an Equals -- and
/// that is what this builds.
///
/// A box is the smallest object there is: the header word pointing at a
/// descriptor, and the value after it. The descriptor is synthesised here, one
/// per boxed type, and carries
///
///   * the TYPE'S OWN NAME, so `o.GetType().Name` on a boxed int says "int"
///     rather than naming a wrapper the program never wrote;
///   * a ToString that renders the value, so joining a boxed number to a
///     string prints the number -- through the shared slot, which is what
///     makes `"x " + (object)2.5` work without the joining code knowing what
///     it is holding;
///   * an Equals that compares the VALUES, because two separately boxed fives
///     are equal in C# and comparing the two addresses says they are not.
///
/// Unboxing is a cast back, and it is EXACT: the descriptor in the object has
/// to be the descriptor of the very type being asked for. `object o = (short)5;
/// (int)o` throws in C# -- there is no widening on the way out of a box -- and
/// it throws here, through the same runtime routine an ordinary bad cast uses.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>Whether converting this type to object means copying it onto the heap.</summary>
    private static bool Boxable(Type t)
    {
        if (t.IsReference || t.IsArray || t.IsPointer || t.ParamName is not null
            || t.IsNullableValue || t.Prim is Prim.Any or Prim.NullLiteral
                                          or Prim.String or Prim.Type or Prim.Error)
        {
            return false;
        }

        // A STRUCT AND AN ENUM ARE ASKED BY THEIR SYMBOL, not by their Prim:
        // a struct's Prim is Void, because the void is what a machine word is
        // not, and testing that first left every user-defined struct unboxed.
        if (t.Symbol is { Kind: TypeKind.Enum or TypeKind.Struct })
        {
            return true;
        }
        return (t.IsNumeric || t.Prim == Prim.Bool) && t.Prim != Prim.Void;
    }

    /// <summary>Whether a boxed value of this type is a block of bytes rather than a number.</summary>
    private static bool BoxedBlock(Type t) => t.Symbol is { Kind: TypeKind.Struct };

    /// <summary>How many bytes a box of this type holds after the header.</summary>
    private static int BoxPayload(Type t)
        => BoxedBlock(t) ? Math.Max(1, t.Symbol!.InstanceSize) : Math.Max(1, t.Size);

    /// <summary>What a boxed value calls itself: the name C# would print for the type.</summary>
    private static string BoxName(Type t)
        => t.Symbol is { Kind: TypeKind.Enum or TypeKind.Struct } named ? named.Name : t.ToString();

    /// <summary>The register width a boxed value is kept and compared in.</summary>
    private static IrType BoxSlot(Type t)
        => t.Symbol is { Kind: TypeKind.Enum } ? IrType.I32
         : BoxedBlock(t) ? IrTypes.Word
         : IrTypes.Of(t);

    private readonly Dictionary<string, string> _boxes = new(StringComparer.Ordinal);

    /// <summary>A value copied onto the heap behind an object header.</summary>
    private VReg BoxValue(Node at, VReg value, Type of)
    {
        int payload = _t.ObjectHeaderBytes;
        int bytes = BoxPayload(of);
        VReg obj = Allocate(at, payload + Math.Max(_t.WordSize, bytes));
        _e.Store(R(obj), new SymOperand(BoxDescriptor(of), _t.DescriptorBytes), 0, _t.WordSize);

        // A STRUCT IS A BLOCK, and the register holding one holds its address:
        // the bytes are copied in behind the header, which is what makes the
        // box a copy rather than a second name for the caller's variable.
        if (BoxedBlock(of))
        {
            VReg into = _e.Binary(Opcode.Add, obj, payload);
            _e.Emit(Opcode.MemCopy, null, R(into), R(value), Imm(bytes, IrTypes.Word));
            // A struct inside it is a block of its own: the box gets a copy,
            // not the caller's.
            foreach (FieldSymbol f in of.Symbol!.Fields)
            {
                if (f.Static || f.Boxed || !IsStructValue(f.Type))
                {
                    continue;
                }
                VReg own = CopyStruct(at, _e.Load(IrTypes.Word, into, f.Offset), f.Type.Symbol!);
                _e.Store(R(into), R(own), f.Offset, _t.WordSize);
            }
            return obj;
        }

        _e.Store(R(obj), R(value), payload, bytes);
        return obj;
    }

    /// <summary>
    /// The value back out of a box, having checked it is a box of exactly
    /// this type. Null and anything else go to the runtime's bad-cast
    /// routine, which is where an ordinary failed reference cast goes too.
    /// </summary>
    private VReg Unbox(Node at, VReg obj, Type want)
    {
        Block check = _f.NewBlock("unbck");
        Block ok = _f.NewBlock("unbok");
        Block bad = _f.NewBlock("unbbad");

        _e.Branch(obj, check, bad);
        _e.SetBlock(check);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg wanted = _e.Address(BoxDescriptor(want), _t.DescriptorBytes);
        _e.Branch(_e.Binary(Opcode.Eq, vt, wanted), ok, bad);

        _e.SetBlock(bad);
        MethodSymbol? fail = RuntimeMethod("InvalidCast", 1);
        if (fail is not null)
        {
            Require(fail);
            _e.Call(CallLabel(fail), IrType.Void, R(obj));
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();

        _e.SetBlock(ok);

        // Unboxing a struct is a COPY, as it is in C#: what comes out is the
        // caller's own block, so writing to it does not write through to the
        // box somebody else may still be holding.
        if (BoxedBlock(want))
        {
            VReg inside = _e.Binary(Opcode.Add, obj, _t.ObjectHeaderBytes);
            return CopyStruct(at, inside, want.Symbol!);
        }

        return _e.Load(BoxSlot(want), obj, _t.ObjectHeaderBytes, Math.Max(1, want.Size),
                       !want.IsUnsigned && want.Prim != Prim.Bool);
    }

    /// <summary>Whether a value of this type, held in an object, is a box of that type.</summary>
    private VReg BoxTest(VReg obj, Type want)
    {
        VReg result = _f.NewReg(IrType.I32, "isbox");
        Block some = _f.NewBlock("bxsome");
        Block end = _f.NewBlock("bxend");
        _e.CopyTo(result, Imm(0, IrType.I32));
        _e.Branch(obj, some, end);
        _e.SetBlock(some);
        VReg vt = _e.Load(IrTypes.Word, obj, 0);
        VReg wanted = _e.Address(BoxDescriptor(want), _t.DescriptorBytes);
        _e.CopyTo(result, R(_e.Binary(Opcode.Eq, R(vt), R(wanted), IrType.I32)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>The descriptor and vtable shared by every box of one type.</summary>
    private string? _objectDescriptor;

    /// <summary>
    /// The descriptor of a bare `new object()`: an object with nothing in it
    /// but its header, what C# programs make to lock on. It derives from
    /// nothing and implements nothing, so like a box it has a display of
    /// itself alone and an empty interface list, and every virtual slot is
    /// object's own -- ToString answers "System.Object", as .NET does.
    /// Structural and shared, so every unit's bare object is one type.
    /// </summary>
    private string ObjectDescriptor()
    {
        if (_objectDescriptor is { } made)
        {
            return made;
        }

        const string sym = "t_System_Object";
        _objectDescriptor = sym;

        int w = _t.WordSize;
        int slots = Math.Max(Math.Max(_b.ToStringSlot, _b.CompareSlot), Math.Max(_b.EqualsSlot, _b.HashSlot)) + 1;
        byte[] block = new byte[_t.DescriptorBytes + slots * w];
        WriteWord(block, DescSize * w, _t.ObjectHeaderBytes);
        WriteWord(block, DescDepth * w, 0);
        WriteWord(block, DescPayload * w, _t.ObjectHeaderBytes);

        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, FromLibrary = true, Coalescible = true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString("System.Object"), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));

        DataItem display = new("td_System_Object", new byte[w]) { ReadOnly = true, Exported = false };
        display.Relocs.Add(new DataReloc(0, sym, 0));
        _m.Data.Add(display);
        item.Relocs.Add(new DataReloc(DescDisplay * w, display.Name, 0));

        DataItem faces = new("tf_System_Object", new byte[w]) { ReadOnly = true, Exported = false };
        _m.Data.Add(faces);
        item.Relocs.Add(new DataReloc(DescInterfaces * w, faces.Name, 0));

        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.ToStringSlot * w, ObjectToStringStub(), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.EqualsSlot * w, ObjectEqualsStub(), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.HashSlot * w, ObjectHashStub(), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.CompareSlot * w, ObjectCompareStub(), 0));
        return sym;
    }

    private string BoxDescriptor(Type of)
    {
        string name = BoxName(of);

        if (_boxes.TryGetValue(name, out string? sym))
        {
            return sym;
        }

        sym = "b_" + Safe(name);
        _boxes[name] = sym;

        int w = _t.WordSize;
        int slots = Math.Max(Math.Max(_b.ToStringSlot, _b.CompareSlot), Math.Max(_b.EqualsSlot, _b.HashSlot)) + 1;
        // A STRUCT'S INTERFACES ARE ANSWERED BY ITS BOX: `IEnumerator<int> e =
        // list.GetEnumerator()` boxes List<int>.Enumerator, and a call through
        // the interface reaches the struct's own member through the box's
        // table, with the value inside the box as its `this` (C# 8.2.4).
        TypeSymbol? shape = BoxedBlock(of) ? of.Symbol : null;
        if (shape is not null)
        {
            foreach (int interfaceSlot in shape.InterfaceImplementations.Keys) slots = Math.Max(slots, interfaceSlot + 1);
        }
        byte[] block = new byte[_t.DescriptorBytes + slots * w];
        WriteWord(block, DescSize * w, _t.ObjectHeaderBytes + Math.Max(w, BoxPayload(of)));
        WriteWord(block, DescDepth * w, 0);
        WriteWord(block, DescPayload * w, _t.ObjectHeaderBytes);

        // Structural, like a sequence descriptor: a boxed int is one type
        // across the whole process, so the library's copy is used where
        // there is one.
        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, FromLibrary = true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString(name), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));

        // The display and the interface list a class descriptor has, at their
        // smallest: a box derives from object and implements nothing, so the
        // display holds only itself and the interface array is the terminator
        // alone. They exist because `is` and `as` read them without knowing
        // what they are looking at.
        DataItem display = new("bd_" + Safe(name), new byte[w]) { ReadOnly = true, Exported = false };
        display.Relocs.Add(new DataReloc(0, sym, 0));
        _m.Data.Add(display);
        item.Relocs.Add(new DataReloc(DescDisplay * w, display.Name, 0));

        // A boxed struct lists every interface it implements, so `is` and
        // `as` find them; anything else implements none.
        List<TypeSymbol> implemented = new();
        if (shape is not null)
        {
            foreach (TypeSymbol i in shape.Interfaces) AddInterfaceClosure(i, implemented);
            implemented.Sort((a, b) => string.CompareOrdinal(InterfaceDescriptor(a), InterfaceDescriptor(b)));
        }
        DataItem faces = new("bf_" + Safe(name), new byte[(implemented.Count + 1) * w]) { ReadOnly = true, Exported = false };
        for (int i = 0; i < implemented.Count; i++) faces.Relocs.Add(new DataReloc(i * w, InterfaceDescriptor(implemented[i]), 0));
        _m.Data.Add(faces);
        item.Relocs.Add(new DataReloc(DescInterfaces * w, faces.Name, 0));

        if (shape is not null)
        {
            foreach (var implementation in shape.InterfaceImplementations)
            {
                if (implementation.Value.Abstract) continue;
                item.Relocs.Add(new DataReloc(_t.DescriptorBytes + implementation.Key * w,
                    BoxInterfaceStub(implementation.Value, name), 0));
            }
        }

        // A BOXED STRUCT'S OWN POINTERS. The collector holds the box, not the
        // struct, so the map has to cover the whole object -- the header word
        // first, then each of the struct's fields at its offset within it.
        if (BoxedBlock(of))
        {
            List<(int, Type)> held = of.Symbol!.Fields
                .Where(fd => !fd.Static)
                .Select(fd => (_t.ObjectHeaderBytes + fd.Offset, fd.Type))
                .ToList();
            string? map = ReferenceMap("box_" + Safe(name), held,
                                       _t.ObjectHeaderBytes + BoxPayload(of));
            if (map is not null)
            {
                item.Relocs.Add(new DataReloc(DescRefMap * w, map, 0));
            }
        }

        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.ToStringSlot * w, BoxToString(of, name), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.EqualsSlot * w, BoxEquals(of, name), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.HashSlot * w, BoxHash(of, name), 0));
        item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.CompareSlot * w, ObjectCompareStub(), 0));
        return sym;
    }

    /// <summary>
    /// A struct's member called through an interface on its box: the same
    /// parameters, with `this` moved past the box's header onto the value.
    /// </summary>
    private string BoxInterfaceStub(MethodSymbol m, string name)
    {
        string target = CallLabel(m);
        string label = "__box_call_" + Safe(name) + "_" + target;
        if (_boxStubs.Contains(label)) return label;
        _boxStubs.Add(label);
        Require(m);

        IrType returns = IrTypes.Of(m.Returns);
        Function f = new(label, returns) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        List<VReg> args = new();
        foreach (ParamSymbol p in m.Params)
        {
            VReg a = f.NewReg(IrTypes.Of(p.Type), p.Name);
            f.Params.Add(a);
            args.Add(a);
        }
        Builder e = new(f, f.NewBlock("entry"));
        args.Insert(0, e.Binary(Opcode.Add, self, _t.ObjectHeaderBytes));
        VReg? result = e.Call(target, returns, args);
        e.Ret(result is null ? null : new RegOperand(result));
        _m.Functions.Add(f);
        return label;
    }

    private readonly HashSet<string> _boxStubs = new(StringComparer.Ordinal);

    /// <summary>A boxed value rendered the way the library renders its type.</summary>
    private string BoxToString(Type of, string name)
    {
        string label = "__box_tostring_" + Safe(name);
        Function f = new(label, IrTypes.Word) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        Builder e = new(f, f.NewBlock("entry"));

        Function savedF = _f;
        Builder savedE = _e;
        _f = f;
        _e = e;

        if (BoxedBlock(of))
        {
            // A STRUCT SAYS WHAT IT DECLARED IT SAYS, and its type's name when
            // it declared nothing -- which is what object.ToString answers for
            // every other type here.
            MethodSymbol? own = of.Symbol!.FindMethods("ToString")
                .FirstOrDefault(m => !m.Static && m.Params.Count == 0);
            VReg inside = e.Binary(Opcode.Add, self, _t.ObjectHeaderBytes);

            if (own is not null)
            {
                Require(own);
                e.Ret(new RegOperand(e.Call(CallLabel(own), IrTypes.Word, R(inside))!));
            }
            else
            {
                e.Ret(new RegOperand(e.Address(InternString(name))));
            }
        }
        else
        {
            VReg raw = e.Load(BoxSlot(of), self, _t.ObjectHeaderBytes, Math.Max(1, of.Size),
                              !of.IsUnsigned && of.Prim != Prim.Bool);
            // Straight into the same rendering a written `"" + value` reaches,
            // so a boxed double prints what an unboxed one prints.
            VReg text = Stringify(new LiteralExpr { Kind = Lit.Int, Text = "0", Line = 0, Col = 0 }, raw, of);
            e.Ret(new RegOperand(text));
        }

        _f = savedF;
        _e = savedE;
        _m.Functions.Add(f);
        return label;
    }

    /// <summary>Two boxes are equal when they hold the same type and the same value.</summary>
    private string BoxEquals(Type of, string name)
    {
        string label = "__box_equals_" + Safe(name);
        Function f = new(label, IrType.I32) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        VReg other = f.NewReg(IrTypes.Word, "other");
        f.Params.Add(self);
        f.Params.Add(other);
        Builder e = new(f, f.NewBlock("entry"));

        Block same = f.NewBlock("bxsame");
        Block no = f.NewBlock("bxno");
        e.Branch(other, same, no);
        e.SetBlock(same);

        VReg mine = e.Load(IrTypes.Word, self, 0);
        VReg theirs = e.Load(IrTypes.Word, other, 0);
        Block values = f.NewBlock("bxvals");
        e.Branch(e.Binary(Opcode.Eq, mine, theirs), values, no);

        e.SetBlock(values);

        if (BoxedBlock(of))
        {
            // TWO STRUCTS ARE EQUAL WHEN THEIR BYTES ARE, which is what .NET's
            // own default for a struct with no Equals of its own comes to for
            // a type holding no references. A struct that declares Equals is
            // asked instead.
            MethodSymbol? own = of.Symbol!.FindMethods("Equals")
                .FirstOrDefault(m => !m.Static && m.Params.Count == 1);
            VReg mine2 = e.Binary(Opcode.Add, self, _t.ObjectHeaderBytes);
            VReg theirs2 = e.Binary(Opcode.Add, other, _t.ObjectHeaderBytes);

            if (own is not null && own.Params[0].Type.Symbol == of.Symbol)
            {
                Require(own);
                e.Ret(new RegOperand(e.Call(CallLabel(own), IrType.I32, R(mine2), R(theirs2))!));
                e.SetBlock(no);
                e.Ret(new ImmOperand(0, IrType.I32));
                _m.Functions.Add(f);
                return label;
            }

            int bytes = BoxPayload(of);
            VReg at = f.NewReg(IrType.I32, "bxat");
            e.CopyTo(at, new ImmOperand(0, IrType.I32));
            Block step = f.NewBlock("bxstep");
            Block more = f.NewBlock("bxmore");
            Block same2 = f.NewBlock("bxeq");
            e.Jump(step);
            e.SetBlock(step);
            e.Branch(e.Binary(Opcode.LtS, at, bytes), more, same2);
            e.SetBlock(more);
            VReg off = IrTypes.Word == IrType.I32 ? at : e.Unary(Opcode.SExt32, at);
            VReg x = e.Load(IrType.I32, e.Binary(Opcode.Add, R(mine2), R(off), IrTypes.Word), 0, 1, false);
            VReg y = e.Load(IrType.I32, e.Binary(Opcode.Add, R(theirs2), R(off), IrTypes.Word), 0, 1, false);
            Block next = f.NewBlock("bxnext");
            e.Branch(e.Binary(Opcode.Eq, x, y), next, no);
            e.SetBlock(next);
            e.CopyTo(at, new RegOperand(e.Binary(Opcode.Add, at, 1)));
            e.Jump(step);
            e.SetBlock(same2);
            e.Ret(new ImmOperand(1, IrType.I32));
            e.SetBlock(no);
            e.Ret(new ImmOperand(0, IrType.I32));
            _m.Functions.Add(f);
            return label;
        }

        int size = Math.Max(1, of.Size);
        bool signed = !of.IsUnsigned && of.Prim != Prim.Bool;
        VReg a = e.Load(BoxSlot(of), self, _t.ObjectHeaderBytes, size, signed);
        VReg b = e.Load(BoxSlot(of), other, _t.ObjectHeaderBytes, size, signed);
        if (a.Type.IsFloat())
        {
            // Boxed Single/Double.Equals differs from operator ==: all NaN
            // values compare equal to one another, as do signed zeroes.
            VReg equal = e.Binary(Opcode.FEq, R(a), R(b), IrType.I32);
            VReg nanA = e.Binary(Opcode.FNe, R(a), R(a), IrType.I32);
            VReg nanB = e.Binary(Opcode.FNe, R(b), R(b), IrType.I32);
            VReg bothNan = e.Binary(Opcode.And, nanA, nanB);
            e.Ret(new RegOperand(e.Binary(Opcode.Or, equal, bothNan)));
        }
        else e.Ret(new RegOperand(e.Binary(Opcode.Eq, R(a), R(b), IrType.I32)));

        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrType.I32));

        _m.Functions.Add(f);
        return label;
    }

    /// <summary>The value type a name in an `is` or a cast spells, or null.</summary>
    private Type? ValueTypeNamed(string name)
    {
        Type? prim = name switch
        {
            "sbyte" => Type.I8, "byte" => Type.U8,
            "short" => Type.I16, "ushort" => Type.U16,
            "int" => Type.I32, "uint" => Type.U32,
            "long" => Type.I64, "ulong" => Type.U64,
            "nint" => Type.NInt, "nuint" => Type.NUInt,
            "float" => Type.F32, "double" => Type.F64,
            "bool" => Type.Bool, "char" => Type.Char,
            _ => null,
        };
        if (prim is not null)
        {
            return prim;
        }
        return _b.Types.TryGetValue(name, out TypeSymbol? sym)
            && sym.Kind is TypeKind.Enum or TypeKind.Struct
            ? new Type { Symbol = sym }
            : null;
    }
}
