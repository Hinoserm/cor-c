#nullable enable
namespace Corsac.Lang;

public sealed partial class BindResult
{
    /// <summary>
    /// For a unit compiled on its own (Binder.CollectInterruptFacts): what the
    /// link needs to check its interrupt handlers' calls into other units, a
    /// fact for every method it binds (Lto.InterruptNotes), already in the
    /// section's form (InterruptNotes.Encode). Null otherwise, and when it
    /// binds none.
    /// </summary>
    public byte[]? InterruptFacts { get; set; }

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
    /// Every interface family's slots, by template, arity and member: what a
    /// box's table fills for an interface no specialisation of which this
    /// unit made -- IEquatable&lt;int&gt;'s Equals on a boxed int -- since the
    /// family's slot is the same in every unit whatever it instantiates.
    /// </summary>
    public Dictionary<(string Template, int Arity, int Member), int> InterfaceFamilySlots { get; } = new();

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
    /// The bodies a want was found in, with the type whose body each is. A
    /// call's overload can change once the copy it asked for exists -- the
    /// copy's return type is a specialisation the first look could not see
    /// -- so these are checked again in the next round (Frontend).
    /// </summary>
    public List<(TypeSymbol? Owner, MethodDecl Body)> Wanting { get; } = new();

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

    /// <summary>The Nullable each expression in Boxes is made, when it is not its own type's: an int into a ulong?.</summary>
    public Dictionary<Expr, Type> BoxedAs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The class each lambda became, and what it captured.
    ///
    /// The code generator makes one of these where the lambda was written: a
    /// heap cell with the vtable at offset zero and the captured values stored
    /// into the fields after it.
    /// </summary>
    public Dictionary<LambdaExpr, ClosureInfo> Closures { get; } = new(ReferenceEqualityComparer.Instance);

    public ExprTypes ExprType { get; } = new();
    public ExprSyms Resolved { get; } = new();
    /// <summary>
    /// Which binding the facts kept on the nodes belong to (NodeBinding), for
    /// Calls, Invocations, Rewrites, NewConstructors, Lowered, Indexers,
    /// IndexSetters, PropertySetters and Receivers.
    /// </summary>
    private readonly NodeBinding _binding = new();

    public CallTargets Calls { get; }
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

    /// <summary>
    /// The declaration a local symbol came from: LocalSymbols read backwards.
    /// Found by walking all of LocalSymbols, it was a walk of every local of
    /// the unit for each one asked about. The reverse map is built from the
    /// same walk, so the first declaration found is still the one answered,
    /// and built again only when a symbol is missing from it and locals have
    /// been added since.
    /// </summary>
    public LocalDecl? DeclOf(LocalSym local)
    {
        if (_declBySym is not null && _declBySym.TryGetValue(local, out LocalDecl? known)) return known;
        if (_declBySym is not null && _declBySymCount == LocalSymbols.Count) return null;
        _declBySym = new Dictionary<LocalSym, LocalDecl>(LocalSymbols.Count, new SameLocal());
        foreach ((LocalDecl decl, LocalSym sym) in LocalSymbols) _declBySym.TryAdd(sym, decl);
        _declBySymCount = LocalSymbols.Count;
        return _declBySym.TryGetValue(local, out LocalDecl? found) ? found : null;
    }

    private Dictionary<LocalSym, LocalDecl>? _declBySym;
    private int _declBySymCount = -1;

