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

    /// <summary>Struct variables a ref local refers into, kept in one block (PlaceOfSym).</summary>
    private readonly HashSet<Sym> _refAliased = new();

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
    // A captured parameter something writes (ParamSym.Boxed): its cell, by index.
    private readonly Dictionary<int, VReg> _paramCells = new();

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

    /// <summary>
    /// The catch bodies being lowered, innermost last: how many handlers were
    /// open around each, the slot its exception is kept in, and the clause.
    /// Every way out of one but handing the exception on ends it (CatchEnd).
    /// </summary>
    private readonly List<(int Depth, FrameSlot Keep, CatchClause Clause)> _openCatches = new();

    /// <summary>The descriptor of the type each open catch takes; null for a catch of everything.</summary>
    private readonly Dictionary<FrameSlot, string?> _catchTypes = new();

    private Block? _returnBlock;
    private VReg? _returnValue;

    /// <summary>The caller's buffer this method writes its struct result to (Buffered), or null.</summary>
    private VReg? _resultBuffer;

    /// <summary>
    /// A SHARED METHOD COPY'S HIDDEN ARGUMENTS (Monomorphiser.CopyName): the
    /// descriptor of each of its type arguments, as its caller found it, 0
    /// where the caller could not. After the declared parameters and the
    /// result buffer, one word each, numbers to every analysis -- a
    /// descriptor is read-only data, never an object a region or the
    /// collector need follow. Null in every other function, a lambda's
    /// included, whose tests then answer as the copy over object.
    /// </summary>
    private VReg[]? _typeArgs;

    /// <summary>The hidden arguments the next direct call of this method passes (EmitCall, CallDirect).</summary>
    private (MethodSymbol Method, List<Operand> Args)? _pendingTypeArgs;

    /// <summary>Struct values known to be blocks of the heap, which a store into the heap may keep as they are.</summary>
    private readonly HashSet<VReg> _heapStructs = new();

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
        _refAliased.Clear();
        _symSlots.Clear();
        _symCells.Clear();
        _paramCells.Clear();
        _loops.Clear();
        _switchBodies.Clear();
        _openHandlers.Clear();
        _openCatches.Clear();
        _this = null;
        _returnBlock = null;
        _returnValue = null;
        _resultBuffer = null;
        _typeArgs = null;
        _pendingTypeArgs = null;
        _heapStructs.Clear();
        _returnType = null;
        _boundsFail = null;
        _checkedDepth = 0;
    }

    private void EmitMethod(MethodSymbol m, MethodDecl decl)
    {
        ResetMethodState();
        _method = m;
        _decl = decl;
        // The method's own file first: a partial class's methods are in several.
        _in = m.Decl is { File.Length: > 0 } own ? own.File : m.Owner.Decl?.File ?? "";

        if (_moveNext.TryGetValue(m, out AsyncMethod? running))
        {
            EmitMoveNext(running);
            return;
        }

        if (_iterators.TryGetValue(m, out IteratorMethod? iterating))
        {
            EmitIteratorMoveNext(iterating);
            return;
        }

        if (decl.Body is { Iterator: true })
        {
            EmitIteratorKickoff(m, decl);
            return;
        }

        if (m.Async)
        {
            EmitKickoff(m, decl);
            return;
        }

        _f = new Function(Label(m), ReturnIr(m))
        {
            SourceFile = _in, Line = decl.Line, Display = Display(m), FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner), SourcePath = SourcePathOf(m.Owner),
            Coalescible = decl.LocalCopy || m.Owner.Decl?.Specialised == true,
            Exported = m.Owner.Decl?.LocalOnly != true,
            NoInlining = NoInlining(decl), Unjudged = decl.AutoAccessor,
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
            _params[i].Number = !p.ByRef && NeverAddress(p.Type);
            _f.Params.Add(_params[i]);
        }

        if (Buffered(m))
        {
            _resultBuffer = _f.NewReg(IrTypes.Word, "retbuf");
            _f.Params.Add(_resultBuffer);
        }

        // A SHARED METHOD COPY'S TYPE ARGUMENTS, last (CallDirect passes them).
        int hidden = Monomorphiser.SharedMethodCopy(m.Name);
        if (hidden > 0)
        {
            _typeArgs = new VReg[hidden];
            for (int i = 0; i < hidden; i++)
            {
                _typeArgs[i] = _f.NewReg(IrTypes.Word, "targ" + i);
                _typeArgs[i].Number = true;
                _f.Params.Add(_typeArgs[i]);
            }
        }

        ScanAddressTaken(decl.Body!);
        MakeCapturedCells(decl.Body!);
        MakeParamCells(decl.Body!);

        // A parameter whose address is taken gets a frame slot it is copied
        // into on entry; the register is never read again.
        for (int i = 0; i < declared; i++)
        {
            if (_addressTakenParams.Contains(i) && !m.Params[i].ByRef)
            {
                Type pt = m.Params[i].Type;
                FrameSlot slot = _f.NewSlot(Math.Max(4, pt.Size), Math.Min(Math.Max(4, pt.Size), _t.Align64), m.Params[i].Name);
                _paramSlots[i] = slot;
                _e.Store(new SlotOperand(slot), RegOperand.Of(_params[i]), 0, pt.Size);
            }
        }

        _returnBlock = _f.NewBlock("ret");
        if (!m.Returns.IsVoid)
        {
            _returnValue = _f.NewReg(ReturnIr(m), "result");
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
                // Named arguments are evaluated where they were written and
                // passed where they belong, as `new`'s are.
                List<Operand> args = new() { RegOperand.Of(_this!) };
                if (decl.Init.ArgumentOrder.Count != 0)
                {
                    Operand[] prepared = new Operand[decl.Init.Args.Count];
                    foreach (int index in decl.Init.ArgumentOrder)
                        prepared[index] = RegOperand.Of(EvalAs(decl.Init.Args[index], chained.Params[index]));
                    args.AddRange(prepared);
                }
                else
                {
                    for (int i = 0; i < decl.Init.Args.Count; i++)
                    {
                        args.Add(RegOperand.Of(EvalAs(decl.Init.Args[i], chained.Params[i])));
                    }
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
            CallDirect(inherited, IrType.Void, new List<Operand> { RegOperand.Of(_this!) });
        }

        VReg? fromC = CalledByC(m) ? EnterFromC(decl) : null;
        if (fromC is not null && _t.WordSize == 4 && CalledStdcall(m))
        {
            // STDCALL'S CALLEE TAKES ITS ARGUMENTS OFF: every parameter a
            // word, a 64-bit one two, as the stack holds them.
            int pops = 0;
            foreach (ParamSymbol p in m.Params) pops += !p.ByRef && IrTypes.Of(p.Type) is IrType.I64 or IrType.F64 ? 8 : 4;
            _f.CalleePops = pops;
        }

        if (NativeImportOf(m) is NativeImport native)
        {
            EmitNativeBody(m, native);
        }
        else
        {
            EmitStmt(decl.Body!);
        }

        if (!_e.Closed)
        {
            // Falling off the end. A void method returns; a valued one cannot
            // get here in a program the binder accepted, so the register holds
            // whatever it holds.
            _e.Jump(_returnBlock);
        }

        _e.SetBlock(_returnBlock);
        if (fromC is not null)
        {
            LeaveToC(decl, fromC);
        }
        if (_resultBuffer is not null && _returnValue is not null)
        {
            // THE RESULT INTO THE CALLER'S BUFFER, and its address answered:
            // bytes only. A buffer is the caller's frame, or a block the caller
            // made for it and marks the cards of itself (ResultBuffer).
            _e.Emit(Opcode.MemCopy, null, R(_resultBuffer), R(_returnValue), Imm(Math.Max(1, StructOf(m.Returns).InstanceSize), IrTypes.Word));
            _e.Ret(RegOperand.Of(_resultBuffer));
        }
        else
        {
            _e.Ret(_returnValue is null ? null : RegOperand.Of(_returnValue));
        }

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
            case LocalDecl { IsRef: true, Init: RefArgExpr referred }:
                MarkRefAliased(referred.Target);
                break;
            case AssignExpr { Op: null, Value: RefArgExpr { IsOut: false, Name: null } referred }:
                MarkRefAliased(referred.Target);
                break;
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
        List<Node> children = Children(n);
        foreach (Node child in children) ScanAddressTaken(child);
        ReturnChildren(children);
    }

    /// <summary>
    /// A CELL FOR EVERY CAPTURED PARAMETER SOMETHING WRITES, holding what the
    /// caller passed: from here on the cell is the parameter, for this method
    /// and every closure over it. Found by its uses in this body and by the
    /// closures made here -- not inside them, whose parameters are their own.
    /// </summary>
    private void MakeParamCells(Node n)
    {
        if (n is Expr e && _b.Resolved.TryGetValue(e, out Sym? sym) && sym is ParamSym { Boxed: true } named)
            MakeParamCell(named, e);
        if (n is LambdaExpr lambda)
        {
            if (_b.Closures.TryGetValue(lambda, out ClosureInfo? made))
                foreach ((FieldSymbol _, Sym from) in made.Captures)
                    if (from is ParamSym { Boxed: true } captured) MakeParamCell(captured, lambda);
            return;
        }
        List<Node> children = Children(n);
        foreach (Node child in children) MakeParamCells(child);
        ReturnChildren(children);
    }

    private void MakeParamCell(ParamSym p, Node at)
    {
        if (p.Index >= _params.Length || _paramCells.ContainsKey(p.Index)) return;
        VReg cell = Allocate(at, Math.Max(_t.WordSize, Math.Max(1, p.Type.Size)));
        // A struct parameter's bytes are its caller's: the cell holds a copy (HeapStruct).
        _e.Store(RegOperand.Of(cell), RegOperand.Of(HeapStruct(at, _params[p.Index], p.Type)), 0, LoadSize(p.Type));
        _paramCells[p.Index] = cell;
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

        List<Node> children = Children(n);
        foreach (Node child in children) MakeCapturedCells(child);
        ReturnChildren(children);
    }

    /// <summary>
    /// The struct local or by-value parameter a ref local refers to, or holds
    /// a field of (PlaceOfSym): kept in one block for as long as it lives.
    /// </summary>
    private void MarkRefAliased(Expr target)
    {
        while (target is MemberExpr { Target: { } inner } && IsStructValue(_b.TypeOf(inner)))
        {
            target = inner;
        }
        if (target is NameExpr name && _b.Resolved.TryGetValue(name, out Sym? sym)
            && sym is LocalSym { IsRef: false } or ParamSym { ByRef: false })
        {
            _refAliased.Add(sym);
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
            case LocalSym { IsRef: true }:
                break;
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
    private static void InitChildren(InitBody body, List<Node> into)
    {
        foreach (InitAssign init in body.Inits)
        {
            if (init.Nested is InitBody nested)
            {
                InitChildren(nested, into);
            }
            else if (init.Value is Expr given)
            {
                into.Add(given);
            }
        }

        foreach (InitAdd add in body.Adds)
        {
            foreach (Expr e in add.Args)
            {
                into.Add(e);
            }
        }

        foreach (InitIndex one in body.Indexes)
        {
            foreach (Expr e in one.Args)
            {
                into.Add(e);
            }
            into.Add(one.Value);
        }
    }

    /// <summary>
    /// A node's children, into a list of this lowering's own, handed back with
    /// it emptied for the next (ReturnChildren): an iterator a node, as these
    /// walks were, was a heap object for every node of every body walked, a
    /// third of a gigabyte of a large unit's lowering.
    /// </summary>
    private List<Node> Children(Node n)
    {
        List<Node> into = _childLists.Count > 0 ? _childLists.Pop() : new List<Node>();
        ChildrenInto(n, into);
        return into;
    }

    private void ReturnChildren(List<Node> list)
    {
        list.Clear();
        _childLists.Push(list);
    }

    private readonly Stack<List<Node>> _childLists = new();

    private void ChildrenInto(Node n, List<Node> into)
    {
        switch (n)
        {
            case AstBlock b: foreach (Stmt s in b.Statements) into.Add(s); break;
            case LocalDecl d:
                if (d.Init is not null)
                {
                    into.Add(d.Init);
                }
                foreach (LocalDecl also in d.Also) into.Add(also);
                break;
            case UsingDeclStmt u: into.Add(u.Declaration); break;
            case ExprStmt e: into.Add(e.Expr); break;
            case IfStmt i:
                into.Add(i.Cond); into.Add(i.Then);
                if (i.Else is not null)
                {
                    into.Add(i.Else);
                }
                break;
            case WhileStmt w: into.Add(w.Cond); into.Add(w.Body); break;
            case LabeledStmt l: into.Add(l.Body); break;
            case DoStmt d: into.Add(d.Body); into.Add(d.Cond); break;
            case ForStmt f:
                if (f.Init is not null)
                {
                    into.Add(f.Init);
                }
                if (f.Cond is not null)
                {
                    into.Add(f.Cond);
                }
                foreach (Expr s in f.Step) into.Add(s);
                into.Add(f.Body);
                break;
            case ForeachStmt fe:
                into.Add(fe.Sequence); into.Add(fe.Body);
                if (_b.Lowered.TryGetValue(fe, out Stmt? low))
                {
                    into.Add(low);
                }
                break;
            case DeconstructStmt ds:
                if (_b.Lowered.TryGetValue(ds, out Stmt? low2))
                {
                    into.Add(low2);
                }
                break;
            case ReturnStmt r:
                if (r.Value is not null)
                {
                    into.Add(r.Value);
                }
                break;
            case YieldStmt y:
                if (y.Value is not null)
                {
                    into.Add(y.Value);
                }
                break;
            case ThrowStmt t: into.Add(t.Value); break;
            case SwitchStmt sw:
                into.Add(sw.Subject);
                foreach (SwitchCase c in sw.Cases)
                {
                    if (c.Pattern is not null)
                    {
                        into.Add(c.Pattern);
                    }
                    foreach (Stmt s in c.Body) into.Add(s);
                }
                break;
            case TryStmt ts:
                into.Add(ts.Body);
                // The clause itself, whose variable a lambda may capture
                // (MakeCapturedCells finds it through PatternSym), then its parts.
                foreach (CatchClause c in ts.Catches)
                {
                    into.Add(c);
                }
                if (ts.Finally is not null)
                {
                    into.Add(ts.Finally);
                }
                break;
            case CatchClause cc:
                if (cc.When is not null)
                {
                    into.Add(cc.When);
                }
                into.Add(cc.Body);
                break;
            case ThrowExpr te: into.Add(te.Value); break;
            case GotoCaseStmt gc:
                if (gc.Value is not null)
                {
                    into.Add(gc.Value);
                }
                break;
            case TupleExpr tu: foreach (Expr e in tu.Items) into.Add(e); break;
            case RangeExpr rg:
                if (rg.From is not null)
                {
                    into.Add(rg.From);
                }
                if (rg.To is not null)
                {
                    into.Add(rg.To);
                }
                break;
            case FromEndExpr fe2: into.Add(fe2.Offset); break;
            case PatternExpr p: into.Add(p.Subject); into.Add(p.Test); break;
            case SequenceExpr q: into.Add(q.Effect); into.Add(q.Value); break;
            case SuppressExpr s: into.Add(s.Operand); break;
            case MemberExpr m: into.Add(m.Target); break;
            case CallExpr c: into.Add(c.Target); foreach (Expr a in c.Args) into.Add(a); break;
            case IndexExpr ix: into.Add(ix.Target); foreach (Expr a in ix.Args) into.Add(a); break;
            case WithExpr w:
                into.Add(w.Source);
                InitChildren(w.Body, into);
                break;
            case NewExpr nw:
                foreach (Expr a in nw.Args) into.Add(a);
                if (nw.ArraySize is not null)
                {
                    into.Add(nw.ArraySize);
                }
                if (nw.Elements is not null)
                {
                    foreach (Expr e in nw.Elements) into.Add(e);
                }
                InitChildren(nw.Body, into);
                break;
            case UnaryExpr u: into.Add(u.Operand); break;
            case BinaryExpr b: into.Add(b.Left); into.Add(b.Right); break;
            case AssignExpr a: into.Add(a.Target); into.Add(a.Value); break;
            case ConditionalExpr c: into.Add(c.Cond); into.Add(c.Then); into.Add(c.Else); break;
            case CastExpr c: into.Add(c.Operand); break;
            case RefArgExpr r: into.Add(r.Target); break;
            case IsExpr i: into.Add(i.Operand); break;
            case AsExpr a: into.Add(a.Operand); break;
            case AwaitExpr a: into.Add(a.Operand); break;
            case SwitchExpr sx:
                into.Add(sx.Subject);
                foreach (SwitchArm arm in sx.Arms)
                {
                    if (arm.Value is not null)
                    {
                        into.Add(arm.Value);
                    }
                    if (arm.When is not null)
                    {
                        into.Add(arm.When);
                    }
                    into.Add(arm.Result);
                }
                break;
            case LambdaExpr l:
                if (l.Body is not null)
                {
                    into.Add(l.Body);
                }
                if (l.BlockBody is not null)
                {
                    into.Add(l.BlockBody);
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
            Type t = _b.LocalType.TryGetValue(d, out Type? declared) ? declared
                   : d.Init is not null ? _b.TypeOf(d.Init) : Type.I32;
            bool boxed = _b.BoxedLocals.Contains(d);
            r = _f.NewReg(boxed || d.IsRef ? IrTypes.Word : IrTypes.Of(t), d.Name);
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
        LocalDecl? found = _b.DeclOf(l);
        if (found is not null)
        {
            _slotDecl[l.Slot] = found;
        }
        return found;
    }

    // ---- places: where an lvalue is -------------------------------------------------

    /// <summary>
    /// An assignable location. Reading and writing one is decided here, once,
    /// rather than at each of the dozen constructs that assign.
    /// </summary>
    private abstract record Place(Type Type);

    /// <summary>A value in a register: an ordinary local or parameter.</summary>
    private sealed record RegPlace(VReg Reg, Type Type) : Place(Type);

    /// <summary>
    /// A value in memory at an address plus offset: a field, an element, a
    /// cell, a slot. `Inline`: a struct held in line (FieldSymbol.Inline) --
    /// the bytes there ARE the struct, so its value is their address and a
    /// store copies bytes in.
    /// </summary>
    private sealed record MemPlace(Operand Address, long Offset, Type Type, bool Volatile = false, bool Inline = false, VReg? CovariantArray = null, FieldSymbol? Field = null) : Place(Type);

    /// <summary>A field's name as the IR carries it on the loads and stores of it (Instr.Field).</summary>
    private static string FieldKey(FieldSymbol f) => f.KeyMade ??= TypeKey(f.Owner) + "::" + f.Name;

    /// <summary>
    /// WHICH FIELD A LOAD OR STORE IS, for region inference (Instr.Family):
    /// an instance field of a class, accessed at its own offset from the
    /// object's start -- as C# compiles `o.f`, the object of the class or of
    /// one derived from it. Named by its declaring class and its name; a
    /// specialisation of a generic class by its template and arity, so the
    /// shared copy's code and a copy's own name one field one way. Null for
    /// a static, a struct's field (held in a local, an array or a class:
    /// reached through an address into something), and an access at any
    /// other offset: what region inference cannot know the class of.
    /// And null for a field that holds no reference: a family names a
    /// REFERENCE field, so a load of one that finds a word never read as a
    /// reference is a load from an object of another class, which reads
    /// nothing (RegionEscape.Loaded). A number, an address kept as one
    /// among them, stays the untyped word's, "the unknown object at most".
    /// </summary>
    private static string? FieldFamily(FieldSymbol f, long offset)
    {
        if (f.Static || f.Owner.Kind != TypeKind.Class || f.Inline || f.Offset <= 0 || offset != f.Offset || HoldsNumber(f.Type)) return null;
        if (f.FamilyMade is string made) return made;
        TypeSymbol owner = f.Owner;
        string declaring = owner.Decl is { Specialised: true, Template: string template } d ? template + "`" + d.TemplateArgs.Count : TypeKey(owner);
        return f.FamilyMade = declaring + "::" + f.Name;
    }

    /// <summary>
    /// A number and only that: no class, struct or array, nothing nullable,
    /// and not `object` or a type parameter, which may hold a reference
    /// (Prim.Any). An enum is its number.
    /// </summary>
    private static bool HoldsNumber(Corsac.Lang.Type ty)
        => !ty.IsArray && !ty.IsNullableValue && ty.Symbol is not { Kind: TypeKind.Class or TypeKind.Interface or TypeKind.Struct }
            && ty.Prim is Prim.Bool or Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64
                or Prim.NInt or Prim.NUInt or Prim.F32 or Prim.F64 or Prim.Char;

    /// <summary>Whether a field's loads and stores carry it (Instr.Field): a reference, held by a class or statically.</summary>
    private bool TagsField(FieldSymbol f) => HoldsReference(f.Type) && (f.Static || f.Owner.Kind == TypeKind.Class);

    private VReg LoadPlace(Place p)
    {
        switch (p)
        {
            case RegPlace r:
                return r.Reg;
            case MemPlace { Inline: true } held:
            {
                // A struct held in line: the value is where its bytes are.
                // Every consumer that keeps it copies it (EvalAs, CopyStruct),
                // as it copies any struct it did not just make.
                if (held.Volatile)
                {
                    _e.Emit(Opcode.Fence, null);
                }
                VReg basis = RegOf(held.Address);
                return held.Offset == 0 ? basis : _e.Binary(Opcode.Add, basis, held.Offset);
            }
            case MemPlace m:
            {
                IrType it = IrTypes.Of(m.Type);
                VReg v = _e.Load(it, m.Address, m.Offset, LoadSize(m.Type), !m.Type.IsUnsigned && m.Type.Prim != Prim.Bool);
                if (m.Field is FieldSymbol read && TagsField(read)) _e.Block.Instrs[^1].Field = FieldKey(read);
                if (m.Field is FieldSymbol readFamily && m.Address is RegOperand) _e.Block.Instrs[^1].Family = FieldFamily(readFamily, m.Offset);
                if (NeverAddress(m.Type)) _e.Block.Instrs[^1].Number = true;
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
                _e.CopyTo(r.Reg, RegOperand.Of(value));
                break;
            case MemPlace { Inline: true } held:
            {
                // Its bytes copied in from the value's: no pointer stored,
                // nothing made. Each reference in it is written over as any
                // stored reference is: the old one reported to a marking
                // collector first, the card marked after.
                if (held.Volatile)
                {
                    _e.Emit(Opcode.Fence, null);
                }
                VReg basis = RegOf(held.Address);
                VReg into = held.Offset == 0 ? basis : _e.Binary(Opcode.Add, basis, held.Offset);
                int bytes = Math.Max(1, StructOf(held.Type).InstanceSize);
                List<(int Offset, VReg Value)> references = new();
                // A STRUCT'S REFERENCES STORED AS SEQUENCES, where stores are
                // (StoreSequences): each written alone first, barrier and card
                // with it, as a field is, and then the bytes copied over them
                // with the same words. A stop between a barrier's test and a
                // copy that takes the whole struct at once would let the copy
                // land over a reference nobody reported.
                if (MakesStoreSequences)
                {
                    foreach ((int offset, Type type) in TracedFields(StructOf(held.Type), 0))
                    {
                        if (!MayHoldReference(type)) continue;
                        VReg word = _e.Load(IrTypes.Word, value, offset);
                        MemPlace place = new(R(into), offset, type);
                        ReferenceBarrier(place, word);
                        _e.Store(R(into), R(word), offset, _t.WordSize);
                        CardMark(place, word);
                    }
                    _e.Emit(Opcode.MemCopy, null, R(into), R(value), Imm(bytes, IrTypes.Word));
                    break;
                }
                foreach ((int offset, Type type) in TracedFields(StructOf(held.Type), 0))
                {
                    if (!MayHoldReference(type)) continue;
                    VReg word = _e.Load(IrTypes.Word, value, offset);
                    references.Add((offset, word));
                    ReferenceBarrier(new MemPlace(R(into), offset, type), word);
                    CardMarkAhead(new MemPlace(R(into), offset, Type.String), word);
                }
                _e.Emit(Opcode.MemCopy, null, R(into), R(value), Imm(bytes, IrTypes.Word));
                foreach ((int offset, VReg word) in references)
                {
                    CardMark(new MemPlace(R(into), offset, Type.String), word);
                }
                break;
            }
            case MemPlace m:
                if (m.Volatile)
                {
                    _e.Emit(Opcode.Fence, null);
                }
                if (m.CovariantArray is VReg array)
                {
                    StoreCheck(array, value, m.Type);
                }
                // A struct kept as a pointer in memory is a block of the heap,
                // not a caller's result buffer (HeapStruct).
                if (IsStructValue(m.Type)) value = HeapStruct(_decl ?? (Node)new MethodDecl { Name = "", Line = 0, Col = 0 }, value, m.Type);
                ReferenceBarrier(m, value);
                CardMarkAhead(m, value);
                _e.Store(m.Address, RegOperand.Of(value), m.Offset, LoadSize(m.Type));
                if (m.Field is FieldSymbol written && TagsField(written)) _e.Block.Instrs[^1].Field = FieldKey(written);
                if (m.Field is FieldSymbol writtenFamily && m.Address is RegOperand) _e.Block.Instrs[^1].Family = FieldFamily(writtenFamily, m.Offset);
                CardMark(m, value);
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
        // AND ITS VALUE FORM, which the optimiser makes of a barrier whose
        // object it keeps in registers (ScalarObjects); present, so that it can.
        if (RuntimeMethod("WriteBarrierValues", 2) is MethodSymbol values)
        {
            Require(values);
        }
        RequireSequenceRoutines();
        _statics.Add(flag);

        Block report = _f.NewBlock("barrier");
        Block store = _f.NewBlock("stored");
        VReg marking = Numbered(_e, _e.Load(IrType.I32, new SymOperand(StaticSymbol(flag)), 0, 4));
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

    /// <summary>
    /// Whether this compile's reference stores are made as sequences
    /// (StoreSequences): asked, on i386, of a runtime with a concurrent
    /// collector's barrier and a card table, which the sequences read.
    /// </summary>
    /// <summary>
    /// What the store sequences call (X86Backend, "THE STORE SEQUENCES"):
    /// Runtime.WriteBarrier, and the three routines that report, store and
    /// mark the card while a mark is under way. The backend writes the stubs
    /// only into a module that has all four, and a program compiled whole,
    /// with no link to keep them, had only the first: its stubs were never
    /// written, and its calls of them did not link.
    /// </summary>
    private void RequireSequenceRoutines()
    {
        if (!MakesStoreSequences) return;
        foreach ((string name, int arity) in new[] { ("WriteBarrier", 2), ("WriteBarrierStore", 2), ("WriteBarrierExchange", 2), ("WriteBarrierCompareExchange", 3) })
            if (RuntimeMethod(name, arity) is MethodSymbol routine) Require(routine);
    }

    private bool MakesStoreSequences
        => StoreSequences && _t.Name == "x86" && _b.Types.TryGetValue(RuntimeType, out TypeSymbol? rt)
            && rt.Fields.Any(f => f.Static && f.Name == "Marking") && rt.Fields.Any(f => f.Static && f.Name == "Cards")
            && RuntimeMethod("WriteBarrier", 2) is not null;

    /// <summary>
    /// Whether an array whose elements are written as this type may be one
    /// of a type derived from it, so that a store into it must be checked:
    /// object, an interface, a class that is not sealed. Not a sealed class
    /// or a string -- nothing derives from them -- nor a value type; and not
    /// a type parameter's, which in shared generic code is a word of any
    /// type: List&lt;T&gt; stores into its T[] at every Add, and the check
    /// there would cost every list on a slow processor what .NET's own
    /// shared code pays (a departure recorded in X86-BACKEND.md).
    /// </summary>
    private static bool MayBeCovariant(Type element)
        => element.ParamName is null && !element.IsNullableValue && !element.IsPointer
        && (element.Prim == Prim.Any
            || element.Symbol is { Kind: TypeKind.Interface }
            || element.Symbol is { Kind: TypeKind.Class } c && c.Decl is { } d && !d.Mods.HasFlag(Mods.Sealed) && !c.Structural);

    /// <summary>
    /// THE COVARIANT STORE'S CHECK: a value stored into an array that may be
    /// one of a derived element type. Nothing when the value is null or the
    /// array is exactly the type written -- one load and one compare -- and
    /// otherwise Runtime.ArrayStoreCheck, which refuses a Cat stored into a
    /// Dog[] held as an Animal[] with ArrayTypeMismatchException.
    /// </summary>
    private void StoreCheck(VReg array, VReg value, Type element)
    {
        if (RuntimeMethod("ArrayStoreCheck", 2) is not MethodSymbol check || value.Type != IrTypes.Word)
        {
            return;
        }
        Block notNull = _f.NewBlock("stchk");
        Block slow = _f.NewBlock("stslow");
        Block done = _f.NewBlock("stdone");
        _e.Branch(value, notNull, done);
        _e.SetBlock(notNull);
        VReg vt = _e.Load(IrTypes.Word, array, 0);
        VReg exact = _e.Address(SequenceDescriptor(ElementKey(element), ElementStride(element), isString: false, elementType: element), _t.DescriptorBytes);
        Block byElement = _f.NewBlock("stelem");
        _e.Branch(_e.Binary(Opcode.Eq, R(vt), R(exact), IrType.I32), done, byElement);
        // OR THE VALUE IS EXACTLY THE ARRAY'S ELEMENT, or the array holds
        // any object: what a shared generic copy's T[] -- a string[] since
        // it reads its arguments (TypeContext) -- meets at every List.Add,
        // never the type written there.
        _e.SetBlock(byElement);
        int w = _t.WordSize;
        VReg held = _e.Load(IrTypes.Word, vt, (long)DescElement * w - _t.DescriptorBytes);
        Block compare = _f.NewBlock("stsame");
        _e.Branch(held, compare, done);
        _e.SetBlock(compare);
        VReg described = _e.Binary(Opcode.Sub, _e.Load(IrTypes.Word, value, 0), _t.DescriptorBytes);
        _e.Branch(_e.Binary(Opcode.Eq, R(held), R(described), IrType.I32), done, slow);
        _e.SetBlock(slow);
        Require(check);
        _e.Call(CallLabel(check), IrType.Void, R(AsParam(array, check.Params[0].Type)), R(AsParam(value, check.Params[1].Type)));
        _e.Jump(done);
        _e.SetBlock(done);
    }

    /// <summary>
    /// A coroutine saves its frame into its machine at each suspension and
    /// marks the machine's cards there (AsyncTransform), when the runtime has
    /// cards: the helper it calls is then part of the program.
    /// </summary>
    private void RequireCardMarkObject()
    {
        if (_b.Types.TryGetValue(RuntimeType, out TypeSymbol? rt) && rt.Fields.Any(f => f.Static && f.Name == "Cards")
            && RuntimeMethod("CardMarkObject", 1) is MethodSymbol helper)
        {
            Require(helper);
        }
    }

    /// <summary>
    /// The mark before the store as well, where a thread can be stopped
    /// between the two (CardMarkBefore): nothing elsewhere.
    /// </summary>
    private void CardMarkAhead(MemPlace m, VReg value)
    {
        if (CardMarkBefore) CardMark(m, value);
    }

    /// <summary>
    /// THE CARD MARK, after the store, as a generational collector needs it:
    /// the byte for the kilobyte the reference went into is set, so the next
    /// minor collection reads that kilobyte for pointers old objects hold into
    /// young ones (Gc, generations). A load of <c>Runtime.Cards</c>, a test, a
    /// shift, an add and a byte store; nothing when the table is 0 -- a
    /// freestanding image, a 64-bit one, one whose collector has no
    /// generations. After, not before: a collection that clears the card
    /// between a mark and the store it stands for would miss the store --
    /// and in an image whose threads stop anywhere, before as well
    /// (CardMarkBefore, CardMarkAhead).
    /// Where the Marking test is omitted -- the collector's own code, a
    /// runtime without Cards -- so is this.
    /// </summary>
    private void CardMark(MemPlace m, VReg value)
    {
        if (!MayHoldReference(m.Type) || value.Type != IrTypes.Word)
        {
            return;
        }
        CardMarkAt(m.Address, m.Offset);
    }

    /// <summary>
    /// A reference written straight into a block being made -- an array's
    /// elements, a box, a cell, a struct's own block, a state machine's
    /// fields -- which takes no barrier (the block is new: nothing in it is
    /// overwritten) but DOES take the card mark. A collection can fall
    /// between the block's allocation and these stores, and find it and make
    /// it old; the stores that follow are old-to-young pointers like any
    /// other, and the card is how the next minor collection hears of them.
    /// </summary>
    private void StoreNew(VReg block, VReg value, long offset, Type type, FieldSymbol? field = null)
    {
        if (IsStructValue(type)) value = HeapStruct(_decl ?? (Node)new MethodDecl { Name = "", Line = 0, Col = 0 }, value, type);
        CardMarkAhead(new MemPlace(R(block), offset, type), value);
        _e.Store(R(block), R(value), offset, LoadSize(type));
        if (field is not null && TagsField(field)) _e.Block.Instrs[^1].Field = FieldKey(field);
        if (field is not null) _e.Block.Instrs[^1].Family = FieldFamily(field, offset);
        CardMark(new MemPlace(R(block), offset, type), value);
    }

    /// <summary>StoreNew for a word the caller knows is a reference -- a struct's block, an object.</summary>
    private void StoreNewReference(VReg block, VReg value, long offset)
    {
        if (CardMarkBefore) CardMarkAt(R(block), offset);
        _e.Store(R(block), R(value), offset, _t.WordSize);
        CardMarkAt(R(block), offset);
    }

    private void CardMarkAt(Operand address, long offset)
    {
        if (_inBarrier)
        {
            return;
        }
        if (!_b.Types.TryGetValue(RuntimeType, out TypeSymbol? rt)
            || rt.Fields.FirstOrDefault(f => f.Static && f.Name == "Cards") is not FieldSymbol cards)
        {
            return;
        }
        // NOT IN THE COLLECTOR'S AND THE RUNTIME'S OWN CODE -- EXCEPT A STATIC'S
        // STORE. A minor collection reads only the statics' cards that are
        // set (Gc.ScanStatics), so a reference stored into a static with no
        // card is one it never sees: Runtime's stop signal and CRC tables,
        // a bare-metal Platform's console. The mark is a byte stored into
        // the card table, committed for the statics from the moment it
        // exists (Gc.StartGenerations), and calls nothing.
        if (_method is { Owner.Name: "Gc" or "GcThreads" or "GcLock" or "GcRoots" or "HeapChunks" or "Runtime" or "Platform" }
            && address is not SymOperand)
        {
            return;
        }
        if (RuntimeMethod("CardMark", 1) is not MethodSymbol mark)
        {
            return;
        }
        // A CALL UNTIL THE LAST PASS, which writes it out (CardMarks): to the
        // lifetime passes it is a note to the collector, as the barrier is.
        _statics.Add(cards);
        Require(mark);
        VReg slot = RegOf(address);
        if (offset != 0)
        {
            slot = _e.Binary(Opcode.Add, slot, offset);
        }
        VReg arg = IrTypes.Of(mark.Params[0].Type) == IrType.I64 && slot.Type != IrType.I64 ? _e.Unary(Opcode.ZExt32, slot) : slot;
        _e.Call(CallLabel(mark), IrType.Void, R(arg));
    }

    /// <summary>Whether a stored value of this type may be a reference the collector follows.</summary>
    private bool MayHoldReference(Type t)
        => LoadSize(t) == _t.WordSize && !t.IsPointer
        && (HoldsReference(t) || t.ParamName is not null || t.Prim is Prim.Any);

    /// <summary>
    /// A NUMBER THAT IS NEVER AN ADDRESS, for region inference (VReg.Number,
    /// Instr.Number): on a 32-bit target a pointer and an int are both I32,
    /// and an int handed to a callee that throws, or widened to a long, read
    /// as an address that escapes. Bool, char, the integers of thirty-two
    /// bits or fewer, float, double, and an enum held in one of those. Not a
    /// long or a nint -- the runtime keeps addresses in both -- nor a
    /// pointer, a reference, an array, a nullable's cell, a struct or a type
    /// parameter. An address is never cast to an int here; the runtime goes
    /// through nint.
    /// </summary>
    private static bool NeverAddress(Type t)
    {
        if (t.IsPointer || t.IsArray || t.IsReference || t.IsNullableValue || t.ParamName is not null || t.Function is not null) return false;
        if (t.IsEnumValue) return t.Symbol!.EnumUnderlying is not (Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt);
        return t.Symbol is null && t.Prim is Prim.Bool or Prim.Char or Prim.I8 or Prim.I16 or Prim.I32
            or Prim.U8 or Prim.U16 or Prim.U32 or Prim.F32 or Prim.F64;
    }

    /// <summary>
    /// THE COUNT A STRING OR A SEQUENCE KEEPS in its header, read: a number,
    /// never an address (Instr.Number), as a field of int is. Unmarked, a
    /// string's Length on a 32-bit target was a word read out of the string
    /// for region inference -- whatever its summary said the string's words
    /// held -- and a sum of lengths handed back from a boundary carried
    /// objects to its caller: 1290's Work answered Sum(s) plus a row's
    /// name's Length, and its rows, merged with the strings in Sheet.Make's
    /// summary, were the unknown object's in Run when the total was printed.
    /// </summary>
    private VReg CountOf(Builder e, VReg of)
        => Numbered(e, e.Load(IrType.I32, of, _t.ArrayCountOffset));

    /// <summary>
    /// A LOAD JUST MADE, MARKED A NUMBER (Instr.Number) where `number` says
    /// it is never an address: a type's flags, depth or entry count read
    /// from its descriptor; a Nullable's has-value byte; a box's value of a
    /// number type (NeverAddress); a float's bits; a string's characters; an
    /// iterator's state or a view's cursor; a machine's size; errno. Region
    /// inference takes an unmarked word read out of an object for whatever
    /// that object's words hold -- on a 32-bit target an int and an address
    /// are one word -- and a number made from one carried objects wherever
    /// it went (1290's string Length). Only `read`, the load the builder
    /// made last, is marked.
    /// </summary>
    private static VReg Numbered(Builder e, VReg read, bool number = true)
    {
        Instr last = e.Block.Instrs[^1];
        if (number && last.Op == Opcode.Load && ReferenceEquals(last.Dest, read)) last.Number = true;
        return read;
    }

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
            _e.Store(RegOperand.Of(cell), RegOperand.Of(HeapStruct(at, value, held)), 0, LoadSize(held));
            return;
        }

        _e.CopyTo(SlotReg(slot, IrTypes.Of(held)), RegOperand.Of(value));
    }

    /// <summary>The place a resolved name denotes, or null with an error.</summary>
    private Place? PlaceOfSym(Sym sym, Node at)
    {
        Place? storage = StorageOfSym(sym, at);

        // A STRUCT VARIABLE A REF LOCAL REFERS TO IS ITS BLOCK, in place
        // (ScanAddressTaken's _refAliased). Assigning the variable a whole new
        // value otherwise points it at a new block, and the ref local -- the
        // address of the old one -- would go on reading what was there. So it
        // is held as a struct in line is: read as the address of its bytes,
        // written by copying into them, and both names see every write.
        if (storage is not null and not MemPlace { Inline: true } && _refAliased.Contains(sym) && IsStructValue(storage.Type))
        {
            return new MemPlace(RegOperand.Of(StructBlock(storage, at, storage.Type)), 0, storage.Type, false, true);
        }
        return storage;
    }

    /// <summary>Where a resolved name's own storage is: a register, a slot, a cell.</summary>
    private Place? StorageOfSym(Sym sym, Node at)
    {
        switch (sym)
        {
            // A REF LOCAL HOLDS AN ADDRESS, as a by-reference parameter does:
            // its place is what is there. A struct one is the address of the
            // struct's bytes, read as that address and written by a copy in.
            case LocalSym { IsRef: true } reference:
            {
                LocalDecl? d = DeclOf(reference);
                if (d is null)
                {
                    Error(at, $"'{reference.Name}' has no declaration");
                    return null;
                }
                return new MemPlace(RegOperand.Of(LocalReg(d)), 0, reference.Type, false, IsStructValue(reference.Type));
            }

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
                        return new MemPlace(RegOperand.Of(cell), 0, b.Type);
                    }

                    Error(at, $"'{b.Name}' has no declaration");
                    return null;
                }
                // The register holds the cell's address; the value is in the cell.
                return new MemPlace(RegOperand.Of(LocalReg(d)), 0, b.Type);
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
                    // A struct by reference is the address of its bytes
                    // (StructReference): read as that address, written by a
                    // copy into it, as a struct held in line is. A captured
                    // variable's cell (ParamSym.Cell) holds the struct as a
                    // boxed local's does.
                    return new MemPlace(RegOperand.Of(_params[p.Index]), 0, p.Type, false, IsStructValue(p.Type) && !p.Cell);
                }
                if (_paramCells.TryGetValue(p.Index, out VReg? paramCell))
                {
                    return new MemPlace(RegOperand.Of(paramCell), 0, p.Type);
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
                return new MemPlace(RegOperand.Of(env), captured.Field.Offset, captured.Field.Type, captured.Field.Volatile);
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
            FieldSymbol store = SharedStatic(f);
            _statics.Add(store);
            // [ThreadStatic]: this thread's cell for the field, made the first
            // time this thread touches it. No field tag: the cell is no static's
            // storage, and nothing shares it with another thread.
            VReg? cell = store.ThreadStatic ? ThreadStaticCell(store, at) : null;
            MemPlace place = cell is not null
                ? new MemPlace(RegOperand.Of(cell), 0, f.Type, f.Volatile)
                : new MemPlace(new SymOperand(StaticSymbol(store)), 0, f.Type, f.Volatile, Field: store);

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
                VReg at2 = cell ?? _e.Address(StaticSymbol(store));
                VReg held = _e.Load(IrTypes.Word, at2, 0);
                Block make = _f.NewBlock("szmake");
                Block made = _f.NewBlock("szdone");
                _e.Branch(held, made, make);
                _e.SetBlock(make);
                VReg zero = NewStruct(at, StructOf(f.Type));
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
            return new MemPlace(RegOperand.Of(cell), 0, f.Type, f.Volatile);
        }

        return new MemPlace(RegOperand.Of(obj), f.Offset, f.Type, f.Volatile, f.Inline, Field: f);
    }

    /// <summary>
    /// A [ThreadStatic] field's cell on this thread: Runtime.ThreadStaticCell,
    /// handed the word with the field's number and the bytes a cell needs --
    /// a static's own size, a struct held by pointer being a word.
    /// </summary>
    private VReg ThreadStaticCell(FieldSymbol f, Node at)
    {
        if (RuntimeMethod("ThreadStaticCell", 2) is not MethodSymbol helper)
        {
            Error(at, $"[ThreadStatic] needs {RuntimeType}.ThreadStaticCell, which no compiled source provides; compile with the system library");
            return _e.Const(0, IrTypes.Word);
        }
        Require(helper);
        long bytes = Math.Max(_t.WordSize, Math.Max(1, f.Type.Size));
        VReg cell = _e.Call(CallLabel(helper), IrTypes.Of(helper.Returns), R(_e.Address(ThreadStaticIndex(f))), R(_e.Const(bytes, IrTypes.Word)))!;
        return cell.Type == IrTypes.Word ? cell : _e.Unary(Opcode.Trunc64, cell);
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
                return new MemPlace(RegOperand.Of(addr), 0, pointee);
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

            case CallExpr call when RefCallee(call) is MethodSymbol answers:
                return RefCallPlace(call, answers);

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
        int stride = sequence.Prim == Prim.String ? 2 : sequence.IsPointer ? Math.Max(1, element.Size) : ElementStride(element);
        Type stored = sequence.Prim == Prim.String ? Type.Char : element;

        if (sequence.IsPointer)
        {
            VReg scaled = stride == 1 ? index : _e.Binary(Opcode.Mul, index, stride);
            VReg addr = _e.Binary(Opcode.Add, basis, WordOf(scaled));
            return new MemPlace(RegOperand.Of(addr), 0, stored);
        }

        BoundsCheck(basis, index, at, sequence.IsArray);
        VReg scaled2 = stride == 1 ? index : _e.Binary(Opcode.Mul, index, stride);
        VReg addr2 = _e.Binary(Opcode.Add, basis, WordOf(scaled2));
        return new MemPlace(RegOperand.Of(addr2), _t.ArrayHeaderBytes, stored,
            Inline: sequence.Prim != Prim.String && InlineElement(stored),
            CovariantArray: sequence.IsArray && MayBeCovariant(stored) ? basis : null);
    }

    private void BoundsCheck(VReg array, VReg index, Node at, bool managedArray)
    {
        VReg count = managedArray ? _e.Unary(Opcode.ArrayLength, R(array), IrType.I32) : CountOf(_e, array);
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
