#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    // ---- per-method state -------------------------------------------------------

    private Function _f = null!;
    private Builder _e = null!;
    private MethodSymbol? _method;
    private MethodDecl? _decl;

    /// <summary>Where each source local lives: a register, or a frame slot when its address is taken.</summary>
    private readonly Dictionary<LocalDecl, VReg> _localRegs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LocalDecl, FrameSlot> _localSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Compiler-made slots -- a switch subject, a pattern binding, a foreach
    /// cursor -- keyed by slot number AND type. The binder reuses numbers
    /// across scopes, sometimes at different types, so the number alone does
    /// not name a register.
    /// </summary>
    private readonly Dictionary<(int Slot, IrType Type), VReg> _slotRegs = new();

    /// <summary>A binder-numbered slot's declaration, when it has one.</summary>
    private readonly Dictionary<int, LocalDecl> _slotDecl = new();

    private VReg? _this;
    private VReg[] _params = Array.Empty<VReg>();
    private FrameSlot?[] _paramSlots = Array.Empty<FrameSlot>();

    /// <summary>Locals and parameters whose address is taken, found by a scan before emission.</summary>
    private readonly HashSet<LocalDecl> _addressTakenLocals = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _addressTakenParams = new();

    /// <summary>
    /// Address-taken locals the binder made without a declaration statement:
    /// `out int v` in an argument list, the names a deconstruction binds.
    /// Keyed by the symbol itself, which is all such a local has.
    /// </summary>
    private readonly HashSet<LocalSym> _addressTakenSyms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LocalSym, FrameSlot> _symSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The CELL of a captured local that no declaration statement made: an
    /// `out var`, a pattern's binding, a foreach cursor.
    ///
    /// An ordinary local's cell is allocated where it is declared. These have
    /// no declaration to allocate at, so the cell is made once on entry --
    /// which is where a closure's display would hold it anyway, and is what
    /// keeps `while (q.TryDequeue(out Interval? cur)) { list.RemoveAll(a =>
    /// a.End &lt; cur.Start); }` working: the lambda reads the same cell the
    /// loop writes.
    /// </summary>
    private readonly Dictionary<LocalSym, VReg> _symCells = new(ReferenceEqualityComparer.Instance);

    /// <summary>Each loop's exits, and how many handlers were open when it began: a break out of a try runs the finally first.</summary>
    // `Continues` is how many handlers are open where Continue leads, which is
    // not where Break leads when this entry is a SWITCH: `continue` inside one
    // belongs to the loop around it.
    private readonly List<(Block Break, Block Continue, int Handlers, int Continues)> _loops = new();

    /// <summary>Captured locals whose cell was made at the top of their block, ahead of a local function.</summary>
    private readonly HashSet<LocalDecl> _cellsMade = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Dictionary<SwitchCase, Block> Cases, int Handlers)> _switchBodies = new();

    /// <summary>Open try blocks, innermost last: a finally to run on the way out, or null for a catch.</summary>
    private readonly List<(FrameSlot Record, AstBlock? Finally)> _openHandlers = new();

    private Block? _returnBlock;
    private VReg? _returnValue;

    /// <summary>What a `return` converts its value to: the method's return type, or an async method's task result.</summary>
    private Type? _returnType;
    private int _checkedDepth;

    /// <summary>The block that reports a failed bounds check; one per function, made on demand.</summary>
    private Block? _boundsFail;

    private void ResetMethodState()
    {
        _localRegs.Clear();
        _localSlots.Clear();
        _slotRegs.Clear();
        _slotDecl.Clear();
        _addressTakenLocals.Clear();
        _addressTakenParams.Clear();
        _addressTakenSyms.Clear();
        _symSlots.Clear();
        _symCells.Clear();
        _loops.Clear();
        _switchBodies.Clear();
        _openHandlers.Clear();
        _this = null;
        _returnBlock = null;
        _returnValue = null;
        _returnType = null;
        _boundsFail = null;
        _checkedDepth = 0;
    }

    private void EmitMethod(MethodSymbol m, MethodDecl decl)
    {
        ResetMethodState();
        _method = m;
        _decl = decl;
        _in = m.Owner.Decl?.File ?? "";

        if (_moveNext.TryGetValue(m, out AsyncMethod? running))
        {
            EmitMoveNext(running);
            return;
        }

        if (m.Async)
        {
            EmitKickoff(m, decl);
            return;
        }

        _f = new Function(Label(m), IrTypes.Of(m.Returns))
        {
            SourceFile = _in, Line = decl.Line, Display = Display(m), FromLibrary = IsLibrary(m.Owner),
            Coalescible = decl.LocalCopy || m.Owner.Decl?.Specialised == true,
            Exported = m.Owner.Decl?.LocalOnly != true,
        };
        Block entry = _f.NewBlock("entry");
        _e = new Builder(_f, entry);

        if (m.Static && !m.IsCtor)
        {
            Entries[$"{m.Owner.Name}.{m.Name}"] = _f.Name;
        }

        // Parameters: `this` first, then the declared ones, each a register
        // the backend fills from wherever its convention put them.
        int declared = m.Params.Count;
        _params = new VReg[declared];
        _paramSlots = new FrameSlot?[declared];

        if (!m.Static)
        {
            _this = _f.NewReg(IrTypes.Word, "this");
            _f.Params.Add(_this);
        }

        for (int i = 0; i < declared; i++)
        {
            ParamSymbol p = m.Params[i];
            _params[i] = _f.NewReg(p.ByRef ? IrTypes.Word : IrTypes.Of(p.Type), p.Name);
            _f.Params.Add(_params[i]);
        }

        ScanAddressTaken(decl.Body!);
        MakeCapturedCells(decl.Body!);

        // A parameter whose address is taken gets a frame slot it is copied
        // into on entry; the register is never read again.
        for (int i = 0; i < declared; i++)
        {
            if (_addressTakenParams.Contains(i) && !m.Params[i].ByRef)
            {
                Type pt = m.Params[i].Type;
                FrameSlot slot = _f.NewSlot(Math.Max(4, pt.Size), Math.Min(Math.Max(4, pt.Size), _t.Align64), m.Params[i].Name);
                _paramSlots[i] = slot;
                _e.Store(new SlotOperand(slot), new RegOperand(_params[i]), 0, pt.Size);
            }
        }

        _returnBlock = _f.NewBlock("ret");
        if (!m.Returns.IsVoid)
        {
            _returnValue = _f.NewReg(IrTypes.Of(m.Returns), "result");
        }

        // A chained constructor runs before the body, on the same object.
        if (decl.Init is not null && !m.Static)
        {
            TypeSymbol? owner = decl.Init.IsThis ? m.Owner : m.Owner.Base;
            MethodSymbol? chained = _b.Chained.TryGetValue(decl, out MethodSymbol? chosen)
                ? chosen
                : owner?.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == decl.Init.Args.Count);

            if (chained is not null)
            {
                List<Operand> args = new() { new RegOperand(_this!) };
                for (int i = 0; i < decl.Init.Args.Count; i++)
                {
                    args.Add(new RegOperand(EvalAs(decl.Init.Args[i], chained.Params[i])));
                }
                CallDirect(chained, IrType.Void, args);
            }
        }

        // AND AN UNWRITTEN ONE RUNS TOO: a constructor that names no other
        // calls its base's parameterless one first, which is C#'s rule and is
        // where the base's own field initialisers live.
        if (decl.IsCtor && decl.Init is null && !m.Static
            && NearestConstructor(m.Owner.Base) is MethodSymbol inherited)
        {
            CallDirect(inherited, IrType.Void, new List<Operand> { new RegOperand(_this!) });
        }

        EmitStmt(decl.Body!);

        if (!_e.Closed)
        {
            // Falling off the end. A void method returns; a valued one cannot
            // get here in a program the binder accepted, so the register holds
            // whatever it holds.
            _e.Jump(_returnBlock);
        }

        _e.SetBlock(_returnBlock);
        _e.Ret(_returnValue is null ? null : new RegOperand(_returnValue));

        _m.Functions.Add(_f);
        _method = null;
        _decl = null;
    }

    /// <summary>
    /// The parameterless constructor of the nearest base that has any. A base
    /// with none of its own passes the question up, since its unwritten one
    /// would only have done the same.
    /// </summary>
    private static MethodSymbol? NearestConstructor(TypeSymbol? from)
    {
        for (TypeSymbol? at = from; at is not null; at = at.Base)
        {
            List<MethodSymbol> ctors = at.Methods.Where(c => c.IsCtor && !c.Static).ToList();

            if (ctors.Count > 0)
            {
                return ctors.FirstOrDefault(c => c.Params.Count == 0 && c.Decl?.Body is not null);
            }
        }
        return null;
    }

    // ---- where things live -------------------------------------------------------

    /// <summary>
    /// Finds every local and parameter whose address is taken -- by `&x`, by a
    /// `ref`/`out` argument, or by a pointer -- so it can be given memory
    /// rather than a register before anything reads it.
    /// </summary>
    private void ScanAddressTaken(Node n)
    {
        switch (n)
        {
            case RefArgExpr ra:
                MarkAddressTaken(ra.Target);
                break;
            case UnaryExpr { Op: UnOp.AddressOf } u:
                MarkAddressTaken(u.Operand);
                break;

            // AN `in` ARGUMENT IS PASSED BY ADDRESS TOO, and the call site says
            // nothing about it -- that is the whole of what `in` is. So the
            // argument does not carry the word the scan looks for, and what the
            // call resolved to has to be asked instead.
            case CallExpr call when _b.Calls.TryGetValue(call, out MethodSymbol? target)
                                 || _b.Invocations.TryGetValue(call, out target):
                for (int i = 0; i < call.Args.Count && i < target.Params.Count; i++)
                {
                    if (target.Params[i].ReadOnly)
                    {
                        MarkAddressTaken(call.Args[i]);
                    }
                }
                break;
        }
        foreach (Node child in Children(n))
        {
            ScanAddressTaken(child);
        }
    }

    /// <summary>
    /// A cell for every captured local this method has that no statement
    /// declares. One allocation each, on entry, because the cell IS the
    /// variable once something has captured it.
    /// </summary>
    private void MakeCapturedCells(Node n)
    {
        if (n is Expr e && _b.Resolved.TryGetValue(e, out Sym? sym)
            && sym is LocalSym { Boxed: true } held && DeclOf(held) is null
            && !_symCells.ContainsKey(held))
        {
            _symCells[held] = Allocate(e, Math.Max(_t.WordSize, Math.Max(1, held.Type.Size)));
        }

        // AND A PATTERN'S BINDING, whose only mention in the body may be inside
        // the lambda that captured it -- `owner.Canon is string canonical`
        // followed by `t => t.Name == canonical` reads the name nowhere else,
        // so nothing above would find a use of it to make the cell from.
        if (_b.PatternSym.TryGetValue(n, out LocalSym? bound) && bound.Boxed
            && DeclOf(bound) is null && !_symCells.ContainsKey(bound))
        {
            _symCells[bound] = Allocate(n, Math.Max(_t.WordSize, Math.Max(1, bound.Type.Size)));
        }

        foreach (Node child in Children(n))
        {
            MakeCapturedCells(child);
        }
    }

    private void MarkAddressTaken(Expr target)
    {
        if (target is not NameExpr name || !_b.Resolved.TryGetValue(name, out Sym? sym))
        {
            return;
        }
        switch (sym)
        {
            case LocalSym { Boxed: false } l:
                _addressTakenSyms.Add(l);
                foreach ((LocalDecl d, LocalSym s) in _b.LocalSymbols)
                {
                    if (ReferenceEquals(s, l))
                    {
                        _addressTakenLocals.Add(d);
                    }
                }
                break;
            case ParamSym { ByRef: false } p:
                _addressTakenParams.Add(p.Index);
                break;
        }
    }

    /// <summary>Every child node of a statement or expression, for the scans.</summary>
    /// <summary>Everything written inside one pair of initialiser braces.</summary>
    private static IEnumerable<Node> InitChildren(InitBody body)
    {
        foreach (InitAssign init in body.Inits)
        {
            if (init.Nested is InitBody nested)
            {
                foreach (Node c in InitChildren(nested))
                {
                    yield return c;
                }
            }
            else if (init.Value is Expr given)
            {
                yield return given;
            }
        }

        foreach (InitAdd add in body.Adds)
        {
            foreach (Expr e in add.Args)
            {
                yield return e;
            }
        }

        foreach (InitIndex one in body.Indexes)
        {
            foreach (Expr e in one.Args)
            {
                yield return e;
            }
            yield return one.Value;
        }
    }

    private IEnumerable<Node> Children(Node n)
    {
        switch (n)
        {
            case AstBlock b: foreach (Stmt s in b.Statements) yield return s; break;
            case LocalDecl d:
                if (d.Init is not null)
                {
                    yield return d.Init;
                }
                foreach (LocalDecl also in d.Also) yield return also;
                break;
            case UsingDeclStmt u: yield return u.Declaration; break;
            case ExprStmt e: yield return e.Expr; break;
            case IfStmt i:
                yield return i.Cond; yield return i.Then;
                if (i.Else is not null)
                {
                    yield return i.Else;
                }
                break;
            case WhileStmt w: yield return w.Cond; yield return w.Body; break;
            case DoStmt d: yield return d.Body; yield return d.Cond; break;
            case ForStmt f:
                if (f.Init is not null)
                {
                    yield return f.Init;
                }
                if (f.Cond is not null)
                {
                    yield return f.Cond;
                }
                foreach (Expr s in f.Step) yield return s;
                yield return f.Body;
                break;
            case ForeachStmt fe:
                yield return fe.Sequence; yield return fe.Body;
                if (_b.Lowered.TryGetValue(fe, out Stmt? low))
                {
                    yield return low;
                }
                break;
            case DeconstructStmt ds:
                if (_b.Lowered.TryGetValue(ds, out Stmt? low2))
                {
                    yield return low2;
                }
                break;
            case ReturnStmt r:
                if (r.Value is not null)
                {
                    yield return r.Value;
                }
                break;
            case ThrowStmt t: yield return t.Value; break;
            case SwitchStmt sw:
                yield return sw.Subject;
                foreach (SwitchCase c in sw.Cases)
                {
                    if (c.Pattern is not null)
                    {
                        yield return c.Pattern;
                    }
                    foreach (Stmt s in c.Body) yield return s;
                }
                break;
            case TryStmt ts:
                yield return ts.Body;
                // The clause itself, whose variable a lambda may capture
                // (MakeCapturedCells finds it through PatternSym), then its parts.
                foreach (CatchClause c in ts.Catches)
                {
                    yield return c;
                }
                if (ts.Finally is not null)
                {
                    yield return ts.Finally;
                }
                break;
            case CatchClause cc:
                if (cc.When is not null)
                {
                    yield return cc.When;
                }
                yield return cc.Body;
                break;
            case ThrowExpr te: yield return te.Value; break;
            case GotoCaseStmt gc:
                if (gc.Value is not null)
                {
                    yield return gc.Value;
                }
                break;
            case TupleExpr tu: foreach (Expr e in tu.Items) yield return e; break;
            case RangeExpr rg:
                if (rg.From is not null)
                {
                    yield return rg.From;
                }
                if (rg.To is not null)
                {
                    yield return rg.To;
                }
                break;
            case FromEndExpr fe2: yield return fe2.Offset; break;
            case PatternExpr p: yield return p.Subject; yield return p.Test; break;
            case SuppressExpr s: yield return s.Operand; break;
            case MemberExpr m: yield return m.Target; break;
            case CallExpr c: yield return c.Target; foreach (Expr a in c.Args) yield return a; break;
            case IndexExpr ix: yield return ix.Target; foreach (Expr a in ix.Args) yield return a; break;
            case WithExpr w:
                yield return w.Source;
                foreach (Node c in InitChildren(w.Body)) yield return c;
                break;
            case NewExpr nw:
                foreach (Expr a in nw.Args) yield return a;
                if (nw.ArraySize is not null)
                {
                    yield return nw.ArraySize;
                }
                if (nw.Elements is not null)
                {
                    foreach (Expr e in nw.Elements) yield return e;
                }
                foreach (Node c in InitChildren(nw.Body)) yield return c;
                break;
            case UnaryExpr u: yield return u.Operand; break;
            case BinaryExpr b: yield return b.Left; yield return b.Right; break;
            case AssignExpr a: yield return a.Target; yield return a.Value; break;
            case ConditionalExpr c: yield return c.Cond; yield return c.Then; yield return c.Else; break;
            case CastExpr c: yield return c.Operand; break;
            case RefArgExpr r: yield return r.Target; break;
            case IsExpr i: yield return i.Operand; break;
            case AsExpr a: yield return a.Operand; break;
            case AwaitExpr a: yield return a.Operand; break;
            case SwitchExpr sx:
                yield return sx.Subject;
                foreach (SwitchArm arm in sx.Arms)
                {
                    if (arm.Value is not null)
                    {
                        yield return arm.Value;
                    }
                    if (arm.When is not null)
                    {
                        yield return arm.When;
                    }
                    yield return arm.Result;
                }
                break;
            case LambdaExpr l:
                if (l.Body is not null)
                {
                    yield return l.Body;
                }
                if (l.BlockBody is not null)
                {
                    yield return l.BlockBody;
                }
                break;
        }
    }

    /// <summary>The register holding a compiler-numbered slot at the given type.</summary>
    private VReg SlotReg(int slot, IrType type)
    {
        if (!_slotRegs.TryGetValue((slot, type), out VReg? r))
        {
            r = _f.NewReg(type, $"s{slot}");
            _slotRegs[(slot, type)] = r;
        }
        return r;
    }

    /// <summary>The register a local declaration lives in, made at its declaration.</summary>
    private VReg LocalReg(LocalDecl d)
    {
        if (!_localRegs.TryGetValue(d, out VReg? r))
        {
            Type t = _b.LocalType.TryGetValue(d, out Type? declared) ? declared : Type.I32;
            bool boxed = _b.BoxedLocals.Contains(d);
            r = _f.NewReg(boxed ? IrTypes.Word : IrTypes.Of(t), d.Name);
            _localRegs[d] = r;
            if (_b.LocalSlot.TryGetValue(d, out int slot))
            {
                _slotDecl[slot] = d;
            }
        }
        return r;
    }

    private LocalDecl? DeclOf(LocalSym l)
    {
        if (_slotDecl.TryGetValue(l.Slot, out LocalDecl? d)
            && _b.LocalSymbols.TryGetValue(d, out LocalSym? s) && ReferenceEquals(s, l))
        {
            return d;
        }
        foreach ((LocalDecl decl, LocalSym sym) in _b.LocalSymbols)
        {
            if (ReferenceEquals(sym, l))
            {
                _slotDecl[l.Slot] = decl;
                return decl;
            }
        }
        return null;
    }

    // ---- places: where an lvalue is -------------------------------------------------

    /// <summary>
    /// An assignable location. Reading and writing one is decided here, once,
    /// rather than at each of the dozen constructs that assign.
    /// </summary>
    private abstract record Place(Type Type);

    /// <summary>A value in a register: an ordinary local or parameter.</summary>
    private sealed record RegPlace(VReg Reg, Type Type) : Place(Type);

    /// <summary>A value in memory at an address plus offset: a field, an element, a cell, a slot.</summary>
    private sealed record MemPlace(Operand Address, long Offset, Type Type, bool Volatile = false) : Place(Type);

    private VReg LoadPlace(Place p)
    {
        switch (p)
        {
            case RegPlace r:
                return r.Reg;
            case MemPlace m:
            {
                IrType it = IrTypes.Of(m.Type);
                VReg v = _e.Load(it, m.Address, m.Offset, LoadSize(m.Type), !m.Type.IsUnsigned && m.Type.Prim != Prim.Bool);
                if (m.Volatile)
                {
                    _e.Emit(Opcode.Fence, null);
                }
                return v;
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private void StorePlace(Place p, VReg value)
    {
        switch (p)
        {
            case RegPlace r:
                _e.CopyTo(r.Reg, new RegOperand(value));
                break;
            case MemPlace m:
                if (m.Volatile)
                {
                    _e.Emit(Opcode.Fence, null);
                }
                ReferenceBarrier(m, value);
                _e.Store(m.Address, new RegOperand(value), m.Offset, LoadSize(m.Type));
                break;
        }
    }

    /// <summary>
    /// THE WRITE BARRIER. A collector that marks while the program runs must
    /// hear of every reference the program overwrites, or an object reachable
    /// when the mark began can be unlinked from under it and swept while
    /// still in use. So a store of anything that may be a reference is
    /// preceded by one load and one branch -- <c>Runtime.Marking</c>, zero
    /// except while such a mark is under way -- and, when it is set, a call
    /// to <c>Runtime.WriteBarrier(slot, value)</c>, which reads what is about
    /// to be overwritten. A store into a freshly made object overwrites
    /// nothing and has no barrier: those go straight to the builder and not
    /// through here.
    ///
    /// A store through a pointer to a local is reported too; the collector
    /// reads a number that is not a reference as exactly that.
    /// </summary>
    private void ReferenceBarrier(MemPlace m, VReg value)
    {
        if (!MayHoldReference(m.Type) || value.Type != IrTypes.Word || _inBarrier)
        {
            return;
        }
        if (!_b.Types.TryGetValue(RuntimeType, out TypeSymbol? rt))
        {
            return;
        }
        FieldSymbol? flag = rt.Fields.FirstOrDefault(f => f.Static && f.Name == "Marking");
        MethodSymbol? barrier = RuntimeMethod("WriteBarrier", 2);
        if (flag is null || barrier is null)
        {
            return;                         // a runtime with no concurrent collector
        }
        // The collector's own code stores no references it needs to hear of,
        // and must not call itself.
        if (_method is { Owner.Name: "Gc" or "GcThreads" or "GcLock" or "GcRoots" or "HeapChunks" or "Runtime" or "Platform" })
        {
            return;
        }

        Require(barrier);
        _statics.Add(flag);

        Block report = _f.NewBlock("barrier");
        Block store = _f.NewBlock("stored");
        VReg marking = _e.Load(IrType.I32, new SymOperand(StaticSymbol(flag)), 0, 4);
        _e.Branch(marking, report, store);

        _e.SetBlock(report);
        _inBarrier = true;
        VReg slot = RegOf(m.Address);
        if (m.Offset != 0)
        {
            slot = _e.Binary(Opcode.Add, slot, m.Offset);
        }
        // The runtime declares both as `long`; an address is not a signed thing.
        VReg a = IrTypes.Of(barrier.Params[0].Type) == IrType.I64 && slot.Type != IrType.I64 ? _e.Unary(Opcode.ZExt32, slot) : slot;
        VReg b = IrTypes.Of(barrier.Params[1].Type) == IrType.I64 && value.Type != IrType.I64 ? _e.Unary(Opcode.ZExt32, value) : value;
        _e.Call(CallLabel(barrier), IrType.Void, R(a), R(b));
        _inBarrier = false;
        _e.Jump(store);
        _e.SetBlock(store);
    }

    private bool _inBarrier;

    /// <summary>Whether a stored value of this type may be a reference the collector follows.</summary>
    private bool MayHoldReference(Type t)
        => LoadSize(t) == _t.WordSize && !t.IsPointer
        && (HoldsReference(t) || t.ParamName is not null || t.Prim is Prim.Any);

    /// <summary>How many bytes a value of a type occupies in memory.</summary>
    private int LoadSize(Type t) => Math.Max(1, t.Size);

    /// <summary>
    /// Where a pattern's binding goes: the slot it was given, or the heap cell
    /// it was given instead when a lambda captured it.
    /// </summary>
    private void BindPattern(Node at, int slot, Type held, VReg value)
    {
        if (_b.PatternSym.TryGetValue(at, out LocalSym? named) && named.Boxed
            && _symCells.TryGetValue(named, out VReg? cell) && cell is not null)
        {
            _e.Store(new RegOperand(cell), new RegOperand(value), 0, LoadSize(held));
            return;
        }

        _e.CopyTo(SlotReg(slot, IrTypes.Of(held)), new RegOperand(value));
    }

    /// <summary>The place a resolved name denotes, or null with an error.</summary>
    private Place? PlaceOfSym(Sym sym, Node at)
    {
        switch (sym)
        {
            case LocalSym { Boxed: true } b:
            {
                LocalDecl? d = DeclOf(b);
                if (d is null)
                {
                    // No statement declared it -- an `out var`, a pattern's
                    // binding, a foreach cursor -- so its cell was made on
                    // entry instead. See MakeCapturedCells.
                    if (_symCells.TryGetValue(b, out VReg? cell) && cell is not null)
                    {
                        return new MemPlace(new RegOperand(cell), 0, b.Type);
                    }

                    Error(at, $"'{b.Name}' has no declaration");
                    return null;
                }
                // The register holds the cell's address; the value is in the cell.
                return new MemPlace(new RegOperand(LocalReg(d)), 0, b.Type);
            }

            case LocalSym l:
            {
                LocalDecl? d = DeclOf(l);
                if (d is null)
                {
                    // A binder-made local without a declaration: a foreach
                    // cursor, a pattern name, an `out var`. It has a slot
                    // number and that is all; it gets a register per type,
                    // or frame memory when something takes its address.
                    if (_addressTakenSyms.Contains(l))
                    {
                        if (!_symSlots.TryGetValue(l, out FrameSlot? made))
                        {
                            int size = Math.Max(4, l.Type.Size);
                            made = _f.NewSlot(size, Math.Min(size, _t.Align64), l.Name);
                            _symSlots[l] = made;
                        }
                        return new MemPlace(new SlotOperand(made), 0, l.Type);
                    }
                    return new RegPlace(SlotReg(l.Slot, IrTypes.Of(l.Type)), l.Type);
                }
                if (_addressTakenLocals.Contains(d))
                {
                    return new MemPlace(new SlotOperand(LocalSlot(d, l.Type)), 0, l.Type);
                }
                return new RegPlace(LocalReg(d), l.Type);
            }

            case ParamSym p:
                if (p.ByRef)
                {
                    return new MemPlace(new RegOperand(_params[p.Index]), 0, p.Type);
                }
                if (_paramSlots[p.Index] is FrameSlot ps)
                {
                    return new MemPlace(new SlotOperand(ps), 0, p.Type);
                }
                return new RegPlace(_params[p.Index], p.Type);

            case ThisSym t:
                return new RegPlace(_this!, t.Type);

            case FieldSym f:
                return PlaceOfField(f.Field, null, at);

            case CapturedFieldSym captured:
            {
                VReg env = _e.Load(IrTypes.Word, _this!, captured.Holder.Offset);
                return new MemPlace(new RegOperand(env), captured.Field.Offset, captured.Field.Type, captured.Field.Volatile);
            }

            default:
                Error(at, "this name is not a storage location");
                return null;
        }
    }

    /// <summary>The frame memory for an address-taken local.</summary>
    private FrameSlot LocalSlot(LocalDecl d, Type t)
    {
        if (!_localSlots.TryGetValue(d, out FrameSlot? s))
        {
            int size = Math.Max(4, t.Size);
            s = _f.NewSlot(size, Math.Min(size, _t.Align64), d.Name);
            _localSlots[d] = s;
        }
        return s;
    }

    /// <summary>The place a field is: a static's symbol, or an offset from an object.</summary>
    private Place PlaceOfField(FieldSymbol f, VReg? instance, Node at)
    {
        if (f.Static)
        {
            // READING OR WRITING A STATIC TOUCHES THE TYPE, which is one of
            // the three things C# says runs its initialisers.
            TouchType(f.Owner);
            _statics.Add(f);
            MemPlace place = new MemPlace(new SymOperand(StaticSymbol(f)), 0, f.Type, f.Volatile);

            // A STATIC STRUCT FIELD IS A VALUE TOO, zero until written; static
            // storage starts as zero bytes, which for a struct held by pointer
            // is a null one. One its declaration gives a value is set by the
            // type's StaticInit$ before anything can reach it; one without has
            // its zero value made the first time anything does -- read,
            // copied or written a field at a time -- as an instance field's is
            // when its object is made (InitStructFields). Put there with a
            // compare-and-swap, so that two processors touching it first agree
            // on one block and a field written into the loser's is not lost.
            if (!f.Boxed && !f.Initialised && IsStructValue(f.Type))
            {
                VReg at2 = _e.Address(StaticSymbol(f));
                VReg held = _e.Load(IrTypes.Word, at2, 0);
                Block make = _f.NewBlock("szmake");
                Block made = _f.NewBlock("szdone");
                _e.Branch(held, made, make);
                _e.SetBlock(make);
                VReg zero = NewStruct(at, f.Type.Symbol!);
                VReg old = _e.Reg(IrTypes.Word);
                _e.Emit(Opcode.AtomicCas, old, R(at2), R(_e.Const(0, IrTypes.Word)), R(zero));
                _e.Jump(made);
                _e.SetBlock(made);
            }
            return place;
        }

        VReg obj = instance ?? _this!;

        // A captured local's field holds the cell; the value is in the cell.
        if (f.Boxed)
        {
            VReg cell = _e.Load(IrTypes.Word, obj, f.Offset);
            return new MemPlace(new RegOperand(cell), 0, f.Type, f.Volatile);
        }

        return new MemPlace(new RegOperand(obj), f.Offset, f.Type, f.Volatile);
    }

    /// <summary>The place an assignable expression denotes.</summary>
    private Place? PlaceOf(Expr target)
    {
        switch (target)
        {
            case NameExpr n when _b.Resolved.TryGetValue(n, out Sym? sym):
                return PlaceOfSym(sym, n);

            case MemberExpr m when _b.Resolved.TryGetValue(m, out Sym? sym) && sym is FieldSym f:
                if (f.Field.Static)
                {
                    return PlaceOfField(f.Field, null, m);
                }
                return PlaceOfField(f.Field, Eval(m.Target), m);

            case UnaryExpr { Op: UnOp.Deref } deref:
            {
                Type pointee = _b.TypeOf(deref);
                VReg addr = Eval(deref.Operand);
                return new MemPlace(new RegOperand(addr), 0, pointee);
            }

            case IndexExpr ix when !_b.Indexers.ContainsKey(ix) && !_b.IndexSetters.ContainsKey(ix):
            {
                Type seq = _b.TypeOf(ix.Target);
                Type element = _b.TypeOf(ix);
                VReg basis = Eval(ix.Target);
                VReg index = EvalAs(ix.Args[0], Type.I32);
                return ElementPlace(basis, index, seq, element, ix);
            }

            case SubjectExpr subject when _b.Resolved.TryGetValue(subject, out Sym? where):
                return PlaceOfSym(where, subject);

            default:
                Error(target, "this assignment target is not implemented yet");
                return null;
        }
    }

    /// <summary>
    /// Where an element of an array, string or pointer is, with the bounds
    /// check an array needs. The check is against the count as an unsigned
    /// compare, so a negative index fails it too.
    /// </summary>
    private MemPlace ElementPlace(VReg basis, VReg index, Type sequence, Type element, Node at)
    {
        int stride = Math.Max(1, sequence.Prim == Prim.String ? 1 : element.Size);
        Type stored = sequence.Prim == Prim.String ? Type.U8 : element;

        if (sequence.IsPointer)
        {
            VReg scaled = stride == 1 ? index : _e.Binary(Opcode.Mul, index, stride);
            VReg addr = _e.Binary(Opcode.Add, basis, WordOf(scaled));
            return new MemPlace(new RegOperand(addr), 0, stored);
        }

        BoundsCheck(basis, index, at, sequence.IsArray);
        VReg scaled2 = stride == 1 ? index : _e.Binary(Opcode.Mul, index, stride);
        VReg addr2 = _e.Binary(Opcode.Add, basis, WordOf(scaled2));
        return new MemPlace(new RegOperand(addr2), _t.ArrayHeaderBytes, stored);
    }

    private void BoundsCheck(VReg array, VReg index, Node at, bool managedArray)
    {
        VReg count = managedArray ? _e.Unary(Opcode.ArrayLength, R(array), IrType.I32) : _e.Load(IrType.I32, array, _t.ArrayCountOffset);
        VReg ok = _e.Binary(Opcode.LtU, index, count);
        Block good = _f.NewBlock("inbounds");
        _e.Branch(ok, good, BoundsFail());
        _e.SetBlock(good);
    }

    private Block BoundsFail()
    {
        if (_boundsFail is null)
        {
            _boundsFail = _f.NewBlock("outofrange");
            Block was = _e.Block;
            _e.SetBlock(_boundsFail);
            MethodSymbol? fail = RuntimeMethod("IndexOutOfRange", 0);
            if (fail is not null)
            {
                Require(fail);
                _e.Call(CallLabel(fail), IrType.Void);
            }
            _e.Emit(Opcode.Trap, null);
            _e.Unreachable();
            _e.SetBlock(was);
        }
        return _boundsFail;
    }

    private VReg LoadElement(VReg basis, VReg index, Type element, Node at)
    {
        Type seq = Type.ArrayOf(element);
        return LoadPlace(ElementPlace(basis, index, seq, element, at));
    }

    /// <summary>An I32 index widened to the word, for address arithmetic.</summary>
    private VReg WordOf(VReg v)
    {
        if (v.Type == IrTypes.Word)
        {
            return v;
        }
        return v.Type == IrType.I64 ? _e.Unary(Opcode.Trunc64, v) : _e.Unary(Opcode.SExt32, v);
    }
}
