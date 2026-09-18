#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

/// <summary>
/// Async methods, as C# means them.
///
/// Each async method becomes two functions. The KICKOFF has the method's own
/// name and signature: it allocates a state machine object, stores the
/// receiver and the arguments in it, creates the task, runs the body once,
/// and returns the task. The body -- MOVENEXT -- is the Invoke of the state
/// machine, a class implementing Action, so the state machine itself is the
/// continuation an awaiter is handed.
///
/// Lowering writes MoveNext as an ordinary function with a marked place at
/// each await where it may suspend; the async transform, which runs after
/// the optimiser, turns those places into saves, returns, a dispatch on
/// entry and reloads. See docs/X86-BACKEND.md, "Async".
/// </summary>
public sealed partial class Lowering
{
    /// <summary>One async method's pieces.</summary>
    private sealed record AsyncMethod(
        MethodSymbol Method, MethodDecl Decl, TypeSymbol StateMachine,
        MethodSymbol MoveNext, string SizeSymbol, int[] ParamOffsets, Type Result);

    private readonly Dictionary<MethodSymbol, AsyncMethod> _moveNext = new();
    private int _awaitPoints;

    /// <summary>The state machine register while a MoveNext is being lowered; null elsewhere.</summary>
    private VReg? _stateMachine;

    // The fixed part of every state machine, after the object header.
    private int TaskField => _t.ObjectHeaderBytes;
    private int StateField => _t.ObjectHeaderBytes + _t.WordSize;
    private int ReceiverField => _t.ObjectHeaderBytes + 2 * _t.WordSize;
    private int FirstParamField => _t.ObjectHeaderBytes + 3 * _t.WordSize + (_t.WordSize == 4 ? 4 : 0);

    /// <summary>
    /// The type a task's awaiter hands back: T for Task&lt;T&gt;, nothing for
    /// Task and for `async void`.
    /// </summary>
    private static Type AsyncResultType(Type declared)
    {
        if (declared.IsVoid)
        {
            return Type.Void;
        }
        MethodSymbol? getAwaiter = declared.Symbol?.FindMethods("GetAwaiter").FirstOrDefault(m => m.Params.Count == 0);
        MethodSymbol? getResult = getAwaiter?.Returns.Symbol?.FindMethods("GetResult").FirstOrDefault(m => m.Params.Count == 0);
        return getResult?.Returns ?? Type.Void;
    }

    private AsyncMethod? AsyncPieces(MethodSymbol m, MethodDecl decl)
    {
        foreach (AsyncMethod existing in _moveNext.Values)
        {
            if (ReferenceEquals(existing.Method, m))
            {
                return existing;
            }
        }

        if (!_b.Types.TryGetValue("Action", out TypeSymbol? action)
            || action.FindMethods("Invoke").FirstOrDefault(i => i.Params.Count == 0) is not MethodSymbol invoke)
        {
            Error(decl, "an async method needs the 'Action' interface, which no compiled source provides");
            return null;
        }

        string identity = ClosureIdentity.Of(m);
        string name = "Async$" + identity;
        TypeSymbol machine = new()
        {
            Name = name,
            Key = name,
            Kind = TypeKind.Class,
            Decl = new TypeDecl { Name = name, Kind = TypeKind.Class, LocalOnly = true },
            Depth = 0,
        };
        machine.Interfaces.Add(action);

        MethodSymbol moveNext = new()
        {
            Name = "Invoke", Returns = Type.Void, Owner = machine,
            Decl = decl, VtableSlot = invoke.VtableSlot,
        };
        machine.Methods.Add(moveNext);

        int[] offsets = new int[m.Params.Count];
        int at = FirstParamField;
        for (int i = 0; i < m.Params.Count; i++)
        {
            if (m.Params[i].ByRef)
            {
                Error(decl, $"'{m.Name}': an async method cannot take a ref or out parameter");
            }
            offsets[i] = at;
            at += 8;
        }
        machine.InstanceSize = at;

        string size = "smsize_" + identity;
        byte[] initial = new byte[_t.WordSize];
        WriteWord(initial, 0, at);
        _m.Data.Add(new DataItem(size, initial) { Align = _t.WordSize, Exported = false });

        AsyncMethod made = new(m, decl, machine, moveNext, size, offsets, AsyncResultType(m.Returns));
        _moveNext[moveNext] = made;
        return made;
    }

