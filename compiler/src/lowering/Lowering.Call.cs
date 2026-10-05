#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    private static bool UsesVirtualDispatch(MethodSymbol m)
        => m.VtableSlot >= 0 && !m.Static && m.Owner.Kind != TypeKind.Struct;

    /// <summary>
    /// A STRUCT RETURNED THROUGH ITS CALLER'S STORAGE, as .NET's return buffer:
    /// a method returning a struct held in line takes, after its own
    /// parameters, the address it writes its result to, and answers that
    /// address. The caller hands it a slot of its own frame (ResultBuffer), so
    /// a struct a call makes is no allocation at all. Not a method C calls or
    /// that calls C, nor an async one, whose result goes into a Task.
    /// </summary>
    private bool Buffered(MethodSymbol m)
        => IsStructValue(m.Returns) && StructOf(m.Returns).HeldInline && !m.Async && !m.IsCtor && !m.RefReturn
        && NativeImportOf(m) is null && !CalledByC(m) && m.Decl is not { File: "<prelude>" };

    /// <summary>
    /// Where a call's struct result is written: a slot of this frame, one per
    /// call site -- or, in an iterator's or an async method's body, whose frame
    /// does not outlast a suspension, a block of the heap.
    /// </summary>
    private VReg ResultBuffer(Node at, Type type)
    {
        TypeSymbol shape = StructOf(type);
        int size = Math.Max(4, shape.InstanceSize);
        if (_stateMachine is not null)
        {
            VReg made = Allocate(at, size);
            _heapStructs.Add(made);
            return made;
        }
        FrameSlot slot = _f.NewSlot(size, Math.Min(_t.Align64, Math.Max(4, shape.InlineAlign)), "result");
        return RegOf(new SlotOperand(slot));
    }

    /// <summary>
    /// A result buffer of the heap, written by its callee with no card marks:
    /// its references' cards marked after the call, for a collection that
    /// may have made the block old under the call.
    /// </summary>
    private void MarkBuffer(VReg buffer, Type type)
    {
        if (!_heapStructs.Contains(buffer)) return;
        foreach ((int at, Type field) in TracedFields(StructOf(type), 0))
        {
            if (MayHoldReference(field)) CardMarkAt(R(buffer), at);
        }
    }

    /// <summary>
    /// What a method answers in a register: its value's, or for one that
    /// returns by reference (MethodSymbol.RefReturn) the address of the
    /// variable, a word whatever the variable holds.
    /// </summary>
    private static IrType ReturnIr(MethodSymbol m) => m.RefReturn ? IrTypes.Word : IrTypes.Of(m.Returns);

    /// <summary>
    /// The method a call returns a variable of, or null (ReturnIr). A
    /// delegate's call -- a ref-returning local function's -- is its Invoke's,
    /// and EmitCall calls that through the interface slot, answering the
    /// address the closure's Invoke returned.
    /// </summary>
    private MethodSymbol? RefCallee(CallExpr call)
        => _b.Invocations.TryGetValue(call, out MethodSymbol? invoke)
           ? invoke.RefReturn ? invoke : null
           : _b.Calls.TryGetValue(call, out MethodSymbol? m) && m.RefReturn ? m : null;

    /// <summary>
    /// The variable a ref-returning call answers: the call made, and its
    /// address the place -- a struct's the address of its bytes, as a
    /// by-reference parameter's is.
    /// </summary>
    private MemPlace RefCallPlace(CallExpr call, MethodSymbol m)
        => new(R(EmitCall(call)), 0, m.Returns, false, IsStructValue(m.Returns));

    /// <summary>A direct call by label; the callee is marked reachable.</summary>
    private VReg? CallDirect(MethodSymbol m, IrType returns, List<Operand> args)
    {
        Require(m);
        VReg? buffer = Buffered(m) ? ResultBuffer(_decl ?? (Node)new MethodDecl { Name = m.Name, Line = 0, Col = 0 }, m.Returns) : null;
        if (buffer is not null) args = new List<Operand>(args) { R(buffer) };
        // THE PROGRAM'S OWN SOURCE CALLING INTO THE COLLECTOR -- asking its
        // heap where a block is, collecting -- means it has one, whatever its
        // allocations need (Escape). Reading a counter (a getter) does not.
        if (_f is { FromLibrary: false } && m.Owner.Name is "Gc" or "HeapChunks" or "GcLock" or "GcRoots" or "GcThreads"
            && !m.Name.StartsWith("get_", StringComparison.Ordinal))
            _m.CallsCollector = true;
        VReg? made = _e.Call(CallLabel(m), returns, args.ToArray());
        if (buffer is not null) MarkBuffer(buffer, m.Returns);
        // A struct a method of source returns other than through a buffer is
        // made for this caller.
        if (!Buffered(m) && !m.RefReturn && IsStructValue(m.Returns) && m.Decl is { File: not "<prelude>" }) _e.Block.Instrs[^1].Field = Instr.FreshStruct;
        return made;
    }

    /// <summary>Whether a type is, extends or implements another.</summary>
    private static bool Derives(TypeSymbol type, TypeSymbol of)
    {
        for (TypeSymbol? at = type; at is not null; at = at.Base)
        {
            if (at == of) return true;
            List<TypeSymbol> faces = new();
            foreach (TypeSymbol face in at.Interfaces) AddInterfaceClosure(face, faces);
            if (faces.Contains(of)) return true;
        }
        return false;
    }

    /// <summary>
    /// A call on a receiver: virtual through the vtable when the method is,
    /// direct otherwise. The receiver, when there is one, is args[0].
    /// </summary>
    private VReg? CallMethod(MethodSymbol m, VReg? receiver, List<Operand> args, bool viaBase = false, TypeSymbol? through = null)
    {
        IrType returns = ReturnIr(m);

        // EVERY CALL INTO A TYPE TOUCHES IT (Lowering.StaticInit), whichever
        // path made it: a static property's getter or setter, a compound
        // assignment through one, a struct's accessor. They are static
        // methods like any other, about to read what the initialisers set;
        // `static Cursor Default { get { return _default; } }` answered null
        // until something else had touched Cursors. EmitCall has already
        // asked for an ordinary call, before its arguments, and the optimiser
        // folds the repeat.
        if (m.Static || m.Owner.Kind == TypeKind.Struct)
        {
            TouchType(m.Owner);
        }

        if (UsesVirtualDispatch(m) && !viaBase && receiver is not null)
        {
            if (m.VtableSlot == _b.ToStringSlot && m.Params.Count == 0)
            {
                // ObjectString recognizes strings, whose descriptor has no
                // callable vtable. Unlike concatenation, an explicit instance
                // call on null must still fault: retain the original read.
                _e.Load(IrTypes.Word, receiver, 0);
                return ObjectString(receiver);
            }
            VReg vt = _e.Load(IrTypes.Word, receiver, 0);
            VReg fn = _e.Load(IrTypes.Word, vt, (long)m.VtableSlot * _t.WordSize);
            VReg? buffer = Buffered(m) ? ResultBuffer(_decl ?? (Node)new MethodDecl { Name = m.Name, Line = 0, Col = 0 }, m.Returns) : null;
            if (buffer is not null) args = new List<Operand>(args) { R(buffer) };
            VReg? called = _e.CallIndirect(R(fn), returns, args);
            // THE RECEIVER'S OWN TYPE when it is narrower than the method's:
            // what can answer `walker.Dispose()` on an IEnumerator<int> is an
            // enumerator, not every IDisposable in the program -- a
            // TextWriter's Dispose among the targets let every foreach's
            // sequence go (Escape.IndirectTargets reads this).
            _e.Block.Instrs[^1].DispatchType = DispatchName(through is not null && through != m.Owner && Derives(through, m.Owner) ? through : m.Owner);
            if (buffer is not null) MarkBuffer(buffer, m.Returns);
            // Whatever implementation answers, a struct it returns other than
            // through a buffer is a copy made for this caller.
            if (!Buffered(m) && !m.RefReturn && IsStructValue(m.Returns)) _e.Block.Instrs[^1].Field = Instr.FreshStruct;
            // NOT ONE THAT RETURNS BY REFERENCE: what it answers is an address
            // its closure's captures may reach -- an element of an array it
            // holds -- so the closure is not merely run by the call, and the
            // escape pass asks the overrides instead (Escape, DelegateInvoke).
            else if (m.Name == "Invoke" && m.Owner.Kind == TypeKind.Interface && !m.RefReturn) _e.Block.Instrs[^1].Field = Instr.DelegateInvoke;
            return called;
        }

        // A CALL ON NULL THROWS AT THE CALL, as C#'s callvirt has it, whether
        // or not the method then reads `this`: the receiver's first word is
        // read, and on a null one that read is the NullReferenceException
        // (Runtime.NullFault). Not on `this`, which is never null, nor on a
        // base call, nor on a struct, whose receiver is an address of storage.
        if (receiver is not null && !m.Static && !viaBase && m.Owner.Kind != TypeKind.Struct
            && !ReferenceEquals(receiver, _this))
        {
            _e.Load(IrTypes.Word, receiver, 0);
        }

        return CallDirect(m, returns, args);
    }

    private VReg CallAccessor(MethodSymbol accessor, bool self, Expr? target, VReg? receiver = null, VReg? value = null)
    {
        List<Operand> args = new();
        VReg? recv = null;

        if (!accessor.Static)
        {
            recv = receiver ?? (self ? _this : target is not null ? Eval(target) : null);
            if (recv is null)
            {
                return Fail(target ?? (Node)_decl!, "this accessor has no receiver");
            }
            args.Add(R(recv));
        }

        if (value is not null)
        {
            args.Add(R(value));
        }

        // base.Prop must reach the base implementation directly, the same as
        // a base.Method() call in EmitCall -- otherwise an override that
        // reads/writes base.Prop calls back into itself through the vtable.
        bool viaBase = target is BaseExpr;
        VReg? r = CallMethod(accessor, recv, args, viaBase);
        return r ?? _e.Const(0, IrTypes.Word);
    }

    /// <summary>
    /// OBJECT'S MEMBERS ON A STRUCT THAT DECLARES NONE are ValueType's, asked
    /// of the boxed value: its descriptor's slots compare, hash and name it by
    /// its fields, as .NET boxes a struct to call an inherited Equals.
    /// </summary>
    private VReg BoxedForObject(MethodSymbol target, Expr on, VReg receiver)
    {
        if (target.Owner.Name == "object" && !target.Static && _b.TypeOf(on) is { } of
            && of.Symbol is { Kind: TypeKind.Struct } && !of.IsNullableValue && !of.IsPointer && !of.IsArray)
        {
            return BoxValue(on, receiver, of);
        }
        return receiver;
    }

    private VReg EmitInstanceCall(MethodSymbol target, Expr on, List<Expr> argExprs, Expr? extra = null)
    {
        VReg receiver = BoxedForObject(target, on, Eval(on));
        List<Operand> args = new() { R(receiver) };
        for (int i = 0; i < argExprs.Count; i++)
        {
            args.Add(R(EvalAs(argExprs[i], target.Params[i])));
        }
        if (extra is not null)
        {
            ParamSymbol p = target.Params[argExprs.Count];
            args.Add(R(EvalAs(extra, p)));
        }
        bool viaBase = on is BaseExpr;
        return CallMethod(target, receiver, args, viaBase) ?? _e.Const(0, IrTypes.Word);
    }

    private VReg EmitCall(CallExpr call)
    {
        if (_b.PointerCalls.TryGetValue(call, out FunctionPointer? pointer))
        {
            return EmitPointerCall(call, pointer);
        }

        // Enum.HasFlag: (value & flag) == flag.
        if (_b.EnumHasFlags.Contains(call) && call.Target is MemberExpr on && call.Args.Count == 1)
        {
            Type et = _b.TypeOf(on.Target);
            VReg v = Eval(on.Target);
            VReg flag = EvalAs(call.Args[0], et);
            VReg both = _e.Binary(Opcode.And, v, flag);
            return _e.Binary(Opcode.Eq, both, flag);
        }

        // System.Enum's statics, over the table this enum's names are in.
        if (_b.EnumStatics.TryGetValue(call, out (TypeSymbol Enum, string Name) statics))
        {
            return EmitEnumStatic(call, statics.Enum, statics.Name);
        }

        // object.ReferenceEquals and the static object.Equals: the same reference or not.
        if (_b.Calls.TryGetValue(call, out MethodSymbol? rooted)
            && rooted.Static && rooted.Owner.Name == "object" && call.Args.Count == 2)
        {
            VReg a = EvalAs(call.Args[0], rooted.Params[0].Type);
            VReg b = EvalAs(call.Args[1], rooted.Params[1].Type);

            if (rooted.Name != "Equals")
            {
                return _e.Binary(Opcode.Eq, a, b);
            }

            // object.Equals(a, b) IS NOT ReferenceEquals. .NET: the same
            // reference, or neither of them null and a.Equals(b) says so --
            // asked of a's OWN Equals, through its slot. `Equals(Element,
            // other.Element)` is how a type compares what it is made of, and
            // answered by reference two equal types were different types.
            return _e.Call(KeyEqualsStub(), IrType.I32, R(a), R(b))!;
        }

        // Calling a value is calling its Invoke through the interface slot.
        if (_b.Invocations.TryGetValue(call, out MethodSymbol? invoke))
        {
            if (call.LocalArgumentOrder.Count != 0)
            {
                VReg localReceiver = Eval(call.Target);
                Operand[] prepared = new Operand[call.Args.Count];
                foreach (int index in call.LocalArgumentOrder)
                    prepared[index] = R(EvalAs(call.Args[index], invoke.Params[index]));
                List<Operand> ordered = new() { R(localReceiver) };
                ordered.AddRange(prepared);
                return CallMethod(invoke, localReceiver, ordered) ?? _e.Const(0, IrTypes.Word);
            }
            return EmitInstanceCall(invoke, call.Target, call.Args);
        }

        if (!_b.Calls.TryGetValue(call, out MethodSymbol? target))
        {
            if (_b.AddressOf.TryGetValue(call, out MethodSymbol? named))
            {
                if (named.Decl?.File == "<prelude>")
                {
                    return Fail(call, $"the intrinsic '{named.Owner.Name}.{named.Name}' has no callable address");
                }

                // A DELEGATE OVER A STATIC METHOD touches the type here, where
                // the address is taken: the call that follows is indirect and
                // has no name to hang the trigger on.
                if (named.Static)
                {
                    TouchType(named.Owner);
                }
                Require(named);
                // Sys.AddressOf is a language long, even when a machine
                // address is only 32 bits. Call/store ABI widths follow the
                // declared result type, not the symbol operand's word size.
                return Widen(_e.Address(CallLabel(named)));
            }
            return Fail(call, "this call did not resolve to a method");
        }

        if (target.Owner.Name is Prelude.TypeName or Prelude.MathType
            && target.Decl?.File == "<prelude>")
        {
            return EmitIntrinsic(call, target);
        }

        VReg? receiver = null;
        List<Operand> args = new();

        if (target.Static)
        {
            // CALLING A STATIC METHOD TOUCHES THE TYPE: before the arguments
            // are worked out, since the method is about to read whatever its
            // initialisers set.
            TouchType(target.Owner);
        }
        else
        {
            if (_b.CapturedReceivers.TryGetValue(call, out FieldSymbol? capturedReceiver))
            {
                receiver = _e.Load(IrTypes.Word, _this!, capturedReceiver.Offset);
            }
            else
            {
                switch (call.Target)
                {
                    case MemberExpr m:
                        receiver = BoxedForObject(target, m.Target, Eval(m.Target));
                        break;
                    case NameExpr:
                        receiver = _this;
                        break;
                    default:
                        return Fail(call, "this receiver form is not implemented yet");
                }
            }

            if (receiver is null)
            {
                return Fail(call, "an instance method was called without a receiver");
            }

            // A STRUCT CAN COME INTO BEING WITHOUT A `new` -- `S s =
            // default(S);` -- so its instance calls ask as well. A class
            // cannot: to have one is to have made one, and making one asked.
            if (target.Owner.Kind == TypeKind.Struct)
            {
                TouchType(target.Owner);
            }
            args.Add(R(receiver));
        }

        // A GENERIC VIRTUAL CALL is bound to the template, whose parameters
        // are still T; the arguments are converted to what T is at this call.
        _b.GenericDispatches.TryGetValue(call, out GenericDispatch? dispatch);
        IReadOnlyList<ParamSymbol> parameters = dispatch?.Params ?? target.Params;

        for (int i = 0; i < call.Args.Count; i++)
        {
            ParamSymbol p = parameters[i];
            args.Add(R(EvalAs(call.Args[i], p)));
        }

        if (dispatch is not null && receiver is not null)
        {
            return GenericVirtualDispatch(dispatch, receiver, args) ?? _e.Const(0, IrTypes.Word);
        }

        bool viaBase = call.Target is MemberExpr { Target: BaseExpr };
        if (target.GenericVirtual && !viaBase)
        {
            return Fail(call, "the generic virtual call to '" + target.Signature + "' was never given its dispatch");
        }

        TypeSymbol? through = call.Target is MemberExpr { Target: var throughExpr } && _b.TypeOf(throughExpr) is { IsArray: false, PointerDepth: 0, Symbol: TypeSymbol { Kind: TypeKind.Class or TypeKind.Interface } st } ? st : null;
        VReg? result = CallMethod(target, receiver, args, viaBase, through);
        return result ?? _e.Const(0, IrTypes.Word);
    }

    /// <summary>
    /// A call of a generic virtual method (Binder.GenericVirtualCall): the
    /// receiver tested against every class that overrides it, deepest first,
    /// and the first that matches called directly. What none of them matches
    /// runs the method's own implementation -- or, when that is abstract,
    /// raises MissingMethodException, because the receiver's class overrides
    /// the method somewhere this call could not see.
    /// </summary>
    private VReg? GenericVirtualDispatch(GenericDispatch dispatch, VReg receiver, List<Operand> args)
    {
        // Each copy answers as its method does (ReturnIr): an address when
        // the method returns by reference.
        IrType returns = dispatch.Fallback is MethodSymbol answering ? ReturnIr(answering)
            : dispatch.Targets.Count > 0 ? ReturnIr(dispatch.Targets[0].Copy) : IrTypes.Of(dispatch.Returns);
        VReg? result = dispatch.Returns.IsVoid ? null : _f.NewReg(returns, "gvm");
        Block end = _f.NewBlock("gvmend");

        // THROUGH NULL IT FAULTS, as a call through a vtable does: C# checks
        // the receiver of every virtual call, and the type tests below would
        // otherwise send null quietly to the fallback.
        _e.Load(IrTypes.Word, receiver, 0);

        void Land(MethodSymbol copy, bool boxed = false)
        {
            // A STRUCT'S METHOD ON ITS BOX takes the value, past the header,
            // as `this` -- what the box's interface stub does for a slot.
            List<Operand> passed = args;
            if (boxed)
            {
                passed = new List<Operand>(args);
                passed[0] = R(_e.Binary(Opcode.Add, receiver, _t.ObjectHeaderBytes));
            }
            VReg? got = CallDirect(copy, returns, passed);
            if (result is not null && got is not null)
            {
                _e.CopyTo(result, R(got));
            }
            _e.Jump(end);
        }

        foreach ((TypeSymbol type, MethodSymbol copy) in dispatch.Targets)
        {
            Block hit = _f.NewBlock("gvmhit");
            Block next = _f.NewBlock("gvmnext");
            bool boxed = type.Kind == TypeKind.Struct;
            _e.Branch(boxed ? BoxTest(receiver, new Type { Prim = Prim.Void, Symbol = type }) : TypeTest(receiver, type), hit, next);
            _e.SetBlock(hit);
            Land(copy, boxed);
            _e.SetBlock(next);
        }

        if (dispatch.Fallback is MethodSymbol own)
        {
            Land(own);
        }
        else
        {
            MethodSymbol? missing = RuntimeMethod("GenericVirtualMissing", 2);
            if (missing is not null)
            {
                Require(missing);
                _e.Call(CallLabel(missing), IrType.Void, R(receiver), R(_e.Address(InternString(dispatch.Method))));
            }
            _e.Emit(Opcode.Trap, null);
            _e.Unreachable();
        }

        _e.SetBlock(end);
        return result;
    }
}
