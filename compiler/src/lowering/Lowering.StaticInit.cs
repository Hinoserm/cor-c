#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// WHEN A TYPE'S STATIC INITIALISERS RUN.
///
/// C#'s answer is "the first time anything touches the type, once", and it
/// used to be "in source order, from the entry stub, before Main". That was a
/// defensible simplification until this machine grew a kernel: the entry stub
/// runs before there is a heap, so a `static byte[] table = new byte[24];` in
/// any file the kernel linked brought the kernel down at entry -- twice in one
/// day, once for a filesystem's table and once for a scratch buffer in the
/// collector itself.
///
/// So the generated StaticInit$ uses an owner/completed/failed state word
/// (see Binder.StaticInitialisers), and the three things that TOUCH a
/// type in C#'s sense reach it first:
///
///   * creating an instance,
///   * calling one of its static methods,
///   * reading or writing one of its static fields.
///
/// (But for a module, of a type its kernel's link has initialised already:
/// nothing is tested, and the module says that it relied on it -- TouchType's
/// own comment, Lto.KernelAssumptions.)
///
/// The test is INLINE and the call is behind it -- a load, a branch, and in
/// an acquire fence, and no call when the state is completed. A call every time would be two more
/// instructions on a machine where every instruction is interpreted, and the
/// common case is that the type was initialised long ago.
///
/// FROM INSIDE THE TYPE ITSELF NOTHING IS EMITTED. Reaching a static method or
/// a static field of T from a method of T means something already entered T,
/// and that entry did the test. This is what keeps the guard off the inside of
/// every library class's own loops -- testing everywhere instead cost grep
/// eighteen per cent of its code, which on this machine is eighteen per cent
/// of the instructions it interprets.
///
/// That shortcut is only worth anything if EVERY way in is covered, so two
/// more go with the three above:
///
///   * MAIN, which nothing else calls -- the entry stub touches its type
///     before calling it, as C# does;
///   * A DELEGATE over a static method, where the address is taken here and
///     the call that follows has no name to hang a trigger on;
///
/// and a struct is asked at every instance call, because a struct can come
/// into being without a `new` -- `S s = default(S); s.Read();` -- and a class
/// cannot.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>
    /// Run <paramref name="owner"/>'s static initialisers if they have not
    /// run, before whatever is about to touch the type.
    /// </summary>
    private void TouchType(TypeSymbol? owner, bool fromOutside = false)
    {
        if (owner is null)
        {
            return;
        }

        // Being done right now: StaticInit$ itself, and anything it calls, is
        // inside the type by definition.
        //
        // EVERY TOUCH IS TESTED, not just the first one a function makes. The
        // first attempt remembered which types a function had already
        // triggered and skipped the rest, which is only sound in a straight
        // line: `if (rare) { T.A = 1; } T.B = 2;` put the only test inside the
        // branch, and a run that did not take it read T's statics unwritten.
        // os/bin/uniq printed nothing at all. A load and a branch is what this
        // costs, and the optimiser folds the repeats within a block.
        // Unless the method is a way in from outside (fromOutside): one C
        // calls is the first of its type to run as often as not.
        if (!fromOutside && ReferenceEquals(owner, _method?.Owner))
        {
            return;
        }

        MethodSymbol? start = owner.FindMethods("StaticInit$")
            .FirstOrDefault(m => m.Static && m.Params.Count == 0);
        // By name (MemberWatch): its presence is the type's shape's to say.
        FieldSymbol? ready = owner.FindField(BindResult.ReadyField) is { Static: true } readyField && ReferenceEquals(readyField.Owner, owner) ? readyField : null;

        if (start is null || ready is null)
        {
            return;
        }

        ready = SharedStatic(ready);
        // INITIALISED BY THE KERNEL'S LINK, in a module built against that
        // kernel (KernelExports.Initialised): the word is "completed" in the
        // image before any module's code runs, and StaticInit$ writes it
        // only on the way to that. Nothing to test, nothing to call -- and the module says
        // that it relied on it (KernelAssumptions): a kernel of another
        // build whose link does not run this initialiser does not export
        // the name, and refuses the module instead of letting it read the
        // type's statics unwritten.
        if (KernelInitialised is { } initialised && initialised.Contains(StaticSymbol(ready)))
        {
            _m.KernelNeeds.Add(Corsac.Lang.Lto.KernelAssumptions.InitName(StaticSymbol(ready)));
            return;
        }

        Require(start);
        _statics.Add(ready);

        Block run = _f.NewBlock("ctorun");
        Block on = _f.NewBlock("ctdone");
        if (ready.Type.Prim != Prim.NInt)
            throw new InvalidOperationException("obsolete static initializer state; rebuild the library/header");
        VReg state = _e.Load(IrTypes.Word, new SymOperand(StaticSymbol(ready)), 0, _t.WordSize, false);
        // An acquire, which x86's ordering gives every load: a barrier to the
        // optimiser only. A full fence here -- `lock or [esp]` at every touch
        // of another type, twice in a row in the kernel's trap path -- was
        // a locked bus cycle for nothing, hundreds of times a trap.
        _e.Emit(Opcode.CompilerFence, null);
        VReg done = _e.Binary(Opcode.Eq, state, 1);
        _e.Branch(done, on, run);
        _e.SetBlock(run);
        _e.Call(CallLabel(start), IrType.Void);
        _e.Jump(on);
        _e.SetBlock(on);
    }
    /// <summary>
    /// WHERE AN INSTANTIATION'S STATIC FIELD IS, AS A FUNCTION SHARED CODE CAN
    /// CALL (MemberExpr.StaticSlot): its type touched, as any read of a static
    /// touches it, then the field's address. What a shared copy's type context
    /// holds for a static of a generic type copied per argument over its
    /// parameters (TypeContext, "static:Name"), and what the copy calls for
    /// its own where the context holds none. Null for a field no address
    /// stands for: one per thread, a struct held in the static's own bytes,
    /// a struct made the first time it is touched.
    /// </summary>
    private string? StaticAccessor(FieldSymbol f)
    {
        FieldSymbol store = SharedStatic(f);
        if (!store.Static || store.ThreadStatic || StaticHeldInline(store) || !store.Boxed && !store.Initialised && IsStructValue(store.Type))
        {
            return null;
        }
        string label = "__static_at$" + StaticSymbol(store);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        _statics.Add(store);
        Function made = new(label, IrTypes.Word) { Coalescible = true, FromLibrary = IsLibrary(f.Owner), SystemCode = SystemCode(f.Owner) };
        Function outerF = _f;
        Builder outerE = _e;
        MethodSymbol? outerMethod = _method;
        try
        {
            _f = made;
            _e = new Builder(made, made.NewBlock("entry"));
            _method = null;
            TouchType(f.Owner, fromOutside: true);
            _e.Ret(R(_e.Address(StaticSymbol(store))));
        }
        finally
        {
            _f = outerF;
            _e = outerE;
            _method = outerMethod;
        }
        _m.Functions.Add(made);
        return label;
    }
    /// <summary>
    /// A STATIC PROPERTY'S ACCESSORS as shared code may call them through an
    /// entry (MemberExpr.StaticSlot), in a table as a method's are
    /// (StaticOverloads): its type's touch, then the getter and the setter,
    /// 0 for one it has not or one handed anything hidden
    /// (CallableThroughEntry), then an auto property's backing field's
    /// accessor (StaticAccessor), which the binder reads in the property's
    /// place where it may (Binder.BackingReads). Null where it has none.
    /// </summary>
    private string? StaticProperty(TypeSymbol holder, string name)
    {
        MethodSymbol? getter = holder.FindMethods("get_", name).FirstOrDefault(x => x.Static && x.Params.Count == 0 && CallableThroughEntry(x));
        MethodSymbol? setter = holder.FindMethods("set_", name).FirstOrDefault(x => x.Static && x.Params.Count == 1 && CallableThroughEntry(x));
        string? backing = holder.FindBackingField(name) is { Static: true } field ? StaticAccessor(field) : null;
        if (getter is null && setter is null && backing is null)
        {
            return null;
        }
        string label = "__statics_prop$" + TypeKey(holder) + "$" + Safe(name);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        int w = _t.WordSize;
        DataItem table = new(label, new byte[4 * w]) { ReadOnly = true, Exported = false, Align = w };
        if (backing is not null) table.Relocs.Add(new DataReloc(3 * w, backing, 0));
        table.Relocs.Add(new DataReloc(0, StaticTouch(holder), 0));
        if (getter is not null)
        {
            Require(getter);
            table.Relocs.Add(new DataReloc(w, CallLabel(getter), 0));
        }
        if (setter is not null)
        {
            Require(setter);
            table.Relocs.Add(new DataReloc(2 * w, CallLabel(setter), 0));
        }
        _m.Data.Add(table);
        return label;
    }

    /// <summary>
    /// A static property's accessor called from shared code over another
    /// instantiation (MemberExpr.StaticSlot): read from the entry's table at
    /// `place` -- 0 the getter, 1 the setter (StaticProperty) -- by the next
    /// call of it (CallDirect).
    /// </summary>
    private void PendStaticAccessor(Expr target, MethodSymbol accessor, int place)
    {
        if (accessor.Static && target is MemberExpr { } m && (m.StaticSlot >= 0 || m.StaticNeed >= 0)
            && StaticEntry(m.StaticSelf, m.StaticSlot, m.StaticNeed) is VReg entry)
            _pendingStatic = (accessor, entry, place);
    }

    /// <summary>
    /// A FUNCTION THAT TOUCHES A TYPE (TouchType), for shared code calling a
    /// static of an instantiation it names only through an entry: a call of
    /// a static touches the type, and the type's own code does not touch it
    /// again -- reached through a word, `_default` in EqualityComparer of a
    /// sealed class was read before its initialiser ran.
    /// </summary>
    private string StaticTouch(TypeSymbol holder)
    {
        string label = "__static_touch$" + TypeKey(holder);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        Function made = new(label, IrType.Void) { Coalescible = true, FromLibrary = IsLibrary(holder), SystemCode = SystemCode(holder) };
        Function outerF = _f;
        Builder outerE = _e;
        MethodSymbol? outerMethod = _method;
        try
        {
            _f = made;
            _e = new Builder(made, made.NewBlock("entry"));
            _method = null;
            TouchType(holder, fromOutside: true);
            _e.Ret(null);
        }
        finally
        {
            _f = outerF;
            _e = outerE;
            _method = outerMethod;
        }
        _m.Functions.Add(made);
        return label;
    }

    /// <summary>
    /// Whether a static method is called with its declared arguments only
    /// (StaticProperty, StaticOverloads): its own code, not a shared copy's
    /// -- one of a class sharing its code runs the canonical copy's, handed
    /// its instantiation's context (Monomorphiser.CanonContextCall), which a
    /// shared method copy, its template out of sight, asks for beside this.
    /// </summary>
    private bool CallableThroughEntry(MethodSymbol m)
        => m.Static && m.Owner.Decl?.Canon is null && !CanonicalCopy(m.Owner) && !Buffered(m) && !TakesContext(Canonical(m))
        && Monomorphiser.SharedMethodCopy(m.Name) == 0 && !_b.PackedNatives.Contains(m);

    /// <summary>
    /// A class's static, non-generic methods by a name, in the order their
    /// declarations stand in the source -- the same in every instantiation
    /// of a template, whatever order binding asked for them in.
    /// </summary>
    private static List<MethodSymbol> StaticsByName(TypeSymbol holder, string name)
        => holder.FindMethods(name).Where(x => x.Static && x.Decl is MethodDecl { TypeParams.Count: 0 })
            .OrderBy(x => x.Decl!.Line).ThenBy(x => x.Decl!.Col).ToList();

    /// <summary>
    /// AN INSTANTIATION'S TABLE OF ITS STATIC METHODS BY A NAME (CallExpr.
    /// StaticCallSlot): the type's touch (StaticTouch), then a word for each,
    /// in declaration order (StaticsByName), the function, or 0 for one
    /// shared code cannot call through a word (CallableThroughEntry). The
    /// call touches the type and takes the word at its overload's place.
    /// </summary>
    private string? StaticOverloads(TypeSymbol holder, string name)
    {
        List<MethodSymbol> all = StaticsByName(holder, name);
        if (!all.Any(CallableThroughEntry))
        {
            return null;
        }
        string label = "__statics_of$" + TypeKey(holder) + "$" + Safe(name);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        int w = _t.WordSize;
        DataItem table = new(label, new byte[(1 + all.Count) * w]) { ReadOnly = true, Exported = false, Align = w };
        table.Relocs.Add(new DataReloc(0, StaticTouch(holder), 0));
        for (int i = 0; i < all.Count; i++)
        {
            if (!CallableThroughEntry(all[i])) continue;
            Require(all[i]);
            table.Relocs.Add(new DataReloc((1 + i) * w, CallLabel(all[i]), 0));
        }
        _m.Data.Add(table);
        return label;
    }

    /// <summary>
    /// The entry shared code reads for another instantiation's static
    /// (MemberExpr.StaticSlot, CallExpr.StaticCallSlot): from its type
    /// context through `self`, or from its dictionary; null where it has
    /// neither.
    /// </summary>
    private VReg? StaticEntry(Expr? self, int slot, int need)
        => slot >= 0
            ? self is not null && _b.TypeOf(self).Symbol is { } selfOwner && SharedContext(selfOwner) ? ContextEntryOrZero(Eval(self), selfOwner, slot) : null
            : need >= 0 && _needDictionary is not null ? NeedEntry(need) : null;

    /// <summary>A call's target by the entry shared code reads for it (StaticWord): the method, the entry and the method's place in the table.</summary>
    private (MethodSymbol Method, VReg Entry, int Place)? _pendingStatic;

    /// <summary>
    /// The function an entry gives a call (StaticEntry): the word at `place`
    /// of the table it names, after the type's touch -- 0 where there is no
    /// entry or no word.
    /// </summary>
    private VReg StaticWord(VReg entry, int place)
    {
        VReg result = _f.NewReg(IrTypes.Word, "staticword");
        Block read = _f.NewBlock("staticread"), end = _f.NewBlock("staticend");
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Branch(entry, read, end);
        _e.SetBlock(read);
        _e.CopyTo(result, R(_e.Load(IrTypes.Word, entry, (long)(1 + place) * _t.WordSize)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }
}