    /// <summary>
    /// The method as callers see it: build the state machine and the task,
    /// run the body until it first suspends, hand back the task.
    /// </summary>
    private void EmitKickoff(MethodSymbol m, MethodDecl decl)
    {
        if (AsyncPieces(m, decl) is not AsyncMethod am)
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
            VReg r = _f.NewReg(IrTypes.Of(p.Type), p.Name);
            _f.Params.Add(r);
            args.Add(r);
        }

        // The machine's size is decided after optimisation, when the
        // transform knows what has to survive a suspension, so it is read
        // from data the transform fills in rather than written here.
        VReg size = _e.Load(IrTypes.Word, new SymOperand(am.SizeSymbol));
        VReg machine = AllocateDynamic(decl, size);
        _e.Store(R(machine), VtableOf(am.StateMachine), 0, _t.WordSize);

        VReg? task = null;
        if (!m.Returns.IsVoid)
        {
            task = NewObject(decl, m.Returns);
            if (task is not null)
            {
                _e.Store(R(machine), R(task), TaskField, _t.WordSize);
            }
        }

        if (self is not null)
        {
            _e.Store(R(machine), R(self), ReceiverField, _t.WordSize);
        }
        for (int i = 0; i < args.Count; i++)
        {
            _e.Store(R(machine), R(args[i]), am.ParamOffsets[i], args[i].Type.Bytes());
        }

