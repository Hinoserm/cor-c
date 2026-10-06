#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A method's local functions that are only ever called, and the variables
/// they share with it, kept in one block of the method's frame
/// (BindResult.Environments): the closure every such function runs on is that
/// block, and a captured variable is its word there rather than a cell on the
/// heap. Made by Binder.ArrangeLocalFunctionEnvironment, read by lowering.
/// </summary>
public sealed class LocalEnvironment
{
    /// <summary>The block's size: the object header, then a word or more per variable.</summary>
    public int Bytes;
    /// <summary>The local functions whose closure is the block.</summary>
    public readonly HashSet<LambdaExpr> Lambdas = new(ReferenceEqualityComparer.Instance);
    /// <summary>The method's captured locals kept there, by declaration, and where.</summary>
    public readonly Dictionary<LocalDecl, int> Locals = new(ReferenceEqualityComparer.Instance);
    /// <summary>Its captured parameters that are cells (written), by index, and where.</summary>
    public readonly Dictionary<int, int> Params = new();
    /// <summary>Its captured variables no statement declares -- a foreach's cursor, a pattern's binding -- and where.</summary>
    public readonly Dictionary<LocalSym, int> Syms = new();
}

public sealed partial class Binder
{
    private int _lambdaDepth;
    private readonly HashSet<LambdaExpr> _methodLambdas = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LambdaExpr> _innerLambdas = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LambdaExpr, LocalDecl> _localFunctionOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LocalDecl, HashSet<NameExpr>> _localFunctionRefs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A CALL-ONLY LOCAL FUNCTION NEEDS NO HEAP. A local function lowered as a
    /// lambda made a cell on the heap for every variable it captured and a
    /// closure holding the cells, every time its method ran; a cell's address
    /// stored in a closure is an escape, so everything the variables held
    /// went to the collector too -- the compiler's own passes, Narrowing's and
    /// Escape's, left a fifth of what the collector freed so. Where a local
    /// function is only ever called (BindResult.LocalFunctionCalls, never
    /// read as a value), it cannot outlive the call that runs it, and neither
    /// can what it captured: as C#'s own compiler does with a struct closure,
    /// the variables live in one block of the method's frame, every such
    /// function's closure is that block, and its fields are the variables'
    /// words there, read and written in place.
    ///
    /// Only what is sure: a method that is neither async nor an iterator;
    /// local functions written at its own level, neither async nor iterators
    /// nor returning by reference; captures that are the method's own
    /// declared locals of a word or less, its parameters, or `this`; no other
    /// lambda capturing the same variable as a cell; and no lambda inside one
    /// of them reaching the method's variables through its closure.
    /// </summary>
    private void ArrangeLocalFunctionEnvironment(MethodDecl md)
    {
        try
        {
            if (_method is not null && !md.Mods.HasFlag(Mods.Async) && md.Body is { Iterator: false })
                Arrange(md);
        }
        finally
        {
            _methodLambdas.Clear();
            _innerLambdas.Clear();
            _localFunctionOf.Clear();
            _localFunctionRefs.Clear();
        }
    }

    private static bool InlineInEnvironment(Type t)
        => !t.IsNullableValue && t.Size <= 8 && (t.IsReference || t.IsPointer || t.IsEnumValue || t.Symbol is null || t.Symbol.Kind == TypeKind.Interface);

    // What a capture is in the environment: a cell variable (a declared local
    // or a written parameter), a value (an unwritten parameter, `this`), or
    // something it cannot hold (null).
    private object? EnvironmentKey(Sym from, out bool cell)
    {
        cell = false;
        switch (from)
        {
            case LocalSym { Boxed: true } local when _declOf.TryGetValue(local, out LocalDecl? declared) && InlineInEnvironment(local.Type):
                cell = true;
                return declared;
            // A foreach's cursor or a pattern's binding: no declaration, its
            // cell made on entry by its symbol (Lowering.MakeCapturedCells).
            case LocalSym { Boxed: true } undeclared when InlineInEnvironment(undeclared.Type):
                cell = true;
                return undeclared;
            case ParamSym { ByRef: false, Cell: false } parameter when InlineInEnvironment(parameter.Type):
                cell = parameter.Boxed;
                return parameter.Index;
            case ThisSym:
                return "this";
            default:
                return null;
        }
    }

