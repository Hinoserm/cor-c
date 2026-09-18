#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

public sealed partial class Lowering
{
    private static bool UsesVirtualDispatch(MethodSymbol m)
        => m.VtableSlot >= 0 && !m.Static && m.Owner.Kind != TypeKind.Struct;

    /// <summary>A direct call by label; the callee is marked reachable.</summary>
    private VReg? CallDirect(MethodSymbol m, IrType returns, List<Operand> args)
    {
        Require(m);
        return _e.Call(CallLabel(m), returns, args.ToArray());
    }

    /// <summary>
    /// A call on a receiver: virtual through the vtable when the method is,
    /// direct otherwise. The receiver, when there is one, is args[0].
    /// </summary>
    private VReg? CallMethod(MethodSymbol m, VReg? receiver, List<Operand> args, bool viaBase = false)
    {
        IrType returns = IrTypes.Of(m.Returns);

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
            return _e.CallIndirect(R(fn), returns, args);
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

    private VReg EmitInstanceCall(MethodSymbol target, Expr on, List<Expr> argExprs, Expr? extra = null)
    {
        VReg receiver = Eval(on);
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

        if (target.Owner.Name is Prelude.TypeName or Prelude.MathType or Prelude.MachineType
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
                        receiver = Eval(m.Target);
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

        for (int i = 0; i < call.Args.Count; i++)
        {
            ParamSymbol p = target.Params[i];
            args.Add(R(EvalAs(call.Args[i], p)));
        }

        bool viaBase = call.Target is MemberExpr { Target: BaseExpr };
        VReg? result = CallMethod(target, receiver, args, viaBase);
        return result ?? _e.Const(0, IrTypes.Word);
    }
}
