#nullable enable
namespace Corsac.Lang;

public sealed partial class BindResult
{
    /// <summary>
    /// How many 64-bit words each type's ancestor mask takes.
    ///
    /// Program-wide: every mask in an image is the same width, so the word a
    /// given type bit lives in is the same in front of every vtable. One for
    /// anything with 64 types or fewer, which is nearly everything.
    /// </summary>
    public int MaskWords { get; set; } = 1;

    /// <summary>
    /// The vtable slot ToString occupies on every class in the image.
    /// </summary>
    public int ToStringSlot { get; set; }

    /// <summary>
    /// The vtable slot Equals(object) occupies on every class in the image.
    ///
    /// Reserved for the same reason ToString's is: a generic method compiled
    /// once over the machine word has to be able to ASK its values things, and
    /// the only way to ask a word anything is through the vtable at offset
    /// zero. `a.Equals(b)` inside a predicate is the case that needs it.
    /// </summary>
    public int EqualsSlot { get; set; }

    /// <summary>
    /// The slot GetHashCode() has on every class, beside Equals and for the
    /// same reason: a table holding keys it knows only as words asks the key
    /// for both, as .NET's does.
    /// </summary>
    public int HashSlot { get; set; }

    /// <summary>
    /// The slot a class's CompareTo is reachable at, whatever interface it was
    /// written for: how the default comparer orders keys it knows only as
    /// words. A tuple's is written for it, element by element.
    /// </summary>
    public int CompareSlot { get; set; }

    /// <summary>
    /// Expressions whose value has to be put in a Nullable&lt;T&gt; cell.
    ///
    /// A T written where a T? is wanted -- `int? n = 5;`, `f(3)` against an
    /// `int?` parameter, `return 7;` from a method returning `long?`. The
    /// binder is the only part that knows both halves of that sentence, so it
    /// marks the expression and the code generator allocates the cell.
    /// </summary>
    /// <summary>
    /// Types with static field initialisers, in the order they were declared.
    ///
    /// Each one's StaticInit$ runs the first time anything touches the type,
    /// once -- see Binder.StaticInitialisers. The list is what tells the code
    /// generator which types have such work at all, so it can test the flag at
    /// the places that trigger it.
    /// </summary>
    public List<string> StaticInits { get; } = new();

    /// <summary>
    /// The flag a type's generated static initialiser sets the first time it
    /// runs, so it runs once. Named here because the code generator looks it
    /// up to test it at the places that trigger initialisation.
    /// </summary>
    public const string ReadyField = "StaticReady$";

    /// <summary>
    /// Generic-method calls, and what their type arguments turned out to be.
    ///
    /// The checker is the only thing that can know: `list.Where(n => n.Ready)`
    /// never says Node anywhere. Each of these becomes a compiled copy, and the
    /// call is rewritten to name it -- see Program.Specialise.
    /// </summary>
    public List<(CallExpr Call, MethodDecl Template, List<TypeRef> Args)> Wanted { get; } = new();

