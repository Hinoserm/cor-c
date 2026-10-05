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
///
/// AN `async ValueTask` METHOD ALLOCATES NOTHING UNTIL IT SUSPENDS, as .NET's
/// AsyncValueTaskMethodBuilder: the kickoff keeps the state machine in its
/// own frame, zeroed, while MoveNext runs synchronously; at the first await
/// that really suspends, MoveNext moves it to the heap (the box: a copy, and
/// the task made then) before it registers the continuation, and the kickoff
/// hands back a ValueTask of that task. One that completes without
/// suspending leaves its result in the machine, and the kickoff makes the
/// ValueTask of the result: no state machine, no task, nothing allocated.
/// The transform keeps every register that points into the machine across a
/// suspension as an offset from it, so the move leaves none behind; where it
/// cannot (a register that may point into the machine or elsewhere), it says
/// so in the method's data, and the kickoff takes the heap from the start.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>One async method's pieces.</summary>
    private sealed record AsyncMethod(
        MethodSymbol Method, MethodDecl Decl, TypeSymbol StateMachine,
        MethodSymbol MoveNext, string SizeSymbol, int[] ParamOffsets, Type Result,
        ValueTaskShape? Value = null);

    /// <summary>
    /// An `async ValueTask` or `async ValueTask&lt;T&gt;` method's extra pieces:
    /// the task type made at the first suspension (or a fault), the
    /// ValueTask constructors the kickoff makes its answer with, where in the
    /// machine it says it is still in the kickoff's frame (Home, nonzero) and
    /// where a synchronous result is left, the data word the transform clears
    /// when the machine may not move, and the box function.
    /// </summary>
    private sealed record ValueTaskShape(Type TaskType, MethodSymbol FromTask, MethodSymbol? FromResult,
        int HomeField, int ResultField, string StackSymbol, string BoxLabel);

    /// <summary>The most a state machine kept in a kickoff's frame may take; a larger one is made on the heap.</summary>
    private const int StackMachineBytes = 512;

    /// <summary>Room below a frame-kept machine's payload where a heap block's header would be (Gc.HeaderBytes, either word size).</summary>
    private const int StackMachineHeader = 16;

    /// <summary>The ValueTask the async method being lowered returns, while its MoveNext is; null otherwise.</summary>
    private ValueTaskShape? _valueShape;

    /// <summary>ValueTask or ValueTask&lt;T&gt;, the structs an async method may return besides a task.</summary>
    internal static bool IsValueTaskType(Type t)
    {
        if (t.IsArray || t.IsPointer || t.Symbol is not TypeSymbol s || s.Kind != TypeKind.Struct)
        {
            return false;
        }
        return LastName(s.Decl?.Template ?? s.Decl?.Name ?? s.Name) == "ValueTask";
    }

    private static bool IsTaskClass(Type t)
        => !t.IsArray && t.Symbol is TypeSymbol s && s.Kind == TypeKind.Class
        && LastName(s.Decl?.Template ?? s.Decl?.Name ?? s.Name) == "Task";

    private static string LastName(string name)
    {
        int dollar = name.IndexOfAny(new[] { '$', '`', '<' });
        if (dollar > 0) name = name.Substring(0, dollar);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }

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
            // A captured variable's cell (ParamSymbol.Cell) is an object the
            // machine may hold; any other reference is into a frame.
            if (m.Params[i].ByRef && !m.Params[i].Cell)
            {
                Error(decl, $"'{m.Name}': an async method cannot take a ref or out parameter");
            }
            offsets[i] = at;
            at += 8;
        }

        Type result = AsyncResultType(m.Returns);
        ValueTaskShape? value = null;
        if (IsValueTaskType(m.Returns))
        {
            value = ValueTaskShapeOf(decl, m, result, identity, ref at);
        }

        machine.InstanceSize = at;
        machine.InlineDecided = true;                  // laid out here, nothing in line

        string size = "smsize_" + identity;
        byte[] initial = new byte[_t.WordSize];
        WriteWord(initial, 0, at);
        _m.Data.Add(new DataItem(size, initial) { Align = _t.WordSize, Exported = false });

        AsyncMethod made = new(m, decl, machine, moveNext, size, offsets, result, value);
        _moveNext[moveNext] = made;
        return made;
    }

    /// <summary>
    /// What an `async ValueTask` method's machine has besides a task method's:
    /// its Home word and the room for its result, after the parameters, and
    /// the constructors and task type its answer is made with.
    /// </summary>
    private ValueTaskShape? ValueTaskShapeOf(MethodDecl decl, MethodSymbol m, Type result, string identity, ref int at)
    {
        TypeSymbol shape = StructOf(m.Returns);
        MethodSymbol? fromTask = null, fromResult = null;
        foreach (MethodSymbol c in shape.Methods)
        {
            if (!c.IsCtor || c.Static || c.Params.Count != 1)
            {
                continue;
            }
            if (IsTaskClass(c.Params[0].Type)) fromTask = c;
            else fromResult = c;
        }
        if (fromTask is null)
        {
            Error(decl, $"'{m.Returns}' has no constructor from a task, which an async method returning it is answered with");
            return null;
        }
        if (!result.IsVoid && fromResult is null)
        {
            Error(decl, $"'{m.Returns}' has no constructor from its result, which an async method returning it completes with");
            return null;
        }

        at = (at + 7) / 8 * 8;
        int home = at;
        at += 8;
        int resultField = at;
        int resultBytes = result.IsVoid ? 0 : IsStructValue(result) ? Math.Max(8, StructOf(result).InstanceSize) : 8;
        at += (resultBytes + 7) / 8 * 8;

        // The word the transform clears when this machine may not move
        // (AsyncTransform); 1 until then.
        string stack = "smstack_" + identity;
        byte[] allowed = new byte[_t.WordSize];
        WriteWord(allowed, 0, 1);
        _m.Data.Add(new DataItem(stack, allowed) { Align = _t.WordSize, Exported = false });

        return new ValueTaskShape(fromTask.Params[0].Type, fromTask, fromResult, home, resultField, stack, "smbox_" + identity);
    }

    /// <summary>Where an `async ValueTask&lt;T&gt;` method's synchronous result is kept in its machine.</summary>
    private MemPlace ValueResultPlace(VReg machine, ValueTaskShape vt, Type result)
        => new(R(machine), vt.ResultField, result, Inline: IsStructValue(result));

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
        if (am.Value is ValueTaskShape value)
        {
            EmitValueKickoff(m, decl, am, value);
            EmitValueBox(am, value);
            return;
        }

        _f = new Function(Label(m), IrTypes.Of(m.Returns)) { SourceFile = _in, Line = decl.Line, Display = Display(m), FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner), SourcePath = SourcePathOf(m.Owner),
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

        // The machine's size is decided after optimisation, when the
        // transform knows what has to survive a suspension, so it is read
        // from data the transform fills in rather than written here.
        VReg size = Numbered(_e, _e.Load(IrTypes.Word, new SymOperand(am.SizeSymbol)));
        VReg machine = AllocateDynamic(decl, size);
        _e.Store(R(machine), VtableOf(am.StateMachine), 0, _t.WordSize);

        VReg? task = null;
        if (!m.Returns.IsVoid)
        {
            task = NewObject(decl, m.Returns);
            if (task is not null)
            {
                StoreNewReference(machine, task, TaskField);
            }
        }

        if (self is not null)
        {
            StoreNewReference(machine, self, ReceiverField);
        }
        for (int i = 0; i < args.Count; i++)
        {
            _e.Store(R(machine), R(args[i]), am.ParamOffsets[i], args[i].Type.Bytes());
            // An argument the word size may be a reference; marking the card
            // of one that is not costs a byte store and nothing else.
            if (args[i].Type == IrTypes.Word)
            {
                CardMarkAt(R(machine), am.ParamOffsets[i]);
            }
        }

        Require(am.MoveNext);
        _e.Call(Label(am.MoveNext), IrType.Void, R(machine));
        _e.Ret(task is null ? null : R(task));
        _m.Functions.Add(_f);
    }

    /// <summary>
    /// An `async ValueTask` method as callers see it. The machine is kept in
    /// this frame (StackMachineBytes at most, zeroed as the heap's would be,
    /// its Home word set) unless the transform said it may not move or it is
    /// larger, when it is made on the heap as a task method's is; no task is
    /// made. MoveNext runs; afterwards a task in the machine -- made by the
    /// box at a suspension, or for an exception -- is answered as a
    /// ValueTask of it, and otherwise the result the body left in the
    /// machine is.
    /// </summary>
    private void EmitValueKickoff(MethodSymbol m, MethodDecl decl, AsyncMethod am, ValueTaskShape vt)
    {
        _f = new Function(Label(m), ReturnIr(m)) { SourceFile = _in, Line = decl.Line, Display = Display(m), FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner), SourcePath = SourcePathOf(m.Owner),
            Coalescible = decl.LocalCopy || m.Owner.Decl?.Specialised == true, Exported = m.Owner.Decl?.LocalOnly != true,
            // ITS FRAME HOLDS THE MACHINE: inlined, the machine would be the
            // caller's frame's, which nothing here sizes.
            NoInlining = true };
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
        // The ValueTask is answered through the caller's buffer (Buffered).
        VReg answer = _f.NewReg(IrTypes.Word, "retbuf");
        _f.Params.Add(answer);

        VReg size = _e.Load(IrTypes.Word, new SymOperand(am.SizeSymbol));
        VReg allowed = _e.Load(IrTypes.Word, new SymOperand(vt.StackSymbol));
        FrameSlot room = _f.NewSlot(StackMachineBytes + StackMachineHeader, 8, "machine");
        VReg machine = _f.NewReg(IrTypes.Word, "machine");

        Block fits = _f.NewBlock("vtfits");
        Block onStack = _f.NewBlock("vtstack");
        Block onHeap = _f.NewBlock("vtheap");
        Block made = _f.NewBlock("vtmade");
        _e.Branch(_e.Binary(Opcode.Ne, allowed, 0), fits, onHeap);

        _e.SetBlock(fits);
        _e.Branch(_e.Binary(Opcode.LeU, size, StackMachineBytes), onStack, onHeap);

        _e.SetBlock(onStack);
        VReg bottom = _e.SlotAddress(room);
        VReg whole = _e.Binary(Opcode.Add, size, StackMachineHeader);
        _e.Emit(Opcode.MemSet, null, R(bottom), new ImmOperand(0, IrType.I32), R(whole));
        _e.CopyTo(machine, R(_e.Binary(Opcode.Add, bottom, StackMachineHeader)));
        _e.Store(R(machine), Imm(1, IrTypes.Word), vt.HomeField, _t.WordSize);
        _e.Jump(made);

        _e.SetBlock(onHeap);
        _e.CopyTo(machine, R(AllocateDynamic(decl, size)));
        _e.Jump(made);

        _e.SetBlock(made);
        _e.Store(R(machine), VtableOf(am.StateMachine), 0, _t.WordSize);
        if (self is not null)
        {
            StoreNewReference(machine, self, ReceiverField);
        }
        for (int i = 0; i < args.Count; i++)
        {
            _e.Store(R(machine), R(args[i]), am.ParamOffsets[i], args[i].Type.Bytes());
            if (args[i].Type == IrTypes.Word)
            {
                CardMarkAt(R(machine), am.ParamOffsets[i]);
            }
        }

        Require(am.MoveNext);
        _e.Call(Label(am.MoveNext), IrType.Void, R(machine));

        VReg task = _e.Load(IrTypes.Word, machine, TaskField);
        Block ofTask = _f.NewBlock("vttask");
        Block ofResult = _f.NewBlock("vtresult");
        Block done = _f.NewBlock("vtdone");
        _e.Branch(_e.Binary(Opcode.Ne, task, 0), ofTask, ofResult);

        _e.SetBlock(ofTask);
        CallDirect(vt.FromTask, IrType.Void, new List<Operand> { R(answer), R(task) });
        _e.Jump(done);

        _e.SetBlock(ofResult);
        if (vt.FromResult is MethodSymbol fromResult && !am.Result.IsVoid)
        {
            VReg kept = LoadPlace(ValueResultPlace(machine, vt, am.Result));
            CallDirect(fromResult, IrType.Void, new List<Operand> { R(answer), R(Convert(decl, kept, am.Result, fromResult.Params[0].Type)) });
        }
        else
        {
            // `default`: the completed ValueTask.
            _e.Emit(Opcode.MemSet, null, R(answer), new ImmOperand(0, IrType.I32), Imm(Math.Max(1, StructOf(m.Returns).InstanceSize), IrTypes.Word));
        }
        _e.Jump(done);

        _e.SetBlock(done);
        _e.Ret(R(answer));
        _m.Functions.Add(_f);
    }

    /// <summary>
    /// THE BOX, called at every suspension of an `async ValueTask` method
    /// before the continuation is registered, answering the machine that
    /// continues. Still in the kickoff's frame (Home nonzero): copied whole
    /// to the heap, the task made and put in both copies -- the kickoff reads
    /// the frame's -- and the heap's answered. Already on the heap: the task
    /// made if it has none yet, and the machine itself answered.
    /// </summary>
    private void EmitValueBox(AsyncMethod am, ValueTaskShape vt)
    {
        Function kickoff = _f;
        Builder kickoffBuilder = _e;
        MethodSymbol m = am.Method;
        _f = new Function(vt.BoxLabel, IrTypes.Word) { SourceFile = _in, Line = am.Decl.Line, Display = Display(m) + " (box)", FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner), SourcePath = SourcePathOf(m.Owner), Exported = false };
        _e = new Builder(_f, _f.NewBlock("entry"));
        VReg machine = _f.NewReg(IrTypes.Word, "machine");
        _f.Params.Add(machine);

        Block moving = _f.NewBlock("moving");
        Block staying = _f.NewBlock("staying");
        _e.Branch(_e.Binary(Opcode.Ne, _e.Load(IrTypes.Word, machine, vt.HomeField), 0), moving, staying);

        _e.SetBlock(staying);
        VReg had = _e.Load(IrTypes.Word, machine, TaskField);
        Block make = _f.NewBlock("maketask");
        Block ready = _f.NewBlock("ready");
        _e.Branch(_e.Binary(Opcode.Ne, had, 0), ready, make);
        _e.SetBlock(make);
        if (NewObject(am.Decl, vt.TaskType) is VReg first)
        {
            StoreNewReference(machine, first, TaskField);
        }
        _e.Jump(ready);
        _e.SetBlock(ready);
        _e.Ret(R(machine));

        _e.SetBlock(moving);
        VReg size = _e.Load(IrTypes.Word, new SymOperand(am.SizeSymbol));
        VReg heap = AllocateDynamic(am.Decl, size);
        _e.Emit(Opcode.MemCopy, null, R(heap), R(machine), R(size));
        _e.Store(R(heap), Imm(0, IrTypes.Word), vt.HomeField, _t.WordSize);
        if (NewObject(am.Decl, vt.TaskType) is VReg task)
        {
            StoreNewReference(heap, task, TaskField);
            _e.Store(R(machine), R(task), TaskField, _t.WordSize);
        }
        _e.Ret(R(heap));

        _m.Functions.Add(_f);
        _f = kickoff;
        _e = kickoffBuilder;
    }

    /// <summary>`new T()` for a class with a parameterless constructor, or null with an error.</summary>
    private VReg? NewObject(Node at, Type type)
    {
        if (type.Symbol is not TypeSymbol sym)
        {
            Error(at, $"'{type}' is not a type an async method can return");
            return null;
        }
        VReg obj = Allocate(at, Math.Max(_t.ObjectHeaderBytes, sym.InstanceSize), described: true);
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

        // SUSPENSION POINTS ARE NUMBERED PER FUNCTION, from zero: the markers'
        // numbers are in the IR a shared copy is certified by (Definition-
        // Semantics), and counted across the unit they said how many other
        // state machines the unit happened to lower first.
        int outsideAwaits = _awaitPoints;
        _awaitPoints = 0;
        _f = new Function(Label(am.MoveNext), IrType.Void) { SourceFile = _in, Line = decl.Line, Display = Display(am.MoveNext), FromLibrary = IsLibrary(m.Owner), SystemCode = SystemCode(m.Owner), SourcePath = SourcePathOf(m.Owner), Exported = false };
        Block entry = _f.NewBlock("entry");
        _e = new Builder(_f, entry);

        VReg machine = _f.NewReg(IrTypes.Word, "machine");
        _f.Params.Add(machine);
        _stateMachine = machine;
        _valueShape = am.Value;
        RequireCardMarkObject();
        _f.Async = new AsyncFrame
        {
            StateMachine = machine,
            StateOffset = StateField,
            FieldsStart = am.StateMachine.InstanceSize,
            SizeSymbol = am.SizeSymbol,
            // An `async ValueTask` machine starts in its kickoff's frame and
            // moves to the heap at the first suspension: nothing may keep
            // its address across one but as an offset (AsyncTransform).
            MayMove = am.Value is not null,
            StackSymbol = am.Value?.StackSymbol,
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
            IrType it = p.ByRef ? IrTypes.Word : IrTypes.Of(p.Type);
            _params[i] = Numbered(_e, _e.Load(it, machine, am.ParamOffsets[i], it.Bytes()), !p.ByRef && NeverAddress(p.Type));
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
        _openCatches.Clear();

        // Completion with the result.
        _e.SetBlock(_returnBlock);
        if (am.Value is ValueTaskShape vt)
        {
            // AN `async ValueTask`: into its task when it has one (it
            // suspended, and was boxed), else left in the machine for the
            // kickoff to answer with -- nothing made.
            VReg task = _e.Load(IrTypes.Word, machine, TaskField);
            Block toTask = _f.NewBlock("totask");
            Block toMachine = _f.NewBlock("tomachine");
            _e.Branch(_e.Binary(Opcode.Ne, task, 0), toTask, toMachine);

            _e.SetBlock(toTask);
            MethodSymbol? setResult = vt.TaskType.Symbol?.FindMethods("SetResultCore")
                .FirstOrDefault(s => s.Params.Count == (am.Result.IsVoid ? 0 : 1));
            if (setResult is null)
            {
                Error(decl, $"'{vt.TaskType}' has no SetResultCore to complete it with");
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
            _e.Ret();

            _e.SetBlock(toMachine);
            if (_returnValue is not null)
            {
                StorePlace(ValueResultPlace(machine, vt, am.Result), _returnValue);
            }
        }
        else if (!m.Returns.IsVoid)
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
        if (am.Value is ValueTaskShape faulted)
        {
            // Into its task, made now if it never suspended: the kickoff
            // answers a ValueTask of the faulted task, as .NET's does.
            VReg task = _f.NewReg(IrTypes.Word, "task");
            _e.CopyTo(task, R(_e.Load(IrTypes.Word, machine, TaskField)));
            Block make = _f.NewBlock("faulttask");
            Block have = _f.NewBlock("fault");
            _e.Branch(_e.Binary(Opcode.Ne, task, 0), have, make);
            _e.SetBlock(make);
            if (NewObject(decl, faulted.TaskType) is VReg made)
            {
                StoreNewReference(machine, made, TaskField);
                _e.CopyTo(task, R(made));
            }
            _e.Jump(have);
            _e.SetBlock(have);
            MethodSymbol? setException = faulted.TaskType.Symbol?.FindMethods("SetExceptionCore")
                .FirstOrDefault(s => s.Params.Count == 1);
            if (setException is null)
            {
                Error(decl, $"'{faulted.TaskType}' has no SetExceptionCore to fail it with");
            }
            else
            {
                CallMethod(setException, task, new List<Operand> { R(task), R(exception) });
            }
        }
        else if (!m.Returns.IsVoid)
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
        _valueShape = null;
        _method = null;
        _decl = null;
        _awaitPoints = outsideAwaits;
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
            VReg before = ChainRead(outermost, HandlerPrev / 4 * w);
            ChainWrite(ThreadBlockNow(), R(before), TlsHandler / 4 * w);
        }

        // The markers carry an index so the transform can pair them even
        // when inlining the registration splits it across blocks.
        int point = _awaitPoints++;
        _e.Call(AsyncFrame.Suspend, IrType.Void, Imm(point, IrType.I32));
        // AN `async ValueTask` MOVES TO THE HEAP HERE, at a suspension that is
        // real, and it is the moved machine the awaiter is handed (EmitValueBox).
        VReg continuation = _stateMachine;
        if (_valueShape is ValueTaskShape vt)
        {
            continuation = _e.Call(vt.BoxLabel, IrTypes.Word, R(_stateMachine))!;
        }
        CallMethod(info.OnCompleted, awaiter, new List<Operand> { R(awaiter), R(continuation) });
        _e.Call(AsyncFrame.Resume, IrType.Void, Imm(point, IrType.I32));

        // Resumed, in a new frame: the handlers go back on the chain,
        // outermost first, each naming this frame's stack.
        foreach ((FrameSlot record, AstBlock? _) in _openHandlers)
        {
            VReg addr = _e.SlotAddress(record);
            VReg head = ChainRead(ThreadBlockNow(), TlsHandler / 4 * w);
            ChainWrite(addr, R(head), HandlerPrev / 4 * w);
            VReg sp = _e.Reg(IrTypes.Word, "sp");
            _e.Emit(Opcode.StackPointer, sp);
            ChainWrite(addr, R(sp), HandlerSp / 4 * w);
            VReg fp = _e.Reg(IrTypes.Word, "fp");
            _e.Emit(Opcode.FramePointer, fp);
            ChainWrite(addr, R(fp), HandlerFp / 4 * w);
            ChainWrite(ThreadBlockNow(), R(addr), TlsHandler / 4 * w);
        }
        _e.Jump(cont);

        _e.SetBlock(cont);
        VReg? result = CallMethod(info.GetResult, awaiter, new List<Operand> { R(awaiter) });
        return result ?? _e.Const(0, IrTypes.Word);
    }
}
