#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using Type = Corsac.Lang.Type;

/// <summary>
/// MAKING AN OBJECT OF A TYPE ONLY RUN TIME KNOWS: Activator.CreateInstance,
/// and `new T()` in a generic method whose T only run time knows -- what C#
/// compiles `new T()` to (Monomorphiser.ParameterMade).
///
/// THE TYPE'S DESCRIPTOR SAYS HOW. Its display -- every ancestor's
/// descriptor, root first, self last -- has one word more, after itself:
/// the type's MAKER, a function of no arguments that answers a new object
/// made by its public parameterless constructor, as `new C()` makes one.
/// A box's display names a maker of the box of the value type's zero, and
/// object's one that makes a bare object. Where there is no maker the word
/// says why: 0 for no public parameterless constructor, 1 for an abstract
/// class. An interface, an array and a string have no display word to read
/// and are refused by their flags.
///
/// A RELOCATION, because a type made only this way is still made: the
/// maker is a function whose address the data names, which every analysis
/// counts as taken -- called by any call that names nothing (RegionSummary's
/// AddressTaken, the link's reachability through the display) -- and whose
/// body is an ordinary allocation of the type and a direct call of its
/// constructor, which keeps that constructor.
///
/// Sys.Make(type) runs it (MakeHelper): the maker the descriptor names, or
/// Runtime.CannotMake with the reason, which throws .NET's
/// MissingMethodException.
///
/// Makers are emitted between methods, never inside one (EmitMakers): a
/// descriptor is made on demand while a method is being lowered, and the
/// maker's body needs the per-method state that method holds.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>The makers named by a display and not yet emitted: a class's, or a box's.</summary>
    private readonly Queue<(string Name, TypeSymbol? Class, Type? Boxed)> _makers = new();

    private readonly HashSet<string> _makerNames = new(StringComparer.Ordinal);

    /// <summary>What a display's maker word holds where there is no maker.</summary>
    private const long NoMakerConstructor = 0, NoMakerAbstract = 1, NoMakerInterface = 2;

    /// <summary>The node a maker's allocations are reported at, having no source of their own.</summary>
    private static readonly NameExpr MakerSite = new() { Name = "<maker>", Line = 0, Col = 0 };

    /// <summary>
    /// The maker of a class, or null with the reason there is none: an
    /// abstract class, a static one, a delegate, a canonical generic copy
    /// (whose objects are made as one of its instantiations), or no public
    /// parameterless constructor -- the one C# gives a class that declares
    /// none counting, as `new()` counts it (Binder.HasPublicParameterless).
    /// </summary>
    private string? ClassMaker(TypeSymbol t, out long reason)
    {
        reason = NoMakerConstructor;
        if (t.Kind != TypeKind.Class || t.Decl is not TypeDecl d || d.IsDelegate || (d.Mods & Mods.Static) != 0
            || d.TypeParams.Count > 0 || CanonicalCopy(t))
        {
            return null;
        }
        if ((d.Mods & Mods.Abstract) != 0)
        {
            reason = NoMakerAbstract;
            return null;
        }
        if (!ParameterlessConstructor(t, out _))
        {
            return null;
        }
        string name = "mk_" + TypeKey(t);
        if (_makerNames.Add(name))
        {
            _makers.Enqueue((name, t, null));
        }
        return name;
    }

    /// <summary>
    /// Whether a class has a public parameterless constructor, and which:
    /// its own, or, declaring none, the base's its implicit one runs (null
    /// where that is none either, as EmitNew calls none).
    /// </summary>
    private static bool ParameterlessConstructor(TypeSymbol t, out MethodSymbol? ctor)
    {
        List<MethodSymbol> ctors = t.Methods.Where(m => m.IsCtor && !m.Static).ToList();
        if (ctors.Count == 0)
        {
            ctor = ImplicitConstructor(t);
            return true;
        }
        ctor = ctors.FirstOrDefault(m => m.Params.Count == 0);
        return ctor is not null && (ctor.Decl is null || ctor.Decl.Mods.HasFlag(Mods.Public));
    }

    /// <summary>The maker of a box: the value type's zero, boxed.</summary>
    private string BoxMaker(Type of, string key)
    {
        string name = "mkb_" + Safe(key);
        if (_makerNames.Add(name))
        {
            _makers.Enqueue((name, null, of));
        }
        return name;
    }

    /// <summary>The maker of a bare object, `new object()`.</summary>
    private string ObjectMaker()
    {
        const string name = "mk_System_Object";
        if (_makerNames.Add(name))
        {
            _makers.Enqueue((name, null, null));
        }
        return name;
    }

    /// <summary>
    /// Every maker named so far, each a function of its own, made with the
    /// per-method state cleared as a method's is. Run between methods.
    /// </summary>
    private void EmitMakers()
    {
        while (_makers.Count > 0)
        {
            (string name, TypeSymbol? cls, Type? boxed) = _makers.Dequeue();
            ResetMethodState();
            _method = null;
            _decl = null;
            _in = cls?.Decl?.File ?? "";
            bool library = cls is null || IsLibrary(cls);
            _f = new Function(name, IrTypes.Word) { Exported = false, FromLibrary = library, SourceFile = _in };
            _e = new Builder(_f, _f.NewBlock("entry"));

            VReg made;
            if (cls is not null)
            {
                // As EmitNew makes one: the type touched, every type it derives
                // from too; the object, its vtable, its struct fields' zeros,
                // and the constructor.
                for (TypeSymbol? touched = cls; touched is not null; touched = touched.Base) TouchType(touched);
                made = Allocate(MakerSite, Math.Max(_t.ObjectHeaderBytes, cls.InstanceSize), described: true);
                _e.Store(R(made), VtableOf(cls), 0, _t.WordSize);
                InitStructFields(MakerSite, made, cls);
                ParameterlessConstructor(cls, out MethodSymbol? ctor);
                if (ctor is not null)
                {
                    CallDirect(ctor, IrType.Void, new List<Operand> { R(made) });
                }
            }
            else if (boxed is not null)
            {
                if (BoxedBlock(boxed))
                {
                    made = BoxValue(MakerSite, NewStruct(MakerSite, StructOf(boxed)), boxed);
                }
                else
                {
                    // A number's, a bool's, a char's or an enum's zero is a
                    // box nothing is written into.
                    made = Allocate(MakerSite, _t.ObjectHeaderBytes + Math.Max(_t.WordSize, BoxPayload(boxed)), described: true);
                    _e.Store(R(made), new SymOperand(BoxDescriptor(boxed), _t.DescriptorBytes), 0, _t.WordSize);
                }
            }
            else
            {
                made = Allocate(MakerSite, _t.ObjectHeaderBytes, described: true);
                _e.Store(R(made), new SymOperand(ObjectDescriptor(), _t.DescriptorBytes), 0, _t.WordSize);
            }
            _e.Ret(R(made));
            _m.Functions.Add(_f);
        }
    }

    /// <summary>An object made by the maker a descriptor's display names (Sys.Make).</summary>
    private VReg MakeDescribed(VReg desc)
        => _e.Call(MakeHelper(), IrTypes.Word, R(desc))!;

    private bool _hasMakeHelper;

    /// <summary>
    /// `__make_described`: the maker a descriptor names, called; or, where
    /// there is none, Runtime.CannotMake with why -- an interface, a class
    /// with no maker (abstract, or no public parameterless constructor), or
    /// anything with no display (an array, a string).
    /// </summary>
    private string MakeHelper()
    {
        const string name = "__make_described";
        if (_hasMakeHelper)
        {
            return name;
        }
        _hasMakeHelper = true;

        int w = _t.WordSize;
        Function f = new(name, IrTypes.Word) { Coalescible = true };
        VReg desc = f.NewReg(IrTypes.Word, "desc");
        f.Params.Add(desc);
        Builder e = new(f, f.NewBlock("entry"));

        Block known = f.NewBlock("mkknown");
        Block notFace = f.NewBlock("mknotface");
        Block hasDisplay = f.NewBlock("mkdisplay");
        Block notNone = f.NewBlock("mknotnone");
        Block call = f.NewBlock("mkcall");
        Block none = f.NewBlock("mknone");
        Block isAbstract = f.NewBlock("mkabstract");
        Block face = f.NewBlock("mkface");

        e.Branch(desc, known, none);

        e.SetBlock(known);
        VReg flags = e.Load(IrTypes.Word, desc, (long)DescFlags * w);
        e.Branch(e.Binary(Opcode.And, flags, TypeFlagInterface), face, notFace);

        e.SetBlock(notFace);
        VReg display = e.Load(IrTypes.Word, desc, (long)DescDisplay * w);
        VReg sequence = e.Binary(Opcode.And, flags, 3);
        Block displayed = f.NewBlock("mkdisplayed");
        e.Branch(sequence, none, displayed);
        e.SetBlock(displayed);
        e.Branch(display, hasDisplay, none);

        e.SetBlock(hasDisplay);
        VReg depth = e.Load(IrTypes.Word, desc, (long)DescDepth * w);
        VReg at = e.Binary(Opcode.Add, display, e.Binary(Opcode.Mul, e.Binary(Opcode.Add, depth, 1), w));
        VReg maker = e.Load(IrTypes.Word, at, 0);
        e.Branch(maker, notNone, none);

        e.SetBlock(notNone);
        e.Branch(e.Binary(Opcode.Eq, R(maker), new ImmOperand(NoMakerAbstract, IrTypes.Word), IrType.I32), isAbstract, call);

        e.SetBlock(call);
        e.Ret(new RegOperand(e.CallIndirect(new RegOperand(maker), IrTypes.Word, Array.Empty<Operand>())!));

        foreach ((Block b, long reason) in new[] { (none, NoMakerConstructor), (isAbstract, NoMakerAbstract), (face, NoMakerInterface) })
        {
            e.SetBlock(b);
            if (RuntimeMethod("CannotMake", 2) is MethodSymbol cannot)
            {
                Require(cannot);
                e.Call(CallLabel(cannot), IrType.Void, new RegOperand(desc), new ImmOperand(reason, IrType.I32));
            }
            e.Emit(Opcode.Trap, null);
            e.Unreachable();
        }

        _m.Functions.Add(f);
        return name;
    }
}
