#nullable enable
namespace Corsac.Lang;

/// <summary>
/// Name resolution and type checking.
///
/// Reference nullability annotations and flow analysis produce warnings, as
/// in C#; they do not remove reference conversions or change runtime types.
/// Nullable value types remain distinct and require explicit unwrapping.
/// </summary>
public sealed partial class Binder
{
    /// <summary>What an async method returns, and the one thing await accepts.</summary>
    private const string TaskTypeName = "Task";

    private BindResult _r = new();
    // Declaration/layout completion precedes the source-ordered body queue.
    // Worker-local binding contexts can consume this boundary without racing
    // declaration discovery or silently omitting extending declarations.
    private readonly List<(TypeDecl Decl, TypeSymbol Symbol)> _bodyWork = new();
    private readonly string _file;
    private readonly Action<string>? _requireDeclaration;
    private readonly Action<string, string>? _requireExtensions;
    private readonly Metadata.DeclarationBatch _declarationBatch = new();
    private readonly IReadOnlyDictionary<(string Name, int Arity), int>? _indexedInterfaces;
    private readonly IReadOnlySet<(string Name, int Arity)>? _libraryInterfaces;

    /// <summary>
    /// Whether a source file belongs to the compiler's own libraries. Set by
    /// the driver; when unset every declaration counts as library, which is
    /// the numbering every image used before projects could add interfaces.
    /// </summary>
    public static Func<string, bool>? LibrarySource { get; set; }

    /// <summary>Where the library region ends: the first slot a library class's own virtuals were given.</summary>
    private int _librarySlots;

    /// <summary>Slots kept above the library's interface region for the library's class virtuals; see the numbering.</summary>
    private const int LibraryClassReserve = 128;

    private TypeSymbol? _thisType;
    private MethodSymbol? _method;
    private readonly List<LocalScope> _scopes = new();

    private sealed class LocalScope : Dictionary<string, Sym>
    {
        // Completed children still forbid a later declaration in this scope,
        // but never forbid reuse in a sibling. These are names, not bindings:
        // lookup and definite assignment retain their declaration-time rules.
        public readonly HashSet<string> NestedNames = new(StringComparer.Ordinal);
        public readonly bool FunctionBoundary;

        public LocalScope(bool functionBoundary) : base(StringComparer.Ordinal)
        {
            FunctionBoundary = functionBoundary;
        }
    }

    /// Calls whose receiver has already been moved into the argument list. A
    /// call is checked once per generic instantiation, and inserting twice
    /// would pass the string as its own first argument.
    private readonly HashSet<CallExpr> _receiverAdded = new(ReferenceEqualityComparer.Instance);
    private int _nextSlot;
    private int _maxSlot;
    private int _loopDepth;
    private readonly List<SwitchStmt> _switches = new();

    /// <summary>
    /// Next free byte of static storage. Statics live in the data segment above
    /// the reserved null and arena words, so the compiler must know where the
    /// first one may go.
    /// </summary>
    private int _staticNext = 16;

    /// <summary>
    /// The same counter for types that came from a library header -- ONE PER
    /// LIBRARY, keyed by the slot that library's statics live in.
    ///
    /// Kept apart from this program's own counter so a library's statics land
    /// where the library put them rather than after whatever statics the
    /// program happens to declare. And kept apart FROM EACH OTHER, which is
    /// the part that was missing: a single shared counter numbered the second
    /// library's fields after the first library's, so the moment std grew
    /// statics of its own every offset in os moved -- in programs, but not in
    /// os itself, which had numbered them from the start. A shell then read a
    /// user's name out of the middle of the allocator's bookkeeping.
    ///
    /// Every library numbers from the same place, because every library was
    /// compiled that way: its own statics start at the first byte after the
    /// reserved words, and its slot is what keeps it clear of the others.
    /// </summary>
    private readonly Dictionary<int, int> _externNext = new();

    /// <summary>How many vtable slots are reserved for interface methods.</summary>
    private int _interfaceSlots;

    /// <summary>How a template is keyed: its name and how many parameters it takes.</summary>
    internal static string Arity(string name, int count) => name + "`" + count;

    /// <summary>
    /// How a declaration is keyed in the flat table of types: by the path C#
    /// would name it with, so a type written inside another is `Outer.Inner`
    /// and cannot be confused with a different `Inner` written somewhere else.
    /// </summary>
    internal static string TypeKey(TypeDecl d)
    {
        string simple = d.TypeParams.Count > 0 ? Arity(d.Name, d.TypeParams.Count) : d.Name;

        return d.Outer is null ? simple : d.Outer + "." + simple;
    }

    /// <summary>
    /// The type a nested name is written inside, or null once the walk reaches
    /// the top level: `A.B.C` gives `A.B`, and `A` gives null.
    /// </summary>
    private static string? Enclosing(string key)
    {
        int cut = key.LastIndexOf('.');

        return cut < 0 ? null : key[..cut];
    }

    /// <summary>
    /// Finds a type by the name the source wrote, from wherever the source
    /// wrote it.
    ///
    /// C#'s rule, and the reason a nested type can be named without its outer
    /// from inside: look in the type being checked, then in the type THAT was
    /// written inside, out to the top level, and only then take the name as
    /// written. Without the walk, `Section.Code` inside Assembler stops
    /// resolving the moment anything else declares a Section.
    /// </summary>
    private bool FindType(string name, out TypeSymbol? sym)
    {
        TypeDecl? written = (_scope ?? _thisType)?.Decl;
        string within = _member?.Scope != null ? _member.Namespace : written?.Namespace ?? "";
        FileScope? file = _member?.Scope ?? written?.Scope;

        // OUTWARDS FROM WHERE IT WAS WRITTEN: the types this one is written
        // inside, which is why a nested type can be named without its outer,
        // and then namespace by namespace. At each step the members come
        // first and then the using directives written at that step -- C#'s
        // order, and the reason `Block` inside Corsac.Lang.Opt is the IR's:
        // an alias written there is reached before Corsac.Lang's own Block is.
        //
        // TWICE, because a SPECIALISATION is keyed globally and yet written
        // wherever the generic was used. `List$Operand` has no path to walk
        // and the namespace it was made in is the only thing that can say
        // which Operand its element is.
        string?[] outwards = { (_scope ?? _thisType)?.Key, within.Length == 0 ? null : within };

        foreach (string? from in outwards)
        {
            for (string? at = from; at is not null; at = Enclosing(at))
            {
                if (TypeCandidate(at + "." + name, out sym))
                {
                    return true;
                }

                if (file != null && Imported(file, at, name, out sym))
                {
                    return true;
                }
            }
        }

        if (TypeCandidate(name, out sym))
        {
            return true;
        }

        if (file != null && Imported(file, "", name, out sym))
        {
            return true;
        }

        // A NESTED TYPE NAMED FROM OUTSIDE THE TYPE THAT HOLDS IT, and named
        // without its outer.
        //
        // C# refuses that and wants `Assembler.Item`. This accepts it when the
        // simple name belongs to exactly one type in the program, because the
        // name is not always written by a person: a specialisation of
        // `List<Item>` is a copy of List's declaration with the argument
        // spliced in AS WRITTEN, and it is checked with List's scope rather
        // than the scope the use site was in. Under a strict rule the copy
        // cannot name its own element type.
        //
        // Two types of the same simple name stay distinct: each is found by
        // the walk above from inside, and neither is sole, so an unqualified
        // mention from anywhere else is refused rather than guessed.
        return Sole(name, out sym);
    }

    /// <summary>
    /// `Registry.IsSet(Settings.Canvas.Width)` as the call it stands for.
    ///
    /// IsSet becomes a question about the key and Revert becomes a delete of
    /// the USER scope, which is the only scope a program's own declared
    /// member ever writes.
    ///
    /// An argument that is not a declared setting is reported and then stood
    /// in for, rather than refused here: leaving the call as written would
    /// have it resolved again as an ordinary method nothing declares, and a
    /// second complaint about the same mistake helps nobody.
    /// </summary>
    private Expr Setting(CallExpr asking)
    {
        MemberExpr called = (MemberExpr)asking.Target;
        string? written = WrittenPath(asking.Args[0]);
        string? key = written is null ? null : Key(written);

        if (key is null)
        {
            Error(asking, $"Registry.{called.Name} takes a setting declared under a [Registry] class, "
                        + (written is null
                            ? "and this is not one -- not a name, and not a value read out of one"
                            : $"and '{written}' is not one"));
            key = "";
        }

        bool forget = called.Name == "Revert";

        CallExpr instead = new()
        {
            Target = new MemberExpr
            {
                Target = new NameExpr { Name = "Registry", Line = asking.Line, Col = asking.Col, File = asking.File },
                Name = forget ? "Delete" : "HasKey",
                Line = asking.Line, Col = asking.Col, File = asking.File,
            },
            Line = asking.Line, Col = asking.Col, File = asking.File,
        };

        instead.Args.Add(new LiteralExpr
        {
            Kind = Lit.Str, Text = key, Line = asking.Line, Col = asking.Col, File = asking.File,
        });
        instead.Args.Add(new MemberExpr
        {
            Target = new NameExpr { Name = "RegistryScope", Line = asking.Line, Col = asking.Col, File = asking.File },
            Name = forget ? "User" : "Both",
            Line = asking.Line, Col = asking.Col, File = asking.File,
        });
        return instead;
    }

    /// <summary>A dotted path of plain names, or null for anything else.</summary>
    private static string? WrittenPath(Expr e)
    {
        return e switch
        {
            NameExpr { TypeArgs.Count: 0 } name => name.Name,
            MemberExpr member => WrittenPath(member.Target) is string outer ? outer + "." + member.Name : null,
            _ => null,
        };
    }

    /// <summary>
    /// The setting a written path names. A path may be written short --
    /// `Canvas.Width` from inside Settings -- so a suffix is accepted where
    /// exactly one setting ends that way, and refused where several do.
    /// </summary>
    private string? Key(string written)
    {
        if (_registryKeys.TryGetValue(written, out string? found))
        {
            return found;
        }

        string? only = null;

        foreach ((string path, string key) in _registryKeys)
        {
            if (!path.EndsWith("." + written, StringComparison.Ordinal))
            {
                continue;
            }

            if (only is not null)
            {
                return null;            // several, so the short path says nothing
            }
            only = key;
        }
        return only;
    }

    /// <summary>What an enum written `: name` is stored as, or null where
    /// that is not something an enum may be stored as.</summary>
    private static Prim? Underlying(string name) => name switch
    {
        "byte" => Prim.U8,
        "sbyte" => Prim.I8,
        "short" => Prim.I16,
        "ushort" => Prim.U16,
        "int" => Prim.I32,
        "uint" => Prim.U32,
        "long" => Prim.I64,
        "ulong" => Prim.U64,
        _ => null,
    };

    /// <summary>Whether a primitive is one this compiler emits a descriptor for.</summary>
    private static bool Descriptive(Prim prim)
        => prim is Prim.Bool or Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64
                or Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64
                or Prim.NInt or Prim.NUInt or Prim.F32 or Prim.F64
                or Prim.Char or Prim.String;

    private bool TypeCandidate(string key, out TypeSymbol? symbol)
    {
        if (_r.Types.TryGetValue(key, out symbol))
        {
            // ASKED FOR, not merely present. This is what tells the managed
            // layout which types this unit has an opinion about.
            symbol.Used = true;
            return true;
        }
        // A MISSING DECLARATION IS RECORDED, NOT RAISED. Unwinding here threw
        // the whole unit away for one name, and a single dispatcher naming a
        // dozen kernel types therefore cost a dozen rebuilds. Checking carries
        // on with the name unresolved instead, which reports nonsense for the
        // rest of this pass -- and that is fine, because the pass is discarded
        // the moment anything was recorded. See DeclarationBatch.
        try { _requireDeclaration?.Invoke(key); }
        catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
        return false;
    }

    /// <summary>Whether this pass has already found declarations it must retry with.</summary>
    private bool Demanded => _declarationBatch.Any;

    /// <summary>
    /// A dotted name as it was written, when an expression is nothing but one:
    /// `Corsac.Asm.X86Assembler` from the member accesses it parsed as. Null
    /// for anything with a call, an index or a bracket in it.
    /// </summary>
    private static string? Spelt(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr m => Spelt(m.Target) is string head ? head + "." + m.Name : null,
        _ => null,
    };

    /// <summary>
    /// What the using directives of one namespace declaration make this name
    /// mean: an alias first, and then the namespaces imported there.
    ///
    /// EXACTLY ONE IMPORTED TYPE, or the name means nothing. C# refuses an
    /// ambiguous import rather than choosing between them, and so does this --
    /// choosing is how a file comes to mean something other than it says.
    /// </summary>
    private bool Imported(FileScope file, string at, string name, out TypeSymbol? sym)
    {
        foreach ((string In, string Alias, string Target) alias in file.Aliases)
        {
            if (alias.In == at && alias.Alias == name
                && TypeCandidate(alias.Target, out sym))
            {
                return true;
            }
        }

        sym = null;

        foreach ((string In, string Namespace) import in file.Imports)
        {
            if (import.In != at
                || !TypeCandidate(import.Namespace + "." + name, out TypeSymbol? found))
            {
                continue;
            }

            if (sym != null && !ReferenceEquals(sym, found))
            {
                sym = null;
                return false;
            }
            sym = found;
        }

        return sym != null;
    }

    /// <summary>
    /// The one type whose simple name is this, if there is exactly one.
    ///
    /// Kept as an index because the alternative is a scan of every declared
    /// type per unresolved name, and unresolved names are common while a file
    /// is being checked.
    /// </summary>
    private bool Sole(string name, out TypeSymbol? sym)
    {
        if (_soleAt != _r.Types.Count)
        {
            _sole.Clear();

            foreach ((string key, TypeSymbol type) in _r.Types)
            {
                if (Enclosing(key) is null)
                {
                    continue;
                }

                // Ambiguous is recorded as null rather than dropped, so a
                // second one cannot be undone by a third.
                _sole[type.Name] = _sole.ContainsKey(type.Name) ? null : type;
            }

            _soleAt = _r.Types.Count;
        }

        return _sole.TryGetValue(name, out sym) && sym is not null;
    }

    /// <summary>Nested types by simple name; null where more than one shares it.</summary>
    private readonly Dictionary<string, TypeSymbol?> _sole = new(StringComparer.Ordinal);

    /// <summary>How large the table of types was when <see cref="_sole"/> was built.</summary>
    private int _soleAt = -1;

    /// <summary>Whether a name resolves to a type from where it was written.</summary>
    private bool IsTypeName(string name) => FindType(name, out _);

    /// <summary>
    /// The type whose declaration is being read, when that is not the same as
    /// the type whose body is being checked -- a member's signature is written
    /// inside its type before <see cref="_thisType"/> means anything.
    /// </summary>
    private TypeSymbol? _scope;

    /// <summary>
    /// The member whose declaration or body is being read, when it is one.
    ///
    /// A partial class is several files merged into one declaration, and each
    /// part's names are resolved with its OWN file's using directives -- which
    /// is C#'s rule and the only one that can be right, since the parts do not
    /// agree about what `Block` means.
    /// </summary>
    private MemberDecl? _member;

    /// <summary>Whether this symbol is a template rather than something real.</summary>
    private static bool IsTemplate(TypeSymbol t) => t.Decl?.TypeParams.Count > 0;

    /// <summary>The type parameters of the method signature being declared.</summary>
    private List<string>? _signature;

    /// <summary>Accessor methods invented for properties, checked like any other body.</summary>
    private readonly List<MethodDecl> _synthesised = new();

    /// <summary>Total bytes of static storage the program needs.</summary>
    public int StaticBytes => _staticNext;

    public Binder(string file = "<source>", Action<string>? requireDeclaration = null,
        IReadOnlyDictionary<(string Name, int Arity), int>? indexedInterfaces = null,
        Action<string, string>? requireExtensions = null,
        IReadOnlySet<(string Name, int Arity)>? libraryInterfaces = null)
    {
        _file = file;
        _requireDeclaration = requireDeclaration;
        _requireExtensions = requireExtensions;
        _indexedInterfaces = indexedInterfaces;
        _libraryInterfaces = libraryInterfaces;
    }

    /// <summary>
    /// A type declared in the compiler's own library sources, or specialised
    /// from a template that was. Its slots are ABI: numbered the way the
    /// library's own build numbered them, whatever this compilation adds.
    /// </summary>
    private static bool IsLibraryType(TypeSymbol t)
    {
        string? path = t.Decl?.SourcePath;
        if (path is null || LibrarySource is null) return true;
        return LibrarySource(path);
    }

    /// <summary>
    /// Checks a whole compilation.
    ///
    /// <paramref name="maskWords"/> is the descriptor depth this compilation
    /// must use rather than the one its own type count would choose: the
    /// libraries it links already read descriptors at that distance, and shared
    /// code cannot be told otherwise. Zero means nothing is being linked and
    /// the count decides.
    /// </summary>
    /// <summary>
    /// Where each declared registry setting lives, by the path it is written
    /// with. Empty in a program that declares none, which is nearly all of
    /// them. See CompilationUnit.RegistryKeys.
    /// </summary>
    private IReadOnlyDictionary<string, string> _registryKeys = new Dictionary<string, string>(StringComparer.Ordinal);

    public static BindResult Bind(CompilationUnit unit, string file = "<source>", Action<string>? requireDeclaration = null,
        IReadOnlyDictionary<(string Name, int Arity), int>? indexedInterfaces = null,
        Action<string, string>? requireExtensions = null,
        IReadOnlySet<(string Name, int Arity)>? libraryInterfaces = null)
    {
        Binder b = new(file, requireDeclaration, indexedInterfaces, requireExtensions, libraryInterfaces);
        b.Run(unit);
        b._r = b._r.CopyForBodyChecking();
        b.CheckBodyWork();
        b._r.StaticBytes = b._staticNext;
        return b._r;
    }

    /// <summary>
    /// Which source is being checked right now, so a diagnostic names the file
    /// the mistake is actually in.
    ///
    /// Several files become one unit before anything is checked, and a line
    /// number means nothing without the file it counts lines in. Stamping every
    /// error with the first name on the command line sent me to a perfectly
    /// correct line of a file I had not touched.
    /// </summary>
    private string _in = "";

    private void Error(Node at, string message)
    {
        // SILENT WHILE A LAMBDA IS BEING LOOKED OVER. A lambda's body is bound
        // twice: once to find out which of the enclosing method's locals it
        // uses, and again for real once a closure with fields for them exists.
        // The first pass resolves those names to locals that the second pass
        // will resolve to fields, and anything it complains about the second
        // pass complains about properly.
        if (_quiet > 0)
        {
            return;
        }

        _r.Errors.Add(new CompileError(Where(), at.Line, at.Col, message));
    }

    /// <summary>
    /// Which file a diagnostic names.
    ///
    /// THE MEMBER'S, when one is being read, and not the type's: a partial
    /// class is written across several files and merged into one declaration,
    /// so the type's file is whichever part came first. A line number from
    /// another part then sends the reader to a line of the wrong file, which is
    /// worse than no file at all.
    /// </summary>
    private string Where()
        => _member?.File is { Length: > 0 } written ? written
         : _in.Length > 0 ? _in
         : _file;

    private void Warning(Node at, string message)
    {
        if (_quiet > 0) return;
        _r.Warnings.Add(new CompileError(Where(), at.Line, at.Col, message, warning: true));
    }

    private int _quiet;

    /// <summary>
    /// How many scopes were already open when the innermost lambda's body
    /// started, or -1 outside one. A local found below this line belongs to the
    /// enclosing method and has to be captured.
    /// </summary>
    private int _lambdaFloor = -1;

    /// <summary>Names the lambda being looked over reads from outside itself.</summary>
    private Dictionary<string, Type>? _captured;
    private TypeSymbol? _capturedThisType;
    private FieldSymbol? _capturedThisField;

    /// <summary>
    /// The source type lexically enclosing a generated closure.
    ///
    /// A lambda's runtime <c>this</c> is its generated closure object, but C#
    /// name lookup does not forget the class in which the lambda was written.
    /// Constants and static members of that class remain directly visible even
    /// in a static method, where there is no instance to capture.
    /// </summary>
    private TypeSymbol? _lexicalType;

    /// <summary>Which declaration each local came from, so a capture can mark it.</summary>
    private readonly Dictionary<LocalSym, LocalDecl> _declOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LocalDecl, LocalSym> _hoistedFunctions = new(ReferenceEqualityComparer.Instance);

    /// <summary>Stable source-method identity, independent of loaded declaration subsets.</summary>
    private string _closureOwner = "declaration";

    /// <summary>Closure classes built within the current body work item.</summary>
    private int _closures;
    private readonly Dictionary<string, ClosureInfo> _groupClosures = new();

    /// <summary>
    /// Method groups with a receiver that is not `this`: the lambda made for
    /// `workers[i].Run` and the receiver expression, which is evaluated ONCE,
    /// when the delegate is made, into the closure's $target field. Reading it
    /// from inside the delegate instead captured `workers` and `i`, and a
    /// delegate made in a loop called the method on whatever the last
    /// iteration left there -- or, with `i` one past the end, threw.
    /// </summary>
    private readonly Dictionary<LambdaExpr, Expr> _boundTargets = new(ReferenceEqualityComparer.Instance);

    /// <summary>The first closure made for each bound method group: its class and Invoke, shared by later conversions of the same method with their own receivers.</summary>
    private readonly Dictionary<string, ClosureInfo> _boundClosures = new();

    private const string BoundTargetField = "$target";

    /// Names the hidden locals a rewritten foreach needs, so nested loops do
    /// not share one.
    private int _iterations;

    /// The pattern subjects being checked right now, innermost last: a pattern
    /// may appear inside another one's alternative, and a whole switch pushes
    /// one for all of its labels together.
    ///
    /// Only the slot and the type are wanted. The node that pushed it was in
    /// here too and nothing ever read it, which stopped a switch -- which has no
    /// PatternExpr of its own -- from using this at all.
    private readonly List<(int Slot, Type Type)> _subject = new();


    /// <summary>
    /// The type an expression is being checked AGAINST, where one is known.
    ///
    /// Only target-typed `new()` reads it: everything else works out what it
    /// is and is then checked against what was wanted, which is the right way
    /// round for reporting. `new()` cannot -- it is the one expression whose
    /// meaning IS the wanted type.
    /// </summary>
    private Type? _wanted;

    /// <summary>
    /// A const's value, or null when what was written is not one.
    ///
    /// Whole-number literals and their negations, and nothing else. Folding
    /// arithmetic here would mean a second evaluator beside the one the machine
    /// already has, and two of those disagree eventually.
    /// </summary>
    /// <summary>
    /// The number a const is a name for.
    ///
    /// FOLDED, NOT MERELY READ. `const int ImmMin = -(1 << (ImmBits - 1));` is
    /// how a machine's own limits are written and how this compiler's Isa.cs
    /// writes eight of them -- a literal there would be a magic number whose
    /// relationship to ImmBits nobody could check. The folder is the same one
    /// the code generator uses on any other constant expression.
    /// </summary>
    private long? ConstantValue(Expr? e, TypeSymbol? owner = null)
        => e is not null && Fold.TryConst(e, out long value, x => Named(x, owner)) ? value : null;

    // Register every declaration before evaluating any initializer. C# permits
    // forward references between constant fields, even across source files;
    // only a dependency cycle is invalid. Keep the declared owner/file while
    // descending so a referenced type's lexical scope cannot leak back into
    // the expression which requested its value.
    private readonly Dictionary<(TypeSymbol Owner, string Name), (FieldDecl Field, string File)>
        _constantDeclarations = new();
    private enum ConstantState { Evaluating, Complete, Failed }
    private readonly Dictionary<(TypeSymbol Owner, string Name), ConstantState>
        _constantStates = new();

    private void EvaluateConstant(TypeSymbol owner, string name)
    {
        var key = (owner, name);
        if (!_constantDeclarations.TryGetValue(key, out var declaration)) return;
        if (_constantStates.TryGetValue(key, out ConstantState state))
        {
            if (state == ConstantState.Evaluating)
            {
                string previousFile = _in;
                _in = declaration.File;
                Error(declaration.Field, $"circular constant dependency involving '{owner.Key}.{name}'");
                _in = previousFile;
                _constantStates[key] = ConstantState.Failed;
            }
            return;
        }

        _constantStates[key] = ConstantState.Evaluating;
        TypeSymbol? previousScope = _scope;
        string previous = _in;
        _scope = owner;
        _in = declaration.File;
        try
        {
            FieldDecl field = declaration.Field;
            string? text = field.Init is LiteralExpr { Kind: Lit.Str } literal
                ? literal.Text : field.Type.Name == "string" ? ConstantText(field.Init, owner) : null;
            if (text is not null)
            {
                _r.TextConstants[key] = text;
                _constantStates[key] = ConstantState.Complete;
            }
            else if (Resolve(field.Type, owner) is { } realType && IsReal(realType))
            {
                if (RealConstant(field.Init, owner) is double real)
                {
                    _r.Constants[key] = RealBits(real, realType);
                    _r.ConstantTypes[key] = realType;
                    _constantStates[key] = ConstantState.Complete;
                }
                else
                {
                    if (_constantStates[key] != ConstantState.Failed)
                        Error(field, $"'{name}' is const, so it must have a constant expression");
                    _constantStates[key] = ConstantState.Failed;
                }
            }
            else if (ConstantValue(field.Init, owner) is long value)
            {
                _r.Constants[key] = value;
                _r.ConstantTypes[key] = Resolve(field.Type, owner);
                _constantStates[key] = ConstantState.Complete;
            }
            else
            {
                if (_constantStates[key] != ConstantState.Failed)
                    Error(field, $"'{name}' is const, so it must have a constant expression");
                _constantStates[key] = ConstantState.Failed;
            }
        }
        finally
        {
            _scope = previousScope;
            _in = previous;
        }
    }

    private static bool IsReal(Type t) => t.Prim is Prim.F32 or Prim.F64 && !t.Nullable;

    /// <summary>
    /// A FLOATING-POINT CONST is kept as the bits of its value as a double,
    /// in the same table as every other const, its type saying which it is. A
    /// float's value is rounded to a float first, as C# computes it.
    /// </summary>
    private static long RealBits(double value, Type type)
        => BitConverter.DoubleToInt64Bits(type.Prim == Prim.F32 ? (double)(float)value : value);

    /// <summary>
    /// What a floating-point constant expression is worth: literals, other
    /// consts (floating or integer), negation, the four operations and casts
    /// between the numeric types -- which is how `double.NaN` is written, as
    /// `0.0 / 0.0`. Null when it is not a constant.
    /// </summary>
    private double? RealConstant(Expr? e, TypeSymbol? owner)
    {
        switch (e)
        {
            case null:
                return null;
            case LiteralExpr { Kind: Lit.Real } real:
                return real.RealValue;
            case LiteralExpr { Kind: Lit.Int } whole:
                return whole.IntValue;
            case UnaryExpr { Op: UnOp.Neg } negated:
                return RealConstant(negated.Operand, owner) is double inner ? -inner : null;
            case BinaryExpr { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Rem } b:
            {
                if (RealConstant(b.Left, owner) is not double left || RealConstant(b.Right, owner) is not double right) return null;
                return b.Op switch
                {
                    BinOp.Add => left + right,
                    BinOp.Sub => left - right,
                    BinOp.Mul => left * right,
                    BinOp.Div => left / right,
                    _ => left % right,
                };
            }
            case CastExpr cast:
            {
                Type to = Resolve(cast.Type, owner);
                if (to.Prim == Prim.F32) return RealConstant(cast.Operand, owner) is double f ? (double)(float)f : null;
                if (to.Prim == Prim.F64) return RealConstant(cast.Operand, owner);
                return ConstantValue(cast, owner) is long integral ? integral : null;
            }
            case NameExpr local when Lookup(local.Name) is ConstSym { Text: null } named:
                return IsReal(named.Type) ? BitConverter.Int64BitsToDouble(named.Value) : named.Value;
            case NameExpr n when owner != null && FindConstant(owner, n.Name) is { } here:
                return IsReal(here.Type) ? BitConverter.Int64BitsToDouble(here.Value) : here.Value;
            case MemberExpr m when ConstantOwner(m.Target) is { } named && FindConstant(named, m.Name) is { } there:
                return IsReal(there.Type) ? BitConverter.Int64BitsToDouble(there.Value) : there.Value;
            default:
                return ConstantValue(e, owner) is long value ? value : null;
        }
    }

    private TypeSymbol? ConstantOwner(Expr expression)
    {
        if (expression is NameExpr name)
            return FindType(name.Name, out TypeSymbol? owner) ? owner : null;
        if (expression is MemberExpr member)
        {
            // A NAMESPACE-QUALIFIED TYPE: the `Corsac.Lang.Elf.Elf` of
            // `const string SharedInitName = Corsac.Lang.Elf.Elf.SharedInitName;`.
            // Nothing in that chain but the last name is a type, so walking it
            // a member at a time finds nothing at the first step; the whole
            // dotted name is what names the type.
            if (Spelt(expression) is { } path && FindType(path, out TypeSymbol? written))
                return written;
            if (ConstantOwner(member.Target) is { } outer)
                return _r.Types.TryGetValue(outer.Key + "." + member.Name, out TypeSymbol? nested)
                    ? nested : null;
        }
        return null;
    }

    private string? ConstantText(Expr? expression, TypeSymbol owner) => expression switch
    {
        LiteralExpr { Kind: Lit.Str } literal => literal.Text,

        // A LOCAL const IS A NAME FOR A VALUE TOO, and one const may be built
        // out of another: `const string name = "x"; const string also = name +
        // "2";` is as ordinary inside a method as it is on a class.
        NameExpr local when Lookup(local.Name) is ConstSym { Text: not null } named => named.Text,

        NameExpr name => FindText(owner, name.Name),
        MemberExpr member when ConstantOwner(member.Target) is { } named => FindText(named, member.Name),
        BinaryExpr { Op: BinOp.Add } add when ConstantText(add.Left, owner) is { } left
            && ConstantText(add.Right, owner) is { } right => left + right,
        _ => null,
    };

    /// <summary>
    /// What a NAME inside a constant expression is worth: another const of the
    /// same type or one it derives from, or an enum member.
    ///
    /// Constant declarations are registered independently of source order.
    /// Looking one up evaluates its dependencies and diagnoses actual cycles,
    /// rather than treating a forward reference as a cycle.
    /// </summary>
    private long? Named(Expr e, TypeSymbol? owner)
    {
        switch (e)
        {
            // A LOCAL const, which names a value exactly as a field's does.
            case NameExpr local when Lookup(local.Name) is ConstSym { Text: null } named:
                return IsReal(named.Type) ? null : named.Value;

            case NameExpr n when owner != null && FindConstant(owner, n.Name) is { } here:
                return IsReal(here.Type) ? null : here.Value;

            case MemberExpr { Target: NameExpr keyword } m
                when !IsTypeName(keyword.Name)
                  && Limit(keyword.Name, m.Name) is long edge:
                return edge;

            case MemberExpr m:
            {
                if (ConstantOwner(m.Target) is { } named)
                {
                    if (named.EnumValues.TryGetValue(m.Name, out long member))
                    {
                        return member;
                    }

                    if (FindConstant(named, m.Name) is { } elsewhere)
                    {
                        return IsReal(elsewhere.Type) ? null : elsewhere.Value;
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// `^k` as the number it means: how long the thing is, minus k.
    ///
    /// Which member says how long depends on what it is -- an array and a span
    /// have a Length, a list has a Count -- and the checker knows by now.
    /// </summary>
    private Expr Counted(Expr target, FromEndExpr end)
    {
        Type had = Peek(target);
        string many = had.IsArray || had.Prim == Prim.String ? "Length"
                    : had.Symbol is TypeSymbol sym && Reachable(sym, "get_Count").Any() ? "Count"
                    : "Length";

        return new BinaryExpr
        {
            Op = BinOp.Sub,
            Left = new MemberExpr { Target = target, Name = many, Line = end.Line, Col = end.Col },
            Right = end.Offset,
            Line = end.Line, Col = end.Col,
        };
    }

    /// <summary>Whether a whole number is inside what a type can hold.</summary>
    private static bool Fits(long value, Type to) => to.Prim switch
    {
        Prim.I8  => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        Prim.U8  => value is >= byte.MinValue and <= byte.MaxValue,
        Prim.I16 => value is >= short.MinValue and <= short.MaxValue,
        Prim.U16 => value is >= ushort.MinValue and <= ushort.MaxValue,
        Prim.I32 => value is >= int.MinValue and <= int.MaxValue,
        Prim.U32 => value is >= 0 and <= uint.MaxValue,
        Prim.Char => value is >= 0 and <= char.MaxValue,
        Prim.I64 or Prim.U64 => true,
        Prim.NInt => Target.Current.WordSize == 8 || value is >= int.MinValue and <= int.MaxValue,
        Prim.NUInt => Target.Current.WordSize == 8 ? value >= 0 : value is >= 0 and <= uint.MaxValue,
        _ => false,
    };

    /// <summary>Looks a TEXT const up on a type or any of its bases.</summary>
    private string? FindText(TypeSymbol? owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = t.Base)
        {
            EvaluateConstant(t, name);
            if (_r.TextConstants.TryGetValue((t, name), out string? text))
            {
                return text;
            }
            if (_constantDeclarations.ContainsKey((t, name))) return null;
        }
        return null;
    }

    /// <summary>Looks a const up on a type or any of its bases.</summary>
    private (long Value, Type Type)? FindConstant(TypeSymbol? owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = t.Base)
        {
            EvaluateConstant(t, name);
            if (_r.Constants.TryGetValue((t, name), out long value))
            {
                return (value, _r.ConstantTypes.TryGetValue((t, name), out Type? was) ? was : Type.I64);
            }
            if (_constantDeclarations.ContainsKey((t, name))) return null;
        }
        return null;
    }

    // ---- top level ------------------------------------------------------

    private void Run(CompilationUnit unit)
    {
        _registryKeys = unit.RegistryKeys;

        // WHAT EVERY TUPLE SHAPE HAS BEEN CALLED, before any of it is checked.
        //
        // A specialisation carries its arguments in its name and not in an
        // argument list, so `List<(int A, int B)>` arrives here as
        // `List$ValueTuple_int_int` and the element names came off with the
        // brackets. These are the brackets, kept by the monomorphiser for this
        // one purpose: resolving each writes the naming down against the class
        // the shape shares, and `list[i].A` can be answered afterwards.
        // QUIETLY: some of them are open -- `(T, U)` inside a template -- and
        // what is wanted from them is the NAMES, not a resolution. A shape
        // that cannot be resolved has nothing to remember and says so to
        // nobody.
        // FIELD INITIALISERS BECOME CONSTRUCTOR STATEMENTS, before anything is
        // declared or checked, so that everything downstream sees ordinary
        // assignments and needs to know nothing about this.
        foreach (TypeDecl d in unit.Types)
        {
            Initialisers(d);
            StaticInitialisers(d);
        }

        // AND A CLASS WHOSE BASE HAS A CONSTRUCTOR HAS ONE TOO, written or not.
        // C# gives every class a constructor that calls its base's; here one is
        // only made when there is something for it to do, and a base that
        // initialises its own fields is something. Without it `new MemberExpr`
        // never ran Node's `File = ""`, and the file of every expression in the
        // compiler compiled by itself was null.
        Dictionary<string, TypeDecl> byName = new(StringComparer.Ordinal);

        foreach (TypeDecl d in unit.Types)
        {
            byName.TryAdd(d.Name, d);
        }

        foreach (TypeDecl d in unit.Types)
        {
            if (d.Kind != TypeKind.Class || d.Mods.HasFlag(Mods.Static)
                || d.Members.OfType<MethodDecl>().Any(c => c.IsCtor))
            {
                continue;
            }

            TypeDecl? up = d;
            bool constructed = false;

            for (int depth = 0; depth < 64 && up is not null && !constructed; depth++)
            {
                up = up.Bases.Select(b => byName.GetValueOrDefault(b.Name))
                       .FirstOrDefault(b => b is { Kind: TypeKind.Class });
                constructed = up is not null
                    && up.Members.OfType<MethodDecl>().Any(c => c.IsCtor && c.Params.Count == 0 && !c.Mods.HasFlag(Mods.Static));
            }

            if (constructed)
            {
                d.Members.Add(new MethodDecl
                {
                    Name = d.Name, IsCtor = true, Mods = Mods.Public,
                    Body = new Block { Line = d.Line, Col = d.Col },
                    Line = d.Line, Col = d.Col, File = d.File,
                    OwnedImplementation = !d.Elsewhere,
                });
            }
        }

        // Source declarations that ADD to a prelude type rather than clashing
        // with it. Given their members and their bodies below, once every
        // symbol exists.
        List<(TypeDecl Decl, TypeSymbol Symbol)> extend = new();

        // Three passes: declare every type first so they can refer to each
        // other in any order, then fill in members, then check bodies.
        foreach (TypeDecl d in unit.Types)
        {
            // A TEMPLATE IS KEYED BY ITS NAME AND ARITY, never by its name
            // alone.
            //
            // `Func<A,R>` and `Func<A,B,R>` are two types sharing a name --
            // which is how .NET spells them and therefore how anything hoping
            // to compile C# has to -- so one key would make the second a
            // duplicate declaration. The monomorphiser has always keyed them
            // this way; this is the checker catching up, now that templates
            // survive into the unit it looks at.
            //
            // Nothing else looks a template up by bare name: every other
            // lookup wants a concrete type, and a concrete type is what a
            // specialisation is.
            string key = TypeKey(d);

            if (_r.Types.TryGetValue(key, out TypeSymbol? already))
            {
                // EXTENDING A PRELUDE TYPE, rather than colliding with it.
                //
                // A few members of Math ARE instructions -- Sqrt, Min, Fma --
                // so the prelude declares them and a call to one becomes an
                // op. Everything else a Math needs is ordinary source, and it
                // had to live under another name because a second `class Math`
                // was a duplicate declaration.
                //
                // This is what .NET does with this very class: some members are
                // intrinsified and the rest are ordinary code, and which is
                // which is not the caller's business.
                if (already.Decl?.File == "<prelude>")
                {
                    extend.Add((d, already));
                    continue;
                }

                // A PROGRAM'S OWN TYPE WINS OVER THE CLASS LIBRARY'S.
                //
                // .NET declares Stack<T> in System.Collections.Generic; a
                // program that writes `class Stack<T>` of its own in the
                // global namespace gets that one, and every unqualified
                // mention of the name resolves to it. This compiler flattens
                // namespaces, so the same answer is reached by asking which
                // declaration came from the library -- neither is an error,
                // and the program's replaces the library's.
                //
                // The pass below declares members only for the symbol whose
                // Decl it is holding, so the shadowed declaration simply goes
                // no further: no members, no bodies, no code.
                if (already.Decl?.FromLibrary == true && !d.FromLibrary)
                {
                    TypeSymbol mine = new() { Name = d.Name, Key = key, Kind = d.Kind, Decl = d };
                    mine.TypeParams.AddRange(d.TypeParams.Select(p => p.Name));
                    _r.Types[key] = mine;
                    continue;
                }

                if (d.FromLibrary && already.Decl?.FromLibrary == false)
                {
                    continue;
                }

                // Named at the file it was written in, not at whatever file the
                // last pass happened to leave behind: a duplicate reported
                // against an unrelated source is a diagnostic that sends the
                // reader to the wrong place.
                _in = d.File;
                Error(d, $"'{key}' is declared more than once");
                continue;
            }

            TypeSymbol sym = new() { Name = d.Name, Key = key, Kind = d.Kind, Decl = d };
            sym.TypeParams.AddRange(d.TypeParams.Select(p => p.Name));
            _r.Types[key] = sym;
        }

        // AND NOW THE TUPLE NAMINGS, which needed the types first.
        //
        // A shape is named by what its elements ARE, so resolving
        // `(ParamSymbol First, ParamSymbol Second)` before ParamSymbol was
        // declared found nothing and remembered nothing -- and `p.First` over a
        // Zip of two of them was told the tuple has no such member. It went
        // unseen while every named shape in the tree was made of primitives,
        // which need no table to resolve.
        //
        // QUIETLY: some of them are open -- `(T, U)` inside a template -- and
        // what is wanted from them is the NAMES, not a resolution. A shape that
        // cannot be resolved has nothing to remember and says so to nobody.
        _quiet++;

        foreach (TypeRef naming in unit.TupleNamings)
        {
            try { Resolve(naming, null); }
            catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
        }

        _quiet--;

        foreach (TypeDecl d in unit.Types)
        {
            if (d.Kind == TypeKind.Enum && _r.Types.TryGetValue(TypeKey(d), out TypeSymbol? enumSym) && ReferenceEquals(enumSym.Decl, d))
            {
                _in = d.File;
                EnumUnderlying(d, enumSym);
            }
        }

        foreach (TypeDecl d in unit.Types)
        {
            // A TEMPLATE GETS ITS MEMBERS DECLARED, because a generic method
            // written over `List<T>` reads them: `values.Count` and
            // `values[i]` are how string.Join is written. It gets no bodies
            // checked, no layout and no code -- see the passes below.
            if (_r.Types.TryGetValue(TypeKey(d), out TypeSymbol? sym) && ReferenceEquals(sym.Decl, d))
            {
                _in = d.File;
                DeclareMembers(d, sym);
            }
        }

        // And the ones extending a prelude type, onto the symbol that already
        // exists. Everything after this walks SYMBOLS, so from here on there is
        // no difference between a member declared in the prelude and one
        // declared beside it.
        foreach ((TypeDecl d, TypeSymbol sym) in extend)
        {
            _in = d.File;
            DeclareMembers(d, sym);
        }

        // Missing signatures must never reach layout or body checking. Gather
        // independent requests from this phase and retry the whole transaction
        // once, rather than rereading every source for each missing type.
        _declarationBatch.ThrowIfAny();

        // Every owner, base and enum is now known. Resolve constants before
        // checking bodies or emitting headers, without changing declaration
        // order for runtime static initializers or field/vtable layout.
        foreach (var key in _constantDeclarations.Keys)
            EvaluateConstant(key.Owner, key.Name);

        // Interface methods get slot numbers FIRST, from one program-wide
        // counter. A call through an interface has no idea which class it will
        // land on, so the slot has to mean the same thing in every class that
        // implements it — numbering them globally guarantees that at the cost
        // of some empty slots, which is the cheapest thing we have.
        // SLOT ZERO IS ToString, ON EVERY CLASS, whether it declares one or not.
        //
        // `"" + anything` calls ToString in C#, and the caller usually has no
        // idea what it is holding -- a `List<T>` printing its elements knows
        // only that they are T, and after canonical instantiation not even
        // that. So the call has to be virtual at a slot that means the same
        // thing on every class, which is the identical problem interface
        // methods have and gets the identical answer.
        //
        // A class that declares no ToString gets one anyway, pointing at the
        // shared stub that answers with the type's name -- which is what
        // object.ToString() does in C#. See Codegen.ObjectToStringStub.
        _r.ToStringSlot = _interfaceSlots++;
        _r.EqualsSlot = _interfaceSlots++;
        _r.HashSlot = _interfaceSlots++;
        _r.CompareSlot = _interfaceSlots++;

        // EVERY INSTANTIATION OF ONE INTERFACE TEMPLATE SHARES ITS SLOTS.
        //
        // `Func<Node,bool>` and `Func<__canon,bool>` are two types and ONE
        // template, and the code that calls Invoke is compiled once against the
        // canonical one. Numbering them separately put the closure's Invoke at
        // the slot Func$Node$bool was given and the call at the slot
        // Func$__canon$bool was given, so the shared code read an empty slot
        // and jumped to address zero.
        //
        // Keyed by the template's name and how many type arguments it takes --
        // which is exactly how the monomorphiser keys a template -- so
        // Func<A,R> and Func<A,B,R> stay apart while their instantiations come
        // together.
        Dictionary<(string, int, int), int> shared = new();

        (string Template, int Arity) Family(TypeSymbol type)
        {
            TypeDecl? declaration = type.Decl;
            string name = declaration?.Template ?? type.Name;
            if (declaration?.Outer is string outer) name = outer + "." + name;
            int arity = declaration?.Template is null ? declaration?.TypeParams.Count ?? 0 : declaration.TemplateArgs.Count;
            return (name, arity);
        }

        // IMPORTED GENERIC TEMPLATES ALREADY HAVE AN ABI. Reserve every slot
        // their GIR selected before allocating slots for interfaces declared
        // by this image. Otherwise the consumer numbers the same canonical
        // interface according to whichever unrelated interfaces its own
        // sources happen to instantiate, then discovers the disagreement only
        // after binding -- exactly the failure the GIR check is meant to stop.
        foreach (TypeSymbol t in _r.Types.Values.Where(t => t.Kind == TypeKind.Interface && !IsTemplate(t)))
        {
            (string template, int arity) = Family(t);

            for (int i = 0; i < t.Methods.Count; i++)
            {
                int wanted = t.Methods[i].Decl?.VtableSlotHint ?? -1;
                if (wanted < 0) continue;

                shared[(template, arity, i)] = wanted;
                t.Methods[i].VtableSlot = wanted;
                _interfaceSlots = Math.Max(_interfaceSlots, wanted + 1);
            }
        }

        // NUMBERED OVER THE DECLARATIONS, NOT OVER THIS COMPILATION'S
        // INSTANTIATIONS -- which is what makes the numbering an ABI two
        // images can share.
        //
        // The slots used to be handed out as the interfaces came up in the
        // type table, so a template first met through `IReadOnlyList$int`
        // was numbered where THAT specialisation happened to sit. A shared
        // object and a program that links it make different sets of
        // instantiations out of the same sources, and the two then disagreed
        // about where an interface method is dispatched: the program called
        // slot 7 of a List the library had built with the implementation in
        // slot 5, and found a hole.
        //
        // So: every interface DECLARED in the sources gets its block, in an
        // order that depends on nothing but the sources -- the template's
        // name and how many type arguments it takes, ordinally. Every build
        // is given the same sources in the same order (see
        // os/build-libs.sh), so every build hands out the same numbers,
        // whether or not it instantiates the interface at all.
        //
        // ASKED OF THE DECLARATION, never counted out of the mangled name.
        // A specialisation says what it was made from and what it was made
        // with; reading it back out of `IReadOnlyList$ImageFile$Section` by
        // counting separators makes that a template of arity two, gives it
        // slots of its own, and every slot after it in the program moves.
        // IN TWO TIERS. The library's interfaces (stdlib, runtime) are the ABI
        // every image shares, and they come first, in an order that depends on
        // the library sources alone. A PROJECT's own interfaces come after the
        // library's CLASSES as well, because a library class's virtuals are
        // numbered straight after the library's interface region, and a
        // program that declared `ICells` -- sorting before IComparable -- used
        // to push every stdlib slot after it along by five: its Form was built
        // with SetBounds in a slot the library called by another number, and
        // the library's constructor jumped to nought.
        SortedDictionary<(string, int), int> families = new(), local = new();
        bool IsLibraryFamily((string, int) family, bool declaredHere)
            => _libraryInterfaces is null ? true : _libraryInterfaces.Contains(family) && !declaredHere;
        if (_indexedInterfaces is not null)
            foreach (var family in _indexedInterfaces)
                (IsLibraryFamily(family.Key, false) ? families : local)[family.Key] = family.Value;
        foreach (TypeSymbol t in _r.Types.Values.Where(t => t.Kind == TypeKind.Interface))
        {
            (string, int) family = Family(t);
            bool library = IsLibraryType(t) || (_libraryInterfaces?.Contains(family) ?? true);
            SortedDictionary<(string, int), int> into = library ? families : local;
            if (library) local.Remove(family);
            into[family] = Math.Max(into.GetValueOrDefault(family), t.Methods.Count);
        }

        void Number(SortedDictionary<(string, int), int> table)
        {
            foreach (((string template, int arity) family, int methods) in table)
            {
                for (int i = 0; i < methods; i++)
                {
                    if (!shared.ContainsKey((family.template, family.arity, i)))
                    {
                        shared[(family.template, family.arity, i)] = _interfaceSlots++;
                    }
                }
            }
        }

        void Assign(bool library)
        {
            foreach (TypeSymbol t in _r.Types.Values.Where(t => t.Kind == TypeKind.Interface && !IsTemplate(t)))
            {
                (string template, int arity) = Family(t);
                if ((IsLibraryType(t) || (_libraryInterfaces?.Contains((template, arity)) ?? true)) != library) continue;

                for (int i = 0; i < t.Methods.Count; i++)
                {
                    if (t.Methods[i].VtableSlot >= 0)
                    {
                        continue;
                    }

                    if (!shared.TryGetValue((template, arity, i), out int slot))
                    {
                        slot = _interfaceSlots++;
                        shared[(template, arity, i)] = slot;
                    }

                    t.Methods[i].VtableSlot = slot;
                }
            }
        }

        Number(families);
        Assign(true);
        // The table this unit numbered over, for diffing the two sides of a
        // link that stops with a layout conflict: a family present on one
        // side only moves every slot after it. Set CORC_DUMP_FAMILIES.
        if (Environment.GetEnvironmentVariable("CORC_DUMP_FAMILIES") is not null)
        {
            Console.Error.WriteLine("families library=" + families.Count + " project=" + local.Count
                + " slots=" + _interfaceSlots);
            foreach (((string template, int arity), int methods) in families)
                Console.Error.WriteLine("  lib " + template + "`" + arity + " methods=" + methods);
            foreach (((string template, int arity), int methods) in local)
                Console.Error.WriteLine("  project " + template + "`" + arity + " methods=" + methods);
        }
        _librarySlots = _interfaceSlots;

        if (local.Count > 0)
        {
            // The project's interfaces start a fixed distance above the
            // library's interface region: room for the library's classes,
            // whose virtuals are numbered from that region. A FIXED distance,
            // not the highest slot of the library classes this unit happens
            // to have loaded, because every unit of a program must give the
            // same interface the same number, and units load different
            // classes. The cost is empty entries in the tables of the
            // project's own classes, per type and not per object.
            _interfaceSlots = _librarySlots + LibraryClassReserve;
            Number(local);
            Assign(false);
        }

        // ONE BIT PER TYPE, in an ancestor mask that is as many words wide as
        // the program needs.
        //
        // It was one word, and a program was silently capped at 64 classes and
        // interfaces. That held right up until the compiler was asked to
        // compile ITSELF, which declares several hundred -- so the cap was
        // never a design decision so much as a first draft.
        //
        // Widening costs nothing at a type test, because the bit index is known
        // when the test is compiled: bit 200 is word 3, bit 8, and the machine
        // reads exactly that word. Still two loads and an AND, still no walk of
        // the hierarchy, still no worst case. What it costs is mask words in
        // front of each vtable, which is per TYPE and not per object.
        // DEPTH IN THE CHAIN, which is what format v6 tests against and what
        // replaces the bit below.
        //
        // Counted by walking a type's own bases and nothing else, so it does
        // not move when an unrelated file declares a class and a library and
        // its consumers cannot disagree about it. An interface stays -1: they
        // do not form a chain, and the interface array is searched instead.
        foreach (TypeSymbol t in _r.Types.Values
                     .Where(t => t.Kind is TypeKind.Class or TypeKind.Interface && !IsTemplate(t)))
        {
            if (t.Kind == TypeKind.Interface)
            {
                t.Depth = -1;
                continue;
            }

            int deep = 0;

            for (TypeSymbol? a = t.Base; a != null; a = a.Base)
            {
                deep++;
            }

            t.Depth = deep;
        }

        // NOTHING COUNTS TYPES ANY MORE.
        //
        // A whole numbering pass lived here: one bit per class and interface,
        // a ceiling to check it against, a mask width derived from the total,
        // and a negotiation with every library the program linked so they could
        // agree on that width. All of it existed to make a type nameable in an
        // instruction's immediate, and all of it is deleted -- a type is named
        // by the address of its descriptor now, which is a relocation like any
        // other and needs no agreement with anybody.
        //
        // Depth, assigned above, is what replaced it: a property of a type and
        // its own bases, so it cannot move when an unrelated file declares a
        // class and two separately compiled things cannot disagree about it.

        // A TEMPLATE IS NOT LAID OUT and gets no type bit, no vtable slots and
        // no code. It is here to be NAMED -- a generic method declared over
        // `List<T>` needs `List` to be a type the checker knows -- and nothing
        // else about it is real until it is specialised.
        foreach (TypeSymbol sym in _r.Types.Values.Where(t => !IsTemplate(t)))
        {
            LayOut(sym);
        }

        foreach (TypeDecl d in unit.Types)
        {
            // A TEMPLATE'S BODIES ARE NOT CHECKED, for the same reason a generic
            // method's are not: `match(value)` inside `List<T>.Find` calls
            // Invoke on an open `Func<T,bool>`, whose parameter still says A
            // because nothing has substituted anything yet. The COPY made for
            // each set of type arguments is concrete, and that is what gets
            // checked.
            //
            // This used to fall out of a lookup that missed -- templates are
            // keyed by name and arity, and this loop asked for the bare name --
            // so it held only as long as nothing keyed them properly.
            if (_r.Types.TryGetValue(TypeKey(d), out TypeSymbol? sym)
                && ReferenceEquals(sym.Decl, d) && !IsTemplate(sym))
            {
                _bodyWork.Add((d, sym));
            }
        }

        // AND THE BODIES OF THE EXTENDING DECLARATIONS, which the loop above
        // cannot reach: it asks whether the symbol's Decl IS this declaration,
        // and for an extending one the symbol belongs to the prelude.
        //
        // Missing this does not fail here. It fails in the CODE GENERATOR,
        // which meets locals nobody bound and calls nobody resolved -- and says
        // so as "this assignment target is not implemented yet" for `i++` and
        // "this call did not resolve to a method" for a sibling static, both
        // labelled <prelude>. Three misleading messages, one cause.
        foreach ((TypeDecl d, TypeSymbol sym) in extend)
        {
            _bodyWork.Add((d, sym));
        }
    }

    private void CheckBodyWork()
    {
        // Still serial until synthetic symbols and binding results have
        // isolated ownership and an ordered merge. Do not parallelize the
        // existing shared BindResult by merely wrapping this loop in Tasks.
        for (int ordinal = 0; ordinal < _bodyWork.Count; ordinal++)
        {
            var work = _bodyWork[ordinal];
            // ONE MEMBER'S MISSING TYPE MUST NOT COST A WHOLE REBUILD. A body
            // names types no signature mentioned -- devfs names Tty, Vga, Arch
            // and a dozen more -- and demanding them one at a time threw the
            // unit away once per name. Checking continues to the next member
            // with the request recorded; what this pass then reports is
            // discarded with the transaction, so only the requests survive.
            try { CheckBodyItem(work.Decl, work.Symbol, ordinal); }
            catch (Metadata.DeclarationDemand demand)
            {
                _declarationBatch.Add(demand);
                _member = null;
                _signature = null;
                _quiet = 0;
            }
        }
        _bodyWork.Clear();
        _declarationBatch.ThrowIfAny();
    }

    private void CheckBodyItem(TypeDecl declaration, TypeSymbol symbol, int ordinal)
    {
        _closures = 0;
        _in = declaration.File;
        CheckBodies(declaration, symbol);
    }

    /// <summary>
    /// Turns `int n = 5;` and `List&lt;T&gt; xs { get; } = new();` into statements
    /// at the top of every constructor.
    ///
    /// WHERE C# RUNS THEM, and in C#'s order: before the constructor's body and
    /// after any `: base(...)` it chains to -- which falls out here, because
    /// the chain is a separate field on the declaration and this only touches
    /// the body.
    ///
    /// They used to be REFUSED, and the refusal was right at the time: an
    /// initialiser accepted and then dropped reads back zero, and the program
    /// looks correct. There was nowhere to run one. There is now, for an
    /// INSTANCE field, and that is the constructor.
    ///
    /// A STATIC field is still refused. It wants a static constructor and an
    /// order between classes, which is a different problem and not one anything
    /// needs yet.
    /// </summary>
    /// <summary>
    /// Moves a type's STATIC field initialisers into a method that runs before
    /// the program does.
    ///
    /// `public static readonly Type Void = new() { ... };` -- which is how this
    /// compiler's own Types.cs names the fifteen primitive types, and how
    /// anybody writes a table of constants that are not constants.
    ///
    /// C# runs a type's static initialisers LAZILY, the first time anything
    /// touches the type, exactly once, and that is what this does: the
    /// generated method claims an owner-tagged state before running its body.
    /// Other threads wait for completion or a cached failure; recursive entry
    /// by the owner and type-initialization wait cycles may see partial values.
    ///
    /// They USED TO RUN IN SOURCE ORDER FROM THE ENTRY STUB, and that is what
    /// made this urgent: in a freestanding image the entry stub runs before
    /// there is a heap, so a `static byte[] table = new byte[24];` in any file
    /// the kernel linked brought the kernel down at entry. Deferring to first
    /// use puts the allocation after Main has started, where the heap is.
    /// </summary>
    private void StaticInitialisers(TypeDecl d)
    {
        if (d.Kind is TypeKind.Interface or TypeKind.Enum)
        {
            return;
        }

        // Binding is deliberately iterative: a generic method discovered in
        // the first pass is specialised, and then the expanded tree is bound
        // again.  The first pass has already moved a field initializer into
        // StaticInit$ and cleared FieldDecl.Init, so looking only for fields on
        // the second pass quietly loses the call from the program entry stub.
        //
        // A shared library arrives in exactly that already-lowered form: its
        // header declares StaticInit$ while the body remains in the library.
        // It must participate too.  Per-process library statics otherwise
        // retain their zero-fill values -- Console.Out was null before the
        // first user statement, and Path's directory separator was NUL.
        //
        // The method is compiler generated and reserved, so one is sufficient
        // and it is the durable record that this type has startup work.
        if (d.Members.OfType<MethodDecl>().Any(m => m.Name == "StaticInit$"
                                                   && m.Mods.HasFlag(Mods.Static)))
        {
            FieldDecl? state = d.Members.OfType<FieldDecl>().FirstOrDefault(f => f.Name == BindResult.ReadyField);
            if (state?.Type.Name != "int")
                Error(d, $"'{d.Name}' has an obsolete static-initialization state; rebuild its library/header");
            _r.StaticInits.Add(d.Name);
            return;
        }

        // Imported declarations cannot acquire a new initializer here: the
        // owning library alone lays out and executes its static state.
        if (d.External)
        {
            return;
        }

        List<Stmt> body = new();
        List<MethodDecl> initializerMethods = new();

        foreach (MemberDecl m in d.Members)
        {
            // A CONST IS NOT STORAGE, exactly as in the instance case: the
            // value goes wherever the name appears and there is nothing to
            // assign to.
            Expr initial;
            TypeRef type;
            string field;
            if (m is FieldDecl f && f.Init is not null && f.Mods.HasFlag(Mods.Static) && !f.Mods.HasFlag(Mods.Const))
            {
                initial = f.Init; type = f.Type; field = f.Name; f.Init = null;
            }
            else if (m is PropertyDecl p && p.Init is not null && p.Auto && p.Mods.HasFlag(Mods.Static))
            {
                initial = p.Init; type = p.Type; field = "<" + p.Name + ">"; p.Init = null;
            }
            else continue;

            body.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new NameExpr { Name = field, Line = m.Line, Col = m.Col },
                    Value = InitializerMethods.Value(m, type, Retarget(initial, type), initializerMethods),
                    Line = m.Line, Col = m.Col,
                },
                Line = m.Line, Col = m.Col,
            });
        }
        d.Members.AddRange(initializerMethods);

        // A C# static constructor runs after every static field initializer.
        // Before this lowering existed, `static Isa()` was parsed correctly but
        // never had a caller: constructors normally run from `new`, and a
        // static one has no object to construct.  Merge its statements into
        // the generated startup method, after the field assignments above,
        // which is precisely the language order.
        //
        // The source method is intentionally left in the tree.  It has no
        // normal call path (a static constructor is not callable source API),
        // and retaining it preserves the AST for later passes without causing
        // a second execution.
        MethodDecl? cctor = d.Members.OfType<MethodDecl>().FirstOrDefault(
            m => m.IsCtor && m.Mods.HasFlag(Mods.Static));

        if (cctor?.Body is { } cctorBody)
        {
            // Keep the source body with its source unit. The type owner runs
            // the shared initialization protocol and calls this ordinary
            // hidden helper, rather than importing the whole constructor body.
            d.Members.Remove(cctor);
            d.Members.Add(new MethodDecl
            {
                Name = "StaticConstructorBody$", Mods = Mods.Static | Mods.Private,
                Returns = new TypeRef { Name = "void" }, Body = cctorBody,
                File = cctor.File, Line = cctor.Line, Col = cctor.Col,
                Scope = cctor.Scope, Namespace = cctor.Namespace,
                OwnedImplementation = cctor.OwnedImplementation,
            });
            body.Add(new ExprStmt { Expr = new CallExpr { Target = new NameExpr { Name = "StaticConstructorBody$" } } });
        }

        if (body.Count == 0)
        {
            return;
        }

        AddSynchronizedInitializer(d, body);
        _r.StaticInits.Add(d.Name);
    }

    private void Initialisers(TypeDecl d)
    {
        if (d.Kind is TypeKind.Interface or TypeKind.Enum || d.Mods.HasFlag(Mods.Static)
            || d.InitialisersPlaced)
        {
            return;
        }

        d.InitialisersPlaced = true;

        List<Stmt> prologue = new();
        List<MethodDecl> initializerMethods = new();

        foreach (MemberDecl m in d.Members)
        {
            Expr? init;
            string field;
            TypeRef declared;

            switch (m)
            {
                // A CONST IS NOT STORAGE, so its "initialiser" is not one: the
                // value is written out wherever the name appears and there is
                // nothing to assign to. It is also not marked static -- `const`
                // implies it without saying so -- which is exactly how this
                // came to try `this.StackBytes = 16384;` and report that only
                // fields can be assigned through a member access.
                case FieldDecl f when f.Init != null
                                   && !f.Mods.HasFlag(Mods.Static)
                                   && !f.Mods.HasFlag(Mods.Const):
                    init = f.Init;
                    field = f.Name;
                    declared = f.Type;
                    break;

                // An AUTO-PROPERTY writes its backing field, which is the one
                // the binder invents for it and spells with angle brackets.
                case PropertyDecl p when p.Init != null && p.Auto && !p.Mods.HasFlag(Mods.Static):
                    init = p.Init;
                    field = "<" + p.Name + ">";
                    declared = p.Type;
                    break;

                default:
                    continue;
            }

            prologue.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new MemberExpr
                    {
                        Target = new ThisExpr { Line = m.Line, Col = m.Col },
                        Name = field, Line = m.Line, Col = m.Col,
                    },
                    Value = InitializerMethods.Value(m, declared, Retarget(init, declared), initializerMethods),
                    Line = m.Line, Col = m.Col,
                },
                Line = m.Line, Col = m.Col,
            });
        }

        d.Members.AddRange(initializerMethods);
        if (prologue.Count == 0)
        {
            return;
        }

        List<MethodDecl> ctors = d.Members.OfType<MethodDecl>().Where(c => c.IsCtor && !c.Mods.HasFlag(Mods.Static)).ToList();

        // A TYPE WITH NO CONSTRUCTOR STILL NEEDS ONE, or its initialisers have
        // nowhere to go -- and most of the types that use this shape are plain
        // records of fields that nobody wrote a constructor for.
        if (ctors.Count == 0)
        {
            MethodDecl made = new()
            {
                Name = d.Name, IsCtor = true, Mods = Mods.Public,
                Body = new Block { Line = d.Line, Col = d.Col },
                Line = d.Line, Col = d.Col, File = d.File,
                OwnedImplementation = !d.Elsewhere,
            };

            d.Members.Add(made);
            ctors.Add(made);
        }

        // NOT INTO ONE THAT HANDS OVER TO ANOTHER OF ITS OWN. `Defs(Function f)
        // : this(new Cfg(f))` has its fields initialised by the constructor it
        // chains to, once, and C# puts them nowhere else. Put into both, the
        // second set ran AFTER the chained constructor had filled the tables
        // and emptied them again.
        foreach (MethodDecl c in ctors.Where(c => c.Init is not { IsThis: true }))
        {
            c.Body?.Statements.InsertRange(0, prologue);
        }
    }

    /// <summary>
    /// Fills in the type of a target-typed <c>new()</c> from the declaration
    /// it is initialising, and leaves everything else alone.
    /// </summary>
    private static Expr Retarget(Expr init, TypeRef declared)
    {
        // A collection expression is made for its type by the checker, which
        // knows what the type is (the assignment this becomes wants it).
        if (init is not NewExpr nw || nw.Type.Name.Length != 0 || nw.Collection)
        {
            return init;
        }

        NewExpr made = new()
        {
            Type = declared, ArraySize = nw.ArraySize, Line = nw.Line, Col = nw.Col,
        };

        made.Args.AddRange(nw.Args);
        made.ArgNames.AddRange(nw.ArgNames);
        made.Elements = nw.Elements;
        made.Inits.AddRange(nw.Inits);
        made.Adds.AddRange(nw.Adds);
        made.Indexes.AddRange(nw.Indexes);
        return made;
    }

    private void DeclareMembers(TypeDecl d, TypeSymbol sym)
    {
        // A SIGNATURE IS WRITTEN INSIDE THIS TYPE, so a name in one may be a
        // type this type holds: `private Section _section;` in Assembler names
        // Assembler's own Section. Bodies get the same courtesy further down,
        // from _thisType; a signature is read before there is a body to be in.
        TypeSymbol? wasScope = _scope;
        _scope = sym;

        try
        {
            DeclareMembersIn(d, sym);
        }
        catch (Metadata.DeclarationDemand demand)
        {
            _declarationBatch.Add(demand);
            _member = null;
            _signature = null;
        }
        finally
        {
            _scope = wasScope;
        }
    }

    /// <summary>
    /// Whether a base type of this declaration already declares a property by
    /// this name.
    ///
    /// Asked of the DECLARATIONS rather than of the symbols, because members
    /// are declared one type at a time and in no particular order: the base's
    /// symbol may hold nothing yet, while the text of it is all there.
    /// </summary>
    private bool InheritsProperty(TypeDecl d, string name)
    {
        for (TypeDecl? up = BaseDeclOf(d); up != null; up = BaseDeclOf(up))
        {
            if (up.Members.Exists(m => m is PropertyDecl && m.Name == name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The declaration of a type's base class, or null for an interface or nothing.</summary>
    private TypeDecl? BaseDeclOf(TypeDecl d)
    {
        foreach (TypeRef b in d.Bases)
        {
            if ((FindType(b.Name, out TypeSymbol? based)
                 || b.Args.Count > 0 && FindType(Arity(b.Name, b.Args.Count), out based))
                && based is not null && based.Kind != TypeKind.Interface)
            {
                return based.Decl;
            }
        }
        return null;
    }

    /// <summary>
    /// WHAT FOLLOWS THE COLON ON AN ENUM IS NOT A BASE CLASS.
    ///
    /// It is the UNDERLYING TYPE -- how wide each member is stored -- and it
    /// is always one of the integer primitives. Resolved through the same
    /// path as a base class it was looked for among the declared types and
    /// reported as 'byte' is not a known type, which is true and is not the
    /// question being asked.
    ///
    /// SETTLED FOR EVERY ENUM BEFORE ANY MEMBER IS DECLARED: a type
    /// declared earlier that takes the enum as a parameter resolves the enum's
    /// type then, and the type an enum resolves to carries its width. A
    /// `ref Size` parameter taken as an int-wide Size refused a byte-wide
    /// Size argument -- as it did in a project, where declaration order is
    /// the index's.
    /// </summary>
    private void EnumUnderlying(TypeDecl d, TypeSymbol sym)
    {
        foreach (TypeRef u in d.Kind == TypeKind.Enum ? d.Bases : Enumerable.Empty<TypeRef>())
        {
            if (Underlying(u.Name) is Prim held)
            {
                sym.EnumUnderlying = held;
            }
            else
            {
                Error(u, $"an enum's underlying type must be an integer; '{u.Name}' is not one");
            }
        }
    }

    private void DeclareMembersIn(TypeDecl d, TypeSymbol sym)
    {
        // An enum's underlying type was settled before any members were
        // declared (EnumUnderlying), so the checks below find it in place.

        // CHECKED AND THEN FALLS THROUGH, rather than returning: the member
        // values are worked out further down this same method, and returning
        // here skipped them -- so every enum compiled to a type with no members
        // at all, and every use of one was 'ExitCode has no member Success'.
        foreach (TypeRef b in d.Kind == TypeKind.Enum ? Enumerable.Empty<TypeRef>() : d.Bases)
        {
            // FROM WHERE THE TYPE WAS WRITTEN, like any other name: a base in
            // the same namespace is named without it, and one in an imported
            // namespace is reached through the using that imported it.
            //
            // A GENERIC BASE IS KEYED BY ARITY, like any other template:
            // `List<T> : IReadOnlyList<T>` names IReadOnlyList`1. The bare
            // name is tried first, because a base is usually a plain class.
            if (!FindType(b.Name, out TypeSymbol? based)
                && !(b.Args.Count > 0 && FindType(Arity(b.Name, b.Args.Count), out based)))
            {
                Error(b, $"'{b.Name}' is not a known type");
                continue;
            }

            if (based is null)
            {
                continue;
            }

            if (based.Kind == TypeKind.Interface)
            {
                sym.Interfaces.Add(based);
            }
            else if (sym.Base != null)
            {
                Error(b, $"'{sym.Name}' already has base class '{sym.Base.Name}'; only one is allowed");
            }
            else
            {
                sym.Base = based;
            }
        }

        if (d.Kind == TypeKind.Enum)
        {
            // C# spells it either way, and `[Flags]` is what everybody writes.
            sym.IsFlags = d.Attributes.Contains("Flags")
                       || d.Attributes.Contains("FlagsAttribute");

            long next = 0;

            foreach (EnumMember m in d.EnumMembers)
            {
                // A CONSTANT EXPRESSION, not merely a literal.
                //
                // `Public = 1 << 0` is how every flags enum in existence is
                // written, including the one in this compiler's own Ast.cs, and
                // demanding a bare literal meant writing out the powers of two
                // by hand. The folder that already exists for constants answers
                // this exactly -- it is the same question.
                if (m.Value != null)
                {
                    if (Fold.TryConst(m.Value, out long folded))
                    {
                        next = folded;
                    }
                    else
                    {
                        Error(m, "an enum member's value must be a constant the compiler can work out");
                    }
                }
                sym.EnumValues[m.Name] = next++;
            }
            return;
        }

        foreach (MemberDecl m in d.Members)
        {
            _member = m;
            try
            {
            switch (m)
            {
                case FieldDecl f:
                {
                    // A const is a NAME FOR A NUMBER and not storage: every use
                    // becomes the value itself. It was being laid out as an
                    // ordinary field with its initialiser dropped, so reading one
                    // from a static method dereferenced a null 'this' — a named
                    // constant that crashed at the point of use.
                    if (f.Mods.HasFlag(Mods.Const))
                    {
                        if (!_constantDeclarations.TryAdd((sym, f.Name),
                            (f, f.File.Length > 0 ? f.File : d.File)))
                            Error(f, $"'{f.Name}' is declared more than once");
                        break;
                    }

                    // NOTHING LEFT TO REFUSE. Both kinds of field initialiser
                    // have been moved by the time this runs -- an instance one
                    // into every constructor, a static one into the type's
                    // StaticInit$, which the code generator calls before Main.
                    // This used to report that a static field could not be
                    // initialised here, which was true and is not any more.
                    // ONE NAME, ONE FIELD. C# refuses a second (CS0102); taken
                    // quietly, the two became one -- whichever the lookup found
                    // first -- and a driver's sixty-four-slot transmit queue was
                    // written into its sixteen-slot receive queue, because both
                    // had been called `_queue` four hundred lines apart.
                    if (sym.Fields.Any(had => had.Name == f.Name)
                        || sym.Fields.Any(had => had.Name == "<" + f.Name + ">"))
                    {
                        Error(f, $"'{sym.Name}' already has a member called '{f.Name}'");
                        break;
                    }

                    sym.Fields.Add(new FieldSymbol
                    {
                        Name = f.Name, Type = Resolve(f.Type, sym), Owner = sym,
                        Static = f.Mods.HasFlag(Mods.Static),
                        Volatile = f.Mods.HasFlag(Mods.Volatile),
                        Required = f.Mods.HasFlag(Mods.Required),
                    });
                    break;
                }

                case PropertyDecl p:
                {
                    // A RECORD'S POSITIONAL PARAMETER MAKES A PROPERTY UNLESS
                    // ONE IS INHERITED. `record RegPlace(VReg Reg, Type Type) :
                    // Place(Type)` hands Type to Place, which declares it, and
                    // C# synthesizes nothing for it -- a second property of the
                    // same name would hide the first and the object would carry
                    // the value twice. The constructor still assigns it: the
                    // one it assigns is the base's.
                    //
                    // Only a SYNTHESIZED property is dropped this way. One
                    // somebody wrote is theirs, and hiding a base member with
                    // it is their business.
                    if (p.FromRecord && InheritsProperty(d, p.Name))
                    {
                        break;
                    }

                    Type propType = Resolve(p.Type, sym);

                    // AN INTERFACE HAS NO STORAGE, so `int Count { get; }` on
                    // one is not an auto property at all -- it is an abstract
                    // accessor, and the class implementing it provides the
                    // body. Treated as a field, it produced no get_Count and
                    // no get_Item, so an IReadOnlyList could be neither counted
                    // nor indexed and LINQ could not be declared over it.
                    // AN ABSTRACT PROPERTY HAS NO STORAGE EITHER. `public
                    // abstract bool Exists { get; }` parses exactly like an
                    // auto property -- accessors with no bodies -- and was
                    // being laid out as a field named <Exists> on the abstract
                    // class. The override in the derived class then wrote a
                    // real get_Exists nobody called: every read of the property
                    // returned the never-assigned field, so an abstract bool
                    // was false however it was overridden, through the derived
                    // type and through the base alike.
                    bool abstractAccessors =
                        sym.Kind == TypeKind.Interface || p.Mods.HasFlag(Mods.Abstract);

                    Block? getBody = p.Getter;
                    Block? setBody = p.Setter;

                    if (p.Auto && !abstractAccessors)
                    {
                        // An auto property is a field plus accessors, and the
                        // field is what layout and code generation use: `x.N`
                        // finds it and loads it, with no call at all.
                        sym.Fields.Add(new FieldSymbol
                        {
                            Name = "<" + p.Name + ">", Type = propType, Owner = sym,
                            Static = p.Mods.HasFlag(Mods.Static),
                            Required = p.Mods.HasFlag(Mods.Required),
                        });

                        // AND THE ACCESSORS ARE REAL METHODS, because an
                        // INTERFACE ASKS FOR THEM BY NAME. `interface IA { int
                        // N { get; set; } }` declares a get_N and a set_N, and
                        // `class Impl : IA { public int N { get; set; } }` --
                        // which is how nearly every C# class satisfies a
                        // property -- had only a field, so it was reported as
                        // not implementing IA.get_N and could not be written
                        // any way but longhand.
                        //
                        // Nothing else reaches them: a read through the class
                        // finds the field first, and a class that implements no
                        // interface never calls them, so dead-function removal
                        // drops the pair.
                        getBody = new Block { Line = p.Line, Col = p.Col };
                        getBody.Statements.Add(new ReturnStmt
                        {
                            Value = new NameExpr { Name = p.Name, Line = p.Line, Col = p.Col },
                            Line = p.Line, Col = p.Col,
                        });

                        if (p.HasSetter)
                        {
                            setBody = new Block { Line = p.Line, Col = p.Col };
                            setBody.Statements.Add(new ExprStmt
                            {
                                Expr = new AssignExpr
                                {
                                    Target = new NameExpr { Name = p.Name, Line = p.Line, Col = p.Col },
                                    Value = new NameExpr { Name = "value", Line = p.Line, Col = p.Col },
                                    Line = p.Line, Col = p.Col,
                                },
                                Line = p.Line, Col = p.Col,
                            });
                        }
                    }

                    // A property with a body is a METHOD. Reading it has to run
                    // that body, so it cannot be a field with a nice name.
                    // AN INTERFACE'S ACCESSOR HAS NO BODY and still exists.
                    // `int Count { get; }` declares a get_Count that whoever
                    // implements the interface provides; there is nothing to
                    // run here and everything to call.
                    bool declared = abstractAccessors;

                    // AN ACCESSOR CARRIES THE PROPERTY'S OWN MODIFIERS. It is a
                    // method invented here, and without virtual/override/abstract
                    // on it the slot loop below skipped it -- so an overriding
                    // get_Exists took a slot of its own instead of the base's,
                    // and a call through the base type went to the abstract one.
                    bool aVirtual = p.Mods.HasFlag(Mods.Virtual);
                    bool aOverride = p.Mods.HasFlag(Mods.Override);
                    bool aAbstract = p.Mods.HasFlag(Mods.Abstract) || sym.Kind == TypeKind.Interface;

                    if (getBody != null || declared)
                    {
                        // THE TEMPLATE INDEX COMES ACROSS WITH IT, on this and
                        // on the setter below.
                        //
                        // An accessor is invented here rather than parsed, so
                        // nothing else can tell which member of a generic
                        // template it belongs to -- and without that, a shared
                        // specialisation cannot find its own code. It presented
                        // as `nothing provides m_List$long_get_Count_0`: every
                        // ordinary method of List had been redirected to the
                        // canonical copy and the property had not.
                        MethodDecl getter = new()
                        {
                            Name = "get_" + p.Name, Mods = p.Mods, Returns = p.Type,
                            Body = getBody, Line = p.Line, Col = p.Col,
                            TemplateIndex = p.TemplateIndex,
                            VtableSlotHint = p.VtableSlotHint,
                            OwnedImplementation = p.OwnedImplementation, File = p.File, Scope = p.Scope, Namespace = p.Namespace,
                        };

                        MethodSymbol gs = new()
                        {
                            Name = getter.Name, Returns = propType, Owner = sym,
                            Static = p.Mods.HasFlag(Mods.Static), Decl = getter,
                            Virtual = aVirtual, Override = aOverride, Abstract = aAbstract,
                        };

                        // AN INDEXER'S PARAMETERS COME FIRST, which is the whole
                        // of what makes get_Item different from get_Name.
                        foreach (Param ip in p.Params)
                        {
                            getter.Params.Add(ip);
                            gs.Params.Add(new ParamSymbol { Name = ip.Name, Type = Resolve(ip.Type, sym) });
                        }
                        sym.Methods.Add(gs);
                        _r.Methods[getter] = gs;
                        _synthesised.Add(getter);
                    }

                    if (setBody != null || (declared && p.HasSetter))
                    {
                        MethodDecl setter = new()
                        {
                            Name = "set_" + p.Name, Mods = p.Mods, Returns = null,
                            Body = setBody, Line = p.Line, Col = p.Col,
                            TemplateIndex = p.TemplateIndex,
                            VtableSlotHint = p.VtableSlotHint,
                            OwnedImplementation = p.OwnedImplementation, File = p.File, Scope = p.Scope, Namespace = p.Namespace,
                        };
                        // The indices, and THEN the value -- `set_Item(i, v)`,
                        // which is the order C# uses and the order the use site
                        // below builds its arguments in.
                        foreach (Param ip in p.Params)
                        {
                            setter.Params.Add(ip);
                        }

                        setter.Params.Add(new Param { Name = "value", Type = p.Type, Line = p.Line, Col = p.Col });

                        MethodSymbol ss = new()
                        {
                            Name = setter.Name, Returns = Type.Void, Owner = sym,
                            Static = p.Mods.HasFlag(Mods.Static), Decl = setter,
                            Virtual = aVirtual, Override = aOverride, Abstract = aAbstract,
                        };

                        foreach (Param ip in p.Params)
                        {
                            ss.Params.Add(new ParamSymbol { Name = ip.Name, Type = Resolve(ip.Type, sym) });
                        }
                        ss.Params.Add(new ParamSymbol { Name = "value", Type = propType });
                        sym.Methods.Add(ss);
                        _r.Methods[setter] = ss;
                        _synthesised.Add(setter);
                    }
                    break;
                }

                case MethodDecl md:
                {
                    // A METHOD'S OWN TYPE PARAMETERS ARE IN SCOPE IN ITS OWN
                    // SIGNATURE, which is where they are almost always used:
                    // `static string Join<T>(string sep, List<T> values)` names
                    // T twice before the body starts.
                    //
                    // They were only in scope in the BODY, because the method
                    // being checked was known when bodies were checked and not
                    // when signatures were declared. So every generic method
                    // ever written reported that 'T' is not a known type, and
                    // the feature read as absent when it was only unreachable.
                    _signature = md.TypeParams.Select(p => p.Name).ToList();

                    MethodSymbol ms = new()
                    {
                        Name = md.Name,
                        Returns = md.IsCtor ? Type.Void : Resolve(md.Returns!, sym),
                        Owner = sym,
                        Static = md.Mods.HasFlag(Mods.Static),
                        Virtual = md.Mods.HasFlag(Mods.Virtual),
                        Override = md.Mods.HasFlag(Mods.Override),
                        Abstract = md.Mods.HasFlag(Mods.Abstract) || d.Kind == TypeKind.Interface,
                        Async = md.Mods.HasFlag(Mods.Async),
                        IsCtor = md.IsCtor,
                        Decl = md,
                    };
                    ms.TypeParams.AddRange(md.TypeParams.Select(p => p.Name));

                    foreach (Param p in md.Params)
                    {
                        ms.Params.Add(new ParamSymbol
                        {
                            Name = p.Name,
                            Type = Resolve(p.Type, sym),
                            ByRef = p.IsRef || p.IsOut,
                            ReadOnly = p.IsReadOnlyRef,
                            IsParams = p.IsParams,
                        });
                    }

                    _signature = null;

                    // ONE SIGNATURE, ONE METHOD. C# refuses a second member of
                    // the same name and parameter types whatever it returns
                    // (CS0111); taken quietly, both reached the code generator
                    // under one mangled name and the compiler died there with
                    // a duplicate-key exception instead of saying where. A
                    // partial method's declaration and its body are one
                    // method; a declaration seen again (a retried pass) is
                    // the same one.
                    MethodSymbol? twin = sym.Methods.FirstOrDefault(had => had.Decl != md && had.Name == ms.Name
                        && had.TypeParams.Count == ms.TypeParams.Count && had.Params.Count == ms.Params.Count
                        && had.Params.Zip(ms.Params).All(pair => MethodSignatures.SameType(pair.First.Type, pair.Second.Type)
                            && pair.First.ByRef == pair.Second.ByRef)
                        && !(had.Decl is MethodDecl hd && hd.Mods.HasFlag(Mods.Partial)) && !md.Mods.HasFlag(Mods.Partial));
                    if (twin != null)
                    {
                        Error(md, $"'{sym.Name}' already defines a member called '{md.Name}' with the same parameter types");
                        // Its body is still checked (its own mistakes are
                        // said too); it is only not a member of the type.
                        _r.Methods[md] = ms;
                        break;
                    }

                    sym.Methods.Add(ms);
                    _r.Methods[md] = ms;
                    break;
                }
            }
            }
            catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
            finally { _signature = null; }
        }

        _member = null;
    }

    /// <summary>
    /// Every interface a type implements: the ones it names, the ones its base
    /// classes name, and the ones THOSE INTERFACES EXTEND.
    ///
    /// The last was missing. A class written `class Both : IB` where
    /// `interface IB : IA` implements IA as well -- C# says so, and the vtable
    /// slots the interface region hands out depend on it -- and without the
    /// closure IA's members took no slot on Both, so a call through an IA
    /// reached whatever happened to be in the slot.
    /// </summary>
    private static IEnumerable<TypeSymbol> AllInterfaces(TypeSymbol sym)
    {
        HashSet<TypeSymbol> seen = new();

        for (TypeSymbol? t = sym; t != null; t = t.Base)
        {
            foreach (TypeSymbol i in t.Interfaces)
            {
                foreach (TypeSymbol one in Extended(i))
                {
                    if (seen.Add(one))
                    {
                        yield return one;
                    }
                }
            }
        }
    }

    /// <summary>
    /// An interface and every interface it extends, transitively -- what C#
    /// calls its base-interface set, and the set a member lookup through it
    /// may see.
    /// </summary>
    private static IEnumerable<TypeSymbol> Extended(TypeSymbol face)
    {
        HashSet<TypeSymbol> seen = new();
        Stack<TypeSymbol> todo = new();

        todo.Push(face);

        while (todo.Count > 0)
        {
            TypeSymbol one = todo.Pop();

            if (!seen.Add(one))
            {
                continue;
            }
            yield return one;

            foreach (TypeSymbol up in one.Interfaces)
            {
                todo.Push(up);
            }
        }
    }

    /// <summary>
    /// A type's methods of this name, INCLUDING the ones an interface it
    /// extends declares: `interface IB : IA` answers IA's members, which is
    /// what C# means by an interface inheriting them. A class's own lookup is
    /// unchanged -- the interfaces it implements are a contract it satisfies,
    /// not a place its members are found.
    /// </summary>
    private static List<MethodSymbol> MethodsOn(TypeSymbol owner, string name)
    {
        List<MethodSymbol> found = owner.FindMethods(name);

        if (found.Count > 0 || owner.Kind != TypeKind.Interface)
        {
            return found;
        }

        foreach (TypeSymbol face in Extended(owner))
        {
            List<MethodSymbol> up = face.FindMethods(name);

            if (up.Count > 0)
            {
                return up;
            }
        }
        return found;
    }

    /// <summary>Assigns field offsets and vtable slots.</summary>
    private void LayOut(TypeSymbol sym)
    {
        if (sym.Kind == TypeKind.Enum)
        {
            return;
        }
        // A class from a library arrives with its size known, but its virtual
        // slots still have to be numbered here, the same way the library
        // numbered them, or a class deriving from it lays its own overrides
        // over the wrong entries and the library's calls land on nothing.
        if (sym.InstanceSize > 0)
        {
            if (!sym.SlotsAssigned)
            {
                if (sym.Base != null) LayOut(sym.Base);
                AssignSlots(sym);
            }
            return;
        }

        // Revision 1.5 gives every object a descriptor and synchronization
        // word before its payload. LdVt follows the descriptor to the vtable.
        int at = sym.Kind == TypeKind.Class ? Target.Current.ObjectHeaderBytes : 0;

        if (sym.Base != null)
        {
            LayOut(sym.Base);
            at = Math.Max(at, sym.Base.InstanceSize);
        }

        // A LIBRARY'S statics are numbered on their own, from the same start,
        // because they are laid out where the library laid them out -- in its
        // own slot of the block every process gets. Sharing this compilation's
        // counter would give them offsets that depend on how many statics the
        // PROGRAM happens to have, and the same library would be reached at a
        // different offset by every program that used it.
        bool external = sym.Decl?.External == true;

        foreach (FieldSymbol f in sym.Fields.Where(f => f.Static))
        {
            int size = Math.Max(1, f.Type.Size);

            if (external)
            {
                int lib = sym.Decl?.LibSlot ?? 0;

                if (!_externNext.TryGetValue(lib, out int next))
                {
                    next = 16;
                }

                next = (next + size - 1) / size * size;
                f.Offset = next;
                _externNext[lib] = next + size;
            }
            else
            {
                _staticNext = (_staticNext + size - 1) / size * size;
                f.Offset = _staticNext;
                _staticNext += size;
            }
        }

        foreach (FieldSymbol f in sym.Fields.Where(f => !f.Static))
        {
            int size = f.Type.Size;

            if (size <= 0)
            {
                throw new InvalidOperationException(
                    $"{sym.Name}.{f.Name}: a field of type '{f.Type}' has no size to lay out "
                    + $"(prim {(int)f.Type.Prim}, parameter '{f.Type.ParamName}', declared in {sym.Decl?.File})");
            }

            at = (at + size - 1) / size * size;   // natural alignment
            f.Offset = at;
            at += size;
        }

        sym.InstanceSize = Math.Max(at,
            sym.Kind == TypeKind.Class ? Target.Current.ObjectHeaderBytes : 1);

        if (sym.Kind == TypeKind.Interface)
        {
            return;
        }

        AssignSlots(sym);
    }

    /// <summary>
    /// Numbers a class's virtual methods: interface region first, then the
    /// base chain's slots, then this class's own, with an override taking the
    /// slot of what it overrides. Deterministic from the declarations alone,
    /// which is what lets a program agree with a library about a class the
    /// library owns.
    /// </summary>
    private void AssignSlots(TypeSymbol sym)
    {
        if (sym.SlotsAssigned) return;
        sym.SlotsAssigned = true;
        // A class's own virtual methods are numbered above the interface
        // region -- the LIBRARY's region for a library class, so that it gets
        // the numbers its own build gave it whatever this compilation adds.
        int slot = IsLibraryType(sym) ? _librarySlots : _interfaceSlots;

        for (TypeSymbol? t = sym.Base; t != null; t = t.Base)
        {
            foreach (MethodSymbol m in t.Methods.Where(m => m.VtableSlot >= 0))
            {
                slot = Math.Max(slot, m.VtableSlot + 1);
            }
        }

        // An implementation of an interface method takes THAT method's slot, so
        // a call through the interface reaches it whatever class it is on.
        foreach (TypeSymbol iface in AllInterfaces(sym))
        {
            bool reimplements = sym.Interfaces.Any(direct => Extended(direct).Contains(iface));
            foreach (MethodSymbol want in iface.Methods)
            {
                if (want.Static) continue;
                // Merely hiding a base member does not remap an inherited
                // interface. An override does; naming the interface again
                // explicitly requests a fresh implementation search.
                if (!reimplements && sym.Base is not null
                    && sym.Base.InterfaceImplementations.TryGetValue(want.VtableSlot, out MethodSymbol? inherited))
                {
                    MethodSymbol? replacement = sym.Methods.FirstOrDefault(m => m.Override
                        && m.Name == inherited.Name && MethodSignatures.Implements(m, inherited));
                    sym.InterfaceImplementations[want.VtableSlot] = replacement ?? inherited;
                    continue;
                }
                MethodSymbol? impl = sym.FindMethods(want.Name)
                    .FirstOrDefault(m => !m.Abstract && MethodSignatures.Implements(m, want));

                // AN ABSTRACT CLASS MAY IMPLEMENT AN INTERFACE MEMBER AND LEAVE
                // THE BODY TO ITS DERIVED CLASSES: `abstract class C : ICounted
                // { public abstract int Count { get; } }` is a complete C# type
                // and was reported as not implementing ICounted.get_Count.
                // The abstract member still takes the interface's slot, so the
                // override that eventually provides the body inherits it.
                if (impl is null && (sym.Decl?.Mods.HasFlag(Mods.Abstract) ?? false))
                {
                    impl = sym.FindMethods(want.Name)
                        .FirstOrDefault(m => m.Abstract && MethodSignatures.Implements(m, want));
                }

                if (impl is null)
                {
                    if (sym.Decl != null)
                    {
                        Error(sym.Decl, $"'{sym.Name}' does not implement '{iface.Name}.{want.Name}({want.Params.Count} args)'");
                    }
                    continue;
                }
                sym.InterfaceImplementations[want.VtableSlot] = impl;
            }
        }

        // ToString takes the reserved slot whether or not anyone wrote
        // 'virtual' or 'override' on it. In C# it is always an override,
        // because everything derives from object; here there is no root class
        // to derive from, so the slot is the thing that makes it one.
        foreach (MethodSymbol m in sym.Methods
                     .Where(m => m.Name == "ToString" && !m.Static && m.Params.Count == 0))
        {
            m.VtableSlot = _r.ToStringSlot;
        }

        // Equals(object) the same way, and for the same reason.
        //
        // THE ONE THAT TAKES AN OBJECT, and only that one. A type that is
        // IEquatable<T> has two one-argument Equals, and `Equals(object o) =>
        // Equals(o as T)` is how every one of them is written: given the same
        // slot, the second overwrote the first and that call came back to
        // itself until the stack ran out.
        //
        // A type that wrote only `Equals(T)` is still asked through the slot --
        // a record is, by every table it is a key of -- but by way of a guard
        // the code generator writes (Lowering.EqualsGuard), because what comes
        // through the slot is ANY object and that method reads it as a T.
        foreach (MethodSymbol m in sym.Methods
                     .Where(m => m.Name == "Equals" && !m.Static && m.Params.Count == 1
                              && m.Params[0].Type.Prim == Prim.Any))
        {
            m.VtableSlot = _r.EqualsSlot;
        }

        // And GetHashCode(), which goes with it.
        foreach (MethodSymbol m in sym.Methods
                     .Where(m => m.Name == "GetHashCode" && !m.Static && m.Params.Count == 0))
        {
            m.VtableSlot = _r.HashSlot;
        }

        // Abstract instance members are implicitly virtual in C#. Without a
        // slot a call through the abstract type became a direct call to a
        // body which cannot exist, and the linker failed on its missing label.
        foreach (MethodSymbol m in sym.Methods.Where(m =>
                     (m.Virtual || m.Override || m.Abstract)
                     && !m.Static && m.VtableSlot < 0))
        {
            List<MethodSymbol> sameArity = sym.Base?.FindMethods(m.Name)
                .Where(b => b.Params.Count == m.Params.Count).ToList()
                ?? new List<MethodSymbol>();

            // BY SIGNATURE, NOT BY COUNT. A base with Write(char) and
            // Write(string) has two one-argument methods of the name, and
            // taking the first gave both overrides the SAME slot: `w.Write('c')`
            // resolved to Write(char), dispatched through the slot Write(string)
            // had been written into, and read the character code as a pointer.
            //
            // The arity-only answer is kept as a fallback for the one case it
            // cannot get wrong -- a single candidate -- because an override
            // whose parameter was substituted through a type argument does not
            // compare equal to the one it overrides.
            MethodSymbol? overridden = sameArity
                .FirstOrDefault(b => b.Params.Zip(m.Params).All(p => p.First.Type.Equals(p.Second.Type)));
            overridden ??= sameArity.Count == 1 ? sameArity[0] : null;

            m.VtableSlot = overridden is { VtableSlot: >= 0 } ? overridden.VtableSlot : slot++;
        }
    }

    // ---- types ----------------------------------------------------------

    private Type Resolve(TypeRef r, TypeSymbol? context)
    {
        Type baseType = ResolveCore(r, context);

        // POINTERS FIRST, then the array: `byte*[]` is an array OF pointers,
        // which is C#'s reading and the only one that makes sense -- an array
        // is a thing on the heap and a pointer is a number.
        for (int i = 0; i < r.PointerDepth; i++)
        {
            baseType = baseType.PointerTo();
        }

        if (r.ArrayRank > 0)
        {
            // THE ELEMENT'S '?' GOES ON THE ELEMENT, before the array is built
            // around it. `string?[]` is an array -- itself never null here --
            // whose entries may be; the array's own '?' is applied below.
            if (r.ElementNullable)
            {
                baseType = baseType.AsNullable();
            }

            // One level at a time, so a '?' between two pairs of brackets
            // lands on the array it follows: `byte[]?[]` is an array of
            // arrays that may be null.
            for (int level = 1; level <= r.ArrayRank; level++)
            {
                baseType = Type.ArrayOf(baseType, 1);
                if (level < r.ArrayRank && (r.InnerNullable & (1 << (level - 1))) != 0)
                {
                    baseType = baseType.AsNullable();
                }
            }
        }

        if (!r.Nullable)
        {
            return baseType;
        }

        // '?' ON A VALUE TYPE IS Nullable<T>, and it used to be an error here.
        //
        // Refusing it was defensible while nothing could represent it. It stops
        // being defensible the moment the compiler's own AST wants
        // `public BinOp? Op` for "an ordinary assignment, or a compound one" --
        // which is exactly the case Nullable<T> is FOR, and which has no honest
        // spelling without it.
        //
        // 'void?' still means nothing at all, so that one stays an error.
        if (baseType.Equals(Type.Void))
        {
            Error(r, "'void' cannot be made nullable with '?'");
            return baseType;
        }
        return baseType.AsNullable();
    }

    private Type ResolveCore(TypeRef r, TypeSymbol? context)
    {
        // A TUPLE TYPE is the class this writes for its shape, carrying the
        // element names it was written with. See TupleType.
        if (r.Name == TypeRef.Tuple && r.Args.Count > 1)
        {
            List<Type> elements = r.Args.Select(a => Resolve(a, context)).ToList();

            return new Type
            {
                Prim = Prim.Void,
                Symbol = TupleType(elements, r.TupleNames),
                Names = r.TupleNames?.ToArray(),
            };
        }

        switch (r.Name)
        {
            case "void":   return Type.Void;
            case "bool":   return Type.Bool;
            case "sbyte":  return Type.I8;
            case "short":  return Type.I16;
            case "int":    return Type.I32;
            case "long":   return Type.I64;

            // THE UNSIGNED FOUR. Not spellings of the signed ones: they load
            // zero-extended, divide unsigned and shift right logically, and
            // `enum Tok : byte` is the first thing in this compiler's own
            // source, so nothing of it could be read without them.
            case "byte":   return Type.U8;
            case "ushort": return Type.U16;
            case "uint":   return Type.U32;
            case "ulong":  return Type.U64;

            // THE NATIVE PAIR: one machine word each, so that an address can
            // be held in one register on a machine whose long is two.
            case "nint":   return Type.NInt;
            case "nuint":  return Type.NUInt;
            case "float":  return Type.F32;
            case "double": return Type.F64;
            case "char":   return Type.Char;
            case "string": return Type.String;
            case "object": return Type.Any;

            // THE SHARED SHAPE OF EVERY MACHINE WORD.
            //
            // What a generic's type parameter becomes in the ONE compiled copy
            // that serves every word-shaped instantiation -- a reference, a
            // long, an array, a pointer. It is `object` in all but name, and it
            // has a name of its own because a reader of a disassembly should be
            // able to tell `List$__canon` (the shared copy) from `List$object`
            // (somebody's list of objects) at a glance.
            //
            // See Monomorphiser.CanonName and TypeDecl.Canon.
            case Monomorphiser.CanonName: return Type.Any;

            // A TYPE, AS A VALUE -- what typeof(T) and GetType() produce.
            //
            // Resolved here rather than declared in the standard library
            // because its one field, the name, lives in a descriptor the code
            // generator lays out. A class in source whose layout had to agree
            // with the code generator by hand is a layout that disagrees
            // eventually, and silently.
            //
            // Yielded only when nothing VISIBLE FROM HERE has declared the
            // name, so a program with a Type of its own keeps it -- and so
            // does a namespace: `Corsac.Lang.Type` is this compiler's own, and
            // asking the flat table for a bare "Type" never sees it.
            case "Type" when !FindType("Type", out _): return Type.TypeHandle;
        }

        if (context != null && context.TypeParams.Contains(r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name };
        }

        if (_method != null && _method.TypeParams.Contains(r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name };
        }

        // The same, for a signature being declared -- there is no method symbol
        // to ask yet, because this is what is building one.
        if (_signature != null && _signature.Contains(r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name };
        }

        // THE NAME AS WRITTEN, from where it was written: `ImageFile.Section`
        // and `Corsac.Lang.Ir.Block` name one type each, and it must be tried
        // BEFORE the qualifier is trimmed -- trimming is what would hand back
        // Assembler's Section instead.
        //
        // `Task<T>` is not `Task`: when a generic type shares its name with a
        // non-generic one, a reference with type arguments means the generic.
        if (r.Args.Count > 0 && FindType(Arity(r.Name, r.Args.Count), out TypeSymbol? generic)
            && generic is not null)
        {
            return new Type
            {
                Prim = Prim.Void,
                Symbol = generic,
                Args = r.Args.Select(a => Resolve(a, context)).ToArray(),
            };
        }

        if (FindType(r.Name, out TypeSymbol? path) && path is not null)
        {
            return new Type
            {
                Prim = path.Kind == TypeKind.Enum ? path.EnumUnderlying : Prim.Void,
                Symbol = path,
                Args = r.Args.Select(a => Resolve(a, context)).ToArray(),
                UseArgs = r.UseArgs?.Select(a => Resolve(a, context)).ToArray(),
            };
        }

        // OTHERWISE A QUALIFIED NAME NAMES ITS LAST PART. `Corsac.Size` is the
        // type Size, said at length because the file it appears in ALSO has a
        // property called Size and the qualification is how C# tells them
        // apart.
        //
        // Namespaces are not a tree here, so a namespace qualifier has nothing
        // to select between -- but refusing to read it would mean rejecting
        // source that says exactly what it means. When namespaces become real
        // this is where they get walked instead of trimmed.
        string bare = r.Name.Contains('.') ? r.Name[(r.Name.LastIndexOf('.') + 1)..] : r.Name;

        // AN OPEN TEMPLATE APPLICATION -- `List<T>` where T is a method's own
        // type parameter, which the monomorphiser could not specialise because
        // only the checker learns what T is. Keyed by name and arity, because
        // Func<A,R> and Func<A,B,R> are two types sharing a name.
        if (r.Args.Count > 0 && FindType(Arity(bare, r.Args.Count), out TypeSymbol? open)
            && open is not null)
        {
            return new Type
            {
                Prim = Prim.Void,
                Symbol = open,
                Args = r.Args.Select(a => Resolve(a, context)).ToArray(),
            };
        }

        // FROM WHERE IT WAS WRITTEN: a name inside a type may be one of that
        // type's own, and only then is it a name the whole program shares.
        if (FindType(bare, out TypeSymbol? sym) && sym is not null)
        {
            return new Type
            {
                Prim = sym.Kind == TypeKind.Enum ? sym.EnumUnderlying : Prim.Void,
                Symbol = sym,
                Args = r.Args.Select(a => Resolve(a, context)).ToArray(),
                UseArgs = r.UseArgs?.Select(a => Resolve(a, context)).ToArray(),
            };
        }

        // The keywords again, in case the qualifier hid one -- `System.Int32`.
        // Quietly, because failing here is not the diagnostic: the one below,
        // naming what was actually written, is.
        if (bare != r.Name)
        {
            _quiet++;

            Type named = ResolveCore(new TypeRef { Name = bare, Line = r.Line, Col = r.Col }, context);

            _quiet--;

            if (!named.IsError)
            {
                return named;
            }
        }

        Error(r, $"'{r.Name}' is not a known type");
        return Type.Error;
    }

    // ---- bodies ---------------------------------------------------------

    private void CheckBodies(TypeDecl d, TypeSymbol sym)
    {
        _thisType = sym;
        _member = null;

        List<MethodDecl> bodies = d.Members.OfType<MethodDecl>().ToList();
        bodies.AddRange(_synthesised.Where(x => _r.Methods.TryGetValue(x, out MethodSymbol? ms) && ReferenceEquals(ms.Owner, sym)));

        foreach (MethodDecl md in bodies)
        {
            _member = md;

            if (md.Body is null || (!md.LocalCopy && (md.OwnedImplementation == false || d.SignatureOnly)))
            {
                continue;
            }

            // A word-shaped specialisation owns its concrete layout and
            // signatures but deliberately emits no body: Canon names the one
            // body shared by every word-shaped instantiation. Check that body
            // once on the canonical declaration. Re-checking these cloned
            // bodies is both redundant and wrong for nullable type arguments,
            // whose source-level annotations differ although their machine
            // representation and instructions are identical.
            if (sym.Decl?.Canon is not null)
            {
                continue;
            }

            // A GENERIC METHOD'S BODY IS A TEMPLATE, and templates are not
            // checked. `keep(source[i])` inside `Where<T>` calls Invoke on a
            // `Func<T,bool>` -- an open application whose members still say A
            // and R, because nothing has substituted anything yet. The COPY
            // made for each set of type arguments is concrete, and that is
            // what gets checked and compiled.
            if (md.TypeParams.Count > 0)
            {
                continue;
            }

            _method = _r.Methods[md];
            _closureOwner = ClosureIdentity.Of(_method);
            _closures = 0;
            _nextSlot = 0;
            _maxSlot = 0;

            // WHAT THE LAST METHOD PROVED IS NOTHING HERE. The null state is
            // flow state, and a path is kept as text: `_held` proved non-null
            // by a test in one method was still "proved" in every method
            // checked after it, so a field read as `Box` where it is `Box?`,
            // and a generic call over it was specialised for the wrong type.
            _notNull.Clear();
            _notNullPaths.Clear();
            PushScope(functionBoundary: true);

            for (int i = 0; i < _method.Params.Count; i++)
            {
                Declare(md.Params[i], _method.Params[i].Name,
                        new ParamSym(i, _method.Params[i].Type, _method.Params[i].Name,
                                     _method.Params[i].ByRef, _method.Params[i].ReadOnly));
            }

            // A constructor's chained call runs before its body, so its
            // arguments are checked in the same scope the parameters are in.
            if (md.Init != null)
            {
                List<Type> given = new();

                foreach (Expr a in md.Init.Args)
                {
                    given.Add(CheckExpr(a));
                }

                TypeSymbol? target = md.Init.IsThis ? sym : sym.Base;
                List<MethodSymbol> others = (target?.Methods ?? new List<MethodSymbol>())
                    .Where(c => c.IsCtor && !c.Static && c.Params.Count == given.Count
                             && !ReferenceEquals(c.Decl, md)).ToList();
                MethodSymbol? runs =
                    others.FirstOrDefault(c => c.Params.Zip(given).All(p => p.First.Type.Equals(p.Second)))
                    ?? others.FirstOrDefault(c => c.Params.Zip(given).All(
                           p => p.Second.IsError || Convertible(p.Second, p.First.Type)))
                    ?? others.FirstOrDefault();

                if (runs is not null)
                {
                    _r.Chained[md] = runs;
                }

                if (target is null)
                {
                    Error(md.Init, $"'{sym.Name}' has no base class to chain to");
                }
                else if (!target.Methods.Any(c => c.IsCtor && c.Params.Count == md.Init.Args.Count))
                {
                    Error(md.Init, $"'{target.Name}' has no constructor taking {md.Init.Args.Count} argument(s)");
                }
            }

            CheckBlock(md.Body);
            PopScope();
            _r.FrameSize[md] = _maxSlot;
            _method = null;
        }
        _thisType = null;
    }

    private void PushScope(bool functionBoundary = false)
        => _scopes.Add(new LocalScope(functionBoundary));

    private void PopScope()
    {
        LocalScope closing = _scopes[^1];
        if (!closing.FunctionBoundary && _scopes.Count > 1)
        {
            _scopes[^2].NestedNames.UnionWith(closing.Keys);
            _scopes[^2].NestedNames.UnionWith(closing.NestedNames);
        }

        // Slots are reused across sibling scopes: a frame is as deep as the
        // deepest nesting, not as long as the method.
        foreach (LocalSym local in _scopes[^1].Values.OfType<LocalSym>())
        {
            _assigned.Remove(local);
        }
        _nextSlot -= _scopes[^1].Values.OfType<LocalSym>().Count();
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void Declare(Node at, string name, Sym sym)
    {
        if (_scopes[^1].ContainsKey(name))
        {
            Error(at, $"'{name}' is already declared in this scope");
            return;
        }

        // C# declaration spaces overlap regardless of textual order. Checking
        // only active ancestors misses `{ int x; } int x;`; checking all names
        // ever seen would incorrectly reject `{ int x; } { int x; }`.
        bool conflict = _scopes[^1].NestedNames.Contains(name);
        for (int i = _scopes.Count - 1;
             i > 0 && !_scopes[i].FunctionBoundary && !conflict; )
        {
            i--;
            conflict = _scopes[i].ContainsKey(name);
        }
        if (conflict)
        {
            Error(at, $"'{name}' is already declared in an enclosing or nested local scope");
            return;
        }
        _scopes[^1][name] = sym;
    }

    private int NewSlot()
    {
        int slot = _nextSlot++;
        _maxSlot = Math.Max(_maxSlot, _nextSlot);
        return slot;
    }

    private Sym? Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out Sym? s))
            {
                // FOUND BELOW THE LAMBDA'S FLOOR, so it belongs to the method
                // the lambda was written in and not to the lambda. Written down
                // rather than acted on: the discovery pass is what this is for,
                // and the closure it produces gets a field per name recorded
                // here.
                if (_captured != null && i < _lambdaFloor)
                {
                    Type held = s switch
                    {
                        LocalSym l => l.Type,
                        ParamSym p => p.Type,
                        _ => Type.Error,
                    };

                    if (!held.IsError)
                    {
                        _captured[name] = held;

                        // AND IT MOVES TO THE HEAP. Both sides read the one
                        // cell from here on, which is what makes a later
                        // assignment on either side visible to the other.
                        if (s is LocalSym box)
                        {
                            box.Boxed = true;

                            if (_declOf.TryGetValue(box, out LocalDecl? where))
                            {
                                _r.BoxedLocals.Add(where);
                            }
                        }
                    }
                }
                return s;
            }
        }
        return null;
    }

    private void CheckBlock(Block b)
    {
        PushScope();

        // A GENERIC LOCAL FUNCTION is a hidden generic method of the type
        // (Block.GenericLocals); in this block its written name is that
        // method's group, as a method of the type is reached by its own.
        foreach ((string name, string method) in b.GenericLocals)
        {
            List<MethodSymbol>? methods = _thisType?.FindMethods(method);
            if (methods is { Count: > 0 }) Declare(b, name, new MethodGroupSym(methods));
        }

        // C# LOCAL FUNCTIONS ARE BLOCK-SCOPED, not declaration-scoped: a call
        // above the declaration is valid and recursion requires the name to be
        // present while its own body is checked. Reserve their slots and names
        // before binding any statement in the block.
        foreach (Stmt statement in b.Statements)
        {
            if (statement is not LocalDecl { LocalFunction: true, Type: not null } local)
            {
                continue;
            }

            Type type = Resolve(local.Type, _thisType);
            LocalSym symbol = new(NewSlot(), type, local.Name);

            _r.LocalSlot[local] = symbol.Slot;
            _r.LocalType[local] = type;
            _r.LocalSymbols[local] = symbol;
            _declOf[symbol] = local;
            _hoistedFunctions[local] = symbol;
            _localFunctionContexts[local] = new(new(_scopes), _scope, _thisType, _lexicalType, _member);
            Declare(local, local.Name, symbol);
            _assigned.Add(symbol);
        }

        foreach (Stmt s in b.Statements)
        {
            CheckStmt(s);
        }
        PopScope();
    }

    private void CheckStmt(Stmt s)
    {
        switch (s)
        {
            case Block b:
                CheckBlock(b);
                break;

            case LocalDecl d:
            {
                if (_hoistedFunctions.TryGetValue(d, out LocalSym? hoisted))
                {
                    if (d.Init is LambdaExpr function)
                    {
                        CheckLambda(function, hoisted.Type);
                    }
                    break;
                }

                // `const int Bx = 3;` IS A NAME FOR A VALUE, not storage --
                // the same thing a const field is, and what makes `r is Bx or
                // Bp` a constant pattern rather than a test against a type
                // nobody declared. C# requires the initialiser to be a
                // constant expression, so one that is not is a mistake worth
                // naming.
                if (d.IsConst)
                {
                    Type declared = d.Type is null ? Type.I32 : Resolve(d.Type, _thisType);

                    foreach (LocalDecl one in new[] { d }.Concat(d.Also))
                    {
                        if (IsReal(declared) && RealConstant(one.Init, _thisType) is double real)
                        {
                            CheckExpr(one.Init!);
                            Declare(one, one.Name, new ConstSym(RealBits(real, declared), declared));
                            continue;
                        }

                        if (!IsReal(declared) && one.Init is not null && ConstantValue(one.Init, _thisType) is long value)
                        {
                            CheckExpr(one.Init);
                            Declare(one, one.Name, new ConstSym(value, declared));
                            continue;
                        }

                        // AND A CONST MAY BE A STRING. `const string name =
                        // "__object_equals";` names a value just as a number
                        // does; it simply is not one, so it is kept as the
                        // text and every read of the name becomes that literal.
                        if (declared.Prim == Prim.String && one.Init is not null
                            && _thisType is not null
                            && ConstantText(one.Init, _thisType) is string text)
                        {
                            CheckExpr(one.Init);
                            Declare(one, one.Name, new ConstSym(0, declared, text));
                            continue;
                        }

                        Error(one, $"'{one.Name}' is const, so what it is set to must be "
                                 + "a constant expression");
                    }
                    break;
                }

                Type type;

                if (d.Type is null)
                {
                    if (d.Init is null)
                    {
                        Error(d, "'var' needs an initialiser to infer from");
                        type = Type.Error;
                    }
                    else
                    {
                        type = CheckExpr(d.Init);

                        if (type.Prim == Prim.NullLiteral)
                        {
                            Error(d, "'var' cannot infer a type from null; write the type explicitly");
                            type = Type.Error;
                        }
                    }
                }
                else
                {
                    type = Resolve(d.Type, _thisType);

                    if (d.Init is LambdaExpr held)
                    {
                        // `Func<int,int> f = x => x + 1;` -- the other place a
                        // lambda is told what it is. The declared type is what
                        // it has to be, exactly as a parameter's type is.
                        CheckLambda(held, type);
                    }
                    else if (d.Init != null)
                    {
                        // AND THE DECLARED TYPE IS WHAT `new()` MEANS HERE, the
                        // same way a return statement's type is: `List<int> t =
                        // new() { 4, 5 };` has been told everything it needs and
                        // only this line hands it over.
                        // Only when the initialiser IS the `new()`. Pushing it
                        // through a whole expression would answer a `new()`
                        // buried in an argument with the variable's type, which
                        // is not what it means there.
                        Type? outer = _wanted;

                        _wanted = d.Init is NewExpr { Type.Name.Length: 0 } or TupleExpr or ConditionalExpr or SwitchExpr ? type : outer;

                        Type had = CheckExpr(d.Init);

                        _wanted = outer;
                        CheckAssignable(had, type, d.Init, $"initialiser for '{d.Name}'");
                    }
                }

                int slot = NewSlot();
                _r.LocalSlot[d] = slot;
                _r.LocalType[d] = type;

                LocalSym made = new(slot, type, d.Name);

                _r.LocalSymbols[d] = made;
                _declOf[made] = d;
                Declare(d, d.Name, made);
                if (d.Init is not null) { _assigned.Add(made); }
                if (d.Init is not null)
                {
                    Type initialState = _r.TypeOf(d.Init);
                    if (type.IsReference && !initialState.Nullable && initialState.Prim != Prim.NullLiteral && !initialState.IsError)
                    {
                        _notNull.Add(made);
                        _notNullPaths.Add(d.Name);
                    }
                }

                // Object-initializer assignments are known facts about the
                // fresh object held by this local. C# carries those facts into
                // subsequent member reads (`ctor.Body`, `tuple.TupleNames`,
                // `array.Elements`) until the local or path is overwritten.
                if (d.Init is NewExpr built)
                {
                    foreach (InitAssign init in built.Inits)
                    {
                        if (init.Value is null)
                        {
                            continue;   // `Name = { … }` sets nothing
                        }

                        Type assigned = _r.TypeOf(init.Value);
                        if (!assigned.Nullable && assigned.Prim != Prim.NullLiteral && !assigned.IsError)
                        {
                            _notNullPaths.Add(d.Name + "." + init.Name);
                        }
                    }
                }

                // AND THE OTHERS IN THE SAME DECLARATION, here rather than as
                // statements of their own so that they land in this scope.
                foreach (LocalDecl also in d.Also)
                {
                    CheckStmt(also);
                }
                break;
            }

            case ExprStmt e:
                CheckExpr(e.Expr);
                break;

            case IfStmt i:
            {
                CheckCondition(i.Cond);
                HashSet<LocalSym> before = new(_assigned, ReferenceEqualityComparer.Instance);

                // THE THEN BRANCH KNOWS WHAT THE CONDITION PROVED, and the else
                // branch knows the opposite. Each is undone afterwards, because
                // what a branch proved is only true inside it.
                List<Sym> inThen = Assume(i.Cond, true);

                CheckStmt(i.Then);
                HashSet<LocalSym> afterThen = new(_assigned, ReferenceEqualityComparer.Instance);
                Forget(inThen);

                _assigned.Clear();
                _assigned.UnionWith(before);

                List<Sym> inElse = Assume(i.Cond, false);

                if (i.Else != null)
                {
                    CheckStmt(i.Else);
                }
                HashSet<LocalSym> afterElse = new(_assigned, ReferenceEqualityComparer.Instance);

                bool thenLeaves = Leaves(i.Then);
                bool elseLeaves = i.Else != null && Leaves(i.Else);

                // Definite assignment is merged only across branches which can
                // reach the following statement. A branch ending in return or
                // throw contributes no path at all; intersecting it used to
                // reject the ordinary `if (...) x = a; else return; use(x);`
                // form even though every reaching path assigned x.
                if (thenLeaves && !elseLeaves)
                {
                    _assigned.Clear();
                    _assigned.UnionWith(afterElse);
                }
                else if (!thenLeaves && elseLeaves)
                {
                    _assigned.Clear();
                    _assigned.UnionWith(afterThen);
                }
                else if (!thenLeaves && !elseLeaves)
                {
                    _assigned.Clear();
                    _assigned.UnionWith(afterThen);
                    _assigned.IntersectWith(afterElse);
                }

                // AND THE GUARD CLAUSE. If the then-branch always leaves, then
                // reaching the line after the `if` means the condition was
                // FALSE -- so whatever that proves holds from here on, and is
                // deliberately not forgotten.
                // A branch that ends calling a [DoesNotReturn] method --
                // Environment.Exit, a throw helper -- leaves as far as null is
                // concerned, as C#'s nullable analysis has it; definite
                // assignment above does not count it, as C#'s does not.
                if (!thenLeaves && !NeverReturns(i.Then))
                {
                    Forget(inElse);
                }
                break;
            }

            // A LOOP'S CONDITION PROVES THINGS INSIDE IT, exactly as an `if`'s
            // does: the body only runs when the condition held.
            //
            // `while (t != null) { ... t.Base ... }` is the same shape as the
            // guard clause, and without this it was the one place a nullable
            // could not be used after being tested. It is undone at the end,
            // because what the condition proved is only true inside.
            case WhileStmt w:
            {
                PushScope();
                CheckCondition(w.Cond);

                List<Sym> inLoop = Assume(w.Cond, true);

                _loopDepth++;
                CheckStmt(w.Body);
                _loopDepth--;
                Forget(inLoop);
                PopScope();
                break;
            }

            case DoStmt dd:
                _loopDepth++;
                CheckStmt(dd.Body);
                _loopDepth--;
                CheckCondition(dd.Cond);
                break;

            case ForStmt f:
            {
                PushScope();

                if (f.Init != null)
                {
                    CheckStmt(f.Init);
                }
                if (f.Cond != null)
                {
                    CheckCondition(f.Cond);
                }

                // WHAT THE CONDITION PROVED HOLDS IN THE BODY AND IN THE STEP.
                //
                // `for (TypeSymbol? t = this; t != null; t = t.Base)` is how
                // every walk up a hierarchy in this compiler's own Types.cs is
                // written -- six of them -- and the step reaches through t as
                // much as the body does.
                List<Sym> proved = f.Cond is null ? new List<Sym>() : Assume(f.Cond, true);

                // THE BODY BEFORE THE STEP, which is the order they run in and
                // now the order they are checked in. The step assigns to the
                // loop variable and an assignment ends what the condition
                // proved -- so checking it first took the proof away from the
                // body, and `t.Interfaces` two lines in was reported as a read
                // through something that may be null.
                _loopDepth++;
                CheckStmt(f.Body);
                _loopDepth--;

                foreach (Expr step in f.Step)
                {
                    CheckExpr(step);
                }

                Forget(proved);
                PopScope();
                break;
            }

            // TAKING A VALUE APART, as a statement. The value goes in a hidden
            // local -- it is read once, whatever it took to produce -- and the
            // names come out of that, by position for a tuple and through
            // Deconstruct for anything else.
            case DeconstructStmt taken:
            {
                Type had = Peek(taken.Value);
                string held = $"$taken${_iterations++}";
                Block block = new() { Line = taken.Line, Col = taken.Col };

                block.Statements.Add(new LocalDecl
                {
                    Name = held, Init = taken.Value, Line = taken.Line, Col = taken.Col,
                });
                block.Statements.AddRange(Deconstruct(
                    taken,
                    new NameExpr { Name = held, Line = taken.Line, Col = taken.Col },
                    taken.Names,
                    had));

                // The names belong to the scope this statement is in, so the
                // block is checked WITHOUT one of its own.
                _r.Lowered[taken] = block;

                foreach (Stmt one in block.Statements)
                {
                    CheckStmt(one);
                }
                break;
            }

            case ForeachStmt fe:
            {
                Type seq = Peek(fe.Sequence);

                // ANYTHING BUT AN ARRAY IS REWRITTEN, which is what C# does to
                // all of them: an array keeps its own loop here only because it
                // has one already and it is the tighter code.
                //
                // AN ARRAY BEING TAKEN APART IS REWRITTEN TOO. The fast path
                // binds ONE name to the element, and a deconstructing loop
                // binds several out of it -- so `foreach ((string text, Tok
                // kind) in table)` over an array of tuples bound `text` to the
                // whole tuple and then could not index it.
                bool linear = seq.IsArray || seq.Prim == Prim.String;

                if ((!linear || fe.Bindings != null) && !seq.IsError
                    && Iterate(fe, seq) is Stmt lowered)
                {
                    _r.Lowered[fe] = lowered;
                    CheckStmt(lowered);
                    break;
                }

                CheckExpr(fe.Sequence);

                Type element = seq.IsArray && seq.Element != null ? seq.Element
                             : seq.Prim == Prim.String ? Type.Char
                             : Type.Error;

                if (!linear && !seq.IsError)
                {
                    Error(fe.Sequence, $"'{seq}' cannot be iterated: it is not an array, "
                        + "has no GetEnumerator, and is not a list");
                }

                PushScope();
                // Three consecutive slots: the visible loop variable, then a
                // hidden index, then the sequence itself. The sequence needs
                // its own home — holding it in the element's slot means the
                // second iteration reads the element as though it were the
                // array.
                int slot = NewSlot();
                NewSlot();   // index
                NewSlot();   // sequence
                _r.ForeachSlot[fe] = slot;
                LocalSym iteration = new(slot, element, fe.Name);
                Declare(fe, fe.Name, iteration);
                _assigned.Add(iteration);
                _loopDepth++;
                CheckStmt(fe.Body);
                _loopDepth--;
                PopScope();
                break;
            }

            case ReturnStmt r:
            {
                Type want = _method is { Async: true } running ? AsyncResult(running, r) : _method?.Returns ?? Type.Void;

                if (r.Value is null)
                {
                    if (!want.IsVoid)
                    {
                        Error(r, $"this method returns '{want}', so 'return' needs a value");
                    }
                }
                else if (want.IsVoid)
                {
                    Error(r, "this method returns void, so 'return' cannot have a value");
                    CheckExpr(r.Value);
                }
                else
                {
                    // WHAT IS BEING RETURNED IS WHAT `new()` MEANS HERE.
                    // `public Type PointerTo() => new() { ... };` is how this
                    // compiler's own Types.cs is written three times over, and
                    // the return type is as good an answer as a declaration's.
                    Type? outerWanted = _wanted;

                    _wanted = want;

                    Type produced = CheckExpr(r.Value);

                    _wanted = outerWanted;
                    CheckAssignable(produced, want, r.Value, "return value");
                }
                break;
            }

            case BreakStmt or ContinueStmt:
                if (_loopDepth == 0)
                {
                    Error(s, $"'{(s is BreakStmt ? "break" : "continue")}' is only valid inside a loop");
                }
                break;

            case GotoCaseStmt jump:
            {
                if (_switches.Count == 0)
                {
                    Error(jump, "'goto case' is only valid inside a switch");
                    break;
                }

                SwitchStmt owner = _switches[^1];
                SwitchCase? target = null;

                if (jump.IsDefault)
                {
                    target = owner.Cases.FirstOrDefault(c => c.Pattern is null);
                }
                else if (jump.Value is { } wanted)
                {
                    CheckExpr(wanted);
                    long? value = ConstantValue(wanted, _thisType);

                    if (value is long constant)
                    {
                        target = owner.Cases.FirstOrDefault(c =>
                            c.Pattern is BinaryExpr { Op: BinOp.Eq } equality
                            && ConstantValue(equality.Right, _thisType) == constant);
                    }
                }

                if (target is null)
                {
                    Error(jump, jump.IsDefault
                        ? "this switch has no default label"
                        : "this switch has no matching constant case label");
                }
                else
                {
                    _r.GotoCases[jump] = target;
                }
                break;
            }

            case ThrowStmt t:
                CheckExpr(t.Value);
                break;

            case SwitchStmt sw:
            {
                Type subject = CheckExpr(sw.Subject);
                HashSet<LocalSym> beforeSwitch = new(_assigned, ReferenceEqualityComparer.Instance);
                List<HashSet<LocalSym>> continuingAssignments = new();

                // `case null:` PROVES THE OTHER ARMS. Reaching any of them
                // means the subject was not null, which is what C# knows and
                // what makes `switch (s) { case null: …; default: s.GetType() }`
                // -- the shape this compiler's own Gir.cs uses three times --
                // legal.
                List<Sym> proven = new();

                if (sw.Cases.Any(c => c.Pattern is BinaryExpr
                                      {
                                          Op: BinOp.Eq,
                                          Left: SubjectExpr,
                                          Right: LiteralExpr { Kind: Lit.Null },
                                      })
                    && Path(sw.Subject) is string spelt && _notNullPaths.Add(spelt))
                {
                    proven.Add(new PathSym(spelt));
                }

                // THE SUBJECT GOES IN A SLOT, ONCE, and every label reads that.
                // The labels are written over a SubjectExpr rather than over the
                // subject expression itself, so `switch (Read())` reads once
                // however many labels follow -- the old ladder re-emitted the
                // subject after every guard and every property load.
                //
                // Pushed for the whole switch and popped at the end, which is
                // what makes a switch inside a case body work: the inner one
                // pushes its own and the labels in it see that, exactly as a
                // nested pattern does.
                int held = NewSlot();

                _r.SwitchSubject[sw] = held;
                _subject.Add((held, subject));
                _switches.Add(sw);

                foreach (SwitchCase c in sw.Cases)
                {
                    _assigned.Clear();
                    _assigned.UnionWith(beforeSwitch);
                    PushScope();
                    _loopDepth++;

                    // A LABEL IS A CONDITION, checked as one. Any binding in it
                    // declares into the scope just pushed and so is visible in
                    // the body and nowhere else, which is what the five fields
                    // this replaced were doing by hand for the single shape of
                    // pattern they understood.
                    List<Sym> caseProof = new();
                    if (c.Pattern != null)
                    {
                        Type label = CheckExpr(c.Pattern);

                        if (!label.IsError && label.Prim != Prim.Bool)
                        {
                            Error(c.Pattern, $"a case label must be a bool, not '{label}'");
                        }
                        caseProof = Assume(c.Pattern, true);
                    }

                    foreach (Stmt body in c.Body)
                    {
                        CheckStmt(body);
                    }

                    // An empty section falls through to the following label;
                    // it is not a path out of the switch by itself. Only an
                    // empty FINAL section reaches the following statement.
                    if ((c.Body.Count > 0 && ReachesAfterSwitch(c.Body[^1]))
                        || (c.Body.Count == 0 && ReferenceEquals(c, sw.Cases[^1])))
                    {
                        continuingAssignments.Add(new HashSet<LocalSym>(
                            _assigned, ReferenceEqualityComparer.Instance));
                    }
                    _loopDepth--;
                    Forget(caseProof);
                    PopScope();
                }

                // A switch with no default may match no arm at all; that path
                // carries only what was assigned before the switch. Otherwise
                // the intersection of every arm that reaches the following
                // statement is exactly C#'s definite-assignment result.
                if (!sw.Cases.Any(c => c.Pattern is null))
                {
                    continuingAssignments.Add(beforeSwitch);
                }

                _assigned.Clear();
                if (continuingAssignments.Count > 0)
                {
                    _assigned.UnionWith(continuingAssignments[0]);
                    foreach (HashSet<LocalSym> path in continuingAssignments.Skip(1))
                    {
                        _assigned.IntersectWith(path);
                    }
                }
                else
                {
                    _assigned.UnionWith(beforeSwitch);
                }

                _subject.RemoveAt(_subject.Count - 1);
                _switches.RemoveAt(_switches.Count - 1);

                Forget(proven);
                break;
            }

            case TryStmt tr:
            {
                CheckBlock(tr.Body);

                foreach (CatchClause c in tr.Catches)
                {
                    PushScope();

                    // A CLAUSE WITH NO TYPE CATCHES EVERYTHING, and everything
                    // thrown is an Exception -- which is the type its variable
                    // has when it has one. The parser gives a nameless clause
                    // a hidden name when its body says `throw;`, so this is
                    // the shape a rethrow out of `catch { … }` arrives in.
                    Type caught = _r.Types.TryGetValue("Exception", out TypeSymbol? root)
                                ? new Type { Prim = Prim.Void, Symbol = root }
                                : Type.Error;

                    if (c.Type != null)
                    {
                        caught = Resolve(c.Type, _thisType);

                        // A clause has to name a class: the test the machine
                        // does is against a type bit, and a primitive has none.
                        // Refusing here is what stops the generator emitting a
                        // test that could never match.
                        if (caught.Symbol is TypeSymbol sym && sym.Kind == TypeKind.Class)
                        {
                            _r.CatchType[c] = sym;
                        }
                        else if (!caught.IsError)
                        {
                            Error(c, $"'catch' needs a class to test against, and '{c.Type.Name}' is not one");
                        }
                    }

                    if (c.Name != null)
                    {
                        int slot = NewSlot();
                        _r.CatchSlot[c] = slot;
                        LocalSym caughtLocal = new(slot, caught, c.Name);
                        Declare(c, c.Name, caughtLocal);
                        _assigned.Add(caughtLocal);
                    }
                    // THE FILTER IS CHECKED WITH THE NAME IN SCOPE, which is
                    // the point of it: `catch (E e) when (e.Code == 2)`.
                    if (c.When != null)
                    {
                        Type filter = CheckExpr(c.When);

                        if (!filter.IsError && filter.Prim != Prim.Bool)
                        {
                            Error(c.When, $"an exception filter must be a bool, not '{filter}'");
                        }
                    }

                    CheckBlock(c.Body);
                    PopScope();
                }

                if (tr.Finally != null)
                {
                    CheckBlock(tr.Finally);
                }
                break;
            }
        }
    }

    private void CheckCondition(Expr e)
    {
        Type t = CheckExpr(e);

        if (!t.IsError && t.Prim != Prim.Bool)
        {
            // No truthiness. An integer is not a condition, and saying so is
            // the difference between catching `if (x = 1)` and shipping it.
            Error(e, $"a condition must be 'bool', not '{t}'");
        }
    }

    /// <summary>The one place assignability and nullability are decided.</summary>
    private void CheckAssignable(Type from, Type to, Node at, string what)
    {
        if (from.IsError || to.IsError || Unmade(to))
        {
            return;
        }

        // Method groups have the same contextual delegate conversion in
        // assignments and returns as in arguments -- static ones, `this`'s,
        // and another object's (bound: MethodGroupLambda). Reuse the closure
        // lowering path rather than treating the unresolved group as void.
        if (at is Expr methodSource && !_r.Rewrites.ContainsKey(methodSource)
            && _r.Resolved.TryGetValue(methodSource, out Sym? methodSym)
            && methodSym is MethodGroupSym or CapturedMethodGroupSym
            && MethodGroupLambda(methodSource, to) is LambdaExpr methodWrapper)
        {
            _r.Rewrites[methodSource] = methodWrapper;
            CheckLambda(methodWrapper, to);
            return;
        }

        // AN ARRAY BECOMES A SPAN, which C# does with an implicit operator on
        // the type. There are no user-defined conversions here, so the compiler
        // makes it: the array is wrapped where it stands, and everything below
        // sees an ordinary construction.
        //
        // `AddData(bytes)` in this compiler's own Emitter.cs takes a
        // ReadOnlySpan<byte> and is handed a byte[] at every call.
        if (at is Expr array && from.IsArray && from.Element is Type held
            && to.Symbol is { } spanOwner
            && spanOwner.Decl is { Template: "ReadOnlySpan" or "Span", TemplateArgs.Count: 1 } span
            && Resolve(span.TemplateArgs[0], _thisType).Equals(held)
            && !_r.Rewrites.ContainsKey(array))
        {
            NewExpr made = new()
            {
                Type = new TypeRef { Name = spanOwner.Name, Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };

            made.Args.Add(array);
            _r.Rewrites[array] = made;
            CheckExpr(made);
            return;
        }

        // A STRING HANDED TO SOMETHING THAT READS A SEQUENCE gives up its
        // characters. .NET's string IS an IEnumerable<char>; there is no class
        // for string here -- it is a primitive the compiler knows -- so the
        // characters are materialised and carried by the same view an array of
        // them gets, which is the one thing that can answer Count, the indexer
        // and GetEnumerator.
        if (at is Expr letters && from.Prim == Prim.String && !from.IsArray
            && ArrayFace(Type.ArrayOf(Type.Char), to) is { } charFace
            && _r.Types.TryGetValue(TypeKey(charFace), out TypeSymbol? charFaceSym)
            && !_r.Rewrites.ContainsKey(letters))
        {
            CallExpr made = new()
            {
                Target = new MemberExpr
                {
                    Target = letters, Name = "ToCharArray",
                    Line = at.Line, Col = at.Col, File = at.File,
                },
                Line = at.Line, Col = at.Col, File = at.File,
            };

            _r.Rewrites[letters] = made;
            CheckExpr(made);

            // THE VIEW GOES ON THE CALL, not on the string: the string is still
            // evaluated for what it is inside the call that reads its
            // characters out.
            _r.Views[made] = ArrayView(Type.Char, charFaceSym);
            return;
        }

        // AND A STRING BECOMES ONE OF CHARACTERS. .NET declares an implicit
        // operator from string to ReadOnlySpan<char>; there are no user-defined
        // conversions here, so the compiler writes the call the operator would
        // make -- which is String.AsSpan, the same method the author would have
        // written by hand.
        if (at is Expr text && from.Prim == Prim.String && !from.IsArray
            && SpanHolds(to) is { Prim: Prim.Char } && ReadOnlySpanned(to)
            && !_r.Rewrites.ContainsKey(text))
        {
            CallExpr spanned = new()
            {
                Target = new MemberExpr
                {
                    Target = text, Name = "AsSpan", Line = at.Line, Col = at.Col, File = at.File,
                },
                Line = at.Line, Col = at.Col, File = at.File,
            };

            _r.Rewrites[text] = spanned;
            CheckExpr(spanned);
            return;
        }

        // A CONSTANT THAT FITS GOES WHERE IT FITS, which is C#'s rule and not a
        // relaxation of it: `byte b = 5;` is legal and `byte b = n;` is not,
        // even when n holds 5, because only the first can be checked. Every
        // table in this compiler's own Isa.cs is filled in this way --
        // `f[(byte)Fmt.R] = 0b0111;` -- and without it each of them needs a
        // cast that says nothing.
        // NOT INTO A NULLABLE, and that exception is not a detail: `int? n = 5;`
        // puts the five in a CELL, and the marking that says so is further down
        // this function. Returning early here skipped it, so the variable held
        // the number where a reference belonged and reading n.Value faulted on
        // address 5.
        if (at is Expr written && to.IsInteger && from.IsInteger && !to.Nullable
            && ConstantValue(written, _thisType) is long fits && Fits(fits, to))
        {
            return;
        }

        if (from.Prim == Prim.NullLiteral)
        {
            if (to.IsNullableValue)
            {
                // Null in a Nullable<T> is the null pointer, which is what a
                // null literal already evaluates to. Nothing to box.
                return;
            }

            // A MACHINE WORD TAKES NULL DIRECTLY. `object?` and a type
            // parameter's `T?` hold zero in the word; there is no cell and no
            // value type here to refuse.
            if (to.Prim == Prim.Any || to.ParamName != null)
            {
                return;
            }

            if (!to.IsReference)
            {
                Error(at, $"{what}: '{to}' is a value type and cannot be null");
            }
            else if (!to.Nullable)
            {
                Warning(at, $"{what}: '{to}' is not nullable; declare it as '{to}?' to allow null");
            }
            return;
        }

        // Reference annotations warn, but do not waive the underlying type
        // conversion check. A nullable Foo is still not an unrelated Bar.
        if (from.Nullable && !to.Nullable && to.IsReference)
        {
            Warning(at, $"{what}: '{from}' may be null but '{to}' may not");
        }
        else if (WeakensPromise(from, to))
        {
            Warning(at, $"{what}: an element of '{from}' may be null but '{to}' says its elements may not");
        }

        // A Nullable<T> WHERE A T IS WANTED is not a conversion the compiler
        // may make on its own -- it can fail, and C# makes you write .Value or
        // a cast so the place it can fail is visible.
        if (from.IsNullableValue && !to.IsNullableValue && !to.Nullable
            && Convertible(from.Underlying, to))
        {
            Error(at, $"{what}: '{from}' may have no value; use '.Value' or cast it to '{to}'");
            return;
        }

        if (Convertible(from, to) || Variant(from, to))
        {
            // A T BECOMING A T? IS A REAL CONVERSION, not merely permitted --
            // the value has to be put in a cell. Recorded here, which is the one
            // place that knows both what was written and what was wanted; the
            // code generator boxes whatever appears in this set.
            if (to.IsNullableValue && !from.IsNullableValue && at is Expr boxed)
            {
                _r.Boxes.Add(boxed);
            }

            // AND AN ARRAY GETS ITS HELPER. The conversion is real -- something
            // has to answer Count and the indexer -- and this is the one place
            // that knows both what was written and what was wanted.
            if (from.IsArray && at is Expr viewed && ArrayFace(from, to) is { } through
                && _r.Types.TryGetValue(TypeKey(through), out TypeSymbol? wantedFace))
            {
                _r.Views[viewed] = ArrayView(from.Element!, wantedFace);
            }
            return;
        }
        Error(at, $"{what}: cannot convert '{from}' to '{to}'");
    }

    /// <summary>
    /// The indexer overload an index of these types calls, or null when the
    /// type has none of that arity.
    ///
    /// BY PARAMETER TYPE, exactly as a method call chooses: `this[int]` and
    /// `this[string]` are two indexers in C# and both live on Dictionary's
    /// cousins here. Picking the first one with the right NUMBER of indices
    /// made the second unreachable and reported the first one's parameter type
    /// as the error.
    /// </summary>
    private MethodSymbol? IndexerFor(IEnumerable<MethodSymbol> candidates, List<Type> index, int arity)
    {
        List<MethodSymbol> fit = candidates.Where(m => m.Params.Count == arity).ToList();

        if (fit.Count <= 1)
        {
            return fit.FirstOrDefault();
        }

        bool Exact(MethodSymbol m) =>
            Enumerable.Range(0, index.Count).All(i => index[i].Equals(m.Params[i].Type));

        bool Takes(MethodSymbol m) =>
            Enumerable.Range(0, index.Count).All(i => index[i].IsError || Convertible(index[i], m.Params[i].Type));

        return fit.FirstOrDefault(Exact) ?? fit.FirstOrDefault(Takes) ?? fit[0];
    }

    /// <summary>
    /// Whether an argument fits a parameter, the way C# asks it at a call
    /// site: the types convert, OR what was written is a CONSTANT EXPRESSION
    /// whose value the parameter's type can hold.
    ///
    /// `new SymbolEntry(0, 0, 0, Elf.StbLocal, 0, 0)` is how every ELF symbol
    /// table this compiler writes is built, and four of those zeroes are
    /// uints. C# converts a constant int to a uint when the value fits and
    /// refuses a variable; the rule is about the VALUE, which is why it needs
    /// the expression and not only its type.
    /// </summary>
    private bool Fits(Type had, Type want, Expr? written)
        => had.IsError || Convertible(had, want) || Variant(had, want)
        || (written is not null && MethodGroupFits(written, want))
        || (written is not null && had.IsInteger && want.IsInteger && !want.Nullable
            && ConstantValue(written, _thisType) is long value && Fits(value, want));

    private bool MethodGroupFits(Expr written, Type wanted)
    {
        if (!_r.Resolved.TryGetValue(written, out Sym? symbol) || Grouped(symbol) is not { } methods)
            return false;
        MethodSymbol? invoke = wanted.Symbol?.FindMethods("Invoke").FirstOrDefault();
        return invoke is not null && methods.Any(method => method.Params.Count == invoke.Params.Count
            && method.TypeParams.Count == 0
            && (method.Returns.Prim == Prim.Void ? invoke.Returns.Prim == Prim.Void
                : Convertible(method.Returns, invoke.Returns))
            && method.Params.Zip(invoke.Params).All(pair => pair.First.ByRef == pair.Second.ByRef
                && pair.First.ReadOnly == pair.Second.ReadOnly
                && (pair.First.ByRef ? MethodSignatures.SameType(pair.First.Type, pair.Second.Type)
                    : Convertible(pair.Second.Type, pair.First.Type))));
    }

    /// <summary>
    /// The same type once every reference and array annotation is taken
    /// off, at every depth. A Nullable&lt;T&gt; value keeps its mark: `int?`
    /// is a different thing from `int`, a cell, not a promise.
    /// </summary>
    private static bool SameUnannotated(Type a, Type b)
    {
        Type x = a.IsNullableValue ? a : a.AsNonNullable();
        Type y = b.IsNullableValue ? b : b.AsNonNullable();
        if (x.IsArray || y.IsArray)
        {
            return x.IsArray && y.IsArray && x.ArrayRank == y.ArrayRank
                && x.PointerDepth == y.PointerDepth
                && x.Element is { } xe && y.Element is { } ye && SameUnannotated(xe, ye);
        }
        return x.Equals(y);
    }

    /// <summary>
    /// Whether a value read out of `from` may be null where `to` promises it
    /// is not, at any depth of array: the warning that goes with an
    /// annotation weakened by an identity conversion.
    /// </summary>
    private static bool WeakensPromise(Type from, Type to)
    {
        if (from.IsArray && to.IsArray && from.Element is { } fe && to.Element is { } te)
        {
            if (fe.Nullable && !te.Nullable && fe.IsReference) return true;
            return WeakensPromise(fe, te);
        }
        return false;
    }

    private bool Convertible(Type from, Type to)
    {
        // Anything is a machine word, which is the whole point of this type.
        if (to.Prim == Prim.Any || from.Prim == Prim.Any)
        {
            return true;
        }

        // A null literal converts to every reference type and Nullable<T>.
        // Reference annotations do not change conversion/overload membership
        // in C#. Nullability warnings belong in CheckAssignable
        // AFTER selecting the method, not in this applicability predicate.
        if (from.Prim == Prim.NullLiteral)
        {
            return to.IsReference || to.IsNullableValue;
        }

        if (from.AsNonNullable().Equals(to.AsNonNullable()))
        {
            return true;
        }

        // AN ARRAY OF THINGS IS AN ARRAY OF THINGS THAT MAY BE NULL, AT
        // EVERY DEPTH. A reference annotation is a promise about what is
        // read out, not a different type: `string?[]` and `string[]` are
        // the same array at run time, and so are `byte[]?[]` and
        // `byte[][]?`, which is what C# says too -- an identity conversion,
        // with a warning where a promise is weakened. Comparing only the
        // top annotation, and then only one level of element, refused
        // `byte[][]? ring = new byte[]?[n];` with the memorable "cannot
        // convert 'byte[]?[]' to 'byte[][]?'". The warning for the unsafe
        // direction is CheckAssignable's, after the conversion is allowed.
        if (from.IsArray && to.IsArray && SameUnannotated(from, to))
        {
            return true;
        }

        // AN UNMADE SPECIALISATION IS WHAT ITS TEMPLATE IS.
        //
        // A `List<KeyValuePair<…>>` from before the copy that spells it really
        // does implement that sequence -- the copy will say so in as many
        // words -- so a round that meets it before the copy exists has to
        // accept it, or LINQ over an inferred sequence is refused for exactly
        // one round and the compile stops there.
        if (Unmade(from) && to.Symbol is { Kind: TypeKind.Interface } asFace
            && from.Symbol?.Decl is { TypeParams.Count: > 0 } openFrom
            && openFrom.TypeParams.Count == from.Args.Count)
        {
            Dictionary<string, Type> mine = new(StringComparer.Ordinal);

            for (int i = 0; i < from.Args.Count; i++)
            {
                mine[openFrom.TypeParams[i].Name] = from.Args[i];
            }

            List<Type> wantedArgs = to.Args.Count > 0
                                  ? to.Args.ToList()
                                  : asFace.Decl is { Template: not null } spelt
                                    ? spelt.TemplateArgs.Select(a => Resolve(a, _thisType)).ToList()
                                    : new List<Type>();

            if (wantedArgs.Count > 0
                && OpenAs(openFrom, asFace.Decl?.Template ?? Bare(asFace.Name),
                          wantedArgs.Count, mine, 0) is { } through
                && through.Count == wantedArgs.Count)
            {
                bool same = true;

                for (int i = 0; i < through.Count; i++)
                {
                    same &= through[i].Equals(wantedArgs[i]);
                }

                if (same)
                {
                    return true;
                }
            }
        }

        // TUPLE CONVERSIONS ARE ELEMENT-WISE. `(FieldSymbol, ThisSym)` is a
        // `(FieldSymbol, Sym)` because ThisSym derives from Sym, just as the
        // corresponding two ordinary assignments would be legal.
        if (from.Symbol is { } fromTuple && to.Symbol is { } toTuple
            && fromTuple.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            && toTuple.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            && fromTuple.Fields.Count == toTuple.Fields.Count)
        {
            for (int i = 0; i < fromTuple.Fields.Count; i++)
            {
                if (!Convertible(fromTuple.Fields[i].Type, toTuple.Fields[i].Type))
                {
                    return false;
                }
            }
            return true;
        }

        // A SPAN IS A READ-ONLY SPAN, which is C#'s other implicit operator on
        // the type and means only that the caller promises not to write.
        if (from.Symbol?.Decl is { Template: "Span", TemplateArgs.Count: 1 } writable
            && to.Symbol?.Decl is { Template: "ReadOnlySpan", TemplateArgs.Count: 1 } readable
            && Resolve(writable.TemplateArgs[0], _thisType).Equals(Resolve(readable.TemplateArgs[0], _thisType)))
        {
            return true;
        }

        // AN ARRAY IS A SPAN, which overload resolution has to know as much as
        // assignment does -- CheckAssignable makes the conversion and this is
        // what lets a call reach the overload that wants one.
        if (from.IsArray && from.Element is Type element && SpanHolds(to) is { } spanned)
        {
            return spanned.Equals(element);
        }

        // A STRING IS A SEQUENCE OF CHARACTERS. .NET's string implements
        // IEnumerable<char>, which is what lets LINQ read one --
        // `text.All(char.IsLetterOrDigit)` is an ordinary line of C# and one
        // this compiler's own lexer writes.
        if (from.Prim == Prim.String && !from.IsArray
            && ArrayFace(Type.ArrayOf(Type.Char), to) is not null)
        {
            return true;
        }

        // AND A STRING IS A ReadOnlySpan<char>, which .NET spells as an
        // implicit operator on string itself. Every span overload of every
        // parser and comparer is called with an ordinary string in C#.
        if (from.Prim == Prim.String && SpanHolds(to) is { Prim: Prim.Char } && ReadOnlySpanned(to))
        {
            return true;
        }

        // A Nullable<T> holds a T, so anything a T converts to it can reach --
        // `long? x = 1;` and `int? a = 2; long? b = a;` both being ordinary C#.
        // Only in this direction: emptying one out is checked above.
        if (to.IsNullableValue && Convertible(from.Underlying, to.Underlying))
        {
            return true;
        }

        // A NATIVE INTEGER'S WIDTH IS THE TARGET'S, so the size rule below
        // cannot decide for it: on a 32-bit word it would let nint into int
        // and uint into nint, and C# allows neither. These are C#'s rules
        // for nint and nuint, which do not depend on the word.
        if (from.IsNative || to.IsNative)
        {
            return NativeConvertible(from.Prim, to.Prim);
        }

        // Widening only. A narrowing conversion loses information and must be
        // written as a cast so it is visible at the point it happens.
        if (from.IsInteger && to.IsInteger)
        {
            return to.Size >= from.Size;
        }

        if (from.IsInteger && to.IsFloat)
        {
            return true;
        }

        if (from.IsFloat && to.IsFloat)
        {
            return to.Size >= from.Size;
        }

        // AN ARRAY IS A SEQUENCE. `T[]` is an IReadOnlyList<T> in C#, and
        // `IReadOnlyList<Type> Args = Array.Empty<Type>();` is how this
        // compiler's own Types.cs starts one.
        if (from.IsArray && ArrayFace(from, to) != null)
        {
            return true;
        }

        // A SPECIALISATION GOES WHERE ITS CANONICAL COPY IS WANTED.
        //
        // `List<TypeRef>` and `List<__canon>` are one compiled routine already
        // -- that is what canonical instantiation means -- so a generic method
        // declared over `List<T>`, which is compiled once against the canonical
        // copy, takes any list whose element is a machine word.
        //
        // `List<int>` is NOT one of them, and is correctly refused here: its
        // elements are four bytes and the canonical copy strides by eight.
        if (from.Symbol?.Decl?.Canon is string canon && canon == to.Symbol?.Name)
        {
            return true;
        }

        if (from.Symbol != null && to.Symbol != null)
        {
            return from.Symbol.DerivesFrom(to.Symbol);
        }
        return false;
    }

    /// <summary>
    /// The implicit conversions to and from nint and nuint, as C# has them.
    ///
    /// Into a nint: any signed type no wider than a word and any unsigned type
    /// narrower than one. Into a nuint: any unsigned type no wider than a word.
    /// Out of a nint: to long and the floats; out of a nuint: to ulong and the
    /// floats. Everything else -- long to nint, uint to nint, nuint to long,
    /// int to nuint, one of them to the other -- is a cast.
    /// </summary>
    private static bool NativeConvertible(Prim from, Prim to)
    {
        if (from == to)
        {
            return true;
        }

        return to switch
        {
            Prim.NInt => from is Prim.I8 or Prim.I16 or Prim.I32 or Prim.U8 or Prim.U16 or Prim.Char,
            Prim.NUInt => from is Prim.U8 or Prim.U16 or Prim.U32 or Prim.Char,
            Prim.I64 or Prim.F32 or Prim.F64 => from == Prim.NInt,
            Prim.U64 => from == Prim.NUInt,
            _ => false,
        } || (from == Prim.NUInt && to is Prim.F32 or Prim.F64);
    }

    private static Type? CommonReference(Type first, Type second)
    {
        if (first.Symbol is null || second.Symbol is null)
        {
            return null;
        }

        for (TypeSymbol? candidate = first.Symbol; candidate is not null; candidate = candidate.Base)
        {
            if (second.Symbol.DerivesFrom(candidate))
            {
                return new Type
                {
                    Prim = Prim.Void, Symbol = candidate,
                    Nullable = first.Nullable || second.Nullable,
                };
            }
        }
        return null;
    }

    /// <summary>
    /// A common implemented interface for conditional-expression arms.
    /// Arrays and lists frequently meet as IReadOnlyList&lt;T&gt;; neither is the
    /// other's base class, but C# still chooses the interface both convert to.
    /// </summary>
    private Type? CommonInterface(Type first, Type second)
    {
        return SearchCommonInterface(first, second) ?? SearchCommonInterface(second, first);
    }

    private Type? SearchCommonInterface(Type from, Type other)
    {
        for (TypeSymbol? at = from.Symbol; at is not null; at = at.Base)
        {
            foreach (TypeSymbol face in at.Interfaces)
            {
                Type candidate = new() { Prim = Prim.Void, Symbol = face };
                if (Convertible(other, candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The root every reference answers to, built once and never declared.
    ///
    /// C# has System.Object and everything derives from it. There is no root
    /// class here -- see the ToString slot -- so the two members that matter
    /// are given a symbol of their own, at the slots every class reserves for
    /// them. Nothing inherits from this; it exists so that a value known only
    /// to be a machine word has somewhere to look its members up.
    /// </summary>
    private TypeSymbol Rooted()
    {
        if (_rooted != null)
        {
            return _rooted;
        }

        _rooted = new TypeSymbol { Name = "object", Kind = TypeKind.Class };

        MethodSymbol str = new()
        {
            Name = "ToString", Returns = Type.String, Owner = _rooted,
            VtableSlot = _r.ToStringSlot,
        };

        MethodSymbol same = new()
        {
            Name = "Equals", Returns = Type.Bool, Owner = _rooted,
            VtableSlot = _r.EqualsSlot,
        };

        same.Params.Add(new ParamSymbol { Name = "other", Type = Type.Any });

        MethodSymbol hashed = new()
        {
            Name = "GetHashCode", Returns = Type.I32, Owner = _rooted,
            VtableSlot = _r.HashSlot,
        };

        // THE STATIC PAIR, which a class calls unqualified in C# because it
        // derives from object. ReferenceEquals asks whether two references are
        // the SAME object, which is the question Equals exists to be able to
        // answer differently -- so a type that overrides Equals still needs a
        // way to ask the original.
        MethodSymbol identical = new()
        {
            Name = "ReferenceEquals", Returns = Type.Bool, Owner = _rooted, Static = true,
        };

        identical.Params.Add(new ParamSymbol { Name = "a", Type = Type.Any });
        identical.Params.Add(new ParamSymbol { Name = "b", Type = Type.Any });

        MethodSymbol pair = new()
        {
            Name = "Equals", Returns = Type.Bool, Owner = _rooted, Static = true,
        };

        pair.Params.Add(new ParamSymbol { Name = "a", Type = Type.Any });
        pair.Params.Add(new ParamSymbol { Name = "b", Type = Type.Any });

        _rooted.Methods.Add(str);
        _rooted.Methods.Add(same);
        _rooted.Methods.Add(hashed);
        _rooted.Methods.Add(identical);
        _rooted.Methods.Add(pair);
        return _rooted;
    }

    private TypeSymbol? _rooted;

    /// <summary>
    /// Extension methods of this name whose receiver accepts this type.
    ///
    /// Only convertibility is asked here, not an exact match: `Where` is
    /// declared over `List&lt;T&gt;`, which after erasure is the canonical copy,
    /// and a `List&lt;Node&gt;` reaches it the same way it reaches any other
    /// method taking one.
    /// </summary>
    /// <summary>
    /// Whether an open `List&lt;T&gt;` receiver accepts this argument.
    ///
    /// Asked with a throwaway binding, because at this point the question is
    /// only whether the extension is a CANDIDATE -- what T actually is gets
    /// settled by inference once the overload is chosen.
    /// </summary>
    private bool Applies(MethodSymbol m, Type want, Type got)
        => (want.Args.Count > 0 || want.IsArray)
        && Unify(m, want, got, new Dictionary<string, Type>(StringComparer.Ordinal));

    // (Applies asks only whether the extension is a candidate.)

    private List<MethodSymbol> Extension(Type target, string name)
    {
        TypeDecl? written = (_scope ?? _thisType)?.Decl;
        string within = _member?.Scope != null ? _member.Namespace : written?.Namespace ?? "";
        FileScope? file = _member?.Scope ?? written?.Scope;
        List<MethodSymbol> InNamespaces(IEnumerable<string> spaces)
        {
            HashSet<string> namespaces = spaces.ToHashSet(StringComparer.Ordinal);
            foreach (string space in namespaces)
            {
                try { _requireExtensions?.Invoke(space, name); }
                catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
            }
            List<MethodSymbol> found = new();
            foreach (TypeSymbol holder in _r.Types.Values.Where(type => namespaces.Contains(type.Decl?.Namespace ?? "")))
            foreach (MethodSymbol m in holder.Methods.Where(method => method.Name == name))
            {
                if (m.Static && m.Params.Count > 0 && m.Decl?.Params.FirstOrDefault()?.IsThis == true
                    && (Convertible(target, m.Params[0].Type)
                        || m.Params[0].Type.ParamName != null
                        || Applies(m, m.Params[0].Type, target)))
                {
                    found.Add(m);
                }
            }
            return found;
        }
        for (string? scope = within; scope is not null; scope = scope.Length == 0 ? null : Enclosing(scope) ?? "")
        {
            List<MethodSymbol> local = InNamespaces(new[] { scope });
            if (local.Count > 0) return local;
            List<MethodSymbol> imported = InNamespaces(file?.Imports.Where(import => import.In == scope)
                .Select(import => import.Namespace) ?? Enumerable.Empty<string>());
            if (imported.Count > 0) return imported;
        }
        return new List<MethodSymbol>();
    }

    /// <summary>
    /// Turns a lambda into a class, once the wanted type says what it is.
    ///
    /// `x => x + 1` is not anything until something wants it. What that
    /// something wants is an interface with an Invoke -- Func and Action in the
    /// standard library are exactly that -- so the lambda becomes a class
    /// implementing that interface, with a field per captured local and the
    /// lambda's body as its Invoke.
    ///
    /// Which is what C# does too. The difference is only that C# writes a
    /// delegate where this writes an interface, and task 57 says that spelling
    /// has to change; the shape of the lowering does not.
    ///
    /// CAPTURE IS BY VALUE. C# captures a local by reference, so a lambda sees
    /// later assignments to it and can assign back. Here the value is copied
    /// into the closure when it is made. The two are indistinguishable unless
    /// something assigns to the captured variable afterwards, so assigning to
    /// one inside a lambda is refused rather than silently doing nothing --
    /// see the check in CheckAssign.
    /// </summary>
    private Type CheckLambda(LambdaExpr lam, Type wanted)
    {
        TypeSymbol? face = wanted.Symbol;

        // NOT YET, IF WHAT IT HAS TO BE IS STILL OPEN.
        //
        // A lambda passed to `Where<T>` is wanted as a `Func<T,bool>`, and T is
        // not decided until the copy for this call site is compiled -- which
        // happens between rounds. Building a closure against the template would
        // give its Invoke the template's own parameter names, and the body
        // would be checked against `A` instead of Node.
        //
        // The round that specialises the method sees a concrete Func and does
        // this properly. Saying nothing here is right: the call has already
        // been written down as one needing a copy.
        if (face?.Decl?.TypeParams.Count > 0)
        {
            return wanted;
        }

        MethodSymbol? invoke = face?.FindMethods("Invoke")
                                    .FirstOrDefault(m => m.Params.Count == lam.Params.Count);

        if (invoke is null)
        {
            Error(lam, face is null
                ? $"there is nothing here for a lambda to be; '{wanted}' is not a type with an 'Invoke'"
                : $"'{face.Name}' has no 'Invoke' taking {lam.Params.Count} argument(s)");
            return Type.Error;
        }

        // ---- pass one: which of the enclosing locals does it read? ----------
        Dictionary<string, Type>? outerCaptured = _captured;
        int outerFloor = _lambdaFloor;
        Dictionary<string, Type> captured = new(StringComparer.Ordinal);

        _captured = captured;
        _quiet++;
        // C# 8+ permits a nested function's parameters and locals to shadow
        // enclosing names. Its own ordinary blocks still cannot shadow its
        // parameters/locals. Keep capture lookup independent of that boundary.
        PushScope(functionBoundary: true);
        _lambdaFloor = _scopes.Count - 1;

        for (int i = 0; i < lam.Params.Count; i++)
        {
            Declare(lam, lam.Params[i].Name,
                    new ParamSym(i, ContextualParameterType(wanted, invoke, i), lam.Params[i].Name, false));
        }

        Look(lam, ContextualMemberResult(wanted, invoke));
        PopScope();
        _quiet--;
        _captured = outerCaptured;
        _lambdaFloor = outerFloor;

        // A lambda inside a lambda captures through the outer one, so anything
        // the inner one reached for has to be captured by the outer one too.
        if (outerCaptured != null)
        {
            foreach ((string name, Type held) in captured)
            {
                if (Lookup(name) is LocalSym or ParamSym)
                {
                    outerCaptured[name] = held;
                }
            }
        }

        // ---- the class ------------------------------------------------------
        // NAMED SO IT CAN BE A LABEL. The name reaches the assembler as the
        // label of the closure's Invoke, and angle brackets are not something
        // a label may contain -- the label then never resolves, the vtable slot
        // holds zero, and calling the lambda jumps to address nothing.
        // COUNTED SEPARATELY, and never from how many have been RECORDED.
        //
        // A lambda's body is looked at more than once -- the wanted type can
        // start open and become concrete a round later -- and each look built a
        // class. Naming them from Closures.Count meant the second class for one
        // lambda replaced the first under the same key, the count did not move,
        // and the NEXT lambda was given a name that was already taken. Two
        // classes called Lambda$1, one vtable, and a predicate that runs
        // somebody else's body: `l.Where(p).Sum(q)` summed the whole of l.
        // A source-ordered work identity, not a process-wide increment, lets
        // future worker contexts name closures without scheduling dependence.
        string name2 = lam.GroupIdentity is string groupIdentity
            ? $"Lambda$Group${(_thisType?.Key ?? "")}${groupIdentity}"
            : $"Lambda${_closureOwner}${_closures++}";
        // A BOUND method group made before is the same class again, with this
        // conversion's own receiver as the value of its one field.
        bool bound = _boundTargets.TryGetValue(lam, out Expr? boundReceiver);
        if (bound && _boundClosures.TryGetValue(name2, out ClosureInfo? boundProto))
        {
            _r.Closures[lam] = new ClosureInfo(boundProto.Type,
                new List<(FieldSymbol, Sym)> { (boundProto.Captures[0].Field, new ValueSym(boundReceiver!)) },
                boundProto.Invoke);
            return wanted;
        }

        // A method group already turned into a closure in this type is that
        // closure again, when nothing but the plain `this` could be captured;
        // a nested capture would need a different source for the same field.
        if (!bound && lam.GroupIdentity is not null && _groupClosures.TryGetValue(name2, out ClosureInfo? sharedClosure)
            && (_method is { Static: true } || (_capturedThisType is null && _thisType is not null)))
        {
            _r.Closures[lam] = sharedClosure;
            return wanted;
        }
        TypeDecl decl = new() { Name = name2, Kind = TypeKind.Class, LocalOnly = true, File = _in, Line = lam.Line, Col = lam.Col };

        // KEYED UNDER THE TYPE THE LAMBDA WAS WRITTEN IN, so that names inside
        // its body are looked up from where they were written. A closure is a
        // class this compiler invents, and checking the body inside it made the
        // enclosing type's own nested types invisible: `foreach (Section s in
        // table)` in a local function of ImageFile stopped naming a type.
        TypeSymbol closure = new()
        {
            Name = name2,
            Key = _thisType is null ? name2 : _thisType.Key + "." + name2,
            Kind = TypeKind.Class,
            Decl = decl,
        };

        closure.Interfaces.Add(face!);

        int at = Target.Current.ObjectHeaderBytes;                 // past descriptor and sync word
        List<(FieldSymbol, Sym)> fields = new();

        TypeSymbol? enclosingThis = null;
        Sym? enclosingThisSource = null;

        // A bound method group's closure holds its receiver and nothing else,
        // so that every conversion of the method has the same layout.
        if (bound)
        {
            FieldSymbol targetField = new()
            {
                Name = BoundTargetField, Type = _r.TypeOf(boundReceiver!), Owner = closure, Offset = at,
            };
            at += Math.Max(8, targetField.Type.Size);
            closure.Fields.Add(targetField);
            fields.Add((targetField, new ValueSym(boundReceiver!)));
        }
        else if (_method is { Static: false })
        {
            // A lambda nested inside another closure needs the ORIGINAL
            // receiver, not the intermediate closure object. The outer
            // closure already carries it in its hidden $this field, so copy
            // that value directly into the inner closure.
            if (_capturedThisType is not null && _capturedThisField is not null)
            {
                enclosingThis = _capturedThisType;
                enclosingThisSource = new FieldSym(_capturedThisField);
            }
            else
            {
                enclosingThis = _thisType;
                if (enclosingThis is not null)
                {
                    enclosingThisSource = new ThisSym(
                        new Type { Prim = Prim.Void, Symbol = enclosingThis });
                }
            }
        }
        FieldSymbol? thisField = null;

        if (enclosingThis is not null && enclosingThisSource is not null)
        {
            Type thisType = new() { Prim = Prim.Void, Symbol = enclosingThis };
            thisField = new FieldSymbol
            {
                Name = "$this", Type = thisType, Owner = closure, Offset = at,
            };
            at += 8;
            closure.Fields.Add(thisField);
            fields.Add((thisField, enclosingThisSource));
        }

        foreach ((string field, Type held) in captured)
        {
            // THE ENCLOSING LOCAL ITSELF, recorded now while its scope is still
            // open. The code generator reads it where the lambda was written,
            // and by then there is no name left to look up.
            Sym? from = Lookup(field);

            if (from is null
                && _thisType is not null
                && _thisType.Name.StartsWith("Lambda$", StringComparison.Ordinal)
                && _thisType.FindField(field) is FieldSymbol outerCapture)
            {
                from = new FieldSym(outerCapture);
            }

            if (from is null)
            {
                continue;
            }

            FieldSymbol f = new()
            {
                Name = field, Type = held, Owner = closure, Offset = at,

                // THE CELL, NOT THE VALUE, when the source local was boxed --
                // which it always is if a lambda reached it, since reaching it
                // is what boxes it. This is the whole of capture by reference:
                // the closure and the enclosing method hold the same address.
                Boxed = from is LocalSym { Boxed: true }
                      || from is FieldSym { Field.Boxed: true },
            };

            at += Math.Max(8, held.Size);
            closure.Fields.Add(f);
            fields.Add((f, from));
            if (LocalFunctionDeclaration(from) is { } localFunction)
                _capturedLocalFunctions[f] = localFunction;
        }

        closure.InstanceSize = Math.Max(Target.Current.ObjectHeaderBytes, at);

        Type closureReturns = ContextualMemberResult(wanted, invoke);
        MethodDecl body = new()
        {
            Name = "Invoke", Mods = Mods.Public, Returns = new TypeRef { Name = "" },
            // An async expression lambda returns what its TASK holds, so
            // `async () => await Work()` of a Func<Task> is a statement.
            Body = lam.BlockBody ?? Wrap(lam.Body!, lam.Async ? AsyncBodyType(closureReturns) : closureReturns),
            File = _in, Line = lam.Line, Col = lam.Col,
        };

        MethodSymbol run = new()
        {
            Name = "Invoke", Returns = closureReturns, Owner = closure,
            Decl = body, VtableSlot = invoke.VtableSlot, Async = lam.Async,
        };

        for (int i = 0; i < lam.Params.Count; i++)
        {
            run.Params.Add(new ParamSymbol { Name = lam.Params[i].Name, Type = ContextualParameterType(wanted, invoke, i) });
            body.Params.Add(new Param { Name = lam.Params[i].Name, Type = new TypeRef { Name = "" }, Line = lam.Line, Col = lam.Col });
        }

        closure.Methods.Add(run);
        _r.Types[name2] = closure;
        _r.Methods[body] = run;
        _r.Closures[lam] = new ClosureInfo(closure, fields, run);
        if (bound) _boundClosures[name2] = _r.Closures[lam];
        else if (lam.GroupIdentity is not null) _groupClosures[name2] = _r.Closures[lam];

        // WHAT WAS PROVED ABOUT A CAPTURE GOES IN WITH IT.
        //
        // C#'s rule is that the null state where a lambda is WRITTEN flows into
        // its body, and here it is stronger than that: capture is by value, so
        // a variable proved non-null at this point cannot become null inside
        // however long the lambda lives.
        //
        // `other != null && !Args.Where((a, i) => !a.Equals(other.Args[i])).Any()`
        // is the line in this compiler's own Types.cs that needs it -- the test
        // is outside the lambda and the read is inside.
        // AND WHAT THE BODY PROVES STAYS INSIDE IT. Capture is by value here,
        // so nothing a lambda does can change what is known about the
        // enclosing method's locals -- and reading one inside must not lose
        // what was proved outside. `while (q.TryDequeue(out Interval? cur)) {
        // active.RemoveAll(a => a.End < cur.Start); Use(cur); }` is the shape:
        // the proof is the while's, the lambda merely reads it, and Use(cur)
        // was then told cur may be null.
        HashSet<Sym> outerNotNull = new(_notNull);
        HashSet<string> outerPaths = new(_notNullPaths, StringComparer.Ordinal);
        List<Sym> insideLambda = new();

        foreach ((FieldSymbol f, Sym from) in fields)
        {
            if (_notNull.Contains(from) && _notNull.Add(new FieldSym(f)))
            {
                insideLambda.Add(new FieldSym(f));
            }
        }

        // ---- pass two: the body, for real, with the captures as fields ------
        TypeSymbol? wasThis = _thisType;
        TypeSymbol? wasLexicalType = _lexicalType;
        TypeSymbol? wasCapturedThis = _capturedThisType;
        FieldSymbol? wasCapturedThisField = _capturedThisField;
        MethodSymbol? wasMethod = _method;
        int wasSlot = _nextSlot;
        List<LocalScope> wasScopes = new(_scopes);

        _scopes.Clear();
        _thisType = closure;
        _lexicalType = wasLexicalType ?? wasThis;
        _capturedThisType = enclosingThis;
        _capturedThisField = thisField;
        _method = run;
        _nextSlot = 0;
        PushScope(functionBoundary: true);

        for (int i = 0; i < lam.Params.Count; i++)
        {
            // A LAMBDA'S PARAMETERS ARE THE INVOKE'S PARAMETERS, not locals of
            // it. Declared as locals they read as zero: the caller puts
            // arguments where a parameter lives, and nothing had put anything
            // in the slot a local would have used.
            Declare(lam, lam.Params[i].Name,
                    new ParamSym(i, run.Params[i].Type, lam.Params[i].Name, false));
        }

        Look(lam, closureReturns);
        _r.FrameSize[body] = _nextSlot;
        PopScope();

        _scopes.Clear();
        _scopes.AddRange(wasScopes);
        _thisType = wasThis;
        _lexicalType = wasLexicalType;
        _capturedThisType = wasCapturedThis;
        _capturedThisField = wasCapturedThisField;
        _method = wasMethod;
        _nextSlot = wasSlot;
        Forget(insideLambda);

        _notNull.Clear();
        _notNull.UnionWith(outerNotNull);
        _notNullPaths.Clear();
        _notNullPaths.UnionWith(outerPaths);

        return wanted;
    }

    /// <summary>Checks a lambda's body, whichever of the two shapes it is.</summary>
    private void Look(LambdaExpr lam, Type returns)
    {
        if (lam.BlockBody != null)
        {
            foreach (Stmt s in lam.BlockBody.Statements)
            {
                CheckStmt(s);
            }
            return;
        }

        Type? outerWanted = _wanted;
        // What the body has to be is what Invoke returns -- or, for an async
        // lambda, what its task holds: a `new()` with no type, and a lambda
        // -- `a => b => a + b` -- both need telling.
        // The capture pass still runs in the enclosing method's context.
        // Its return type is unrelated to this lambda: using it here can
        // reject target-typed new before visiting any initializer captures.
        Type? produces = lam.Async ? AsyncBodyType(returns) : returns;
        // WHATEVER THE BODY IS, and not only the two shapes that cannot be
        // checked without it. C# target-types the whole of an expression-bodied
        // lambda from what it returns, which is what makes `o switch {
        // RegOperand r => …, SlotOperand s => … }` in an `Operand Op(Operand)`
        // an Operand rather than two unrelated classes.
        _wanted = produces is { IsVoid: false, IsError: false } ? produces : outerWanted;
        Type produced = CheckExpr(lam.Body!);
        _wanted = outerWanted;

        if (produces is { IsVoid: false, IsError: false })
        {
            CheckAssignable(produced, produces, lam.Body!, "the value a lambda produces");
        }
    }

    /// <summary>An expression-bodied lambda, as the block it means.</summary>
    private static Block Wrap(Expr value, Type returns)
    {
        Block block = new() { Line = value.Line, Col = value.Col };

        block.Statements.Add(returns.IsVoid
            ? new ExprStmt { Expr = value, Line = value.Line, Col = value.Col }
            : new ReturnStmt { Value = value, Line = value.Line, Col = value.Col });

        return block;
    }

    /// <summary>
    /// Puts a call's named arguments into parameter order, and fills any gap
    /// a defaulted parameter leaves.
    ///
    /// Which method it is has not been decided yet, so the ONE candidate whose
    /// parameter names all match is what settles it. That is enough for what
    /// named arguments are actually for -- saying which of several similar
    /// parameters you mean -- and an overload set where two members take the
    /// same names in different positions is not something worth guessing at.
    /// </summary>
    private void Reorder(CallExpr c)
    {
        if (c.ArgNames.Count == 0 || c.ArgNames.All(n => n is null))
        {
            c.ArgNames.Clear();
            return;
        }

        if (!_r.Resolved.TryGetValue(c.Target, out Sym? sym) || sym is not MethodGroupSym group)
        {
            // The target has not been resolved yet on this path -- a call
            // through a value, say. Names cannot be matched to anything, and
            // the ordinary "not a method" diagnostic below is the right one.
            return;
        }

        foreach (MethodSymbol m in group.Methods)
        {
            Expr?[] placed = new Expr?[m.Params.Count];
            bool fits = true;

            for (int i = 0; i < c.Args.Count && fits; i++)
            {
                int at = c.ArgNames[i] is string name
                       ? m.Params.FindIndex(p => p.Name == name)
                       : i;

                if (at < 0 || at >= placed.Length || placed[at] != null)
                {
                    fits = false;
                    break;
                }

                placed[at] = c.Args[i];
            }

            // A HOLE IS ONLY ALLOWED WHERE THE PARAMETER HAS A DEFAULT, which
            // is the whole reason to name an argument in the first place: to
            // skip the ones before it.
            for (int i = 0; i < placed.Length && fits; i++)
            {
                if (placed[i] is null)
                {
                    if (m.Decl?.Params.ElementAtOrDefault(i) is { Default: not null } spare)
                    {
                        placed[i] = Written(m, spare);
                    }
                    else
                    {
                        fits = false;
                    }
                }
            }

            if (!fits)
            {
                continue;
            }

            c.Args.Clear();
            c.Args.AddRange(placed!);
            c.ArgNames.Clear();
            return;
        }

        Error(c, $"no overload of '{group.Methods[0].Name}' takes arguments named "
                + string.Join(", ", c.ArgNames.Where(n => n != null).Select(n => $"'{n}'")));
        c.ArgNames.Clear();
    }

    /// <summary>
    /// Works out a generic method's type arguments from what it was given.
    ///
    /// `U.Pick(4, 5)` rather than `U.Pick&lt;int&gt;(4, 5)` -- which is how C#
    /// is written, and how the compiler's own source writes it.
    ///
    /// A parameter that IS a type parameter binds it to whatever was passed;
    /// every other parameter has to accept its argument the ordinary way. The
    /// first binding wins and the rest must agree with it, so `Pick(1, "two")`
    /// finds no overload rather than quietly picking one of the two.
    /// </summary>
    /// <summary>
    /// Whether inference is on its SECOND attempt, where an unmade
    /// specialisation is read through the interfaces its template declares.
    ///
    /// Only then, because the first attempt is what every call that resolves
    /// today resolves by, and reading a template's base list is a last resort
    /// -- it is right for `d.OrderBy(f).ToList()`, whose specialisation the
    /// next round writes, and it must not be allowed to answer a question the
    /// ordinary rules already answer.
    /// </summary>
    private bool _throughTemplates;

    /// <summary>
    /// C#'s better conversion from a LAMBDA (12.6.4.5): of two candidates
    /// that both take it, the one whose delegate returns exactly what the
    /// lambda produces, and failing that the one whose return converts to
    /// the other's. `xs.Sum(x => x.Bytes)` over a long is Sum(Func&lt;T,
    /// long&gt;), not the Func&lt;T, double&gt; one declared first. True when
    /// `a` is better for some lambda and worse for none.
    /// </summary>
    private bool BetterLambdas(MethodSymbol a, Dictionary<string, Type> aBound, MethodSymbol b,
                               Dictionary<string, Type> bBound, List<Expr> written)
    {
        bool better = false;
        for (int i = 0; i < written.Count && i < a.Params.Count && i < b.Params.Count; i++)
        {
            if (written[i] is not LambdaExpr lam) continue;
            Type? made = Produces(a, a.Params[i].Type, lam, aBound);
            if (made is null || made.IsError) continue;
            Type ga = Close(Substitute(Invoked(a.Params[i].Type)?.Returns ?? Type.Error, Applied(a.Params[i].Type)), aBound);
            Type gb = Close(Substitute(Invoked(b.Params[i].Type)?.Returns ?? Type.Error, Applied(b.Params[i].Type)), bBound);
            if (ga.IsError || gb.IsError || ga.Equals(gb)) continue;
            bool aExact = ga.Equals(made), bExact = gb.Equals(made);
            if (aExact && !bExact) { better = true; continue; }
            if (bExact && !aExact) return false;
            bool aToB = Convertible(ga, gb), bToA = Convertible(gb, ga);
            if (aToB && !bToA) { better = true; continue; }
            if (bToA && !aToB) return false;
        }
        return better;
    }

    private bool Infer(MethodSymbol m, List<Type> args, List<Expr> written, out Dictionary<string, Type> bound)
    {
        bool ok = InferOnce(m, args, written, out bound);

        // AND AGAIN THROUGH THE TEMPLATE'S OWN INTERFACES when something the
        // RESULT is made of was left unbound.
        //
        // Leaving a type argument unbound is ordinary and right: a T that only
        // ever appears as an argument was erased to the canonical machine word
        // before the checker saw it, and one compiled copy serves every list of
        // words. It is only when the RETURN type is made of that T that an
        // unbound one matters -- `d.OrderBy(f).ToList()` hands back a
        // `List<T>`, and a foreach over that takes a T apart. So the second
        // attempt is made exactly there, and kept only if it answers.
        Dictionary<string, Type> had = bound;

        if (m.TypeParams.Any(t => !had.ContainsKey(t) && Mentions(m.Returns, t)))
        {
            bool was = _throughTemplates;

            _throughTemplates = true;
            try
            {
                if (InferOnce(m, args, written, out Dictionary<string, Type> more)
                    && m.TypeParams.All(t => more.ContainsKey(t) || !Mentions(m.Returns, t)))
                {
                    bound = more;
                    return true;
                }
            }
            finally
            {
                _throughTemplates = was;
            }

            bound = had;
        }

        return ok;
    }

    private bool InferOnce(MethodSymbol m, List<Type> args, List<Expr> written, out Dictionary<string, Type> bound)
    {
        bound = new Dictionary<string, Type>(StringComparer.Ordinal);

        for (int i = 0; i < m.Params.Count && i < args.Count; i++)
        {
            // A lambda has no type until the parameter gives it one. Trying to
            // unify its temporary `void` marker with Func<T,R> rejects every
            // ordinary LINQ call before the receiver has had a chance to bind
            // T. The second pass below checks the lambda once those input
            // bindings are known and uses its result to infer R.
            if (i < written.Count && IsFunctionSource(written[i]))
            {
                continue;
            }

            if (!Unify(m, m.Params[i].Type, args[i], bound))
            {
                return false;
            }
        }

        // AND WHAT THE LAMBDAS PRODUCE, which is where the rest of it comes
        // from. `Select<T,R>` learns T from the list it was given and R from
        // nothing else at all -- only from what `x => x.Name` evaluates to.
        //
        // Done in a second pass because the lambda's own parameter types come
        // from the first: R cannot be worked out until T is.
        for (int i = 0; i < m.Params.Count && i < written.Count; i++)
        {
            LambdaExpr? lam = written[i] as LambdaExpr
                           ?? MethodGroupLambda(written[i], Close(m.Params[i].Type, bound));

            if (lam is not null && Produces(m, m.Params[i].Type, lam, bound) is Type made)
            {
                Type gives = Substitute(Invoked(m.Params[i].Type)?.Returns ?? Type.Error,
                                        Applied(m.Params[i].Type));

                // A concrete delegate result is a constraint too. Ignoring
                // failure keeps (for example) the int-returning Sum overload
                // eligible for a lambda whose result is long or double.
                if (!Unify(m, gives, made, bound)) return false;
            }
        }

        // NOT "every type parameter got bound". A T that appears only as a type
        // ARGUMENT -- `List<T>` -- was erased to the canonical word before the
        // checker ever saw it, because one compiled copy serves every list of
        // machine words. There is nothing left to bind and nothing that needs
        // binding: string.Join has exactly this shape.
        //
        // A T that appears BARE is bound here, and one that appears nowhere at
        // all is caught at the return type, where its absence actually matters.
        //
        // EVERY BINDING HAS TO BE A MACHINE WORD, because the method is one
        // compiled routine and that routine strides by eight. Binding T to int
        // would have `T[]` read a four-byte array eight bytes at a time -- a
        // silent wrong answer, which is the one outcome worth refusing for.
        return bound.Values.All(IsWord);
    }

    /// <summary>Whether a type is made of the named type parameter.</summary>
    private static bool Mentions(Type t, string name)
    {
        if (t.ParamName == name)
        {
            return true;
        }

        if (t.Element is Type element && Mentions(element, name))
        {
            return true;
        }

        foreach (Type a in t.Args)
        {
            if (Mentions(a, name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The methods a symbol names, when it names a group of them.</summary>
    private static List<MethodSymbol>? Grouped(Sym sym) => sym switch
    {
        MethodGroupSym group => group.Methods,
        CapturedMethodGroupSym captured => captured.Methods,
        _ => null,
    };

    /// <summary>Whether an expression is already, or can become, a function value.</summary>
    private bool IsFunctionSource(Expr e)
        => e is LambdaExpr
        || (e is ConditionalExpr c && (IsFunctionSource(c.Then) || IsFunctionSource(c.Else)))
        || (_r.Resolved.TryGetValue(e, out Sym? sym)
            && sym is MethodGroupSym or CapturedMethodGroupSym);

    /// <summary>
    /// Converts a C# method group to the lambda-shaped closure this runtime
    /// uses for delegates. `items.Select(CheckExpr)` becomes the semantic
    /// equivalent of `items.Select(x => CheckExpr(x))`; instance receivers are
    /// captured by the existing lambda path and static receivers remain plain
    /// calls. The original syntax node is retained as the call target so normal
    /// overload resolution still chooses the actual method.
    /// </summary>
    private LambdaExpr? MethodGroupLambda(Expr source, Type wanted)
    {
        if (!_r.Resolved.TryGetValue(source, out Sym? sym)
            || sym is not (MethodGroupSym or CapturedMethodGroupSym))
        {
            return null;
        }

        MethodSymbol? invoke = wanted.Symbol?.FindMethods("Invoke").FirstOrDefault();
        if (invoke is null)
        {
            return null;
        }

        // A RECEIVER THAT IS NOT `this` IS A VALUE, taken now. The call in
        // the delegate is made on the closure's $target field instead.
        Expr callTarget = source;
        Expr? receiver = null;
        if (source is MemberExpr member && sym is MethodGroupSym instanceGroup
            && instanceGroup.Methods.Any(m => !m.Static)
            && member.Target is not ThisExpr and not BaseExpr && !member.NullConditional)
        {
            receiver = member.Target;
            MemberExpr onTarget = new()
            {
                Target = new NameExpr { Name = BoundTargetField, Line = source.Line, Col = source.Col },
                Name = member.Name, Line = source.Line, Col = source.Col,
            };
            onTarget.TypeArgs.AddRange(member.TypeArgs);
            callTarget = onTarget;
        }

        CallExpr call = new() { Target = callTarget, Line = source.Line, Col = source.Col };
        LambdaExpr made = new() { Body = call, Line = source.Line, Col = source.Col };
        // Which method the group means here is the one whose arity the
        // delegate's Invoke has; its identity names the closure class, so a
        // second conversion of the same method anywhere in the type is the
        // same class and the two compare equal, as C# requires of delegates.
        IReadOnlyList<MethodSymbol> candidates = sym is MethodGroupSym mg ? mg.Methods : ((CapturedMethodGroupSym)sym).Methods;
        MethodSymbol? chosen = candidates.FirstOrDefault(m => m.Params.Count == invoke.Params.Count);
        if (chosen is not null) made.GroupIdentity = ClosureIdentity.Of(chosen) + (receiver is null ? "" : "$bound");
        if (receiver is not null) _boundTargets[made] = receiver;

        for (int i = 0; i < invoke.Params.Count; i++)
        {
            string name = "$arg" + i;
            made.Params.Add(new Param
            {
                Name = name,
                Type = new TypeRef { Name = "object", Line = source.Line, Col = source.Col },
                Line = source.Line,
                Col = source.Col,
            });
            call.Args.Add(new NameExpr { Name = name, Line = source.Line, Col = source.Col });
        }
        return made;
    }

    /// <summary>
    /// Whether a type can be a generic method's type argument.
    ///
    /// Anything with a name, now. It used to have to be a class or an
    /// interface: a generic method was compiled ONCE over the machine word, so
    /// the single copy strode by eight and asked its values things through the
    /// vtable at offset zero -- and a string, an array and a long have none.
    ///
    /// A copy is compiled per type argument now, so the copy knows what it has
    /// and none of that applies. What is still needed is a NAME, because the
    /// call site records its type arguments as written-down types for the
    /// specialisation pass to substitute.
    /// </summary>
    private static bool IsWord(Type t) => RefOf(t) != null;

    /// <summary>
    /// The class that presents an array of this element as that interface.
    ///
    /// One per (element size, interface), because that is all they differ by:
    /// the body of Count is a length instruction and the body of the indexer is
    /// a load with a stride, and neither knows anything else about the element.
    /// The code generator lays both down; there is no source for them because
    /// there is nothing a source could say that the two instructions do not.
    ///
    /// It implements the interface for real -- the vtable slots are the
    /// interface's own -- so everything reaching it does so by ordinary
    /// dispatch, and `Args.Count` inside a generic method compiled against
    /// IReadOnlyList finds it without knowing an array is behind it.
    /// </summary>
    /// <summary>
    /// What an expression's type IS, without the checking counting.
    ///
    /// A foreach has to know what it is looping over before it can decide what
    /// to rewrite it into, and the rewrite contains the same expression -- so
    /// this asks, then puts back the two things that would otherwise be said
    /// twice: a diagnostic, and a request for a generic method to be copied at
    /// some type. Everything else the checker records is keyed by the node and
    /// is simply written again with the same answer.
    /// </summary>
    private Type Peek(Expr e)
    {
        int errors = _r.Errors.Count;
        int warnings = _r.Warnings.Count;
        int wanted = _r.Wanted.Count;
        Type had = CheckExpr(e);

        _r.Errors.RemoveRange(errors, _r.Errors.Count - errors);
        _r.Warnings.RemoveRange(warnings, _r.Warnings.Count - warnings);
        _r.Wanted.RemoveRange(wanted, _r.Wanted.Count - wanted);
        return had;
    }

    /// <summary>
    /// The statements a `foreach` over a collection stands for, or null when
    /// the thing cannot be looped over at all.
    ///
    /// TWO SHAPES, and C# has both. The first is the one it prefers: anything
    /// with a GetEnumerator is walked with one, whatever its type says it
    /// implements -- the pattern, not the interface, exactly as C# resolves it.
    /// The second is a plain index loop over anything with a Count and an
    /// indexer, which is what a list is; C# gets there through IEnumerable and
    /// arrives at the same elements in the same order, and this way a list
    /// costs no allocation to walk.
    /// </summary>
    private Stmt? Iterate(ForeachStmt fe, Type seq)
    {
        int n = _iterations++;

        // AN ARRAY, when it is being taken apart: the same index loop, over the
        // length instead of a Count. Only reached for a deconstructing loop --
        // an ordinary one keeps the code generator's own tighter path.
        if (seq.IsArray && fe.Bindings is { } split)
        {
            string walked = $"$sequence${n}";
            string cursor = $"$index${n}";
            Block inside = new() { Line = fe.Line, Col = fe.Col };
            string element = $"$element${n}";
            IndexExpr take = new()
            {
                Target = new NameExpr { Name = walked, Line = fe.Line, Col = fe.Col },
                Line = fe.Line, Col = fe.Col,
            };

            take.Args.Add(new NameExpr { Name = cursor, Line = fe.Line, Col = fe.Col });
            inside.Statements.Add(new LocalDecl
            {
                Name = element, Init = take, Line = fe.Line, Col = fe.Col,
            });
            inside.Statements.AddRange(Deconstruct(
                fe,
                new NameExpr { Name = element, Line = fe.Line, Col = fe.Col },
                split,
                seq.Element ?? Type.Error));
            inside.Statements.Add(fe.Body);

            ForStmt stepping = new()
            {
                Init = new LocalDecl
                {
                    Type = new TypeRef { Name = "int", Line = fe.Line, Col = fe.Col },
                    Name = cursor,
                    Init = new LiteralExpr
                    {
                        Kind = Lit.Int, Text = "0", IntValue = 0, Line = fe.Line, Col = fe.Col,
                    },
                    Line = fe.Line, Col = fe.Col,
                },
                Cond = new BinaryExpr
                {
                    Op = BinOp.Lt,
                    Left = new NameExpr { Name = cursor, Line = fe.Line, Col = fe.Col },
                    Right = new MemberExpr
                    {
                        Target = new NameExpr { Name = walked, Line = fe.Line, Col = fe.Col },
                        Name = "Length", Line = fe.Line, Col = fe.Col,
                    },
                    Line = fe.Line, Col = fe.Col,
                },
                Body = inside,
                Line = fe.Line, Col = fe.Col,
            };

            stepping.Step.Add(new UnaryExpr
            {
                Op = UnOp.PostInc,
                Operand = new NameExpr { Name = cursor, Line = fe.Line, Col = fe.Col },
                Line = fe.Line, Col = fe.Col,
            });

            Block whole = new() { Line = fe.Line, Col = fe.Col };

            whole.Statements.Add(new LocalDecl
            {
                Name = walked, Init = fe.Sequence, Line = fe.Line, Col = fe.Col,
            });
            whole.Statements.Add(stepping);
            return whole;
        }

        if (seq.Symbol is not TypeSymbol had)
        {
            return null;
        }
        Block outer = new() { Line = fe.Line, Col = fe.Col };

        Expr Named(string name) => new NameExpr { Name = name, Line = fe.Line, Col = fe.Col };
        Expr On(Expr target, string member)
            => new MemberExpr { Target = target, Name = member, Line = fe.Line, Col = fe.Col };
        Expr Called(Expr target, string member)
            => new CallExpr { Target = On(target, member), Line = fe.Line, Col = fe.Col };

        if (Reachable(had, "GetEnumerator").FirstOrDefault(m => m.Params.Count == 0) is { } walk)
        {
            string walker = $"$enumerator${n}";
            Block body = new() { Line = fe.Line, Col = fe.Col };

            if (fe.Bindings is { } taken)
            {
                string element = $"$element${n}";

                // WHAT THE ENUMERATOR HANDS BACK, which is what is being taken
                // apart -- asked here rather than by checking the lowering,
                // because the lowering has to be BUILT knowing whether this is
                // a tuple (its elements by position) or anything else (its own
                // Deconstruct).
                // WITH THE SEQUENCE'S OWN ARGUMENTS PUT IN. The enumerator
                // found on a template is the TEMPLATE's, so its Current is a
                // `T`; what the sequence was constructed with is the only
                // thing that says which T. It shows whenever the sequence is
                // an inferred one -- `d.OrderBy(p => p.Key).ToList()` -- whose
                // specialisation the round after this one writes.
                Type walked = Close(walk.Returns, Received(seq, had));

                Type each = walked.Symbol is TypeSymbol e
                          ? Close(Reachable(e, "get_Current").FirstOrDefault()?.Returns ?? Type.Error,
                                  Received(walked, e))
                          : Type.Error;

                body.Statements.Add(new LocalDecl
                {
                    Name = element, Init = On(Named(walker), "Current"),
                    Line = fe.Line, Col = fe.Col,
                });
                body.Statements.AddRange(Deconstruct(fe, Named(element), taken, each));
            }
            else
            {
                body.Statements.Add(new LocalDecl
                {
                    Type = fe.Type, Name = fe.Name, Init = On(Named(walker), "Current"),
                    Line = fe.Line, Col = fe.Col,
                });
            }

            body.Statements.Add(fe.Body);

            outer.Statements.Add(new LocalDecl
            {
                Name = walker, Init = Called(fe.Sequence, "GetEnumerator"),
                Line = fe.Line, Col = fe.Col,
            });
            outer.Statements.Add(new WhileStmt
            {
                Cond = Called(Named(walker), "MoveNext"), Body = body,
                Line = fe.Line, Col = fe.Col,
            });
            return outer;
        }

        // A COUNT AND AN INDEXER, which is a list -- or a LENGTH and an indexer,
        // which is a span. C# walks a span by index too, for the same reason:
        // there is nothing to allocate an enumerator on.
        string howMany = Reachable(had, "get_Count").Any() ? "Count"
                       : Reachable(had, "get_Length").Any() ? "Length"
                       : "";

        if (howMany.Length == 0 || !Reachable(had, "get_Item").Any(m => m.Params.Count == 1))
        {
            return null;
        }

        string over = $"$sequence${n}";
        string at = $"$index${n}";
        Block stepped = new() { Line = fe.Line, Col = fe.Col };

        IndexExpr read = new() { Target = Named(over), Line = fe.Line, Col = fe.Col };

        read.Args.Add(Named(at));

        if (fe.Bindings is { } names)
        {
            string element = $"$element${n}";
            Type each = Reachable(had, "get_Item").FirstOrDefault(m => m.Params.Count == 1)?.Returns
                     ?? Type.Error;


            stepped.Statements.Add(new LocalDecl
            {
                Name = element, Init = read, Line = fe.Line, Col = fe.Col,
            });
            stepped.Statements.AddRange(Deconstruct(fe, Named(element), names, each));
        }
        else
        {
            stepped.Statements.Add(new LocalDecl
            {
                Type = fe.Type, Name = fe.Name, Init = read, Line = fe.Line, Col = fe.Col,
            });
        }

        stepped.Statements.Add(fe.Body);

        ForStmt loop = new()
        {
            Init = new LocalDecl
            {
                Type = new TypeRef { Name = "int", Line = fe.Line, Col = fe.Col },
                Name = at,
                Init = new LiteralExpr { Kind = Lit.Int, Text = "0", IntValue = 0, Line = fe.Line, Col = fe.Col },
                Line = fe.Line, Col = fe.Col,
            },
            Cond = new BinaryExpr
            {
                Op = BinOp.Lt, Left = Named(at), Right = On(Named(over), howMany),
                Line = fe.Line, Col = fe.Col,
            },
            Body = stepped,
            Line = fe.Line, Col = fe.Col,
        };

        loop.Step.Add(new UnaryExpr
        {
            Op = UnOp.PostInc, Operand = Named(at), Line = fe.Line, Col = fe.Col,
        });

        outer.Statements.Add(new LocalDecl
        {
            Name = over, Init = fe.Sequence, Line = fe.Line, Col = fe.Col,
        });
        outer.Statements.Add(loop);
        return outer;
    }

    /// <summary>
    /// Taking one value apart into several names.
    ///
    /// TWO WAYS, and C# has both. A TUPLE comes apart by position, because its
    /// elements are what it is. Anything else comes apart through a Deconstruct
    /// method with an `out` per name -- which is how walking a dictionary reads
    /// the way it does, since what a dictionary hands back is a KeyValuePair
    /// and the pair is what knows how to split itself.
    ///
    /// The Deconstruct form is an ordinary call with ordinary out arguments, so
    /// the names are declared by the call exactly as `TryGetValue(k, out V v)`
    /// declares one. Nothing new happens below the checker for it.
    /// </summary>
    private List<Stmt> Deconstruct(Node fe, Expr whole, List<Binding> names, Type had)
    {
        List<Stmt> taken = new();

        if (had.Symbol is TypeSymbol from
         && from.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
         && from.Fields.Count == names.Count)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Nested is null && names[i].Name == "_")
                {
                    continue;
                }

                MemberExpr element = new()
                {
                    Target = whole, Name = from.Fields[i].Name,
                    Line = names[i].Line, Col = names[i].Col,
                };

                // A PLACE THAT ALREADY EXISTS IS ASSIGNED, not declared:
                // `(a, b) = (b, a)` swaps two variables and declares nothing.
                if (names[i].Target is { } place)
                {
                    taken.Add(new ExprStmt
                    {
                        Expr = new AssignExpr
                        {
                            Target = place, Value = element,
                            Line = names[i].Line, Col = names[i].Col,
                        },
                        Line = names[i].Line, Col = names[i].Col,
                    });
                    continue;
                }

                taken.Add(new LocalDecl
                {
                    Type = names[i].Type, Name = names[i].Name,
                    Init = element,
                    Line = names[i].Line, Col = names[i].Col,
                });

                // A TARGET WRITTEN AS A LIST comes apart out of the element it
                // stands for, by the same two rules.
                if (names[i].Nested is { } deeper)
                {
                    taken.AddRange(Deconstruct(
                        fe,
                        new NameExpr { Name = names[i].Name, Line = names[i].Line, Col = names[i].Col },
                        deeper,
                        from.Fields[i].Type));
                }
            }
            return taken;
        }

        CallExpr call = new()
        {
            Target = new MemberExpr
            {
                Target = whole, Name = "Deconstruct", Line = fe.Line, Col = fe.Col,
            },
            Line = fe.Line, Col = fe.Col,
        };

        // WHAT EACH `out` HANDS BACK, for the targets that are lists
        // themselves: the one Deconstruct this type offers for this many names.
        MethodSymbol? splitter = had.Symbol is TypeSymbol owner
                               ? Reachable(owner, "Deconstruct")
                                 .FirstOrDefault(m => m.Params.Count == names.Count)
                               : null;

        foreach (Binding one in names)
        {
            // AN EXISTING PLACE IS WRITTEN THROUGH A HIDDEN NAME and copied
            // into afterwards: `out` declares, and a deconstructing assignment
            // does not.
            string name = one.Name == "_" ? $"$discard${taken.Count}" : one.Name;

            call.Args.Add(new RefArgExpr
            {
                Target = new NameExpr { Name = name, Line = one.Line, Col = one.Col },
                IsOut = true, Declare = one.Type, Name = name,
                Line = one.Line, Col = one.Col,
            });
        }

        taken.Add(new ExprStmt { Expr = call, Line = fe.Line, Col = fe.Col });

        foreach (Binding one in names)
        {
            if (one.Target is not { } place)
            {
                continue;
            }

            taken.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = place,
                    Value = new NameExpr { Name = one.Name, Line = one.Line, Col = one.Col },
                    Line = one.Line, Col = one.Col,
                },
                Line = one.Line, Col = one.Col,
            });
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Nested is not { } deeper)
            {
                continue;
            }

            taken.AddRange(Deconstruct(
                fe,
                new NameExpr { Name = names[i].Name, Line = names[i].Line, Col = names[i].Col },
                deeper,
                splitter is null ? Type.Error : splitter.Params[i].Type));
        }
        return taken;
    }

    /// <summary>
    /// Every method of that name this type can reach: its own, its bases', and
    /// those of every interface any of them implements.
    ///
    /// FindMethods walks the base chain only, which is right for a class and
    /// wrong for an interface -- an interface's members are inherited through
    /// Interfaces, not Base, so `IReadOnlyList`'s Count is invisible to it.
    /// </summary>
    private static List<MethodSymbol> Reachable(TypeSymbol t, string name)
    {
        List<MethodSymbol> found = new();

        void Walk(TypeSymbol at)
        {
            found.AddRange(at.Methods.Where(m => m.Name == name));

            if (at.Base != null)
            {
                Walk(at.Base);
            }

            foreach (TypeSymbol face in at.Interfaces)
            {
                Walk(face);
            }
        }

        Walk(t);
        return found;
    }

    /// <summary>
    /// What `x.ToString()` means when x has no vtable to find one in, or null
    /// when it has one and the ordinary call is right.
    ///
    /// A number becomes String.FromInt, a bool String.FromBool, and an ENUM
    /// becomes a switch over its members answering their names -- which is what
    /// C# answers, and is a thing only the compiler can write, since the names
    /// exist nowhere at run time.
    ///
    /// The rewritten expression is checked in place of this one, so everything
    /// below the checker sees an ordinary call and knows nothing about it.
    /// </summary>
    /// <summary>
    /// The array `Enum.GetValues&lt;T&gt;()` stands for, or null when T is not
    /// an enum this compilation declared.
    /// </summary>
    /// <summary>
    /// An enum's members, written out as the array C# would have written --
    /// the values themselves, or their names.
    ///
    /// IN VALUE ORDER, which is the order .NET reports them in and the order
    /// the code generator's own table is in, so that the two agree member for
    /// member. Because it is an array literal it costs a real array at every
    /// call site, which is right: .NET's GetValues and GetNames each answer a
    /// fresh array the caller may write to.
    /// </summary>
    private NewExpr Listed(TypeSymbol chosen, Node at, bool values)
    {
        NewExpr listed = new()
        {
            Type = new TypeRef { Name = values ? chosen.Name : "string", Line = at.Line, Col = at.Col },
            Elements = new List<Expr>(),
            Line = at.Line, Col = at.Col,
        };

        foreach ((string member, long value) in chosen.EnumValues.OrderBy(m => m.Value))
        {
            listed.Elements.Add(values
                ? new CastExpr
                {
                    Type = new TypeRef { Name = chosen.Name, Line = at.Line, Col = at.Col },
                    Operand = new LiteralExpr
                    {
                        Kind = Lit.Int, IntValue = value,
                        Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Line = at.Line, Col = at.Col,
                    },
                    Line = at.Line, Col = at.Col,
                }
                : new LiteralExpr { Kind = Lit.Str, Text = member, Line = at.Line, Col = at.Col });
        }

        return listed;
    }

    /// <summary>
    /// THE STATIC HALF OF System.Enum, which only the compiler can answer:
    /// an enum's members exist at compile time and nowhere else.
    ///
    /// Both spellings .NET has -- `Enum.GetName&lt;Colour&gt;(c)` and the older
    /// `Enum.GetName(typeof(Colour), c)` -- mean the same thing and get one
    /// implementation. The two that are simply a list become that list, as
    /// the array C# would have written; the rest read the table the code
    /// generator writes for the enum, which is the same table `e.ToString()`
    /// reads (Lowering.Enum.cs).
    ///
    /// Null when this is not one of them, so an ordinary member lookup can
    /// report what it usually would.
    /// </summary>
    private Type? EnumStatic(MemberExpr named, CallExpr call)
    {
        TypeSymbol? chosen = null;
        int skip = 0;

        // THROUGH THE ORDINARY TYPE LOOKUP, and not the flat table: the table
        // is keyed by the whole dotted name, so `Enum.GetValues<Fmt>()` written
        // inside a namespace found nothing and the call was reported as a
        // missing class called Enum.
        if (named.TypeArgs.Count == 1
            && FindType(named.TypeArgs[0].Name, out TypeSymbol? byArgument)
            && byArgument is { Kind: TypeKind.Enum })
        {
            chosen = byArgument;
        }
        else if (call.Args.Count > 0 && call.Args[0] is TypeOfExpr spelt
                 && FindType(spelt.Type.Name, out TypeSymbol? byTypeof)
                 && byTypeof is { Kind: TypeKind.Enum })
        {
            chosen = byTypeof;
            skip = 1;
        }

        if (chosen is null)
        {
            return null;
        }

        Type asEnum = new() { Prim = chosen.EnumUnderlying, Symbol = chosen };
        List<Expr> rest = call.Args.Skip(skip).ToList();

        switch (named.Name)
        {
            // THE MEMBERS THEMSELVES. This is what makes
            // `new byte[Enum.GetValues<Fmt>().Length]` -- a table sized by the
            // enum it is indexed by -- possible at all.
            case "GetValues" when rest.Count == 0:
            case "GetNames" when rest.Count == 0:
            {
                NewExpr listed = Listed(chosen, call, named.Name == "GetValues");

                _r.Rewrites[call] = listed;
                return CheckExpr(listed);
            }

            case "GetName" when rest.Count == 1:
            case "IsDefined" when rest.Count == 1:
            {
                Type had = CheckExpr(rest[0]);

                if (!had.IsError && !ReferenceEquals(had.Symbol, chosen) && !had.IsInteger)
                {
                    Error(rest[0], $"'{named.Name}' wants a '{chosen.Name}', not a '{had}'");
                }
                _r.EnumStatics[call] = (chosen, named.Name);
                return named.Name == "GetName" ? Type.String.AsNullable() : Type.Bool;
            }

            // THE TEXT BACK INTO A VALUE. .NET reads a member's name, a
            // comma-separated list of them, or a plain number, and Parse
            // throws where TryParse answers false.
            case "Parse" when rest.Count is 1 or 2:
            case "TryParse" when rest.Count is 2 or 3:
            {
                bool tries = named.Name == "TryParse";
                int wanted = tries ? rest.Count - 1 : rest.Count;
                Type text = CheckExpr(rest[0]);

                if (!text.IsError && text.Prim != Prim.String)
                {
                    Error(rest[0], $"'{named.Name}' wants the text to parse, not a '{text}'");
                }

                if (wanted == 2 && CheckExpr(rest[1]) is { IsError: false, Prim: not Prim.Bool } fold)
                {
                    Error(rest[1], $"'{named.Name}' takes whether to ignore case, not a '{fold}'");
                }

                _r.EnumStatics[call] = (chosen, named.Name);

                if (!tries)
                {
                    return asEnum;
                }

                // `out var c` NAMES NO TYPE, and the one it wants belongs to
                // no overload here: the enum is already known, so the variable
                // is brought into being with it rather than waiting for a
                // resolution that will not happen.
                if (rest[^1] is not RefArgExpr { IsOut: true } into)
                {
                    Error(call, "the last argument of 'TryParse' is passed 'out'");
                    return Type.Bool;
                }

                if (into.Declare is null && into.Name is not null)
                {
                    LocalSym made = new(NewSlot(), asEnum, into.Name);

                    Declare(into, into.Name, made);
                    _assigned.Add(made);
                    CheckExpr(into.Target);
                }
                else
                {
                    if (CheckExpr(into) is { IsError: false } holds
                        && !ReferenceEquals(holds.Symbol, chosen))
                    {
                        Error(into, $"'TryParse' writes a '{chosen.Name}', not a '{holds}'");
                    }

                    // WRITTEN THROUGH, so what it held before does not matter:
                    // the same note CheckCall leaves for an ordinary `out`.
                    if (into.Target is NameExpr spoken && Lookup(spoken.Name) is LocalSym filled)
                    {
                        _assigned.Add(filled);
                    }
                }
                return Type.Bool;
            }
        }
        return null;
    }

    private Expr? Stringify(MemberExpr spelt, CallExpr call)
    {
        Type had = Peek(spelt.Target);

        // AN ENUM SAYS WHAT ITS MEMBER IS CALLED, and `"" + e` is where that
        // already happens: the code generator writes a table of the names for
        // every enum a program renders and reads it with one call (see
        // Lowering.Enum.cs), and the empty string is folded away there.
        //
        // Said this way rather than expanded here as a switch per call site
        // because the two HAVE to agree -- a `[Flags]` value is "Read, Run"
        // and a value no member has is the number, whichever of the two forms
        // the program wrote -- and because a switch rebuilt at every use is a
        // string literal and a jump table each time for something the image
        // already holds once.
        if (had.Symbol is { Kind: TypeKind.Enum })
        {
            return new BinaryExpr
            {
                Op = BinOp.Add,
                Left = new LiteralExpr { Kind = Lit.Str, Text = "", Line = call.Line, Col = call.Col },
                Right = spelt.Target,
                Line = call.Line, Col = call.Col,
            };
        }

        if (had.Prim is Prim.Bool)
        {
            return Call("String", "FromBool", call, spelt.Target);
        }

        // A CHAR IS THE CHARACTER, NOT ITS NUMBER. C# answers "a" for
        // 'a'.ToString(), and char is an integer here, so it has to be taken
        // out of the way of String.FromInt before that arm claims it.
        if (had.Prim is Prim.Char && !had.Nullable)
        {
            return Call("String", "FromByte", call, spelt.Target);
        }

        if (had.IsInteger && !had.Nullable)
        {
            // ulong.ToString() is not long.ToString(): see String.FromUInt.
            bool unsigned = had.Prim is Prim.U64 || (had.Prim is Prim.NUInt && Target.Current.WordSize == 8);

            return Call("String", unsigned ? "FromUInt" : "FromInt", call, spelt.Target);
        }

        return null;
    }

    /// <summary>
    /// A call to one of the standard library's statics, built where something
    /// else was written.
    ///
    /// QUALIFIED, and that is not decoration. A bare `String` means whatever
    /// `String` means where the rewrite lands, and this compiler's own Types.cs
    /// has `public static readonly Type String` -- so `x.ToString()` inside
    /// that class became a call on a FIELD. A qualified name is resolved by its
    /// last part against the type table and nothing else can shadow it, which
    /// is also how C# spells its way out of exactly this collision.
    /// </summary>
    private static CallExpr Call(string owner, string method, Node at, params Expr[] arguments)
    {
        CallExpr made = new()
        {
            Target = new MemberExpr
            {
                Target = new MemberExpr
                {
                    Target = new NameExpr { Name = "System", Line = at.Line, Col = at.Col },
                    Name = owner, Line = at.Line, Col = at.Col,
                },
                Name = method, Line = at.Line, Col = at.Col,
            },
            Line = at.Line, Col = at.Col,
        };

        made.Args.AddRange(arguments);
        return made;
    }

    /// <summary>
    /// The class a tuple of these element types is.
    ///
    /// ONE PER SHAPE, written by the checker and shared by everything with that
    /// shape -- which is what makes `(int, string)` in two files one type, and
    /// is why the names are not part of it.
    ///
    /// C# has ValueTuple in its library and the compiler knows it specially.
    /// There is no library type here, for a reason worth stating: a tuple's
    /// element types are whatever the elements turn out to be, and this
    /// compiler expands generics BEFORE it checks anything -- so a written
    /// `ValueTuple&lt;int, string&gt;` could be instantiated and `(1, "x")`
    /// never could. Synthesising it is how the second one works, and it is the
    /// same answer the array-as-a-sequence helper reached.
    ///
    /// No type bit and so no type TEST: `o is (int, string)` will not answer
    /// true. Nothing in C# source that this compiler has to read does that, and
    /// giving one out here would mean claiming a bit after they were numbered.
    /// </summary>
    private TypeSymbol TupleType(IReadOnlyList<Type> elements, IReadOnlyList<string>? names = null)
    {
        // SPELLED THE WAY THE MONOMORPHISER SPELLS IT. A specialisation's name
        // writes a nested or namespaced type with the separator the name itself
        // uses, so `Corsac.Lang.ParamSymbol` is `Corsac$Lang$ParamSymbol`.
        // Naming the shape with the dots left in made a SECOND class for a
        // shape that already had one, with no element names on it -- and
        // `p.First` over a Zip of two ParamSymbols was told the tuple has no
        // such member.
        string name = TypeRef.Tuple + "$"
                    + string.Join("$", elements.Select(e => (NameOf(e) ?? "word")
                        .Replace("<", "_").Replace(">", "").Replace(", ", "_").Replace(".", "$")));

        if (_r.Types.TryGetValue(name, out TypeSymbol? already))
        {
            Remember(already, names);
            return already;
        }

        TypeDecl decl = new() { Name = name, Kind = TypeKind.Class, File = _in };
        TypeSymbol tuple = new() { Name = name, Kind = TypeKind.Class, Decl = decl, Structural = true };
        int at = 8;                             // past the vtable

        for (int i = 0; i < elements.Count; i++)
        {
            int size = Math.Max(1, elements[i].Size);

            at = (at + size - 1) / size * size; // natural alignment, as LayOut does
            tuple.Fields.Add(new FieldSymbol
            {
                Name = "Item" + (i + 1), Type = elements[i], Owner = tuple, Offset = at,
            });
            at += size;
        }

        tuple.InstanceSize = (at + 7) & ~7;
        _r.Types[name] = tuple;
        Remember(tuple, names);
        return tuple;
    }

    /// <summary>
    /// Keeps a shape's element names, and forgets them again the moment two
    /// places disagree. See TypeSymbol.TupleNames for why the fallback exists.
    /// </summary>
    private static void Remember(TypeSymbol shape, IReadOnlyList<string>? names)
    {
        if (names is null || names.All(string.IsNullOrEmpty))
        {
            return;
        }

        if (!shape.TupleNamings.Any(had => had.SequenceEqual(names)))
        {
            shape.TupleNamings.Add(names.ToArray());
        }

        if (!shape.TupleNamesSeen)
        {
            shape.TupleNamesSeen = true;
            shape.TupleNames = names.ToArray();
            return;
        }

        if (shape.TupleNames is { } was && !was.SequenceEqual(names))
        {
            shape.TupleNames = null;
        }
    }

    private TypeSymbol ArrayView(Type element, TypeSymbol face)
    {
        string name = $"ArrayView${face.Name}";

        if (_r.Types.TryGetValue(name, out TypeSymbol? already))
        {
            return already;
        }

        TypeDecl decl = new() { Name = name, Kind = TypeKind.Class, File = _in };
        TypeSymbol view = new() { Name = name, Kind = TypeKind.Class, Decl = decl, Structural = true };

        // EVERY SEQUENCE INTERFACE AN ARRAY HAS, not only the one asked for.
        //
        // .NET gives T[] the whole set -- IEnumerable<T>, IReadOnlyCollection<T>
        // and IReadOnlyList<T> -- and code takes advantage: an operator
        // declared over IEnumerable<T> asks at run time whether what it was
        // handed is a list, so that it can count and index rather than walk.
        // A view that implemented only the interface it was converted to
        // answered no and then had to walk, through a GetEnumerator nothing
        // had written.
        foreach (TypeSymbol also in SequenceFaces(element, face))
        {
            view.Interfaces.Add(also);
        }

        view.InstanceSize = Target.Current.ObjectHeaderBytes + Target.Current.WordSize;
        view.Fields.Add(new FieldSymbol
        {
            Name = "items", Type = Type.ArrayOf(element), Owner = view,
            Offset = Target.Current.ObjectHeaderBytes,
        });

        // THE INTERFACES' OWN SLOTS, so a caller that has only one of them
        // reaches these without knowing what it is holding.
        foreach (TypeSymbol implemented in view.Interfaces)
        {
            foreach (MethodSymbol want in implemented.Methods)
            {
                if (view.Methods.Any(had => had.Name == want.Name
                                         && had.Params.Count == want.Params.Count))
                {
                    continue;
                }

                MethodSymbol made = new()
                {
                    Name = want.Name, Returns = want.Returns, Owner = view,
                    VtableSlot = want.VtableSlot,
                };

                foreach (ParamSymbol p in want.Params)
                {
                    made.Params.Add(new ParamSymbol { Name = p.Name, Type = p.Type });
                }

                view.Methods.Add(made);
            }
        }

        _r.Types[name] = view;
        _r.ArrayViews.Add(view);

        // AND SOMETHING TO WALK IT WITH. GetEnumerator has to hand back an
        // IEnumerator<T>, and the only one an array could borrow belongs to a
        // list this program may never have made. So the view brings its own,
        // written the same way the view is: two fields and two methods, over
        // the same array.
        if (view.Methods.FirstOrDefault(x => x.Name == "GetEnumerator" && x.Params.Count == 0)
            is { Returns.Symbol: { } walks })
        {
            ArrayWalker(element, walks);
        }
        return view;
    }

    /// <summary>The enumerator an array view hands back: an index into the array.</summary>
    private TypeSymbol ArrayWalker(Type element, TypeSymbol face)
    {
        string name = $"ArrayEnumerator${face.Name}";

        if (_r.Types.TryGetValue(name, out TypeSymbol? already))
        {
            return already;
        }

        TypeDecl decl = new() { Name = name, Kind = TypeKind.Class, File = _in };
        TypeSymbol walker = new() { Name = name, Kind = TypeKind.Class, Decl = decl, Structural = true };
        int header = Target.Current.ObjectHeaderBytes;
        int word = Target.Current.WordSize;

        walker.Interfaces.Add(face);
        walker.InstanceSize = header + word + 4;
        walker.Fields.Add(new FieldSymbol
        {
            Name = "items", Type = Type.ArrayOf(element), Owner = walker, Offset = header,
        });
        walker.Fields.Add(new FieldSymbol
        {
            Name = "at", Type = Type.I32, Owner = walker, Offset = header + word,
        });

        foreach (MethodSymbol want in face.Methods)
        {
            MethodSymbol made = new()
            {
                Name = want.Name, Returns = want.Returns, Owner = walker,
                VtableSlot = want.VtableSlot,
            };

            foreach (ParamSymbol p in want.Params)
            {
                made.Params.Add(new ParamSymbol { Name = p.Name, Type = p.Type });
            }

            walker.Methods.Add(made);
        }

        _r.Types[name] = walker;
        _r.ArrayViews.Add(walker);
        return walker;
    }

    /// <summary>
    /// The sequence interfaces an array of this element implements: the one it
    /// is being converted to, and every other one the standard library has an
    /// instantiation of for that element.
    /// </summary>
    private List<TypeSymbol> SequenceFaces(Type element, TypeSymbol face)
    {
        List<TypeSymbol> faces = new() { face };

        foreach (string sequence in new[] { "IEnumerable", "IReadOnlyCollection", "IReadOnlyList" })
        {
            if (RefOf(element) is TypeRef written
                && _r.Types.TryGetValue(Monomorphiser.MangledName(sequence, new List<TypeRef> { written }),
                                        out TypeSymbol? also)
                && !faces.Contains(also))
            {
                faces.Add(also);
            }
        }
        return faces;
    }

    /// <summary>
    /// The sequence interface an array is being converted to, or null.
    ///
    /// Matched by ELEMENT: a `Type[]` satisfies an `IReadOnlyList` of Type and
    /// nothing else. The interface has already been specialised by the time
    /// this is asked, so its element is in its TemplateArgs.
    /// </summary>
    private TypeDecl? ArrayFace(Type from, Type to)
    {
        if (!from.IsArray || from.Element is not Type element
            || to.Symbol is not { Kind: TypeKind.Interface } face)
        {
            return null;
        }

        // AN OPEN APPLICATION NAMES THE SAME INTERFACE. `IEnumerable<byte>`
        // arrives that way when a generic method's own T has just been
        // inferred, and the specialisation it means is the one the flat table
        // is keyed by.
        if (face.Decl is not { Template: not null } made)
        {
            if (to.Args is not { Count: 1 } open
                || Bare(face.Name) is not ("IReadOnlyList" or "IReadOnlyCollection" or "IEnumerable")
                || RefOf(open[0]) is not TypeRef written
                || !_r.Types.TryGetValue(
                        Monomorphiser.MangledName(Bare(face.Name), new List<TypeRef> { written }),
                        out TypeSymbol? concrete)
                || concrete.Decl is not { Template: not null } instead)
            {
                return null;
            }

            made = instead;
        }

        if (made.Template is not ("IReadOnlyList" or "IReadOnlyCollection" or "IEnumerable")
            || made.TemplateArgs.Count != 1)
        {
            return null;
        }

        return Resolve(made.TemplateArgs[0], _thisType).Equals(element) ? made : null;
    }

    /// <summary>
    /// This type, or something it derives from or implements, as an
    /// instantiation of the named template.
    ///
    /// `List$Node` IS a `List` of Node and IMPLEMENTS an `IReadOnlyList` of
    /// Node, and a LINQ operator declared over the second has to accept the
    /// first -- which is the whole reason C# declares them over the interface.
    /// </summary>
    private static TypeDecl? Instance(TypeSymbol? t, string template, int arity)
    {
        for (TypeSymbol? at = t; at != null; at = at.Base)
        {
            if (at.Decl is { Template: not null } made
                && made.Template == template && made.TemplateArgs.Count == arity)
            {
                return made;
            }

            foreach (TypeSymbol face in at.Interfaces)
            {
                if (Instance(face, template, arity) is { } through)
                {
                    return through;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// What a template's base list makes it, as the named interface, with a
    /// set of arguments already put in for its own parameters.
    ///
    /// `List&lt;T&gt;` says IEnumerable&lt;T&gt; outright; IReadOnlyList reaches
    /// it through IReadOnlyCollection, so the walk follows the bases of the
    /// bases. Everything is done on the WRITTEN types rather than resolved
    /// ones, because the parameter in `IReadOnlyList&lt;T&gt;` is the
    /// template's own T and means nothing anywhere the call was written.
    /// </summary>
    private List<Type>? OpenAs(TypeDecl template, string face, int arity,
                               Dictionary<string, Type> mine, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        foreach (TypeRef b in template.Bases)
        {
            List<Type> args = new();
            bool plain = b.Args.Count > 0;

            foreach (TypeRef a in b.Args)
            {
                if (a.Args.Count == 0 && a.ArrayRank == 0 && !a.Nullable
                    && mine.TryGetValue(a.Name, out Type? bound))
                {
                    args.Add(bound);
                }
                else
                {
                    plain = false;
                    break;
                }
            }

            if (!plain)
            {
                continue;
            }

            if (Bare(b.Name) == face && args.Count == arity)
            {
                return args;
            }

            if (!(FindType(b.Name, out TypeSymbol? holder)
                  || FindType(Arity(b.Name, b.Args.Count), out holder))
                || holder?.Decl is not { } deeper
                || deeper.TypeParams.Count != args.Count)
            {
                continue;
            }

            Dictionary<string, Type> next = new(StringComparer.Ordinal);

            for (int i = 0; i < args.Count; i++)
            {
                next[deeper.TypeParams[i].Name] = args[i];
            }

            if (OpenAs(deeper, face, arity, next, depth + 1) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a variant interface makes this conversion, as C# declares it and
    /// for the reason C# gives.
    ///
    /// `IEnumerable&lt;out T&gt;` says the sequence only ever hands a T OUT, so
    /// an IEnumerable of FieldDecl IS an IEnumerable of MemberDecl: everything
    /// read from it is a MemberDecl, which is true of every FieldDecl. `in` is
    /// the same read backwards -- an IComparer of MemberDecl can compare
    /// FieldDecls.
    ///
    /// Nothing is made and nothing is wrapped. An interface's methods share one
    /// vtable slot across every specialisation of its template (the slot
    /// families in Run), so a call through either specialisation reaches the
    /// same implementation, and the reference that arrives is the reference
    /// that was passed.
    ///
    /// ASKED WHERE A CONVERSION IS, and not in Convertible: that predicate also
    /// decides which extension methods are candidates and which overload wins,
    /// and a rule that makes more things convertible there moves calls that
    /// resolve perfectly well today.
    /// </summary>
    private bool Variant(Type from, Type to)
    {
        if (to.Symbol is not { Kind: TypeKind.Interface } variantFace
            || (variantFace.Decl?.Template ?? Bare(variantFace.Name)) is not { Length: > 0 } faceName
            || Arguments(to) is not { Count: > 0 } wantedArguments
            || Variances(faceName, wantedArguments.Count) is not { } varies
            || !varies.Any(v => v != Variance.None)
            || Instance(from.Symbol, faceName, wantedArguments.Count) is not { } implemented)
        {
            return false;
        }

        List<Type> given = implemented.TemplateArgs.Select(a => Resolve(a, _thisType)).ToList();

        if (given.Count != wantedArguments.Count)
        {
            return false;
        }

        for (int i = 0; i < given.Count; i++)
        {
            // Reference types only, which is C#'s rule too: variance is safe
            // because every argument is one machine word however it is
            // annotated, and a value type is not.
            bool fits = varies[i] switch
            {
                Variance.Out => Carried(given[i]) && Carried(wantedArguments[i])
                             && Convertible(given[i], wantedArguments[i]),
                Variance.In => Carried(given[i]) && Carried(wantedArguments[i])
                            && Convertible(wantedArguments[i], given[i]),
                _ => given[i].Equals(wantedArguments[i]),
            };

            if (!fits)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether a type argument is carried in ONE MACHINE WORD, which is what
    /// makes variance safe: the compiled code is the same however the argument
    /// is annotated. `object` is one as surely as a class is -- it IS the word
    /// -- and a value type is not.
    /// </summary>
    private static bool Carried(Type t)
        => t.IsReference || t.Prim == Prim.Any || t.IsArray;

    /// <summary>
    /// A constructed type's arguments, however it is spelt: the template with
    /// its arguments beside it, or the specialisation that names them.
    /// </summary>
    private List<Type>? Arguments(Type t)
        => t.Args.Count > 0
         ? t.Args.ToList()
         : t.Symbol?.Decl is { Template: not null } made && made.TemplateArgs.Count > 0
           ? made.TemplateArgs.Select(a => Resolve(a, _thisType)).ToList()
           : null;

    private readonly Dictionary<(string, int), List<Variance>?> _variances = new();

    /// <summary>
    /// How an interface template's parameters were declared to vary, or null
    /// when no template of that name and arity is in the program.
    ///
    /// Asked of the TEMPLATE, because a specialisation has no parameters left
    /// to carry the annotation -- `IEnumerable$FieldDecl` is a finished type.
    /// </summary>
    private List<Variance>? Variances(string template, int arity)
    {
        if (_variances.TryGetValue((template, arity), out List<Variance>? known))
        {
            return known;
        }

        List<Variance>? found = null;

        if (_r.Types.TryGetValue(Arity(template, arity), out TypeSymbol? open)
            || _r.Types.TryGetValue(template, out open))
        {
            if (open.Decl is { } decl && decl.TypeParams.Count == arity)
            {
                found = decl.TypeParams.Select(p => p.Variance).ToList();
            }
        }

        _variances[(template, arity)] = found;
        return found;
    }

    /// <summary>
    /// A template's own parameter names bound to what an application gave
    /// them: for `Func&lt;T,R&gt;` over `Func&lt;A,R&gt;`, that is A to T.
    /// </summary>
    private static Dictionary<string, Type> Applied(Type t)
    {
        Dictionary<string, Type> map = new(StringComparer.Ordinal);

        if (t.Symbol?.Decl is { } decl)
        {
            for (int i = 0; i < decl.TypeParams.Count && i < t.Args.Count; i++)
            {
                map[decl.TypeParams[i].Name] = t.Args[i];
            }
        }
        return map;
    }

    /// <summary>The Invoke of a function type, open or closed.</summary>
    private static MethodSymbol? Invoked(Type t)
        => t.Symbol?.FindMethods("Invoke").FirstOrDefault();

    /// <summary>
    /// What a lambda evaluates to, given what its parameters have turned out
    /// to be -- or null when they have not turned out to be anything yet.
    ///
    /// Checked QUIETLY and thrown away. This is inference asking a question,
    /// not the compiler checking a body; the body is checked for real once the
    /// copy for these type arguments is compiled.
    /// </summary>
    private Type? Produces(MethodSymbol m, Type want, LambdaExpr lam, Dictionary<string, Type> bound)
    {
        if (Invoked(want) is not { } invoke || invoke.Params.Count != lam.Params.Count
            || lam.Body is null)
        {
            return null;
        }

        // THROUGH THE APPLICATION FIRST. Invoke's parameters are the
        // TEMPLATE's -- `Func<A,R>` says A -- and the parameter here is
        // `Func<T,R>`, so A means T before T means Node. Two substitutions, in
        // that order.
        Dictionary<string, Type> applied = Applied(want);
        List<Type> taken = new();

        foreach (ParamSymbol p in invoke.Params)
        {
            Type filled = Substitute(Substitute(p.Type, applied), bound);

            if (filled.ParamName != null)
            {
                return null;                    // still open; nothing to ask with
            }

            taken.Add(filled);
        }

        _quiet++;
        PushScope(functionBoundary: true);

        for (int i = 0; i < lam.Params.Count; i++)
        {
            Declare(lam, lam.Params[i].Name, new ParamSym(i, taken[i], lam.Params[i].Name, false));
        }

        Type produced = CheckExpr(lam.Body);

        PopScope();
        _quiet--;
        return produced.IsError ? null : produced;
    }

    /// <summary>
    /// Matches one parameter against one argument, binding type parameters.
    /// </summary>
    /// <summary>
    /// A TYPE ARGUMENT against a type argument. Where the parameter's names no
    /// type parameter of the method, the two must be the same type, or both
    /// references one converts to (the variance an in/out parameter allows):
    /// C# converts `Func&lt;T, long&gt;` to no `Func&lt;T, double&gt;`, so
    /// `Sum(xs, f)` with f a Func of long does not find the double overload.
    /// </summary>
    private bool UnifyArgument(MethodSymbol m, Type want, Type got, Dictionary<string, Type> bound)
    {
        if (m.TypeParams.Any(t => Mentions(want, t))) return Unify(m, want, got, bound);
        if (got.IsError || want.IsError) return true;
        if (want.IsReference && got.IsReference) return Convertible(got, want);
        return Convertible(got, want) && Convertible(want, got);
    }

    private bool Unify(MethodSymbol m, Type want, Type got, Dictionary<string, Type> bound)
    {
        if (want.ParamName is string name && m.TypeParams.Contains(name))
        {
            if (!bound.TryGetValue(name, out Type? already))
            {
                bound[name] = got;
                return true;
            }

            // The SAME T twice has to mean the same thing both times, so
            // `Pick(1, "two")` finds no overload rather than quietly taking one
            // of the two and mistyping the result.
            return got.IsError || Convertible(got, already);
        }

        // `T[]` against `string[]` binds T to string. Through the element,
        // because the array itself is an address either way.
        if (want.IsArray && got.IsArray && want.Element != null && got.Element != null)
        {
            return Unify(m, want.Element, got.Element, bound);
        }

        // AN ARRAY IS A SEQUENCE OF ITS ELEMENT, which is what .NET says:
        // `T[]` implements IEnumerable<T>, IReadOnlyCollection<T> and
        // IReadOnlyList<T>. An array carries no vtable here and so has no
        // symbol to look those up on, which is why the element is matched
        // against the interface's argument directly -- and why `args.Any(a =>
        // a.StartsWith("@"))` over a string[] found no Any at all.
        if (got.IsArray && got.Element is { } item && want.Args.Count == 1
            && want.Symbol is { } sequence
            && (sequence.Decl?.Template ?? Bare(sequence.Name))
                is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList")
        {
            return Unify(m, want.Args[0], item, bound);
        }

        // AND A STRING IS A SEQUENCE OF CHARACTERS, for the same reason and in
        // the same way: .NET's string implements IEnumerable<char>, and a
        // string here is a primitive the compiler knows rather than a class
        // with a vtable, so the element is matched against the interface's
        // argument directly. `text.All(c => char.IsLetterOrDigit(c))` is the
        // line in this compiler's own lexer that wanted it.
        if (got.Prim == Prim.String && !got.IsArray && want.Args.Count == 1
            && want.Symbol is { } letters
            && (letters.Decl?.Template ?? Bare(letters.Name))
                is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList")
        {
            return Unify(m, want.Args[0], Type.Char, bound);
        }

        // AN UNMADE SPECIALISATION IS STILL AN APPLICATION OF ITS TEMPLATE.
        //
        // `d.OrderBy(p => p.Key)` is an `IEnumerable<KeyValuePair<…>>` before
        // any specialisation spells that name -- the template with its
        // arguments beside it, which is what an inferred type is for one round
        // -- and `.ToList()` over it has to read the argument off it just the
        // same, or the list it hands back holds a T and the foreach over it
        // takes a T apart.
        if (want.Symbol is { } opened && want.Args.Count > 0
            && got.Symbol is { } alsoOpened && got.Args.Count == want.Args.Count
            && ReferenceEquals(opened, alsoOpened))
        {
            for (int i = 0; i < want.Args.Count; i++)
            {
                if (!UnifyArgument(m, want.Args[i], got.Args[i], bound))
                {
                    return false;
                }
            }
            return true;
        }

        // AND AN UNMADE ONE IMPLEMENTS WHAT ITS TEMPLATE DOES, with its own
        // arguments put in.
        //
        // `d.OrderBy(p => p.Key)` is a `List<KeyValuePair<…>>` before the copy
        // that spells that name exists, and `.ToList()` over it is
        // `ToList<T>(this IEnumerable<T>)`: the element has to be read off the
        // template's own base list, or the list that comes back holds a T and
        // the foreach over it takes a T apart.
        if (_throughTemplates && want.Symbol is { } face && want.Args.Count > 0 && Unmade(got)
            && got.Symbol?.Decl is { TypeParams.Count: > 0 } openTemplate
            && openTemplate.TypeParams.Count == got.Args.Count)
        {
            Dictionary<string, Type> mine = new(StringComparer.Ordinal);

            for (int i = 0; i < openTemplate.TypeParams.Count; i++)
            {
                mine[openTemplate.TypeParams[i].Name] = got.Args[i];
            }

            string wanted = face.Decl?.Template ?? Bare(face.Name);

            if (OpenAs(openTemplate, wanted, want.Args.Count, mine, 0) is { } through)
            {
                for (int i = 0; i < want.Args.Count; i++)
                {
                    if (!UnifyArgument(m, want.Args[i], through[i], bound))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        // Infer from this use of a constructed type before consulting its
        // shared physical declaration. Tuple names inside List<T> belong to
        // the receiver, not the first List specialisation with that shape.
        if (want.Symbol is { } useTarget && want.Args.Count > 0
            && got.UseArgs is { Count: > 0 } useArguments
            && HasTupleUse(got)
            && got.Symbol?.Decl?.Template is string useTemplateName
            && FindType(Arity(useTemplateName, useArguments.Count), out TypeSymbol? useTemplate)
            && useTemplate?.Decl is TypeDecl useDeclaration
            && useDeclaration.TypeParams.Count == useArguments.Count)
        {
            Dictionary<string, Type> mine = new(StringComparer.Ordinal);
            for (int i = 0; i < useArguments.Count; i++)
                mine[useDeclaration.TypeParams[i].Name] = useArguments[i];
            string targetName = Bare(useTarget.Name);
            IReadOnlyList<Type>? through = targetName == useTemplateName
                ? useArguments
                : OpenAs(useDeclaration, targetName, want.Args.Count, mine, 0);
            if (through is not null && through.Count == want.Args.Count)
            {
                for (int i = 0; i < through.Count; i++)
                    if (!UnifyArgument(m, want.Args[i], through[i], bound)) return false;
                return true;
            }
        }

        // `List<T>` AGAINST A `List$Node`, which is what makes LINQ work.
        //
        // The parameter is an OPEN application of a template; the argument is
        // a specialisation of one, and it remembers which -- so if they are the
        // same template, unifying their arguments pairwise says what T is.
        if (want.Symbol is { } template && want.Args.Count > 0
            && Instance(got.Symbol, template.Name, want.Args.Count) is { } made)
        {
            for (int i = 0; i < want.Args.Count; i++)
            {
                if (!UnifyArgument(m, want.Args[i], Resolve(made.TemplateArgs[i], _thisType), bound))
                {
                    return false;
                }
            }
            return true;
        }

        return got.IsError || Convertible(got, want);
    }

    /// <summary>
    /// A template's name without the arity it is keyed by: `IEnumerable`1` is
    /// IEnumerable. An OPEN application names the template itself, where a
    /// specialisation records which template it came from.
    /// </summary>
    private static string Bare(string name)
    {
        int tick = name.LastIndexOf('`');

        return tick < 0 ? name : name[..tick];
    }

    /// <summary>A type with a generic method's inferred arguments put in.</summary>
    private static Type Substitute(Type t, Dictionary<string, Type>? bound)
    {
        if (bound is null)
        {
            return t;
        }

        if (t.ParamName is string name && bound.TryGetValue(name, out Type? actual))
        {
            return actual;
        }

        // THROUGH THE ELEMENT TOO, so `T[]` against a `string[]` argument is
        // checked as a `string[]`. Substituting only the bare parameter left
        // the array itself saying T, and the caller was told it could not
        // convert 'string[]' to 'T[]' -- having just inferred that they are
        // the same thing.
        if (t.IsArray && t.Element is Type element)
        {
            Type made = Substitute(element, bound);

            if (!made.Equals(element))
            {
                Type rebuilt = Type.ArrayOf(made, 1);
                return t.Nullable ? rebuilt.AsNullable() : rebuilt;
            }
        }

        return t;
    }

    /// <summary>
    /// The same, but able to turn an open `List&lt;T&gt;` into the real
    /// `List$Node` once T is known.
    ///
    /// Separate from Substitute because it needs the type table to look the
    /// specialisation up, and because it is only the CALL SITE that wants it:
    /// inside the method, T stays T until the copy is made.
    /// </summary>
    private Type Close(Type t, Dictionary<string, Type>? bound)
    {
        Type made = Substitute(t, bound);

        if (bound is null || made.Symbol is not { } template || made.Args.Count == 0
            || template.Decl?.TypeParams.Count != made.Args.Count)
        {
            return made;
        }

        List<TypeRef> args = new();
        List<Type> closed = new();

        foreach (Type a in made.Args)
        {
            // THROUGH THE NESTED ARGUMENT TOO. `Func<Task<T>>` with T known is
            // `Func$Task$int`, and that name can only be spelt once the inner
            // `Task<T>` has itself been closed into `Task$int` -- substituting
            // alone leaves an open template no mangled name matches.
            Type filled = Close(a, bound);

            closed.Add(filled);

            if (RefOf(filled) is not TypeRef spelt)
            {
                return made;
            }

            args.Add(spelt);
        }

        if (_r.Types.TryGetValue(Monomorphiser.MangledName(template.Name, args), out TypeSymbol? real))
        {
            return new Type
            {
                Prim = real.Kind == TypeKind.Enum ? Prim.I32 : Prim.Void,
                Symbol = real, UseArgs = closed,
            };
        }

        // THE SPECIALISATION MAY NOT EXIST YET. `Repeat<T>` returning
        // `Task<List<T>>` called with a string asks for a `Task$List$string`
        // that nothing has written down: the copy of Repeat that writes it is
        // only made for the round AFTER this one. Hand back the template with
        // its arguments closed, so what reads this type -- an await, above all
        // -- can still see that the T inside it is a string, instead of the
        // round reporting an error and stopping before the copy is made.
        return WithArgs(made, closed);
    }

    /// <summary>
    /// What a constructed type's arguments bind its class's parameters to, or
    /// null when the receiver is not a constructed template: a specialisation
    /// has its arguments in its members already, and a plain class has none.
    /// </summary>
    /// <summary>
    /// What a Span or ReadOnlySpan holds, or null when this is neither.
    ///
    /// Answers for the specialisation `Span$byte` and for the template with
    /// its argument beside it, which is what an inferred `x.AsSpan()` is until
    /// the copy that spells it has been made.
    /// </summary>
    /// <summary>
    /// Whether this is a ReadOnlySpan rather than a writable one -- the
    /// specialisation, whose own name carries its argument, or the template.
    /// </summary>
    private static bool ReadOnlySpanned(Type t)
        => t.Symbol?.Decl is { Template: "ReadOnlySpan" }
                          or { Name: "ReadOnlySpan", TypeParams.Count: 1 };

    private Type? SpanHolds(Type t)
    {
        if (t.Symbol?.Decl is { Template: "ReadOnlySpan" or "Span", TemplateArgs.Count: 1 } made)
        {
            return Resolve(made.TemplateArgs[0], _thisType);
        }

        if (t.Symbol?.Decl is { Name: "ReadOnlySpan" or "Span", TypeParams.Count: 1 } && t.Args.Count == 1)
        {
            return t.Args[0];
        }

        return null;
    }

    private static Dictionary<string, Type>? Received(Type target, TypeSymbol owner)
    {
        List<TypeParam>? parameters = owner.Decl?.TypeParams;

        if (parameters is null || parameters.Count == 0 || target.Args.Count != parameters.Count)
        {
            return null;
        }

        Dictionary<string, Type> bound = new();

        for (int i = 0; i < parameters.Count; i++)
        {
            bound[parameters[i].Name] = target.Args[i];
        }

        return bound;
    }

    /// <summary>
    /// Whether matching a pattern of this type against a subject of that one
    /// is a BOX TEST: an object holding a value type, asked whether it is one
    /// of a particular type. Exactly what `is` already tests, and the one kind
    /// of pattern whose test is neither a reference test nor nothing at all.
    /// </summary>
    private static bool BoxMatched(Type subject, Type tested)
        => subject.Prim == Prim.Any && !subject.IsArray
        && !tested.IsReference && !tested.IsNullableValue && !tested.IsArray && !tested.IsPointer
        && tested.ParamName is null
        && (tested.Symbol is { Kind: TypeKind.Enum or TypeKind.Struct }
            || ((tested.IsNumeric || tested.Prim == Prim.Bool) && tested.Prim != Prim.Void));

    /// <summary>The same type carrying different type arguments.</summary>
    private static Type WithArgs(Type t, IReadOnlyList<Type> args) => new()
    {
        Prim = t.Prim,
        Symbol = t.Symbol,
        Nullable = t.Nullable,
        Element = t.Element,
        ArrayRank = t.ArrayRank,
        Args = args,
        UseArgs = t.UseArgs,
        ParamName = t.ParamName,
        Names = t.Names,
        PointerDepth = t.PointerDepth,
        Pointee = t.Pointee,
    };

    /// <summary>
    /// How much a candidate's parameters SAY, so that two overloads which both
    /// unify can be told apart. A bare type parameter says nothing -- it
    /// matches whatever it is handed -- while every layer of a written
    /// template (`Func<Task<T>>`) is one more thing the argument had to be.
    /// </summary>
    private static int Specificity(MethodSymbol m)
    {
        int total = 0;

        foreach (ParamSymbol p in m.Params)
        {
            total += Specificity(p.Type);
        }

        return total;
    }

    private static int Specificity(Type t)
    {
        if (t.ParamName != null)
        {
            return 0;
        }

        int total = 1;

        if (t.Element is Type element)
        {
            total += Specificity(element);
        }

        foreach (Type a in t.Args)
        {
            total += Specificity(a);
        }

        return total;
    }

    /// <summary>
    /// What a collection expression is, for the type that wants it: an array
    /// of that element; for IEnumerable&lt;T&gt;, IReadOnlyCollection&lt;T&gt; and
    /// IReadOnlyList&lt;T&gt;, which an array is, an array of T; and for a class
    /// or struct with an Add, its collection initializer. Null, and why, for
    /// anything else.
    ///
    /// A SPREAD (`..items`) makes it a chain of CollectionExpressionBuilder
    /// calls instead (SpreadFor): generic calls with their arguments spelt, so
    /// the round after this one makes the copies, and the List an array is
    /// gathered in, as it does for any generic call.
    /// </summary>
    private Expr? CollectionFor(NewExpr collection, Type target, out string? why)
    {
        why = null;
        if (collection.Adds.Any(add => add.Spread))
        {
            return SpreadFor(collection, target, out why);
        }
        List<Expr> elements = collection.Adds.Select(add => add.Args[0]).ToList();

        Type? element = null;
        if (target.IsArray && target.ArrayRank == 1) element = target.Element;
        else if (target.Symbol is { Kind: TypeKind.Interface, Decl: { } decl }
                 && decl.Template is string template && decl.TemplateArgs.Count == 1
                 && Last(template) is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList")
        {
            if (Resolve(decl.TemplateArgs[0], _thisType) is { IsError: false } argument) element = argument;
        }
        if (element is not null)
        {
            if (RefOf(element) is not TypeRef elementRef)
            {
                why = $"a collection expression cannot make an array of '{element}'";
                return null;
            }
            return new NewExpr
            {
                Type = elementRef, Elements = elements,
                Line = collection.Line, Col = collection.Col,
            };
        }

        if (target.Symbol is { Kind: TypeKind.Class or TypeKind.Struct } && !target.IsArray && RefOf(target) is TypeRef targetRef)
        {
            NewExpr made = new() { Type = targetRef, Line = collection.Line, Col = collection.Col };
            made.Adds.AddRange(collection.Adds);
            return made;
        }

        why = $"a collection expression cannot be a '{target}'";
        return null;

        static string Last(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }

    /// <summary>
    /// A collection expression with a spread in it, as calls: the collection
    /// is started (a List of the element for an array or an interface target,
    /// the target itself for a class with an Add), each element and each
    /// spread is one Element or Spread call on it, in the order written, and
    /// an array target takes the List's ToArray at the end.
    /// </summary>
    private Expr? SpreadFor(NewExpr collection, Type target, out string? why)
    {
        why = null;
        Type? element = null;
        bool gathered = false;
        if (target.IsArray && target.ArrayRank == 1)
        {
            element = target.Element;
            gathered = true;
        }
        else if (target.Symbol is { Kind: TypeKind.Interface, Decl: { } decl }
                 && decl.Template is string template && decl.TemplateArgs.Count == 1
                 && Last(template) is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList")
        {
            if (Resolve(decl.TemplateArgs[0], _thisType) is { IsError: false } argument) element = argument;
            gathered = true;
        }
        else if (target.Symbol is { Kind: TypeKind.Class or TypeKind.Struct } owner && !target.IsArray)
        {
            List<MethodSymbol> adders = owner.FindMethods("Add").Where(a => !a.Static && a.Params.Count == 1).ToList();
            if (adders.Count == 1 && adders[0].Params[0].Type is { ParamName: null } taken) element = taken;
            else if (target.Args.Count == 1) element = target.Args[0];
            if (element is null)
            {
                why = $"'{target}' has no single 'Add' to take a collection expression's spread";
                return null;
            }
        }
        if (element is null)
        {
            why = $"a collection expression cannot be a '{target}'";
            return null;
        }
        if (RefOf(element) is not TypeRef elementRef)
        {
            why = $"a collection expression cannot gather elements of '{element}'";
            return null;
        }

        TypeRef? into = gathered ? null : RefOf(target);
        if (!gathered && into is null)
        {
            why = $"a collection expression cannot be a '{target}'";
            return null;
        }

        MemberExpr Builder(string method, Node at)
        {
            MemberExpr step = new()
            {
                Target = new MemberExpr
                {
                    Target = new NameExpr { Name = "System", Line = at.Line, Col = at.Col },
                    Name = "CollectionExpressionBuilder", Line = at.Line, Col = at.Col,
                },
                Name = method, Line = at.Line, Col = at.Col,
            };
            if (into is not null) step.TypeArgs.Add(into);
            step.TypeArgs.Add(elementRef);
            return step;
        }

        Expr built = into is not null
            ? new NewExpr { Type = into, Line = collection.Line, Col = collection.Col }
            : new CallExpr { Target = Builder("Gather", collection), Line = collection.Line, Col = collection.Col };
        foreach (InitAdd add in collection.Adds)
        {
            string method = gathered ? (add.Spread ? "AddRange" : "Add") : (add.Spread ? "Spread" : "Element");
            CallExpr call = new() { Target = Builder(method, add), Line = add.Line, Col = add.Col };
            call.Args.Add(built);
            call.Args.Add(add.Args[0]);
            built = call;
        }

        if (!gathered)
        {
            return built;
        }
        return new CallExpr
        {
            Target = new MemberExpr { Target = built, Name = "ToArray", Line = collection.Line, Col = collection.Col },
            Line = collection.Line, Col = collection.Col,
        };

        static string Last(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }

    /// <summary>What a type is called, where it has a name a TypeRef could hold.</summary>
    /// <summary>
    /// A type as a TypeRef, or null when it has no name one could hold.
    ///
    /// The specialisation pass works on the TREE -- it clones a method and puts
    /// the type arguments in as written types -- so a type the checker worked
    /// out has to be spellable to be a type argument at all.
    /// </summary>
    private static TypeRef? RefOf(Type t)
    {
        Type bare = t;
        int rank = 0;

        while (bare.IsArray && bare.Element is Type of)
        {
            bare = of;
            rank++;
        }

        // A synthesized tuple symbol name is an implementation key such as
        // `ValueTuple$string$int`, not a type spelling. Feeding that key back
        // into generic specialization loses the element arguments. Restore
        // the structural tuple TypeRef instead.
        if (bare.Symbol is TypeSymbol tuple
            && tuple.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal))
        {
            TypeRef structural = new()
            {
                Name = TypeRef.Tuple,
                ArrayRank = rank,
                Nullable = rank == 0 ? bare.Nullable : t.Nullable,
                ElementNullable = rank > 0 && bare.Nullable,
                TupleNames = (bare.Names ?? tuple.TupleNames) is not { } tupleNames
                    ? null
                    : new List<string>(tupleNames),
            };

            foreach (FieldSymbol field in tuple.Fields)
            {
                if (RefOf(field.Type) is not TypeRef element)
                {
                    return null;
                }
                structural.Args.Add(element);
            }
            return structural;
        }

        if (NameOf(bare) is not string spelt)
        {
            return null;
        }

        List<TypeRef>? useArgs = null;
        if (bare.UseArgs is not null)
        {
            useArgs = new();
            foreach (Type argument in bare.UseArgs)
            {
                TypeRef? written = RefOf(argument);
                if (written is null) return null;
                useArgs.Add(written);
            }
        }
        List<TypeRef> typeArguments = new();
        foreach (Type argument in bare.Args)
        {
            TypeRef? written = RefOf(argument);
            if (written is null) return null;
            typeArguments.Add(written);
        }
        return new TypeRef
        {
            Name = typeArguments.Count == 0 ? spelt : Bare(spelt),
            Args = typeArguments,
            UseArgs = useArgs,
            ArrayRank = rank,
            Nullable = rank == 0 ? bare.Nullable : t.Nullable,
            ElementNullable = rank > 0 && bare.Nullable,
        };
    }

    /// <summary>
    /// How a bound type is spelled when it has to go back into syntax -- for a
    /// generic method being copied with its arguments put in, say.
    ///
    /// Its KEY, so a nested type keeps its outer: written as the simple name,
    /// `Any(rows)` over a `List$Holder$Row` inferred its element as `Row` and
    /// asked for a `IReadOnlyList$Row`, which is a different specialisation
    /// from the one the list implements and converts to nothing.
    /// </summary>
    private static string? NameOf(Type t) => t.Symbol?.Key ?? t.Prim switch
    {
        Prim.String => "string",
        Prim.Bool => "bool",
        Prim.I8 => "sbyte",  Prim.U8 => "byte",
        Prim.I16 => "short", Prim.U16 => "ushort",
        Prim.I32 => "int",   Prim.U32 => "uint",
        Prim.I64 => "long",  Prim.U64 => "ulong",
        Prim.NInt => "nint", Prim.NUInt => "nuint",
        Prim.F32 => "float", Prim.F64 => "double",
        Prim.Char => "char", Prim.Any => "object",
        _ => null,
    };

    // ---- expressions ------------------------------------------------------

    /// <summary>
    /// The type of an integer literal: what its suffix declares, or else the
    /// first of int, long and ulong that its value fits.
    ///
    /// `10U` is a uint, `10L` a long and `10UL` a ulong, in either letter
    /// order and either case; a `U` whose value is past uint is a ulong, and
    /// an unsuffixed value past long is a ulong too, since C# has nothing
    /// else for it to be. The parser stores every literal as 64 bits, so a
    /// ulong past long.MaxValue arrives here NEGATIVE -- which is exactly the
    /// test for it.
    /// </summary>
    private static Type IntLiteralType(LiteralExpr l)
    {
        bool unsigned = false;
        bool wide = false;

        for (int i = l.Text.Length - 1; i >= 0 && l.Text[i] is 'L' or 'l' or 'U' or 'u'; i--)
        {
            if (l.Text[i] is 'U' or 'u')
            {
                unsigned = true;
            }
            else
            {
                wide = true;
            }
        }

        if (unsigned)
        {
            return !wide && l.IntValue is >= 0 and <= uint.MaxValue ? Type.U32 : Type.U64;
        }

        if (wide)
        {
            return Type.I64;
        }

        if (l.IntValue is >= int.MinValue and <= int.MaxValue)
        {
            return Type.I32;
        }

        // THE FIRST TYPE THAT CAN HOLD IT, which is C#'s rule: int, then long,
        // then ulong for a decimal literal, and int, uint, long, ulong for one
        // written in hex or binary. A negative IntValue here is a literal past
        // long.MaxValue that wrapped, and that is a ulong either way --
        // `0x8000000000000000` is one bit, not a negative number, and `v &
        // 0x8000000000000000` on a ulong was refused for mixing the two.
        if (l.IntValue < 0)
        {
            return Type.U64;
        }

        return IsHexOrBinary(l.Text) && l.IntValue <= uint.MaxValue ? Type.U32 : Type.I64;
    }

    /// <summary>Whether a literal was written in hex or binary.</summary>
    private static bool IsHexOrBinary(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("0b", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The type of an argument once what it fills in is known.
    ///
    /// A bare `default` answers Error while the overload is undecided, for the
    /// same reason `out var` does -- overloads are chosen from the argument
    /// types and this is one of them -- so this is where it is told what it
    /// meant. Everything else is already what it was.
    /// </summary>
    private Type Settle(Expr written, Type want, Type had)
    {
        // A TUPLE LITERAL TAKES THE SHAPE THE PARAMETER GAVE IT, which could
        // not be known while the overload was still being chosen. Checking it
        // again with that shape in hand is what converts each element and puts
        // a value into a cell where the target holds a nullable one.
        if (written is TupleExpr && want.Symbol is { } shape
            && shape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            && !had.Equals(want))
        {
            Type? outer = _wanted;

            _wanted = want;

            Type again = CheckExpr(written);

            _wanted = outer;
            return again;
        }

        if (written is not DefaultExpr { Type.Name.Length: 0 })
        {
            return had;
        }

        // The zero of a reference type IS null, which is what `default` means
        // there and what the declared form answers too.
        Type settled = want.IsReference ? want.AsNullable() : want;

        _r.ExprType[written] = settled;
        return settled;
    }

    /// <summary>
    /// One pair of initialiser braces, resolved against the type they fill in.
    ///
    /// The same three kinds of element wherever the braces are written -- after
    /// a `new`, or after a member's `=` -- so this is one method and it calls
    /// itself for the nested case.
    /// </summary>
    private void CheckInitBody(Type type, InitBody body)
    {
        // THE OBJECT INITIALISER, resolved against the type being made.
        //
        // An auto-property is a field called <Name>, so setting one through an
        // initialiser is a field store and needs no accessor at all -- which is
        // also how `init` works without an enforcement pass: an initialiser
        // writing the backing field directly is exactly what C# generates.
        foreach (InitAssign init in body.Inits)
        {
            TypeSymbol? owner = type.Symbol;

            if (owner is null)
            {
                if (!type.IsError)
                {
                    Error(init, $"'{type}' cannot be built with an object initialiser");
                }
                continue;
            }

            FieldSymbol? field = owner.FindField(init.Name)
                              ?? owner.FindField("<" + init.Name + ">");

            // `Name = { … }` -- the member is READ and what it holds is filled
            // in, so a get-only property is enough and no assignment happens.
            if (init.Nested is InitBody nested)
            {
                if (field != null)
                {
                    _r.InitField[init] = field;
                    CheckInitBody(field.Type, nested);
                    continue;
                }

                MethodSymbol? getter = owner.FindMethods("get_" + init.Name)
                                            .FirstOrDefault(g => g.Params.Count == 0);

                if (getter != null)
                {
                    _r.InitGetter[init] = getter;
                    CheckInitBody(getter.Returns, nested);
                    continue;
                }

                Error(init, $"'{owner.Name}' has no member '{init.Name}' to initialise");
                continue;
            }

            MethodSymbol? setter = field is null
                                 ? owner.FindMethods("set_" + init.Name).FirstOrDefault()
                                 : null;

            if (field is null && (setter is null || setter.Params.Count != 1))
            {
                CheckExpr(init.Value!);
                Error(init, $"'{owner.Name}' has no member '{init.Name}' to set");
                continue;
            }

            // THE MEMBER IS WHAT THE VALUE IS BEING CONVERTED TO, so it is
            // resolved first and the value read against it: `Op = new()` and
            // `Op = default` both mean the member's type and nothing else.
            Type wants = field?.Type ?? setter!.Params[0].Type;
            Type? outer = _wanted;

            _wanted = wants;

            Type value = Settle(init.Value!, wants, CheckExpr(init.Value!));

            _wanted = outer;

            if (field != null)
            {
                _r.InitField[init] = field;
            }
            else
            {
                _r.InitSetter[init] = setter!;
            }

            CheckAssignable(value, wants, init.Value!, $"'{init.Name}'");
        }

        // A COLLECTION INITIALISER, which C# defines as a sequence of calls to
        // Add -- no special member, no interface to implement beyond having
        // one. `new List<int> { 1, 2 }` is exactly `new List<int>()` and then
        // two calls, and that is all it becomes here too.
        foreach (InitAdd add in body.Adds)
        {
            // AN ELEMENT IS NOT WHAT THE DECLARATION IS WAITING FOR: in
            // `List<SymbolEntry> s = new() { default }` the braces hold a
            // SymbolEntry and the declaration says what the list is. So the
            // target type is put away, and Add's parameter fills the element in
            // once the overload is chosen.
            Type? outerElement = _wanted;

            _wanted = null;

            // A TARGET-TYPED `new(...)` or a LAMBDA waits for the Add to say
            // what it is, as an argument of any call does.
            List<Type> given = add.Args.Select(argument =>
                argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null, Collection: false }
                    ? Type.Any : CheckExpr(argument)).ToList();

            _wanted = outerElement;

            TypeSymbol? owner = type.Symbol;

            if (owner is null)
            {
                if (!type.IsError)
                {
                    Error(add, $"'{type}' cannot be built with a collection initialiser");
                }
                continue;
            }

            List<MethodSymbol> adders = owner.FindMethods("Add")
                                             .Where(a => !a.Static && a.Params.Count == given.Count)
                                             .ToList();

            if (adders.Count == 0)
            {
                Error(add, given.Count == 1
                    ? $"'{owner.Name}' has no 'Add' taking one argument, so it cannot be built from a list of elements"
                    : $"'{owner.Name}' has no 'Add' taking {given.Count} arguments");
                continue;
            }

            // AN ARGUMENT WHOSE TYPE IS NOT YET DECIDED MATCHES ANYTHING, which
            // is what Error means for a bare `default` here and is how the
            // ordinary overload path reads one too.
            MethodSymbol? chosen = adders.FirstOrDefault(
                a => given.Where((g, i) => !Fits(g, a.Params[i].Type, add.Args[i])).Count() == 0);

            if (chosen is null)
            {
                Error(add, $"no 'Add' on '{owner.Name}' accepts ({string.Join(", ", given)})");
                continue;
            }

            for (int i = 0; i < given.Count; i++)
            {
                if (add.Args[i] is LambdaExpr lambda)
                {
                    given[i] = CheckLambda(lambda, chosen.Params[i].Type);
                    continue;
                }
                if (add.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null, Collection: false })
                {
                    Type? saved = _wanted;
                    _wanted = chosen.Params[i].Type;
                    given[i] = CheckExpr(add.Args[i]);
                    _wanted = saved;
                }
                given[i] = Settle(add.Args[i], chosen.Params[i].Type, given[i]);
                CheckAssignable(given[i], chosen.Params[i].Type, add.Args[i], $"argument {i + 1} of 'Add'");
            }

            _r.InitAdder[add] = chosen;
        }

        // `[key] = value`, which is a call to the INDEXER -- C#'s rule, and the
        // difference from Add shows on a duplicate key: Add refuses one and the
        // indexer overwrites it.
        foreach (InitIndex one in body.Indexes)
        {
            Type? outerIndexed = _wanted;

            _wanted = null;

            List<Type> index = one.Args.Select(CheckExpr).ToList();
            Type value = CheckExpr(one.Value);

            _wanted = outerIndexed;

            TypeSymbol? owner = type.Symbol;

            if (owner is null)
            {
                if (!type.IsError)
                {
                    Error(one, $"'{type}' cannot be built with an indexed initialiser");
                }
                continue;
            }

            MethodSymbol? setter = IndexerFor(
                Reachable(owner, "set_Item"), index, index.Count + 1);

            if (setter is null)
            {
                Error(one, $"'{owner.Name}' has no indexer to write through");
                continue;
            }

            for (int i = 0; i < index.Count; i++)
            {
                index[i] = Settle(one.Args[i], setter.Params[i].Type, index[i]);
                CheckAssignable(index[i], setter.Params[i].Type, one.Args[i], "index");
            }

            value = Settle(one.Value, setter.Params[index.Count].Type, value);
            CheckAssignable(value, setter.Params[index.Count].Type, one.Value, "the value");
            _r.InitIndexer[one] = setter;
        }
    }

    /// <summary>Body-context defaults; never overwrite shared parameter ASTs.</summary>
    private readonly Dictionary<Param, Expr> _writtenDefaults = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A default argument, spelled so that it means the same thing wherever it
    /// is copied to.
    ///
    /// The names in it are written out in full the first time a call leaves the
    /// argument out, in the scope the parameter was DECLARED in -- which is
    /// where C# evaluates a default. The expression itself is then handed to
    /// every call site, in whatever class, namespace or file, and the round of
    /// specialisation that follows copies it; a bare name would mean something
    /// else there, or nothing at all.
    /// </summary>
    private Expr Written(MethodSymbol declared, Param p)
    {
        if (!_writtenDefaults.TryGetValue(p, out Expr? written))
        {
            TypeSymbol? scope = _scope;
            TypeSymbol? lexical = _lexicalType;
            TypeSymbol? self = _thisType;
            MemberDecl? member = _member;

            _thisType = null;
            _scope = declared.Owner;
            _lexicalType = declared.Owner;
            _member = declared.Decl;
            try
            {
                written = Spell(p.Default!);
                _writtenDefaults.Add(p, written);
            }
            finally
            {
                _scope = scope;
                _lexicalType = lexical;
                _thisType = self;
                _member = member;
            }
        }

        return written!;
    }

    /// <summary>
    /// The same expression with every bare name in it written out in full.
    ///
    /// Only the shapes a default argument can take: C# requires a constant, a
    /// `default`, or a `new()`, so what can carry a name is a name, a member of
    /// one, a cast, and the arithmetic that builds a constant out of them.
    /// </summary>
    private Expr Spell(Expr e) => e switch
    {
        NameExpr n => Full(n),

        MemberExpr m => new MemberExpr
        {
            Target = Spell(m.Target), Name = m.Name, Guarded = m.Guarded,
            NullConditional = m.NullConditional,
            Line = m.Line, Col = m.Col, File = m.File,
        },

        UnaryExpr u => new UnaryExpr
        {
            Op = u.Op, Operand = Spell(u.Operand), Line = u.Line, Col = u.Col, File = u.File,
        },

        BinaryExpr b => new BinaryExpr
        {
            Op = b.Op, Left = Spell(b.Left), Right = Spell(b.Right),
            Line = b.Line, Col = b.Col, File = b.File,
        },

        CastExpr c => new CastExpr
        {
            Type = c.Type, Operand = Spell(c.Operand), Line = c.Line, Col = c.Col, File = c.File,
        },

        _ => e,
    };

    /// <summary>
    /// One name, written out in full: a type by its whole key, a const or a
    /// static of the class the default was written in by that class's.
    /// </summary>
    private Expr Full(NameExpr n)
    {
        if (n.Name.Contains('.'))
        {
            return n;
        }

        if (FindType(n.Name, out TypeSymbol? type) && type is not null)
        {
            return new NameExpr { Name = type.Key, Line = n.Line, Col = n.Col, File = n.File };
        }

        for (TypeSymbol? at = _scope; at is not null; at = Outer(at))
        {
            bool has = FindConstant(at, n.Name) is not null
                    || FindText(at, n.Name) is not null
                    || at.FindField(n.Name) is { Static: true }
                    || at.FindField("<" + n.Name + ">") is { Static: true }
                    || at.EnumValues.ContainsKey(n.Name);

            if (has)
            {
                return new MemberExpr
                {
                    Target = new NameExpr { Name = at.Key, Line = n.Line, Col = n.Col, File = n.File },
                    Name = n.Name, Line = n.Line, Col = n.Col, File = n.File,
                };
            }
        }

        return n;
    }

    /// <summary>The type this one is written inside, if any.</summary>
    private TypeSymbol? Outer(TypeSymbol t)
        => Enclosing(t.Key) is { } key && _r.Types.TryGetValue(key, out TypeSymbol? outer) ? outer : null;

    private Type CheckExpr(Expr e)
    {
        Type t = CheckExprCore(e);
        _r.ExprType[e] = t;
        return t;
    }

    private Type CheckExprCore(Expr e)
    {
        switch (e)
        {
            // A TUPLE, WRITTEN OUT. Its type is its elements' types, which is
            // why this cannot be resolved before anything is checked -- and the
            // construction it becomes is an object initialiser, so no
            // constructor has to exist for a class nobody declared.
            case TupleExpr tup:
            {
                List<Type> elements = tup.Items.Select(CheckExpr).ToList();

                if (elements.Any(t => t.IsError))
                {
                    return Type.Error;
                }

                // THE SHAPE SOMETHING IS WAITING FOR WINS, element by element.
                //
                // `(int Reg, long Off) Named() { return (3, 40); }` writes two
                // ints and returns a tuple whose second element is a long, and
                // C# converts each element on the way. Without this the literal
                // is its own shape and the return is a type error about a
                // conversion nobody would think to write.
                if (_wanted?.Symbol is TypeSymbol want
                 && want.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
                 && want.Fields.Count == elements.Count)
                {
                    bool fits = true;

                    for (int i = 0; i < elements.Count; i++)
                    {
                        fits &= Convertible(elements[i], want.Fields[i].Type);
                    }

                    if (fits)
                    {
                        elements = want.Fields.Select(f => f.Type).ToList();
                    }
                }

                TypeSymbol shape = TupleType(elements, tup.Names);
                NewExpr made = new()
                {
                    Type = new TypeRef { Name = shape.Name, Line = tup.Line, Col = tup.Col },
                    Line = tup.Line, Col = tup.Col,
                };

                for (int i = 0; i < tup.Items.Count; i++)
                {
                    InitAssign one = new()
                    {
                        Name = "Item" + (i + 1), Value = tup.Items[i],
                        Line = tup.Line, Col = tup.Col,
                    };

                    _r.InitField[one] = shape.Fields[i];
                    made.Inits.Add(one);
                }

                Type shaped = new() { Prim = Prim.Void, Symbol = shape, Names = tup.Names.ToArray() };

                // The construction is a node the code generator will ask the
                // type of, and nothing else ever checks it -- so it is answered
                // here, where the answer is known.
                _r.ExprType[made] = shaped;
                _r.Tuples[tup] = made;
                return shaped;
            }

            case LiteralExpr l:
                return l.Kind switch
                {
                    // The suffix is a type declaration, not decoration.  In
                    // particular 1L << 48 must be a 64-bit shift; narrowing it
                    // because the VALUE one fits in an int turns the result
                    // into zero and invalidates every 48-bit address bound.
                    Lit.Int  => IntLiteralType(l),
                    Lit.Real => l.Text.EndsWith("F", StringComparison.OrdinalIgnoreCase)
                              ? Type.F32 : Type.F64,
                    Lit.Str  => Type.String,
                    Lit.Char => Type.Char,
                    Lit.Bool => Type.Bool,
                    _        => Type.Null,
                };

            case ThisExpr:
                if (_thisType is null || _method is { Static: true })
                {
                    Error(e, "'this' is not available in a static method");
                    return Type.Error;
                }
                return new Type { Prim = Prim.Void, Symbol = _thisType };

            case NameExpr n:
                return CheckName(n);

            case MemberExpr m:
                return CheckMember(m);

            // `x.ToString()` ON SOMETHING THAT IS NOT AN OBJECT.
            //
            // Every value in C# has one, including the ones with no vtable to
            // put it in: a long, a bool, a char, an enum. So the compiler is
            // what answers for them, by rewriting the call into what it means --
            // which for an enum is a switch over its members, because the NAME
            // is what C# answers and only the compiler knows the names.
            //
            // Before the call is checked, because there is nothing to check: a
            // long has no members and the ordinary path would say so.
            // `new string(c, n)` -- a character repeated, which is a
            // CONSTRUCTOR in C# and cannot be one here: a string is a primitive
            // with a static class beside it rather than a class of its own. So
            // the construction becomes the call it means, and the source is
            // written the way C# writes it.
            case NewExpr { Type: { Name: "string" or "String", ArrayRank: 0 }, Args.Count: 2 } text
                when _r.Types.ContainsKey("String"):
            {
                CallExpr repeat = Call("String", "Repeat", text, text.Args.ToArray());
                _r.Rewrites[text] = repeat;
                return CheckExpr(repeat);
            }

            // `new string(chars)` and `new string(chars, start, length)` -- the
            // other two constructors C# gives a string, turned into the calls
            // they mean for the same reason the repeating one is.
            case NewExpr { Type: { Name: "string" or "String", ArrayRank: 0 }, Args.Count: 1 or 3 } spelled
                when _r.Types.ContainsKey("String") && spelled.Elements is null:
            {
                CallExpr built = Call("String", "FromChars", spelled, spelled.Args.ToArray());
                _r.Rewrites[spelled] = built;
                return CheckExpr(built);
            }

            // SYSTEM.ENUM'S STATICS, which only the compiler can answer, for
            // the same reason an enum's ToString is: the members exist at
            // compile time and nowhere else. Guarded on nothing having
            // declared a type of that name, so a program with an `Enum` of its
            // own keeps it.
            case CallExpr { Target: MemberExpr { Target: NameExpr { Name: "Enum" } } asked } wanted
                when !FindType("Enum", out _) && EnumStatic(asked, wanted) is Type answered:
                return answered;

            // ASKING WHETHER A SETTING IS SET IS NOT A QUESTION ABOUT ITS
            // VALUE, and neither is forgetting one. Both take a declared
            // member, and a declared member is a property -- so evaluating
            // the argument would read the registry and hand these a number,
            // by which point what was asked about is gone. The SHAPE of the
            // call is read instead, at compile time, exactly as nameof and
            // sizeof are.
            // Guarded on this program declaring settings at all, so one that
            // declares none keeps whatever it means by Registry.IsSet.
            case CallExpr { Args.Count: 1, Target: MemberExpr { Name: "IsSet" or "Revert",
                                                                Target: NameExpr { Name: "Registry" } } } asking
                when _registryKeys.Count > 0:
            {
                Expr resolved = Setting(asking);

                _r.Rewrites[asking] = resolved;
                return CheckExpr(resolved);
            }

            case CallExpr { Args.Count: 0, Target: MemberExpr { Name: "ToString" } spelt } written
                when Stringify(spelt, written) is Expr instead:
            {
                // RECORDED, not merely returned: the code generator meets the
                // call that was WRITTEN, and without a way to look up what it
                // stands for it finds a call nobody resolved.
                _r.Rewrites[written] = instead;
                return CheckExpr(instead);
            }

            // A THROW WHERE A VALUE BELONGS never produces one: control leaves.
            // It is typed as the machine word so it fits whatever was waiting,
            // which is C#'s rule said in this checker's terms.
            case ThrowExpr th:
                CheckExpr(th.Value);
                return Type.Any;

            // A LAMBDA WHERE AN EXPRESSION IS WANTED: returned from a method,
            // or the body of another lambda. What it has to be is what the
            // context wants, exactly as for a declaration or an argument;
            // those two reach CheckLambda directly and this is the third way.
            case LambdaExpr lam when _wanted is { Symbol: not null } wantedType:
                return CheckLambda(lam, wantedType);

            case LambdaExpr lam:
                Error(lam, "a lambda here has nothing to tell it what type it is");
                return Type.Error;

            // A PATTERN WHOSE SUBJECT IS EVALUATED ONCE. The subject goes in a
            // slot and every alternative reads that, which is what makes
            // `Peek() is 'x' or 'X'` one call rather than two.
            case PatternExpr pat:
            {
                Type had = CheckExpr(pat.Subject);
                int slot = NewSlot();

                _r.PatternSubject[pat] = slot;
                _subject.Add((slot, had));

                Type answer = CheckExpr(pat.Test);

                _subject.RemoveAt(_subject.Count - 1);
                return answer;
            }

            case SubjectExpr subject when _subject.Count > 0:
                _r.Resolved[subject] = new LocalSym(_subject[^1].Slot, _subject[^1].Type, "");
                return _subject[^1].Type;

            // `x!` says it is not null, and the checker takes the author's
            // word: that is what the operator is for and what it costs.
            case SuppressExpr sure:
            {
                Type suppressed = CheckExpr(sure.Operand);
                if (suppressed.Prim == Prim.NullLiteral) { return Type.Any; }

                // `x!` SAYS SO ABOUT x, not merely about this reading of it.
                // C# sets the null state, which is what lets `_idom![b]` be
                // followed by `_idom[runner]` without a second `!` on every
                // later use.
                Proved(sure.Operand);

                // This checker treats nullable annotations as hard errors,
                // where Roslyn can issue a warning and continue. Suppressing an
                // array therefore suppresses its annotated element as well as
                // the array reference, allowing the common `filled!` idiom
                // after every slot has been proven populated.
                if (suppressed.IsArray && suppressed.Element is Type element)
                {
                    return Type.ArrayOf(element.AsNonNullable(), suppressed.ArrayRank);
                }
                return suppressed.AsNonNullable();
            }

            case CallExpr c:
                return CheckCall(c);

            // A SLICE OF A STRING: `s[1..4]`, `s[2..]`, `s[..3]`.
            //
            // C# calls it a range and gives string an indexer that takes one;
            // there is no Range type here yet, and the meaning is a substring,
            // so that is what it becomes. Written exactly as C# writes it, and
            // the same characters come out.
            // `s[^1]` -- an index counted from the end, which is the
            // subtraction it stands for once the target is known.
            case IndexExpr { Args.Count: 1 } back when back.Args[0] is FromEndExpr end:
            {
                back.Args[0] = Counted(back.Target, end);
                return CheckExpr(back);
            }

            // A SLICE OF AN ARRAY: `all[2..5]`, `all[6..]`, `all[..4]`.
            //
            // An array range COPIES, which is the difference between it and the
            // span case below: `a[1..4]` hands back a new array of three, and
            // writing to it leaves the original alone. C# compiles it to a call
            // to RuntimeHelpers.GetSubArray, and so does this.
            //
            // Taken before the sliceable-shape rule so that an array is never
            // read as one: the two mean different things, and the copy is the
            // one C# specifies here.
            case IndexExpr { Args.Count: 1 } part when part.Args[0] is RangeExpr taken
                                                    && Peek(part.Target).IsArray:
            {
                Expr first = taken.From is FromEndExpr fromBack
                           ? Counted(part.Target, fromBack)
                           : taken.From ?? new LiteralExpr
                             {
                                 Kind = Lit.Int, Text = "0", IntValue = 0,
                                 Line = taken.Line, Col = taken.Col,
                             };

                Expr? last = taken.To is FromEndExpr toBack ? Counted(part.Target, toBack) : taken.To;

                // One past the end minus the start, and the whole of the rest
                // when no end was written.
                Expr count = new BinaryExpr
                {
                    Op = BinOp.Sub,
                    Left = last ?? new MemberExpr
                    {
                        Target = part.Target, Name = "Length", Line = taken.Line, Col = taken.Col,
                    },
                    Right = first,
                    Line = taken.Line, Col = taken.Col,
                };

                CallExpr copy = Call("RuntimeHelpers", "GetSubArray", part, part.Target, first, count);

                _r.Rewrites[part] = copy;
                return CheckExpr(copy);
            }

            // THE SAME ON ANYTHING WITH A Slice, which is what a span is. C#
            // defines the range indexer exactly this way -- a type with a
            // Length and a Slice is sliceable -- and `s[4..]` is how a header
            // is written a field at a time in this compiler's own Meta.cs.
            case IndexExpr { Args.Count: 1 } cut when cut.Args[0] is RangeExpr piece
                                                  && Peek(cut.Target).Symbol is TypeSymbol shape
                                                  && Reachable(shape, "Slice").Any():
            {
                Expr start = piece.From is FromEndExpr fromEnd
                           ? Counted(cut.Target, fromEnd)
                           : piece.From ?? new LiteralExpr
                             {
                                 Kind = Lit.Int, Text = "0", IntValue = 0,
                                 Line = piece.Line, Col = piece.Col,
                             };

                Expr? stop = piece.To is FromEndExpr toEnd ? Counted(cut.Target, toEnd) : piece.To;

                CallExpr slice = new()
                {
                    Target = new MemberExpr
                    {
                        Target = cut.Target, Name = "Slice", Line = piece.Line, Col = piece.Col,
                    },
                    Line = piece.Line, Col = piece.Col,
                };

                slice.Args.Add(start);

                if (stop is not null)
                {
                    slice.Args.Add(new BinaryExpr
                    {
                        Op = BinOp.Sub, Left = stop, Right = start,
                        Line = piece.Line, Col = piece.Col,
                    });
                }

                _r.Rewrites[cut] = slice;
                return CheckExpr(slice);
            }

            case IndexExpr { Args.Count: 1 } sliced when sliced.Args[0] is RangeExpr span
                                                      && Peek(sliced.Target).Prim == Prim.String:
            {
                Expr from = span.From is FromEndExpr startFromEnd
                          ? Counted(sliced.Target, startFromEnd)
                          : span.From ?? new LiteralExpr
                            {
                                Kind = Lit.Int, Text = "0", IntValue = 0,
                                Line = span.Line, Col = span.Col,
                            };

                Expr? to = span.To is FromEndExpr endFromEnd
                         ? Counted(sliced.Target, endFromEnd)
                         : span.To;

                // The length of the slice, which is one past the end minus the
                // start -- and the whole of the rest when no end was written.
                Expr length = to is null
                    ? new BinaryExpr
                    {
                        Op = BinOp.Sub,
                        Left = new MemberExpr
                        {
                            Target = sliced.Target, Name = "Length", Line = span.Line, Col = span.Col,
                        },
                        Right = from,
                        Line = span.Line, Col = span.Col,
                    }
                    : new BinaryExpr
                    {
                        Op = BinOp.Sub, Left = to, Right = from,
                        Line = span.Line, Col = span.Col,
                    };

                CallExpr cut = Call("String", "Substring", sliced, sliced.Target, from, length);

                _r.Rewrites[sliced] = cut;
                return CheckExpr(cut);
            }

            case IndexExpr ix:
            {
                Type target = CheckExpr(ix.Target);

                // AN INDEXER, when the thing is not an array.
                //
                // `b[i]` becomes a call to get_Item, and `b[i] = v` a call to
                // set_Item -- which is what C# compiles an indexer to, and is
                // why the parser was able to desugar the declaration into two
                // ordinary methods. The store side is resolved where assignment
                // targets are, because only that path knows it is a store.
                //
                // WHAT AN INDEX IS ALLOWED TO BE DEPENDS ON WHAT IS INDEXED,
                // and asking that question first is the whole of this. An ARRAY
                // subscript is an integer and nothing else. An indexer takes
                // whatever it was declared to take, which for a Dictionary is
                // the key -- so requiring an integer of every index refused
                // `d["name"]` outright, and refused the canonical instantiation
                // of Dictionary along with it, since a shared key is a machine
                // word rather than a number.
                // REACHABLE, not declared on the type itself: an indexer
                // reached through an interface that extends another -- `IC : IB
                // : IA` where IA is what declares `this[int]` -- is the
                // interface's own as far as C# is concerned, and asking only
                // the type answered that 'IC' cannot be indexed.
                if (target.Symbol != null && !target.IsArray && !target.IsError
                    && Reachable(target.Symbol, "get_Item")
                             .Any(m => m.Params.Count == ix.Args.Count))
                {
                    List<Type> index = ix.Args.Select(CheckExpr).ToList();
                    MethodSymbol? getter = IndexerFor(
                        Reachable(target.Symbol, "get_Item"), index, ix.Args.Count);

                    if (getter != null)
                    {
                        for (int i = 0; i < ix.Args.Count; i++)
                        {
                            CheckAssignable(index[i], getter.Params[i].Type, ix.Args[i], "index");
                        }

                        RequireNonNull(target, ix.Target, "index into");
                        _r.Indexers[ix] = getter;
                        return ContextualMemberResult(target, getter);
                    }
                }

                foreach (Expr a in ix.Args)
                {
                    Type at = CheckExpr(a);

                    if (!at.IsError && !at.IsInteger)
                    {
                        Error(a, $"an index must be an integer, not '{at}'");
                    }
                }

                // A POINTER INDEX IS THE C# SPELLING OF A SCALED RAW-MEMORY
                // ACCESS. It has no object header and no bounds check; the
                // pointee type supplies both the result type and element size.
                if (target.IsPointer && ix.Args.Count == 1)
                {
                    return target.Pointee ?? Type.Error;
                }

                if (target.IsError)
                {
                    return Type.Error;
                }

                // A STRING INDEXES TO A CHARACTER, which is C#'s `s[i]` and is
                // the same load Sys.GetByte does -- our own spelling of it, and
                // one this compiler's own source never uses because C# has this
                // one. `text[0] is 'r' or 'R'` is how a register name is read.
                if (target.Prim == Prim.String && ix.Args.Count == 1)
                {
                    RequireNonNull(target, ix.Target, "index into");
                    return Type.Char;
                }

                if (!target.IsArray)
                {
                    Error(ix, $"'{target}' cannot be indexed");
                    return Type.Error;
                }

                RequireNonNull(target, ix.Target, "index into");
                return target.Element ?? Type.Error;
            }

            case NewExpr { Collection: true, Type.Name.Length: 0 } collection:
            {
                // A COLLECTION EXPRESSION becomes what its target is: an array
                // of the elements, or the target's own collection initializer.
                if (_wanted is not { } target || target.IsError)
                {
                    if (_wanted is null)
                        Error(collection, "a collection expression needs a type to become; write it where one is wanted, or as 'new T[] { ... }'");
                    foreach (InitAdd add in collection.Adds) CheckExpr(add.Args[0]);
                    return Type.Error;
                }
                Expr? made = CollectionFor(collection, target, out string? why);
                if (made is null)
                {
                    Error(collection, why!);
                    foreach (InitAdd add in collection.Adds) CheckExpr(add.Args[0]);
                    return Type.Error;
                }
                _r.Rewrites[collection] = made;
                return CheckExpr(made);
            }

            case NewExpr nw:
            {
                // A TARGET-TYPED `new()` THAT NOBODY TOLD THE TYPE. The parser
                // leaves the name empty and the declaration is supposed to fill
                // it in; reaching here means it was written somewhere with no
                // declared type to take it from.
                if (nw.Type.Name.Length == 0 && nw.Elements is null)
                {
                    // UNLESS SOMETHING IS WAITING FOR IT. A return statement
                    // says what it wants, and that is what `new()` means there;
                    // a declaration says the same thing and has already filled
                    // this in by the time it reaches here.
                    // `object gate = new();` is `new object()`: object has no
                    // symbol here, being the language's own, but it is a type
                    // a target-typed new can make.
                    if (_wanted is { Prim: Prim.Any, Symbol: null, IsArray: false } && nw.Body.IsEmpty)
                    {
                        NewExpr plain = new()
                        {
                            Type = new TypeRef { Name = "object", Line = nw.Line, Col = nw.Col },
                            Line = nw.Line, Col = nw.Col,
                        };
                        plain.Args.AddRange(nw.Args);
                        _r.Rewrites[nw] = plain;
                        return CheckExpr(plain);
                    }

                    if (_wanted is not { Symbol: not null })
                    {
                        Error(nw, "the type of 'new()' cannot be worked out here; write the type, "
                                + "as in 'new List<int>()'");
                        return Type.Error;
                    }
                }

                // The type it was told, or the type something is waiting for.
                // The code generator reads neither: it asks what this
                // expression's type came out as, which is what is returned
                // below either way.
                Type type = nw.Type.Name.Length == 0
                          ? (_wanted ?? Type.Error)
                          : Resolve(nw.Type, _thisType);

                if (nw.Elements is { } written)
                {
                    // `new[] { a, b }` takes its element from the FIRST one
                    // written, which is what C# does; `new Op[] { ... }` was
                    // told. Every other element is then checked against it, so
                    // a list whose members disagree says so at the member that
                    // disagrees.
                    Type element = nw.Type.Name.Length == 0
                                 ? (written.Count > 0 ? CheckExpr(written[0]) : Type.Error)
                                 : type;

                    if (written.Count == 0 && nw.Type.Name.Length == 0)
                    {
                        Error(nw, "an implicitly-typed array needs at least one element to take its type from");
                        return Type.Error;
                    }

                    for (int i = 0; i < written.Count; i++)
                    {
                        Type had = i == 0 && nw.Type.Name.Length == 0 ? element : CheckExpr(written[i]);

                        CheckAssignable(had, element, written[i], $"element {i}");
                    }

                    if (nw.ArraySize != null)
                    {
                        CheckExpr(nw.ArraySize);
                    }

                    return Type.ArrayOf(element);
                }

                if (nw.ArraySize != null)
                {
                    Type size = CheckExpr(nw.ArraySize);

                    if (!size.IsError && !size.IsInteger)
                    {
                        Error(nw.ArraySize, $"an array length must be an integer, not '{size}'");
                    }
                    return Type.ArrayOf(type);
                }

                // A DELEGATE-CREATION EXPRESSION, `new Action<string>(list.Add)`:
                // the one argument -- a method group, a lambda or a delegate --
                // becomes the delegate, exactly as it would assigned to one. A
                // delegate is an interface here, with no constructor, so left
                // to the object path below it built an empty object.
                // Action and Func are interfaces with an Invoke; `new` of any
                // other interface is not C# at all.
                if (type.Symbol is { Kind: TypeKind.Interface } face && !type.IsArray
                    && (face.Decl?.IsDelegate == true || face.FindMethods("Invoke").Any())
                    && nw.Body.Adds.Count == 0
                    && nw.Body.Inits.Count == 0 && nw.Body.Indexes.Count == 0)
                {
                    if (nw.Args.Count != 1)
                    {
                        Error(nw, $"a delegate '{type}' is made from exactly one method, lambda or delegate");
                        foreach (Expr argument in nw.Args) CheckExpr(argument);
                        return Type.Error;
                    }
                    Expr source = nw.Args[0];
                    Type? outerDelegate = _wanted;
                    _wanted = type;
                    Type made = source is LambdaExpr lambda ? CheckLambda(lambda, type) : CheckExpr(source);
                    _wanted = outerDelegate;
                    if (source is not LambdaExpr && MethodGroupLambda(source, type) is LambdaExpr wrapper)
                    {
                        _r.Rewrites[source] = wrapper;
                        made = CheckLambda(wrapper, type);
                    }
                    else if (source is not LambdaExpr)
                    {
                        CheckAssignable(made, type, source, "the delegate's method");
                    }
                    _r.Rewrites[nw] = source;
                    return type;
                }

                // AN ARGUMENT IS NOT WHAT THE SURROUNDING DECLARATION IS
                // WAITING FOR, here as at any other call.
                Type? outerNew = _wanted;

                _wanted = null;

                List<Type> constructorArgs = nw.Args.Select(argument =>
                    argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null } ? Type.Any : CheckExpr(argument)).ToList();

                _wanted = outerNew;

                if (type.Symbol is TypeSymbol constructed)
                {
                    NormalizeConstructorArguments(nw, constructed, constructorArgs);
                    // A `params` CONSTRUCTOR HAS TWO FORMS, exactly as a
                    // `params` method does: an array supplied in the final
                    // position is an ordinary call, and anything else is
                    // packed into a fresh array here so that nothing below
                    // needs a second calling convention.
                    //
                    // `new MInstr(MOp.Mov, dest, source)` is how this
                    // compiler's own instruction selector writes every
                    // instruction it emits, and there were three hundred of
                    // them that no constructor accepted.
                    if (!constructed.Methods.Any(m => m.IsCtor && m.Params.Count == constructorArgs.Count
                                                   && constructorArgs.Where((a, i) =>
                                                        !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0))
                    {
                        MethodSymbol? variadic = constructed.Methods.FirstOrDefault(
                            m => m.IsCtor && m.Params.Count > 0 && m.Params[^1].IsParams
                              && m.Params[^1].Type.Element is not null
                              && constructorArgs.Count >= m.Params.Count - 1
                              && Enumerable.Range(0, m.Params.Count - 1)
                                           .All(i => constructorArgs[i].IsError
                                                  || Convertible(constructorArgs[i], m.Params[i].Type)));

                        if (variadic is not null && RefOf(variadic.Params[^1].Type.Element!) is TypeRef each)
                        {
                            int fixedCount = variadic.Params.Count - 1;
                            NewExpr packed = new()
                            {
                                Type = each,
                                Elements = nw.Args.Skip(fixedCount).ToList(),
                                Line = nw.Line, Col = nw.Col,
                            };

                            nw.Args.RemoveRange(fixedCount, nw.Args.Count - fixedCount);
                            nw.Args.Add(packed);
                            constructorArgs.RemoveRange(fixedCount, constructorArgs.Count - fixedCount);
                            constructorArgs.Add(CheckExpr(packed));
                        }
                    }

                    // A CONSTRUCTOR PARAMETER WITH A DEFAULT NEED NOT BE
                    // PASSED, exactly as a method's need not: `record
                    // MemPlace(..., bool Volatile = false)` is written with
                    // three arguments everywhere but once. Only exact arity was
                    // tried, so no constructor was found, none was recorded,
                    // and the object came back with every field still zero --
                    // silently, because nothing had said the call was wrong.
                    if (!constructed.Methods.Any(m => m.IsCtor && m.Params.Count == constructorArgs.Count
                        && constructorArgs.Where((a, i) => !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0))
                    {
                        MethodSymbol? shorter = constructed.Methods.FirstOrDefault(
                            m => m.IsCtor && m.Params.Count > constructorArgs.Count
                              && constructorArgs.Where((a, i) => !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0
                              && Enumerable.Range(constructorArgs.Count,
                                                  m.Params.Count - constructorArgs.Count)
                                           .All(i => m.Decl?.Params.ElementAtOrDefault(i)?.Default is not null));

                        if (shorter != null)
                        {
                            for (int i = constructorArgs.Count; i < shorter.Params.Count; i++)
                            {
                                Expr fallback = Written(shorter, shorter.Decl!.Params[i]);

                                nw.Args.Add(fallback);
                                constructorArgs.Add(CheckExpr(fallback));
                            }
                        }
                    }

                    List<MethodSymbol> ctors = constructed.Methods
                        .Where(m => m.IsCtor && m.Params.Count == constructorArgs.Count)
                        .ToList();

                    // THE CLOSEST FIT WINS, not the first one written. Every
                    // constructor whose parameters the arguments convert to
                    // is a candidate, and among those the one that matches
                    // the most parameters EXACTLY is the one C# picks.
                    //
                    // Without that count, two constructors whose parameters
                    // merely convert to each other are told apart by which
                    // was declared first: `DeflateStream(Stream,
                    // CompressionLevel, bool)` and `DeflateStream(Stream,
                    // CompressionMode, bool)` are both int-shaped and both
                    // convertible, so every call went to whichever came
                    // first in the class -- and asking for a compression
                    // level built a decompressor. A call still resolves as
                    // it did whenever no candidate matches more exactly,
                    // because the ordering is stable.
                    MethodSymbol? ctor = ctors
                        .Where(m => constructorArgs
                            .Where((a, i) => !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0)
                        .OrderByDescending(m => constructorArgs
                            .Where((a, i) => a.Equals(m.Params[i].Type)).Count())
                        .FirstOrDefault();

                    if (ctor != null)
                    {
                        _r.NewConstructors[nw] = ctor;
                        for (int i = 0; i < constructorArgs.Count; i++)
                        {
                            if (nw.Args[i] is LambdaExpr lambda)
                            {
                                constructorArgs[i] = CheckLambda(lambda, ctor.Params[i].Type);
                                continue;
                            }
                            if (MethodGroupLambda(nw.Args[i], ctor.Params[i].Type) is LambdaExpr wrapper)
                            {
                                _r.Rewrites[nw.Args[i]] = wrapper;
                                constructorArgs[i] = CheckLambda(wrapper, ctor.Params[i].Type);
                                continue;
                            }
                            if (nw.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null })
                            {
                                Type? saved = _wanted;
                                _wanted = ctor.Params[i].Type;
                                constructorArgs[i] = CheckExpr(nw.Args[i]);
                                _wanted = saved;
                            }
                            constructorArgs[i] = Settle(nw.Args[i], ctor.Params[i].Type, constructorArgs[i]);
                            CheckAssignable(constructorArgs[i], ctor.Params[i].Type,
                                            nw.Args[i], $"constructor argument {i + 1}");
                        }
                    }
                    else if (constructed.Methods.Any(m => m.IsCtor))
                    {
                        // NOTHING TO CALL IS AN ERROR, and was silence: the
                        // object was allocated, no constructor ran, and every
                        // field held its zero.
                        Error(nw, $"no constructor of '{constructed.Name}' accepts "
                                + $"({string.Join(", ", constructorArgs)})");
                    }
                }

                CheckInitBody(type, nw.Body);

                // EVERY REQUIRED MEMBER HAS TO BE SET, and this is the one place
                // that can tell whether it was.
                //
                // Checked against the members of the type and every type it
                // inherits from, because a required field on a base is required
                // of everything below it -- that is what makes the promise worth
                // anything to code holding the base.
                //
                // Only when the type is actually being built here. A constructor
                // that fills them in itself is C#'s SetsRequiredMembers, which
                // this has no attribute syntax for yet; until it does, a type
                // with required members is built with an initialiser.
                if (type.Symbol is TypeSymbol built)
                {
                    for (TypeSymbol? up = built; up != null; up = up.Base)
                    {
                        foreach (FieldSymbol f in up.Fields)
                        {
                            if (!f.Required || f.Static)
                            {
                                continue;
                            }

                            // A backing field is <Name>; the initialiser names
                            // the property.
                            string wanted = f.Name.StartsWith('<') && f.Name.EndsWith('>')
                                          ? f.Name[1..^1]
                                          : f.Name;

                            if (!nw.Inits.Any(i => i.Name == wanted))
                            {
                                Error(nw, $"'{built.Name}' requires '{wanted}' to be set here");
                            }
                        }
                    }
                }
                return type;
            }

            case UnaryExpr u:
            {
                Type t = CheckExpr(u.Operand);

                if (t.IsError)
                {
                    return Type.Error;
                }

                switch (u.Op)
                {
                    case UnOp.Checked:
                    case UnOp.Unchecked:
                        return t;

                    // `*p` -- what is at the address. One star fewer, and the
                    // pointee's width decides how much is read: a byte* reads
                    // one byte, not a word with seven neighbours in it.
                    case UnOp.Deref:
                        if (!t.IsPointer)
                        {
                            Error(u, $"'*' needs a pointer, not '{t}'");
                            return Type.Error;
                        }
                        return t.Pointee ?? Type.Error;

                    // `&x` -- where a thing lives. Only somewhere a thing
                    // actually lives: the address of a computed value would be
                    // the address of a register, which stops being that value
                    // the moment anything else is evaluated.
                    case UnOp.AddressOf:
                        RequireAssignable(u.Operand, "take the address of");
                        return t.PointerTo();

                    case UnOp.Not:
                        if (t.Prim != Prim.Bool)
                        {
                            Error(u, $"'!' needs a 'bool', not '{t}'");
                        }
                        return Type.Bool;

                    case UnOp.Neg:
                        if (!t.IsNumeric)
                        {
                            Error(u, $"'-' needs a number, not '{t}'");
                            return Type.Error;
                        }
                        return t.IsNullableValue ? Promote(t.Underlying).AsNullable() : Promote(t);

                    case UnOp.BitNot:
                        if (!t.IsInteger)
                        {
                            Error(u, $"'~' needs an integer, not '{t}'");
                            return Type.Error;
                        }

                        // `~E` IS AN E, as C# says -- which is what makes
                        // `flags & ~Ways.Read` a Ways rather than an int.
                        if (t.Symbol is { Kind: TypeKind.Enum })
                        {
                            Type same = new Type { Prim = t.Symbol.EnumUnderlying, Symbol = t.Symbol };
                            return t.IsNullableValue ? same.AsNullable() : same;
                        }
                        return t.IsNullableValue ? Promote(t.Underlying).AsNullable() : Promote(t);

                    default:
                        if (!t.IsNumeric)
                        {
                            Error(u, $"'++' and '--' need a number, not '{t}'");
                        }
                        BindWriteAccessor(u.Operand);
                        RequireAssignable(u.Operand, "increment");
                        return t;
                }
            }

            case BinaryExpr b:
                return CheckBinary(b);

            case AssignExpr a:
            {
                if (a.Op is null && a.Target is NameExpr { Name: "_" })
                {
                    Type discarded = CheckExpr(a.Value);
                    _r.DiscardAssignments.Add(a);
                    return discarded;
                }

                // `in` IS A REF THE CALLEE MAY NOT WRITE, which is the whole
                // difference between it and `ref`: the address is passed so a
                // large struct is not copied at the call, and C# refuses the
                // assignment that would change what the caller holds.
                if (a.Target is NameExpr readOnlyTarget
                    && Lookup(readOnlyTarget.Name) is ParamSym { ReadOnly: true })
                {
                    Error(a, $"'{readOnlyTarget.Name}' is an 'in' parameter and cannot be assigned to");
                }

                // WRITING TO SOMETHING FORGETS WHAT WAS PROVED ABOUT IT.
                //
                // `for (Node? t = this; t != null; t = t.Base)` proves t is not
                // null inside the loop -- and then assigns something that may
                // be. The proof is about the value that WAS there, so it is
                // dropped here: what the variable can hold is its declared type
                // and nothing narrower, and after the write nothing is known
                // again until it is tested.
                // THE VALUE IS CHECKED FIRST, and that order is the whole of it:
                // in `t = t.Base` the right-hand side is read while the proof
                // still holds -- t is not null there, which is why t.Base can
                // be reached at all -- and the proof dies with the write.
                //
                // Looked up by NAME rather than in the resolved map, because
                // the target has not been checked yet.
                // `new()` on the right of an assignment gets its type from the
                // declared lvalue, just as it does from a local declaration or
                // return type. Resolve the simple lvalue forms without reading
                // them (a read could itself require definite assignment), then
                // expose that expected type only while checking the value.
                Type? assignmentWanted = a.Target is NameExpr assignmentName
                    ? Lookup(assignmentName.Name) switch
                    {
                        LocalSym l => l.Type,
                        ParamSym p => p.Type,
                        FieldSym f => f.Field.Type,
                        CapturedFieldSym f => f.Field.Type,
                        _ => _thisType?.FindField(assignmentName.Name)?.Type
                          ?? _capturedThisType?.FindField(assignmentName.Name)?.Type,
                    }
                    : null;
                // A LAMBDA ASSIGNED TO A MEMBER -- `h.Op = v => v + 9` -- is
                // told what it is by the member, which has to be looked at
                // first to know. Only for a lambda: anything else is checked
                // in the order the language reads it.
                //
                // Checked here ONCE: `target` below reuses this result rather
                // than checking the same target expression again, which would
                // otherwise re-run CheckMember and duplicate any diagnostic it
                // reports (a typoed member name would then be reported twice).
                bool targetPrechecked = assignmentWanted is null
                    && a.Value is LambdaExpr or NewExpr or ConditionalExpr or SwitchExpr && a.Target is not NameExpr;
                if (targetPrechecked)
                {
                    assignmentWanted = CheckExpr(a.Target);
                }
                Type? previousWanted = _wanted;
                if (assignmentWanted is not null)
                {
                    _wanted = assignmentWanted;
                }
                // `_at![j] = _at[last]`: the target's receiver is evaluated
                // before the value, so its `!` holds on the right-hand side.
                ProveReceivers(a.Target switch { MemberExpr m => m.Target, IndexExpr ix => ix.Target, _ => null });
                Type value = CheckExpr(a.Value);
                _wanted = previousWanted;

                if (a.Target is NameExpr into && Lookup(into.Name) is Sym held)
                {
                    _notNull.Remove(held);
                }

                // AND EVERY PATH THROUGH WHAT WAS WRITTEN. Assigning `d` says
                // nothing about `d.Init` any more, and assigning `d.Init` says
                // nothing about `d.Init.Args`.
                if (Path(a.Target) is string written)
                {
                    _notNullPaths.RemoveWhere(
                        p => p == written || p.StartsWith(written + ".", StringComparison.Ordinal));

                    // WHAT WAS WRITTEN IS WHAT IS THERE. `init = new CtorInit
                    // { … };` leaves init not null, and C# knows it for the
                    // rest of the block -- which is the whole reason the
                    // assignment is written where it is.
                    if (!value.Nullable && value.Prim != Prim.NullLiteral && !value.IsError)
                    {
                        _notNullPaths.Add(written);

                        if (a.Target is NameExpr plain && Lookup(plain.Name) is Sym wrote
                            && wrote is LocalSym or ParamSym)
                        {
                            _notNull.Add(wrote);
                        }
                    }
                }

                LocalSym? assignedLocal = a.Target is NameExpr namedTarget
                                        ? Lookup(namedTarget.Name) as LocalSym
                                        : null;
                Type target;

                if (a.Op is null && assignedLocal is not null && a.Target is NameExpr targetName)
                {
                    _r.Resolved[targetName] = assignedLocal;
                    target = assignedLocal.Type;
                }
                else if (a.Op is null && a.Target is NameExpr fieldName
                         && Lookup(fieldName.Name) is FieldSym declaredField)
                {
                    // Flow analysis narrows a field READ, never the storage the
                    // field was declared with. Assigning null back into a T?
                    // after testing it non-null is legal; checking the narrowed
                    // read type here incorrectly treated that lvalue as T.
                    _r.Resolved[fieldName] = declaredField;
                    target = declaredField.Field.Type;
                }
                else if (a.Op is null && a.Target is NameExpr capturedName
                         && Lookup(capturedName.Name) is CapturedFieldSym capturedField)
                {
                    _r.Resolved[capturedName] = capturedField;
                    target = capturedField.Field.Type;
                }
                // A PARAMETER ASSIGNED TO IS THE PARAMETER, not a field of the
                // same name. The chain above knew about locals, fields and
                // captures and not about parameters, so `ticks = ...` inside
                // `static bool TryInner(string text, out long ticks)` bound to
                // the instance field `ticks` -- in a STATIC method, where there
                // is no instance to reach it through. The lowering then built
                // the field's address off a `this` that does not exist and the
                // optimiser fell over a register that was never defined.
                //
                // Reading the name was always right; only the assignment side
                // had the gap. C#'s rule is the ordinary one: a parameter
                // shadows a field for the whole body.
                else if (a.Op is null && a.Target is NameExpr paramName
                         && Lookup(paramName.Name) is ParamSym assignedParam)
                {
                    _r.Resolved[paramName] = assignedParam;
                    target = assignedParam.Type;
                }
                else if (a.Op is null && a.Target is NameExpr ownFieldName
                         && _thisType?.FindField(ownFieldName.Name) is FieldSymbol ownField)
                {
                    FieldSym symbol = new(ownField);
                    _r.Resolved[ownFieldName] = symbol;
                    target = ownField.Type;
                }
                else if (a.Op is null && a.Target is NameExpr capturedOwnName
                         && _capturedThisType?.FindField(capturedOwnName.Name) is FieldSymbol outerField
                         && _capturedThisField is not null)
                {
                    CapturedFieldSym symbol = new(_capturedThisField, outerField);
                    _r.Resolved[capturedOwnName] = symbol;
                    target = outerField.Type;
                }
                else
                {
                    target = targetPrechecked ? assignmentWanted! : CheckExpr(a.Target);

                    // A WRITE THROUGH `a![i]` STORES INTO THE ARRAY AS IT WAS
                    // DECLARED. Suppressing an array strips its elements'
                    // annotation too, for reading (SuppressExpr): `filled![i]`
                    // reads as not null. The same element as an assignment
                    // target is storage, and storage is what the declaration
                    // says -- C# takes `owners![i] = null` for a `Process?[]?`,
                    // since `!` speaks for the array reference, not for what
                    // it may hold. Reading the stripped type here refused it.
                    if (a.Target is IndexExpr { Target: SuppressExpr sureArray }
                        && _r.TypeOf(sureArray.Operand) is { IsArray: true, Element: Type declaredElement }
                        && declaredElement.Nullable)
                    {
                        target = declaredElement;
                    }

                    string? propertyName = a.Target switch
                    {
                        NameExpr n => n.Name,
                        MemberExpr m => m.Name,
                        _ => null,
                    };
                    MethodSymbol? getter = _r.Resolved.TryGetValue(a.Target, out Sym? property)
                        ? property switch
                        {
                            PropertyGetSym p => p.Getter,
                            CapturedPropertyGetSym p => p.Getter,
                            _ => null,
                        }
                        : null;
                    if (getter != null && propertyName != null)
                    {
                        MethodSymbol? setter = getter.Owner
                            .FindMethods("set_" + propertyName)
                            .FirstOrDefault(m => m.Params.Count == 1);
                        if (setter == null)
                        {
                            Error(a.Target, $"property '{propertyName}' has no setter");
                        }
                        else
                        {
                            _r.PropertySetters[a.Target] = setter;
                            target = setter.Params[0].Type;
                        }
                    }

                    // As with an unqualified field, flow narrowing describes a
                    // member READ and not its storage. Recover the declared
                    // type for a plain assignment through a member expression.
                    if (a.Op is null && _r.Resolved.TryGetValue(a.Target, out Sym? memberTarget))
                    {
                        target = memberTarget switch
                        {
                            FieldSym f => f.Field.Type,
                            CapturedFieldSym f => f.Field.Type,
                            PropertySetSym p => p.Setter.Params[^1].Type,
                            _ => target,
                        };
                    }
                }
                RequireAssignable(a.Target, "assign to");

                // WRITING THROUGH AN INDEXER needs the other accessor, and this
                // is the only path that knows a store is what this is. Checking
                // the target above has already found get_Item -- which a
                // compound assignment genuinely needs, since `b[i] += x` reads
                // before it writes.
                if (a.Target is IndexExpr store && _r.Indexers.ContainsKey(store))
                {
                    Type on = _r.TypeOf(store.Target);

                    // THE SAME INDEX TYPES THE READ CHOSE. The getter that was
                    // bound above says which indexer this is, so the store side
                    // lands on its pair rather than on whichever overload was
                    // declared first.
                    List<Type> index = _r.Indexers[store].Params.Select(pa => pa.Type).ToList();
                    MethodSymbol? setter = on.Symbol is null ? null
                        : IndexerFor(Reachable(on.Symbol, "set_Item"), index, store.Args.Count + 1);

                    if (setter is null)
                    {
                        Error(a, $"'{on}' can be read by index but not written to");
                    }
                    else
                    {
                        _r.IndexSetters[store] = setter;
                    }
                }

                if (a.Op is null)
                {
                    CheckAssignable(value, target, a.Value, "assignment");
                    if (assignedLocal is not null) { _assigned.Add(assignedLocal); }
                }
                else if (!target.IsError && !value.IsError)
                {
                    bool ok = target.IsNumeric && value.IsNumeric;

                    // `bool` supports the non-short-circuiting bitwise pair in
                    // C#, including their compound-assignment forms. This is
                    // useful when every predicate must run rather than stopping
                    // at the first false/true result.
                    if (target.Prim == Prim.Bool && value.Prim == Prim.Bool
                        && a.Op is BinOp.And or BinOp.Or or BinOp.Xor)
                    {
                        ok = true;
                    }

                    if (a.Op is BinOp.Add && target.Prim == Prim.String)
                    {
                        ok = true;
                    }

                    // A DELEGATE += OR -= IS COMBINE OR REMOVE on the multicast
                    // class the parser synthesised beside the delegate. The call
                    // is built here as ordinary syntax, bound like anything the
                    // program could have written, and remembered for lowering,
                    // which stores its result back into the same place.
                    if (a.Op is BinOp.Add or BinOp.Sub && target.Symbol?.Decl is { IsDelegate: true } delegateDecl)
                    {
                        ok = true;
                        Expr qualified = new NameExpr { Name = delegateDecl.Name + "__Multicast", Line = a.Line, Col = a.Col };
                        if (!string.IsNullOrEmpty(delegateDecl.Namespace))
                        {
                            string[] parts = delegateDecl.Namespace.Split('.');
                            Expr chain = new NameExpr { Name = parts[0], Line = a.Line, Col = a.Col };
                            for (int i = 1; i < parts.Length; i++)
                                chain = new MemberExpr { Target = chain, Name = parts[i], Line = a.Line, Col = a.Col };
                            qualified = new MemberExpr { Target = chain, Name = delegateDecl.Name + "__Multicast", Line = a.Line, Col = a.Col };
                        }
                        CallExpr synthesised = new()
                        {
                            Target = new MemberExpr { Target = qualified, Name = a.Op is BinOp.Add ? "Combine" : "Remove", Line = a.Line, Col = a.Col },
                            Line = a.Line, Col = a.Col,
                        };
                        synthesised.Args.Add(a.Target);
                        synthesised.Args.Add(a.Value);
                        synthesised.ArgNames.Add(null);
                        synthesised.ArgNames.Add(null);
                        CheckExpr(synthesised);
                        _r.DelegateCompounds[a] = synthesised;
                    }

                    if (!ok)
                    {
                        Error(a, $"cannot apply compound assignment to '{target}' and '{value}'");
                    }
                }

                // WHAT AN ASSIGNMENT IS WORTH IS WHAT WAS PUT THERE. The
                // declared type of the place is what it can hold; the value of
                // the expression is the value just written, and C# reads it
                // with the null state that value has. `MReg Got() => got ??=
                // m.NewReg();` is the shape: the variable is an `MReg?`, what
                // it now holds is an MReg, and the method returns one.
                if (target.IsReference && target.Nullable
                    && !value.Nullable && value.Prim != Prim.NullLiteral && !value.IsError)
                {
                    return target.AsNonNullable();
                }

                return target;
            }

            case ConditionalExpr c2:
            {
                // EACH ARM KNOWS WHAT THE CONDITION PROVED, which is the same
                // rule '&&' follows and for the same reason: only one of them
                // runs, and which one is exactly what the test decided.
                // `x == null ? "none" : x.name` is the shape that needs it, and
                // it is how anybody writes a default.
                CheckCondition(c2.Cond);

                // A DELEGATE IS WANTED: an arm that is a method group or a
                // lambda becomes one, as C#'s target-typed conditional makes
                // `decl is null ? null : decl.Add` an Action<string>.
                Type? wantedDelegate = _wanted is { IsError: false } w && w.AsNonNullable() is { } plain
                                       && plain.Symbol?.FindMethods("Invoke").Any() == true ? plain : null;
                Type Arm(Expr arm)
                {
                    if (wantedDelegate is not null && arm is LambdaExpr lambda) return CheckLambda(lambda, wantedDelegate);
                    Type had = CheckExpr(arm);
                    if (wantedDelegate is not null && !_r.Rewrites.ContainsKey(arm)
                        && MethodGroupLambda(arm, wantedDelegate) is LambdaExpr wrapper)
                    {
                        _r.Rewrites[arm] = wrapper;
                        return CheckLambda(wrapper, wantedDelegate);
                    }
                    return had;
                }

                // Checked again (a call's argument, once its overload is
                // known), what an earlier look decided about the arms goes.
                _r.Boxes.Remove(c2.Then);
                _r.Boxes.Remove(c2.Else);

                List<Sym> whenTrue = Assume(c2.Cond, true);
                Type a2 = Arm(c2.Then);

                Forget(whenTrue);

                List<Sym> whenFalse = Assume(c2.Cond, false);
                Type b2 = Arm(c2.Else);

                Forget(whenFalse);

                // A METHOD GROUP WITH NOTHING TO SAY WHICH DELEGATE it is has
                // no type yet (void): the call checks this again once its
                // overload says (FunctionSource). Nothing is decided now -- a
                // `null` arm against it must not put it in a nullable cell.
                if (a2.IsError || b2.IsError || a2.IsVoid || b2.IsVoid)
                {
                    return Type.Error;
                }

                // AN ARM AGAINST `null` MAKES THE WHOLE THING NULLABLE, and
                // for a VALUE that is a real conversion: the number has to go
                // into a cell, or the other arm's null and this arm's number
                // are read as the same kind of thing and the first HasValue
                // follows a number as though it were an address. `int? n = yes
                // ? 5 : null` crashed on exactly that.
                Type Lifted(Type had, Expr arm)
                {
                    Type whole = had.AsNullable();

                    if (whole.IsNullableValue && !had.IsNullableValue)
                    {
                        _r.Boxes.Add(arm);
                    }
                    return whole;
                }

                if (a2.Prim == Prim.NullLiteral)
                {
                    return Lifted(b2, c2.Else);
                }
                if (b2.Prim == Prim.NullLiteral)
                {
                    return Lifted(a2, c2.Then);
                }

                // ONE ARM IN A CELL AND THE OTHER NOT is the same conversion
                // one step along: the plain one goes into a cell too.
                if (a2.IsNullableValue && !b2.IsNullableValue && Convertible(b2, a2.Underlying))
                {
                    _r.Boxes.Add(c2.Else);
                    return a2;
                }
                if (b2.IsNullableValue && !a2.IsNullableValue && Convertible(a2, b2.Underlying))
                {
                    _r.Boxes.Add(c2.Then);
                    return b2;
                }

                // A CONSTANT ARM TAKES THE OTHER ARM'S TYPE when it fits in
                // it, which is C#'s implicit constant expression conversion.
                // `dyn.TextRel ? Elf.DfTextRel : 0` over a uint is a uint;
                // without this the zero stayed an int, `uint | int` widened to
                // long to hold both, and the flags word would not go back into
                // the uint it came from.
                if (a2.IsInteger && b2.IsInteger && !a2.Equals(b2))
                {
                    if (ConstantValue(c2.Else, _thisType) is long otherwise && Fits(otherwise, a2))
                    {
                        return a2;
                    }
                    if (ConstantValue(c2.Then, _thisType) is long so && Fits(so, b2))
                    {
                        return b2;
                    }
                }

                // AN ARRAY ARM OF AN INTERFACE-TYPED WHOLE GETS ITS HELPER, as it
                // would anywhere else an array becomes an interface: `enumerate
                // ? list : Enumerable.Empty<T>()` is an IEnumerable<T>, and the
                // array on its far side has no GetEnumerator slot to call.
                Type Joined(Type whole)
                {
                    foreach ((Type had, Expr arm) in new[] { (a2, c2.Then), (b2, c2.Else) })
                    {
                        if (had.IsArray && !whole.IsArray && ArrayFace(had, whole) is { } through
                            && _r.Types.TryGetValue(TypeKey(through), out TypeSymbol? face))
                        {
                            _r.Views[arm] = ArrayView(had.Element!, face);
                        }
                    }
                    return whole;
                }

                if (Convertible(a2, b2))
                {
                    return Joined(b2);
                }
                if (Convertible(b2, a2))
                {
                    return Joined(a2);
                }

                if (CommonReference(a2, b2) is Type common)
                {
                    return Joined(common);
                }

                if (CommonInterface(a2, b2) is Type shared)
                {
                    return Joined(shared);
                }

                Error(c2, $"the branches of a conditional have unrelated types '{a2}' and '{b2}'");
                return Type.Error;
            }

            case CastExpr cast:
            {
                Type operand = CheckExpr(cast.Operand);
                Type wanted = Resolve(cast.Type, _thisType);

                // `(int?)5` PUTS THE NUMBER IN A CELL, which is a conversion
                // and not merely a name for the same bits. Marked on the
                // operand, because that is the value the cell is made from.
                if (wanted.IsNullableValue && !operand.IsNullableValue
                    && operand.Prim != Prim.NullLiteral && !operand.IsError)
                {
                    _r.Boxes.Add(cast.Operand);
                }
                return wanted;
            }

            case SwitchExpr sx:
            {
                // WHAT SOMETHING IS WAITING FOR, kept before the arms are read:
                // checking them sets and clears it, and a switch expression
                // whose arms do not agree among themselves is converted to the
                // type its context wants. `o switch { RegOperand r => Lo(r),
                // ImmOperand i => Imm(i), _ => R(o) }` in a method returning
                // MOperand is three different types and one MOperand, which is
                // what C# makes of it.
                Type? target = _wanted;
                Type subject = CheckExpr(sx.Subject);

                // THE SUBJECT GETS A SLOT OF ITS OWN, evaluated once and read
                // back per arm.
                //
                // Holding it in a register across the arms is the obvious thing
                // and it does not survive contact with an arm that CALLS
                // anything -- comparing strings calls String.Compare, and a
                // call saves and restores the registers below it, so the one
                // holding the subject is live across a call boundary that knows
                // nothing about it. A slot is one store and a load per arm, and
                // this machine's loads are cheap.
                int held = NewSlot();

                _r.SwitchSlot[sx] = held;

                // A RELATIONAL ARM READS THE SUBJECT BACK from that slot: the
                // parser writes `>= 90 => …` as a guard over a SubjectExpr,
                // and this is what the SubjectExpr resolves to.
                _subject.Add((held, subject));
                Type result = Type.Error;
                bool first = true;
                bool exhaustive = false;

                foreach (SwitchArm arm in sx.Arms)
                {
                    // A TYPE PATTERN NAMES WHAT IT MATCHED, exactly as `is`
                    // does, and the name belongs to the ARM rather than to the
                    // whole switch -- two arms may both call it `n` and mean
                    // different types. Each gets a scope of its own.
                    PushScope();

                    // A BARE NAME THAT IS A CONSTANT IS A CONSTANT PATTERN.
                    //
                    // `r switch { RegZero => "zero", ... }` compares against a
                    // named number, and the parser cannot know that: a bare name
                    // after `switch {` reads as a type pattern, and only the
                    // checker knows which names are types. Turned into the value
                    // arm it always was, so nothing below here learns a case.
                    if (arm.Type is { Args.Count: 0, ArrayRank: 0, Nullable: false } bare
                        && arm.Binding is null
                        && !IsTypeName(bare.Name)
                        && (FindConstant(_thisType, bare.Name) is not null
                            || Lookup(bare.Name) is ConstSym))
                    {
                        arm.Value = new NameExpr { Name = bare.Name, Line = bare.Line, Col = bare.Col };
                        arm.Type = null;
                    }

                    if (arm.Type is not null)
                    {
                        Type tested = Resolve(arm.Type, _thisType);

                        if (tested.Symbol is { } armSymbol)
                        {
                            _r.TestedTypes[arm] = armSymbol;
                        }
                        else if (tested.IsArray)
                        {
                            _r.TestedArrays[arm] = tested;
                        }

                        // WHETHER THIS ARM NEEDS A RUNTIME TEST AT ALL.
                        //
                        // `n switch { long v when v > 0 => ... }` over a long
                        // subject is a pattern that always matches: it exists to
                        // NAME the subject so a guard can talk about it. There
                        // is no test to emit and nothing to emit it with -- a
                        // value type has no type bit, because there is no header
                        // on a long to read one out of.
                        //
                        // Recorded here rather than worked out again in the code
                        // generator, which has the TypeRef but not the subject's
                        // type and would have to guess.
                        // `object` IS NOT IsReference -- it is the machine word,
                        // which may hold one -- and asking only IsReference meant
                        // no arm of a switch over an object was ever tested: every
                        // type pattern matched, so the FIRST one won whatever was
                        // handed in. `o switch { string s => ..., Dog d => ... }`
                        // answered the string arm for a Dog and then faulted
                        // joining the null it had bound.
                        if (tested.IsReference && (subject.IsReference || subject.Prim == Prim.Any))
                        {
                            _r.ArmTests.Add(arm);
                            if (tested.Prim == Prim.String && arm.Type.ArrayRank == 0)
                            {
                                _r.StringTests.Add(arm);
                            }
                        }

                        // A VALUE TYPE MATCHED AGAINST AN OBJECT IS A BOX TEST,
                        // and a test is exactly what this decides there is none
                        // of. `o switch { int n => ... }` has to ask whether the
                        // box is an int's and take the value out of it if it is
                        // -- which is what `o is int n` already did. Without it
                        // the arm matched anything at all and bound the BOX'S
                        // ADDRESS, so a boxed 7 printed as a pointer and no
                        // later arm was ever reached.
                        else if (BoxMatched(subject, tested))
                        {
                            _r.ArmTests.Add(arm);
                        }
                        else if (!Convertible(subject, tested) && !subject.IsError && !tested.IsError)
                        {
                            Error(arm, $"this arm matches '{tested}' and the subject is '{subject}'");
                        }

                        if (arm.Binding is not null)
                        {
                            int slot = NewSlot();

                            _r.ArmSlot[arm] = slot;
                            LocalSym armLocal = new(slot, tested, arm.Binding);
                            _r.PatternSym[arm] = armLocal;
                            Declare(arm, arm.Binding, armLocal);
                            _assigned.Add(armLocal);
                        }

                        // A PATTERN THAT ALWAYS MATCHES IS AS GOOD AS '_' for
                        // deciding whether the switch can fall off the end.
                        exhaustive = exhaustive || (!_r.ArmTests.Contains(arm) && arm.When is null);
                    }
                    else if (arm.Value is not null)
                    {
                        Type constant = CheckExpr(arm.Value);

                        if (!Convertible(constant, subject) && !constant.IsError && !subject.IsError)
                        {
                            Error(arm, $"this arm compares '{subject}' against '{constant}'");
                        }

                    }
                    // `var big => …`: no test, and the name is the subject
                    // under another name -- with the subject's own type, which
                    // is what `var` means in a pattern.
                    else if (arm.Discard && arm.Binding is not null)
                    {
                        int slot = NewSlot();

                        _r.ArmSlot[arm] = slot;
                        LocalSym named = new(slot, subject, arm.Binding);
                        _r.PatternSym[arm] = named;
                        Declare(arm, arm.Binding, named);
                        _assigned.Add(named);
                    }

                    List<Sym> armProof = new();
                    if (arm.When is not null)
                    {
                        CheckCondition(arm.When);
                        armProof = Assume(arm.When, true);
                    }

                    // `_` WITH NO GUARD IS THE ONE THAT MAKES IT EXHAUSTIVE.
                    // One behind a guard matches only sometimes, so it settles
                    // nothing -- and a switch expression that falls off the end
                    // has no value to be.
                    exhaustive = exhaustive || (arm.Discard && arm.When is null);

                    Type value = CheckExpr(arm.Result);

                    Forget(armProof);

                    PopScope();

                    if (value.IsError)
                    {
                        result = Type.Error;
                        first = false;
                        continue;
                    }

                    if (first)
                    {
                        result = value;
                        first = false;
                    }
                    else if (value.Prim == Prim.NullLiteral)
                    {
                        result = result.AsNullable();
                    }
                    else if (result.Prim == Prim.NullLiteral)
                    {
                        result = value.AsNullable();
                    }
                    else if (Convertible(value, result))
                    {
                        // the arms agree already
                    }
                    else if (Convertible(result, value))
                    {
                        result = value;
                    }
                    else if (target is { IsError: false } wanted
                          && Convertible(value, wanted) && Convertible(result, wanted))
                    {
                        result = wanted;
                    }
                    else if (!result.IsError)
                    {
                        Error(arm, $"this arm is '{value}' and the ones before it are '{result}'");
                        result = Type.Error;
                    }
                }

                _subject.RemoveAt(_subject.Count - 1);

                if (sx.Arms.Count == 0)
                {
                    Error(sx, "a switch expression needs at least one arm");
                    return Type.Error;
                }

                // AND WHAT THE CONTEXT WANTS IS WHAT IT IS, when every arm can
                // be that. C# gives a switch expression a natural type and then
                // CONVERTS it where it is used; converting the whole thing
                // rather than each arm is not the same operation when the arms
                // are of different widths or signs. `op switch { 1 => (int)v,
                // 2 => (uint)v, … }` in a method returning long is four longs
                // in C#, and here the arms agreed on int first and the uint was
                // then sign-extended into the long -- a wrong answer, quietly.
                if (target is { IsError: false, IsVoid: false } asked && !result.Equals(asked)
                    && !result.IsError
                    && sx.Arms.All(arm => _r.ExprType.TryGetValue(arm.Result, out Type? had)
                                       && (had.IsError || Convertible(had, asked)
                                           || (had.IsInteger && asked.IsInteger))))
                {
                    result = asked;
                }

                // NOT A WARNING. An expression has to have a value on every
                // path, and there is nothing sensible to produce when nothing
                // matched -- C# throws, and throwing here would mean inventing
                // an exception type and a control-flow path nobody asked for.
                if (!exhaustive)
                {
                    Error(sx, "this switch expression has no '_' arm, so there is a value it cannot produce");
                }
                return result;
            }

            case SizeOfExpr so:
            {
                // sizeof(T). A constant the compiler knows, which is the whole
                // of what it is in C# too -- and what makes `p + 1` mean the
                // next element rather than the next byte.
                //
                // Recorded rather than recomputed later, because by the time
                // the code generator runs it has a TypeRef and no context to
                // resolve it in -- and inside a generic the answer depends on
                // which instantiation this is.
                _r.SizeOfs[so] = SizeInBytes(Resolve(so.Type, _thisType));
                return Type.I32;
            }

            case TypeOfExpr to:
            {
                // typeof(T). Recorded the same way and for the same reason as
                // sizeof: the code generator has a TypeRef and nowhere to
                // resolve it, and inside a generic the answer depends on which
                // instantiation is being compiled.
                Type named = Resolve(to.Type, _thisType);

                // A PRIMITIVE HAS A DESCRIPTOR TOO. It has no vtable to put
                // one in front of, but a descriptor is what a type's IDENTITY
                // is here -- two of them compare by address -- and `typeof(int)`
                // is ordinary C# that a generic asking what T is cannot do
                // without. One is emitted per primitive and shared, so every
                // `typeof(int)` in a program is the same address.
                if (named.Symbol is null || named.ArrayRank > 0)
                {
                    if (named.ArrayRank > 0 && named.Element is Type element)
                    {
                        _r.ArrayTypeOfs[to] = element;
                        return Type.TypeHandle;
                    }

                    if (named.ArrayRank == 0 && named.PointerDepth == 0 && Descriptive(named.Prim))
                    {
                        _r.PrimitiveTypeOfs[to] = named.Prim;
                        return Type.TypeHandle;
                    }

                    Error(to, $"typeof needs a type with a descriptor; '{named}' has none");
                    return Type.Error;
                }

                _r.TypeOfs[to] = named.Symbol;
                return Type.TypeHandle;
            }

            case DefaultExpr df:
            {
                // A BARE `default` TAKES THE TYPE OF WHATEVER IS WAITING FOR
                // IT: a declaration, a return, an assignment -- and otherwise
                // an argument, whose type belongs to a parameter of an overload
                // not yet chosen. Error is what an argument whose type is not
                // yet decided answers, and CheckCall fills it in afterwards;
                // the same two steps `out var` takes, for the same reason.
                if (df.Type.Name.Length == 0)
                {
                    if (_wanted is not { } waiting)
                    {
                        return Type.Error;
                    }

                    return waiting.IsReference ? waiting.AsNullable() : waiting;
                }

                Type of = Resolve(df.Type, _thisType);

                // NULLABLE WHEN IT IS A REFERENCE, because the zero of a
                // reference type IS null -- and a checker that called it
                // non-null would let it be assigned somewhere that had asked
                // for a value which cannot be null.
                //
                // EXCEPT FOR A TYPE PARAMETER, which C# leaves oblivious:
                // `default(T)` on an unconstrained T is a T, and assigning it
                // to a T is how every TryGetValue in the class library says
                // "nothing was there". The parameter is gone by the time the
                // checker sees this -- substitution replaced it -- so the
                // expression remembers what it was written as.
                return of.IsReference && !df.OfTypeParameter ? of.AsNullable() : of;
            }

            case RefArgExpr ra:
            {
                // A DECLARATION FORM brings the variable into being here, in
                // the enclosing scope -- `Try(out int n)` leaves n usable for
                // the rest of the method, which is the entire idiom.
                if (ra.Declare is not null)
                {
                    Type declared = Resolve(ra.Declare, _thisType);

                    LocalSym declaredLocal = new(NewSlot(), declared, ra.Name!);
                    Declare(ra, ra.Name!, declaredLocal);
                    _assigned.Add(declaredLocal);
                    CheckExpr(ra.Target);
                    return declared;
                }

                // `out var x` NAMES NO TYPE, and the type it wants belongs to a
                // parameter of an overload that has not been chosen yet --
                // because overloads are resolved from the argument types, and
                // this is one of them.
                //
                // So it answers Error here and is filled in afterwards, in
                // CheckCall, once `best` is known. Error is not a lie of
                // convenience: overload resolution already treats an errored
                // argument as matching anything, which is exactly the behaviour
                // an argument whose type is not yet decided needs, and it means
                // one `out var` cannot make an otherwise unambiguous call
                // ambiguous.
                if (ra.Name is not null)
                {
                    return Type.Error;
                }

                if (ra.IsOut && ra.Target is NameExpr outName
                    && Lookup(outName.Name) is LocalSym outLocal)
                {
                    _r.Resolved[outName] = outLocal;
                    return outLocal.Type;
                }

                Type target = CheckExpr(ra.Target);

                RequireReferenceVariable(ra.Target);
                return target;
            }

            case IsExpr isx:
            {
                Type operand = CheckExpr(isx.Operand);

                // A VAR PATTERN ASKS NOTHING AND NAMES WHAT IT FOUND. The name
                // has the subject's own type, nullability and all: C# says a
                // var pattern matches null too, so narrowing it would be a
                // promise the pattern never made.
                if (isx.Type.Name == TypeRef.Anything)
                {
                    if (isx.Binding is not null)
                    {
                        int held = NewSlot();
                        LocalSym named = new(held, operand, isx.Binding);

                        _r.PatternSlot[isx] = held;
                        _r.PatternSym[isx] = named;
                        Declare(isx, isx.Binding, named);
                        _assigned.Add(named);
                    }
                    return Type.Bool;
                }

                // A BARE CONSTANT NAME IN A PATTERN IS A VALUE, NOT A TYPE.
                // The parser cannot know which it is. C# commonly writes
                // property patterns such as `{ Name: TaskTypeName }`, and the
                // compiler itself uses constant identifiers in `or` patterns.
                // Once symbols exist, turn that ambiguous IsExpr into the
                // equality test it denotes and let the ordinary constant path
                // type-check and emit it.
                NameExpr possibleConstant = new()
                {
                    Name = isx.Type.Name,
                    Line = isx.Type.Line,
                    Col = isx.Type.Col,
                };
                _quiet++;
                CheckExpr(possibleConstant);
                _quiet--;
                bool isConstant = _r.Resolved.TryGetValue(possibleConstant, out Sym? possibleSym)
                                  && possibleSym is ConstSym
                               || _r.Rewrites.TryGetValue(possibleConstant, out Expr? possibleRewrite)
                                  && possibleRewrite is LiteralExpr;

                if (isx.Binding is null && !IsTypeName(isx.Type.Name) && isConstant)
                {
                    BinaryExpr valuePattern = new()
                    {
                        Op = BinOp.Eq,
                        Left = isx.Operand,
                        Right = possibleConstant,
                        Line = isx.Line,
                        Col = isx.Col,
                    };
                    _r.Rewrites[isx] = valuePattern;
                    return CheckExpr(valuePattern);
                }

                // `is { } y` NAMES THE SUBJECT, and its type is the subject's
                // own with the nullability taken off -- a pattern that matched
                // matched something, which is the whole point of the binding.
                Type tested = isx.Type.Name == TypeRef.Same
                            ? operand.AsNonNullable()
                            : Resolve(isx.Type, _thisType);

                if (tested.Symbol is { } testedSymbol)
                {
                    _r.TestedTypes[isx] = testedSymbol;
                }
                else if (tested.IsArray)
                {
                    _r.TestedArrays[isx] = tested;
                }

                if (operand.IsNullableValue)
                {
                    if (tested.Equals(operand.Underlying))
                    {
                        _r.NullablePatterns.Add(isx);
                    }
                    else if (!tested.IsError)
                    {
                        Error(isx, $"a '{operand}' can only match its held '{operand.Underlying}' value");
                    }
                }
                else if (!operand.IsReference && tested.Equals(operand))
                {
                    // A value already known to be T always matches `is T x`;
                    // the declaration still has to receive the value.
                    _r.ValuePatterns.Add(isx);
                }

                // A DECLARATION PATTERN: `x is T t` tests and NAMES the result,
                // so that the branch that knows the test passed does not have to
                // cast it back. The parser has always read the name; nothing
                // declared it, so every use of one was an undeclared identifier.
                //
                // The variable is declared in the ENCLOSING scope rather than in
                // the branch, which is what C# does and is not merely simpler:
                // `if (x is not T t) return;` is meant to leave t usable for the
                // whole rest of the method, and a binding scoped to the test's
                // own branch could never do that.
                //
                // NOT NULLABLE, and that is the point of it. The name exists on
                // the path where the test succeeded, and a pattern that matched
                // matched something.
                if (tested.Prim == Prim.String && isx.Type.ArrayRank == 0)
                {
                    _r.StringTests.Add(isx);
                }

                if (isx.Binding is not null)
                {
                    int slot = NewSlot();
                    LocalSym pattern = new(slot, tested, isx.Binding);

                    _r.PatternSlot[isx] = slot;
                    _r.PatternSym[isx] = pattern;
                    Declare(isx, isx.Binding, pattern);
                    _assigned.Add(pattern);
                }
                return Type.Bool;
            }

            case WithExpr copy:
            {
                // THE TYPE IS THE SOURCE'S. There is nothing else it could be:
                // a copy of a Token is a Token, and `with` cannot change what
                // something is, only what it holds.
                Type of = CheckExpr(copy.Source);

                if (of.Symbol is not TypeSymbol owner)
                {
                    if (!of.IsError)
                    {
                        Error(copy, $"'{of}' has no members to change, so 'with' means nothing for it");
                    }
                    return of;
                }

                foreach (InitAssign init in copy.Inits)
                {
                    // `with` changes members to VALUES. C# gives it the same
                    // braces as an object initialiser but not the same
                    // contents, and a nested one has nothing to mean here:
                    // the copy is made before anything in it can be reached.
                    if (init.Value is not Expr given)
                    {
                        Error(init, $"'with' sets '{init.Name}' to a value; a nested initialiser is not one");
                        continue;
                    }

                    Type value = CheckExpr(given);
                    FieldSymbol? field = owner.FindField(init.Name)
                                      ?? owner.FindField("<" + init.Name + ">");

                    if (field is null)
                    {
                        Error(init, $"'{owner.Name}' has no member '{init.Name}' to change");
                        continue;
                    }

                    _r.InitField[init] = field;
                    CheckAssignable(value, field.Type, given, $"'{init.Name}'");
                }

                return of;
            }

            case AsExpr asx:
            {
                CheckExpr(asx.Operand);
                Type type = Resolve(asx.Type, _thisType);

                if (type.Symbol is { } asSymbol)
                {
                    _r.TestedTypes[asx] = asSymbol;
                }
                else if (type.IsArray)
                {
                    _r.TestedArrays[asx] = type;
                }

                if (!type.IsReference && !type.IsError)
                {
                    Error(asx, $"'as' needs a reference type, and '{type}' is a value type");
                }

                if (type.Prim == Prim.String && asx.Type.ArrayRank == 0)
                {
                    _r.StringTests.Add(asx);
                }
                // 'as' yields null on failure, so its result is always nullable.
                return type.AsNullable();
            }

            case AwaitExpr aw:
            {
                Type operand = CheckExpr(aw.Operand);

                if (_method is { Async: false })
                {
                    Error(aw, $"'{_method.Name}' is not async, so it cannot await");
                    return Type.Error;
                }

                if (operand.IsError)
                {
                    return Type.Error;
                }

                // THE AWAITER PATTERN, as C# defines it: anything with a
                // GetAwaiter() whose result has IsCompleted, OnCompleted and
                // GetResult can be awaited, and the await's value is what
                // GetResult returns. Task and Task<T> are two such things;
                // nothing about them is special here.
                if (Awaiter(operand, aw) is not AwaitInfo info)
                {
                    return Type.Error;
                }
                _r.Awaits[aw] = info;

                // WHAT A STILL-OPEN TASK PRODUCES. When the awaited type is a
                // template whose specialisation has not been made yet (see
                // Close), GetResult is the template's own `T`; the arguments
                // the type is carrying say what that T is, and without them
                // the await reports handing back a 'T'.
                Type produces = info.GetResult.Returns;

                // WHAT A STILL-OPEN TASK PRODUCES. When the awaited type is a
                // template whose specialisation has not been made yet (see
                // Close), GetResult is the template's own `T`; the arguments
                // the type is carrying say what that T is, and without them
                // the await reports handing back a 'T'. Only an open result
                // needs this: a real specialisation already says `int`.
                if (Open(produces))
                {
                    produces = Close(produces, Arguments(operand, info.GetAwaiter.Returns));
                }

                return produces;
            }

            case BaseExpr:
                if (_thisType?.Base is null)
                {
                    Error(e, "'base' is not available: this type has no base class");
                    return Type.Error;
                }
                return new Type { Prim = Prim.Void, Symbol = _thisType.Base };

            default:
                return Type.Error;
        }
    }

    /// <summary>Whether a type still mentions a type parameter anywhere in it.</summary>
    /// <summary>
    /// Whether this is a TEMPLATE WITH ITS ARGUMENTS BESIDE IT rather than the
    /// specialisation that spells them: a `Span&lt;ulong&gt;` from before any
    /// `Span$ulong` has been written.
    ///
    /// It is what an inferred type looks like for ONE ROUND. `x.AsSpan()` asks
    /// for a copy of AsSpan made for ulong, and the copy -- with the
    /// `Span$ulong` in its signature that makes the monomorphiser write that
    /// class -- belongs to the round after this one. Nothing about the program
    /// is wrong here; the name does not exist yet, so a check against it would
    /// be a check against T, and the round that has the copy checks it for
    /// real.
    /// </summary>
    private static bool Unmade(Type t)
        => t.Symbol?.Decl is { TypeParams.Count: > 0 } template
        && t.Args.Count == template.TypeParams.Count;

    private static bool Open(Type t)
    {
        if (t.ParamName != null)
        {
            return true;
        }

        if (t.Element is Type element && Open(element))
        {
            return true;
        }

        foreach (Type a in t.Args)
        {
            if (Open(a))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the type parameters of an AWAITER stand for, read off the task it
    /// came from: `Task&lt;T&gt;.GetAwaiter()` returns a `TaskAwaiter&lt;T&gt;`,
    /// so the awaiter's parameters are the task's arguments in the order the
    /// task passed them on.
    /// </summary>
    private Dictionary<string, Type>? Arguments(Type awaited, Type awaiter)
    {
        if (awaited.Symbol?.Decl is not { } task || task.TypeParams.Count != awaited.Args.Count
            || awaited.Args.Count == 0)
        {
            return null;
        }

        Dictionary<string, Type> taskBound = new(StringComparer.Ordinal);

        for (int i = 0; i < task.TypeParams.Count; i++)
        {
            taskBound[task.TypeParams[i].Name] = awaited.Args[i];
        }

        if (awaiter.Symbol?.Decl is not { } decl || decl.TypeParams.Count != awaiter.Args.Count)
        {
            return taskBound;
        }

        Dictionary<string, Type> bound = new(StringComparer.Ordinal);

        for (int i = 0; i < decl.TypeParams.Count; i++)
        {
            bound[decl.TypeParams[i].Name] = Close(awaiter.Args[i], taskBound);
        }

        return bound;
    }

    /// <summary>
    /// The awaiter pattern on a type, or null with an error naming what is
    /// missing.
    /// </summary>
    private AwaitInfo? Awaiter(Type awaited, Node at)
    {
        MethodSymbol? getAwaiter = awaited.Symbol?.FindMethods("GetAwaiter")
                                          .FirstOrDefault(m => m.Params.Count == 0 && !m.Static);
        if (getAwaiter is null)
        {
            Error(at, $"'{awaited}' cannot be awaited: it has no GetAwaiter()");
            return null;
        }

        TypeSymbol? awaiter = getAwaiter.Returns.Symbol;
        MethodSymbol? isCompleted = awaiter?.FindMethods("get_IsCompleted").FirstOrDefault(m => m.Params.Count == 0);
        MethodSymbol? onCompleted = awaiter?.FindMethods("OnCompleted").FirstOrDefault(m => m.Params.Count == 1);
        MethodSymbol? getResult = awaiter?.FindMethods("GetResult").FirstOrDefault(m => m.Params.Count == 0);

        if (awaiter is null || isCompleted is null || onCompleted is null || getResult is null)
        {
            Error(at, $"'{getAwaiter.Returns}' is not an awaiter: it needs IsCompleted, OnCompleted(Action) and GetResult()");
            return null;
        }
        return new AwaitInfo(getAwaiter, isCompleted, onCompleted, getResult);
    }

    /// <summary>
    /// What `return` in an async method hands back: the result type of the
    /// task it declares, found the same way an await finds it, or nothing for
    /// `async void` and `async Task`.
    /// </summary>
    private Type AsyncResult(MethodSymbol m, Node at)
    {
        if (m.Returns.IsVoid)
        {
            return Type.Void;
        }
        AwaitInfo? info = Awaiter(m.Returns, at);
        return info?.GetResult.Returns ?? Type.Error;
    }

    /// <summary>The result type of a task type, without reporting: a lambda's shape is decided before its body is checked.</summary>
    private static Type AsyncBodyType(Type task)
    {
        MethodSymbol? getAwaiter = task.Symbol?.FindMethods("GetAwaiter").FirstOrDefault(m => m.Params.Count == 0);
        MethodSymbol? getResult = getAwaiter?.Returns.Symbol?.FindMethods("GetResult").FirstOrDefault(m => m.Params.Count == 0);
        return getResult?.Returns ?? Type.Void;
    }

    /// <summary>Small integer types compute at 32 bits, as they do in C#.</summary>
    private static Type Promote(Type t) => NumericRules.Unary(t);

    /// <summary>
    /// How many bytes one of these takes.
    ///
    /// An OBJECT is a pointer, so a class is a word however many fields it has
    /// -- which is what C# answers for a reference type too. A struct would be
    /// its contents, and there are none with fields yet.
    /// </summary>
    private static int SizeInBytes(Type t)
        => t.IsPointer || t.IsReference || t.Symbol is { Kind: TypeKind.Class or TypeKind.Interface }
         ? 8
         : t.Size;

    private void RequireAssignable(Expr target, string what)
    {
        if (_r.PropertySetters.ContainsKey(target)
            || target is IndexExpr index && _r.IndexSetters.ContainsKey(index))
        {
            return;
        }

        bool ok = target switch
        {
            NameExpr n  => _r.Resolved.TryGetValue(n, out Sym? s) && s is LocalSym or ParamSym or FieldSym or PropertySetSym or CapturedFieldSym,
            MemberExpr  => true,
            IndexExpr   => true,

            // `*p = v` is a store, and `&*p` is p. A dereference is a place,
            // which is the whole reason a pointer is worth having.
            UnaryExpr { Op: UnOp.Deref } => true,
            _           => false,
        };

        if (!ok)
        {
            Error(target, $"cannot {what} this expression");
        }
    }

    /// <summary>
    /// Requires actual storage for a ref/out argument. Properties and ordinary
    /// indexers may be assignment targets through setter calls, but they are not
    /// C# variables and consequently have no stable address a callee can retain.
    /// A one-dimensional array or pointer element is a variable and is checked
    /// and addressed by code generation.
    /// </summary>
    private void RequireReferenceVariable(Expr target)
    {
        bool ok = target switch
        {
            NameExpr name => _r.Resolved.TryGetValue(name, out Sym? named)
                && named is LocalSym or ParamSym or FieldSym or CapturedFieldSym,
            MemberExpr member => _r.Resolved.TryGetValue(member, out Sym? selected)
                && selected is FieldSym,
            IndexExpr index => index.Args.Count == 1
                && !_r.Indexers.ContainsKey(index)
                && (_r.TypeOf(index.Target).IsArray
                    || _r.TypeOf(index.Target).IsPointer),
            UnaryExpr { Op: UnOp.Deref } => true,
            _ => false,
        };

        if (!ok)
        {
            Error(target,
                "cannot pass this expression by reference; use a variable, field, array element or pointer element");
        }
    }

    /// <summary>
    /// Records the write accessor paired with a property/indexer read.  Update
    /// expressions do not pass through assignment binding, but they need the
    /// same setter as <c>+=</c> and simple assignment so code generation can
    /// capture one receiver/index location and use it for both accessors.
    /// </summary>
    private void BindWriteAccessor(Expr target)
    {
        if (target is IndexExpr index && _r.Indexers.ContainsKey(index)
            && !_r.IndexSetters.ContainsKey(index))
        {
            Type on = _r.TypeOf(index.Target);
            List<Type> keys = _r.Indexers[index].Params.Select(pa => pa.Type).ToList();
            MethodSymbol? setter = on.Symbol is null ? null
                : IndexerFor(Reachable(on.Symbol, "set_Item"), keys, index.Args.Count + 1);

            if (setter is null)
            {
                Error(index, $"'{on}' can be read by index but not written to");
            }
            else
            {
                _r.IndexSetters[index] = setter;
            }
            return;
        }

        string? propertyName = target switch
        {
            NameExpr name => name.Name,
            MemberExpr member => member.Name,
            _ => null,
        };
        MethodSymbol? getter = _r.Resolved.TryGetValue(target, out Sym? resolved)
            ? resolved switch
            {
                PropertyGetSym property => property.Getter,
                CapturedPropertyGetSym property => property.Getter,
                _ => null,
            }
            : null;

        if (getter is null || propertyName is null
            || _r.PropertySetters.ContainsKey(target))
        {
            return;
        }

        MethodSymbol? propertySetter = getter.Owner
            .FindMethods("set_" + propertyName)
            .FirstOrDefault(m => m.Params.Count == 1);

        if (propertySetter is null)
        {
            Error(target, $"property '{propertyName}' has no setter");
        }
        else
        {
            _r.PropertySetters[target] = propertySetter;
        }
    }

    private void RequireNonNull(Type t, Node at, string what)
    {
        if (!t.Nullable)
        {
            return;
        }

        string message = $"'{t}' may be null; use '?.' or check it before you {what} it";

        if (t.IsReference) Warning(at, message);
        else Error(at, message);

        // AND AFTER A DEREFERENCE IT IS NOT NULL. That is C#'s rule and the
        // only reading that makes sense: either the value was there, or the
        // program is already over. It means ONE diagnostic per place a proof
        // is missing rather than one per use of the same thing afterwards.
        Proved(at);
    }

    /// <summary>
    /// Records that this expression is not null from here on -- because it was
    /// just dereferenced, or because the author wrote `!` after it.
    /// </summary>
    /// <summary>
    /// The `!`s along a receiver chain -- `a!.b!.c` -- applied now, because
    /// C# evaluates the receiver before what follows it (a call's arguments,
    /// an assignment's value) and those are checked here first.
    /// </summary>
    private void ProveReceivers(Expr? receiver)
    {
        for (Expr? chain = receiver; chain is not null;
             chain = chain switch
             {
                 MemberExpr inner => inner.Target,
                 IndexExpr indexed => indexed.Target,
                 SuppressExpr sure => sure.Operand,
                 _ => null,
             })
        {
            if (chain is SuppressExpr asserted)
            {
                Proved(asserted.Operand);
            }
        }
    }

    private void Proved(Node at)
    {
        if (at is not Expr e)
        {
            return;
        }

        if (Path(e) is string route)
        {
            _notNullPaths.Add(route);
        }

        if (_r.Resolved.TryGetValue(e, out Sym? sym)
            && sym is LocalSym or ParamSym or FieldSym or CapturedFieldSym)
        {
            _notNull.Add(sym);
        }
    }

    // ---- null state ---------------------------------------------------------
    //
    // WHICH NULLABLE THINGS ARE KNOWN NOT TO BE NULL HERE.
    //
    // Without this, nullability is a property of the DECLARATION and nothing
    // else -- so `if (x != null) { x.Y }` was an error, which makes a nullable
    // reference useless: the only way to read one would be `?.`, and the whole
    // point of testing it is to stop having to. C# calls this the null STATE,
    // as against the null-ability, and tracks it per symbol as the checker
    // walks the code.
    //
    // Locals and parameters are tracked by symbol. Stable member paths are
    // tracked separately below and invalidated by writes to their prefixes.
    private readonly HashSet<Sym> _notNull = new();
    private readonly HashSet<LocalSym> _assigned = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// MEMBER PATHS proved non-null: `d.Init`, `node.Left.Right`.
    ///
    /// C# tracks these as well as plain names, and the shape that needs it is
    /// the ordinary one: `if (d.Init is null) { … } else { d.Init.IsThis }`.
    /// Kept as text because a path is not a symbol -- it is a route to one --
    /// and any write to a prefix of it forgets everything below.
    /// </summary>
    private readonly HashSet<string> _notNullPaths = new(StringComparer.Ordinal);

    /// <summary>
    /// The path an expression spells, or null when it is not one: a local or
    /// parameter, optionally followed by member names.
    ///
    /// A CALL ANYWHERE IN IT DISQUALIFIES IT, and so does an index: the thing
    /// proved non-null must be the thing read afterwards, and neither of those
    /// answers the same twice by definition.
    /// </summary>
    private string? Path(Expr e)
    {
        switch (e)
        {
            case NameExpr n when (ResolvedOrLookup(n)
                                  is LocalSym or ParamSym or FieldSym or CapturedFieldSym)
                                  || _thisType?.FindField(n.Name) is not null
                                  || _capturedThisType?.FindField(n.Name) is not null:
                return n.Name;

            case MemberExpr m when Path(m.Target) is string root:
                return root + "." + m.Name;

            default:
                return null;
        }
    }

    private Sym? ResolvedOrLookup(NameExpr n)
        => _r.Resolved.TryGetValue(n, out Sym? found) ? found : Lookup(n.Name);

    /// <summary>
    /// What a condition proves, and about what.
    ///
    /// Answers the symbol a test is about, and whether TRUE means non-null.
    /// `x != null` proves it when true; `x == null` proves it when false; and
    /// `x is Foo` proves it when true, because a null matches no type.
    /// </summary>
    /// <summary>
    /// The receiver a null-conditional chain hangs from -- the `t.Decl` of
    /// `t.Decl?.Template` -- or null when the expression has no `?.` in it.
    /// </summary>
    private static Expr? ConditionalRoot(Expr e)
        => e switch
        {
            MemberExpr { NullConditional: true } m => m.Target,
            MemberExpr m => ConditionalRoot(m.Target),
            CallExpr c => ConditionalRoot(c.Target),
            IndexExpr i => ConditionalRoot(i.Target),
            _ => null,
        };

    private bool Tests(Expr cond, out Sym? about, out bool whenTrue)
    {
        about = null;
        whenTrue = true;

        switch (cond)
        {
            case BinaryExpr { Op: BinOp.Eq or BinOp.Ne } b:
            {
                Expr? side = b.Right is LiteralExpr { Kind: Lit.Null } ? b.Left
                           : b.Left is LiteralExpr { Kind: Lit.Null } ? b.Right
                           : null;

                if (side is null)
                {
                    return false;
                }

                // AN ASSIGNMENT TESTED PROVES WHAT IT WROTE. `while ((r =
                // Next()) != null) r.Kind` is the loop C# writes to drain
                // anything, and the value tested is the one now in r: C#
                // narrows r on the path the test holds, and so does this.
                if (side is AssignExpr { Op: null, Target: NameExpr written }
                    && _r.Resolved.TryGetValue(written, out Sym? assigned)
                    && assigned is LocalSym or ParamSym or FieldSym or CapturedFieldSym)
                {
                    about = assigned;
                    whenTrue = b.Op == BinOp.Ne;
                    return true;
                }

                // A NULL-CONDITIONAL CHAIN PROVES ITS RECEIVER. `t.Decl?.Template
                // is null` being FALSE means t.Decl was not null -- the answer
                // could not be anything else -- and C# reads it that way. The
                // chain's root is what was tested; nothing is proved on the
                // other branch, where the member itself may have been null.
                if (ConditionalRoot(side) is { } reached
                    && _r.Resolved.TryGetValue(reached, out Sym? receiver)
                    && receiver is LocalSym or ParamSym or FieldSym or CapturedFieldSym)
                {
                    about = receiver;
                    whenTrue = b.Op == BinOp.Ne;
                    return true;
                }

                if (!_r.Resolved.TryGetValue(side, out Sym? sym))
                {
                    return false;
                }

                about = sym;
                whenTrue = b.Op == BinOp.Ne;
                return sym is LocalSym or ParamSym or FieldSym or CapturedFieldSym;
            }

            case IsExpr isx when _r.Resolved.TryGetValue(isx.Operand, out Sym? sym)
                                  && sym is LocalSym or ParamSym or FieldSym or CapturedFieldSym:
                about = sym;
                whenTrue = true;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Applies what a condition proved, and answers what to undo afterwards.
    ///
    /// Returns the symbols this call added, so a branch can put the state back
    /// exactly as it found it. Tracking what WE changed rather than copying the
    /// whole set means a nested test cannot lose an outer one's proof.
    /// </summary>
    private List<Sym> Assume(Expr cond, bool holds)
    {
        List<Sym> added = new();

        // `!x` PROVES WHAT x DISPROVES. Without this, the false branch of an
        // `if (!found)` knew nothing, and neither did the right of `!a || b`.
        if (cond is UnaryExpr { Op: UnOp.Not } negated)
        {
            return Assume(negated.Operand, !holds);
        }

        // `a && b` proves everything a proves AND everything b proves, when
        // the whole is true -- which is what makes `if (x != null && x.Y)`
        // work, and that shape is everywhere in real code.
        if (cond is BinaryExpr { Op: BinOp.AndAlso } and && holds)
        {
            added.AddRange(Assume(and.Left, true));
            added.AddRange(Assume(and.Right, true));
            return added;
        }

        // And `a || b` false means both were false.
        if (cond is BinaryExpr { Op: BinOp.OrElse } or && !holds)
        {
            added.AddRange(Assume(or.Left, false));
            added.AddRange(Assume(or.Right, false));
            return added;
        }

        // For `a || b` on the TRUE path, only facts established by every
        // successful alternative survive. This is especially important for
        // `TryGetValue(..., out x) || OtherTry(..., out x)`: whichever call
        // succeeded, x is populated.
        HashSet<string> successfulOuts = OutPathsWhen(cond, holds);
        if (successfulOuts.Count > 0)
        {
            foreach (string path in successfulOuts)
            {
                if (_notNullPaths.Add(path))
                {
                    added.Add(new PathSym(path));
                }

                if (Lookup(path) is Sym local && _notNull.Add(local))
                {
                    added.Add(local);
                }
            }
        }

        if (Tests(cond, out Sym? about, out bool whenTrue) && about is not null && whenTrue == holds
            && _notNull.Add(about))
        {
            added.Add(about);
        }

        // A CALL THAT ANSWERS TRUE AND FILLS IN AN `out`.
        //
        // `if (!map.TryGetValue(k, out V? found) || found.X is null)` reads
        // `found` on the path where the call said it had one, and .NET says so
        // with [NotNullWhen(true)] on the parameter. Attributes are read and
        // dropped by this parser, so the rule is applied to the SHAPE instead:
        // a method that answers a bool and writes an out parameter has told the
        // caller something about it, and the true path is where it did.
        //
        // The day attributes are carried this becomes exactly the attribute's
        // rule rather than an approximation of it.
        if (holds && cond is CallExpr call && _r.Calls.TryGetValue(call, out MethodSymbol? called)
            && called.Returns.Prim == Prim.Bool)
        {
            foreach (Expr a in call.Args)
            {
                if (a is RefArgExpr { IsOut: true } filled && Path(filled.Target) is string got
                    && _notNullPaths.Add(got))
                {
                    added.Add(new PathSym(got));

                    if (filled.Target is NameExpr name && Lookup(name.Name) is Sym local
                        && _notNull.Add(local))
                    {
                        added.Add(local);
                    }
                }
            }
        }

        // WHAT THE ANSWER PROVES, where the method said so.
        //
        // `[NotNullWhen(false)] string? value` on string.IsNullOrEmpty means a
        // false answer proves the argument was not null, which is the whole
        // reason `if (!string.IsNullOrEmpty(dir)) { Use(dir); }` is how the
        // check is written. The shape-based rule above covers the `out` case;
        // this is the attribute itself, for every parameter and either answer.
        if (cond is CallExpr told && _r.Calls.TryGetValue(told, out MethodSymbol? says)
            && says.Decl is { } signature && says.Returns.Prim == Prim.Bool)
        {
            for (int i = 0; i < told.Args.Count && i < signature.Params.Count; i++)
            {
                if (signature.Params[i].NotNullWhen != holds)
                {
                    continue;
                }

                Expr given = told.Args[i] is RefArgExpr passed ? passed.Target : told.Args[i];

                if (Path(given) is string proved && _notNullPaths.Add(proved))
                {
                    added.Add(new PathSym(proved));
                }

                if (_r.Resolved.TryGetValue(given, out Sym? held)
                    && held is LocalSym or ParamSym or FieldSym or CapturedFieldSym
                    && _notNull.Add(held))
                {
                    added.Add(held);
                }
            }
        }

        // AND THE SAME FOR A PATH. `d.Init != null` proves `d.Init`, which is
        // not a symbol and is what the code after the test reads.
        if (cond is BinaryExpr { Op: BinOp.Eq or BinOp.Ne } test)
        {
            Expr? side = test.Right is LiteralExpr { Kind: Lit.Null } ? test.Left
                       : test.Left is LiteralExpr { Kind: Lit.Null } ? test.Right
                       : null;

            if (side is MemberExpr && Path(side) is string spelt
                && (test.Op == BinOp.Ne) == holds && _notNullPaths.Add(spelt))
            {
                _proved.Add(spelt);
                added.Add(new PathSym(spelt));
            }
        }
        return added;
    }

    private HashSet<string> OutPathsWhen(Expr expression, bool holds)
    {
        (HashSet<string> whenTrue, HashSet<string> whenFalse) = OutPaths(expression);
        return holds ? whenTrue : whenFalse;
    }

    /// <summary>
    /// The `out` paths a condition proves assigned on each of its outcomes,
    /// BOTH outcomes from ONE walk.
    ///
    /// Asking for one outcome at a time recursed into the left of every
    /// `&amp;&amp;` and `||` twice -- once for true, once for false -- and a
    /// condition written `a &amp;&amp; b &amp;&amp; c &amp;&amp; ...` nests to
    /// the LEFT, so the left operand is the deep one. That doubled at every
    /// level: two to the power of the chain's length. A two-hundred-line
    /// kernel source with long guard chains allocated thirty-four gigabytes
    /// binding, more than every other kernel source together, and ran alone
    /// for the last third of a parallel build because nothing else was left.
    ///
    /// The sets returned are fresh; nothing handed back is shared with a
    /// child's result, so a caller may take what it is given and change it.
    /// </summary>
    private (HashSet<string> WhenTrue, HashSet<string> WhenFalse) OutPaths(Expr expression)
    {
        HashSet<string> Empty() => new(StringComparer.Ordinal);

        if (expression is CallExpr call && _r.Calls.TryGetValue(call, out MethodSymbol? method)
            && method.Returns.Prim == Prim.Bool)
        {
            HashSet<string> proved = Empty();
            foreach (Expr argument in call.Args)
            {
                if (argument is RefArgExpr { IsOut: true } output
                    && Path(output.Target) is string path)
                {
                    proved.Add(path);
                }
            }
            return (proved, Empty());
        }

        if (expression is UnaryExpr { Op: UnOp.Not } negated)
        {
            (HashSet<string> whenTrue, HashSet<string> whenFalse) = OutPaths(negated.Operand);
            return (whenFalse, whenTrue);
        }

        if (expression is BinaryExpr { Op: BinOp.AndAlso } andExpr)
        {
            (HashSet<string> leftTrue, HashSet<string> leftFalse) = OutPaths(andExpr.Left);
            (HashSet<string> rightTrue, HashSet<string> rightFalse) = OutPaths(andExpr.Right);

            // True is left-true and right-true.
            HashSet<string> whenTrue = new(leftTrue, StringComparer.Ordinal);
            whenTrue.UnionWith(rightTrue);

            // False is either left-false, or left-true/right-false.
            HashSet<string> viaRight = new(leftTrue, StringComparer.Ordinal);
            viaRight.UnionWith(rightFalse);
            HashSet<string> whenFalse = new(leftFalse, StringComparer.Ordinal);
            whenFalse.IntersectWith(viaRight);
            return (whenTrue, whenFalse);
        }

        if (expression is BinaryExpr { Op: BinOp.OrElse } orExpr)
        {
            (HashSet<string> leftTrue, HashSet<string> leftFalse) = OutPaths(orExpr.Left);
            (HashSet<string> rightTrue, HashSet<string> rightFalse) = OutPaths(orExpr.Right);

            // False is left-false and right-false.
            HashSet<string> whenFalse = new(leftFalse, StringComparer.Ordinal);
            whenFalse.UnionWith(rightFalse);

            // True is either left-true, or left-false/right-true.
            HashSet<string> viaRight = new(leftFalse, StringComparer.Ordinal);
            viaRight.UnionWith(rightTrue);
            HashSet<string> whenTrue = new(leftTrue, StringComparer.Ordinal);
            whenTrue.IntersectWith(viaRight);
            return (whenTrue, whenFalse);
        }

        return (Empty(), Empty());
    }

    private void Forget(List<Sym> added)
    {
        foreach (Sym s in added)
        {
            if (s is PathSym path)
            {
                _notNullPaths.Remove(path.Spelt);
                continue;
            }

            _notNull.Remove(s);
        }
    }

    /// Paths proved in this method, for the diagnostics to ignore.
    private readonly List<string> _proved = new();

    /// <summary>
    /// Whether a statement always leaves -- returns, throws, breaks, continues.
    ///
    /// This is what makes the guard-clause shape work:
    ///
    ///     if (x == null) { return; }
    ///     x.Y                              // and here x cannot be null
    ///
    /// which is how most real code tests a nullable, because the alternative is
    /// indenting the entire body of every method by one.
    /// </summary>
    private static bool Leaves(Stmt s) => s switch
    {
        ReturnStmt or ThrowStmt or BreakStmt or ContinueStmt => true,
        Block b => b.Statements.Count > 0 && Leaves(b.Statements[^1]),
        IfStmt i => i.Else != null && Leaves(i.Then) && Leaves(i.Else),
        _ => false,
    };

    /// <summary>
    /// Whether a statement ends in a call to a method declared
    /// `[DoesNotReturn]` (System.Diagnostics.CodeAnalysis): the null state
    /// after it is never reached. Asked after the statement is checked, so the
    /// call has been resolved.
    /// </summary>
    private bool NeverReturns(Stmt s) => s switch
    {
        ExprStmt { Expr: Expr e } => Called(e) is { Decl: not null } m
            && m.Decl.Attributes.Any(a => a.Target.Length == 0 && a.Name is "DoesNotReturn" or "DoesNotReturnAttribute"),
        Block b => b.Statements.Count > 0 && (Leaves(b.Statements[^1]) || NeverReturns(b.Statements[^1])),
        IfStmt i => i.Else != null && (Leaves(i.Then) || NeverReturns(i.Then)) && (Leaves(i.Else) || NeverReturns(i.Else)),
        _ => false,
    };

    /// <summary>
    /// The method a call expression resolved to, through any rewrite the
    /// binder made of it (a bare `F()` in a static method is `T.F()`).
    /// </summary>
    private MethodSymbol? Called(Expr e)
    {
        for (int hops = 0; hops < 8 && _r.Rewrites.TryGetValue(e, out Expr? to); hops++) e = to;
        return e is CallExpr call && _r.Calls.TryGetValue(call, out MethodSymbol? m) ? m : null;
    }

    /// <summary>
    /// Whether a completed case reaches the statement after its switch.
    /// `break` does; return, throw and continue do not. This differs from the
    /// general Leaves predicate because break leaves the case precisely by
    /// taking the path whose definite assignments the switch must merge.
    /// </summary>
    private static bool ReachesAfterSwitch(Stmt s) => s switch
    {
        BreakStmt => true,
        ReturnStmt or ThrowStmt or ContinueStmt => false,
        Block b => b.Statements.Count == 0 || ReachesAfterSwitch(b.Statements[^1]!),
        IfStmt i when i.Else is not null
            => ReachesAfterSwitch(i.Then) || ReachesAfterSwitch(i.Else),
        _ => true,
    };

    /// Checking the body of a static method, where there is no `this`. A
    /// lambda's body is checked inside its closure class, whose fields are
    /// the captures and are read from the closure object, so that is never
    /// a static context whatever the method around it was.
    private bool InStaticContext =>
        _method is { Static: true }
        && !(_thisType?.Name.StartsWith("Lambda$", StringComparison.Ordinal) ?? false);

    private Type CheckName(NameExpr n)
    {
        // A static method group retained as the target of a compiler-generated
        // delegate wrapper keeps meaning the same methods inside the wrapper's
        // synthetic closure. There is no instance receiver to rebind and the
        // original source node already has the complete overload set.
        if (_r.Resolved.TryGetValue(n, out Sym? prior)
            && prior is MethodGroupSym priorMethods
            && priorMethods.Methods.All(m => m.Static))
        {
            return Type.Void;
        }

        // `global::Name`: a type, whatever a local or a member of the
        // enclosing types is called. Not a type, it goes on as any name does
        // -- the first part of a namespace-qualified one.
        if (n.Global
            && ((FindType(n.Name, out TypeSymbol? globalType) && globalType is not null)
                || (Alias(n.Name) is string globalAlias && _r.Types.TryGetValue(globalAlias, out globalType))))
        {
            _r.Resolved[n] = new TypeNameSym(globalType);
            return new Type { Prim = Prim.Void, Symbol = globalType };
        }

        Sym? sym = Lookup(n.Name);

        if (sym != null)
        {
            _r.Resolved[n] = sym;

            if (sym is LocalSym local && !_assigned.Contains(local))
            {
                Error(n, $"'{n.Name}' is used before it is definitely assigned");
            }

            // A CONST STRING READS AS THE TEXT IT NAMES, which is the same
            // rewrite a const field of a class gets.
            if (sym is ConstSym { Text: not null } spelt)
            {
                _r.Rewrites[n] = new LiteralExpr
                {
                    Kind = Lit.Str, Text = spelt.Text, Line = n.Line, Col = n.Col, File = n.File,
                };
                return spelt.Type;
            }

            Type found = sym switch
            {
                LocalSym l => l.Type,
                ParamSym p => p.Type,
                FieldSym f => f.Field.Type,
                CapturedFieldSym f => f.Field.Type,

                // A LOCAL `const` IS A NAME FOR A VALUE. It reads as that
                // value wherever one is wanted, which is what lets it stand in
                // a constant pattern.
                ConstSym k => k.Type,
                // A generic local function's name: its method group, called
                // as any method of the type is.
                MethodGroupSym => Type.Void,
                _ => Type.Error,
            };

            // PROVED NON-NULL HERE reads as the non-nullable type, which is
            // what makes every use of it after the test legal without ceremony.
            // The DECLARATION is untouched: this is what the value is known to
            // be at this point, not what it was declared to be.
            // NOT for a Nullable<T>. Proving a `string?` is not null makes it a
            // `string`, because the two are the same thing at run time and only
            // differ in what the checker will let you do. Proving an `int?` is
            // not null does NOT make it an `int`: it is still a cell, and it
            // still has to be opened with .Value -- which is exactly what C#
            // requires, and what makes the reference case feel different.
            return _notNull.Contains(sym) && !found.IsNullableValue
                 ? found.AsNonNullable()
                 : found;
        }

        // A field or method of the enclosing type, reached without 'this'.
        if (_thisType != null)
        {
            // Inside class Binder, `Binder.Fits` names the TYPE, not its
            // constructor. Constructors share the class name in the method
            // table, so this must be settled before ordinary method lookup.
            if (n.Name == _thisType.Name)
            {
                _r.Resolved[n] = new TypeNameSym(_thisType);
                return new Type { Prim = Prim.Void, Symbol = _thisType };
            }

            if (FindConstant(_thisType, n.Name) is (long value, Type ctype))
            {
                _r.Resolved[n] = new ConstSym(value, ctype);
                return ctype;
            }

            if (FindText(_thisType, n.Name) is string spelt)
            {
                _r.Rewrites[n] = new LiteralExpr
                {
                    Kind = Lit.Str, Text = spelt, Line = n.Line, Col = n.Col,
                };
                return Type.String;
            }

            FieldSymbol? f = _thisType.FindField(n.Name) ?? _thisType.FindField("<" + n.Name + ">");

            // NO INSTANCE IN A STATIC METHOD (C#'s CS0120). A static method
            // naming one of its class's instance fields has no object to read
            // it from. Letting it bind produced a field read with no receiver,
            // which reached the optimiser as a value nobody defined and
            // crashed it (ConstantAndCopyPropagation, a null register) with no
            // file or line -- two kernel files hit it on 2026-09-23.
            if (f is { Static: false } && InStaticContext)
            {
                Error(n, $"An object reference is required for the non-static field, method, or property '{_thisType.Name}.{n.Name}'");
                return Type.Error;
            }

            if (f != null)
            {
                FieldSym read = new(f);

                _r.Resolved[n] = read;

                // A lambda nested in a generated closure may read one of that
                // closure's capture fields. Carry that value, or the same
                // boxed cell, into the inner closure. Source-class fields are
                // still reached through captured `this` and are not copied.
                if (_captured is not null
                    && _thisType.Name.StartsWith("Lambda$", StringComparison.Ordinal))
                {
                    _captured[n.Name] = f.Type;
                }

                // A FIELD IS NOT TRACKED, with one exception: a lambda's
                // CAPTURE, which is a field of the closure class and was proved
                // before the lambda was written. Nothing else ever puts a field
                // in the set, so asking is safe -- see CheckLambda.
                return (_notNull.Contains(read) || _notNullPaths.Contains(n.Name))
                       && !f.Type.IsNullableValue
                     ? f.Type.AsNonNullable()
                     : f.Type;
            }

            List<MethodSymbol> methods = _thisType.FindMethods(n.Name);

            // From a static method only the static overloads are callable
            // without a receiver; a group with none is CS0120.
            if (methods.Count > 0 && InStaticContext)
            {
                List<MethodSymbol> statics = methods.Where(m => m.Static).ToList();
                if (statics.Count == 0)
                {
                    Error(n, $"An object reference is required for the non-static field, method, or property '{_thisType.Name}.{n.Name}'");
                    return Type.Error;
                }
                methods = statics;
            }

            if (methods.Count > 0)
            {
                _r.Resolved[n] = new MethodGroupSym(methods);
                return Type.Void;
            }

            MethodSymbol? getter = _thisType.FindMethods("get_" + n.Name).FirstOrDefault();

            if (getter is { Static: false } && InStaticContext)
            {
                Error(n, $"An object reference is required for the non-static field, method, or property '{_thisType.Name}.{n.Name}'");
                return Type.Error;
            }

            if (getter != null)
            {
                _r.Resolved[n] = new PropertyGetSym(getter);
                return getter.Returns;
            }
        }

        // A closure object becomes `_thisType` while its Invoke body is
        // checked, but unqualified static names continue to bind in the class
        // where the lambda was written.  They are not captures: a const is
        // folded at compile time and a static member already has one address
        // independent of any closure instance.
        if (_lexicalType is not null && _lexicalType != _thisType)
        {
            if (n.Name == _lexicalType.Name)
            {
                _r.Resolved[n] = new TypeNameSym(_lexicalType);
                return new Type { Prim = Prim.Void, Symbol = _lexicalType };
            }

            if (FindConstant(_lexicalType, n.Name) is (long value, Type ctype))
            {
                _r.Resolved[n] = new ConstSym(value, ctype);
                return ctype;
            }

            if (FindText(_lexicalType, n.Name) is string spelt)
            {
                _r.Rewrites[n] = new LiteralExpr
                {
                    Kind = Lit.Str, Text = spelt, Line = n.Line, Col = n.Col,
                };
                return Type.String;
            }

            FieldSymbol? staticField = _lexicalType.FindField(n.Name)
                                      ?? _lexicalType.FindField("<" + n.Name + ">");
            if (staticField is { Static: true })
            {
                _r.Resolved[n] = new FieldSym(staticField);
                return staticField.Type;
            }

            // EVERY METHOD OF THE NAME, and not only the static ones, when
            // there is a receiver to reach the rest through.
            //
            // C# resolves a bare name against every member that has it and
            // then picks by the arguments. Offering only the static ones meant
            // a lambda inside an instance method could not call
            // `Fits(a, b, c)` while a static `Fits(a, b)` existed -- three
            // lines of this compiler's own checker, each reported as an
            // overload taking three arguments that nothing takes. The captured
            // receiver is ignored for a static method by the code generator,
            // which is what makes one group able to hold both.
            List<MethodSymbol> reachable = _lexicalType.FindMethods(n.Name);
            List<MethodSymbol> onlyStatic = reachable.Where(m => m.Static).ToList();

            // ONLY WHERE THE NAME IS BOTH. A name that is all static or all
            // instance already resolves as it should; it is the MIXED one that
            // could not be seen whole, and answering it whole is the only
            // change.
            bool through = onlyStatic.Count > 0 && onlyStatic.Count < reachable.Count
                        && _capturedThisType is not null && _capturedThisField is not null
                        && ReferenceEquals(_lexicalType, _capturedThisType);

            List<MethodSymbol> staticMethods = through ? reachable : onlyStatic;
            if (staticMethods.Count > 0)
            {
                _r.Resolved[n] = through
                               ? new CapturedMethodGroupSym(_capturedThisField!, staticMethods)
                               : new MethodGroupSym(staticMethods);
                return Type.Void;
            }

            MethodSymbol? staticGetter = _lexicalType.FindMethods("get_" + n.Name)
                                                     .FirstOrDefault(m => m.Static);
            if (staticGetter is not null)
            {
                _r.Resolved[n] = new PropertyGetSym(staticGetter);
                return staticGetter.Returns;
            }
        }

        if (_capturedThisType is not null && n.Name == _capturedThisType.Name)
        {
            _r.Resolved[n] = new TypeNameSym(_capturedThisType);
            return new Type { Prim = Prim.Void, Symbol = _capturedThisType };
        }

        // An instance local function is lowered to a closure but retains the
        // enclosing method's `this`. Reads, writes, properties and method calls
        // therefore go through the hidden reference captured in that closure.
        if (_capturedThisType is not null && _capturedThisField is not null)
        {
            FieldSymbol? outerField = _capturedThisType.FindField(n.Name)
                                   ?? _capturedThisType.FindField("<" + n.Name + ">");
            if (outerField is not null)
            {
                _r.Resolved[n] = new CapturedFieldSym(_capturedThisField, outerField);
                return outerField.Type;
            }

            List<MethodSymbol> outerMethods = _capturedThisType.FindMethods(n.Name);
            if (outerMethods.Count > 0)
            {
                _r.Resolved[n] = new CapturedMethodGroupSym(_capturedThisField, outerMethods);
                return Type.Void;
            }

            MethodSymbol? outerGetter = _capturedThisType.FindMethods("get_" + n.Name).FirstOrDefault();
            if (outerGetter is not null)
            {
                _r.Resolved[n] = new CapturedPropertyGetSym(_capturedThisField, outerGetter);
                return outerGetter.Returns;
            }
        }

        // AN ENCLOSING TYPE'S MEMBERS, named without qualification.
        //
        // In C# a nested type sees everything its outer types declare,
        // private included, and an unqualified name resolves outwards through
        // the chain of enclosing types. Only STATIC members are reachable
        // that way -- a nested type holds no reference to an instance of its
        // outer -- so consts, static fields, static methods, static
        // properties and nested types (the last already handled by FindType
        // below) are what the walk offers.
        for (string? outerKey = Enclosing((_thisType ?? _lexicalType ?? _scope)?.Key ?? "");
             outerKey is not null;
             outerKey = Enclosing(outerKey))
        {
            if (!_r.Types.TryGetValue(outerKey, out TypeSymbol? outer))
            {
                continue;
            }

            if (outer == _thisType || outer == _lexicalType)
            {
                continue;
            }

            if (FindConstant(outer, n.Name) is (long outerValue, Type outerConstType))
            {
                _r.Resolved[n] = new ConstSym(outerValue, outerConstType);
                return outerConstType;
            }

            if (FindText(outer, n.Name) is string outerText)
            {
                _r.Rewrites[n] = new LiteralExpr
                {
                    Kind = Lit.Str, Text = outerText, Line = n.Line, Col = n.Col,
                };
                return Type.String;
            }

            FieldSymbol? outerStatic = outer.FindField(n.Name)
                                    ?? outer.FindField("<" + n.Name + ">");
            if (outerStatic is { Static: true })
            {
                _r.Resolved[n] = new FieldSym(outerStatic);
                return outerStatic.Type;
            }

            List<MethodSymbol> outerStatics = outer.FindMethods(n.Name)
                                                   .Where(m => m.Static).ToList();
            if (outerStatics.Count > 0)
            {
                _r.Resolved[n] = new MethodGroupSym(outerStatics);
                return Type.Void;
            }

            MethodSymbol? outerStaticGetter = outer.FindMethods("get_" + n.Name)
                                                   .FirstOrDefault(m => m.Static);
            if (outerStaticGetter is not null)
            {
                _r.Resolved[n] = new PropertyGetSym(outerStaticGetter);
                return outerStaticGetter.Returns;
            }
        }

        // object's STATIC members, called unqualified. In C# they reach a
        // class through the root it derives from; there is no root here, so
        // they are looked up in the symbol that stands for one.
        if (Rooted().FindMethods(n.Name) is { Count: > 0 } rooted && rooted.Any(m => m.Static))
        {
            _r.Resolved[n] = new MethodGroupSym(rooted.Where(m => m.Static).ToList());
            return Type.Void;
        }

        // A TYPE NAME, either written out or spelled with its keyword.
        //
        // `string.Join` and `String.Join` are the same call in C#, because the
        // keyword is an ALIAS for the type and not a separate thing -- and the
        // keyword is what everybody actually writes. The first line of this
        // compiler's own Ast.cs to need it is `string.Join(", ", Args)`.
        if (n.Name == "object")
        {
            TypeSymbol root = Rooted();
            _r.Resolved[n] = new TypeNameSym(root);
            return new Type { Prim = Prim.Void, Symbol = root };
        }
        if ((FindType(n.Name, out TypeSymbol? type) && type is not null)
            || (Alias(n.Name) is string full && _r.Types.TryGetValue(full, out type)))
        {
            _r.Resolved[n] = new TypeNameSym(type);
            return new Type { Prim = Prim.Void, Symbol = type };
        }

        Error(n, $"'{n.Name}' is not declared");
        return Type.Error;
    }

    /// <summary>
    /// The type a C# keyword is an alias for, or null when it is not one.
    ///
    /// Every one of these is a real name in C#, so they are all listed even
    /// though only a few have a class behind them here yet -- a name with no
    /// class simply fails to resolve, exactly as it would have without this.
    /// </summary>
    /// <summary>
    /// MinValue or MaxValue for a numeric keyword, or null for anything else.
    ///
    /// ulong.MaxValue is every bit set, which as a signed word is -1. That is
    /// the same sixty-four bits either way, and the type recorded alongside it
    /// is what decides how they are read.
    /// </summary>
    private static long? Limit(string keyword, string member) => (keyword, member) switch
    {
        ("sbyte",  "MinValue") => sbyte.MinValue,
        ("sbyte",  "MaxValue") => sbyte.MaxValue,
        ("byte",   "MinValue") => byte.MinValue,
        ("byte",   "MaxValue") => byte.MaxValue,
        ("short",  "MinValue") => short.MinValue,
        ("short",  "MaxValue") => short.MaxValue,
        ("ushort", "MinValue") => ushort.MinValue,
        ("ushort", "MaxValue") => ushort.MaxValue,
        ("int",    "MinValue") => int.MinValue,
        ("int",    "MaxValue") => int.MaxValue,
        ("uint",   "MinValue") => uint.MinValue,
        ("uint",   "MaxValue") => uint.MaxValue,
        ("long",   "MinValue") => long.MinValue,
        ("long",   "MaxValue") => long.MaxValue,
        ("ulong",  "MinValue") => 0L,
        ("ulong",  "MaxValue") => -1L,
        ("nint",   "MinValue") => Target.Current.WordSize == 4 ? int.MinValue : long.MinValue,
        ("nint",   "MaxValue") => Target.Current.WordSize == 4 ? int.MaxValue : long.MaxValue,
        ("nuint",  "MinValue") => 0L,
        ("nuint",  "MaxValue") => Target.Current.WordSize == 4 ? uint.MaxValue : -1L,
        ("char",   "MinValue") => 0L,
        ("char",   "MaxValue") => char.MaxValue,
        _ => null,
    };

    private static string? Alias(string keyword) => keyword switch
    {
        "bool"   => "Boolean",
        "sbyte"  => "SByte",
        "byte"   => "Byte",
        "short"  => "Int16",
        "ushort" => "UInt16",
        "int"    => "Int32",
        "uint"   => "UInt32",
        "long"   => "Int64",
        "ulong"  => "UInt64",
        "nint"   => "IntPtr",
        "nuint"  => "UIntPtr",
        "float"  => "Single",
        "double" => "Double",
        "char"   => "Char",
        "string" => "String",
        "object" => "Object",
        _        => null,
    };

    /// <summary>
    /// Whether `Type.name` names something: a static field or method, a static
    /// property, an enum member, or a type nested inside this one.
    /// </summary>
    private bool HasStaticMember(TypeSymbol type, string name)
    {
        return type.EnumValues.ContainsKey(name)
            || type.FindField(name) is { Static: true }
            || type.FindMethods(name).Any(method => method.Static)
            || type.FindMethods("get_" + name).Any(method => method.Static)
            || type.FindMethods("set_" + name).Any(method => method.Static)
            || _r.Types.ContainsKey(type.Key + "." + name);
    }

    /// <summary>
    /// A member access, `X.Y`.
    ///
    /// A SIMPLE NAME IN THE RECEIVER POSITION MAY BE BOTH A TYPE AND A VALUE,
    /// and C# decides in favour of the type whenever the value reading cannot
    /// produce the member. A method group is a value only where it is invoked
    /// or converted to a delegate, so in `Stat.Room()` the receiver names the
    /// class even inside a class that declares a method called `Stat` -- the
    /// argument list belongs to `Room`. The colour-colour rule is the same
    /// rule: `Colour Colour` is legal, and `Colour.Red` means the type's static
    /// member when the field's own value has no `Red`.
    ///
    /// So the value reading goes first and QUIETLY, because it is the commoner
    /// one and because the reading that loses must not report anything on the
    /// way. Only when it fails outright does the type get its turn; when it
    /// wins it is simply checked again aloud, which is what keeps its own
    /// diagnostics -- an unassigned local, a misspelt member -- reported
    /// exactly once and by the reading that was actually taken.
    /// </summary>
    private Type CheckMember(MemberExpr m)
    {
        if (m.Target is not NameExpr ambiguous || ambiguous.TypeArgs.Count > 0
            || !FindType(ambiguous.Name, out TypeSymbol? alsoAType) || alsoAType is null
            || !HasStaticMember(alsoAType, m.Name))
        {
            return CheckMemberCore(m, null);
        }

        _quiet++;
        Type asValue = CheckMemberCore(m, null);
        _quiet--;

        if (!asValue.IsError)
        {
            return CheckMemberCore(m, null);
        }

        // Nothing the losing reading wrote down may survive it: a rewrite left
        // on the target would be substituted for a receiver that is now a type.
        _r.Resolved.Remove(m);
        _r.Rewrites.Remove(m);
        _r.Rewrites.Remove(m.Target);
        _r.Receivers.Remove(m);

        return CheckMemberCore(m, alsoAType);
    }

    private Type CheckMemberCore(MemberExpr m, TypeSymbol? asType)
    {
        bool ConditionalChain(Expr expression) => expression is MemberExpr member
            && (member.NullConditional || ConditionalChain(member.Target));

        // A NAME THAT IS A MEMBER OF THE TYPE BEING COMPILED IS A VALUE, and
        // `value.Name` binds a MEMBER of that value. Locals and type names
        // were asked about and fields, properties, methods and constants were
        // not, so any capitalised one of those looked like a NAMESPACE: with a
        // `static class Files` anywhere in the program, `Shared.Files` --
        // Shared being a static field -- read as the namespace-qualified type
        // `Files`, and the property returning it was told it could not convert
        // 'Files' to 'UserFile?[]'. C#'s rule is the other way round: a simple
        // name takes the innermost declaration that matches, and a type only
        // when nothing else does.
        bool NamesAValue(string name)
            => Lookup(name) is not null
            || (_thisType is { } owner
                && (owner.FindField(name) is not null
                    || owner.FindField("<" + name + ">") is not null
                    || owner.FindMethods(name).Count > 0
                    || owner.FindMethods("get_" + name).Count > 0))
            || FindConstant(_thisType, name) is not null
            || FindText(_thisType, name) is not null;

        bool NamespaceOnly(Expr expression) => expression switch
        {
            // Namespace identifiers in the compiler's C# sources follow the
            // standard capitalised convention. Requiring that also prevents an
            // as-yet-unmaterialised captured local (`a.Args` inside a lambda)
            // from being mistaken for the namespace-qualified type `Args`.
            NameExpr n => n.Name.Length > 0 && char.IsUpper(n.Name[0])
                       && !NamesAValue(n.Name) && !IsTypeName(n.Name),
            // A SEGMENT MAY SHARE A NAME WITH A TYPE and still be a namespace.
            // `System.Runtime.InteropServices.CollectionsMarshal` walks through
            // Runtime, which is also this machine's runtime class; what settles
            // it is that `System.Runtime` names no type, while
            // `Assembler.Section` does and is therefore not a namespace.
            MemberExpr n => NamespaceOnly(n.Target)
                         && Lookup(n.Name) is null
                         && (!IsTypeName(n.Name)
                             || (Spelt(n) is string path && !FindType(path, out _))),
            _ => false,
        };


        // THE ENDS OF A NUMERIC TYPE'S RANGE. `long.MinValue`, `int.MaxValue`.
        //
        // Answered before the target is checked, because the target is a
        // KEYWORD -- there is no class called `long` for it to resolve to, and
        // asking would report that 'long' is not declared.
        //
        // Constants, not fields: the value is a property of the type and the
        // compiler is what knows it. This is what makes `x == long.MinValue`
        // readable instead of a magic number, and the compiler's own constant
        // folder guards division with exactly that comparison.
        if (m.Target is NameExpr keyword && !IsTypeName(keyword.Name)
            && Limit(keyword.Name, m.Name) is long edge)
        {
            Type of = ResolveCore(new TypeRef { Name = keyword.Name, Line = m.Line, Col = m.Col }, _thisType);

            _r.Resolved[m] = new ConstSym(edge, of);
            return of;
        }

        // A TYPE NAMED THROUGH ITS NAMESPACE: `Corsac.Size.D`.
        //
        // There is one flat table of types here, so a namespace has nothing to
        // select between -- but source says what it means and has to compile.
        // The rule is the same one a qualified name in a TYPE position already
        // follows: the last part names the type. Only when the qualifier is not
        // a declared thing at all, so a real member access is never shadowed by
        // a type that happens to share its name.
        if (NamespaceOnly(m.Target))
        {
            // THE WHOLE PATH FIRST, because a namespace is real: `Corsac.Lang.Block`
            // and `Corsac.Lang.Ir.Block` differ in nothing else, and reading
            // only the last part is how one becomes the other.
            if (Spelt(m) is string path && FindType(path, out TypeSymbol? byPath) && byPath is not null)
            {
                _r.Resolved[m] = new TypeNameSym(byPath);
                return new Type { Prim = Prim.Void, Symbol = byPath };
            }

            // AND THEN THE LAST PART ALONE, for the namespaces this compiler
            // models as nothing at all: `System.Text.StringBuilder` is a
            // StringBuilder declared in the global namespace here.
            if (_r.Types.TryGetValue(m.Name, out TypeSymbol? qualified))
            {
                _r.Resolved[m] = new TypeNameSym(qualified);
                return new Type { Prim = Prim.Void, Symbol = qualified };
            }
        }

        // The type the target names, when the caller has already settled that
        // the target is a TYPE NAME and not a value -- see CheckMember.
        Type target;

        if (asType is not null)
        {
            _r.Resolved[m.Target] = new TypeNameSym(asType);
            target = new Type { Prim = Prim.Void, Symbol = asType };
        }
        else
        {
            target = CheckExpr(m.Target);
        }

        if (target.IsError)
        {
            return Type.Error;
        }

        // THE SAME NAME AS A MEMBER AND AS A TYPE, which C# calls the
        // colour-colour rule: `Colour Colour { get; }` is legal, and
        // `Colour.Red` then has to mean the enum member rather than a member of
        // the property's value.
        //
        // The rule is that whichever reading WORKS wins, and a member of the
        // value is tried first because it is the commoner one. This compiler's
        // own Types.cs has `public Prim Prim { get; init; }` and then compares
        // it against `Prim.Void`, which is the case exactly.
        if (m.Target is NameExpr both && _r.Types.TryGetValue(both.Name, out TypeSymbol? shadowed)
            && shadowed.Kind == TypeKind.Enum && shadowed.EnumValues.ContainsKey(m.Name)
            && (target.Symbol is null || target.Symbol.FindField(m.Name) is null))
        {
            Type asEnum = new() { Prim = shadowed.EnumUnderlying, Symbol = shadowed };

            _r.Resolved[m] = new ConstSym(shadowed.EnumValues[m.Name], asEnum);
            return asEnum;
        }

        // A TYPE REACHED THROUGH THE TYPE IT WAS WRITTEN INSIDE: `Outer.Inner`,
        // `Isa.OpFlags`.
        //
        // The parser hoists a nested type out to sit beside the one it was
        // written inside, keyed by the path -- so the type is under
        // `Outer.Inner` here, while the source says `Outer.Inner`, and in an
        // EXPRESSION that reads as a member access on a type name. Answering it
        // with the type turns the whole access back into a type name, so
        // `Outer.Inner.Deeper` and `Outer.Kind.B` both work by the same rule
        // applied twice.
        if (_r.Resolved.TryGetValue(m.Target, out Sym? qualifier) && qualifier is TypeNameSym holder
            && _r.Types.TryGetValue(holder.Symbol.Key + "." + m.Name, out TypeSymbol? nested))
        {
            _r.Resolved[m] = new TypeNameSym(nested);
            return new Type { Prim = Prim.Void, Symbol = nested };
        }

        // Enum member access: State.Idle.
        if (_r.Resolved.TryGetValue(m.Target, out Sym? s) && s is TypeNameSym { Symbol.Kind: TypeKind.Enum } en)
        {
            if (!en.Symbol.EnumValues.TryGetValue(m.Name, out long value))
            {
                Error(m, $"'{en.Symbol.Name}' has no member '{m.Name}'");
                return Type.Error;
            }

            // AND THE VALUE IS RECORDED, not merely the type.
            //
            // Without this the checker was happy and the code generator had
            // nothing to emit, so every read of an enum member answered "only
            // field reads through a member access are implemented so far" --
            // which reads as a missing feature and was a missing line. Enums
            // could be declared, and one is declared in the language's own test
            // file, but reading a member had never worked.
            //
            // A member is a constant, so it becomes one: the same ConstSym a
            // named const uses, and the same single instruction.
            Type enumType = new() { Prim = en.Symbol.EnumUnderlying, Symbol = en.Symbol };

            _r.Resolved[m] = new ConstSym(value, enumType);
            return enumType;
        }

        // AN ELEMENT OF A TUPLE, BY THE NAME IT WAS GIVEN. `f.At` where f is an
        // `(int At, string Label)` is the first field, and the name is carried
        // by the TYPE rather than by the class -- two tuples of the same shape
        // are one class and may name their elements differently.
        if (target.Symbol is TypeSymbol shaped && shaped.TupleNamings.Count > 0)
        {
            IReadOnlyList<string>? named = target.Names ?? target.Symbol?.TupleNames;
            int which = -1;

            for (int i = 0; named != null && i < named.Count; i++)
            {
                if (named[i] == m.Name)
                {
                    which = i;
                    break;
                }
            }

            // NOTHING WRITTEN HERE SAYS WHICH NAMING, which is what a
            // monomorphised generic hands back: the element came out of one
            // shared copy of List and the names went with the argument. Ask
            // every naming of the shape instead, and answer only where they
            // cannot disagree.
            if (which < 0 && target.Names is null)
            {
                which = shaped.TupleElement(m.Name);
            }

            if (which >= 0 && which < shaped.Fields.Count)
            {
                RequireNonNull(target, m.Target, "read");
                _r.Resolved[m] = new FieldSym(shaped.Fields[which]);
                return shaped.Fields[which].Type;
            }
        }

        // NULLABLE<T> HAS EXACTLY TWO MEMBERS, and neither is declared anywhere:
        // both are the cell itself, read two different ways. Answered before the
        // non-null check below because asking a Nullable<T> whether it has a
        // value is the one thing that is always safe to do to one.
        if (target.IsNullableValue)
        {
            switch (m.Name)
            {
                case "HasValue":
                    return Type.Bool;

                case "Value":
                    return target.Underlying;
            }

            // `x?.Member` REACHES INSIDE THE CELL, which is what the question
            // mark is for: C# reads the member of the VALUE and makes the
            // answer nullable. `s.Definition?.Symbol` is a Symbol? and is how
            // this compiler's own dynamic linker asks a record struct what it
            // points at.
            //
            // Rewritten rather than resolved in place, because the member
            // belongs to the value and the value is inside a cell: everything
            // below here would read the member at an offset from the CELL.
            // `x.HasValue ? x.Value.Member : null` says exactly the same thing
            // with parts that already work, and the subject is hoisted so `x`
            // is evaluated once.
            if (m.NullConditional || ConditionalChain(m.Target))
            {
                SubjectExpr subject = new() { Line = m.Line, Col = m.Col };
                ConditionalExpr instead = new()
                {
                    Cond = new MemberExpr
                    {
                        Target = subject, Name = "HasValue", Guarded = true,
                        Line = m.Line, Col = m.Col,
                    },
                    Then = new MemberExpr
                    {
                        Target = new MemberExpr
                        {
                            Target = subject, Name = "Value", Guarded = true,
                            Line = m.Line, Col = m.Col,
                        },
                        Name = m.Name, Guarded = true, Line = m.Line, Col = m.Col,
                    },
                    Else = new LiteralExpr
                    {
                        Kind = Lit.Null, Text = "null", Line = m.Line, Col = m.Col,
                    },
                    Line = m.Line, Col = m.Col,
                };

                PatternExpr once = new()
                {
                    Subject = m.Target, Test = instead, Line = m.Line, Col = m.Col,
                };

                // THE ARMS HAVE TO AGREE, and one of them is `null`: a member
                // whose type is a value needs the cell said out loud, or the
                // conditional hands back a raw number where the caller reads a
                // Nullable<T> and the first HasValue reads whatever the number
                // points at. Asked quietly first, because asking is the only
                // way to learn what the member's type is.
                _quiet++;

                Type member = CheckExpr(once);

                _quiet--;

                if (!member.IsError && !member.IsReference
                    && RefOf(member.AsNonNullable()) is TypeRef cell)
                {
                    instead.Then = new CastExpr
                    {
                        Type = new TypeRef
                        {
                            Name = cell.Name, Args = cell.Args, ArrayRank = cell.ArrayRank,
                            PointerDepth = cell.PointerDepth, TupleNames = cell.TupleNames,
                            Nullable = true, Line = m.Line, Col = m.Col,
                        },
                        Operand = instead.Then, Line = m.Line, Col = m.Col,
                    };
                }

                _r.Rewrites[m] = once;
                return CheckExpr(once);
            }

            Error(m, $"'{target}' has no member '{m.Name}'; a nullable value type has 'HasValue' and 'Value'");
            return Type.Error;
        }

        // A NULLABLE TUPLE ANSWERS .Value AND .HasValue.
        //
        // C#'s tuple is a struct, so `(Block, int)?` is a Nullable<ValueTuple<…>>
        // and those two members are how it is opened. A tuple is a class here
        // and the question mark is an annotation on a reference, so the two
        // members become what they mean: whether the thing is there, and the
        // thing.
        // Asked of the tuple whether or not the '?' is still on it: proving a
        // nullable reference is not null takes the annotation off here, and
        // C#'s Nullable<T> keeps its two members whatever the flow has proved.
        if (target.Symbol?.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal) == true
            && m.Name is "Value" or "HasValue")
        {
            if (m.Name == "HasValue")
            {
                _r.Rewrites[m] = new BinaryExpr
                {
                    Op = BinOp.Ne, Left = m.Target,
                    Right = new LiteralExpr
                    {
                        Kind = Lit.Null, Text = "null", Line = m.Line, Col = m.Col,
                    },
                    Line = m.Line, Col = m.Col,
                };
                return Type.Bool;
            }

            if (m.Name == "Value")
            {
                _r.Rewrites[m] = new SuppressExpr
                {
                    Operand = m.Target, Line = m.Line, Col = m.Col,
                };
                return target.AsNonNullable();
            }
        }

        bool conditional = m.NullConditional || ConditionalChain(m.Target);

        if (!conditional && !m.Guarded
            && !(Path(m.Target) is string spelt && _notNullPaths.Contains(spelt)))
        {
            RequireNonNull(target, m.Target, "reach through");
        }

        // THE TWO THINGS EVERY OBJECT ANSWERS, on a value whose type is only
        // known to be a machine word -- `object`, or a generic method's own T.
        //
        // Both go through the slot every class shares, so the right override
        // runs without the caller knowing what it is holding. This is the whole
        // reason those slots are reserved: a predicate compiled once cannot
        // know what it was handed.
        if ((target.Prim == Prim.Any || target.ParamName != null) && !target.IsArray
            && Rooted().FindMethods(m.Name) is { Count: > 0 } rooted)
        {
            _r.Resolved[m] = new MethodGroupSym(rooted);
            return Type.Void;
        }

        TypeSymbol? owner = target.Symbol;

        if (owner is null)
        {
            // Length works on arrays and strings alike, because both are a
            // length word followed by a payload.
            if (m.Name == "Length" && (target.IsArray || target.Prim == Prim.String))
            {
                return Type.I32;
            }

            // A TYPE'S NAME, which is the whole of reflection tier 1's surface
            // alongside comparing two of them. Read straight out of the
            // descriptor the code generator put in front of the vtable.
            if (m.Name is "Name" or "FullName" && target.Prim == Prim.Type)
            {
                return Type.String;
            }

            // ITS ASSEMBLY, which is the program's: one image holds every
            // type (System.Reflection.Assembly). The Type is still evaluated,
            // as reading a member of it would be.
            if (m.Name == "Assembly" && target.Prim == Prim.Type)
            {
                CallExpr of = new()
                {
                    Target = new MemberExpr
                    {
                        Target = new MemberExpr
                        {
                            Target = new MemberExpr
                            {
                                Target = new NameExpr { Name = "System", Line = m.Line, Col = m.Col },
                                Name = "Reflection", Line = m.Line, Col = m.Col,
                            },
                            Name = "Assembly", Line = m.Line, Col = m.Col,
                        },
                        Name = "Of", Line = m.Line, Col = m.Col,
                    },
                    Line = m.Line, Col = m.Col,
                };
                of.Args.Add(m.Target);
                _r.Rewrites[m] = of;
                return CheckExpr(of);
            }

            // A STRING'S METHODS ARE CALLED THE WAY C# CALLS THEM.
            //
            // `s.Substring(1, 3)` rather than `String.Substring(s, 1, 3)`. The
            // library's methods are static and take the string first -- which
            // is what they have to be, since a primitive has no vtable to hang
            // an instance method on -- so a member call on a string is looked
            // up in the String class and the receiver becomes the first
            // argument. That is what an extension method is, and it is how C#
            // spells this for every type it does not own.
            //
            // It matters beyond taste: the compiler's own source calls
            // s.Substring, s.StartsWith and s.IndexOf constantly, and every one
            // of them is a place that source could not compile here.
            // Numeric primitives use the same receiver-first library
            // representation as String (for example Int64.CompareTo).
            if (Alias(target.ToString()) is { } primitiveName
                && _r.Types.TryGetValue(primitiveName, out TypeSymbol? str))
            {
                List<MethodSymbol> onString = str.FindMethods(m.Name)
                                                 .Where(x => x.Static && x.Params.Count > 0
                                                          && x.Params[0].Type.Prim == target.Prim)
                                                 .ToList();

                if (onString.Count > 0)
                {
                    _r.Resolved[m] = new MethodGroupSym(onString);
                    _r.Receivers[m] = true;
                    return Type.Void;
                }
            }

            // AN EXTENSION METHOD, which is where LINQ lives. `list.Where(f)`
            // is `Enumerable.Where(list, f)`, and C# says so with `this` on the
            // first parameter -- so that is what is looked for here.
            //
            // Last, after every real member has been tried, because an
            // extension must never shadow one. That is C#'s rule and it is what
            // keeps adding an extension from changing what existing code means.
            if (Extension(target, m.Name) is { Count: > 0 } found)
            {
                _r.Resolved[m] = new MethodGroupSym(found);
                _r.Receivers[m] = true;
                return Type.Void;
            }

            Error(m, $"'{target}' has no member '{m.Name}'");
            return Type.Error;
        }

        if (FindText(owner, m.Name) is string constantText)
        {
            _r.Rewrites[m] = new LiteralExpr
            {
                Kind = Lit.Str, Text = constantText, Line = m.Line, Col = m.Col,
            };
            return Type.String;
        }

        if (FindConstant(owner, m.Name) is (long cvalue, Type ctype2))
        {
            _r.Resolved[m] = new ConstSym(cvalue, ctype2);
            return ctype2;
        }

        // A MEMBER OF A CONSTRUCTED TYPE CARRIES THAT TYPE'S ARGUMENTS.
        // `Holder<string>.Items` is a `List<string>`, not the `List<T>` the
        // class was written with.
        //
        // It only ever shows while the receiver is the TEMPLATE with its
        // arguments beside it rather than the specialisation -- which is
        // exactly what an INFERRED type is before the copy that spells it has
        // been made. `Hold(x)` declared to return `Holder<T>` is a
        // `Holder<string>` at the call site long before any `Holder$string`
        // exists, and reading `.Items` off it straight answered `List<T>`.
        // Through a local it never showed, because a written `Holder<string>`
        // asks for the specialisation and is given it.
        Dictionary<string, Type>? received = Received(target, owner);

        FieldSymbol? field = owner.FindField(m.Name) ?? owner.FindField("<" + m.Name + ">");

        if (field != null)
        {
            FieldSym read = new(field);
            _r.Resolved[m] = read;
            Type fieldType = Close(ContextualFieldResult(target, field), received);
            if (!fieldType.IsNullableValue
                && ((Path(m) is string path && _notNullPaths.Contains(path)) || _notNull.Contains(read)))
            {
                fieldType = fieldType.AsNonNullable();
            }
            return conditional ? fieldType.AsNullable() : fieldType;
        }

        List<MethodSymbol> group = MethodsOn(owner, m.Name);

        if (group.Count > 0)
        {
            _r.Resolved[m] = new MethodGroupSym(group);
            return Type.Void;
        }

        MethodSymbol? getter = MethodsOn(owner, "get_" + m.Name).FirstOrDefault();

        if (getter != null)
        {
            _r.Resolved[m] = new PropertyGetSym(getter);
            Type read = Close(ContextualMemberResult(target, getter), received);
            return conditional ? read.AsNullable() : read;
        }

        // WHAT EVERY OBJECT ANSWERS. Every type derives from object, so
        // ToString, Equals, GetHashCode and GetType are on it whether or not it
        // declared one -- and a class that declares none reached nothing at
        // all. `operand.ToString()` on an abstract MOperand is an ordinary line
        // and was reported as a member the type does not have.
        //
        // Before the extensions, because these are real members and an
        // extension must never shadow one.
        if (owner.Kind is TypeKind.Class or TypeKind.Interface
            && Rooted().FindMethods(m.Name) is { Count: > 0 } inherited)
        {
            _r.Resolved[m] = new MethodGroupSym(inherited);
            return Type.Void;
        }

        // AN EXTENSION METHOD, which is where LINQ lives. Last, after every
        // real member of the type has been tried, because an extension must
        // never shadow one -- that is C#'s rule, and it is what keeps adding an
        // extension from changing what existing code means.
        if (Extension(target, m.Name) is { Count: > 0 } outside)
        {
            _r.Resolved[m] = new MethodGroupSym(outside);
            _r.Receivers[m] = true;
            return Type.Void;
        }

        Error(m, $"'{owner.Name}' has no member '{m.Name}'");
        return Type.Error;
    }

    private Type CheckCall(CallExpr c)
    {
        // `GetType()` WRITTEN BARE inside a class is this object's, as C#
        // reads every inherited member of object: the call is `this.GetType()`.
        // Only when nothing in scope is called GetType.
        if (c.Target is NameExpr { Name: "GetType" } bareGetType && c.Args.Count == 0 && !InStaticContext
            && Lookup("GetType") is null && _thisType?.FindMethods("GetType").Count is null or 0)
        {
            CallExpr ofThis = new()
            {
                Target = new MemberExpr { Target = new ThisExpr { Line = bareGetType.Line, Col = bareGetType.Col }, Name = "GetType", Line = bareGetType.Line, Col = bareGetType.Col },
                Line = c.Line, Col = c.Col,
            };
            _r.Rewrites[c] = ofThis;
            return CheckExpr(ofThis);
        }

        // A NULL-CONDITIONAL CALL evaluates its receiver once, calls only when
        // that value exists, and otherwise produces null. Member access already
        // had this behaviour; invocation did not, so `x?.Items.FirstOrDefault()`
        // still tried to pass a nullable receiver to the extension method.
        // Lower it into the evaluate-once PatternExpr used by complex patterns.
        if (c.Target is MemberExpr conditionalTarget
            && (conditionalTarget.NullConditional || HasConditionalMember(conditionalTarget.Target)))
        {
            SubjectExpr forTest = new() { Line = c.Line, Col = c.Col };
            SubjectExpr forCall = new() { Line = c.Line, Col = c.Col };
            MemberExpr safeTarget = new()
            {
                Target = new SuppressExpr
                {
                    Operand = forCall,
                    Line = c.Line,
                    Col = c.Col,
                },
                Name = conditionalTarget.Name,
                Guarded = true,
                Line = conditionalTarget.Line,
                Col = conditionalTarget.Col,
            };
            safeTarget.TypeArgs.AddRange(conditionalTarget.TypeArgs);

            CallExpr safeCall = new()
            {
                Target = safeTarget,
                Line = c.Line,
                Col = c.Col,
            };
            safeCall.Args.AddRange(c.Args);
            safeCall.ArgNames.AddRange(c.ArgNames);

            ConditionalExpr choose = new()
            {
                Cond = new BinaryExpr
                {
                    Op = BinOp.Eq,
                    Left = forTest,
                    Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = c.Line, Col = c.Col },
                    Line = c.Line,
                    Col = c.Col,
                },
                Then = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = c.Line, Col = c.Col },
                Else = safeCall,
                Line = c.Line,
                Col = c.Col,
            };

            PatternExpr lowered = new()
            {
                Subject = conditionalTarget.Target,
                Test = choose,
                Line = c.Line,
                Col = c.Col,
            };
            _r.Rewrites[c] = lowered;
            return CheckExpr(lowered);
        }

        if (c.Target is MemberExpr { Name: "HasFlag" } flag && c.Args.Count == 1)
        {
            Type valueType = CheckExpr(flag.Target);
            Type flagType = CheckExpr(c.Args[0]);

            if (valueType.Symbol?.Kind == TypeKind.Enum
                && flagType.AsNonNullable().Equals(valueType.AsNonNullable()))
            {
                _r.EnumHasFlags.Add(c);
                return Type.Bool;
            }
        }

        // AddressOf comes first, before the arguments are given types at all: a
        // method NAMED rather than called has no type, so asking for one is the
        // question that fails. It is the address of the name, not a value.
        if (c.Target is MemberExpr { Name: "AddressOf" } named
            && named.Target is NameExpr { Name: "Sys" }
            && !(c.Args.Count == 1 && c.Args[0] is RefArgExpr))
        {
            if (c.Args.Count == 1)
            {
                CheckExpr(c.Args[0]);

                if (_r.Resolved.TryGetValue(c.Args[0], out Sym? found)
                    && found is MethodGroupSym mg && mg.Methods.Count == 1 && mg.Methods[0].Static)
                {
                    _r.AddressOf[c] = mg.Methods[0];
                    return Type.I64;
                }
            }

            Error(c, "'Sys.AddressOf' names one static method, written without its arguments");
            return Type.Error;
        }

        // GetType(), which every object has and nothing declares.
        //
        // Answered here rather than by a method on some root class, because
        // this language has no root class -- and it does not need one for this:
        // every object carries its vtable at offset 0 and the descriptor sits
        // in front of the vtable, so the answer is two instructions on anything
        // with a vtable at all.
        //
        // ON ANY REFERENCE, which is what C# allows. It used to be only a
        // declared type, from the days when a string and an array had no
        // vtable; both carry a descriptor now, and so does everything held
        // as `object` or as an interface, since a word of those types is an
        // address of something that has one. A value type has no descriptor
        // to read, and is refused as before.
        if (c.Args.Count == 0 && c.Target is MemberExpr { Name: "GetType" } asked)
        {
            Type on = CheckExpr(asked.Target);

            // What has been PROVED about it counts here as much as anywhere: a
            // subject that a `case null` has already dealt with is an object.
            if (Path(asked.Target) is string spelt && _notNullPaths.Contains(spelt))
            {
                on = on.AsNonNullable();
            }

            if (on.Prim == Prim.Any || (on.IsReference && on.Prim != Prim.NullLiteral))
            {
                RequireNonNull(on, asked.Target, "ask the type of");
                _r.GetTypes.Add(c);
                return Type.TypeHandle;
            }

            if (!on.IsError)
            {
                Error(c, $"GetType needs an object; '{on}' does not carry its type at run time");
                return Type.Error;
            }
        }

        // Local functions retain their own parameter names/defaults even
        // though their runtime representation is an ordinary Func/Action.
        if (c.Target is NameExpr localName)
        {
            Sym? localTarget = Lookup(localName.Name);
            if (localTarget is null && _thisType?.FindField(localName.Name) is { } capturedLocal)
                localTarget = new FieldSym(capturedLocal);
            if (localTarget is not null && LocalFunctionDeclaration(localTarget) is { } localFunction)
                CompleteLocalArguments(c, localFunction);
        }

        // NAMED ARGUMENTS ARE PUT IN ORDER BEFORE ANYTHING ELSE HAPPENS.
        //
        // Done first, and by rewriting the call, so that overload resolution,
        // the by-reference checks, the code generator and everything else go
        // on seeing an ordinary positional call. The alternative is teaching
        // each of them about names, and there are eleven places that would
        // have to agree.
        //
        // C# evaluates named arguments where they were WRITTEN and passes them
        // where they belong. Here they are evaluated where they belong, which
        // differs only for arguments with side effects that also depend on each
        // other -- and reordering is what makes `With(nullable: true)` work at
        // all, which is the reason this exists.
        // The target has to be resolved first to know whose parameter names
        // these are, and arguments are otherwise checked before it -- so this
        // only happens when something was actually named. Checking the target
        // twice is safe: it is a name or a member access, and both record the
        // same answer whichever time they are asked.
        if (c.ArgNames.Any(n => n != null))
        {
            CheckExpr(c.Target);
            Reorder(c);
        }

        // A LAMBDA OR METHOD GROUP IS LEFT UNTIL THE OVERLOAD IS KNOWN,
        // because until then there is nothing for it to be.  A method group
        // still has to be checked once here so its overload set is recorded;
        // after that it stands in as object just like a written lambda.  This
        // matters after generic expansion, where `All(IsWord)` calls the
        // already-concrete All$Type routine and no inference pass remains to
        // give the otherwise-void method group its delegate type.
        // AN ARGUMENT IS NOT WHAT THE SURROUNDING DECLARATION IS WAITING FOR.
        // `List<int> x = Make(new());` says what x is, not what Make takes, so
        // the target type is put away while the arguments are read and the
        // parameter's own type is what fills them in below.
        Type? outerTarget = _wanted;

        _wanted = null;

        // THE RECEIVER IS EVALUATED FIRST, so what it proves holds in the
        // arguments: `_form!.PaintWindow(r, _form.Width)` is legal C#, the `!`
        // having set _form's null state before the argument reads it. The
        // arguments are checked before the target here (overloads are chosen
        // from them), so the receiver chain's `!`s are applied ahead of them.
        ProveReceivers((c.Target as MemberExpr)?.Target);

        List<Type> args = new();
        foreach (Expr argument in c.Args)
        {
            if (argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null })
            {
                args.Add(Type.Any);
                continue;
            }

            Type argumentType = CheckExpr(argument);
            args.Add(IsFunctionSource(argument) ? Type.Any : argumentType);
        }
        _wanted = outerTarget;

        Type targetType = CheckExpr(c.Target);

        // A PROPERTY THAT IS BEING CALLED IS NOT THE PROPERTY. `list.Count` is
        // how many there are; `list.Count(x => x.Ready)` is how many match, and
        // it is an extension method that happens to share the name. C# reads it
        // the same way -- a property cannot take arguments, so the only reading
        // that works is the one that was meant.
        if (c.Target is MemberExpr calledProperty
            && _r.Resolved.TryGetValue(c.Target, out Sym? already) && already is PropertyGetSym
            && Extension(_r.TypeOf(calledProperty.Target), calledProperty.Name) is { Count: > 0 } instead)
        {
            _r.Resolved[c.Target] = new MethodGroupSym(instead);
            _r.Receivers[calledProperty] = true;
        }

        // AND A TYPE NAME THAT IS BEING CALLED IS NOT THE TYPE. `Table.Entry(2)`
        // calls the method `Entry` even where `Table.Entry` also names a type
        // nested inside Table: a type name cannot take an argument list, so the
        // only reading that works is the method group. This is the mirror of
        // the rule in CheckMember, where a method group that is NOT being
        // called loses to the type of the same name.
        if (c.Target is MemberExpr calledType
            && _r.Resolved.TryGetValue(c.Target, out Sym? asType) && asType is TypeNameSym
            && _r.Resolved.TryGetValue(calledType.Target, out Sym? qualifierSym)
            && qualifierSym is TypeNameSym qualifier
            && qualifier.Symbol.FindMethods(calledType.Name).Where(method => method.Static).ToList()
               is { Count: > 0 } calledMethods
            && !targetType.Symbol!.FindMethods("Invoke").Any(method => method.Params.Count == args.Count))
        {
            _r.Resolved[c.Target] = new MethodGroupSym(calledMethods);
            targetType = Type.Void;
        }


        if (!_r.Resolved.TryGetValue(c.Target, out Sym? sym))
        {
            sym = null;
        }

        MethodGroupSym? group = sym as MethodGroupSym;
        if (sym is CapturedMethodGroupSym capturedGroup)
        {
            group = new MethodGroupSym(capturedGroup.Methods);
            _r.CapturedReceivers[c] = capturedGroup.Holder;
        }

        if (group is null)
        {
            // A VALUE THAT CAN BE CALLED, which is what a function held in a
            // variable is on this machine.
            //
            // There are no delegate types. What there is instead is an ordinary
            // interface with an Invoke method -- Func and Action in the standard
            // library are exactly that -- and `f(x)` is sugar for `f.Invoke(x)`.
            // Virtual dispatch already does the rest, so a function value costs
            // no machinery that classes and interfaces did not already need.
            //
            // This is the half of lambdas that does not need lambdas: with it,
            // higher-order code can be written and tested today by implementing
            // the interface by hand, and a lambda becomes sugar for a class the
            // compiler writes instead of one the author does.
            MethodSymbol? invoke = targetType.Symbol?
                                             .FindMethods("Invoke")
                                             .FirstOrDefault(m => m.Params.Count == args.Count);

            if (invoke != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if (c.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null })
                    {
                        Type? saved = _wanted;
                        _wanted = invoke.Params[i].Type;
                        args[i] = CheckExpr(c.Args[i]);
                        _wanted = saved;
                    }
                    CheckAssignable(args[i], invoke.Params[i].Type, c.Args[i],
                                    $"argument {i + 1} of '{targetType}'");
                }

                RequireNonNull(targetType, c.Target, "call");
                _r.Invocations[c] = invoke;
                return invoke.Returns;
            }

            if (!targetType.IsError)
            {
                Error(c, "this expression is not a method and cannot be called");
            }
            return Type.Error;
        }

        // THE RECEIVER BECOMES THE FIRST ARGUMENT, for a member call on a type
        // whose methods are static -- a string, today.
        //
        // Done by rewriting the call rather than by teaching the code generator
        // about a second calling convention: once the receiver is in the
        // argument list this is an ordinary static call, and everything below
        // here and in the code generator needs to know nothing.
        //
        // Guarded because a call is checked once per generic instantiation and
        // inserting twice would pass the string as its own first argument.
        if (c.Target is MemberExpr recv && _r.Receivers.ContainsKey(recv)
            && !_receiverAdded.Contains(c) && !c.ReceiverAdded)
        {
            _receiverAdded.Add(c);
            c.ReceiverAdded = true;
            c.Args.Insert(0, recv.Target);
            args.Insert(0, _r.TypeOf(recv.Target));
        }

        // A `params T[]` CALL HAS TWO FORMS. If an array is supplied in the
        // final position it is an ordinary call. Otherwise the written tail is
        // packed into a fresh array before overload resolution and codegen, so
        // neither of those later stages needs a second calling convention.
        //
        // Ordinary viable overloads win over expanded form, as C# requires.
        // This matters for `F(int)` beside `F(params int[])`, and also keeps an
        // explicitly supplied array from being wrapped in another array.
        // `out` AND `ref` ARE PART OF THE SIGNATURE. C# chooses between
        // `IsImm(Operand, out long)` and `IsImm(Operand, long)` by the word at
        // the call site and nothing else; without this the first was chosen and
        // the caller was then told it had failed to write `out`.
        // A MEMBER OF A CONSTRUCTED TEMPLATE TAKES THAT TEMPLATE'S ARGUMENTS.
        //
        // `x.AsSpan()` is a `Span<ulong>` before any `Span$ulong` has been
        // written -- the copy that spells it is made a round later -- so the
        // methods found on it are the TEMPLATE's, and `SequenceEqual` there
        // takes a `ReadOnlySpan<T>` rather than a `ReadOnlySpan<ulong>`. An
        // array offered to it fitted neither, and the call was reported as an
        // arity nothing has. CheckMember already does this for fields and for
        // properties; a method's parameters need it just as much.
        Dictionary<string, Type>? fromReceiver =
            c.Target is MemberExpr through && !_r.Receivers.ContainsKey(through)
            && group.Methods.Count > 0
            && _r.ExprType.TryGetValue(through.Target, out Type? receiver)
                ? Received(receiver, group.Methods[0].Owner)
                : null;

        Type Wants(MethodSymbol m, int i)
            => fromReceiver is null ? m.Params[i].Type : Close(m.Params[i].Type, fromReceiver);

        bool WordFits(MethodSymbol m, int i)
            => i >= m.Params.Count
            || c.Args[i] is RefArgExpr == (m.Params[i].ByRef && !m.Params[i].ReadOnly);

        bool WrittenFits(Type had, Type want, Expr written)
        {
            if (written is NewExpr { Type.Name.Length: 0, Elements: null } && (want.Symbol is not null || (written is NewExpr { Collection: true } && want.IsArray))) return true;
            if (Convertible(had, want) || had.IsError || Unmade(want) || Variant(had, want))
            {
                return true;
            }

            // A TUPLE LITERAL IS SHAPED BY WHAT WANTS IT. `undo.Add((key,
            // null))` has nothing in it to say what the null is, and C#
            // converts the literal to the parameter's tuple type element by
            // element -- so whether it fits is asked of the elements.
            if (written is TupleExpr made && want.Symbol is { } shape
                && shape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
                && shape.Fields.Count == made.Items.Count)
            {
                return Enumerable.Range(0, made.Items.Count)
                                 .All(i => WrittenFits(_r.TypeOf(made.Items[i]),
                                                       shape.Fields[i].Type, made.Items[i]));
            }

            // A NAMED constant counts, not only a literal one. C#'s rule is
            // about constant EXPRESSIONS, and `const int V5HeaderBytes = 64;`
            // passed to a ushort parameter is the case that found this: the
            // literal 64 was accepted where the name for it was not.
            return had.IsInteger && want.IsInteger && !want.Nullable
                && ConstantValue(written, _thisType) is long value && Binder.Fits(value, want);
        }

        bool OrdinaryFits(MethodSymbol m) => m.Params.Count == args.Count
            && Enumerable.Range(0, args.Count)
                         .All(i => WordFits(m, i) && WrittenFits(args[i], Wants(m, i), c.Args[i]));

        MethodSymbol? expandedParams = null;

        if (!group.Methods.Any(OrdinaryFits))
        {
            expandedParams = group.Methods.FirstOrDefault(m =>
                m.TypeParams.Count == 0
                && m.Params.Count > 0
                && m.Params[^1].IsParams
                && m.Params[^1].Type.IsArray
                && m.Params[^1].Type.Element is Type element
                && args.Count >= m.Params.Count - 1
                && Enumerable.Range(0, m.Params.Count - 1)
                             .All(i => WrittenFits(args[i], Wants(m, i), c.Args[i]))
                && Enumerable.Range(m.Params.Count - 1, args.Count - (m.Params.Count - 1))
                             .All(i => WrittenFits(args[i], element, c.Args[i])));

            if (expandedParams is { } variadic)
            {
                int fixedCount = variadic.Params.Count - 1;
                Type element = variadic.Params[^1].Type.Element!;
                TypeRef? elementRef = RefOf(element);

                if (elementRef is null)
                {
                    Error(c, $"the element type of the 'params' array in '{variadic.Name}' cannot be constructed");
                    return Type.Error;
                }

                NewExpr packed = new()
                {
                    Type = elementRef,
                    Elements = c.Args.Skip(fixedCount).ToList(),
                    Line = c.Line,
                    Col = c.Col,
                };

                c.Args.RemoveRange(fixedCount, c.Args.Count - fixedCount);
                c.Args.Add(packed);
                args.RemoveRange(fixedCount, args.Count - fixedCount);
                args.Add(CheckExpr(packed));
            }
        }

        // Overload resolution by arity, then by exact-then-convertible match.
        List<MethodSymbol> byArity = expandedParams is null
            ? group.Methods.Where(m => m.Params.Count == args.Count).ToList()
            : new List<MethodSymbol> { expandedParams };

        // A PARAMETER WITH A DEFAULT NEED NOT BE PASSED, which is what the
        // default is FOR. `char Peek(int n = 1)` is called as `Peek()` eleven
        // times in this compiler's own lexer, and every one of them was an
        // overload that takes no arguments and does not exist.
        //
        // The missing arguments are filled in from the declaration here, so
        // everything below sees a call with its full complement -- exactly what
        // the named-argument path does when it leaves a hole.
        //
        // TRIED ALSO WHEN AN OVERLOAD OF EXACTLY THIS ARITY EXISTS BUT DOES NOT
        // FIT. `EvalAs(Expr, ParamSymbol)` and `EvalAs(Expr, Type, bool = false,
        // bool = false)` both exist, and a call with a Type was answered by the
        // first and refused -- where C# considers every candidate, in expanded
        // form as well as written.
        if (byArity.Count > 0
            && !byArity.Any(m => m.TypeParams.Count > 0
                              || Enumerable.Range(0, args.Count)
                                           .All(i => WordFits(m, i)
                                                  && WrittenFits(args[i], Wants(m, i), c.Args[i]))))
        {
            byArity = new List<MethodSymbol>();
        }

        if (byArity.Count == 0)
        {
            // THE ONE WHOSE WRITTEN ARGUMENTS FIT, not merely the first that
            // can be completed. `Load(IrType, Operand, long = 0, …)` and
            // `Load(IrType, VReg, long = 0, …)` are both three arguments short
            // of five, and taking the first meant a VReg was handed to the
            // overload that wants an Operand -- and then reported as an
            // argument list nothing accepts.
            bool Completable(MethodSymbol m)
                => m.Params.Count > args.Count
                && Enumerable.Range(args.Count, m.Params.Count - args.Count)
                             .All(i => m.Decl?.Params.ElementAtOrDefault(i)?.Default is not null);

            MethodSymbol? shorter = group.Methods.FirstOrDefault(
                m => Completable(m)
                  && Enumerable.Range(0, args.Count)
                               .All(i => WordFits(m, i) && WrittenFits(args[i], Wants(m, i), c.Args[i])))
                ?? group.Methods.FirstOrDefault(Completable);

            if (shorter != null)
            {
                for (int i = args.Count; i < shorter.Params.Count; i++)
                {
                    Expr fallback = Written(shorter, shorter.Decl!.Params[i]);

                    c.Args.Add(fallback);
                    args.Add(CheckExpr(fallback));
                }

                byArity = new List<MethodSymbol> { shorter };
            }
        }

        if (byArity.Count == 0)
        {
            // THE ROOT'S OVERLOAD, which the type's own one hid. A class that
            // declares `Equals(Type other)` still inherits the two-argument
            // static object.Equals in C#, and calling it unqualified is
            // ordinary -- this compiler's own Type does it. Here the enclosing
            // type is searched first and answers with its own arity, so the
            // root is tried when that arity does not fit.
            byArity = Rooted().FindMethods(group.Methods[0].Name)
                              .Where(m => m.Static && m.Params.Count == args.Count)
                              .ToList();

            if (byArity.Count == 0)
            {
                Error(c, $"no overload of '{group.Methods[0].Name}' takes {args.Count} argument{(args.Count == 1 ? "" : "s")}");
                return Type.Error;
            }

            _r.Resolved[c.Target] = new MethodGroupSym(byArity);
        }

        // A LAMBDA CHOOSES BY ITS ARITY. It stands in as 'object', which
        // converts to everything, so without this the first overload taking
        // any function at all wins -- and `Where((n, i) => ...)` picked the
        // one-argument Where and was told it has no Invoke taking two.
        bool Fits(MethodSymbol m)
        {
            for (int i = 0; i < c.Args.Count && i < m.Params.Count; i++)
            {
                List<MethodSymbol> invokes = m.Params[i].Type.Symbol?.FindMethods("Invoke")
                                          ?? new List<MethodSymbol>();

                if (c.Args[i] is LambdaExpr lam
                    && !invokes.Any(v => v.Params.Count == lam.Params.Count))
                {
                    return false;
                }

                // AND SO DOES A METHOD GROUP. `f.Blocks.Where(_cfg.IsRoot)`
                // names a method of one parameter, which is the one-argument
                // Where; the other takes a function of two and nothing in the
                // group has that many, so without this the two-argument
                // overload won and IsRoot was told it takes two.
                if (c.Args[i] is not LambdaExpr && invokes.Count > 0
                    && _r.Resolved.TryGetValue(c.Args[i], out Sym? passed)
                    && Grouped(passed) is { Count: > 0 } named
                    && !invokes.Any(v => named.Any(g => g.Params.Count == v.Params.Count)))
                {
                    return false;
                }
            }
            return true;
        }

        byArity = byArity.Where(Fits).ToList();

        if (byArity.Count == 0)
        {
            Error(c, $"no overload of '{group.Methods[0].Name}' takes a function of that many arguments");
            return Type.Error;
        }

        // A WHOLE-NUMBER CONSTANT FITS WHERE IT FITS, at a call as much as at an
        // assignment: `list.Add(0)` on a List<byte> passes a byte in C#,
        // because the compiler can see the value. Judged per ARGUMENT, since
        // only the written expression carries the constant.
        // Of the overloads that accept the arguments, the one better than
        // every other: no argument converts to it worse, and some convert
        // to it better. With none better than all the rest, the first, as
        // this was chosen before the rule.
        MethodSymbol? BetterMember(List<MethodSymbol> applicable)
        {
            if (applicable.Count <= 1) return applicable.FirstOrDefault();
            foreach (MethodSymbol one in applicable)
            {
                if (applicable.All(other => ReferenceEquals(other, one) || BetterThan(one, other))) return one;
            }
            return applicable[0];
        }

        bool BetterThan(MethodSymbol one, MethodSymbol other)
        {
            bool better = false;
            for (int i = 0; i < args.Count && i < one.Params.Count && i < other.Params.Count; i++)
            {
                int said = BetterConversion(args[i], Wants(one, i), Wants(other, i));
                if (said < 0) return false;
                if (said > 0) better = true;
            }
            return better;
        }

        // C# 12.6.4.5: which of two parameter types an argument of type
        // `from` converts to better -- 1 the first, -1 the second, 0 neither.
        // The one it already is; else the one that converts to the other and
        // not back; else a signed integer over an unsigned one.
        int BetterConversion(Type from, Type first, Type second)
        {
            if (from.IsError || Same(first, second)) return 0;
            bool isFirst = Same(from, first), isSecond = Same(from, second);
            if (isFirst != isSecond) return isFirst ? 1 : -1;
            bool toSecond = Convertible(first, second), toFirst = Convertible(second, first);
            if (toSecond != toFirst) return toSecond ? 1 : -1;
            if (SignedIntegral(first) && UnsignedIntegral(second)) return 1;
            if (SignedIntegral(second) && UnsignedIntegral(first)) return -1;
            return 0;
        }

        static bool Same(Type a, Type b)
            => a.Equals(b) || (!a.IsNullableValue && !b.IsNullableValue && a.AsNonNullable().Equals(b.AsNonNullable()));

        static bool SignedIntegral(Type t)
            => t.Symbol is null && !t.IsNullableValue && t.Prim is Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.NInt;

        static bool UnsignedIntegral(Type t)
            => t.Symbol is null && !t.IsNullableValue && t.Prim is Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64 or Prim.NUInt;

        bool Accepts(MethodSymbol m, bool variant)
        {
            for (int i = 0; i < args.Count && i < m.Params.Count; i++)
            {
                Type want = Wants(m, i);

                if (c.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null } && (want.Symbol is not null || (c.Args[i] is NewExpr { Collection: true } && want.IsArray))) continue;

                // A BY-REFERENCE ARGUMENT FITS ONLY ITS OWN TYPE. C# counts an
                // overload applicable only when every ref and out argument's
                // type is the parameter's exactly (nullability aside); one that
                // would need a conversion is not a candidate at all. Taken as
                // one here, `Interlocked.Exchange(ref box, b)` chose the
                // (ref object?) overload over the generic that fits, and was
                // then refused for the very mismatch that should have ruled it
                // out.
                if (c.Args[i] is RefArgExpr && m.Params[i].ByRef && !m.Params[i].ReadOnly)
                {
                    if (args[i].IsError || args[i].AsNonNullable().Equals(want.AsNonNullable())) continue;
                    return false;
                }

                if (Convertible(args[i], want) || args[i].IsError || Unmade(want)
                    || (variant && Variant(args[i], want)))
                {
                    continue;
                }

                // A TUPLE LITERAL IS SHAPED BY WHAT WANTS IT, here as much as
                // in the arity filter. Only the tuple rule, and not the whole
                // of WrittenFits: the constant-expression conversion below
                // leaves char out on purpose, and `d.Write(7)` over
                // Write(char)/Write(string)/Write(long) is the call that says
                // why.
                if (i < c.Args.Count && c.Args[i] is TupleExpr shaped
                    && want.Symbol is { } toShape
                    && toShape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
                    && toShape.Fields.Count == shaped.Items.Count
                    && Enumerable.Range(0, shaped.Items.Count)
                                 .All(k => WrittenFits(_r.TypeOf(shaped.Items[k]),
                                                       toShape.Fields[k].Type, shaped.Items[k])))
                {
                    continue;
                }

                // NOT TO CHAR, which C# leaves out of the constant expression
                // conversion on purpose: a number is not a character. With it
                // in, `Write(7)` against Write(char)/Write(string)/Write(long)
                // took the char and printed a control code.
                if (args[i].IsInteger && want.IsInteger && !want.Nullable
                    && want.Prim != Prim.Char && args[i].Prim != Prim.Char
                    && i < c.Args.Count && ConstantValue(c.Args[i], _thisType) is long value
                    && Binder.Fits(value, want))
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        // THE WORD AT THE CALL SITE NARROWS FIRST, because it is part of the
        // signature rather than a thing to complain about afterwards: `IsImm(y,
        // 0)` cannot mean the overload whose second parameter is `out long`.
        List<MethodSymbol> byWord = byArity
            .Where(m => Enumerable.Range(0, args.Count).All(i => WordFits(m, i))).ToList();

        if (byWord.Count > 0)
        {
            byArity = byWord;
        }

        MethodSymbol? best = byArity.FirstOrDefault(
                                 m => m.TypeParams.Count == 0
                                   && Enumerable.Range(0, Math.Min(args.Count, m.Params.Count))
                                                .All(i => args[i].Equals(Wants(m, i))
                                                    || (!args[i].IsNullableValue && !Wants(m, i).IsNullableValue
                                                        && args[i].AsNonNullable().Equals(Wants(m, i).AsNonNullable()))))
                          // AN ENUM IS NOT A NUMBER TO C#: there is no implicit
                          // conversion from one, so `sb.Append(op)` is
                          // Append(object) and prints the member's NAME. This
                          // language lets an enum stand where an integer is
                          // wanted, so the rule is kept as a preference: given
                          // an overload that takes the enum as an object, that
                          // one, before any that would take it as a number.
                          ?? (args.Any(a => a.Symbol is { Kind: TypeKind.Enum })
                                ? byArity.FirstOrDefault(m => m.TypeParams.Count == 0 && Accepts(m, false)
                                      && Enumerable.Range(0, Math.Min(args.Count, m.Params.Count))
                                                   .All(i => args[i].Symbol is not { Kind: TypeKind.Enum }
                                                          || Wants(m, i).Prim == Prim.Any
                                                          || ReferenceEquals(Wants(m, i).Symbol, args[i].Symbol)))
                                : null)
                          // THE BETTER FUNCTION MEMBER (C# 12.6.4.3), not the
                          // first declared: `Math.Min(long, int)` is
                          // Min(long, long) wherever Min(double, double)
                          // happens to be written.
                          ?? BetterMember(byArity.Where(m => m.TypeParams.Count == 0 && Accepts(m, false)).ToList())
                          // AND LAST, THROUGH A VARIANT INTERFACE. Only when
                          // nothing fits without it: variance makes more things
                          // convertible, and a rule that widens the candidate
                          // set moves calls that resolve perfectly well today.
                          ?? byArity.FirstOrDefault(m => m.TypeParams.Count == 0 && Accepts(m, variant: true));

        // A GENERIC METHOD IS TRIED LAST, so an ordinary overload that fits
        // still wins -- which is what C# does, and what keeps adding a generic
        // overload from changing where existing calls go.
        Dictionary<string, Type>? bound = fromReceiver;

        if (best is null)
        {
            foreach (MethodSymbol candidate in byArity.Where(m => m.TypeParams.Count > 0))
            {
                // WRITTEN DOWN BEATS WORKED OUT. `Array.Empty<Node>()` has
                // nothing to infer from -- it takes no arguments at all -- and
                // saying so at the call is how C# answers that.
                List<TypeRef> written = c.Target switch
                {
                    MemberExpr onType => onType.TypeArgs,
                    NameExpr bare => bare.TypeArgs,
                    _ => new List<TypeRef>(),
                };

                if (written.Count == candidate.TypeParams.Count)
                {
                    Dictionary<string, Type> told = new(StringComparer.Ordinal);

                    for (int i = 0; i < written.Count; i++)
                    {
                        told[candidate.TypeParams[i]] = Resolve(written[i], _thisType);
                    }

                    best = candidate;
                    bound = told;
                    break;
                }

                if (Infer(candidate, args, c.Args, out Dictionary<string, Type> got))
                {
                    // THE MOST SPECIFIC SHAPE WINS, not the first one declared.
                    // `Task.Run(work)` with a Func<Task<int>> unifies against
                    // BOTH `Run<T>(Func<T>)` (T = Task<int>) and
                    // `Run<T>(Func<Task<T>>)` (T = int); taking the first asks
                    // for a Task<Task<int>> that the program never named. C#
                    // answers this by preferring the parameter that says more
                    // about the argument, so keep looking and pick that one.
                    // AND ONE THAT LEFT NOTHING OPEN BEATS ONE THAT DID.
                    //
                    // `Select<T,R>(Func<T,R>)` and `Select<T,R>(Func<T,int,R>)`
                    // are both reached by a method group of one argument; only
                    // the first can say what R is, and the second inferred T
                    // and stopped -- so `names.Select(Twice)` came back as a
                    // `List<R>` that converts to nothing. A candidate with an
                    // unbound parameter has not really been inferred.
                    int openNow = candidate.TypeParams.Count(t => !got.ContainsKey(t));
                    int openBest = best is null
                                 ? int.MaxValue
                                 : best.TypeParams.Count(t => bound?.ContainsKey(t) != true);

                    if (best is null || openNow < openBest
                        || (openNow == openBest && Specificity(candidate) > Specificity(best))
                        || (openNow == openBest && Specificity(candidate) == Specificity(best)
                            && BetterLambdas(candidate, got, best, bound ?? new(), c.Args)))
                    {
                        best = candidate;
                        bound = got;
                    }
                }
            }
        }

        if (best is null)
        {
            // SAY WHICH LIMIT WAS HIT. When the only candidates are generic,
            // "no overload accepts these" sends the author looking for a
            // missing overload that is right there -- the real answer is that
            // its type argument is not a machine word.
            string why = byArity.Any(m => m.TypeParams.Count > 0)
                ? "; a generic method is compiled once for every type argument, so the"
                  + " argument must be a class or an interface -- something carrying a"
                  + " vtable the one copy can dispatch through"
                : "";

            Error(c, $"no overload of '{byArity[0].Name}' accepts ({string.Join(", ", args)}){why}");
            return Type.Error;
        }

        // `out var x` AND A BARE `default` GET THEIR TYPE NOW, from the
        // parameter of the overload that was just chosen -- which is why they
        // could not have one before.
        for (int i = 0; i < args.Count; i++)
        {
            if (i < best.Params.Count)
            {
                args[i] = Settle(c.Args[i], best.Params[i].Type, args[i]);
            }

            if (c.Args[i] is RefArgExpr { Declare: null, Name: not null } inferred
                && i < best.Params.Count)
            {
                LocalSym made = new(NewSlot(), best.Params[i].Type, inferred.Name);
                Declare(inferred, inferred.Name, made);
                _assigned.Add(made);

                // Resolving the target NOW is what gives the code generator
                // somewhere to take the address of: it looks the name up in
                // Resolved, and nothing had put it there.
                CheckExpr(inferred.Target);
                args[i] = best.Params[i].Type;
            }
        }

        for (int i = 0; i < args.Count; i++)
        {
            // BOTH SIDES HAVE TO SAY SO. Passing by reference is not something
            // a caller may decide on its own: the callee reads and writes
            // through the address, so handing it a value would have it treat a
            // number as somewhere to write. C# makes the caller repeat the word
            // for exactly this reason -- it is the one place where what happens
            // to the caller's variable is invisible at the call site otherwise.
            bool passed = c.Args[i] is RefArgExpr;

            // AN `in` PARAMETER IS THE EXCEPTION, and for the reason the word
            // exists: the address is passed so a struct is not copied, and
            // nothing the caller can see changes, so C# asks for no word.
            if (passed != (best.Params[i].ByRef && !best.Params[i].ReadOnly))
            {
                Error(c.Args[i], passed
                    ? $"argument {i + 1} of '{best.Name}' is not declared 'out' or 'ref'"
                    : $"argument {i + 1} of '{best.Name}' is 'out' or 'ref' and must be passed with the word");
                continue;
            }

            // AND THE TYPE MUST MATCH EXACTLY, not merely convert. A conversion
            // needs somewhere to put the converted value, and there is nowhere:
            // the callee writes through the address it was given, straight into
            // the caller's variable, so a long written through a pointer that
            // was really an int's would scribble on whatever is next to it.
            // NULLABILITY IS NOT PART OF THE MATCH, because it is not part of
            // the machine: `out TypeDecl` and `out TypeDecl?` are the same word
            // written through the same address, and C# warns rather than
            // refusing. What must match exactly is the SHAPE -- an int written
            // through a long's address is what this check exists to stop.
            // AGAINST WHAT THE PARAMETER IS FOR THIS CALL, type arguments and
            // all. A generic method's `ref T[]` is still spelled T[] in the
            // symbol; comparing the argument against that refused
            // `Array.Resize(ref items, n)` -- "argument 1 of 'Resize' is
            // 'T[]' and this is 'int[]'" -- having just inferred that T is
            // int and that they are the same thing.
            Type wantByRef = Close(best.Params[i].Type, bound);

            if (passed && !args[i].AsNonNullable().Equals(wantByRef.AsNonNullable())
                && !args[i].IsError)
            {
                Error(c.Args[i],
                      $"argument {i + 1} of '{best.Name}' is '{wantByRef}' "
                    + $"and this is '{args[i]}'; a by-reference argument must match exactly");
                continue;
            }

            if (!passed)
            {
                Type want = Close(best.Params[i].Type, bound);
                if (best.TypeParams.Count > 0 && HasTupleUse(want)
                    && IsFunctionSource(c.Args[i]) && RefOf(want) is TypeRef argumentUse)
                {
                    c.ArgumentTypeUses ??= new();
                    c.ArgumentTypeUses[i] = argumentUse;
                }
                else if (best.TypeParams.Count == 0
                    && c.ArgumentTypeUses?.TryGetValue(i, out TypeRef? savedUse) == true)
                    want = Resolve(savedUse, _thisType);
                if (c.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null })
                {
                    Type? saved = _wanted;
                    _wanted = want;
                    args[i] = CheckExpr(c.Args[i]);
                    _wanted = saved;
                }

                // AND HERE IS WHERE A LAMBDA FINDS OUT WHAT IT IS. The overload
                // has been chosen, so the parameter's type is known, so the
                // closure can be built against the Invoke it has to implement.
                if (c.Args[i] is LambdaExpr lam)
                {
                    args[i] = CheckLambda(lam, want);
                    continue;
                }

                if (MethodGroupLambda(c.Args[i], want) is LambdaExpr wrapper)
                {
                    _r.Rewrites[c.Args[i]] = wrapper;
                    args[i] = CheckLambda(wrapper, want);
                    continue;
                }

                // A CONDITIONAL WITH A METHOD GROUP OR LAMBDA ARM, checked
                // again now that the delegate it has to be is known.
                if (c.Args[i] is ConditionalExpr { } choosing && IsFunctionSource(choosing))
                {
                    Type? saved = _wanted;
                    _wanted = want;
                    args[i] = CheckExpr(choosing);
                    _wanted = saved;
                }

                // WITH THE INFERRED ARGUMENTS PUT IN. A parameter declared 'T'
                // is checked against what T was worked out to be, not against
                // the type parameter -- otherwise every generic call reports
                // that it cannot convert its argument to 'T'.
                CheckAssignable(args[i], want, c.Args[i], $"argument {i + 1} of '{best.Name}'");
            }
            else if (c.Args[i] is RefArgExpr { IsOut: true, Target: NameExpr outName }
                     && Lookup(outName.Name) is LocalSym filled)
            {
                _assigned.Add(filled);
            }
        }

        // A GENERIC METHOD CALL IS WRITTEN DOWN so a copy can be compiled for
        // it. Nothing here can compile one: the monomorphiser has already run
        // and is syntactic, so this is a note for the round after.
        MethodSymbol called = best;

        if (best.TypeParams.Count > 0 && bound != null && best.Decl is { } generic)
        {
            List<TypeRef> spelt = new();

            foreach (string p in best.TypeParams)
            {
                if (!bound.TryGetValue(p, out Type? was) || RefOf(was) is not TypeRef spell)
                {
                    spelt.Clear();
                    break;
                }

                spelt.Add(spell);
            }

            if (spelt.Count == best.TypeParams.Count)
            {
                int member = generic.TemplateIndex;

                if (member < 0 && best.Owner.Decl is TypeDecl declaring)
                {
                    member = declaring.Members.IndexOf(generic);
                }

                string wanted = Monomorphiser.MethodName(generic.Name, spelt)
                              + "$" + member;
                MethodSymbol? existing = best.Owner.Methods
                    .FirstOrDefault(m => m.Name == wanted);

                if (existing is not null)
                {
                    // A previous discovery round already made this copy. Bind
                    // directly to it instead of requesting the generic
                    // template forever; generated/rewritten calls are not
                    // necessarily nodes retained in the source AST for
                    // Program.Specialise to rename between rounds.
                    called = existing;
                }
                else
                {
                    _r.Wanted.Add((c, generic, spelt));
                }
            }
        }

        _r.Calls[c] = called;

        // Calling an async method hands back a TASK, not the value: the method
        // has not finished and quite possibly has not started doing the part
        // that takes time. Awaiting it is what produces the value, and the type
        // of that is what the method said it returns.

        // A GENERIC METHOD HANDS BACK WHAT IT WAS GIVEN, not a type parameter.
        // `Pick(a, b)` where a is a Node is a Node, and the caller should not
        // have to cast it back into one.
        Type resultType = best.Returns;
        if (!c.ReceiverAdded && c.Target is MemberExpr access)
            resultType = ContextualMemberResult(_r.TypeOf(access.Target), best);
        Type answer = Close(resultType, bound);
        if (best.TypeParams.Count > 0)
        {
            c.ResultTupleNames = answer.Names is null ? null : new List<string>(answer.Names);
            c.ResultTypeUse = HasTupleUse(answer) ? RefOf(answer) : null;
        }
        else if (c.ResultTypeUse is TypeRef resultUse)
            answer = Resolve(resultUse, _thisType);
        else if (c.ResultTupleNames is not null && answer.Symbol is TypeSymbol resultShape
                 && resultShape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal))
        {
            answer = answer.WithNames(c.ResultTupleNames);
        }

        // A RESULT THAT IS NULL ONLY WHERE AN ARGUMENT WAS.
        // `[return: NotNullIfNotNull(nameof(path))]` on Path.ChangeExtension
        // says the method hands back a null exactly when it was given one, so a
        // caller who passed a string is handed a string. The result type is
        // declared nullable because of the other case, and this is the rule
        // that makes the ordinary call usable without a cast.
        if (answer.Nullable && best.Decl is { NotNullIfNotNull: { } onlyFor } signed)
        {
            int which = signed.Params.FindIndex(p => p.Name == onlyFor);

            if (which >= 0 && which < args.Count && !args[which].Nullable
                && args[which].Prim != Prim.NullLiteral && !args[which].IsError)
            {
                answer = answer.AsNonNullable();
            }
        }

        if (best.TypeParams.Count > 0 && answer.ParamName is string unbound)
        {
            // Nothing in the arguments says what it returns -- `T Make<T>()`.
            // C# makes you write the type argument at the call site for exactly
            // this, and that spelling is not read here yet.
            Error(c, $"the type argument for '{unbound}' cannot be worked out from the arguments of '{best.Name}'");
            return Type.Error;
        }

        return answer;
    }

    private static bool HasConditionalMember(Expr expression) => expression switch
    {
        MemberExpr member => member.NullConditional || HasConditionalMember(member.Target),
        CallExpr call => HasConditionalMember(call.Target),
        IndexExpr index => HasConditionalMember(index.Target),
        _ => false,
    };

    /// <summary>
    /// C#'s IMPLICIT CONSTANT EXPRESSION CONVERSION, applied to the two sides
    /// of an operator.
    ///
    /// `v % 10` with v a ulong has no common type in the promotion table: an
    /// int and a ulong could each hold a value the other cannot, so the table
    /// refuses them. C# does not refuse this line, because 10 is a CONSTANT and
    /// a non-negative constant is known to fit -- so the constant becomes a
    /// ulong and the table then has two ulongs. The same rule takes an int
    /// constant to uint, where the table would otherwise widen both to long.
    ///
    /// The constant's recorded type is REWRITTEN, not merely reconsidered
    /// here: the code generator promotes the operands from the types recorded
    /// for them and knows nothing of constants, so it must find a ulong on
    /// each side.
    /// </summary>
    private void AdoptUnsignedConstant(Expr left, ref Type l, Expr right, ref Type r)
    {
        // C#'s implicit constant conversions (spec 10.2.11): an int constant
        // becomes any unsigned type its value fits, a long constant only
        // ulong. `uintValue == SomeLongConstant` is a comparison of longs --
        // the constant made a uint here was lowered at its declared width,
        // an i32 compared with an i64.
        if (l.Prim is Prim.U64 or Prim.U32 or Prim.NUInt && Adoptable(r.Prim, l.Prim))
        {
            if (FitsUnsigned(right, l.Prim))
            {
                // THE VALUE, NOT THE CELL. A constant is a number and never a
                // `uint?`, so what it adopts from a nullable neighbour is the
                // type that neighbour holds -- otherwise `loadBase ?? 0x10000`
                // made the literal a nullable too and the whole expression came
                // out as one.
                r = l.IsNullableValue ? l.Underlying : l;
                _r.ExprType[right] = r;
            }
        }
        else if (r.Prim is Prim.U64 or Prim.U32 or Prim.NUInt && Adoptable(l.Prim, r.Prim))
        {
            if (FitsUnsigned(left, r.Prim))
            {
                l = r.IsNullableValue ? r.Underlying : r;
                _r.ExprType[left] = l;
            }
        }
    }

    /// <summary>Whether a signed constant of type `signed` may convert to `unsigned` at all.</summary>
    private static bool Adoptable(Prim signed, Prim unsigned)
        => signed is Prim.I8 or Prim.I16 or Prim.I32 || signed == Prim.I64 && unsigned == Prim.U64;

    /// <summary>Whether a signed constant expression is non-negative and fits the unsigned width.</summary>
    private bool FitsUnsigned(Expr constant, Prim unsigned)
    {
        if (ConstantValue(constant, _thisType) is not long value || value < 0)
        {
            return false;
        }
        return unsigned == Prim.U64
            || (unsigned == Prim.NUInt && Target.Current.WordSize == 8)
            || value <= uint.MaxValue;
    }

    /// <summary>
    /// `a - b` where the type of a or of b declared `operator -`, rewritten
    /// into the call it stands for. Null when neither side has one, which is
    /// every ordinary expression.
    ///
    /// The search is the type's own methods and its bases, left side first --
    /// C# gathers the candidates from both operand types and then overloads
    /// between them, which differs only where both sides declare an operator
    /// for the same pair, and there the first is the one either would pick.
    /// </summary>
    private Type? UserOperator(BinaryExpr b, Type l, Type r)
    {
        if (OperatorMethodName(b.Op) is not string name)
        {
            return null;
        }

        if (l.ArrayRank > 0 || r.ArrayRank > 0 || l.Prim == Prim.String || r.Prim == Prim.String
            || l.Prim == Prim.NullLiteral || r.Prim == Prim.NullLiteral)
        {
            return null;
        }

        if (l.Symbol is null && r.Symbol is null)
        {
            return null;
        }

        MethodSymbol? found = Operator(l.Symbol, name, l, r) ?? Operator(r.Symbol, name, l, r);

        if (found is null)
        {
            return null;
        }

        // The operator's owner by its full name, wherever it lives. Only
        // types under System could be reached by the helper-call shape
        // before, which is where every operator the compiler itself needed
        // happened to be; a program's own struct is anywhere.
        CallExpr call = new()
        {
            Target = new MemberExpr { Target = Qualified(found.Owner.Key, b), Name = name, Line = b.Line, Col = b.Col },
            Line = b.Line, Col = b.Col,
        };
        call.Args.Add(b.Left);
        call.Args.Add(b.Right);

        _r.Rewrites[b] = call;
        return CheckExpr(call);
    }

    /// <summary>A dotted type name as the member chain a program would write for it.</summary>
    private static Expr Qualified(string dotted, Node at)
    {
        string[] parts = dotted.Split('.');
        Expr chain = new NameExpr { Name = parts[0], Line = at.Line, Col = at.Col };
        for (int i = 1; i < parts.Length; i++)
        {
            chain = new MemberExpr { Target = chain, Name = parts[i], Line = at.Line, Col = at.Col };
        }
        return chain;
    }

    /// <summary>The operator of that name on this type whose two parameters
    /// accept these operands, or null.</summary>
    private MethodSymbol? Operator(TypeSymbol? holder, string name, Type l, Type r)
    {
        if (holder is null)
        {
            return null;
        }

        foreach (MethodSymbol m in holder.FindMethods(name))
        {
            if (m.Static && m.Params.Count == 2
                && Convertible(l, m.Params[0].Type) && Convertible(r, m.Params[1].Type))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>The metadata name of a binary operator, which is what the
    /// parser gave the declaration. Null for the ones that cannot be
    /// overloaded (&amp;&amp;, ||, ?? and the rest are resolved by the
    /// language itself).</summary>
    private static string? OperatorMethodName(BinOp op)
    {
        return op switch
        {
            BinOp.Add => "op_Addition",
            BinOp.Sub => "op_Subtraction",
            BinOp.Mul => "op_Multiply",
            BinOp.Div => "op_Division",
            BinOp.Rem => "op_Modulus",
            BinOp.And => "op_BitwiseAnd",
            BinOp.Or => "op_BitwiseOr",
            BinOp.Xor => "op_ExclusiveOr",
            BinOp.Shl => "op_LeftShift",
            BinOp.Shr => "op_RightShift",
            BinOp.Eq => "op_Equality",
            BinOp.Ne => "op_Inequality",
            BinOp.Lt => "op_LessThan",
            BinOp.Gt => "op_GreaterThan",
            BinOp.Le => "op_LessThanOrEqual",
            BinOp.Ge => "op_GreaterThanOrEqual",
            _ => null,
        };
    }

    private Type CheckBinary(BinaryExpr b)
    {
        // THE RIGHT SIDE OF '&&' KNOWS WHAT THE LEFT SIDE PROVED.
        //
        // `x != null && x.Kind == K` is the commonest line in C# there is, and
        // it only runs the right side when the left was true -- so within it,
        // x is not null. Checking both halves in the same state reported that x
        // may be null in the one place it provably is not.
        //
        // It matters more than it looks, because `is { Kind: K }` lowers to
        // exactly this shape: the null test and the member read, joined by &&.
        if (b.Op == BinOp.AndAlso)
        {
            Type left = CheckExpr(b.Left);
            List<Sym> proved = Assume(b.Left, true);
            Type right = CheckExpr(b.Right);

            Forget(proved);

            if (!left.IsError && !right.IsError && (left.Prim != Prim.Bool || right.Prim != Prim.Bool))
            {
                Error(b, $"'&&' needs two 'bool' operands, not '{left}' and '{right}'");
            }
            return Type.Bool;
        }

        // AND THE RIGHT SIDE OF '||' KNOWS THE LEFT SIDE WAS FALSE, which is
        // the same rule read the other way round and is just as common:
        // `!map.TryGetValue(k, out V? v) || v.X is null` reads v only where the
        // lookup said it had one.
        if (b.Op == BinOp.OrElse)
        {
            Type first = CheckExpr(b.Left);
            List<Sym> knowing = Assume(b.Left, false);
            Type second = CheckExpr(b.Right);

            Forget(knowing);

            if (!first.IsError && !second.IsError
                && (first.Prim != Prim.Bool || second.Prim != Prim.Bool))
            {
                Error(b, $"'||' needs two 'bool' operands, not '{first}' and '{second}'");
            }
            return Type.Bool;
        }

        Type l = CheckExpr(b.Left);

        // WHAT THE LEFT SIDE IS, IS WHAT THE RIGHT SIDE HAS TO BE. `isDefined
        // ?? (_ => false)` is a lambda with nothing else to tell it its type,
        // and C# target-types the right operand of `??` from the left -- which
        // is the only reading that can work, since the whole expression has to
        // be one type.
        Type? beforeFallback = _wanted;

        if (b.Op == BinOp.Coalesce && !l.IsError)
        {
            _wanted = l.IsNullableValue ? l.Underlying : l.AsNonNullable();
        }

        Type r = CheckExpr(b.Right);
        _wanted = beforeFallback;

        if (l.IsError || r.IsError)
        {
            return Type.Error;
        }

        AdoptUnsignedConstant(b.Left, ref l, b.Right, ref r);

        // AN OPERATOR THE TYPE DECLARED ITSELF. `later - earlier` on two
        // DateTimes, `span1 + span2`, `a < b` on a Version: C# resolves these
        // to a static method the type wrote, named op_Subtraction and its
        // fellows, and so does this. It is tried before the built-in rules
        // decide the operands are not numbers, and only when one side is a
        // class, struct or enum -- so nothing about `int + int` or `"a" + x`
        // passes through here.
        if (UserOperator(b, l, r) is Type byOperator)
        {
            return byOperator;
        }

        switch (b.Op)
        {
            case BinOp.AndAlso:
            case BinOp.OrElse:
                if (l.Prim != Prim.Bool || r.Prim != Prim.Bool)
                {
                    Error(b, $"'{(b.Op == BinOp.AndAlso ? "&&" : "||")}' needs two 'bool' operands, not '{l}' and '{r}'");
                }
                return Type.Bool;

            case BinOp.Coalesce:
                // `??` ON SOMETHING ALREADY NON-NULL IS ALLOWED, and C# is the
                // reason: it warns and compiles. This refused, and the shape it
                // refused is the ordinary one -- `source ?? throw new
                // ArgumentNullException(nameof(source))` guards a parameter
                // whose type says it cannot be null against a caller that had
                // no such checking. This compiler's own Lexer.cs opens with it.
                // A NULLABLE VALUE TYPE ON THE LEFT GIVES THE TYPE IT HOLDS.
                // `loadBase ?? 0x10000` is a uint and not a `uint?`: C# says
                // the right side is converted to the left's underlying type and
                // the whole expression is that type, which is the only reading
                // that makes `??` a way of supplying the missing value.
                if (l.IsNullableValue && !r.Nullable && !r.IsNullableValue
                    && Convertible(r, l.Underlying))
                {
                    return l.Underlying;
                }

                return r.Nullable ? r : r.AsNonNullable();

            case BinOp.Eq:
            case BinOp.Ne:
                // A NAME THAT IS A TYPE, COMPARED, IS A TYPE TEST.
                //
                // `sym is not (MethodGroupSym or CapturedMethodGroupSym)` is a
                // group of ALTERNATIVES, and the parser cannot tell one from a
                // group of constants -- both are names in brackets joined by
                // `or` -- so it writes the equality either way and only the
                // checker knows which the names are. Written out as the test it
                // means, which is the same rewrite a bare constant name in a
                // pattern already gets, the other way round.
                if (b.Right is NameExpr spelt && !spelt.Name.Contains('.')
                    && _r.Resolved.TryGetValue(b.Right, out Sym? asType) && asType is TypeNameSym)
                {
                    IsExpr test = new()
                    {
                        Operand = b.Left,
                        Type = new TypeRef { Name = spelt.Name, Line = spelt.Line, Col = spelt.Col },
                        Line = b.Line, Col = b.Col, File = b.File,
                    };

                    Expr whole = b.Op == BinOp.Eq
                               ? test
                               : new UnaryExpr
                                 {
                                     Op = UnOp.Not, Operand = test,
                                     Line = b.Line, Col = b.Col, File = b.File,
                                 };

                    _r.Rewrites[b] = whole;
                    return CheckExpr(whole);
                }

                if (l.Prim == Prim.NullLiteral || r.Prim == Prim.NullLiteral)
                {
                    Type other = l.Prim == Prim.NullLiteral ? r : l;

                    // A PATTERN'S OWN NULL TEST OVER A VALUE TYPE IS NOT A
                    // TEST. C# writes no such test there -- a struct is always
                    // there -- so the answer it always gives is written down
                    // instead, and the members are checked as the pattern says.
                    if (b.PatternNullTest && !other.IsReference && !other.IsNullableValue
                        && other.Prim != Prim.Any && other.ParamName is null && !other.IsError)
                    {
                        LiteralExpr always = new()
                        {
                            Kind = Lit.Bool, Text = b.Op == BinOp.Ne ? "true" : "false",
                            IntValue = b.Op == BinOp.Ne ? 1 : 0,
                            Line = b.Line, Col = b.Col, File = b.File,
                        };

                        _r.Rewrites[b] = always;
                        return Type.Bool;
                    }

                    // 'object' COMPARES AGAINST NULL. It is the root reference
                    // type in C# and it is a machine word here, which is the
                    // same thing said twice -- a word that may hold an address,
                    // and zero is not one. It is not IsReference only because
                    // that answers "is this definitely a pointer", and an
                    // object may be holding a long.
                    if (!other.IsReference && !other.IsNullableValue
                        && other.Prim != Prim.Any && other.ParamName is null && !other.IsError)
                    {
                        Error(b, $"'{other}' is a value type and can never be null");
                    }
                    return Type.Bool;
                }

                if (l.IsNumeric && r.IsNumeric)
                {
                    if (!NumericRules.TryBinary(l, r, out _))
                    {
                        Error(b, $"'{l}' and '{r}' have no implicit common numeric type");
                    }
                }
                else if (!Convertible(l, r) && !Convertible(r, l))
                {
                    Error(b, $"'{l}' and '{r}' cannot be compared");
                }
                return Type.Bool;

            case BinOp.Lt:
            case BinOp.Gt:
            case BinOp.Le:
            case BinOp.Ge:
                // Strings order by their bytes. C# makes you call CompareTo for
                // this; sorting a list of names is common enough, and the
                // ordering unsurprising enough, that the operator is worth it.
                if (l.Prim == Prim.String && r.Prim == Prim.String)
                {
                    return Type.Bool;
                }

                if (!l.IsNumeric || !r.IsNumeric)
                {
                    Error(b, $"'{l}' and '{r}' cannot be ordered");
                }
                else if (!NumericRules.TryBinary(l, r, out _))
                {
                    Error(b, $"'{l}' and '{r}' have no implicit common numeric type");
                }
                return Type.Bool;

            case BinOp.Add when l.Prim == Prim.String || r.Prim == Prim.String:
                return Type.String;

            case BinOp.And:
            case BinOp.Or:
            case BinOp.Xor:
                if (l.Prim == Prim.Bool && r.Prim == Prim.Bool)
                {
                    // `bool?` is lifted too, three-valued as C# has it:
                    // false & null is false, true | null is true.
                    return l.IsNullableValue || r.IsNullableValue ? Type.Bool.AsNullable() : Type.Bool;
                }
                if (!l.IsInteger || !r.IsInteger)
                {
                    Error(b, $"'{b.Op}' needs integer operands, not '{l}' and '{r}'");
                    return Type.Error;
                }

                // COMBINING TWO OF AN ENUM GIVES THAT ENUM, which is C#'s rule
                // and the whole point of a `[Flags]` one: `Read | Run` is a
                // Ways, not the 5 it is made of. Promoting it to int was
                // invisible while an enum printed as its number and is not
                // now -- it is the difference between "Read, Run" and "5".
                if (l.Symbol is { Kind: TypeKind.Enum } bits && ReferenceEquals(bits, r.Symbol))
                {
                    return new Type { Prim = bits.EnumUnderlying, Symbol = bits };
                }
                goto case BinOp.Add;

            case BinOp.Rem when l.IsFloat || r.IsFloat:
                Error(b, "floating-point remainder is not supported by CORS-C#");
                return Type.Error;

            case BinOp.Shl:
            case BinOp.Shr:
                if (!l.IsInteger || !r.IsInteger)
                {
                    Error(b, $"shifts need integers, not '{l}' and '{r}'");
                    return Type.Error;
                }
                if (l.IsNullableValue || r.IsNullableValue) return Promote(l.Underlying).AsNullable();
                return Promote(l);

            case BinOp.Add:
            case BinOp.Sub:
            case BinOp.Mul:
            case BinOp.Div:
            case BinOp.Rem:
            default:
            {
                if (!l.IsNumeric || !r.IsNumeric)
                {
                    Error(b, $"'{l}' and '{r}' are not both numbers");
                    return Type.Error;
                }

                // LIFTED, as C# lifts every arithmetic operator: an `int?`
                // plus an `int` is an `int?`, null when either is.
                if (l.IsNullableValue || r.IsNullableValue)
                {
                    Type lu = l.IsNullableValue ? l.Underlying : l, ru = r.IsNullableValue ? r.Underlying : r;
                    if (!NumericRules.TryBinary(lu, ru, out Type liftedType))
                    {
                        Error(b, $"'{l}' and '{r}' have no implicit common numeric type");
                        return Type.Error;
                    }
                    return liftedType.AsNullable();
                }

                RequireNonNull(l, b.Left, "use");
                RequireNonNull(r, b.Right, "use");

                if (!NumericRules.TryBinary(l, r, out Type promoted))
                {
                    Error(b, $"'{l}' and '{r}' have no implicit common numeric type");
                    return Type.Error;
                }
                return promoted;
            }
        }
    }
}
