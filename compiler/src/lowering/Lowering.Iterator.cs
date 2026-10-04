#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

/// <summary>
/// Iterators, as C# means them: run a step at a time, nothing before the
/// first MoveNext.
///
/// A method, local function or lambda whose body yields (AstBlock.Iterator)
/// becomes two things. The KICKOFF has the method's own name and signature:
/// it allocates the iterator object, stores the receiver and the arguments
/// in it and hands it back, running none of the body. The object is a class
/// implementing IEnumerable&lt;T&gt; and IEnumerable when the method returns one
/// of those, and IEnumerator&lt;T&gt;, IEnumerator and IDisposable always; its
/// MOVENEXT is the body, written as an async body is (Lowering.Async) with a
/// suspension at each `yield return`, which AsyncTransform makes resumable
/// and which returns true there (AsyncFrame.SuspendResult).
///
/// The states are C#'s: -2 an enumerable not yet enumerated, 0 not started,
/// -1 running or finished, k &gt; 0 suspended at the k-th yield. MoveNext sets
/// -1 while it runs, so an exception leaves the iterator finished, as C#'s
/// does. GetEnumerator hands the object itself out the first time and a
/// fresh copy with the original arguments after that. Dispose of one that is
/// suspended resumes it with the stop signal (Runtime.IteratorStop), which
/// unwinds through the finally blocks around the yield -- C# allows a yield
/// inside a try only with no catch, so only finally blocks see it -- and
/// MoveNext's own handler catches it at the top.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>One iterator's pieces.</summary>
    private sealed record IteratorMethod(
        MethodSymbol Method, MethodDecl Decl, TypeSymbol Machine, MethodSymbol MoveNext,
        string SizeSymbol, int[] ParamOffsets, Type Element, bool Enumerable);

    private readonly Dictionary<MethodSymbol, IteratorMethod> _iterators = new();

    /// <summary>The iterator whose MoveNext is being lowered; null elsewhere.</summary>
    private IteratorMethod? _iterating;

    /// <summary>
    /// Where an iterator object holds its Current in line, by the object's
    /// type: a struct element held in line has an area of its own at the
    /// object's end, and a yield copies its bytes there -- a word pointing at
    /// a block made for every element kept each of them for the collector.
    /// </summary>
    private readonly Dictionary<TypeSymbol, int> _inlineCurrent = new(ReferenceEqualityComparer.Instance);
    private int _yieldPoints;

    // The fixed part of every iterator object, after the object header.
    private int IterStateField => _t.ObjectHeaderBytes;
    private int IterCurrentField => _t.ObjectHeaderBytes + 8;
    private int IterReceiverField => _t.ObjectHeaderBytes + 16;
    private int IterDisposingField => _t.ObjectHeaderBytes + 16 + _t.WordSize;
    private int IterFirstParamField => (_t.ObjectHeaderBytes + 16 + _t.WordSize + 4 + 7) / 8 * 8;

    private IteratorMethod? IteratorPieces(MethodSymbol m, MethodDecl decl)
    {
        foreach (IteratorMethod existing in _iterators.Values)
        {
            if (ReferenceEquals(existing.Method, m)) return existing;
        }

        Type? element = Binder.IteratorElement(m.Returns);
        if (element is null || m.Returns.Symbol is not TypeSymbol returned)
        {
            Error(decl, $"'{m.Name}' yields, so it has to return IEnumerable<T>, IEnumerator<T>, IEnumerable or IEnumerator");
            return null;
        }
        bool enumerable = PlainTypeName(returned) == "IEnumerable";
        TypeSymbol? enumerator = enumerable
            ? returned.Methods.FirstOrDefault(x => x.Name == "GetEnumerator" && x.Params.Count == 0)?.Returns.Symbol
            : returned;
        if (enumerator is null)
        {
            Error(decl, $"'{m.Returns}' has no enumerator for an iterator to be");
            return null;
        }

        string identity = ClosureIdentity.Of(m);
        string name = "Iter$" + identity;
        TypeSymbol machine = new()
        {
            Name = name, Key = name, Kind = TypeKind.Class,
            Decl = new TypeDecl { Name = name, Kind = TypeKind.Class, LocalOnly = true },
        };
        List<TypeSymbol> faces = enumerable ? new() { returned, enumerator } : new() { enumerator };
        foreach (TypeSymbol face in faces) machine.Interfaces.Add(face);
        Binder.ImplementSlots(machine, faces);

        MethodSymbol? moveNext = machine.Methods.FirstOrDefault(x => x.Name == "MoveNext" && x.Params.Count == 0);
        if (moveNext is null)
        {
            Error(decl, "an iterator needs IEnumerator.MoveNext, which no compiled source provides");
            return null;
        }
        // The body is MoveNext's: EmitMethod finds it by this symbol.
        MethodSymbol body = new()
        {
            Name = moveNext.Name, Returns = moveNext.Returns, Owner = machine,
            Decl = decl, VtableSlot = moveNext.VtableSlot,
        };
        machine.Methods[machine.Methods.IndexOf(moveNext)] = body;

        int[] offsets = new int[m.Params.Count];
        int at = IterFirstParamField;
        for (int i = 0; i < m.Params.Count; i++)
        {
            // A captured variable's cell (ParamSymbol.Cell) is an object the
            // machine may hold, and every enumerator it makes shares.
            if (m.Params[i].ByRef && !m.Params[i].Cell)
            {
                Error(decl, $"'{m.Name}': an iterator cannot take a ref, out or in parameter");
            }
            offsets[i] = at;
            at += 8;
        }
        if (IsStructValue(element) && StructOf(element).HeldInline)
        {
            at = (at + 7) / 8 * 8;
            _inlineCurrent[machine] = at;
            at += (Math.Max(1, StructOf(element).InstanceSize) + 7) / 8 * 8;
        }
        machine.InstanceSize = at;
        machine.InlineDecided = true;                  // laid out here, nothing in line

        string size = "itsize_" + identity;
        byte[] initial = new byte[_t.WordSize];
        WriteWord(initial, 0, at);
        _m.Data.Add(new DataItem(size, initial) { Align = _t.WordSize, Exported = false });

        IteratorMethod made = new(m, decl, machine, body, size, offsets, element, enumerable);
        _iterators[body] = made;
        return made;
    }

    private static string PlainTypeName(TypeSymbol t)
    {
        string name = t.Decl?.Template ?? t.Name;
        int cut = name.IndexOfAny(new[] { '`', '$', '<' });
        string bare = cut < 0 ? name : name[..cut];
        int dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare[(dot + 1)..];
    }

    /// <summary>
    /// The method as callers see it: the iterator object, with the receiver
    /// and the arguments in it, and nothing of the body run.
    /// </summary>
    private void EmitIteratorKickoff(MethodSymbol m, MethodDecl decl)
    {
        if (IteratorPieces(m, decl) is not IteratorMethod it)
        {
            return;
        }

        _f = new Function(Label(m), IrTypes.Of(m.Returns)) { SourceFile = _in, Line = decl.Line, Display = Display(m), FromLibrary = IsLibrary(m.Owner),
            Coalescible = decl.LocalCopy || m.Owner.Decl?.Specialised == true, Exported = m.Owner.Decl?.LocalOnly != true };
        _e = new Builder(_f, _f.NewBlock("entry"));

        if (m.Static && !m.IsCtor)
        {
            Entries[$"{m.Owner.Name}.{m.Name}"] = _f.Name;
        }

        VReg? self = null;
        if (!m.Static)
        {
            self = _f.NewReg(IrTypes.Word, "this");
            _f.Params.Add(self);
        }
        List<VReg> args = new();
        foreach (ParamSymbol p in m.Params)
        {
            VReg r = _f.NewReg(p.ByRef ? IrTypes.Word : IrTypes.Of(p.Type), p.Name);
            _f.Params.Add(r);
            args.Add(r);
        }

        VReg machine = AllocateDynamic(decl, _e.Load(IrTypes.Word, new SymOperand(it.SizeSymbol)));
        _e.Store(R(machine), VtableOf(it.Machine), 0, _t.WordSize);
        _e.Store(R(machine), Imm(it.Enumerable ? -2 : 0, IrType.I32), IterStateField, 4);
        if (self is not null)
        {
            StoreNewReference(machine, self, IterReceiverField);
        }
        for (int i = 0; i < args.Count; i++)
        {
            _e.Store(R(machine), R(args[i]), it.ParamOffsets[i], args[i].Type.Bytes());
            if (args[i].Type == IrTypes.Word)
            {
                CardMarkAt(R(machine), it.ParamOffsets[i]);
            }
        }
        _e.Ret(R(machine));
        _m.Functions.Add(_f);

        Require(it.MoveNext);
        EmitIteratorMembers(it);
    }

    /// <summary>
    /// Everything but MoveNext: both GetEnumerators, both Currents, Reset and
    /// Dispose. Written straight into IR, as an array view's members are.
    /// </summary>
    private void EmitIteratorMembers(IteratorMethod it)
    {
        Function savedFn = _f; Builder savedB = _e;
        foreach (MethodSymbol member in it.Machine.Methods)
        {
            if (ReferenceEquals(member, it.MoveNext)) continue;
            string what = member.ExplicitMember ?? member.Name;
            Function f = new(Label(member), IrTypes.Of(member.Returns))
            {
                SourceFile = _in, Line = it.Decl.Line, Display = Display(member), FromLibrary = IsLibrary(it.Method.Owner),
                Coalescible = true, Exported = false,
            };
            VReg self = f.NewReg(IrTypes.Word, "this");
            f.Params.Add(self);
            // A struct Current is written to its caller's buffer (Buffered).
            VReg? buffer = null;
            if (Buffered(member))
            {
                buffer = f.NewReg(IrTypes.Word, "retbuf");
                f.Params.Add(buffer);
            }
            Builder e = new(f, f.NewBlock("entry"));
            _f = f; _e = e;

            switch (what)
            {
                case "GetEnumerator":
                {
                    // The object itself, the first time; a copy with the
                    // original arguments whenever it is asked again.
                    Block fresh = f.NewBlock("fresh");
                    Block itself = f.NewBlock("itself");
                    VReg state = e.Load(IrType.I32, self, IterStateField);
                    e.Branch(e.Binary(Opcode.Eq, state, -2), itself, fresh);
                    e.SetBlock(itself);
                    e.Store(R(self), Imm(0, IrType.I32), IterStateField, 4);
                    e.Ret(R(self));
                    e.SetBlock(fresh);
                    VReg copy = AllocateDynamic(it.Decl, e.Load(IrTypes.Word, new SymOperand(it.SizeSymbol)));
                    e.Store(R(copy), VtableOf(it.Machine), 0, _t.WordSize);
                    e.Store(R(copy), R(e.Load(IrTypes.Word, self, IterReceiverField)), IterReceiverField, _t.WordSize);
                    foreach (int offset in it.ParamOffsets)
                    {
                        e.Store(R(copy), R(e.Load(IrType.I64, self, offset, 8)), offset, 8);
                    }
                    e.Ret(R(copy));
                    break;
                }

                case "get_Current":
                {
                    VReg current = _inlineCurrent.TryGetValue(it.Machine, out int inlineAt)
                        ? e.Binary(Opcode.Add, self, inlineAt)
                        : e.Load(IrTypes.Of(it.Element), self, IterCurrentField, LoadSize(it.Element),
                                 !it.Element.IsUnsigned && it.Element.Prim != Prim.Bool);
                    // IEnumerator's Current is an object: the element boxed.
                    if (member.ExplicitMember is not null && Boxable(it.Element))
                    {
                        current = BoxValue(it.Decl, current, it.Element);
                    }
                    // A struct element goes into the caller's buffer.
                    else if (buffer is not null)
                    {
                        e.Emit(Opcode.MemCopy, null, R(buffer), R(current), Imm(Math.Max(1, StructOf(it.Element).InstanceSize), IrTypes.Word));
                        current = buffer;
                    }
                    else if (IsStructValue(it.Element))
                    {
                        current = CopyStruct(it.Decl, current, StructOf(it.Element));
                    }
                    e.Ret(R(current));
                    break;
                }

                case "Reset":
                    if (RequireRuntime(it.Decl, "IteratorReset", 0, "an iterator's Reset") is MethodSymbol reset)
                    {
                        e.Call(CallLabel(reset), IrType.Void);
                    }
                    e.Ret();
                    break;

                case "Dispose":
                {
                    // Suspended inside the body: resume it to stop, so the
                    // finally blocks around the yield run. Otherwise it is
                    // simply finished.
                    Block resume = f.NewBlock("stopbody");
                    Block done = f.NewBlock("finished");
                    VReg state = e.Load(IrType.I32, self, IterStateField);
                    e.Branch(e.Binary(Opcode.GtS, state, 0), resume, done);
                    e.SetBlock(resume);
                    e.Store(R(self), Imm(1, IrType.I32), IterDisposingField, 4);
                    e.Call(Label(it.MoveNext), IrType.I32, R(self));
                    e.Jump(done);
                    e.SetBlock(done);
                    e.Store(R(self), Imm(-1, IrType.I32), IterStateField, 4);
                    e.Ret();
                    break;
                }

                default:
                    Error(it.Decl, $"an iterator has no '{member.Name}' to write");
                    e.Ret(IrTypes.Of(member.Returns) == IrType.Void ? null : Imm(0, IrTypes.Of(member.Returns)));
                    break;
            }
            _m.Functions.Add(f);
        }
        _f = savedFn; _e = savedB;
    }

    /// <summary>
    /// The body, as the iterator's MoveNext: the receiver and the arguments
    /// read out of the object on entry, a suspension at each `yield return`,
    /// and false once it has run to the end.
    /// </summary>
    private void EmitIteratorMoveNext(IteratorMethod it)
    {
        MethodSymbol m = it.Method;
        MethodDecl decl = it.Decl;
        _method = m;
        _decl = decl;
        _in = m.Owner.Decl?.File ?? decl.File ?? "";

        // Yield points numbered from zero in each body, for the reason the
        // await points are (EmitMoveNext).
        int outsideYields = _yieldPoints;
        _yieldPoints = 0;
        _f = new Function(Label(it.MoveNext), IrType.I32) { SourceFile = _in, Line = decl.Line, Display = Display(it.MoveNext),
            FromLibrary = IsLibrary(m.Owner), Coalescible = true, Exported = false };
        Block entry = _f.NewBlock("entry");
        _e = new Builder(_f, entry);

        VReg machine = _f.NewReg(IrTypes.Word, "machine");
        _f.Params.Add(machine);
        _stateMachine = machine;
        _iterating = it;
        RequireCardMarkObject();
        _f.Async = new AsyncFrame
        {
            StateMachine = machine,
            StateOffset = IterStateField,
            FieldsStart = it.Machine.InstanceSize,
            SizeSymbol = it.SizeSymbol,
            SuspendResult = Imm(1, IrType.I32),
        };

        // NOT STARTED IS THE ONLY STATE THAT RUNS FROM HERE: a resumption is
        // dispatched past this entry, and a finished one -- or an enumerable
        // not yet enumerated -- answers false.
        Block start = _f.NewBlock("start");
        Block nothing = _f.NewBlock("nothing");
        _e.Branch(_e.Binary(Opcode.Eq, _e.Load(IrType.I32, machine, IterStateField), 0), start, nothing);
        _e.SetBlock(nothing);
        _e.Ret(Imm(0, IrType.I32));
        _e.SetBlock(start);
        _e.Store(R(machine), Imm(-1, IrType.I32), IterStateField, 4);

        if (!m.Static)
        {
            _this = _e.Load(IrTypes.Word, machine, IterReceiverField);
        }

        _params = new VReg[m.Params.Count];
        _paramSlots = new FrameSlot?[m.Params.Count];
        for (int i = 0; i < m.Params.Count; i++)
        {
            ParamSymbol p = m.Params[i];
            IrType type = p.ByRef ? IrTypes.Word : IrTypes.Of(p.Type);
            _params[i] = _e.Load(type, machine, it.ParamOffsets[i], type.Bytes());
        }

        ScanAddressTaken(decl.Body!);
        MakeParamCells(decl.Body!);
        for (int i = 0; i < m.Params.Count; i++)
        {
            if (_addressTakenParams.Contains(i))
            {
                Type pt = m.Params[i].Type;
                FrameSlot slot = _f.NewSlot(Math.Max(4, pt.Size), Math.Min(Math.Max(4, pt.Size), _t.Align64), m.Params[i].Name);
                _paramSlots[i] = slot;
                _e.Store(new SlotOperand(slot), new RegOperand(_params[i]), 0, pt.Size);
            }
        }

        _returnType = Type.Void;
        _returnBlock = _f.NewBlock("end");
        _returnValue = null;

        // AN EXCEPTION LEAVES IT FINISHED and goes on to the caller, as C#'s
        // does -- the state is already -1 while the body runs. The stop signal
        // Dispose resumes it with is caught here and is the end.
        Block failed = _f.NewBlock("failed");
        FrameSlot catchAll = PushHandler(failed);
        _openHandlers.Add((catchAll, null));

        EmitStmt(decl.Body!);

        if (!_e.Closed)
        {
            UnwindToReturn();
            _e.Jump(_returnBlock);
        }
        _openHandlers.Clear();
        _openCatches.Clear();

        _e.SetBlock(_returnBlock);
        _e.Store(R(machine), Imm(-1, IrType.I32), IterStateField, 4);
        _e.Ret(Imm(0, IrType.I32));

        VReg exception = LandingValue(failed);
        _e.Store(R(machine), Imm(-1, IrType.I32), IterStateField, 4);
        if (RequireRuntime(decl, "IteratorStopping", 1, "an iterator's Dispose") is MethodSymbol stopping)
        {
            Block stopped = _f.NewBlock("stopped");
            Block onward = _f.NewBlock("onward");
            VReg isStop = _e.Call(CallLabel(stopping), IrType.I32, R(exception))!;
            _e.Branch(isStop, stopped, onward);
            _e.SetBlock(stopped);
            _e.Ret(Imm(0, IrType.I32));
            _e.SetBlock(onward);
        }
        Rethrow(exception, decl);

        _m.Functions.Add(_f);
        _stateMachine = null;
        _iterating = null;
        _method = null;
        _decl = null;
        _yieldPoints = outsideYields;
    }

    /// <summary>
    /// `yield return value`: the value into Current, and a suspension that
    /// answers true. Resumed, the handlers go back on the chain, a Dispose
    /// that resumed it stops it, and it is running again.
    /// `yield break`: as `return` is anywhere else, through the finally blocks
    /// to the end.
    /// </summary>
    private void EmitYield(YieldStmt y)
    {
        if (_iterating is not IteratorMethod it || _stateMachine is not VReg machine)
        {
            Error(y, "'yield' outside an iterator body");
            return;
        }
        if (y.Value is null)
        {
            UnwindToReturn();
            _e.Jump(_returnBlock!);
            return;
        }

        if (_inlineCurrent.TryGetValue(it.Machine, out int inlineAt))
        {
            // Its bytes into the object's own area: nothing kept by pointer.
            MemPlace place = new(R(machine), inlineAt, it.Element, Inline: true);
            StorePlace(place, InlineValue(place, y.Value, it.Element));
        }
        else
        {
            VReg value = EvalAs(y.Value, it.Element);
            _e.Store(R(machine), R(value), IterCurrentField, LoadSize(it.Element));
        }

        int w = _t.WordSize;
        if (_openHandlers.Count > 0)
        {
            VReg outermost = _e.SlotAddress(_openHandlers[0].Record);
            VReg before = _e.Load(IrTypes.Word, outermost, HandlerPrev / 4 * w);
            _e.Store(ThreadBlockNow(), before, TlsHandler / 4 * w);
        }

        int point = _yieldPoints++;
        _e.Call(AsyncFrame.Suspend, IrType.Void, Imm(point, IrType.I32));
        _e.Call(AsyncFrame.Resume, IrType.Void, Imm(point, IrType.I32));

        foreach ((FrameSlot record, AstBlock? _) in _openHandlers)
        {
            VReg addr = _e.SlotAddress(record);
            VReg head = _e.Load(IrTypes.Word, ThreadBlockNow(), TlsHandler / 4 * w);
            _e.Store(addr, head, HandlerPrev / 4 * w);
            VReg sp = _e.Reg(IrTypes.Word, "sp");
            _e.Emit(Opcode.StackPointer, sp);
            _e.Store(addr, sp, HandlerSp / 4 * w);
            VReg fp = _e.Reg(IrTypes.Word, "fp");
            _e.Emit(Opcode.FramePointer, fp);
            _e.Store(addr, fp, HandlerFp / 4 * w);
            _e.Store(ThreadBlockNow(), addr, TlsHandler / 4 * w);
        }

        _e.Store(R(machine), Imm(-1, IrType.I32), IterStateField, 4);
        if (RequireRuntime(y, "IteratorStop", 0, "an iterator's Dispose") is MethodSymbol stop)
        {
            Block stopping = _f.NewBlock("stopping");
            Block running = _f.NewBlock("running");
            _e.Branch(_e.Load(IrType.I32, machine, IterDisposingField), stopping, running);
            _e.SetBlock(stopping);
            _e.Call(CallLabel(stop), IrType.Void);
            _e.Jump(running);
            _e.SetBlock(running);
        }
    }
}