        Require(am.MoveNext);
        _e.Call(Label(am.MoveNext), IrType.Void, R(machine));
        _e.Ret(task is null ? null : R(task));
        _m.Functions.Add(_f);
    }

    /// <summary>`new T()` for a class with a parameterless constructor, or null with an error.</summary>
    private VReg? NewObject(Node at, Type type)
    {
        if (type.Symbol is not TypeSymbol sym)
        {
            Error(at, $"'{type}' is not a type an async method can return");
            return null;
        }
        VReg obj = Allocate(at, Math.Max(_t.ObjectHeaderBytes, sym.InstanceSize));
        _e.Store(R(obj), VtableOf(sym), 0, _t.WordSize);
        MethodSymbol? ctor = sym.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == 0);
        if (ctor is not null)
        {
            CallDirect(ctor, IrType.Void, new List<Operand> { R(obj) });
        }
        return obj;
    }

    /// <summary>
    /// The body, as the state machine's Invoke. The receiver and the
    /// arguments are read out of the machine on entry into the registers the
    /// body uses, and the transform keeps those registers alive across every
    /// suspension like any others.
    /// </summary>
    private void EmitMoveNext(AsyncMethod am)
    {
        MethodSymbol m = am.Method;
        MethodDecl decl = am.Decl;
        _method = m;
        _decl = decl;
        _in = m.Owner.Decl?.File ?? decl.File ?? "";

        _f = new Function(Label(am.MoveNext), IrType.Void) { SourceFile = _in, Line = decl.Line, Display = Display(am.MoveNext), FromLibrary = IsLibrary(am.MoveNext.Owner), Exported = false };
        Block entry = _f.NewBlock("entry");
        _e = new Builder(_f, entry);

        VReg machine = _f.NewReg(IrTypes.Word, "machine");
        _f.Params.Add(machine);
        _stateMachine = machine;
        _f.Async = new AsyncFrame
        {
            StateMachine = machine,
            StateOffset = StateField,
            FieldsStart = am.StateMachine.InstanceSize,
            SizeSymbol = am.SizeSymbol,
        };

        if (!m.Static)
        {
            _this = _e.Load(IrTypes.Word, machine, ReceiverField);
        }

        _params = new VReg[m.Params.Count];
        _paramSlots = new FrameSlot?[m.Params.Count];
        for (int i = 0; i < m.Params.Count; i++)
        {
            ParamSymbol p = m.Params[i];
            IrType it = IrTypes.Of(p.Type);
            _params[i] = _e.Load(it, machine, am.ParamOffsets[i], it.Bytes());
        }

        ScanAddressTaken(decl.Body!);
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

        _returnType = am.Result;
        _returnBlock = _f.NewBlock("complete");
        if (!am.Result.IsVoid)
        {
            _returnValue = _f.NewReg(IrTypes.Of(am.Result), "result");
        }

        // Everything the body throws completes the task with it; for an
        // `async void` method it is unhandled, as in C#.
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

        // Completion with the result.
        _e.SetBlock(_returnBlock);
        if (!m.Returns.IsVoid)
        {
            VReg task = _e.Load(IrTypes.Word, machine, TaskField);
            TypeSymbol? taskType = m.Returns.Symbol;
            MethodSymbol? setResult = taskType?.FindMethods("SetResultCore")
                .FirstOrDefault(s => s.Params.Count == (am.Result.IsVoid ? 0 : 1));
            if (setResult is null)
            {
                Error(decl, $"'{m.Returns}' has no SetResultCore to complete it with");
            }
            else
            {
                List<Operand> args = new() { R(task) };
                if (_returnValue is not null)
                {
                    args.Add(R(Convert(decl, _returnValue, am.Result, setResult.Params[0].Type)));
                }
                CallMethod(setResult, task, args);
            }
        }
        _e.Ret();

        // Completion with the exception.
        VReg exception = LandingValue(failed);
        if (!m.Returns.IsVoid)
        {
            VReg task = _e.Load(IrTypes.Word, machine, TaskField);
            MethodSymbol? setException = m.Returns.Symbol?.FindMethods("SetExceptionCore")
                .FirstOrDefault(s => s.Params.Count == 1);
            if (setException is null)
            {
                Error(decl, $"'{m.Returns}' has no SetExceptionCore to fail it with");
            }
            else
            {
                CallMethod(setException, task, new List<Operand> { R(task), R(exception) });
            }
        }
        else if (AsyncRuntimeMethod(decl, "Unhandled", 1) is MethodSymbol unhandled)
        {
            _e.Call(CallLabel(unhandled), IrType.Void, R(exception));
        }
        _e.Ret();

        _m.Functions.Add(_f);
        _stateMachine = null;
        _method = null;
        _decl = null;
    }

    private MethodSymbol? AsyncRuntimeMethod(Node at, string name, int arity)
    {
        if (_b.Types.TryGetValue("AsyncRuntime", out TypeSymbol? rt)
            && rt.Methods.FirstOrDefault(x => x.Name == name && x.Static && x.Params.Count == arity) is MethodSymbol found)
        {
            Require(found);
            return found;
        }
        Error(at, $"async needs AsyncRuntime.{name} with {arity} parameter(s), which no compiled source provides; compile with lib/threading.cor");
        return null;
    }

    /// <summary>
    /// `await e`: ask the awaiter; if it is done, carry on; otherwise leave
    /// this invocation's exception handlers, register the machine as the
    /// continuation, and suspend. Resumption re-links the handlers into the
    /// new stack frame and carries on at GetResult.
    /// </summary>
    private VReg EmitAwait(AwaitExpr aw)
    {
        if (!_b.Awaits.TryGetValue(aw, out AwaitInfo? info))
        {
            return Fail(aw, "this await did not bind");
        }
        if (_stateMachine is null)
        {
            return Fail(aw, "'await' outside an async method body");
        }

        VReg awaited = Eval(aw.Operand);
        VReg awaiter = CallMethod(info.GetAwaiter, awaited, new List<Operand> { R(awaited) })!;
        VReg done = CallMethod(info.IsCompleted, awaiter, new List<Operand> { R(awaiter) })!;

        Block suspend = _f.NewBlock("suspend");
        Block cont = _f.NewBlock("awaited");
        _e.Branch(done, cont, suspend);

        _e.SetBlock(suspend);
        int w = _t.WordSize;
        if (_openHandlers.Count > 0)
        {
            // The handlers this invocation linked name its stack frame, which
            // is about to go away: unlink them all at once.
            VReg outermost = _e.SlotAddress(_openHandlers[0].Record);
            VReg before = _e.Load(IrTypes.Word, outermost, HandlerPrev / 4 * w);
            _e.Store(ThreadBlockNow(), before, TlsHandler / 4 * w);
        }

        // The markers carry an index so the transform can pair them even
        // when inlining the registration splits it across blocks.
        int point = _awaitPoints++;
        _e.Call(AsyncFrame.Suspend, IrType.Void, Imm(point, IrType.I32));
        CallMethod(info.OnCompleted, awaiter, new List<Operand> { R(awaiter), R(_stateMachine) });
        _e.Call(AsyncFrame.Resume, IrType.Void, Imm(point, IrType.I32));

        // Resumed, in a new frame: the handlers go back on the chain,
        // outermost first, each naming this frame's stack.
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
        _e.Jump(cont);

        _e.SetBlock(cont);
        VReg? result = CallMethod(info.GetResult, awaiter, new List<Operand> { R(awaiter) });
        return result ?? _e.Const(0, IrTypes.Word);
    }
}
