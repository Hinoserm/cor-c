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

    // ---- another instantiation's statics, from shared code -----------------
    //
    // A generic type with statics is copied per argument (Monomorphiser.
    // HasStaticState), and code one copy of which serves every reference
    // argument finds the instantiation it means at run time, as .NET's and
    // Mono's shared code ask a generic dictionary for a type's statics: an
    // entry of its type context, or of a shared method copy's dictionary
    // (Monomorphiser.CanonStaticField, CanonStaticCall), which each
    // instantiation fills with a table of its own (TypeContext). Every table
    // begins with the same two words -- the type's ready word and the
    // function that runs its initialiser (StaticTouch), 0 where it needs none
    // -- so that a touch is two loads and a compare once the type is made, as
    // TouchType's own is, and a call only the first time.

    /// <summary>The words every statics table begins with (StaticHeader): the ready word's address, then the touch.</summary>
    private const int StaticHeaderWords = 2;

    /// <summary>
    /// A type's ready word and its touch (StaticTouch) for a table's head:
    /// both 0 for a type with no initialiser, or one the kernel's link made
    /// (TouchType's own cases).
    /// </summary>
    private void StaticHeader(DataItem table, TypeSymbol holder)
    {
        MethodSymbol? start = holder.FindMethods("StaticInit$").FirstOrDefault(m => m.Static && m.Params.Count == 0);
        FieldSymbol? ready = holder.FindField(BindResult.ReadyField) is { Static: true } readyField && ReferenceEquals(readyField.Owner, holder) ? readyField : null;
        if (start is null || ready is null)
        {
            return;
        }
        ready = SharedStatic(ready);
        // MADE BY THE KERNEL'S LINK: nothing to touch, and the module says
        // it relied on that, as TouchType's own case does (KernelAssumptions).
        if (KernelInitialised is { } initialised && initialised.Contains(StaticSymbol(ready)))
        {
            _m.KernelNeeds.Add(Corsac.Lang.Lto.KernelAssumptions.InitName(StaticSymbol(ready)));
            return;
        }
        _statics.Add(ready);
        table.Relocs.Add(new DataReloc(0, StaticSymbol(ready), 0));
        table.Relocs.Add(new DataReloc(_t.WordSize, StaticTouch(holder), 0));
    }

    /// <summary>
    /// A FUNCTION THAT TOUCHES A TYPE (TouchType), for shared code reaching a
    /// static of an instantiation it names only through a table: a read of a
    /// static or a call of one touches its type, and the type's own code does
    /// not touch it again -- reached through a word, `_default` in
    /// EqualityComparer of a sealed class was read before its initialiser ran.
    /// </summary>
    private string StaticTouch(TypeSymbol holder)
    {
        string label = "__static_touch$" + TypeKey(holder);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        Function made = new(label, IrType.Void) { Coalescible = true, FromLibrary = IsLibrary(holder), SystemCode = SystemCode(holder) };
        InFunction(made, () =>
        {
            TouchType(holder, fromOutside: true);
            _e.Ret(null);
        });
        return label;
    }

    /// <summary>Lowers `body` into a function of its own, the one being lowered left as it was.</summary>
    private void InFunction(Function made, Action body)
    {
        Function outerF = _f;
        Builder outerE = _e;
        MethodSymbol? outerMethod = _method;
        try
        {
            _f = made;
            _e = new Builder(made, made.NewBlock("entry"));
            _method = null;
            body();
        }
        finally
        {
            _f = outerF;
            _e = outerE;
            _method = outerMethod;
        }
        _m.Functions.Add(made);
    }

    /// <summary>
    /// AN INSTANTIATION'S STATIC FIELD'S TABLE ("static:Name"): its head
    /// (StaticHeader), the field's address, and for one per thread
    /// ([ThreadStatic]), whose address is each thread's, the function that
    /// answers this thread's cell instead.
    /// </summary>
    private string StaticFieldTable(FieldSymbol f)
    {
        FieldSymbol store = SharedStatic(f);
        string label = "__static_of$" + StaticSymbol(store);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        int w = _t.WordSize;
        DataItem table = new(label, new byte[(StaticHeaderWords + 2) * w]) { ReadOnly = true, Exported = false, Align = w };
        StaticHeader(table, f.Owner);
        _statics.Add(store);
        if (store.ThreadStatic)
        {
            string cell = "__static_cell$" + StaticSymbol(store);
            if (_structHelpers.Add(cell))
            {
                Function made = new(cell, IrTypes.Word) { Coalescible = true, FromLibrary = IsLibrary(f.Owner), SystemCode = SystemCode(f.Owner) };
                InFunction(made, () => _e.Ret(R(ThreadStaticCell(store, f.Owner.Decl ?? (Node)new MethodDecl { Name = "", Line = 0, Col = 0 }))));
            }
            table.Relocs.Add(new DataReloc((StaticHeaderWords + 1) * w, cell, 0));
        }
        else
        {
            table.Relocs.Add(new DataReloc(StaticHeaderWords * w, StaticSymbol(store), 0));
        }
        _m.Data.Add(table);
        return label;
    }

    /// <summary>
    /// A STATIC PROPERTY'S TABLE ("static:Name"): its head, its getter and
    /// its setter -- 0 for one it has not, or one handed anything hidden
    /// (StaticWordFor) -- and an auto property's backing field's
    /// table (StaticFieldTable), which the binder reads in the property's
    /// place where it may (Binder.BackingReads). Null where it has none.
    /// </summary>
    private string? StaticProperty(TypeSymbol holder, string name)
    {
        string? getter = holder.FindMethods("get_", name).FirstOrDefault(x => x.Static && x.Params.Count == 0) is MethodSymbol get ? StaticWordFor(get, holder) : null;
        string? setter = holder.FindMethods("set_", name).FirstOrDefault(x => x.Static && x.Params.Count == 1) is MethodSymbol set ? StaticWordFor(set, holder) : null;
        FieldSymbol? backing = holder.FindBackingField(name) is { Static: true } field ? field : null;
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
        DataItem table = new(label, new byte[(StaticHeaderWords + 3) * w]) { ReadOnly = true, Exported = false, Align = w };
        StaticHeader(table, holder);
        if (getter is not null) table.Relocs.Add(new DataReloc(StaticHeaderWords * w, getter, 0));
        if (setter is not null) table.Relocs.Add(new DataReloc((StaticHeaderWords + 1) * w, setter, 0));
        if (backing is not null) table.Relocs.Add(new DataReloc((StaticHeaderWords + 2) * w, StaticFieldTable(backing), 0));
        _m.Data.Add(table);
        return label;
    }

    /// <summary>A static property's table's word for its getter (0), its setter (1) and its backing field's table (2), after the head.</summary>
    private const int PropertyGetter = 0, PropertySetter = 1, PropertyBacking = 2;

    /// <summary>
    /// Whether a static method can be called through a table's word with its
    /// declared arguments (StaticWordFor): none of a result buffer, type
    /// arguments, a struct by value or a native's packing, which a call
    /// through a word does not hand. Alike in every instantiation of the
    /// template, so what shared code refuses (PendHiddenArguments) is what no
    /// instantiation's table could serve.
    /// </summary>
    private bool EntryCallable(MethodSymbol m)
        => m.Static && !Buffered(m) && Monomorphiser.SharedMethodCopy(m.Name) == 0 && !_b.PackedNatives.Contains(m)
        && !StructContext(Canonical(m)) && !m.Params.Any(p => !p.ByRef && HomedStruct(p.Type));

    /// <summary>
    /// WHAT AN INSTANTIATION'S TABLE HOLDS FOR ONE OF ITS STATIC METHODS
    /// (StaticOverloads, StaticProperty): the method itself, its own code,
    /// declared in the type the table is the named type's; else a thunk
    /// taking the declared arguments that touches the type that declares it
    /// -- a call of a static touches its declaring type, not the type that
    /// named it, and the method's own code does not (TouchType) -- and calls
    /// it, a class sharing its code's body handed this instantiation's
    /// context (ContextStatic) as a call naming it directly is (CallDirect).
    /// Null for one no word can call (EntryCallable).
    /// </summary>
    private string? StaticWordFor(MethodSymbol m, TypeSymbol holder)
    {
        if (!EntryCallable(m))
        {
            return null;
        }
        MethodSymbol canonical = Canonical(m);
        bool shared = TakesContext(canonical);
        if (shared && m.Owner.Decl?.Canon is null)
        {
            return null;
        }
        Require(m);
        if (!shared && ReferenceEquals(m.Owner, holder))
        {
            return CallLabel(m);
        }
        string label = "__static_thunk$" + TypeKey(m.Owner) + "$" + CallLabel(m);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        IrType returns = ReturnIr(m);
        Function made = new(label, returns) { Coalescible = true, FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner) };
        InFunction(made, () =>
        {
            TouchType(m.Owner, fromOutside: true);
            List<Operand> args = new();
            if (shared) args.Add(R(_e.Address(ContextToken(m.Owner))));
            foreach (ParamSymbol p in m.Params)
            {
                VReg given = _f.NewReg(p.ByRef ? IrTypes.Word : IrTypes.Of(p.Type), p.Name);
                _f.Params.Add(given);
                args.Add(R(given));
            }
            VReg? answered = _e.Call(shared ? BodyLabel(canonical) : CallLabel(m), returns, args.ToArray());
            _e.Ret(answered is null ? null : R(answered));
        });
        return label;
    }

    /// <summary>
    /// A class's static, non-generic methods by a name, its bases' among them
    /// (FindMethods), in the order of the template each is declared in and
    /// its place there -- the same in every instantiation, whatever order
    /// binding asked for them in.
    /// </summary>
    private static List<MethodSymbol> StaticsByName(TypeSymbol holder, string name)
        => holder.FindMethods(name).Where(x => x.Static && x.Decl is MethodDecl { TypeParams.Count: 0 })
            .OrderBy(x => x.Owner.Decl?.Template ?? x.Owner.Name, StringComparer.Ordinal).ThenBy(x => x.Decl!.Line).ThenBy(x => x.Decl!.Col).ToList();

    /// <summary>
    /// AN INSTANTIATION'S TABLE OF ITS STATIC METHODS BY A NAME ("call:Name"):
    /// its head, then a word for each (StaticsByName), the function, or 0 for
    /// one shared code cannot call through a word (StaticWordFor).
    /// </summary>
    private string? StaticOverloads(TypeSymbol holder, string name)
    {
        List<MethodSymbol> all = StaticsByName(holder, name);
        List<string?> words = all.Select(m => StaticWordFor(m, holder)).ToList();
        if (words.All(x => x is null))
        {
            return null;
        }
        string label = "__statics_of$" + TypeKey(holder) + "$" + Safe(name);
        if (!_structHelpers.Add(label))
        {
            return label;
        }
        int w = _t.WordSize;
        DataItem table = new(label, new byte[(StaticHeaderWords + all.Count) * w]) { ReadOnly = true, Exported = false, Align = w };
        StaticHeader(table, holder);
        for (int i = 0; i < all.Count; i++)
        {
            if (words[i] is not string word) continue;
            table.Relocs.Add(new DataReloc((StaticHeaderWords + i) * w, word, 0));
        }
        _m.Data.Add(table);
        return label;
    }

    /// <summary>
    /// The entry shared code reads for another instantiation's static
    /// (MemberExpr.StaticSlot, CallExpr.StaticCallSlot): from its type
    /// context through `self`, or from its dictionary; null where it has
    /// neither. 0 at run time where the entry holds no table.
    /// </summary>
    private VReg? StaticEntry(Expr? self, int slot, int need)
        => slot >= 0
            ? self is not null && _b.TypeOf(self).Symbol is { } selfOwner && SharedContext(selfOwner) ? ContextEntryOrZero(Eval(self), selfOwner, slot) : null
            : need >= 0 && _needDictionary is not null ? NeedEntry(need) : null;

    /// <summary>
    /// A call's target by the entry shared code reads for it (StaticWord): the
    /// method, the entry, the method's place in the table, and whether the
    /// table's type is touched -- not for a static the named type inherits,
    /// whose word touches the type that declares it (StaticWordFor).
    /// </summary>
    private (MethodSymbol Method, VReg Entry, int Place, bool Touch)? _pendingStatic;

    /// <summary>
    /// A table's touch (StaticHeader), where the table is not 0: its type's
    /// initialiser run unless its ready word says it has.
    /// </summary>
    private void StaticTableTouch(VReg table)
    {
        int w = _t.WordSize;
        Block has = _f.NewBlock("statichas"), check = _f.NewBlock("staticcheck"), run = _f.NewBlock("staticrun"), done = _f.NewBlock("statictouched");
        _e.Branch(table, has, done);
        _e.SetBlock(has);
        VReg touch = _e.Load(IrTypes.Word, table, w);
        _e.Branch(touch, check, done);
        _e.SetBlock(check);
        VReg ready = _e.Load(IrTypes.Word, table, 0);
        VReg state = _e.Load(IrTypes.Word, ready, 0, _t.WordSize, false);
        _e.Emit(Opcode.CompilerFence, null);
        _e.Branch(_e.Binary(Opcode.Eq, state, 1), done, run);
        _e.SetBlock(run);
        _e.CallIndirect(R(touch), IrType.Void, Array.Empty<Operand>(), StaticTableCall.Marker);
        _e.Jump(done);
        _e.SetBlock(done);
    }

    /// <summary>
    /// The word at `place` after a table's head (StaticHeader), its type
    /// touched first -- 0 where there is no table or no word.
    /// </summary>
    private VReg StaticWord(VReg table, int place, bool touch = true)
    {
        if (touch) StaticTableTouch(table);
        VReg result = _f.NewReg(IrTypes.Word, "staticword");
        Block read = _f.NewBlock("staticread"), end = _f.NewBlock("staticend");
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Branch(table, read, end);
        _e.SetBlock(read);
        _e.CopyTo(result, R(_e.Load(IrTypes.Word, table, (long)(StaticHeaderWords + place) * _t.WordSize)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>A word of a table, read only where the table is not 0 (else 0), with no touch.</summary>
    private VReg TableWord(VReg table, int place)
    {
        VReg result = _f.NewReg(IrTypes.Word, "tableword");
        Block read = _f.NewBlock("tableread"), end = _f.NewBlock("tableend");
        _e.CopyTo(result, Imm(0, IrTypes.Word));
        _e.Branch(table, read, end);
        _e.SetBlock(read);
        _e.CopyTo(result, R(_e.Load(IrTypes.Word, table, (long)(StaticHeaderWords + place) * _t.WordSize)));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }

    /// <summary>
    /// A static property's accessor called from shared code over another
    /// instantiation (MemberExpr.StaticSlot): read from the entry's table at
    /// `place` -- PropertyGetter or PropertySetter (StaticProperty) -- by the
    /// next call of it (CallDirect).
    /// </summary>
    private void PendStaticAccessor(Expr target, MethodSymbol accessor, int place)
    {
        if (!accessor.Static || target is not MemberExpr { } m || m.StaticSlot < 0 && m.StaticNeed < 0 || !ThroughTable(m.Target, accessor.Owner)) return;
        // One no table can serve: refused, never the copy over object's.
        if (!EntryCallable(accessor))
            Error(m, $"'{accessor.Owner.Name}.{m.Name}' is reached from code shared by every reference argument, and its accessor takes what a call through a table cannot hand it (a struct result or a struct by value); C# gives each instantiation its own, which that code cannot find");
        else if (StaticEntry(m.StaticSelf, m.StaticSlot, m.StaticNeed) is VReg entry)
            _pendingStatic = (accessor, entry, place, Inherits(m.Target, accessor) is not true);
        else
            Error(m, $"'{accessor.Owner.Name}.{m.Name}' is reached from shared code with no type context or dictionary to find its instantiation's in (internal)");
    }

    /// <summary>
    /// Whether a static named through `named` from shared code is found in a
    /// statics table. Only one that is each instantiation's own -- declared
    /// in a generic type, copied per argument or sharing its code (whose
    /// word StaticWordFor hands its context) -- named through a type copied
    /// per argument. Not one of a non-generic type, a base's: it is the same
    /// for every argument. Nor one named through a type with a canonical
    /// copy (Decl.Canon): it is called as that code's own, handed its
    /// context as any call of it is (`Span<T>.Move` from Array.Copy's shared
    /// copy). SharedContext is not the test for that: its shape test holds
    /// for the copy over object of a type copied per argument too. Either
    /// way the static is the one this code names (PendHiddenArguments,
    /// PendStaticAccessor, OtherStaticAddress).
    /// </summary>
    private bool ThroughTable(Expr named, TypeSymbol owner)
        => owner.Decl?.Template is not null && !(_b.TypeOf(named).Symbol is { Decl.Canon: not null });

    /// <summary>
    /// Whether `m` is a static the type `named` names inherits from a base
    /// (StaticWordFor), whose word touches its declaring type; null where
    /// the binder gave the name no type, and false for a type no template
    /// made. Only true leaves the named type's table untouched.
    /// </summary>
    private bool? Inherits(Expr named, MethodSymbol m)
        => _b.TypeOf(named).Symbol is { } type ? type.Decl?.Template is string written && m.Owner.Decl?.Template != written : null;

    /// <summary>
    /// WHERE ANOTHER INSTANTIATION'S STATIC FIELD IS, read from shared code
    /// (MemberExpr.StaticSlot): its table's address -- or this thread's cell
    /// of one per thread -- its type touched; where the entry holds none, the
    /// field this code names, touched as any static's read touches it. Null
    /// where the read has no entry at all.
    /// </summary>
    private VReg? OtherStaticAddress(FieldSymbol f, FieldSymbol store, MemberExpr read, Node at)
    {
        if (StaticEntry(read.StaticSelf, read.StaticSlot, read.StaticNeed) is not VReg entry)
        {
            // Marked, and no context to read it in: never the copy over
            // object's field read in its place.
            if (ThroughTable(read.Target, store.Owner))
                Error(at, $"'{store.Owner.Name}.{read.Name}' is read from shared code with no type context or dictionary to find its instantiation's in (internal)");
            return null;
        }
        // An auto property's backing field read in its place: the field's
        // table its property's table holds (StaticProperty).
        VReg table = read.Name != f.Name ? TableWord(entry, PropertyBacking) : entry;
        StaticTableTouch(table);
        VReg found;
        if (store.ThreadStatic)
        {
            VReg cellOf = TableWord(table, 1);
            found = _f.NewReg(IrTypes.Word, "staticcell");
            Block call = _f.NewBlock("staticcellcall"), called = _f.NewBlock("staticcelldone");
            _e.CopyTo(found, Imm(0, IrTypes.Word));
            _e.Branch(cellOf, call, called);
            _e.SetBlock(call);
            _e.CopyTo(found, R(_e.CallIndirect(R(cellOf), IrTypes.Word, Array.Empty<Operand>(), StaticTableCall.Marker)!));
            _e.Jump(called);
            _e.SetBlock(called);
        }
        else
        {
            found = TableWord(table, 0);
        }
        VReg result = _f.NewReg(IrTypes.Word, "staticat");
        Block own = _f.NewBlock("staticownat"), end = _f.NewBlock("staticatend");
        _e.CopyTo(result, R(found));
        _e.Branch(found, end, own);
        _e.SetBlock(own);
        TouchType(f.Owner);
        _e.CopyTo(result, R(store.ThreadStatic ? ThreadStaticCell(store, at) : _e.Address(StaticSymbol(store))));
        _e.Jump(end);
        _e.SetBlock(end);
        return result;
    }
}