    /// <summary>A local symbol is itself and no other, whatever its record fields say.</summary>
    private sealed class SameLocal : IEqualityComparer<LocalSym>
    {
        public bool Equals(LocalSym? a, LocalSym? b) => ReferenceEquals(a, b);
        public int GetHashCode(LocalSym local) => local.Slot * 31 + local.Name.Length;
    }

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
    /// `x is object` and `x is object y` over a reference: true exactly when x
    /// is not null, as C# has it -- every object is an object, and null none.
    /// </summary>
    public HashSet<IsExpr> NonNullPatterns { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>`v is object` and `v is object y` over a value that is no nullable: always true, the value boxed.</summary>
    public HashSet<IsExpr> BoxPatterns { get; } = new(ReferenceEqualityComparer.Instance);

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

    /// <summary>
    /// An ARRAY TYPE a pattern, `as` or switch arm tests for: `o is byte[] b`.
    /// An array has no symbol for TestedTypes to hold -- what identifies it at
    /// run time is the descriptor its element type and stride share -- so
    /// the whole bound type is kept.
    /// </summary>
    public Dictionary<Node, Type> TestedArrays { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A TEST OR CAST TO A GENERIC INTERFACE OVER A SHARED METHOD COPY'S OWN
    /// TYPE PARAMETERS (ICanonShape): the interface's family and its
    /// arguments as resolved, a CanonParam of -2 - k standing for hidden
    /// argument k. Asked of the object at run time (Runtime.ShapedAs).
    /// </summary>
    public Dictionary<Node, CanonShape> Shapes { get; } = new(ReferenceEqualityComparer.Instance);

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
    public ExprRewrites Rewrites { get; }

    /// <summary>The calls Rewrites made of user-defined conversions: their
    /// value is the operator's result, not the expression they replace.</summary>
    public HashSet<Expr> UserConversions { get; } = new(ReferenceEqualityComparer.Instance);

    /// The Add each element of a collection initialiser calls.
    public Dictionary<InitAdd, MethodSymbol> InitAdder { get; } = new(ReferenceEqualityComparer.Instance);
    public NewTargets NewConstructors { get; }

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
    public LoweredStmts Lowered { get; }

    /// Which accessor an `x[i]` on a user type calls. Reading records get_Item
    /// here; assigning records set_Item, because only the assignment path knows
    /// that is what it is.
    public IndexTargets Indexers { get; }

    public IndexTargets IndexSetters { get; }
    public ExprMethods PropertySetters { get; }

    /// What each `sizeof(T)` came to. Worked out where the type can be resolved
    /// and the instantiation is known, not left for the code generator.
    public Dictionary<SizeOfExpr, int> SizeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    public int SizeOfType(SizeOfExpr e) => SizeOfs.TryGetValue(e, out int n) ? n : 8;

    /// <summary>Which type each typeof(T) named, once resolved.</summary>
    public Dictionary<TypeOfExpr, TypeSymbol> TypeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// `typeof` of a type that has no declaration to point at: int, bool,
    /// string and the rest. C# has always allowed it -- `typeof(int)` is
    /// ordinary code -- and a generic that asks what T is cannot be written
    /// without it.
    /// </summary>
    public Dictionary<TypeOfExpr, Prim> PrimitiveTypeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>`typeof` of an array. Every array of one element type shares
    /// one descriptor, so `typeof(byte[]) == typeof(byte[])` holds.</summary>
    public Dictionary<TypeOfExpr, Type> ArrayTypeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>`typeof(T)` of a type argument only run time knows (Type.CanonParam): its descriptor as run time finds it (Lowering.RunTimeDescriptor).</summary>
    public Dictionary<TypeOfExpr, Type> RunTimeTypeOfs { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The calls that are GetType(), which is not a declared method.</summary>
    public HashSet<CallExpr> GetTypes { get; } = new(ReferenceEqualityComparer.Instance);

    /// Calls of a VALUE rather than of a named method: `f(x)` where f holds
    /// something with an Invoke. The method recorded here is that Invoke.
    public ExprMethods Invocations { get; }

    /// <summary>
    /// Calls of a GENERIC VIRTUAL METHOD (MethodSymbol.GenericVirtual) and the
    /// copies they dispatch among: for each class that overrides or implements
    /// the method, deepest in the hierarchy first, the copy of its override at
    /// the call's type arguments. The code generator tests the receiver
    /// against each class in turn and calls the first that matches directly;
    /// `Fallback` is the copy on the method's own class, or null when that one
    /// is abstract and the receiver must have matched a class above it.
    /// </summary>
    public Dictionary<CallExpr, GenericDispatch> GenericDispatches { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Copies of generic methods that no call names but a dispatch needs: the
    /// overrides a generic virtual call may land on (GenericDispatches). Made
    /// beside their templates the way Wanted's are, without renaming a call.
    /// </summary>
    public List<(TypeDecl Owner, MethodDecl Template, List<TypeRef> Args, string Name)> WantedOverrides { get; } = new();

    /// <summary>
    /// A type the checker spelt into the source that no expansion has made
    /// yet -- a lambda's natural type, `Func<int, int>` for `var f = (int x)
    /// => x + 1;` where nothing else names it (LocalDecl.NaturalType). The
    /// unit goes round once more so the monomorphiser makes it, as it would
    /// for a copy that named it.
    /// </summary>
    public bool Reexpand { get; set; }

    /// <summary>
    /// The shapes of the delegates C# synthesises that a natural type here
    /// needs and the unit does not declare yet (Parser.AnonymousDelegates):
    /// the driver adds them, and the round goes again.
    /// </summary>
    public HashSet<string> AnonymousDelegates { get; } = new(StringComparer.Ordinal);

    /// <summary>`&Method`: the static method whose address this is, as a function pointer.</summary>
    public Dictionary<UnaryExpr, MethodSymbol> MethodAddresses { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>A call through a function pointer: the pointer's signature.</summary>
    public Dictionary<CallExpr, FunctionPointer> PointerCalls { get; } = new(ReferenceEqualityComparer.Instance);

    /// Member accesses whose RECEIVER is really the first argument: `s.Trim()`
    /// calling the static `String.Trim(s)`. A primitive has no vtable to hang an
    /// instance method on, so this is how a string gets methods at all.
    public ReceiverFlags Receivers { get; }
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
    /// <summary>Each `goto name;` and the labelled statement it reaches.</summary>
    public Dictionary<GotoStmt, LabeledStmt> Gotos { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<AssignExpr> DiscardAssignments { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<CallExpr, FieldSymbol> CapturedReceivers { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// Each call of a local function, by name, and the function's declaration:
    /// the delegate a local function is never reassigned, so the call runs its
    /// own lambda's Invoke, and lowering calls that directly (no slot read).
    /// </summary>
    public Dictionary<CallExpr, LocalDecl> LocalFunctionCalls { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Each method whose call-only local functions share one block of its frame (Binder.ArrangeLocalFunctionEnvironment).</summary>
    public Dictionary<MethodDecl, LocalEnvironment> Environments { get; } = new(ReferenceEqualityComparer.Instance);
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
    public TypeTable Types { get; } = new();

    /// <summary>
    /// NULLABLE&lt;T&gt; AS .NET LAYS IT OUT, one shape per T: hasValue, a bool
    /// at 0, then the value at T's alignment -- a struct like any other,
    /// held in line wherever it is held, and empty when it is all zero. Made
    /// on first asking, by the binder laying out a field or by the code
    /// generator making a value, and the same shape for both.
    /// </summary>
    public Dictionary<string, TypeSymbol> NullableShapes { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>The struct a value of this type is: a nullable value's shape, or the struct itself.</summary>
    public TypeSymbol StructOf(Type t) => t.IsNullableValue ? NullableShape(t.Underlying) : t.Symbol!;

    /// <summary>The Nullable&lt;T&gt; shape for T, which must already be laid out if it is a struct.</summary>
    public TypeSymbol NullableShape(Type underlying)
    {
        string key = underlying.Symbol?.Key ?? underlying.Prim.ToString();
        lock (NullableShapes)
        {
            if (NullableShapes.TryGetValue(key, out TypeSymbol? made)) return made;
            TypeSymbol shape = new() { Name = "Nullable$" + key, Kind = TypeKind.Struct, Structural = true };
            int size, align;
            bool inline = false;
            if (underlying.Symbol is { Kind: TypeKind.Struct } held)
            {
                inline = held.HeldInline;
                size = inline ? Math.Max(1, held.InstanceSize) : Target.Current.WordSize;
                align = inline ? Math.Max(1, held.InlineAlign) : Target.Current.WordSize;
            }
            else
            {
                size = Math.Max(1, underlying.Size);
                align = Math.Min(8, size);
            }
            shape.WritableFields.Add(new FieldSymbol { Name = "hasValue", Type = Type.Bool, Owner = shape, Offset = 0 });
            shape.WritableFields.Add(new FieldSymbol { Name = "value", Type = underlying.AsNonNullable(), Owner = shape, Offset = align, Inline = inline });
            shape.InstanceSize = (align + size + align - 1) / align * align;
            shape.InlineAlign = Math.Max(1, align);
            shape.HeldInline = underlying.Symbol is not { Kind: TypeKind.Struct } || inline;
            shape.InlineDecided = true;
            shape.SlotsAssigned = true;
            NullableShapes[key] = shape;
            return shape;
        }
    }
    /// <summary>Bytes of static storage, including the reserved low words.</summary>
    public int StaticBytes { get; set; } = 16;

    public BindResult()
    {
        Calls = new(_binding);
        Invocations = new(_binding, invokes: true);
        Rewrites = new(_binding);
        NewConstructors = new(_binding);
        Lowered = new(_binding);
        Indexers = new(_binding, setters: false);
        IndexSetters = new(_binding, setters: true);
        PropertySetters = new(_binding, invokes: false);
        Receivers = new(_binding);
    }

    public Type TypeOf(Expr e) => ExprType.TryGetValue(e, out Type? t) ? t : Type.Error;

    /// <summary>
    /// A NODE NOTHING WILL ASK ABOUT AGAIN, let go of: its rows in every
    /// table keyed by node (Lowering.ReleaseBody, once its method is
    /// lowered). The facts kept on the nodes themselves -- types, symbols,
    /// calls, rewrites -- go with the nodes; a row here held its node, and so
    /// the subtree under it, to the end of lowering whatever became of the
    /// method: every local declaration with its initialiser, every boxing,
    /// pattern and tuple of the unit. Asking about a node after this answers
    /// as for a node never bound, so only a node no one will ask about may
    /// be forgotten.
    /// </summary>
    public void Forget(Node n)
    {
        switch (n)
        {
            case LocalDecl d:
                LocalSlot.Remove(d);
                LocalType.Remove(d);
                BoxedLocals.Remove(d);
                // The reverse map (DeclOf) loses the row too. In step with the
                // table before, it stays in step; otherwise it is made again
                // at the next miss -- a count that only matched because rows
                // went as others came would have said nothing was added.
                bool inStep = _declBySym is not null && _declBySymCount == LocalSymbols.Count;
                if (LocalSymbols.Remove(d, out LocalSym? local))
                {
                    if (_declBySym is not null && _declBySym.TryGetValue(local, out LocalDecl? mapped) && ReferenceEquals(mapped, d))
                        _declBySym.Remove(local);
                    _declBySymCount = inStep ? LocalSymbols.Count : -1;
                }
                break;
            case IsExpr i:
                PatternSlot.Remove(i);
                NullablePatterns.Remove(i);
                ValuePatterns.Remove(i);
                NonNullPatterns.Remove(i);
                BoxPatterns.Remove(i);
                break;
            case SwitchArm arm:
                ArmSlot.Remove(arm);
                ArmTests.Remove(arm);
                break;
            case InitAssign init:
                InitField.Remove(init);
                InitSetter.Remove(init);
                InitGetter.Remove(init);
                break;
            case InitAdd add:
                InitAdder.Remove(add);
                break;
            case InitIndex index:
                InitIndexer.Remove(index);
                break;
            case ForeachStmt each:
                ForeachSlot.Remove(each);
                break;
            case SwitchStmt sw:
                SwitchSubject.Remove(sw);
                break;
            case GotoCaseStmt gc:
                GotoCases.Remove(gc);
                break;
            case GotoStmt go:
                Gotos.Remove(go);
                break;
            case CatchClause cc:
                CatchType.Remove(cc);
                CatchSlot.Remove(cc);
                break;
        }
        if (n is Expr e)
        {
            Views.Remove(e);
            Boxes.Remove(e);
            BoxedAs.Remove(e);
            UserConversions.Remove(e);
            switch (e)
            {
                case AssignExpr a:
                    DelegateCompounds.Remove(a);
                    DiscardAssignments.Remove(a);
                    break;
                case SwitchExpr sx:
                    SwitchSlot.Remove(sx);
                    break;
                case PatternExpr p:
                    PatternSubject.Remove(p);
                    break;
                case TupleExpr tu:
                    Tuples.Remove(tu);
                    break;
                case SizeOfExpr so:
                    SizeOfs.Remove(so);
                    break;
                case TypeOfExpr to:
                    TypeOfs.Remove(to);
                    PrimitiveTypeOfs.Remove(to);
                    ArrayTypeOfs.Remove(to);
                    RunTimeTypeOfs.Remove(to);
                    break;
                case CallExpr c:
                    GetTypes.Remove(c);
                    GenericDispatches.Remove(c);
                    PointerCalls.Remove(c);
                    CapturedReceivers.Remove(c);
                    LocalFunctionCalls.Remove(c);
                    EnumHasFlags.Remove(c);
                    EnumStatics.Remove(c);
                    AddressOf.Remove(c);
                    break;
                case UnaryExpr u:
                    MethodAddresses.Remove(u);
                    break;
            }
        }
        StringTests.Remove(n);
        TestedTypes.Remove(n);
        TestedArrays.Remove(n);
        Shapes.Remove(n);
        PatternSym.Remove(n);
    }

    /// <summary>
    /// Drop the completed analysis after specialization has consumed it.
    /// Only the final binding is needed by lowering. Clear references even
    /// if a conservative stack root temporarily retains this result object.
    /// Does not mutate the AST or the symbols that the next round will read.
    /// </summary>
    public void ReleaseForRebind()
    {
        // Calls, Rewrites and the rest kept on the nodes, all at once.
        _binding.Next();
        StaticInits.Clear();
        Wanted.Clear();
        Wanting.Clear();
        Views.Clear();
        ArrayViews.Clear();
        Boxes.Clear();
        Closures.Clear();
        ExprType.Clear();
        Resolved.Clear();
        Chained.Clear();
        Methods.Clear();
        FrameSize.Clear();
        LocalSlot.Clear();
        LocalType.Clear();
        LocalSymbols.Clear();
        _declBySym = null;
        BoxedLocals.Clear();
        PatternSlot.Clear();
        NullablePatterns.Clear();
        ValuePatterns.Clear();
        NonNullPatterns.Clear();
        BoxPatterns.Clear();
        StringTests.Clear();
        TestedTypes.Clear();
        Shapes.Clear();
        TestedArrays.Clear();
        ArmSlot.Clear();
        PatternSym.Clear();
        ArmTests.Clear();
        SwitchSlot.Clear();
        PatternSubject.Clear();
        InitField.Clear();
        InitSetter.Clear();
        InitGetter.Clear();
        UserConversions.Clear();
        InitAdder.Clear();
        InitIndexer.Clear();
        Tuples.Clear();
        SizeOfs.Clear();
        TypeOfs.Clear();
        PrimitiveTypeOfs.Clear();
        ArrayTypeOfs.Clear();
        RunTimeTypeOfs.Clear();
        GetTypes.Clear();
        GenericDispatches.Clear();
        WantedOverrides.Clear();
        ForeachSlot.Clear();
        SwitchSubject.Clear();
        GotoCases.Clear();
        Gotos.Clear();
        DiscardAssignments.Clear();
        CapturedReceivers.Clear();
        LocalFunctionCalls.Clear();
        Environments.Clear();
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
}

/// <summary>
/// A static array of constants (FieldDecl.StaticData): its element type,
/// by keyword, and its values -- whole numbers (bool, char and the integers,
/// as their bits), doubles (float and double), or strings, null for a null
/// element.
/// </summary>
public sealed class StaticArray
{
    public required string Element { get; init; }
    public List<long> Integers { get; } = new();
    public List<double> Reals { get; } = new();
    public List<string?> Strings { get; } = new();
    public int Count => Element == "string" ? Strings.Count : Element is "float" or "double" ? Reals.Count : Integers.Count;
}

/// <summary>
/// A generic interface tested or cast to in a shared method copy, over one
/// or more of the copy's own type parameters (BindResult.Shapes): the
/// interface as the copy names it -- over object, the static answer -- and
/// its type arguments, each resolved, the copy's hidden argument k where its
/// CanonParam is -2 - k.
/// </summary>
public sealed class CanonShape
{
    public required TypeSymbol Interface { get; init; }
    public required List<Type> Args { get; init; }

    /// <summary>The most type arguments an interface's identity record holds and Runtime.ShapedAs is given.</summary>
    public const int MostArguments = 4;

    /// <summary>
    /// WHICH GENERIC INTERFACE A SPECIALISED ONE IS MADE FROM, as a number
    /// every unit computes alike: the template's path and arity, hashed
    /// (FNV-1a) to the word. IList of string and IList of Node share it, and
    /// nothing else does but by a collision a matching argument list would
    /// also have to share. Null for an interface that was not made from a
    /// template.
    /// </summary>
    public static long? FamilyOf(TypeSymbol face)
    {
        if (face.Decl is not { Specialised: true, Template: string template } decl || decl.TemplateArgs.Count == 0)
        {
            return null;
        }
        string key = template + "`" + decl.TemplateArgs.Count;
        ulong hash = 14695981039346656037UL;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        // Folded to 31 bits, a positive number every target's word holds and
        // every instruction takes as an immediate; a collision would also
        // need the arguments to match to answer wrongly.
        long family = (long)((hash ^ (hash >> 32)) & 0x7fffffffUL);
        // Zero is no family (a record's absence), so never a family's number.
        return family == 0 ? 1 : family;
    }
}

/// <summary>Where a generic virtual call may land. See BindResult.GenericDispatches.</summary>
public sealed class GenericDispatch
{
    /// <summary>The method as the call names it, for the exception when nothing matches.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// Deepest class first, so the first class the receiver is decides: an
    /// object of a class below two overriders runs the nearer one.
    /// </summary>
    public required List<(TypeSymbol Class, MethodSymbol Copy)> Targets { get; init; }
    public MethodSymbol? Fallback { get; init; }

    /// <summary>
    /// The signature at the call's type arguments: what the arguments are
    /// converted to and what comes back. The call itself is bound to the
    /// template, whose parameters are still T.
    /// </summary>
    public required List<ParamSymbol> Params { get; init; }
    public required Type Returns { get; init; }
}

/// <summary>
/// THE TYPES BY KEY, and by where they are and what they are called: a type
/// `A.B.C` is found as ("A.B", "C") too, so the binder's walk outwards through
/// namespaces (FindType) asks each level without spelling `at + "." + name`
/// -- a string made and thrown away at every level of every name the binder
/// resolved, 2% of all the compiler allocated. Kept in step by the writes the
/// binder makes; read as the dictionary it is everywhere else.
/// </summary>
public sealed class TypeTable : Dictionary<string, TypeSymbol>
{
    private readonly Dictionary<(string Within, string Name), TypeSymbol> _split = new();

    public TypeTable() : base(StringComparer.Ordinal) { }

    // How many split entries the index holds, and the table's count, as of
    // the last write it saw.
    private int _indexed, _counted;

    private void Reindex()
    {
        _split.Clear();
        foreach (var (key, value) in this)
            if (Split(key) is { } at) _split[at] = value;
        _indexed = _split.Count;
        _counted = Count;
    }

    private void Seen() { _indexed = _split.Count; _counted = Count; }

    private static (string, string)? Split(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot <= 0 || dot == key.Length - 1 ? null : (key[..dot], key[(dot + 1)..]);
    }

    public new TypeSymbol this[string key]
    {
        get => base[key];
        set
        {
            bool fresh = _split.Count == _indexed && Count == _counted;
            base[key] = value;
            if (Split(key) is { } at) _split[at] = value;
            if (fresh) Seen();
        }
    }

    public new void Add(string key, TypeSymbol value)
    {
        bool fresh = _split.Count == _indexed && Count == _counted;
        base.Add(key, value);
        if (Split(key) is { } at) _split[at] = value;
        if (fresh) Seen();
    }

    public new bool Remove(string key)
    {
        bool fresh = _split.Count == _indexed && Count == _counted;
        if (!base.Remove(key)) return false;
        if (Split(key) is { } at) _split.Remove(at);
        if (fresh) Seen();
        return true;
    }

    /// <summary>The type called `name` directly within `within` (no dot in `name`).</summary>
    public bool TryGetWithin(string within, string name, out TypeSymbol? symbol)
    {
        // A write that came some other way -- through the dictionary itself --
        // shows in the count: the index is made again from what is there.
        if (_split.Count != _indexed || Count != _counted) Reindex();
        if (_split.TryGetValue((within, name), out TypeSymbol? found)) { symbol = found; return true; }
        symbol = null;
        return false;
    }
}

/// <summary>
/// EACH EXPRESSION'S TYPE, kept on the expression. A dictionary of every
/// expression a unit binds grew by doubling to tables of megabytes, each
/// rehash holding the old table and the new at once: on a 256 MB machine the
/// two-megabyte one a large unit asked for found no hole in a heap of free
/// pieces, and the compile ran out of memory with a third of the heap free.
///
/// The same trees are bound again in a later round (Frontend's rebinds), so a
/// type is read back only by the binding that wrote it: each table is a
/// generation, and Clear starts a new one, leaving nothing behind to read.
/// </summary>
public sealed class ExprTypes
{
    private static int _generations;
    private int _generation = Interlocked.Increment(ref _generations);

    public Type this[Expr e]
    {
        get => TryGetValue(e, out Type? t) ? t : throw new KeyNotFoundException("an expression this binding has not typed");
        set { e.BoundType = value; e.BoundBy = _generation; }
    }

    public bool TryGetValue(Expr e, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Type type)
    {
        if (e.BoundBy == _generation && e.BoundType is { } bound) { type = bound; return true; }
        type = null;
        return false;
    }

    public bool ContainsKey(Expr e) => e.BoundBy == _generation && e.BoundType is not null;

    public void Clear() => _generation = Interlocked.Increment(ref _generations);
}

/// <summary>What each name or member access resolved to, kept on the expression as ExprTypes keeps its type.</summary>
public sealed class ExprSyms
{
    private static int _generations;
    private int _generation = Interlocked.Increment(ref _generations);

    public Sym this[Expr e]
    {
        get => TryGetValue(e, out Sym? s) ? s : throw new KeyNotFoundException("an expression this binding has not resolved");
        set { e.BoundSym = value; e.BoundSymBy = _generation; }
    }

    public bool TryGetValue(Expr e, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Sym sym)
    {
        if (e.BoundSymBy == _generation && e.BoundSym is { } bound) { sym = bound; return true; }
        sym = null;
        return false;
    }

    public bool ContainsKey(Expr e) => e.BoundSymBy == _generation && e.BoundSym is not null;

    public bool Remove(Expr e)
    {
        if (!ContainsKey(e)) return false;
        e.BoundSym = null;
        return true;
    }

    public void Clear() => _generation = Interlocked.Increment(ref _generations);
}

/// <summary>
/// WHICH BINDING the facts kept on the nodes belong to, for the maps below:
/// one generation shared by all of a BindResult's node-kept maps, so a node
/// needs one number however many of them it is in.
///
/// They were dictionaries keyed by node, and the ones over every call, every
/// rewrite and every user indexer grew as ExprTypes' did, to tables whose
/// rehash a fragmented heap could not place. Keeping the value on the node
/// costs no table at all. As with ExprTypes, a value is read back only by the
/// binding that wrote it: the same trees are bound again in later rounds, and
/// Next (ReleaseForRebind) leaves every map sharing this generation empty at
/// once. They are only ever emptied together, so none of them has a Clear of
/// its own.
/// </summary>
public sealed class NodeBinding
{
    private static int _generations;
    internal int Current = Interlocked.Increment(ref _generations);

    internal void Next() => Current = Interlocked.Increment(ref _generations);
}

/// <summary>
/// THE RARER FACTS ABOUT ONE EXPRESSION: what it was rewritten to, the
/// property setter assigning it calls, the Invoke a call of a value runs, and
/// whether a member access's receiver is really the first argument. Most
/// expressions have none of these, so they live in an object made only for
/// those that do (Expr.Facts) rather than in four fields on every expression.
/// </summary>
internal sealed class ExprFacts
{
    internal int By;
    internal Expr? Rewrite;
    internal MethodSymbol? Setter;
    internal MethodSymbol? Invoke;
    internal bool? Receiver;

    /// <summary>
    /// The expression's natural type as spelt (Expr.NaturalType): syntax, not
    /// a binding's, so it survives every binding's emptying below. Made with
    /// none of the rest, the facts are read as none (By is no binding's).
    /// </summary>
    internal TypeRef? Natural;

    /// <summary>This binding's facts about e, or null when it has written none.</summary>
    internal static ExprFacts? Read(Expr e, NodeBinding binding)
        => e.Facts is { } facts && facts.By == binding.Current ? facts : null;

    /// <summary>
    /// This binding's facts about e, to write into: made the first time, and
    /// emptied when what is there belongs to an earlier binding, so nothing of
    /// that one shows through.
    /// </summary>
    internal static ExprFacts Write(Expr e, NodeBinding binding)
    {
        ExprFacts? facts = e.Facts;
        if (facts is null)
        {
            facts = new ExprFacts { By = binding.Current };
            e.Facts = facts;
        }
        else if (facts.By != binding.Current)
        {
            facts.By = binding.Current;
            facts.Rewrite = null;
            facts.Setter = null;
            facts.Invoke = null;
            facts.Receiver = null;
        }
        return facts;
    }
}

/// <summary>The method each call was bound to (BindResult.Calls), kept on the call.</summary>
public sealed class CallTargets
{
    private readonly NodeBinding _binding;

    internal CallTargets(NodeBinding binding) => _binding = binding;

    public MethodSymbol this[CallExpr c]
    {
        get => TryGetValue(c, out MethodSymbol? m) ? m : throw new KeyNotFoundException("a call this binding has not bound");
        set { c.BoundCall = value; c.BoundCallBy = _binding.Current; }
    }

    public bool TryGetValue(CallExpr c, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MethodSymbol method)
    {
        if (c.BoundCallBy == _binding.Current && c.BoundCall is { } bound) { method = bound; return true; }
        method = null;
        return false;
    }

    public bool ContainsKey(CallExpr c) => c.BoundCallBy == _binding.Current && c.BoundCall is not null;

    public bool Remove(CallExpr c)
    {
        if (!ContainsKey(c)) return false;
        c.BoundCall = null;
        return true;
    }
}

/// <summary>The constructor each `new` runs (BindResult.NewConstructors), kept on the NewExpr.</summary>
public sealed class NewTargets
{
    private readonly NodeBinding _binding;

    internal NewTargets(NodeBinding binding) => _binding = binding;

    public MethodSymbol this[NewExpr n]
    {
        get => TryGetValue(n, out MethodSymbol? m) ? m : throw new KeyNotFoundException("a new this binding has not bound");
        set { n.BoundCtor = value; n.BoundCtorBy = _binding.Current; }
    }

    public bool TryGetValue(NewExpr n, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MethodSymbol ctor)
    {
        if (n.BoundCtorBy == _binding.Current && n.BoundCtor is { } bound) { ctor = bound; return true; }
        ctor = null;
        return false;
    }

    public bool ContainsKey(NewExpr n) => n.BoundCtorBy == _binding.Current && n.BoundCtor is not null;

    public bool Remove(NewExpr n)
    {
        if (!ContainsKey(n)) return false;
        n.BoundCtor = null;
        return true;
    }
}

/// <summary>
/// The accessor a user type's `x[i]` calls, kept on the IndexExpr: the getter
/// for BindResult.Indexers, the setter for IndexSetters. The two share the
/// node's generation, so writing either for a new binding empties both first.
/// </summary>
public sealed class IndexTargets
{
    private readonly NodeBinding _binding;
    private readonly bool _setters;

    internal IndexTargets(NodeBinding binding, bool setters)
    {
        _binding = binding;
        _setters = setters;
    }

    private MethodSymbol? Read(IndexExpr ix)
        => ix.BoundIndexBy != _binding.Current ? null : _setters ? ix.BoundSetter : ix.BoundGetter;

    private void Write(IndexExpr ix, MethodSymbol? value)
    {
        if (ix.BoundIndexBy != _binding.Current)
        {
            ix.BoundIndexBy = _binding.Current;
            ix.BoundGetter = null;
            ix.BoundSetter = null;
        }
        if (_setters) ix.BoundSetter = value;
        else ix.BoundGetter = value;
    }

    public MethodSymbol this[IndexExpr ix]
    {
        get => Read(ix) ?? throw new KeyNotFoundException("an index this binding has not bound");
        set => Write(ix, value);
    }

    public bool TryGetValue(IndexExpr ix, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MethodSymbol accessor)
    {
        accessor = Read(ix);
        return accessor is not null;
    }

    public bool ContainsKey(IndexExpr ix) => Read(ix) is not null;

    public bool Remove(IndexExpr ix)
    {
        if (Read(ix) is null) return false;
        Write(ix, null);
        return true;
    }
}

/// <summary>
/// What a foreach or a deconstruction became (BindResult.Lowered), kept on
/// the statement. Only those two are ever rewritten, so only they carry the
/// field; any other statement has nothing here, and recording one is a bug.
/// </summary>
public sealed class LoweredStmts
{
    private readonly NodeBinding _binding;

    internal LoweredStmts(NodeBinding binding) => _binding = binding;

    private Stmt? Read(Stmt s) => s switch
    {
        ForeachStmt fe when fe.BoundLoweredBy == _binding.Current => fe.BoundLowered,
        DeconstructStmt ds when ds.BoundLoweredBy == _binding.Current => ds.BoundLowered,
        _ => null,
    };

    private void Write(Stmt s, Stmt? value)
    {
        switch (s)
        {
            case ForeachStmt fe: fe.BoundLowered = value; fe.BoundLoweredBy = _binding.Current; break;
            case DeconstructStmt ds: ds.BoundLowered = value; ds.BoundLoweredBy = _binding.Current; break;
            default: throw new InvalidOperationException("only a foreach or a deconstruction is lowered by the binder");
        }
    }

    public Stmt this[Stmt s]
    {
        get => Read(s) ?? throw new KeyNotFoundException("a statement this binding has not lowered");
        set => Write(s, value);
    }

    public bool TryGetValue(Stmt s, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Stmt lowered)
    {
        lowered = Read(s);
        return lowered is not null;
    }

    public bool ContainsKey(Stmt s) => Read(s) is not null;

    public bool Remove(Stmt s)
    {
        if (Read(s) is null) return false;
        Write(s, null);
        return true;
    }
}

/// <summary>What each expression was rewritten to (BindResult.Rewrites), kept in its ExprFacts.</summary>
public sealed class ExprRewrites
{
    private readonly NodeBinding _binding;

    internal ExprRewrites(NodeBinding binding) => _binding = binding;

    public Expr this[Expr e]
    {
        get => TryGetValue(e, out Expr? r) ? r : throw new KeyNotFoundException("an expression this binding has not rewritten");
        set => ExprFacts.Write(e, _binding).Rewrite = value;
    }

    public bool TryGetValue(Expr e, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Expr rewrite)
    {
        rewrite = ExprFacts.Read(e, _binding)?.Rewrite;
        return rewrite is not null;
    }

    public bool ContainsKey(Expr e) => ExprFacts.Read(e, _binding)?.Rewrite is not null;

    public bool Remove(Expr e)
    {
        ExprFacts? facts = ExprFacts.Read(e, _binding);
        if (facts is null || facts.Rewrite is null) return false;
        facts.Rewrite = null;
        return true;
    }
}

/// <summary>
/// A method recorded in an expression's ExprFacts: the property setter an
/// assignment to it calls (BindResult.PropertySetters), or the Invoke a call
/// of a value runs (BindResult.Invocations).
/// </summary>
public sealed class ExprMethods
{
    private readonly NodeBinding _binding;
    private readonly bool _invokes;

    internal ExprMethods(NodeBinding binding, bool invokes)
    {
        _binding = binding;
        _invokes = invokes;
    }

    private MethodSymbol? Read(Expr e)
        => ExprFacts.Read(e, _binding) is { } facts ? (_invokes ? facts.Invoke : facts.Setter) : null;

    public MethodSymbol this[Expr e]
    {
        get => Read(e) ?? throw new KeyNotFoundException("an expression this binding has not bound");
        set
        {
            ExprFacts facts = ExprFacts.Write(e, _binding);
            if (_invokes) facts.Invoke = value;
            else facts.Setter = value;
        }
    }

    public bool TryGetValue(Expr e, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out MethodSymbol method)
    {
        method = Read(e);
        return method is not null;
    }

    public bool ContainsKey(Expr e) => Read(e) is not null;

    public bool Remove(Expr e)
    {
        if (Read(e) is null) return false;
        ExprFacts facts = ExprFacts.Write(e, _binding);
        if (_invokes) facts.Invoke = null;
        else facts.Setter = null;
        return true;
    }
}

/// <summary>The member accesses whose receiver is the first argument (BindResult.Receivers), kept in their ExprFacts.</summary>
public sealed class ReceiverFlags
{
    private readonly NodeBinding _binding;

    internal ReceiverFlags(NodeBinding binding) => _binding = binding;

    public bool this[MemberExpr m]
    {
        get => TryGetValue(m, out bool flag) ? flag : throw new KeyNotFoundException("a member access this binding has not marked");
        set => ExprFacts.Write(m, _binding).Receiver = value;
    }

    public bool TryGetValue(MemberExpr m, out bool flag)
    {
        bool? held = ExprFacts.Read(m, _binding)?.Receiver;
        flag = held ?? false;
        return held.HasValue;
    }

    public bool ContainsKey(MemberExpr m) => ExprFacts.Read(m, _binding)?.Receiver is not null;

    public bool Remove(MemberExpr m)
    {
        ExprFacts? facts = ExprFacts.Read(m, _binding);
        if (facts is null || facts.Receiver is null) return false;
        facts.Receiver = null;
        return true;
    }
}