    /// <summary>
    /// Expressions that are an ARRAY on their way to an interface an array
    /// implements.
    ///
    /// In C# a `T[]` is an IReadOnlyList&lt;T&gt;, an IList&lt;T&gt; and an
    /// IEnumerable&lt;T&gt;, and the runtime makes that true by giving the
    /// array's method table entries that point at helpers taking the array as
    /// their receiver. An array here is a length word and its elements -- no
    /// vtable to put entries in, and giving it one would change the shape of
    /// every array and every string on the machine.
    ///
    /// So the helper is an OBJECT: a generated class holding the array and
    /// implementing the interface, made where the conversion happens. Same
    /// answer as .NET's, reached the way this machine can reach it.
    /// </summary>
    public Dictionary<Expr, TypeSymbol> Views { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The generated array-view classes, whose bodies the code generator lays down.</summary>
    public List<TypeSymbol> ArrayViews { get; } = new();

    public HashSet<Expr> Boxes { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The class each lambda became, and what it captured.
    ///
    /// The code generator makes one of these where the lambda was written: a
    /// heap cell with the vtable at offset zero and the captured values stored
    /// into the fields after it.
    /// </summary>
    public Dictionary<LambdaExpr, ClosureInfo> Closures { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<Expr, Type> ExprType { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Expr, Sym> Resolved { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Expr, MethodSymbol> Calls { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>A delegate += or -=: the synthesised Combine or Remove call that replaces it, already bound.</summary>
    public Dictionary<AssignExpr, CallExpr> DelegateCompounds { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Which constructor a `: this(...)` or `: base(...)` runs, chosen as any
    /// other call is: by what the arguments ARE. By their number alone,
    /// `Defs(Function f) : this(new Cfg(f))` beside `Defs(Cfg cfg)` chose the
    /// first of the two, which was itself.
    /// </summary>
    public Dictionary<MethodDecl, MethodSymbol> Chained { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<MethodDecl, MethodSymbol> Methods { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<MethodDecl, int> FrameSize { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<LocalDecl, int> LocalSlot { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<LocalDecl, Type> LocalType { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The exact bound symbol made for a source local declaration.
    ///
    /// Frame slots are deliberately reused after a lexical scope closes, so a
    /// code-generation decision about one local must not be keyed by slot
    /// number alone.  Keeping the declaration-to-symbol identity closes that
    /// ambiguity without changing frame layout.
    /// </summary>
    public Dictionary<LocalDecl, LocalSym> LocalSymbols { get; }
        = new(ReferenceEqualityComparer.Instance);

    /// <summary>Declarations whose local a lambda captured, and which therefore
    /// need a heap cell rather than a frame slot.</summary>
    public HashSet<LocalDecl> BoxedLocals { get; } = new(ReferenceEqualityComparer.Instance);

    /// Where a declaration pattern's name lives: `x is T t` gives t a slot of
    /// its own, filled by the test when it succeeds. Its own map rather than a
    /// row in LocalSlot because that one is keyed by LocalDecl, and a pattern
    /// binding is not a declaration statement -- it is part of an expression.
    public Dictionary<IsExpr, int> PatternSlot { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Declaration patterns that open a Nullable&lt;T&gt; cell. Unlike an object
    /// type test this is a null check followed by a load of the held value.
    /// </summary>
    public HashSet<IsExpr> NullablePatterns { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<IsExpr> ValuePatterns { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Type tests -- `is`, `as`, and a switch arm's pattern -- whose tested type
    /// is `string`.
    ///
    /// String is a primitive here with no TypeSymbol behind it, so the lowering
    /// cannot find a class descriptor to compare against and used to refuse the
    /// test outright. It is recorded here, where the type is actually known,
    /// and the lowering answers it from the string bit in the object's
    /// descriptor instead.
    /// </summary>
    public HashSet<Node> StringTests { get; } = new(ReferenceEqualityComparer.Instance);

    /// Where a switch arm's type-pattern name lives. Per ARM rather than per
    /// switch: two arms may bind the same name at different types.
    /// <summary>
    /// THE TYPE A PATTERN TESTS FOR, resolved.
    ///
    /// The code generator had been looking the written name up in the flat
    /// table itself, which works only while every type's name is its key. With
    /// namespaces it is not: `x is NameExpr` inside Corsac.Lang means
    /// `Corsac.Lang.NameExpr`, and the generator reported that the type cannot
    /// be tested at run time -- eight hundred times, in a compiler whose own
    /// source is full of patterns. The checker resolved it properly; this is
    /// the checker writing down what it found.
    /// </summary>
    public Dictionary<Node, TypeSymbol> TestedTypes { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<SwitchArm, int> ArmSlot { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// THE LOCAL A PATTERN'S BINDING IS, beside the slot it was given.
    ///
    /// A binding a lambda captures lives in a heap CELL like any other
    /// captured local, and the code generator has to write it there rather
    /// than into the slot's register -- `owner.Canon is string canonical`
    /// followed by `t => t.Name == canonical` is the shape, and it is written
    /// six times in this compiler's own source.
    /// </summary>
    public Dictionary<Node, LocalSym> PatternSym { get; } = new(ReferenceEqualityComparer.Instance);

    /// Arms whose pattern needs a runtime type test. An arm matching a value
    /// type always matches -- it is there to give the subject a name for a
    /// guard to use -- and there is no header on a long to test.
    public HashSet<SwitchArm> ArmTests { get; } = new(ReferenceEqualityComparer.Instance);

    /// Where a switch expression keeps its subject while the arms are tried.
    public Dictionary<SwitchExpr, int> SwitchSlot { get; } = new(ReferenceEqualityComparer.Instance);

    /// The same, for a pattern whose subject had to be evaluated once: the
    /// alternatives all read it out of here.
    public Dictionary<PatternExpr, int> PatternSubject { get; } = new(ReferenceEqualityComparer.Instance);

    /// What each `Name = value` in an object initialiser actually writes: a
    /// field for a plain field or an auto-property, and a setter for a property
    /// with a body. One or the other, never both.
    public Dictionary<InitAssign, FieldSymbol> InitField { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<InitAssign, MethodSymbol> InitSetter { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The getter a `Name = { … }` reads the member through, when the member is
    /// a property with a body. A nested initialiser does not assign anything --
    /// it fills in what the member already holds -- so it wants the way IN, and
    /// a field's is <see cref="InitField"/> as usual.
    /// </summary>
    public Dictionary<InitAssign, MethodSymbol> InitGetter { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Expressions the checker replaced with others, for the code generator to
    /// emit instead: `n.ToString()` is `String.FromInt(n)`, and an enum's is a
    /// switch over its members.
    /// </summary>
    public Dictionary<Expr, Expr> Rewrites { get; } = new(ReferenceEqualityComparer.Instance);

    /// The Add each element of a collection initialiser calls.
    public Dictionary<InitAdd, MethodSymbol> InitAdder { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<NewExpr, MethodSymbol> NewConstructors { get; } = new(ReferenceEqualityComparer.Instance);

    /// The indexer each `[key] = value` in an initialiser calls.
    public Dictionary<InitIndex, MethodSymbol> InitIndexer { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The construction each tuple written out became: `(a, b)` is a `new` of
    /// the class the checker wrote for that shape, with its elements assigned
    /// as an object initialiser would assign them.
    /// </summary>
    public Dictionary<TupleExpr, NewExpr> Tuples { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What a `foreach` over something that is not an array became.
    ///
    /// C# defines foreach over a collection by REWRITING it -- to a call to
    /// GetEnumerator and a while over MoveNext -- and that is exactly what this
    /// holds: ordinary statements, bound and emitted like any others. Doing it
    /// this way rather than in the code generator means an enumerator reached
    /// through an interface dispatches the same way every other interface call
    /// does, because it IS every other interface call.
    /// </summary>
    public Dictionary<Stmt, Stmt> Lowered { get; } = new(ReferenceEqualityComparer.Instance);

    /// Which accessor an `x[i]` on a user type calls. Reading records get_Item
    /// here; assigning records set_Item, because only the assignment path knows
    /// that is what it is.
    public Dictionary<IndexExpr, MethodSymbol> Indexers { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<IndexExpr, MethodSymbol> IndexSetters { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Expr, MethodSymbol> PropertySetters { get; } = new(ReferenceEqualityComparer.Instance);

    /// What each `sizeof(T)` came to. Worked out where the type can be resolved
    /// and the instantiation is known, not left for the code generator.
    public Dictionary<SizeOfExpr, int> SizeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    public int SizeOfType(SizeOfExpr e) => SizeOfs.TryGetValue(e, out int n) ? n : 8;

    /// <summary>Which type each typeof(T) named, once resolved.</summary>
    public Dictionary<TypeOfExpr, TypeSymbol> TypeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The calls that are GetType(), which is not a declared method.</summary>
    public HashSet<CallExpr> GetTypes { get; } = new(ReferenceEqualityComparer.Instance);

    /// Calls of a VALUE rather than of a named method: `f(x)` where f holds
    /// something with an Invoke. The method recorded here is that Invoke.
    public Dictionary<CallExpr, MethodSymbol> Invocations { get; } = new(ReferenceEqualityComparer.Instance);

    /// Member accesses whose RECEIVER is really the first argument: `s.Trim()`
    /// calling the static `String.Trim(s)`. A primitive has no vtable to hang an
    /// instance method on, so this is how a string gets methods at all.
    public Dictionary<MemberExpr, bool> Receivers { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<ForeachStmt, int> ForeachSlot { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Where a switch keeps its subject while the labels are being tried.
    ///
    /// One slot for the whole statement, because the labels are conditions over
    /// a SubjectExpr rather than over the subject expression -- so a subject
    /// that does work is done once.
    ///
    /// This replaces CaseSlot, CaseField and CaseGetter, which were how a
    /// second, smaller pattern engine remembered what a case label had been
    /// resolved to. Labels are ordinary expressions now and the ordinary tables
    /// hold everything about them.
    /// </summary>
    public Dictionary<SwitchStmt, int> SwitchSubject { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<GotoCaseStmt, SwitchCase> GotoCases { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<AssignExpr> DiscardAssignments { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<CallExpr, FieldSymbol> CapturedReceivers { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<CallExpr> EnumHasFlags { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The calls to System.Enum's statics that read an enum's name table:
    /// which enum, and which of them. The code generator emits each over the
    /// table it writes for that enum (Lowering.Enum.cs); the two that are
    /// simply a list of the members become that list here instead.
    /// </summary>
    public Dictionary<CallExpr, (TypeSymbol Enum, string Name)> EnumStatics { get; }
        = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What each catch clause caught, and where its variable lives.
    ///
    /// The type is resolved here rather than in the generator because the
    /// generator has no name lookup: by then a clause is a type test against a
    /// bit, and which bit is a question only the binder can answer.
    /// </summary>
    public Dictionary<CatchClause, TypeSymbol> CatchType { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<CatchClause, int> CatchSlot { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Named numbers, by the type that declared them. A const is not storage:
    /// it never reaches the generator as a field, and every use of one is the
    /// value written out where the name was.
    /// </summary>
    public Dictionary<(TypeSymbol Owner, string Name), long> Constants { get; } = new();

    /// <summary>
    /// What each const was DECLARED as.
    ///
    /// Every one of them used to answer `long`, which is wrong in the ordinary
    /// C# way: `(int)op & (OpSpace - 1)` where OpSpace is a `const int` is an
    /// int, and calling it a long made an int variable refuse its own
    /// initialiser. The value is the same sixty-four bits either way; the type
    /// is what the arithmetic around it is decided by.
    /// </summary>
    public Dictionary<(TypeSymbol Owner, string Name), Type> ConstantTypes { get; } = new();

    /// <summary>
    /// Consts that name TEXT rather than a number: `const string Tuple =
    /// "ValueTuple";`, which is how this compiler's own Ast.cs states the name
    /// it gives a tuple's class.
    ///
    /// Kept apart from the numbers because the value is a string literal, and
    /// every use of one is that literal written out where the name was.
    /// </summary>
    public Dictionary<(TypeSymbol Owner, string Name), string> TextConstants { get; } = new();

    /// <summary>Which method each Sys.AddressOf names. Not a call of it -- the address of it.</summary>
    public Dictionary<CallExpr, MethodSymbol> AddressOf { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// How each `await` is carried out: the awaiter pattern's four members,
    /// found on the operand's type. The lowering calls exactly these.
    /// </summary>
    public Dictionary<AwaitExpr, AwaitInfo> Awaits { get; } = new(ReferenceEqualityComparer.Instance);
    public List<CompileError> Errors { get; } = new();
    public List<CompileError> Warnings { get; } = new();
    public Dictionary<string, TypeSymbol> Types { get; } = new(StringComparer.Ordinal);
    /// <summary>Bytes of static storage, including the reserved low words.</summary>
    public int StaticBytes { get; set; } = 16;

    public Type TypeOf(Expr e) => ExprType.TryGetValue(e, out Type? t) ? t : Type.Error;

    /// <summary>
    /// Drop the completed analysis after specialization has consumed it.
    /// Only the final binding is needed by lowering. Clear references even
    /// if a conservative stack root temporarily retains this result object.
    /// Does not mutate the AST or the symbols that the next round will read.
    /// </summary>
    public void ReleaseForRebind()
    {
        StaticInits.Clear();
        Wanted.Clear();
        Views.Clear();
        ArrayViews.Clear();
        Boxes.Clear();
        Closures.Clear();
        ExprType.Clear();
        Resolved.Clear();
        Calls.Clear();
        Chained.Clear();
        Methods.Clear();
        FrameSize.Clear();
        LocalSlot.Clear();
        LocalType.Clear();
        LocalSymbols.Clear();
        BoxedLocals.Clear();
        PatternSlot.Clear();
        NullablePatterns.Clear();
        ValuePatterns.Clear();
        StringTests.Clear();
        TestedTypes.Clear();
        ArmSlot.Clear();
        PatternSym.Clear();
        ArmTests.Clear();
        SwitchSlot.Clear();
        PatternSubject.Clear();
        InitField.Clear();
        InitSetter.Clear();
        InitGetter.Clear();
        Rewrites.Clear();
        InitAdder.Clear();
        NewConstructors.Clear();
        InitIndexer.Clear();
        Tuples.Clear();
        Lowered.Clear();
        Indexers.Clear();
        IndexSetters.Clear();
        PropertySetters.Clear();
        SizeOfs.Clear();
        TypeOfs.Clear();
        GetTypes.Clear();
        Invocations.Clear();
        Receivers.Clear();
        ForeachSlot.Clear();
        SwitchSubject.Clear();
        GotoCases.Clear();
        DiscardAssignments.Clear();
        CapturedReceivers.Clear();
        EnumHasFlags.Clear();
        EnumStatics.Clear();
        CatchType.Clear();
        CatchSlot.Clear();
        Constants.Clear();
        ConstantTypes.Clear();
        TextConstants.Clear();
        AddressOf.Clear();
        Awaits.Clear();
        Errors.Clear();
        Warnings.Clear();
        Types.Clear();
    }

    /// <summary>
    /// Separate mutable result containers from declaration-time containers.
    /// Symbols and AST nodes retain identity; this is not a deep symbol freeze
    /// and does not by itself make body checking safe to run concurrently.
    /// </summary>
    public BindResult CopyForBodyChecking()
    {
        BindResult copy = new()
        {
            MaskWords = MaskWords, ToStringSlot = ToStringSlot,
            EqualsSlot = EqualsSlot, HashSlot = HashSlot,
            CompareSlot = CompareSlot, StaticBytes = StaticBytes,
        };
        copy.StaticInits.AddRange(StaticInits);
        copy.Wanted.AddRange(Wanted);
        CopyEntries(Views, copy.Views);
        copy.ArrayViews.AddRange(ArrayViews);
        foreach (var item in Boxes) copy.Boxes.Add(item);
        CopyEntries(Closures, copy.Closures);
        CopyEntries(ExprType, copy.ExprType);
        CopyEntries(Resolved, copy.Resolved);
        CopyEntries(Calls, copy.Calls);
        CopyEntries(Chained, copy.Chained);
        CopyEntries(Methods, copy.Methods);
        CopyEntries(FrameSize, copy.FrameSize);
        CopyEntries(LocalSlot, copy.LocalSlot);
        CopyEntries(LocalType, copy.LocalType);
        CopyEntries(LocalSymbols, copy.LocalSymbols);
        foreach (var item in BoxedLocals) copy.BoxedLocals.Add(item);
        CopyEntries(PatternSlot, copy.PatternSlot);
        foreach (var item in NullablePatterns) copy.NullablePatterns.Add(item);
        foreach (var item in ValuePatterns) copy.ValuePatterns.Add(item);
        foreach (var item in StringTests) copy.StringTests.Add(item);
        CopyEntries(TestedTypes, copy.TestedTypes);
        CopyEntries(ArmSlot, copy.ArmSlot);
        CopyEntries(PatternSym, copy.PatternSym);
        foreach (var item in ArmTests) copy.ArmTests.Add(item);
        CopyEntries(SwitchSlot, copy.SwitchSlot);
        CopyEntries(PatternSubject, copy.PatternSubject);
        CopyEntries(InitField, copy.InitField);
        CopyEntries(InitSetter, copy.InitSetter);
        CopyEntries(InitGetter, copy.InitGetter);
        CopyEntries(Rewrites, copy.Rewrites);
        CopyEntries(InitAdder, copy.InitAdder);
        CopyEntries(NewConstructors, copy.NewConstructors);
        CopyEntries(InitIndexer, copy.InitIndexer);
        CopyEntries(Tuples, copy.Tuples);
        CopyEntries(Lowered, copy.Lowered);
        CopyEntries(Indexers, copy.Indexers);
        CopyEntries(IndexSetters, copy.IndexSetters);
        CopyEntries(PropertySetters, copy.PropertySetters);
        CopyEntries(SizeOfs, copy.SizeOfs);
        CopyEntries(TypeOfs, copy.TypeOfs);
        foreach (var item in GetTypes) copy.GetTypes.Add(item);
        CopyEntries(Invocations, copy.Invocations);
        CopyEntries(Receivers, copy.Receivers);
        CopyEntries(ForeachSlot, copy.ForeachSlot);
        CopyEntries(SwitchSubject, copy.SwitchSubject);
        CopyEntries(GotoCases, copy.GotoCases);
        foreach (var item in DiscardAssignments) copy.DiscardAssignments.Add(item);
        CopyEntries(CapturedReceivers, copy.CapturedReceivers);
        foreach (var item in EnumHasFlags) copy.EnumHasFlags.Add(item);
        CopyEntries(EnumStatics, copy.EnumStatics);
        CopyEntries(CatchType, copy.CatchType);
        CopyEntries(CatchSlot, copy.CatchSlot);
        CopyEntries(Constants, copy.Constants);
        CopyEntries(ConstantTypes, copy.ConstantTypes);
        CopyEntries(TextConstants, copy.TextConstants);
        CopyEntries(AddressOf, copy.AddressOf);
        CopyEntries(Awaits, copy.Awaits);
        copy.Errors.AddRange(Errors);
        copy.Warnings.AddRange(Warnings);
        CopyEntries(Types, copy.Types);
        return copy;
    }

    private static void CopyEntries<K, V>(Dictionary<K, V> source, Dictionary<K, V> destination)
        where K : notnull
    {
        foreach (KeyValuePair<K, V> entry in source)
            destination.Add(entry.Key, entry.Value);
    }
}
