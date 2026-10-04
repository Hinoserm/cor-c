#nullable enable
namespace Corsac.Lang;

/// <summary>
/// REF LOCALS AND REF RETURNS: `ref ulong word = ref _bits[i];`, `ref int
/// Find(...) => ref _items[i];`. A ref local is a second name
/// for a variable that lives somewhere else -- an array element, a field, a
/// local, a by-reference parameter -- and the lowering keeps it as that
/// variable's address, exactly as it keeps a by-reference parameter.
///
/// What the checker owns is C#'s rules about them: the initialiser is a
/// variable of exactly the local's type, a `ref readonly` one is never written
/// through, a reference to something read-only is only taken read-only, a ref
/// local never reaches a lambda, and a reference never outlives the variable it
/// names (RefEscapes).
///
/// A DELEGATE RETURNS BY REFERENCE TOO: `delegate ref int D(...)`, and the
/// delegate a local function `ref int At(int[] xs, int i) => ref xs[i];` is
/// made into (Parser.LocalFunctionDelegate), whose Invoke carries the same
/// Mods.RefReturn a method does. A lambda converted to one is a closure whose
/// Invoke returns the address (CheckLambda), and calling the delegate is a
/// variable exactly as calling a ref-returning method is (RefCallee). Its
/// body may not hand back a variable it captured: that is a local of the
/// enclosing method, and C# reaches it no further (CapturedVariable).
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// Set while a reference is taken for something that only reads through
    /// it -- a `ref readonly` local -- so a read-only variable may be named.
    /// </summary>
    private bool _readOnlyReference;

    private void CheckRefLocal(LocalDecl d)
    {
        // C# 12's rule, and the frame's: an async method or an iterator keeps
        // its locals in an object on the heap between resumptions, and an
        // address into this method's frame is not something that object may
        // hold.
        if (_method is { Async: true } || _method?.Decl is MethodDecl { Body.Iterator: true })
        {
            Error(d, $"'{d.Name}' is a ref local, and an async method or an iterator cannot have one");
        }

        Type type = Type.Error;
        bool escapes = false;

        if (d.Init is not RefArgExpr { IsOut: false, Name: null } reference)
        {
            Error(d, $"'{d.Name}' is a ref local, so it must be initialised with 'ref' and a variable");
            if (d.Init is not null) CheckExpr(d.Init);
            if (d.Type is not null) type = Resolve(d.Type, _thisType);
        }
        else
        {
            Type referred = CheckReference(reference, d.IsReadOnlyRef);
            type = d.Type is null ? referred : Resolve(d.Type, _thisType);
            RequireSameReferenceType(reference, referred, type, d.Name);
            escapes = RefEscapes(reference.Target);
        }

        int slot = NewSlot();
        _r.LocalSlot[d] = slot;
        _r.LocalType[d] = type;

        LocalSym made = new(slot, type, d.Name) { IsRef = true, ReadOnlyRef = d.IsReadOnlyRef, RefEscapes = escapes };

        _r.LocalSymbols[d] = made;
        _declOf[made] = d;
        Declare(d, d.Name, made);
        _assigned.Add(made);

        foreach (LocalDecl also in d.Also)
        {
            CheckStmt(also);
        }
    }

    /// <summary>
    /// `r = ref other;`: the ref local is pointed at another variable. The
    /// value of the expression is the variable, as C#'s is.
    /// </summary>
    private Type CheckRefAssignment(AssignExpr a, RefArgExpr reference)
    {
        if (a.Target is not NameExpr name || Lookup(name.Name) is not LocalSym { IsRef: true } local)
        {
            Error(a, "only a ref local can be pointed at another variable with '= ref'");
            CheckExpr(reference);
            return Type.Error;
        }

        _r.Resolved[name] = local;
        Type referred = CheckReference(reference, local.ReadOnlyRef);

        // ANOTHER VARIABLE IS ANOTHER VALUE: whatever was proved about what
        // the local read before is not known of what it reads now.
        _notNull.Remove(local);
        _notNullPaths.RemoveWhere(p => p == local.Name || p.StartsWith(local.Name + ".", StringComparison.Ordinal));
        RequireSameReferenceType(reference, referred, local.Type, local.Name);

        // A REF LOCAL'S REACH IS FIXED WHERE IT IS DECLARED. One that may be
        // returned cannot be pointed at a variable of this frame, which would
        // be gone by then.
        if (local.RefEscapes && !RefEscapes(reference.Target))
        {
            Error(a, $"cannot point '{local.Name}' at this variable: it lives in this method, "
                   + $"and '{local.Name}' may be used beyond it");
        }
        return local.Type;
    }

    /// <summary>
    /// `return ref x;` from a method that returns by reference: the variable
    /// itself goes back, so it must be one the caller can still reach -- not
    /// a local of this method, nor anything held in one (RefEscapes).
    /// </summary>
    private void CheckRefReturn(ReturnStmt r)
    {
        if (_method is not { RefReturn: true } method)
        {
            Error(r, _method is not null && IsClosure(_method.Owner)
                ? "only a lambda or local function that returns by reference can 'return ref'"
                : "only a method that returns by reference can 'return ref'");
            if (r.Value is not null) CheckExpr(r.Value);
            return;
        }
        // A lambda's or local function's Invoke is named for what it is.
        string name = IsClosure(method.Owner) ? "this function" : method.Name;
        string spelt = IsClosure(method.Owner) ? name : $"'{name}'";
        if (method.Async)
        {
            Error(r, $"{spelt} is async, and an async method cannot return by reference");
        }
        if (r.Value is not RefArgExpr { IsOut: false, Name: null } reference)
        {
            Error(r, $"{spelt} returns by reference, so it returns 'ref' and a variable");
            if (r.Value is not null) CheckExpr(r.Value);
            return;
        }

        Type referred = CheckReference(reference, method.RefReturnReadOnly);
        RequireSameReferenceType(reference, referred, method.Returns, name);
        if (referred.IsError || RefEscapes(reference.Target)) return;

        // A VARIABLE A LAMBDA OR LOCAL FUNCTION CAPTURED is the enclosing
        // method's local, and C# answers it as one (CS8168, CS8166): the
        // closure holding it is no promise it outlives the call.
        if (CapturedVariable(reference.Target) is string captured)
        {
            Error(r, $"cannot return a reference to '{captured}': it is a variable of the enclosing method, "
                   + "captured, and a reference to it may not leave this function");
            return;
        }
        Error(r, "cannot return a reference to this variable: it lives in this method, "
               + "and is gone when the method returns");
    }

    /// <summary>
    /// The method a call returns a variable of (MethodSymbol.RefReturn), or
    /// null. A delegate's call -- a local function's among them -- answers as
    /// its Invoke does: `delegate ref int D(...)` returns a variable.
    /// </summary>
    private MethodSymbol? RefCallee(CallExpr call)
        => _r.Invocations.TryGetValue(call, out MethodSymbol? invoke)
           ? invoke.RefReturn ? invoke : null
           : _r.Calls.TryGetValue(call, out MethodSymbol? method) && method.RefReturn ? method : null;

    /// <summary>What a call is written as calling: a local function's or delegate's own name, not 'Invoke'.</summary>
    private static string CalleeName(CallExpr call, MethodSymbol method)
        => call.Target switch
        {
            NameExpr n => n.Name,
            MemberExpr m => m.Name,
            _ => method.Name,
        };

    /// <summary>A class the checker made for a lambda or a local function (CheckLambda).</summary>
    private static bool IsClosure(TypeSymbol type) => type.Name.StartsWith("Lambda$", StringComparison.Ordinal);

    /// <summary>
    /// A closure's field holding a variable it captured from the method it
    /// was written in -- not its `$this`, nor a bound method group's target.
    /// </summary>
    private static bool IsCapture(FieldSymbol field)
        => IsClosure(field.Owner) && field.Name != "$this" && field.Name != BoundTargetField;

    /// <summary>
    /// The captured variable a reference names, through the fields of a
    /// struct held in it, or null.
    /// </summary>
    private string? CapturedVariable(Expr target)
        => target switch
        {
            NameExpr n when _r.Resolved.TryGetValue(n, out Sym? s) && s is FieldSym { Field: var f } && IsCapture(f) => n.Name,
            MemberExpr { Target: { } inner } m when m.Target is not ThisExpr
                && _r.TypeOf(inner) is { Symbol.Kind: TypeKind.Struct, Nullable: false, IsArray: false, IsPointer: false }
                => CapturedVariable(inner),
            _ => null,
        };

    /// <summary>The type of the variable a `ref` expression names, checked as one.</summary>
    private Type CheckReference(RefArgExpr reference, bool readOnly)
    {
        bool outer = _readOnlyReference;
        _readOnlyReference = readOnly;
        Type referred = CheckExpr(reference);
        _readOnlyReference = outer;
        return referred;
    }

    /// <summary>
    /// A REFERENCE IS TO EXACTLY ITS TYPE. No conversion can happen through
    /// an address -- an int read as a long is four bytes that are not there --
    /// so C# asks for the same type, nullable annotations aside.
    /// </summary>
    private void RequireSameReferenceType(Node at, Type referred, Type wanted, string name)
    {
        if (referred.IsError || wanted.IsError) return;
        if (!MethodSignatures.SameType(referred.AsNonNullable(), wanted.AsNonNullable()))
        {
            Error(at, $"'{name}' refers to a '{wanted}', and this variable is a '{referred}'");
        }
    }

    /// <summary>
    /// A WRITE, OR A WRITABLE REFERENCE, THROUGH SOMETHING READ-ONLY: a `ref
    /// readonly` local, an `in` parameter, or a field of a struct held by one.
    /// Answers the name at fault, or null. Asked of a checked expression.
    /// </summary>
    private string? ReadOnlyVariable(Expr target)
    {
        switch (target)
        {
            case NameExpr n when _r.Resolved.TryGetValue(n, out Sym? named):
                return named switch
                {
                    LocalSym { ReadOnlyRef: true } l => $"'{l.Name}' is a ref readonly local",
                    ParamSym { ReadOnly: true } p => $"'{p.Name}' is an 'in' parameter",
                    _ => null,
                };

            // A FIELD OF A STRUCT IS PART OF IT: writing `r.X` writes r. Only a
            // struct's -- through a reference to an object, the object is not
            // what is read-only.
            case MemberExpr { Target: { } inner } when _r.TypeOf(inner) is { Symbol.Kind: TypeKind.Struct, Nullable: false, IsArray: false, IsPointer: false }:
                return ReadOnlyVariable(inner);

            case CallExpr call when RefCallee(call) is { RefReturnReadOnly: true } method:
                return $"'{CalleeName(call, method)}' returns a ref readonly variable";

            default:
                return null;
        }
    }

    /// <summary>
    /// WHETHER A REFERENCE TO THIS VARIABLE MAY LEAVE THE METHOD: what C#
    /// calls its ref-safe-to-escape scope, answered as the two scopes that
    /// matter -- this method, or beyond it. The heap and a caller's variables
    /// outlive the call; this frame's locals and by-value parameters, and a
    /// struct's fields inside them, do not.
    /// </summary>
    private bool RefEscapes(Expr target)
    {
        switch (target)
        {
            case NameExpr n when _r.Resolved.TryGetValue(n, out Sym? s):
                return s switch
                {
                    LocalSym { IsRef: true } l => l.RefEscapes,
                    LocalSym => false,
                    ParamSym p => p.ByRef,
                    // A VARIABLE A CLOSURE CAPTURED is a local of the method
                    // it was written in, and reaches no further than one
                    // (CapturedVariable): a field of the closure's class only
                    // in how it is kept.
                    FieldSym f when IsCapture(f.Field) => false,
                    // A field named alone is this object's: on the heap for a
                    // class, a struct's own `this` -- a reference the caller
                    // lent, which C# does not let the struct hand back.
                    FieldSym f => f.Field.Static || f.Field.Owner.Kind != TypeKind.Struct,
                    // The enclosing instance's field, read through the `this`
                    // a lambda or local function holds: the object's, as
                    // above.
                    CapturedFieldSym c => c.Field.Static || c.Field.Owner.Kind != TypeKind.Struct,
                    _ => false,
                };

            case MemberExpr m when _r.Resolved.TryGetValue(m, out Sym? s) && s is FieldSym f:
            {
                if (f.Field.Static) return true;
                Type owner = _r.TypeOf(m.Target);
                if (owner.IsReference || owner.IsPointer || owner.Nullable && !owner.IsNullableValue) return true;
                return m.Target is ThisExpr ? false : RefEscapes(m.Target);
            }

            case IndexExpr:
            case UnaryExpr { Op: UnOp.Deref }:
                return true;

            case CallExpr call:
                return RefCallEscapes(call);

            default:
                return false;
        }
    }

    /// <summary>
    /// A ref-returning call's result reaches as far as every reference passed
    /// to it does, since it may be any one of them handed back.
    /// </summary>
    private bool RefCallEscapes(CallExpr call)
    {
        if (RefCallee(call) is not MethodSymbol method) return false;
        for (int i = 0; i < call.Args.Count; i++)
        {
            // Passed by reference, `in` too, whatever the call site wrote.
            Expr argument = call.Args[i] is RefArgExpr passed ? passed.Target : call.Args[i];
            bool byReference = call.Args[i] is RefArgExpr || i < method.Params.Count && method.Params[i].ByRef;
            if (byReference && !RefEscapes(argument)) return false;
        }
        return true;
    }

    /// <summary>
    /// C# REFUSES A REF LOCAL INSIDE A LAMBDA OR A LOCAL FUNCTION: a closure
    /// is an object on the heap and may outlive the frame the reference points
    /// into. Asked of what a lambda's body named from outside it.
    /// </summary>
    private void RefuseCapturedRefLocals(Node at, IEnumerable<string> captured)
    {
        foreach (string name in captured)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
            {
                if (!_scopes[i].TryGetValue(name, out Sym? s)) continue;
                if (s is LocalSym { IsRef: true })
                {
                    Error(at, $"cannot use ref local '{name}' inside a lambda or a local function");
                }
                break;
            }
        }
    }
}