    private void Arrange(MethodDecl md)
    {
        // --trace-escape NAME: why each of a method's local functions was or
        // was not given the environment, for methods whose name contains NAME.
        bool tracing = Corsac.Lang.Opt.Escape.PromoteTrace is { } traced && md.Name.Contains(traced, StringComparison.Ordinal);
        void Say(LambdaExpr lam, string why)
        {
            if (tracing) Console.Error.WriteLine($"environment {md.Name}: {(_localFunctionOf.TryGetValue(lam, out LocalDecl? d) ? d.Name : "lambda@" + lam.Line)} {why}");
        }
        // The call-only local functions written at the method's own level.
        Dictionary<LambdaExpr, ClosureInfo> chosen = new(ReferenceEqualityComparer.Instance);
        HashSet<NameExpr> called = new(ReferenceEqualityComparer.Instance);
        foreach ((CallExpr call, LocalDecl _) in _r.LocalFunctionCalls)
            if (call.Target is NameExpr name) called.Add(name);
        foreach (LambdaExpr lam in _methodLambdas)
        {
            if (!_localFunctionOf.TryGetValue(lam, out LocalDecl? function) || !_r.Closures.TryGetValue(lam, out ClosureInfo? info)) continue;
            if (lam.Async || lam.BlockBody is { Iterator: true } || info.Invoke.RefReturn || info.Invoke.Static) { Say(lam, "async, iterator or by reference"); continue; }
            if (_localFunctionRefs.TryGetValue(function, out HashSet<NameExpr>? refs) && !refs.All(called.Contains)) { Say(lam, "used as a value"); continue; }
            if (info.Captures.FirstOrDefault(c => EnvironmentKey(c.Source, out _) is null) is { Field: not null } odd) { Say(lam, "captures " + odd.Source); continue; }
            chosen[lam] = info;
        }
        if (chosen.Count == 0) return;

        // TAKEN OUT AGAIN, until nothing changes: a function whose own
        // variable another lambda captures -- to call it -- may be called by
        // that lambda after the method has returned, with its frame gone.
        // A lambda inside a chosen one reaches the method's variables through
        // its closure's fields, which are the environment's words.
        Dictionary<FieldSymbol, object> keyOfField = new(ReferenceEqualityComparer.Instance);
        for (bool changed = true; changed;)
        {
            changed = false;
            keyOfField.Clear();
            foreach ((LambdaExpr lam, ClosureInfo info) in chosen)
                foreach ((FieldSymbol field, Sym from) in info.Captures)
                    keyOfField[field] = EnvironmentKey(from, out _)!;
            HashSet<object> capturedElsewhere = new();
            foreach (LambdaExpr lam in _methodLambdas)
                if (!chosen.ContainsKey(lam) && _r.Closures.TryGetValue(lam, out ClosureInfo? other))
                    foreach ((FieldSymbol _, Sym from) in other.Captures)
                        if (EnvironmentKey(from, out _) is { } key) capturedElsewhere.Add(key);
            foreach (LambdaExpr inner in _innerLambdas)
                if (_r.Closures.TryGetValue(inner, out ClosureInfo? nested))
                    foreach ((FieldSymbol _, Sym from) in nested.Captures)
                        if (from is FieldSym { Field: { } through } && keyOfField.TryGetValue(through, out object? key)) capturedElsewhere.Add(key);
            foreach ((LambdaExpr lam, ClosureInfo info) in chosen.ToList())
                if (_localFunctionOf.TryGetValue(lam, out LocalDecl? function) && capturedElsewhere.Contains(function))
                {
                    Say(lam, "called from a lambda that may outlive the method");
                    chosen.Remove(lam);
                    changed = true;
                }
        }
        if (chosen.Count == 0) return;

        // SHARED, a variable another lambda also captures: it keeps its heap
        // cell, and the environment holds the cell, as any closure does.
        HashSet<object> shared = new();
        foreach (LambdaExpr lam in _methodLambdas)
            if (!chosen.ContainsKey(lam) && _r.Closures.TryGetValue(lam, out ClosureInfo? other))
                foreach ((FieldSymbol _, Sym from) in other.Captures)
                    if (EnvironmentKey(from, out bool cell) is { } key && cell) shared.Add(key);
        foreach (LambdaExpr inner in _innerLambdas)
            if (_r.Closures.TryGetValue(inner, out ClosureInfo? nested))
                foreach ((FieldSymbol _, Sym from) in nested.Captures)
                    if (from is FieldSym { Field: { } through } && keyOfField.TryGetValue(through, out object? key)) shared.Add(key);

        // The block: the header, then each variable once, in the order the
        // functions captured them -- the variable itself where only they
        // capture it, the cell where it is shared, the value where captured
        // by value.
        LocalEnvironment env = new();
        Dictionary<object, int> offsets = new();
        int at = Target.Current.ObjectHeaderBytes;
        foreach (LambdaExpr lam in _methodLambdas.Where(chosen.ContainsKey))
            foreach ((FieldSymbol field, Sym from) in chosen[lam].Captures)
            {
                object key = EnvironmentKey(from, out bool cell)!;
                bool inline = cell && !shared.Contains(key);
                if (!offsets.TryGetValue(key, out int offset))
                {
                    offset = (at + 7) & ~7;
                    offsets[key] = offset;
                    at = offset + (inline || !cell ? Math.Max(8, field.Type.Size) : 8);
                    if (inline && key is LocalDecl local) env.Locals[local] = offset;
                    else if (inline && key is int index) env.Params[index] = offset;
                    else if (inline && key is LocalSym symbol) env.Syms[symbol] = offset;
                }
                // A shared cell's field stays a cell (Boxed), now at its word here.
                if (inline) field.Boxed = false;
                field.Offset = offset;
            }
        env.Bytes = (at + 7) & ~7;
        foreach ((LambdaExpr lam, ClosureInfo info) in chosen)
        {
            Say(lam, "in the environment");
            info.Type.InstanceSize = env.Bytes;
            env.Lambdas.Add(lam);
        }
        _r.Environments[md] = env;
    }
}
