#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    private void EmitStmt(Stmt s)
    {
        // WHICH LINE THE READER IS ON. Everything this statement becomes is
        // stamped with it on its way into a block, which is what lets a stack
        // trace say `in file.cor:line N` rather than naming the function and
        // stopping there.
        if (s.Line > 0)
        {
            _e.Line = s.Line;
        }

        if (_e.Closed)
        {
            // Control already left: a return, throw, break or continue above.
            // Anything after it in the block is dead, and emitting it would
            // start an unreachable block nobody jumps to.
            return;
        }

        switch (s)
        {
            case AstBlock b:
            {
                int outside = _checkedDepth;
                if (b.ArithmeticContext != 0)
                    _checkedDepth = b.ArithmeticContext == 1 ? 1 : 0;
                try
                {
                // A LOCAL FUNCTION IS CALLABLE FROM THE TOP OF ITS BLOCK, which is
                // C#'s rule and is how they are written: `return flow;` and then
                // the helpers, below everything that calls them. Its closure is
                // made where the block starts, not where the text is -- made at
                // the text, one written after the return was never made at all
                // and the call went through whatever the register held.
                //
                // What it captures has to exist first, so the cells of this
                // block's own captured locals are made here too, and their
                // declarations fill them in when they are reached.
                if (b.Statements.Any(st => st is LocalDecl { LocalFunction: true }))
                {
                    foreach (LocalDecl early in b.Statements.OfType<LocalDecl>()
                                                 .SelectMany(d => d.Also.Prepend(d)))
                    {
                        if (_b.BoxedLocals.Contains(early) && _cellsMade.Add(early))
                        {
                            Type held = _b.LocalType.TryGetValue(early, out Type? known) ? known : Type.I32;

                            _e.CopyTo(LocalReg(early),
                                      new RegOperand(Allocate(early, Math.Max(_t.WordSize, held.Size))));
                        }
                    }

                    foreach (LocalDecl function in b.Statements.OfType<LocalDecl>().Where(d => d.LocalFunction))
                    {
                        EmitLocalDecl(function);
                    }
                }

                foreach (Stmt inner in b.Statements)
                {
                    if (inner is LocalDecl { LocalFunction: true })
                    {
                        continue;
                    }

                    EmitStmt(inner);
                    if (_e.Closed)
                    {
                        break;
                    }
                }
                }
                finally { _checkedDepth = outside; }
                break;
            }

            case LocalDecl d:
                EmitLocalDecl(d);
                break;

            case UsingDeclStmt u:
                // `using var x = ...;` -- the declaration, and Dispose on the
                // way out of the enclosing block. The binder rewrote the block
                // if it needed to; here it is the declaration.
                EmitLocalDecl(u.Declaration);
                break;

            case ExprStmt e:
                Eval(e.Expr);
                break;

            case IfStmt i:
            {
                if (Fold.TryConst(i.Cond, out long known))
                {
                    if (known != 0)
                    {
                        EmitStmt(i.Then);
                    }
                    else if (i.Else is not null)
                    {
                        EmitStmt(i.Else);
                    }
                    break;
                }

                Block then = _f.NewBlock("then");
                Block end = _f.NewBlock("endif");
                Block els = i.Else is null ? end : _f.NewBlock("else");

                BranchOn(i.Cond, then, els);
                _e.SetBlock(then);
                EmitStmt(i.Then);
                if (!_e.Closed)
                {
                    _e.Jump(end);
                }

                if (i.Else is not null)
                {
                    _e.SetBlock(els);
                    EmitStmt(i.Else);
                    if (!_e.Closed)
                    {
                        _e.Jump(end);
                    }
                }
                _e.SetBlock(end);
                break;
            }

            case WhileStmt w:
            {
                if (Fold.TryConst(w.Cond, out long known) && known == 0)
                {
                    break;
                }

                Block top = _f.NewBlock("while");
                Block body = _f.NewBlock("whilebody");
                Block end = _f.NewBlock("endwhile");

                _e.Jump(top);
                _e.SetBlock(top);
                BranchOn(w.Cond, body, end);
                _e.SetBlock(body);
                _loops.Add((end, top, _openHandlers.Count, _openHandlers.Count));
                EmitStmt(w.Body);
                _loops.RemoveAt(_loops.Count - 1);
                if (!_e.Closed)
                {
                    _e.Jump(top);
                }
                _e.SetBlock(end);
                break;
            }

            case DoStmt d2:
            {
                Block top = _f.NewBlock("do");
                Block cond = _f.NewBlock("docond");
                Block end = _f.NewBlock("enddo");

                _e.Jump(top);
                _e.SetBlock(top);
                _loops.Add((end, cond, _openHandlers.Count, _openHandlers.Count));
                EmitStmt(d2.Body);
                _loops.RemoveAt(_loops.Count - 1);
                if (!_e.Closed)
                {
                    _e.Jump(cond);
                }
                _e.SetBlock(cond);
                BranchOn(d2.Cond, top, end);
                _e.SetBlock(end);
                break;
            }

            case ForStmt f:
            {
                if (f.Init is not null)
                {
                    EmitStmt(f.Init);
                }

                if (f.Cond is not null && Fold.TryConst(f.Cond, out long known) && known == 0)
                {
                    break;
                }

                Block top = _f.NewBlock("for");
                Block body = _f.NewBlock("forbody");
                Block step = _f.NewBlock("forstep");
                Block end = _f.NewBlock("endfor");

                _e.Jump(top);
                _e.SetBlock(top);
                if (f.Cond is not null && !(Fold.TryConst(f.Cond, out long always) && always != 0))
                {
                    BranchOn(f.Cond, body, end);
                }
                else
                {
                    _e.Jump(body);
                }

                _e.SetBlock(body);
                _loops.Add((end, step, _openHandlers.Count, _openHandlers.Count));
                EmitStmt(f.Body);
                _loops.RemoveAt(_loops.Count - 1);
                if (!_e.Closed)
                {
                    _e.Jump(step);
                }

                _e.SetBlock(step);
                foreach (Expr e2 in f.Step)
                {
                    Eval(e2);
                }
                _e.Jump(top);
                _e.SetBlock(end);
                break;
            }

            case DeconstructStmt taken when _b.Lowered.TryGetValue(taken, out Stmt? apart):
                EmitStmt(apart);
                break;

            case ForeachStmt fe:
                EmitForeach(fe);
                break;

            case SwitchStmt sw:
                EmitSwitch(sw);
                break;

            case ReturnStmt r:
            {
                if (r.Value is not null && _method is not null && _returnValue is not null)
                {
                    VReg v = EvalAs(r.Value, _returnType ?? _method.Returns);
                    _e.CopyTo(_returnValue, new RegOperand(v));
                }
                UnwindToReturn();
                _e.Jump(_returnBlock!);
                break;
            }

            case ThrowStmt th:
                if (th.IsRethrow) Rethrow(Eval(th.Value), th);
                else EmitThrow(th.Value, th);
                break;

            case TryStmt t:
                EmitTry(t);
                break;

            case BreakStmt:
                if (_loops.Count > 0)
                {
                    UnwindTo(_loops[^1].Handlers);
                    _e.Jump(_loops[^1].Break);
                }
                break;

            case ContinueStmt:
                if (_loops.Count > 0)
                {
                    UnwindTo(_loops[^1].Continues);
                    _e.Jump(_loops[^1].Continue);
                }
                break;

            case GotoCaseStmt jump:
                if (_b.GotoCases.TryGetValue(jump, out SwitchCase? destination)
                    && _switchBodies.Count > 0
                    && _switchBodies[^1].Cases.TryGetValue(destination, out Block? target))
                {
                    UnwindTo(_switchBodies[^1].Handlers);
                    _e.Jump(target);
                }
                else
                {
                    Error(jump, "goto case has no bound switch label");
                }
                break;

            default:
                Error(s, $"{s.GetType().Name} is not implemented by the lowering yet");
                break;
        }
    }

    private void EmitLocalDecl(LocalDecl d)
    {
        bool boxed = _b.BoxedLocals.Contains(d);
        Type type = _b.LocalType.TryGetValue(d, out Type? declared)
                  ? declared
                  : d.Init is not null ? _b.TypeOf(d.Init) : Type.I32;

        // A captured local lives in a heap cell the closure shares; the
        // register holds the cell's address, made here at the declaration
        // because that is the first moment the name exists.
        if (boxed && !_cellsMade.Contains(d))
        {
            VReg cell = Allocate(d, Math.Max(_t.WordSize, type.Size));
            _e.CopyTo(LocalReg(d), new RegOperand(cell));
        }

        if (d.Init is not null)
        {
            VReg value = EvalAs(d.Init, type);
            if (boxed)
            {
                _e.Store(new RegOperand(LocalReg(d)), new RegOperand(value), 0, LoadSize(type));
            }
            else if (_addressTakenLocals.Contains(d))
            {
                _e.Store(new SlotOperand(LocalSlot(d, type)), new RegOperand(value), 0, LoadSize(type));
            }
            else
            {
                _e.CopyTo(LocalReg(d), new RegOperand(value));
            }
        }
        else if (IsStructValue(type))
        {
            // A STRUCT LOCAL WITH NO INITIALISER IS A VALUE ALREADY, zero until
            // written, and C# lets it be written a field at a time (`Pair p;
            // p.A = 1;`): the block its fields live in is made here.
            VReg zero = NewStruct(d, type.Symbol!);
            if (boxed)
            {
                _e.Store(new RegOperand(LocalReg(d)), new RegOperand(zero), 0, LoadSize(type));
            }
            else if (_addressTakenLocals.Contains(d))
            {
                _e.Store(new SlotOperand(LocalSlot(d, type)), new RegOperand(zero), 0, LoadSize(type));
            }
            else
            {
                _e.CopyTo(LocalReg(d), new RegOperand(zero));
            }
        }
        else if (!boxed)
        {
            // Make the register exist so later reads have a definition,
            // and give the address-taken kind its slot.
            if (_addressTakenLocals.Contains(d))
            {
                LocalSlot(d, type);
            }
            LocalReg(d);
        }

        foreach (LocalDecl also in d.Also)
        {
            EmitLocalDecl(also);
        }
    }

    /// <summary>
    /// A loop over an array or a string. Anything else was rewritten by the
    /// binder into GetEnumerator and MoveNext, which arrive here as ordinary
    /// statements.
    /// </summary>
    private void EmitForeach(ForeachStmt fe)
    {
        if (_b.Lowered.TryGetValue(fe, out Stmt? rewritten))
        {
            EmitStmt(rewritten);
            return;
        }

        if (!_b.ForeachSlot.TryGetValue(fe, out int elem))
        {
            Error(fe, "foreach did not bind");
            return;
        }

        Type sequenceType = _b.TypeOf(fe.Sequence);
        Type element = sequenceType.Element
                    ?? (sequenceType.Prim == Prim.String ? Type.Char : Type.I32);

        VReg seq = Eval(fe.Sequence);
        VReg index = _f.NewReg(IrType.I32, "i");
        _e.CopyTo(index, new ImmOperand(0, IrType.I32));
        VReg count = sequenceType.IsArray ? _e.Unary(Opcode.ArrayLength, R(seq), IrType.I32) : _e.Load(IrType.I32, seq, _t.ArrayCountOffset);

        Block top = _f.NewBlock("foreach");
        Block body = _f.NewBlock("febody");
        Block step = _f.NewBlock("festep");
        Block end = _f.NewBlock("endforeach");

        _e.Jump(top);
        _e.SetBlock(top);
        _e.Branch(_e.Binary(Opcode.LtS, index, count), body, end);

        _e.SetBlock(body);
        // No bounds check: the loop's own test is the bound.
        int stride = sequenceType.Prim == Prim.String ? 1 : Math.Max(1, element.Size);
        Type stored = sequenceType.Prim == Prim.String ? Type.U8 : element;
        VReg scaled = stride == 1 ? index : _e.Binary(Opcode.Mul, index, stride);
        VReg addr = _e.Binary(Opcode.Add, seq, WordOf(scaled));
        VReg value = LoadPlace(new MemPlace(new RegOperand(addr), _t.ArrayHeaderBytes, stored));
        VReg cursor = SlotReg(elem, IrTypes.Of(element));
        _e.CopyTo(cursor, new RegOperand(value));

        _loops.Add((end, step, _openHandlers.Count, _openHandlers.Count));
        EmitStmt(fe.Body);
        _loops.RemoveAt(_loops.Count - 1);
        if (!_e.Closed)
        {
            _e.Jump(step);
        }

        _e.SetBlock(step);
        _e.CopyTo(index, new RegOperand(_e.Binary(Opcode.Add, index, 1)));
        _e.Jump(top);
        _e.SetBlock(end);
    }

    private void EmitSwitch(SwitchStmt sw)
    {
        Block end = _f.NewBlock("endswitch");
        Block[] bodies = sw.Cases.Select(_ => _f.NewBlock("case")).ToArray();
        Block? defaultBlock = null;
        Dictionary<SwitchCase, Block> caseBlocks = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < sw.Cases.Count; i++)
        {
            caseBlocks[sw.Cases[i]] = bodies[i];
            if (sw.Cases[i].Pattern is null)
            {
                defaultBlock = bodies[i];
            }
        }
        _switchBodies.Add((caseBlocks, _openHandlers.Count));

        // The subject once, into its slot; every label is a condition over a
        // SubjectExpr that reads it.
        Type of = _b.TypeOf(sw.Subject);
        VReg subject = Eval(sw.Subject);
        VReg held = SlotReg(_b.SwitchSubject[sw], IrTypes.Of(of));
        _e.CopyTo(held, new RegOperand(subject));

        Block fallback = defaultBlock ?? end;

        if (TryDenseSwitch(sw, bodies, fallback, out long minimum, out Block[] table))
        {
            VReg key = of.Prim is Prim.I64 or Prim.U64 ? _e.Unary(Opcode.Trunc64, held) : held;
            if (minimum != 0)
            {
                key = _e.Binary(Opcode.Sub, key, minimum);
            }
            _e.Switch(new RegOperand(key), table, fallback);
        }
        else
        {
            for (int i = 0; i < sw.Cases.Count; i++)
            {
                if (sw.Cases[i].Pattern is not Expr label)
                {
                    continue;
                }
                Block next = _f.NewBlock("nextcase");
                BranchOn(label, bodies[i], next);
                _e.SetBlock(next);
            }
            _e.Jump(fallback);
        }

        for (int i = 0; i < sw.Cases.Count; i++)
        {
            _e.SetBlock(bodies[i]);

            // Stacked labels: `case 1: case 3:` is one label with no body
            // that shares the next one's. Falling through to it is what the
            // language means here, and the only fall-through it allows.
            if (sw.Cases[i].Body.Count == 0 && i + 1 < sw.Cases.Count)
            {
                _e.Jump(bodies[i + 1]);
                continue;
            }

            // `continue` IN A SWITCH IS THE LOOP'S, and only `break` is the
            // switch's own. Given the switch's end for both, `default:
            // continue;` fell out of the switch and ran the rest of the loop's
            // body with the last turn's values.
            _loops.Add(_loops.Count > 0
                ? (end, _loops[^1].Continue, _openHandlers.Count, _loops[^1].Continues)
                : (end, end, _openHandlers.Count, _openHandlers.Count));
            foreach (Stmt body in sw.Cases[i].Body)
            {
                EmitStmt(body);
                if (_e.Closed)
                {
                    break;
                }
            }
            _loops.RemoveAt(_loops.Count - 1);
            if (!_e.Closed)
            {
                _e.Jump(end);
            }
        }

        _e.SetBlock(end);
        _switchBodies.RemoveAt(_switchBodies.Count - 1);
    }

    /// <summary>
    /// Whether every label is an integer constant equality on the subject and
    /// the range is dense enough for a jump table: at most twice as many
    /// entries as cases, and at least four cases.
    /// </summary>
    private bool TryDenseSwitch(SwitchStmt sw, Block[] bodies, Block fallback,
                                out long minimum, out Block[] table)
    {
        minimum = 0;
        table = Array.Empty<Block>();
        List<(long Value, Block Target)> entries = new();

        for (int i = 0; i < sw.Cases.Count; i++)
        {
            if (sw.Cases[i].Pattern is null)
            {
                continue;
            }
            if (!TryCaseConstant(sw.Cases[i].Pattern!, out long value))
            {
                return false;
            }
            entries.Add((value, bodies[i]));
        }

        if (entries.Count < 4)
        {
            return false;
        }

        long min = entries.Min(e => e.Value);
        long max = entries.Max(e => e.Value);
        long span = max - min + 1;
        if (span <= 0 || span > 2L * entries.Count + 8 || span > 4096)
        {
            return false;
        }

        minimum = min;
        table = new Block[span];
        Array.Fill(table, fallback);
        foreach ((long v, Block t) in entries)
        {
            if (table[v - min] != fallback)
            {
                return false;       // a duplicate label; let the branches sort it out
            }
            table[v - min] = t;
        }
        return true;
    }

    /// <summary>A case label of the form `subject == constant`, or `constant` on its own.</summary>
    private bool TryCaseConstant(Expr pattern, out long value)
    {
        if (pattern is BinaryExpr { Op: BinOp.Eq } eq)
        {
            if (eq.Left is SubjectExpr && Fold.TryConst(eq.Right, out value)) return true;
            if (eq.Right is SubjectExpr && Fold.TryConst(eq.Left, out value)) return true;
        }
        return Fold.TryConst(pattern, out value);
    }

    // ---- exceptions ----------------------------------------------------------------

    private const int HandlerBytes = 16;
    private const int HandlerPrev = 0, HandlerAddr = 4, HandlerSp = 8, HandlerFp = 12;

    /// <summary>
    /// Opens a handler: a record on this frame, linked at the head of the
    /// chain, remembering where to land and what the stack and frame pointers
    /// were. The offsets are the word size times the field index, so the
    /// record is the same shape on a 64-bit target.
    /// </summary>
    private FrameSlot PushHandler(Block landing)
    {
        int w = _t.WordSize;
        FrameSlot rec = _f.NewSlot(4 * w, w, "handler");
        VReg recAddr = _e.SlotAddress(rec);
        VReg head = _e.Load(IrTypes.Word, ThreadBlockNow(), TlsHandler / 4 * w);
        _e.Store(recAddr, head, HandlerPrev / 4 * w);
        _e.Store(recAddr, _e.LabelAddress(landing), HandlerAddr / 4 * w);
        VReg sp = _e.Reg(IrTypes.Word, "sp");
        _e.Emit(Opcode.StackPointer, sp);
        _e.Store(recAddr, sp, HandlerSp / 4 * w);
        VReg fp = _e.Reg(IrTypes.Word, "fp");
        _e.Emit(Opcode.FramePointer, fp);
        _e.Store(recAddr, fp, HandlerFp / 4 * w);
        _e.Store(ThreadBlockNow(), recAddr, TlsHandler / 4 * w);
        return rec;
    }

    /// <summary>Closes the innermost handler: the chain's head goes back to what it was.</summary>
    private void PopHandler(FrameSlot rec)
    {
        int w = _t.WordSize;
        VReg recAddr = _e.SlotAddress(rec);
        VReg prev = _e.Load(IrTypes.Word, recAddr, HandlerPrev / 4 * w);
        _e.Store(ThreadBlockNow(), prev, TlsHandler / 4 * w);
    }

    /// <summary>
    /// Throws: unlinks the head record and unwinds to it with the object. No
    /// handler at all is the program's fault, and the runtime says so.
    /// </summary>
    private void EmitThrow(Expr value, Node at)
    {
        VReg obj = Eval(value);

        // WHERE THIS THROW IS, recorded into the exception before anything
        // unwinds, because afterwards the stack no longer says. The frame
        // pointer is read HERE and handed over, so the answer does not depend
        // on whether the runtime's own helpers were inlined into each other.
        //
        // Only a `throw expr`, never a bare `throw;`: a rethrow keeps the
        // trace the first throw recorded, which is what C# promises and the
        // whole reason the two are spelled differently.
        if (RuntimeMethod("Capture", 3) is MethodSymbol capture)
        {
            Require(capture);
            VReg frame = _e.Reg(IrTypes.Word, "fp");
            _e.Emit(Opcode.FramePointer, frame);
            // A frame chain names callers, not the current instruction. Keep
            // the throwing source location explicitly, even if Capture or its
            // callers are inlined; no extra machine frame is required.
            string site = "   at " + (_f.Display ?? _f.Name);
            string file = _f.SourceFile ?? at.File;
            if (!string.IsNullOrEmpty(file)) site += " in " + file + ":line " + at.Line;
            _e.Call(CallLabel(capture), IrType.Void, R(obj), R(Widen(frame)), new SymOperand(InternString(site + "\n")));
        }

        Rethrow(obj, at);
    }

    private void Rethrow(VReg obj, Node at)
    {
        int w = _t.WordSize;
        VReg head = _e.Load(IrTypes.Word, ThreadBlockNow(), TlsHandler / 4 * w);
        Block none = _f.NewBlock("nohandler");
        Block some = _f.NewBlock("unwind");
        _e.Branch(head, some, none);

        _e.SetBlock(some);
        VReg prev = _e.Load(IrTypes.Word, head, HandlerPrev / 4 * w);
        _e.Store(ThreadBlockNow(), prev, TlsHandler / 4 * w);
        _e.Emit(Opcode.Unwind, null, new RegOperand(head), new RegOperand(obj));

        _e.SetBlock(none);
        MethodSymbol? unhandled = RuntimeMethod("Unhandled", 1);
        if (unhandled is not null)
        {
            Require(unhandled);
            _e.Call(CallLabel(unhandled), IrType.Void, new RegOperand(obj));
        }
        _e.Emit(Opcode.Trap, null);
        _e.Unreachable();

        // Whatever follows is unreachable; give it a block so emission can continue.
        _e.SetBlock(_f.NewBlock("afterthrow"));
    }

    /// <summary>The exception object on entry to a landing pad.</summary>
    private VReg LandingValue(Block pad)
    {
        pad.IsLandingPad = true;
        _e.SetBlock(pad);
        return _e.Call("__exception", IrTypes.Word)!;
    }

    private void EmitTry(TryStmt t)
    {
        if (t.Finally is not null && t.Catches.Count > 0)
        {
            EmitProtected(t.Finally, () =>
            {
                TryStmt inner = new() { Body = t.Body, Line = t.Line, Col = t.Col };
                inner.Catches.AddRange(t.Catches);
                EmitTry(inner);
            });
            return;
        }

        if (t.Finally is not null)
        {
            EmitProtected(t.Finally, () => EmitStmt(t.Body));
            return;
        }

        EmitCatching(t);
    }

    /// <summary>
    /// A body under a finally. The block is emitted twice -- once on the
    /// normal path, once on the unwinding path -- because sharing one copy
    /// needs a return address for it, and that is a second thing to get
    /// right on the path where something has already gone wrong.
    /// </summary>
    private void EmitProtected(AstBlock fin, Action body)
    {
        Block landing = _f.NewBlock("finally");
        Block end = _f.NewBlock("endfinally");

        FrameSlot rec = PushHandler(landing);
        _openHandlers.Add((rec, fin));
        body();
        _openHandlers.RemoveAt(_openHandlers.Count - 1);

        if (!_e.Closed)
        {
            PopHandler(rec);
            EmitStmt(fin);
            if (!_e.Closed)
            {
                _e.Jump(end);
            }
        }

        // Unwinding: the record is already unlinked. Run the block and carry
        // on outward with the same object.
        VReg exc = LandingValue(landing);
        FrameSlot keep = _f.NewSlot(_t.WordSize, _t.WordSize, "exc");
        _e.Store(new SlotOperand(keep), new RegOperand(exc));
        EmitStmt(fin);
        if (!_e.Closed)
        {
            VReg again = _e.Load(IrTypes.Word, new SlotOperand(keep));
            Rethrow(again, fin);
        }

        _e.SetBlock(end);
    }

    /// <summary>
    /// A try with catches: each clause is a type test on the object the
    /// unwinder handed over, then an optional filter. Nothing matching means
    /// it was never this method's to deal with.
    /// </summary>
    private void EmitCatching(TryStmt t)
    {
        Block landing = _f.NewBlock("catch");
        Block end = _f.NewBlock("endtry");

        FrameSlot rec = PushHandler(landing);
        _openHandlers.Add((rec, null));
        EmitStmt(t.Body);
        _openHandlers.RemoveAt(_openHandlers.Count - 1);

        if (!_e.Closed)
        {
            PopHandler(rec);
            _e.Jump(end);
        }

        VReg exc = LandingValue(landing);
        FrameSlot keep = _f.NewSlot(_t.WordSize, _t.WordSize, "exc");
        _e.Store(new SlotOperand(keep), new RegOperand(exc));

        foreach (CatchClause c in t.Catches)
        {
            Block next = _f.NewBlock("nocatch");
            Block body = _f.NewBlock("catchbody");
            VReg obj = _e.Load(IrTypes.Word, new SlotOperand(keep));

            if (_b.CatchType.TryGetValue(c, out TypeSymbol? want))
            {
                VReg matched = TypeTest(obj, want);
                _e.Branch(matched, body, next);
            }
            else
            {
                _e.Jump(body);
            }

            _e.SetBlock(body);
            if (_b.CatchSlot.TryGetValue(c, out int slot))
            {
                _e.CopyTo(SlotReg(slot, IrTypes.Word), new RegOperand(obj));
            }

            if (c.When is Expr filter)
            {
                Block accepted = _f.NewBlock("filtered");
                BranchOn(filter, accepted, next);
                _e.SetBlock(accepted);
            }

            EmitStmt(c.Body);
            if (!_e.Closed)
            {
                _e.Jump(end);
            }
            _e.SetBlock(next);
        }

        // Nothing here wanted it.
        VReg leftover = _e.Load(IrTypes.Word, new SlotOperand(keep));
        Rethrow(leftover, t);
        _e.SetBlock(end);
    }

    /// <summary>
    /// Leaves every open try on the way to a return: each record unlinked and
    /// each finally run, innermost first. The return value is already in its
    /// register, which the finally blocks do not touch.
    /// </summary>
    private void UnwindToReturn() => UnwindTo(0);

    /// <summary>
    /// Leaves every try opened since <paramref name="depth"/> handlers were
    /// open: a break, continue or goto case out of a try, or a return out of
    /// all of them.
    /// </summary>
    private void UnwindTo(int depth)
    {
        List<(FrameSlot Record, AstBlock? Finally)> saved = _openHandlers.ToList();

        for (int i = saved.Count - 1; i >= depth; i--)
        {
            PopHandler(saved[i].Record);

            if (saved[i].Finally is AstBlock fin)
            {
                // The finally runs with only the handlers OUTSIDE it open, so
                // a return inside it unwinds those and not itself again.
                _openHandlers.Clear();
                _openHandlers.AddRange(saved.Take(i));
                EmitStmt(fin);
                if (_e.Closed)
                {
                    break;
                }
            }
        }

        _openHandlers.Clear();
        _openHandlers.AddRange(saved);
    }
}
