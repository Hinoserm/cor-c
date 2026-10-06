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
    private readonly Action<string, int>? _requireOverrides;
    private readonly Metadata.DeclarationBatch _declarationBatch = new();
    private readonly IReadOnlyDictionary<(string Name, int Arity), int>? _indexedInterfaces;
    private readonly IReadOnlySet<(string Name, int Arity)>? _libraryInterfaces;
    /// <summary>
    /// A kernel module's compile: every interface family the KERNEL'S index
    /// holds. A family outside it is the module's own, and is numbered in a
    /// tier of its own above the kernel's classes (see the numbering), so
    /// that declaring one never moves a slot the kernel was built with.
    /// Null for anything that is not a module.
    /// </summary>
    private readonly IReadOnlySet<(string Name, int Arity)>? _kernelInterfaces;

    /// <summary>
    /// Whether a source file belongs to the compiler's own libraries. Set by
    /// the driver; when unset every declaration counts as library, which is
    /// the numbering every image used before projects could add interfaces.
    /// </summary>
    public static Func<string, bool>? LibrarySource { get; set; }

    /// <summary>Whether the code being bound is the runtime's or the class library's, which build strings.</summary>
    private bool BuildsStrings()
    {
        string? path = _thisType?.Decl?.SourcePath ?? _scope?.Decl?.SourcePath;
        return path is null || LibrarySource is null || LibrarySource(path);
    }

    /// <summary>Where the library region ends: the first slot a library class's own virtuals were given.</summary>
    private int _librarySlots;

    /// <summary>Slots kept above the library's interface region for the library's class virtuals; see the numbering.</summary>
    private const int LibraryClassReserve = 128;

    /// <summary>Where a project class's own virtuals start: the end of the project's interface region, the same in every unit.</summary>
    private int _projectClassSlots;

    /// <summary>Slots kept above the project's interface region for its class virtuals, below this unit's own interfaces.</summary>
    private const int ProjectClassReserve = 128;

    private TypeSymbol? _thisType;

    /// <summary>Inside unchecked(...) or an unchecked block: a constant cast may wrap (CS0221).</summary>
    private int _uncheckedDepth;

    private MethodSymbol? _method;
    private readonly List<LocalScope> _scopes = new();

    private sealed class LocalScope : Dictionary<string, Sym>
    {
        // Completed children still forbid a later declaration in this scope,
        // but never forbid reuse in a sibling. These are names, not bindings:
        // lookup and definite assignment retain their declaration-time rules.
        public readonly HashSet<string> NestedNames = new(StringComparer.Ordinal);
        public bool FunctionBoundary;
        /// <summary>Kept by a local function to bind its body later: never reused.</summary>
        public bool Captured;

        public LocalScope(bool functionBoundary) : base(StringComparer.Ordinal)
        {
            FunctionBoundary = functionBoundary;
        }
    }

    // SCOPES ARE REUSED: a block's scope, emptied when it closes, is the next
    // block's -- two tables for every block the binder checked were most of
    // what PushScope cost. One a local function keeps is never reused.
    private readonly Stack<LocalScope> _spareScopes = new();

    /// Calls whose receiver has already been moved into the argument list. A
    /// call is checked once per generic instantiation, and inserting twice
    /// would pass the string as its own first argument.
    private readonly HashSet<CallExpr> _receiverAdded = new(ReferenceEqualityComparer.Instance);
    private int _nextSlot;
    private int _maxSlot;
    private int _loopDepth;

    /// <summary>
    /// What is definitely assigned at the breaks out of each loop or switch
    /// section being checked, innermost last: the intersection of every
    /// break's state, null while none has been met. After a loop whose
    /// condition is always true, that is what is assigned (C#'s rule: its
    /// only way out is a break).
    /// </summary>
    private readonly List<HashSet<LocalSym>?> _breaks = new();

    private void EnterBreakable()
    {
        _loopDepth++;
        _breaks.Add(null);
    }

    private HashSet<LocalSym>? LeaveBreakable()
    {
        _loopDepth--;
        HashSet<LocalSym>? broke = _breaks[^1];
        _breaks.RemoveAt(_breaks.Count - 1);
        return broke;
    }

    /// <summary>After a loop that only a break leaves: what every break had assigned.</summary>
    private void AfterEndlessLoop(Expr? cond, HashSet<LocalSym>? broke)
    {
        if (broke is null || cond is not null && !(cond is LiteralExpr { Kind: Lit.Bool, IntValue: 1 }))
        {
            return;
        }
        _assigned.Clear();
        _assigned.UnionWith(broke);
    }
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
    /// <summary>
    /// Where the library's global types live when a program's type takes
    /// one's name: .NET's namespace for them (MoveToSystem).
    /// </summary>
    private const string LibraryHome = "System";

    /// <summary>The keys of library types moved into System: old key to new.</summary>
    private readonly Dictionary<string, string> _movedToSystem = new(StringComparer.Ordinal);

    /// <summary>
    /// A library type the program has taken the name of, moved into System
    /// with every library type nested in it -- declared under its new key,
    /// and the old key left to the program's.
    /// </summary>
    private void MoveToSystem(TypeDecl library)
    {
        string before = TypeKey(library);
        library.MovedToSystem = true;
        library.Outer = library.Outer is null ? LibraryHome : LibraryHome + "." + library.Outer;
        if (library.Namespace.Length == 0) library.Namespace = LibraryHome;
        string after = TypeKey(library);
        _movedToSystem[before] = after;

        if (_r.Types.TryGetValue(before, out TypeSymbol? held) && ReferenceEquals(held.Decl, library))
        {
            _r.Types.Remove(before);
        }
        TypeSymbol moved = new() { Name = library.Name, Key = after, Kind = library.Kind, Decl = library };
        if (library.TypeParams.Count > 0) AddNames(moved.WritableTypeParamNames, library.TypeParams);
        _r.Types[after] = moved;

        // Its nested types declared already go with it; the rest are moved as
        // they are met (MovedPath).
        foreach (string nested in _r.Types.Keys.Where(k => k.StartsWith(before + ".", StringComparison.Ordinal)).ToList())
        {
            TypeSymbol inner = _r.Types[nested];
            if (inner.Decl is not { FromLibrary: true } innerDecl || innerDecl.Outer is null)
            {
                continue;
            }
            _r.Types.Remove(nested);
            innerDecl.Outer = MovedPath(innerDecl.Outer) ?? innerDecl.Outer;
            if (innerDecl.Namespace.Length == 0) innerDecl.Namespace = LibraryHome;
            TypeSymbol movedInner = new() { Name = innerDecl.Name, Key = TypeKey(innerDecl), Kind = innerDecl.Kind, Decl = innerDecl };
            if (innerDecl.TypeParams.Count > 0) AddNames(movedInner.WritableTypeParamNames, innerDecl.TypeParams);
            _r.Types[movedInner.Key] = movedInner;
        }
    }

    /// <summary>
    /// How the checker names a library type it makes something of -- `^1` is
    /// System.Index, `a..b` System.Range -- whatever a program calls its own:
    /// the moved key when the program has taken the name (MoveToSystem).
    /// </summary>
    private string LibraryType(string name)
        => _r.Types.TryGetValue(LibraryHome + "." + name, out TypeSymbol? moved) && moved.Decl is { MovedToSystem: true }
            ? LibraryHome + "." + name : name;

    /// <summary>A path inside a type moved into System, rewritten to its new place; null if it is not.</summary>
    private string? MovedPath(string path)
    {
        foreach ((string before, string after) in _movedToSystem)
        {
            if (path == before)
            {
                return after;
            }
            if (path.StartsWith(before + ".", StringComparison.Ordinal))
            {
                return after + path.Substring(before.Length);
            }
        }
        return null;
    }

    internal static string TypeKey(TypeDecl d)
    {
        string simple = d.TypeParams.Count > 0 ? Arity(d.Name, d.TypeParams.Count) : d.Name;

        return d.Outer is null ? simple : d.Outer + "." + simple;
    }

    /// <summary>
    /// The type a nested name is written inside, or null once the walk reaches
    /// the top level: `A.B.C` gives `A.B`, and `A` gives null.
    /// </summary>
    private string? Enclosing(string key)
    {
        // REMEMBERED: every type lookup walks outwards namespace by namespace,
        // and each step cut a new string of the same few parents.
        if (_enclosing.TryGetValue(key, out string? known)) return known;
        int cut = key.LastIndexOf('.');
        string? parent = cut < 0 ? null : key[..cut];
        _enclosing[key] = parent;
        return parent;
    }

    private readonly Dictionary<string, string?> _enclosing = new(StringComparer.Ordinal);

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
    /// <summary>
    /// Whether a bare `Type` written here is System.Type, by C#'s walk:
    /// outwards from where it was written, namespace by namespace, the types
    /// declared at each step and then the using directives written there --
    /// and System.Type is a type of the namespace System like any other. So
    /// inside Corsac.Lang the compiler's own Type is found first, and in a
    /// file that imports System and nothing declaring a Type, or in the
    /// class library, it is System.Type. Before, it was System.Type only
    /// where no Type existed anywhere in the program.
    /// </summary>
    private bool MeansSystemType()
    {
        const string name = "Type";
        TypeDecl? written = (_scope ?? _thisType)?.Decl;
        string within = _member?.Scope != null ? _member.Namespace : written?.Namespace ?? "";
        FileScope? file = _member?.Scope ?? written?.Scope;
        bool ImportsSystem(string at) => file != null && file.Imports.Any(i => i.In == at && i.Namespace == "System");
        string?[] outwards = { (_scope ?? _thisType)?.Key, within.Length == 0 ? null : within };

        foreach (string? from in outwards)
        {
            for (string? at = from; at is not null; at = Enclosing(at))
            {
                if (TypeCandidate(at + "." + name, out _)) return false;
                if (at == "System") return true;
                if (file != null && Imported(file, at, name, out _)) return false;
                if (ImportsSystem(at)) return true;
            }
        }

        if (TypeCandidate(name, out _)) return false;
        if (file != null && Imported(file, "", name, out _)) return false;
        if (ImportsSystem("")) return true;
        return !Sole(name, out _);
    }

    private bool FindType(string name, out TypeSymbol? sym)
    {
        TypeDecl? written = (_scope ?? _thisType)?.Decl;
        string within = _member?.Scope != null ? _member.Namespace : written?.Namespace ?? "";
        FileScope? file = _member?.Scope ?? written?.Scope;
        // A SPECIALISATION'S OWN ARGUMENTS ARE THE TYPES THEY NAMED WHERE THEY
        // WERE WRITTEN. The monomorphiser spells an argument out where it was
        // written, so a simple one is a global type's -- a namespaced one
        // would be dotted -- but the copy is read from the template's scope:
        // a program's `Version` spliced into ReadOnlyCollection<T>, declared
        // in a namespace beneath System, was read there as System.Version.
        if (written is { Specialised: true, TemplateArgs.Count: > 0 } specialised && !name.Contains('.')
            && NamesTemplateArg(specialised, name) && TypeCandidate(name, out sym) && sym is not null)
        {
            return true;
        }

        // THE LIBRARY'S GLOBAL CODE FINDS ITS OWN TYPE FIRST where a program's
        // took the name and the library's was moved into System (MoveToSystem)
        // -- judged by the declaration, which keeps the move after the pass
        // that made it. Not in a specialisation, whose arguments were written
        // where it was used.
        // By its two parts where the name is simple (TryGetWithin): asked for
        // every type name the library's global code mentions, the key spelt
        // to be looked up was the binder's largest run of strings.
        if (written is { FromLibrary: true, Specialised: false } && within.Length == 0
            && (name.IndexOf('.') < 0 ? _r.Types.TryGetWithin(LibraryHome, name, out TypeSymbol? moved)
                : _r.Types.TryGetValue(LibraryHome + "." + name, out moved))
            && moved!.Decl is { MovedToSystem: true })
        {
            if (!BindingElsewhere && !_namingOnly) moved.Used = true;
            sym = moved;
            return true;
        }

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
        //
        // The two starting points are held in locals, not an array: this is
        // asked for every type name every body mentions.
        string? fromType = (_scope ?? _thisType)?.Key, fromNamespace = within.Length == 0 ? null : within;

        for (int pass = 0; pass < 2; pass++)
        {
            for (string? at = pass == 0 ? fromType : fromNamespace; at is not null; at = Enclosing(at))
            {
                if (TypeCandidateIn(at, name, out sym))
                {
                    return true;
                }

                if (file != null && Imported(file, at, name, out sym))
                {
                    return true;
                }
            }
        }

        // A LIBRARY'S DECLARATION NEVER SEES THE PROGRAM'S TYPES: compiled on
        // its own it had none of them, and its global and imported names
        // meant its own. A program's global `Rectangle` read back into
        // Control.Bounds -- System.Drawing's, by the library's using -- gave
        // the program's unit another layout of the method than the library's.
        // Compiled with the program or read from another unit's declarations
        // alike: a library is its own assembly to C#, and a program's global
        // `Color` is no System.Drawing.Color to it. Not a copy of one of its
        // generic methods for the program's types (LocalCopy) --
        // OfType<Field>, Where<Op> -- which names them.
        // The CLASS library, by its sources: a unit compiled with --lib marks
        // all its files library, and a project's units -- the compiler's own
        // -- name one another.
        bool libraryOwn = written is { FromLibrary: true, Specialised: false } && _member is not { LocalCopy: true }
                          && IsClassLibrary(written);
        if (TypeCandidate(name, out sym) && !(libraryOwn && sym?.Decl is { } candidate && IsProgramSource(candidate)))
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

    /// <summary>Whether a specialisation has a template argument written as this name.</summary>
    private static bool NamesTemplateArg(TypeDecl specialised, string name)
    {
        foreach (TypeRef a in specialised.TemplateArgs)
        {
            if (a.Name == name) return true;
        }
        return false;
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
                or Prim.Char or Prim.String or Prim.Any or Prim.Void;

    /// <summary>Whether the declaration being bound is somebody else's: an Elsewhere type's, or a member whose code another unit has.</summary>
    private bool BindingElsewhere => _thisType?.Decl?.Elsewhere == true || _member?.OwnedImplementation == false
        // The tuple namings of every file, this unit's and the others', read
        // for their element names alone: a type a tuple names is asked for
        // where the code that makes or reads the tuple is bound.
        || _namingTuples
        // And the member signatures of a specialisation (List<Control>, made
        // because a library signature names it): its types are its arguments,
        // and a unit that writes List<Control> itself has already asked for
        // Control where it wrote it.
        || _thisType is null && _scope?.Decl is { Elsewhere: true } or { Specialised: true };

    private bool _namingTuples;

    /// <summary>
    /// TypeCandidate for `within + "." + name`, without making that string
    /// unless it is needed: a dotted name, or one not there whose absence a
    /// declaration index must hear of.
    /// </summary>
    private bool TypeCandidateIn(string within, string name, out TypeSymbol? symbol)
    {
        bool plain = name.IndexOf('.') < 0;
        if (plain && _r.Types.TryGetWithin(within, name, out symbol))
        {
            if (!BindingElsewhere && !_namingOnly) symbol!.Used = true;
            return true;
        }
        if (plain && _requireDeclaration is null)
        {
            symbol = null;
            return false;
        }
        // A NAME ASKED IN A SCOPE THAT HAS NO SUCH TYPE, NOR AN INDEX ONE TO
        // DEMAND, is asked again from every expression that mentions it, and
        // each time spelt `within.name` to be told no -- the binder's largest
        // run of string building. Remembered by its two parts, scope first,
        // the second asking costs two lookups and no string.
        // Kept here only for the global scope: a name in any other is in
        // _demandedWithin as well (below), which answers the same and holds it
        // for the whole unit, where these were a set a scope for every binding.
        if (plain && _absentWithin.TryGetValue(within, out HashSet<string>? absent) && absent.Contains(name))
        {
            symbol = null;
            return false;
        }
        // A DOTTED NAME BY ITS LAST PART TOO: `within.Lang.Ir.Block` is the
        // table's `within.Lang.Ir` and `Block`, and that scope is spelt once
        // a pair and remembered (DottedParts) -- qualified names asked from
        // every scope outwards were twice the plain ones' strings.
        if (!plain && within.Length > 0)
        {
            (string scope, string tail) = DottedParts(within, name);
            if (_r.Types.TryGetWithin(scope, tail, out symbol))
            {
                if (!BindingElsewhere && !_namingOnly) symbol!.Used = true;
                return true;
            }
        }
        // AND WITHOUT THE STRING WHERE IT COULD ONLY BE TOLD NO: a simple name
        // the split table did not have is not in the table by its full key
        // either (TryGetWithin splits that key the same way), so all the key
        // was for is the demand -- none while only naming, and none for a
        // name already demanded (TypeCandidate's _demanded, kept here by its
        // parts). Spelt for nothing, these were the most strings a unit's
        // binding made: a hundred megabytes for one compiler unit.
        if (within.Length > 0 && (_namingOnly || _demandedWithin.Contains((within, name))))
        {
            symbol = null;
            return false;
        }
        bool demandedBefore = _declarationBatch.Any;
        bool found = TypeCandidate(within + "." + name, out symbol);
        if (!found && within.Length > 0 && !_namingOnly) _demandedWithin.Add((within, name));
        if (!found && plain && within.Length == 0 && !_namingOnly && (demandedBefore || !_declarationBatch.Any)) Absent(within, name);
        return found;
    }

    /// <summary>`within + "." + name` split at its last dot, spelt once a pair.</summary>
    private (string Scope, string Tail) DottedParts(string within, string name)
    {
        if (_dottedParts.TryGetValue((within, name), out var parts)) return parts;
        int dot = name.LastIndexOf('.');
        parts = (string.Concat(within, ".", name.AsSpan(0, dot)), name[(dot + 1)..]);
        _dottedParts[(within, name)] = parts;
        return parts;
    }

    private readonly Dictionary<(string Within, string Name), (string Scope, string Tail)> _dottedParts = new();

    private void Absent(string within, string name)
    {
        if (!_absentWithin.TryGetValue(within, out HashSet<string>? absent)) _absentWithin[within] = absent = new HashSet<string>(StringComparer.Ordinal);
        absent.Add(name);
    }

    private readonly Dictionary<string, HashSet<string>> _absentWithin = new(StringComparer.Ordinal);

    /// <summary>The names TypeCandidateIn has demanded, by their two parts (TypeCandidate's _demanded).</summary>
    private HashSet<(string Within, string Name)> _demandedWithin = new();

    private bool TypeCandidate(string key, out TypeSymbol? symbol)
    {
        if (_r.Types.TryGetValue(key, out symbol))
        {
            // ASKED FOR, not merely present. This is what tells the managed
            // layout which types this unit has an opinion about -- asked for
            // by THIS unit's code, that is: binding the declarations of a
            // source given only for them (--ref, TypeDecl.Elsewhere) asks
            // about the types they mention, and a program then described
            // every library type the library's own signatures name.
            if (!BindingElsewhere && !_namingOnly) symbol.Used = true;
            return true;
        }
        // A MISSING DECLARATION IS RECORDED, NOT RAISED. Unwinding here threw
        // the whole unit away for one name, and a single dispatcher naming a
        // dozen kernel types therefore cost a dozen rebuilds. Checking carries
        // on with the name unresolved instead, which reports nonsense for the
        // rest of this pass -- and that is fine, because the pass is discarded
        // the moment anything was recorded. See DeclarationBatch.
        if (_namingOnly) return false;
        // ONCE A NAME, AND A COPY TO THE DEMAND, which keeps it: the key is
        // mostly a name built to be looked up (`ns + "." + name`) and dropped,
        // and handed on, it was the collector's on every path. A name asked
        // again in this pass is already in the batch, or loaded and still
        // missing: asking again does nothing either way.
        if (_requireDeclaration is null || _demanded.Contains(key)) return false;
        string kept = key.Substring(0);
        _demanded.Add(kept);
        try { _requireDeclaration(kept); }
        catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
        return false;
    }

    private HashSet<string> _demanded = new(StringComparer.Ordinal);

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
    /// <summary>
    /// What the using directives of one namespace declaration make this name
    /// mean, asked once a pass for each file, level and name (ImportedNow):
    /// asked at every enclosing level of every name a body mentions, it built
    /// `namespace.name` for each import and looked it up, 2.6% of a native
    /// self-compile. What it finds cannot change within a pass -- a declaration
    /// a pass demands arrives in the next one, with a binder of its own -- so
    /// the answer is kept, and a type found is marked asked for as the lookup
    /// would have marked it. Not while a pass is still demanding, when a miss
    /// may be a declaration on its way.
    /// </summary>
    private bool Imported(FileScope file, string at, string name, out TypeSymbol? sym)
    {
        if (!_importedSeen.TryGetValue(file, out Dictionary<(string, string), TypeSymbol?>? known))
            _importedSeen[file] = known = new Dictionary<(string, string), TypeSymbol?>();
        if (known.TryGetValue((at, name), out sym))
        {
            if (sym is not null && !BindingElsewhere && !_namingOnly) sym.Used = true;
            return sym is not null;
        }
        bool demandedBefore = _declarationBatch.Any;
        bool found = ImportedNow(file, at, name, out sym);
        if (!_namingOnly && !demandedBefore && !_declarationBatch.Any) known[(at, name)] = found ? sym : null;
        return found;
    }

    private readonly Dictionary<FileScope, Dictionary<(string, string), TypeSymbol?>> _importedSeen = new(ReferenceEqualityComparer.Instance);

    private bool ImportedNow(FileScope file, string at, string name, out TypeSymbol? sym)
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
            // By its parts (TypeCandidateIn): a name and each namespace the
            // file imports, asked from every mention, spelt to be told no.
            if (import.In != at
                || !TypeCandidateIn(import.Namespace, name, out TypeSymbol? found))
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
    /// A type entered in the unit's table, and in the simple-name index as it
    /// goes when that is up to date: rebuilt from the whole table each time
    /// the table grew, the index was a walk of every type for every tuple,
    /// closure and specialisation the checker made.
    /// </summary>
    private void RegisterType(string key, TypeSymbol type)
    {
        bool current = _soleAt == _r.Types.Count && !_r.Types.ContainsKey(key);
        _r.Types[key] = type;
        if (current)
        {
            NoteSole(key, type);
            _soleAt = _r.Types.Count;
        }
    }

    private void NoteSole(string key, TypeSymbol type)
    {
        if (Enclosing(key) is null)
        {
            return;
        }
        // Ambiguous is recorded as null rather than dropped, so a second one
        // cannot be undone by a third.
        _sole[type.Name] = _sole.ContainsKey(type.Name) ? null : type;
        if (IsLibraryType(type))
            _soleLibrary[type.Name] = _soleLibrary.ContainsKey(type.Name) ? null : type;
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
            _soleLibrary.Clear();

            foreach ((string key, TypeSymbol type) in _r.Types)
            {
                NoteSole(key, type);
            }

            _soleAt = _r.Types.Count;
        }

        // THE LIBRARY SEES ONLY THE LIBRARY. It is another assembly in .NET,
        // compiled before any program existed, and no name in it can mean a
        // type a program declared in a namespace of its own: the class
        // library's `Type` is System.Type, not the compiler's Corsac.Lang.Type
        // -- which, as the one Type in the program, the flat rule handed to
        // every library signature that said Type.
        return (BindingLibrary ? _soleLibrary : _sole).TryGetValue(name, out sym) && sym is not null;
    }

    /// <summary>Library types by simple name, as <see cref="_sole"/> is for every type.</summary>
    private readonly Dictionary<string, TypeSymbol?> _soleLibrary = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether what is being bound is the class library's own code: a
    /// declaration from a library source, and not a specialisation, which is
    /// checked from where it was made rather than where its template was.
    /// </summary>
    private bool BindingLibrary
        => (_scope ?? _thisType)?.Decl is { Specialised: false, SourcePath: string path }
           && LibrarySource is not null && LibrarySource(path);

    /// <summary>Nested types by simple name; null where more than one shares it.</summary>
    private readonly Dictionary<string, TypeSymbol?> _sole = new(StringComparer.Ordinal);

    /// <summary>How large the table of types was when <see cref="_sole"/> was built.</summary>
    private int _soleAt = -1;

    /// <summary>Whether a name resolves to a type from where it was written.</summary>
    private bool IsTypeName(string name) => FindType(name, out _);

    /// <summary>
    /// Whether a pattern's bare name that is both a constant and a type is the
    /// constant. C# looks through the enclosing types' members -- a constant
    /// and a nested type alike -- before any namespace or using directive, so
    /// a constant in scope beats a type reached only through a namespace:
    /// `case Process:` over a class's own `const int Process` is 5, not a test
    /// for System.Diagnostics.Process, and NtQuerySystemInformation's switch
    /// took its `case Exception:` for a type test and read the class number as
    /// an object. Between two members, the nearer enclosing type's wins.
    /// </summary>
    private bool ConstantHidesType(string name)
    {
        if (!FindType(name, out TypeSymbol? type) || type is null) return true;
        int dot = type.Key.LastIndexOf('.');
        string? container = dot < 0 ? null : type.Key.Substring(0, dot);
        for (TypeSymbol? at = _scope ?? _thisType; at is not null; at = Outer(at))
        {
            if (container == at.Key) return false;
            if (FindConstant(at, name) is not null) return true;
        }
        return true;
    }

    /// <summary>
    /// Whether a dotted name resolves as a type the way a written type does --
    /// the library's types included, which are declared without the namespace
    /// a program names them with (System.Text.StringBuilder).
    /// </summary>
    private bool NamesType(string dotted, Expr at)
    {
        if (IsTypeName(dotted)) return true;
        // A CONSTANT OF A TYPE IS NOT A TYPE. Resolving the name as written
        // trims the qualifier -- namespaces are not a tree here -- and finds
        // any type of the last name anywhere: `case NtObjectRequest.Watch:`
        // became a type test for the registry's nested class Watch, reading an
        // enum value as an object's header. Only a member the qualifier's
        // type really has stops it; a type nested in its base still resolves.
        int dot = dotted.LastIndexOf('.');
        if (dot > 0 && FindType(dotted[..dot], out TypeSymbol? owner) && owner is not null)
        {
            string member = dotted[(dot + 1)..];
            if (owner.EnumValues.ContainsKey(member) || FindConstant(owner, member) is not null) return false;
        }
        _quiet++;
        try { return !Resolve(new TypeRef { Name = dotted, Line = at.Line, Col = at.Col }, _thisType).IsError; }
        finally { _quiet--; }
    }

    /// <summary>A name or a chain of member names, written out with its dots; null for anything else.</summary>
    private static string? Dotted(Expr e) => e switch
    {
        NameExpr n => n.Name,
        MemberExpr { Guarded: false } m when Dotted(m.Target) is string left => left + "." + m.Name,
        _ => null,
    };

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

    /// <summary>Specialisations whose `new()` arguments have been checked (CS0310): once each.</summary>
    private readonly HashSet<string> _constraintsChecked = new(StringComparer.Ordinal);

    /// <summary>
    /// WHETHER A TYPE ARGUMENT MEETS `new()` (C# 15.2.5): a value type, or a
    /// class neither abstract nor static with a public constructor of no
    /// parameters -- the one C# gives a class that declares none counts.
    /// Not an interface, a delegate, an array, a pointer or string. A type
    /// parameter, or a shared copy's word, is its own declaration's to
    /// answer for, and passes here.
    /// </summary>
    private static bool HasPublicParameterless(Type a)
    {
        if (a.IsError || a.ParamName is not null || a.CanonParam != -1)
        {
            return true;
        }
        if (a.IsArray || a.IsPointer || a.Function is not null)
        {
            return false;
        }
        if (a.IsNullableValue)
        {
            return true;
        }
        if (a.Symbol is not TypeSymbol s)
        {
            return a.Prim != Prim.String && a.Prim != Prim.Type;
        }
        if (s.Kind is TypeKind.Struct or TypeKind.Enum)
        {
            return true;
        }
        if (s.Kind != TypeKind.Class || s.Decl is { IsDelegate: true } || s.Decl is { } d && (d.Mods & (Mods.Abstract | Mods.Static)) != 0)
        {
            return false;
        }
        List<MethodSymbol> ctors = s.Methods.Where(m => m.IsCtor && !m.Static).ToList();
        return ctors.Count == 0 || ctors.Any(m => m.Params.Count == 0 && (m.Decl is null || m.Decl.Mods.HasFlag(Mods.Public)));
    }

    /// <summary>
    /// What `Activator.CreateInstance<T>()` is over the T written: `new T()`
    /// where T has a public parameterless constructor (a value type its
    /// zero), else CannotCreate with .NET's reason. Null to call the method
    /// as written: a T still a type parameter, in a template never run.
    /// A T only run time knows is refused, as `new T()` over one is
    /// (Monomorphiser.ParameterMade).
    /// </summary>
    private Expr? ActivatorMade(CallExpr c, TypeRef written)
    {
        Type t = Resolve(written, _thisType);
        if (t.IsError || t.ParamName is not null)
        {
            return null;
        }
        if (t.CanonParam != -1)
        {
            Error(c, $"Activator.CreateInstance<{written.Name}>() over a type argument only run time knows: give the parameter it comes from the new() constraint");
            return null;
        }
        if (HasPublicParameterless(t))
        {
            return new NewExpr { Type = written, Line = c.Line, Col = c.Col };
        }
        string reason = t.Symbol is TypeSymbol { Kind: TypeKind.Interface } ? "Cannot create an instance of an interface."
                      : t.Symbol is TypeSymbol { Decl: { } d } && (d.Mods & Mods.Abstract) != 0 ? "Cannot create an abstract class."
                      : "No parameterless constructor defined.";
        MemberExpr refused = new()
        {
            Target = new NameExpr { Name = "Activator", Line = c.Line, Col = c.Col },
            Name = "CannotCreate", Line = c.Line, Col = c.Col,
        };
        refused.WritableTypeArgs.Add(written);
        CallExpr call = new() { Target = refused, Line = c.Line, Col = c.Col };
        call.Args.Add(new LiteralExpr { Kind = Lit.Str, Text = reason, Line = c.Line, Col = c.Col });
        return call;
    }

    /// <summary>C#'s refusal of a type argument that does not meet `new()` (CS0310).</summary>
    private static string NotConstructible(Type a, string parameter, string generic)
        => $"CS0310: '{a}' must be a non-abstract type with a public parameterless constructor in order to use it as parameter '{parameter}' in the generic type or method '{generic}'";

    /// <summary>The type parameters of the method signature being declared.</summary>
    private List<TypeParam>? _signature;

    /// <summary>Accessor methods invented for properties, checked like any other body.</summary>
    /// <summary>
    /// The accessors made for properties, by the type they belong to. One list
    /// of them all, filtered for each type whose bodies were checked, was a
    /// scan of every accessor in the program for every type -- thousands of
    /// types, tens of thousands of accessors -- and a fifth of a self-hosted
    /// unit's time.
    /// </summary>
    private readonly Dictionary<TypeSymbol, List<MethodDecl>> _synthesised = new(ReferenceEqualityComparer.Instance);

    private void Synthesised(TypeSymbol owner, MethodDecl accessor)
    {
        if (!_synthesised.TryGetValue(owner, out List<MethodDecl>? made))
        {
            made = new List<MethodDecl>();
            _synthesised[owner] = made;
        }
        made.Add(accessor);
    }

    /// <summary>Total bytes of static storage the program needs.</summary>
    public int StaticBytes => _staticNext;

    public Binder(string file = "<source>", Action<string>? requireDeclaration = null,
        IReadOnlyDictionary<(string Name, int Arity), int>? indexedInterfaces = null,
        Action<string, string>? requireExtensions = null,
        IReadOnlySet<(string Name, int Arity)>? libraryInterfaces = null,
        Action<string, int>? requireOverrides = null,
        IReadOnlySet<(string Name, int Arity)>? kernelInterfaces = null)
    {
        _file = file;
        _kernelInterfaces = kernelInterfaces;
        _requireDeclaration = requireDeclaration;
        _requireExtensions = requireExtensions;
        _requireOverrides = requireOverrides;
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
        IReadOnlySet<(string Name, int Arity)>? libraryInterfaces = null,
        Action<string, int>? requireOverrides = null, bool freshOnly = false,
        IReadOnlySet<(string Name, int Arity)>? kernelInterfaces = null, Metadata.DemandsAsked? asked = null)
    {
        Binder b = new(file, requireDeclaration, indexedInterfaces, requireExtensions, libraryInterfaces, requireOverrides, kernelInterfaces);
        // WHAT EARLIER BINDINGS OF THIS UNIT ALREADY ASKED THE INDEX. A name
        // once required is answered for the rest of the unit's compile: it was
        // in no declaration, or it was loaded -- straight away, or by the pass
        // its demand threw away -- and asking again only spells it again to
        // be told nothing. Each round's binder asked them all afresh, every
        // name spelt again in every binding of the unit.
        if (asked is not null)
        {
            b._demanded = asked.Names;
            b._demandedWithin = asked.Within;
        }
        b._freshOnly = freshOnly;
        b._usesDynamic = unit.UsesDynamic;
        b.Run(unit);
        // The declarations' tables carry straight on into the bodies': a copy
        // of every one, the original then dropped, was a unit's whole binding
        // made twice for the collector.
        b.CheckBodyWork();
        b.DeclareUsed();
        // No await under a lock, nothing awaited or allocated in an interrupt
        // handler: across the unit, now that every body is bound
        // (Binder.AwaitChecks).
        b.CheckAsyncSafety();
        b._r.StaticBytes = b._staticNext;
        b.Retire();
        return b._r;
    }

    /// <summary>
    /// THE IMPORTED TYPES THIS UNIT ASKED FOR, their members declared while
    /// binding can still answer for them. A unit describes the layout of
    /// every type it used (ManagedLayouts), and one named only in a signature
    /// -- a parameter, a field of its own -- has had nothing of it asked for.
    /// Declared after binding, a declaration its members wanted could no
    /// longer be loaded; declared here, it is asked of the index like any
    /// other, and the pass goes round for it.
    /// </summary>
    private void DeclareUsed()
    {
        foreach (TypeSymbol t in _r.Types.Values.Where(t => t.MembersPending && t.Used && t.Decl?.SignatureTypes is not null).ToList())
        {
            t.EnsureMembers();
        }
        _declarationBatch.ThrowIfAny();
    }

    /// <summary>
    /// Binding is over. THE BINDER LIVES ON while any type's members are
    /// still to be declared -- each such symbol holds it (DeclareMembersNow)
    /// -- so what only the bodies needed is let go here rather than kept
    /// with it through lowering: the bodies' bookkeeping, and caches that
    /// fill again if asked.
    /// </summary>
    private void Retire()
    {
        _bound = true;
        _boundBodies.Clear();
        _lockSummary.Clear();
        _asyncSafetyReported.Clear();
        _declOf.Clear();
        _hoistedFunctions.Clear();
        _importedSeen.Clear();
        _absentWithin.Clear();
        _dottedParts.Clear();
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

        _r.Errors.Add(new CompileError(Where(at), at.Line, at.Col, message));
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
    ///
    /// THE NODE'S OWN FIRST, when it knows it: a type is checked against its
    /// interfaces outside any member, where the file last read -- the
    /// library's, often -- put a test's line 38 in Core.cor.
    private string Where(Node? at = null)
        => at?.File is { Length: > 0 } own ? own
         : _member?.File is { Length: > 0 } written ? written
         : _in.Length > 0 ? _in
         : _file;

    /// <summary>
    /// This unit's own `#pragma warning` directives, replayed by
    /// PragmaWarnings against a warning's file, code and line whenever one
    /// is about to be reported. Empty outside Run(), same as before a
    /// pragma table existed at all.
    /// </summary>
    private IReadOnlyList<PragmaWarning> _pragmas = Array.Empty<PragmaWarning>();

    /// <summary>
    /// Records a warning under Roslyn's own code for the same condition, in
    /// its own format: "file(line,col): warning CS8602: message". Silent
    /// when a `#pragma warning disable` covers this exact code at this exact
    /// line -- the caller does not have to know that, any more than it knows
    /// whether -Wno-error is set.
    /// </summary>
    private void Warning(Node at, string code, string message)
    {
        if (_quiet > 0) return;

        string file = Where(at);

        if (PragmaWarnings.IsSuppressed(_pragmas, file, code, at.Line)) return;

        _r.Warnings.Add(new CompileError(file, at.Line, at.Col, message, warning: true, code: code));
    }

    /// <summary>
    /// Which of Roslyn's four "a possibly-null value went somewhere it
    /// should not have" codes applies, read off the same `what` text the
    /// message itself is built from. `what` is not a code, it is prose
    /// written for a human ("argument 2 of 'Foo'", "return value", "the
    /// value a lambda produces", "'Name'" for an initialiser member) -- so
    /// this is pattern matching on that prose, not a lookup table, and a
    /// caller that invents a new `what` string outside these patterns falls
    /// through to CONVERSION, the same bucket real C# uses for a cast.
    /// </summary>
    private enum NullContext { Assignment, Return, Argument, Local, Conversion }

    private static NullContext ClassifyNullContext(string what)
    {
        // ARGUMENTS: a value flowing into a method's, constructor's,
        // indexer's or Add's formal parameter -- CS8604 alongside it, or
        // CS8620 for the element-nullability mismatch.
        if (what.Contains("argument") || what is "index" or "the value")
        {
            return NullContext.Argument;
        }

        // RETURNS: an ordinary return, a yield return, and a lambda's
        // expression body all hand a value back out of a method -- CS8603.
        if (what.Contains("return") || what == "the value a lambda produces")
        {
            return NullContext.Return;
        }

        // A LOCAL, declared with its value or assigned one later: Roslyn's
        // general CS8600, for a null literal as much as for a maybe-null
        // value (checked against Roslyn: `string s = null;` and `s = maybe;`
        // are both CS8600, where the same into a field is CS8625 and CS8601).
        if (what.StartsWith("initialiser for ", StringComparison.Ordinal) || what == "assignment to a local")
        {
            return NullContext.Local;
        }

        // ASSIGNMENTS: a plain `x = y` into a field, a property or an
        // element, an object- or `with`-initialiser
        // member (built as `'Name'`), and an array/collection initialiser
        // element (`element 3`) all store a value into a place that already
        // has a declared type -- CS8601.
        if (what == "assignment" || what.StartsWith("element ", StringComparison.Ordinal)
            || (what.Length > 1 && what[0] == '\'' && what[^1] == '\''))
        {
            return NullContext.Assignment;
        }

        // EVERYTHING ELSE IS A CONVERSION: a cast, and a method-group-to-
        // delegate conversion, which is not a "place" that is assigned to at
        // all -- the general code, as for a local.
        return NullContext.Conversion;
    }

    /// <summary>A possibly-null value where a non-nullable one was wanted: CS8600/8601/8603/8604.</summary>
    private static string NullCode(string what) => ClassifyNullContext(what) switch
    {
        NullContext.Argument => "CS8604",
        NullContext.Return => "CS8603",
        NullContext.Assignment => "CS8601",
        _ => "CS8600",
    };

    /// <summary>
    /// A null LITERAL where a non-nullable one was wanted: CS8625 into a
    /// field, a property, an element or an argument, but a local takes the
    /// general CS8600 and a return CS8603, as the maybe-null value does.
    /// </summary>
    private static string NullLiteralCode(string what) => ClassifyNullContext(what) switch
    {
        NullContext.Local or NullContext.Conversion => "CS8600",
        NullContext.Return => "CS8603",
        _ => "CS8625",
    };

    /// <summary>
    /// An element's (or type argument's) nullability not matching: CS8619 in
    /// general, or CS8620 specifically when the mismatched value is an
    /// argument -- the same argument/other split as NullCode, because that
    /// is the one distinction Roslyn itself draws here.
    /// </summary>
    private static string ElementNullCode(string what)
        => ClassifyNullContext(what) == NullContext.Argument ? "CS8620" : "CS8619";

    private int _quiet;

    /// <summary>
    /// How many scopes were already open when the innermost lambda's body
    /// started, or -1 outside one. A local found below this line belongs to the
    /// enclosing method and has to be captured.
    /// </summary>
    private int _lambdaFloor = -1;

    /// <summary>Names the lambda being looked over reads from outside itself.</summary>
    private Dictionary<string, Type>? _captured;

    /// <summary>
    /// The enclosing method's constants a lambda reads, found alongside the
    /// captures. A constant is not captured -- it has no storage to share --
    /// so the lambda's body declares the same constant (pass two).
    /// </summary>
    private Dictionary<string, ConstSym>? _capturedConstants;
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
        // A constant of a type whose members wait to be asked for is not in
        // the table until they are declared: asking for it is asking. Unless
        // none of them has the name (TypeSymbol.MayHave): a name looked for
        // as a constant is looked for on the type being checked and every
        // base, outer type and imported type it passes, and each would have
        // been declared to find nothing.
        if (!owner.MayHave(name)) return;
        owner.EnsureMembers();
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
            case UnaryExpr { Op: UnOp.Plus } plus:
                return RealConstant(plus.Operand, owner);
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
            case NameExpr n when owner != null && FindConstantOutward(owner, n.Name) is { } here:
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

        NameExpr name => owner is null ? null : FindTextOutward(owner, name.Name),
        MemberExpr member when ConstantOwner(member.Target) is { } named => FindText(named, member.Name),
        BinaryExpr { Op: BinOp.Add } add => JoinedText(add, owner),
        _ => null,
    };

    /// <summary>
    /// A chain of `+` joined in ONE pass. The chain is left-deep -- `a + b +
    /// c` is `(a + b) + c` -- and joining it pairwise copied the growing text
    /// once per operand: a table written as a thousand-line concatenation
    /// made gigabytes of garbage to bind one constant.
    /// </summary>
    private string? JoinedText(BinaryExpr add, TypeSymbol owner)
    {
        List<Expr> parts = new();
        Expr at = add;
        while (at is BinaryExpr { Op: BinOp.Add } more)
        {
            parts.Add(more.Right);
            at = more.Left;
        }
        parts.Add(at);
        System.Text.StringBuilder text = new();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            if (ConstantText(parts[i], owner) is not { } piece) return null;
            text.Append(piece);
        }
        return text.ToString();
    }

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

            case NameExpr n when owner != null && FindConstantOutward(owner, n.Name) is { } here:
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
    /// The methods a name means on a type, as C#'s member lookup finds them:
    /// on an interface that includes what its base interfaces declare, so a
    /// default body in ILoud can say `Name` for IGreeter's.
    /// </summary>
    private static List<MethodSymbol> Members(TypeSymbol t, string name)
        => t.Kind == TypeKind.Interface ? Reachable(t, name) : t.FindMethods(name);

    private static bool IsIndexType(Type t)
        => t.Symbol is { Kind: TypeKind.Struct, Name: "Index" } s && Reachable(s, "GetOffset").Any();

    private static bool IsRangeType(Type t)
        => t.Symbol is { Kind: TypeKind.Struct, Name: "Range" } s && Reachable(s, "GetOffsetAndLength").Any();

    /// <summary>
    /// `target[index]` where the index is an Index or a Range held in a value,
    /// as C# lowers it: the target evaluated once, then the index, then what
    /// they say together. `list[i]` is `list[i.GetOffset(list.Count)]`;
    /// `s[r]` is `s.Substring(start, end - start)` with each end's offset
    /// taken against the length, and a span's is the same through Slice; an
    /// array's is RuntimeHelpers.GetSubArray, which copies.
    /// </summary>
    private Expr ThroughIndexValue(IndexExpr ix, Type indexed, bool range)
    {
        int line = ix.Line, col = ix.Col;
        SubjectExpr Held(int outer) => new() { Outer = outer, Line = line, Col = col };
        MemberExpr Member(Expr on, string name) => new() { Target = on, Name = name, Line = line, Col = col };
        CallExpr Invoke(Expr on, string name, params Expr[] args)
        {
            CallExpr call = new() { Target = Member(on, name), Line = line, Col = col };
            foreach (Expr a in args)
            {
                call.Args.Add(a);
                call.WritableArgNames.Add(null);
            }
            return call;
        }
        Expr Length(Expr on) => new MemberExpr
        {
            Target = on, Name = "Length", Else = "Count", Line = line, Col = col,
        };
        PatternExpr Once(Expr subject, Expr test) => new() { Subject = subject, Test = test, Line = line, Col = col };

        if (range && indexed.IsArray)
        {
            CallExpr sub = new()
            {
                Target = Member(new NameExpr { Name = "RuntimeHelpers", Line = line, Col = col }, "GetSubArray"),
                Line = line, Col = col,
            };
            sub.Args.Add(ix.Target);
            sub.WritableArgNames.Add(null);
            sub.Args.Add(ix.Args[0]);
            sub.WritableArgNames.Add(null);
            return sub;
        }

        // Inside both patterns: the index or range is the inner subject (0)
        // and the target the outer one (1).
        if (!range)
        {
            IndexExpr at = new() { Target = Held(1), Line = line, Col = col };
            at.Args.Add(Invoke(Held(0), "GetOffset", Length(Held(1))));
            return Once(ix.Target, Once(ix.Args[0], at));
        }

        Expr Start() => Invoke(Member(Held(0), "Start"), "GetOffset", Length(Held(1)));
        Expr size = new BinaryExpr
        {
            Op = BinOp.Sub, Left = Invoke(Member(Held(0), "End"), "GetOffset", Length(Held(1))), Right = Start(),
            Line = line, Col = col,
        };
        string slicer = indexed.Prim == Prim.String ? "Substring" : "Slice";
        return Once(ix.Target, Once(ix.Args[0], Invoke(Held(1), slicer, Start(), size)));
    }

    /// <summary>
    /// `^k` as the number it means: how long the thing is, minus k.
    ///
    /// Which member says how long depends on what it is -- an array and a span
    /// have a Length, a list has a Count -- and the checker knows by now.
    /// </summary>
    /// <summary>
    /// A target whose second reading is the first: a local, a parameter,
    /// `this`, a literal. Anything else -- a call, a property, an element --
    /// may do something or answer differently, and is evaluated once.
    /// </summary>
    private bool ReadTwiceForNothing(Expr target) => target switch
    {
        ThisExpr or LiteralExpr => true,
        NameExpr n => Lookup(n.Name) is LocalSym or ParamSym,
        _ => false,
    };

    /// <summary>
    /// `target[^k]` with the target evaluated once and held (a PatternExpr's
    /// subject, as ThroughIndexValue does): the element at the held value's
    /// count less k, handed to `use` -- read as it is, or assigned to.
    /// </summary>
    private PatternExpr FromEndOnce(Expr target, FromEndExpr end, Func<IndexExpr, Expr> use)
    {
        SubjectExpr Held() => new() { Outer = 0, Line = end.Line, Col = end.Col };
        IndexExpr element = new() { Target = Held(), Line = end.Line, Col = end.Col };
        element.Args.Add(new BinaryExpr
        {
            Op = BinOp.Sub,
            Left = new MemberExpr { Target = Held(), Name = "Length", Else = "Count", Line = end.Line, Col = end.Col },
            Right = end.Offset,
            Line = end.Line, Col = end.Col,
        });
        return new PatternExpr { Subject = target, Test = use(element), Line = end.Line, Col = end.Col };
    }

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
    /// <summary>Whether a floating constant, truncated, is a value of the integer type.</summary>
    private static bool RealFits(double real, Type to)
    {
        if (double.IsNaN(real) || double.IsInfinity(real)) return false;
        double t = Math.Truncate(real);
        if (to.Prim == Prim.U64) return t >= 0 && t < 18446744073709551616.0;
        return t >= -9223372036854775808.0 && t < 9223372036854775808.0 && Fits((long)t, to);
    }

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

    /// <summary>
    /// Whether a constant converts implicitly to an integral type, at an
    /// assignment or a call: C# 10.2.11's implicit constant expression
    /// conversion.
    /// An INT constant becomes any integral type that holds its value; a LONG
    /// one only ulong, when it is not negative. Letting a long constant
    /// narrow too made `Math.Min(9L, 3)` fit Min(int, int) as well as
    /// Min(long, long); neither was then better than the other, and the call
    /// fell back to the first overload, Min(double, double).
    ///
    /// Literals are typed long here past int's range where C# types them
    /// uint first, so a long constant inside uint's range and outside int's
    /// fits uint too, as the uint literal C# would have made of it.
    /// The same rule at an assignment as at a call: `int n = WordSize.Bytes`
    /// with Bytes a const long is an error, as it is in C#; say (int).
    /// </summary>
    // C# 10.2.11: an int constant into sbyte, byte, short, ushort, uint or
    // ulong when it fits -- never char, which no number becomes implicitly --
    // and a long constant into ulong when it is not negative.
    private static bool ConstantConverts(Type from, long value, Type to) => to.Prim != Prim.Char && from.Prim switch
    {
        Prim.I32 => to.Prim == Prim.U64 ? value >= 0 : Fits(value, to),
        Prim.I64 => to.Prim == Prim.U64 && value >= 0,
        _ => false,
    };

    /// <summary>
    /// Whether an integer constant expression reaches `want` implicitly: by
    /// the constant conversion (ConstantConverts), into a nullable of what it
    /// reaches (C# 10.6.1, wrapping after), or into the operand of a user-
    /// defined implicit conversion -- `x - 1` over a UInt128 takes the 1 as
    /// a uint first, as .NET's operators do.
    /// </summary>
    private bool IntegerConstantFits(Expr? written, Type had, Type want)
    {
        if (written is null || !had.IsInteger || had.Nullable || had.Symbol is not null
            || ConstantValue(written, _thisType) is not long value)
        {
            return false;
        }
        Type target = want.IsNullableValue ? want.Underlying : want;
        if (target.IsInteger && target.Symbol is null && !target.Nullable && ConstantConverts(had, value, target)) return true;
        return target.Symbol is not null && UserConversion(had, target, false, value) is not null;
    }

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

    /// <summary>
    /// A const by the name an initialiser writes: on the type or its bases,
    /// then outwards through the types it is nested in, as any unqualified
    /// name resolves. A nested class's `const long OuterBudget = JudgeBudget;`
    /// naming its outer class's const was refused as no constant expression,
    /// and corc stopped compiling its own region solver (test 1321).
    /// </summary>
    private (long Value, Type Type)? FindConstantOutward(TypeSymbol owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = Outer(t))
        {
            if (FindConstant(t, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>A TEXT const by the name an initialiser writes, outwards as FindConstantOutward.</summary>
    private string? FindTextOutward(TypeSymbol owner, string name)
    {
        for (TypeSymbol? t = owner; t != null; t = Outer(t))
        {
            if (FindText(t, name) is { } found) return found;
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

    /// <summary>--dump-slots: every interface method's slot as it is numbered, on standard error.</summary>
    private static readonly bool DumpSlots = Switches.DumpSlots;

    private void Run(CompilationUnit unit)
    {
        _registryKeys = unit.RegistryKeys;
        _pragmas = unit.Pragmas;

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
        // NOT YET FOR A DECLARATION WHOSE MEMBERS ARE DEFERRED: there are none
        // to move until they are made, and that is done with the rest of
        // declaring them (DeclareMembersNow). Which ones those are is settled
        // here, once: a list can be made by looking at it before its symbol
        // exists (ImpliedConstructor looks at a base's), and its type is
        // still one whose members are declared on first use.
        // NOT AN IMPORTED ONE WHOSE NAME ANOTHER DECLARATION HAS: the pass
        // below adds it to the prelude's type of its name, or moves one of
        // the two into System, and declares it there and then. The
        // monomorphiser deferred none such (ImportedLater); a declaration a
        // later round added could still take the name.
        _deferred.Clear();
        Dictionary<string, int>? imported = null;
        foreach (TypeDecl d in unit.Types)
        {
            if (d.MembersPending && d.SignatureTypes is not null) (imported ??= new(StringComparer.Ordinal))[d.Name] = 0;
        }
        if (imported is not null)
        {
            foreach (TypeDecl d in unit.Types)
            {
                if (imported.TryGetValue(d.Name, out int seen)) imported[d.Name] = seen + 1;
            }
        }
        foreach (TypeDecl d in unit.Types)
        {
            if (!d.MembersPending) continue;
            if (d.SignatureTypes is not null && imported![d.Name] > 1) _ = d.Members;
            else _deferred.Add(d);
        }

        foreach (TypeDecl d in unit.Types)
        {
            if (_deferred.Contains(d)) continue;
            Initialisers(d);
            StaticInitialisers(d);
        }

        // AND A CLASS WHOSE BASE HAS A CONSTRUCTOR HAS ONE TOO, written or not.
        // C# gives every class a constructor that calls its base's; here one is
        // only made when there is something for it to do, and a base that
        // initialises its own fields is something. Without it `new MemberExpr`
        // never ran Node's `File = ""`, and the file of every expression in the
        // compiler compiled by itself was null.
        _declsByName = new(StringComparer.Ordinal);

        foreach (TypeDecl d in unit.Types)
        {
            _declsByName.TryAdd(d.Name, d);
        }

        foreach (TypeDecl d in unit.Types)
        {
            if (!_deferred.Contains(d)) ImpliedConstructor(d);
        }

        // Source declarations that ADD to a prelude type rather than clashing
        // with it. Given their members and their bodies below, once every
        // symbol exists.
        List<(TypeDecl Decl, TypeSymbol Symbol)> extend = new();

        // Three passes: declare every type first so they can refer to each
        // other in any order, then fill in members, then check bodies.
        foreach (TypeDecl d in unit.Types)
        {
            // A LIBRARY TYPE NESTED IN ONE MOVED TO SYSTEM moves with it.
            if (d.FromLibrary && d.Outer is not null && MovedPath(d.Outer) is string movedOuter)
            {
                d.Outer = movedOuter;
                if (d.Namespace.Length == 0) d.Namespace = LibraryHome;
            }

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
                //
                // Whichever of the two is met first. A unit bound from the
                // declaration index can meet the library's Math before the
                // prelude's; the prelude's, not being the library's, then
                // looked like a program's own Math, and the rule below moved
                // the library's out of the way into System -- leaving
                // Math.Min(double, double) the only Min, so `int n =
                // Math.Min(a, b)` on two ints was a double.
                if (already.Decl?.File == "<prelude>" || d.File == "<prelude>")
                {
                    extend.Add((d, already));
                    continue;
                }

                // A PROGRAM'S OWN TYPE WINS OVER THE CLASS LIBRARY'S -- in the
                // program. .NET declares Index in System; a program that writes
                // `class Index` of its own in the global namespace gets that
                // one for every unqualified mention IT makes, and the library's
                // own code, which is in System, still gets System.Index.
                //
                // The library's global types are System's here, so the
                // library's declaration is MOVED into System (MoveToSystem):
                // it keeps its members, its bodies and its code under
                // System.Index, and the library's code finds it there first
                // (FindType). Dropping it, as this once did, bound every use
                // the library itself made -- Range's, the indexers' -- to the
                // program's class, and the library no longer compiled.
                if (already.Decl is { FromLibrary: true } library && !d.FromLibrary)
                {
                    MoveToSystem(library);
                    TypeSymbol mine = new() { Name = d.Name, Key = key, Kind = d.Kind, Decl = d };
                    if (d.TypeParams.Count > 0) AddNames(mine.WritableTypeParamNames, d.TypeParams);
                    RegisterType(key, mine);
                    continue;
                }

                if (d.FromLibrary && already.Decl?.FromLibrary == false)
                {
                    MoveToSystem(d);
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
            if (d.TypeParams.Count > 0) AddNames(sym.WritableTypeParamNames, d.TypeParams);
            if (_deferred.Contains(d)) sym.DeclareMembersLater(_declareMembersNow ??= DeclareMembersNow);
            RegisterType(key, sym);
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

        _namingTuples = true;
        try
        {
            foreach (TypeRef naming in unit.TupleNamings)
            {
                try { Resolve(naming, null); }
                catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
            }
        }
        finally { _namingTuples = false; }

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
                // ONLY WHAT IT IS, of one whose members wait to be asked for:
                // its base and interfaces, which every conversion and type
                // test reads off the symbol without asking for a member, and
                // its type arguments, so that any declaration they need is
                // asked of the index now, while a pass can still be retried
                // with it -- the one thing its members' signatures could ask
                // for that its canonical copy's do not.
                if (!_deferred.Contains(d)) DeclareMembers(d, sym);
                else if (_basesDeclared.Add(sym)) DeclareMembers(d, sym, members: false);
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
        // A COPY OF THE KEYS: a constant's value may name a member of a type
        // whose members are declared on that first asking, which adds its own
        // constants to the table -- evaluated as they are declared, from here
        // on (DeclareMembersNow).
        _constantsEvaluated = true;
        foreach (var key in _constantDeclarations.Keys.ToList())
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
        // AND A THIRD TIER, THIS UNIT'S OWN: an interface the compiler made
        // inside a body -- the delegate of a local function whose signature
        // Func and Action cannot say (Parser.LocalFunctionDelegate) -- which
        // no declaration index lists and no other unit has. Numbered among
        // the shared families, one moved every family sorted after it, in
        // this unit alone; merely existing, it opened the project region, so
        // every class of the project numbered its own virtuals 129 higher in
        // the units that had one than in the units that did not. They are
        // numbered last, above the project's classes, where nothing shared is.
        // WHERE IT WAS DECLARED DECIDES THE TIER, index or no index -- the
        // same question AssignSlots asks of a class. Without an index every
        // interface used to count as library, so a unit compiled from its
        // sources alongside the library (--lib) numbered its own IShape among
        // the library's families, while the unit that read IShape from the
        // index put it in the project's tier: every library slot sorted after
        // it moved by two on one side of the link and not the other, and the
        // link stopped on ArgumentException's layout. The index only adds
        // what the sources cannot say, a family listed as the library's.
        bool IsLibraryInterface(TypeSymbol t, (string, int) family)
            => IsLibraryType(t) || (_libraryInterfaces?.Contains(family) ?? false);
        // AND A MODULE'S OWN, in a kernel module's compile: a family the
        // kernel's index does not hold. Numbered among the kernel's project
        // families, as they were while a module was one compile against the
        // kernel's index alone, one that sorted before a kernel family moved
        // that family's slots, and every class the kernel's interface region
        // ends below, in the module's view and not in the kernel's. They are
        // numbered above the kernel's classes instead, where the kernel has
        // nothing (moduleTier).
        SortedDictionary<(string, int), int> families = new(), local = new(), unitLocal = new(), moduleTier = new();
        bool IsLibraryFamily((string, int) family, bool declaredHere)
            => _libraryInterfaces is null ? true : _libraryInterfaces.Contains(family) && !declaredHere;
        bool IsModuleFamily((string, int) family) => _kernelInterfaces is not null && !_kernelInterfaces.Contains(family);
        if (_indexedInterfaces is not null)
            foreach (var family in _indexedInterfaces)
                (IsLibraryFamily(family.Key, false) ? families : IsModuleFamily(family.Key) ? moduleTier : local)[family.Key] = family.Value;
        foreach (TypeSymbol t in _r.Types.Values.Where(t => t.Kind == TypeKind.Interface))
        {
            (string, int) family = Family(t);
            if (t.Decl?.LocalOnly == true)
            {
                unitLocal[family] = Math.Max(unitLocal.GetValueOrDefault(family), t.Methods.Count(m => m.Decl?.LocalCopy != true));
                continue;
            }
            bool library = IsLibraryInterface(t, family);
            SortedDictionary<(string, int), int> into = library ? families : IsModuleFamily(family) ? moduleTier : local;
            if (library) { local.Remove(family); moduleTier.Remove(family); }
            // THE DECLARED MEMBERS, not the copies this unit made beside them:
            // a copy of the interface's generic method is local to the unit,
            // and counting it gave the family one more slot here than in every
            // unit that made no copy -- moving every family numbered after it.
            into[family] = Math.Max(into.GetValueOrDefault(family), t.Methods.Count(m => m.Decl?.LocalCopy != true));
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

        void Assign(bool library, bool unitOnly = false)
        {
            foreach (TypeSymbol t in _r.Types.Values.Where(t => t.Kind == TypeKind.Interface && !IsTemplate(t)))
            {
                (string template, int arity) = Family(t);
                if ((t.Decl?.LocalOnly == true) != unitOnly) continue;
                if (!unitOnly && IsLibraryInterface(t, (template, arity)) != library) continue;

                for (int i = 0; i < t.Methods.Count; i++)
                {
                    if (t.Methods[i].VtableSlot >= 0 || t.Methods[i].Decl?.LocalCopy == true)
                    {
                        continue;
                    }

                    if (!shared.TryGetValue((template, arity, i), out int slot))
                    {
                        slot = _interfaceSlots++;
                        shared[(template, arity, i)] = slot;
                    }

                    t.Methods[i].VtableSlot = slot;
                    if (DumpSlots) Console.Error.WriteLine("interface slot " + slot + " " + t.Key + "." + t.Methods[i].Name + " library=" + library);
                }
            }
        }

        Number(families);
        Assign(true);
        foreach (var (family, slot) in shared) _r.InterfaceFamilySlots[family] = slot;
        // The table this unit numbered over, for diffing the two sides of a
        // link that stops with a layout conflict: a family present on one
        // side only moves every slot after it. Pass --dump-families.
        if (Switches.DumpFamilies)
        {
            Console.Error.WriteLine("families library=" + families.Count + " project=" + local.Count
                + " slots=" + _interfaceSlots);
            foreach (((string template, int arity), int methods) in families)
                Console.Error.WriteLine("  lib " + template + "`" + arity + " methods=" + methods);
            foreach (((string template, int arity), int methods) in local)
                Console.Error.WriteLine("  project " + template + "`" + arity + " methods=" + methods);
            foreach (((string template, int arity), int methods) in moduleTier)
                Console.Error.WriteLine("  module " + template + "`" + arity + " methods=" + methods);
            foreach (((string template, int arity), int methods) in unitLocal)
                Console.Error.WriteLine("  unit " + template + "`" + arity + " methods=" + methods);
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
            if (moduleTier.Count == 0) Assign(false);
        }
        _projectClassSlots = _interfaceSlots;

        // THE MODULE'S TIER: a fixed distance above where the kernel's
        // classes begin their virtuals, as the project's own interfaces sit
        // above the library's classes. Every unit of the module numbers the
        // same families here, the module's index listing them all.
        if (moduleTier.Count > 0)
        {
            _interfaceSlots = _projectClassSlots + ProjectClassReserve;
            Number(moduleTier);
            Assign(false);
        }

        // Only closures the compiler made implement these, and they declare
        // no virtuals of their own, so the reserve is room to spare.
        if (unitLocal.Count > 0)
        {
            _interfaceSlots = (moduleTier.Count > 0 ? _interfaceSlots : _projectClassSlots) + ProjectClassReserve;
            Number(unitLocal);
            Assign(false, unitOnly: true);
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
        // replaces the bit below, is derived by TypeSymbol.Depth from the
        // type's own bases, so nothing here assigns it.

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
        // NOR ONE WHOSE MEMBERS ARE NOT DECLARED: it is laid out when they
        // are, which anything wanting its size, its fields or its slots
        // brings about first (TypeSymbol.EnsureMembers).
        _layingOut = true;
        foreach (TypeSymbol sym in _r.Types.Values.Where(t => !IsTemplate(t)).ToList())
        {
            if (sym.MembersPending) continue;
            LayOut(sym);
        }

        // A SPECIALISATION'S ARGUMENTS, resolved once in its own scope, for
        // the name .NET gives it: List`1[[System.Int32, ...]].
        // Named only: a type looked up here is not one this unit uses, and
        // one not loaded is not asked for -- described but never laid out,
        // it made a second, empty layout of a type another unit had.
        _quiet++;
        _namingOnly = true;
        try
        {
            foreach (TypeSymbol made in _r.Types.Values.Where(t => t.Decl is { Specialised: true, TemplateArgs.Count: > 0 } && t.TemplateArgTypes.Count == 0).ToList())
            {
                List<Type> args = made.Decl!.TemplateArgs.Select(arg => Resolve(arg, made)).ToList();
                // Every argument resolved, or none kept: the name stays the
                // specialisation's own rather than spell an error.
                if (args.All(a => !Unresolved(a))) made.WritableTemplateArgTypes.AddRange(args);
            }
            // And what a shared copy's code makes for it (TypeDecl.CanonMade).
            foreach (TypeSymbol made in _r.Types.Values.Where(t => t.Decl?.CanonMade is { Count: > 0 } && t.CanonMadeTypes.Count == 0).ToList())
            {
                foreach (TypeRef each in made.Decl!.CanonMade!)
                {
                    Type resolved = Resolve(each, made);
                    // An array of one (Monomorphiser.CanonTested) by its element:
                    // lowering makes the array's descriptor of it.
                    made.WritableCanonMadeTypes.Add(Unresolved(resolved) ? null
                        : resolved.IsArray ? resolved.Element is { Symbol: { } element } inner && !inner.IsArray ? element : null
                        : resolved.Symbol);
                }
            }
        }
        finally { _quiet--; _namingOnly = false; }

        // WHAT A DECLARED TYPE'S DESCRIPTOR WILL NAME, declared now, while
        // the bodies are still to be checked (ForceContext): from here on
        // each type is seen to as its members are declared.
        _contextsResolved = true;
        foreach (TypeSymbol t in _r.Types.Values.Where(t => !t.MembersPending && !IsTemplate(t)).ToList())
        {
            ForceContext(t);
        }

        // `where T : new()` OF A GENERIC TYPE, against each specialisation's
        // arguments (CS0310), once the arguments are known. Said where the
        // template is: the copy has no use site of its own.
        foreach (TypeSymbol made in _r.Types.Values.Where(t => t.Decl is { Specialised: true, TemplateParams: { } }).ToList())
        {
            List<TypeParam> ps = made.Decl!.TemplateParams!;
            if (made.TemplateArgTypes.Count != ps.Count || !_constraintsChecked.Add(made.Name))
            {
                continue;
            }
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].New && !HasPublicParameterless(made.TemplateArgTypes[i]))
                {
                    Error(made.Decl, NotConstructible(made.TemplateArgTypes[i], ps[i].Name,
                        made.Decl.Template + "<" + string.Join(", ", ps.Select(p => p.Name)) + ">"));
                }
            }
        }

        // The tuple shapes met so far take ValueTuple's interfaces now that
        // those have slots; any made from here on take them as they are made.
        // Asked for by name, so that every unit has them -- a unit's library
        // declarations arrive as its source names them -- and every unit
        // gives a tuple shape the same two.
        FindType("IComparable", out _);
        FindType("System.Runtime.CompilerServices.ITuple", out _);
        _tupleFacesReady = true;
        foreach (TypeSymbol shape in _r.Types.Values.Where(t => t.Structural && t.Kind == TypeKind.Struct).ToList())
        {
            TupleFaces(shape);
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
            // And not one whose members were never asked for: its bodies are
            // its canonical copy's, which are never checked here either --
            // or, for a copy made per argument, its own, checked once they
            // are asked for (BodiesNow), and never lowered until they are.
            if (_r.Types.TryGetValue(TypeKey(d), out TypeSymbol? sym)
                && ReferenceEquals(sym.Decl, d) && !IsTemplate(sym) && !sym.MembersPending)
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

        // A TYPE WHOSE MEMBERS ARE DECLARED FROM HERE ON joins this list
        // rather than miss it (DeclareMembersNow).
        _bodiesListed = true;
    }

    private void CheckBodyWork()
    {
        // Still serial until synthetic symbols and binding results have
        // isolated ownership and an ordered merge. Do not parallelize the
        // existing shared BindResult by merely wrapping this loop in Tasks.
        // THE COUNT IS READ EACH TIME ROUND: a copy made per argument whose
        // members are first asked for by a body checked here is added to
        // the end, and checked in its turn (DeclareMembersNow).
        _inBodies = true;
        try
        {
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
        }
        finally
        {
            _inBodies = false;
            _bodiesChecked = true;
        }
        _bodyWork.Clear();
        // Any generic local function a body outside a method declared.
        SettleGenericCaptures();
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
    /// <summary>One statement in a block of its own: a scope for what it declares.</summary>
    private static Stmt Scoped(Stmt one)
    {
        Block block = new() { Line = one.Line, Col = one.Col };
        block.WritableStatements.Add(one);
        return block;
    }

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
            if (state?.Type.Name != "nint")
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
                // A TABLE OF CONSTANTS IS DATA, not code: laid down in the
                // image and never built (FieldDecl.StaticData).
                if (d.TypeParams.Count == 0 && !d.Specialised && StaticArrayOf(f) is StaticArray table)
                {
                    f.StaticData = table;
                    f.Init = null;
                    continue;
                }
                initial = f.Init; type = f.Type; field = f.Name; f.Init = null;
            }
            else if (m is PropertyDecl p && p.Init is not null && p.Auto && p.Mods.HasFlag(Mods.Static))
            {
                initial = p.Init; type = p.Type; field = "<" + p.Name + ">"; p.Init = null;
            }
            else continue;

            // EACH INITIALISER ITS OWN SCOPE, as C# has it: two of them that
            // each name a pattern's match `t` -- Escape's PromoteTrace and
            // EscapeFields' FieldTrace -- are not one `t` declared twice.
            body.Add(Scoped(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new NameExpr { Name = field, Line = m.Line, Col = m.Col },
                    Value = InitializerMethods.Value(m, type, Retarget(initial, type), initializerMethods),
                    Line = m.Line, Col = m.Col,
                },
                Line = m.Line, Col = m.Col,
            }));
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
        // A DEFERRED LIST IS MADE BEFORE THE FLAG IS READ: a copy made per
        // argument once its template's initialisers are placed is made with
        // them placed, and says so (Monomorphiser.MakeMembers). Read first,
        // the flag said not yet, and they were placed a second time.
        if (d.MembersPending) _ = d.Members;
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

            prologue.Add(Scoped(new ExprStmt
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
            }));
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
            c.Body?.WritableStatements.InsertRange(0, prologue);
        }
    }

    /// <summary>
    /// Fills in the type of a target-typed <c>new()</c> from the declaration
    /// it is initialising, and leaves everything else alone.
    /// </summary>
    /// A property of `owner` named `name`, by its getter: its type, or null.
    private static Type? PropertyType(TypeSymbol? owner, string name)
    {
        if (owner is null) return null;
        foreach (MethodSymbol m in owner.FindMethods("get_" + name))
            if (m.Params.Count == 0) return m.Returns;
        return null;
    }

    private static Expr Retarget(Expr init, TypeRef declared)
    {
        // A collection expression is made for its type by the checker, which
        // knows what the type is (the assignment this becomes wants it). And
        // `new[] { ... }` is not a target-typed `new()`: its type is its
        // elements', and naming the declared type made it an array OF the
        // declared type -- `string[] x = new[] { "a" }` a string[][].
        if (init is not NewExpr nw || nw.Type.Name.Length != 0 || nw.Collection || nw.Elements is not null)
        {
            return init;
        }

        NewExpr made = new()
        {
            Type = declared, ArraySize = nw.ArraySize, Line = nw.Line, Col = nw.Col,
        };

        made.Args.AddRange(nw.Args);
        if (nw.ArgNames.Count > 0) made.WritableArgNames.AddRange(nw.ArgNames);
        made.Elements = nw.Elements;
        if (nw.Inits.Count > 0) made.Body.WritableInits.AddRange(nw.Inits);
        if (nw.Adds.Count > 0) made.Body.WritableAdds.AddRange(nw.Adds);
        if (nw.Indexes.Count > 0) made.Body.WritableIndexes.AddRange(nw.Indexes);
        return made;
    }

    /// <summary>Every declaration of the unit by name, for the constructors a class is given (ImpliedConstructor).</summary>
    private Dictionary<string, TypeDecl> _declsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// C#'s parameterless constructor, given to a class that wrote none when
    /// a base it derives from has one to call (Run).
    /// </summary>
    private void ImpliedConstructor(TypeDecl d)
    {
        if (d.Kind != TypeKind.Class || d.Mods.HasFlag(Mods.Static)
            || d.Members.OfType<MethodDecl>().Any(c => c.IsCtor))
        {
            return;
        }

        TypeDecl? up = d;
        bool constructed = false;

        for (int depth = 0; depth < 64 && up is not null && !constructed; depth++)
        {
            up = up.Bases.Select(b => _declsByName.GetValueOrDefault(b.Name))
                   .FirstOrDefault(b => b is { Kind: TypeKind.Class });
            // A BASE WHOSE MEMBERS WERE DEFERRED has its initialisers placed
            // before it is asked, as every other base had by now: a class
            // with field initialisers and no constructor is given one there
            // (Initialisers), which is the constructor this asks about.
            if (up is not null) Initialisers(up);
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

    // ---- members declared on first use ------------------------------------
    //
    // A word-shaped specialisation of a generic class is declared for every
    // signature that names it, and most of them are never asked a thing:
    // a unit that hands a List of Control along never calls its Add. Their
    // members are made and declared the first time something asks for one
    // (Monomorphiser.MembersLater, TypeDecl.DeferMembers), through the
    // symbol, which every lookup, conversion, layout, slot and descriptor
    // goes through (TypeSymbol.EnsureMembers). Until then such a type has
    // its symbol, its base, its interfaces and its arguments resolved, and
    // nothing else; a walk over every type that needs nothing of members
    // nobody asked for passes over it (MembersPending).

    /// <summary>The declarations whose members were deferred when binding began (TypeDecl.MembersPending).</summary>
    private readonly HashSet<TypeDecl> _deferred = new(ReferenceEqualityComparer.Instance);

    /// <summary>The one delegate every deferred symbol is handed (DeclareMembersLater).</summary>
    private Action<TypeSymbol>? _declareMembersNow;

    /// <summary>The deferred types whose base, interfaces and arguments are declared (DeclareMembers, members: false).</summary>
    private readonly HashSet<TypeSymbol> _basesDeclared = new(ReferenceEqualityComparer.Instance);

    /// <summary>Constants are being evaluated, or have been: a deferred one's are evaluated as they are declared.</summary>
    private bool _constantsEvaluated;

    /// <summary>
    /// Binding is over (Bind): what is declared now is declared for lowering,
    /// with no pass left to report a mistake in or retry a declaration for.
    /// </summary>
    private bool _bound;

    /// <summary>
    /// A deferred type's members, declared on the first asking: made, given
    /// their initialisers and implied constructor as Run gives every other
    /// type's before declaring them, declared, their constants evaluated and
    /// the type laid out -- each only once the passes over every other type
    /// have reached it, so that the type ends as it would have, had it been
    /// declared with them.
    ///
    /// WHATEVER WAS BEING BOUND IS PUT ASIDE and restored after: the asking
    /// can come from the middle of a body, a quiet lambda pass or a naming
    /// pass, and none of that is any part of declaring a type. Declared as
    /// the members pass declares a specialisation, with no type being checked
    /// and nothing marked used (BindingElsewhere).
    /// </summary>
    private void DeclareMembersNow(TypeSymbol sym)
    {
        TypeDecl d = sym.Decl!;
        string wasIn = _in;
        MemberDecl? wasMember = _member;
        List<TypeParam>? wasSignature = _signature;
        TypeSymbol? wasScope = _scope, wasThis = _thisType, wasLexical = _lexicalType;
        MethodSymbol? wasMethod = _method;
        int wasQuiet = _quiet, wasFloor = _lambdaFloor;
        bool wasNaming = _namingOnly, wasTuples = _namingTuples;
        Dictionary<string, Type>? wasCaptured = _captured;
        Dictionary<string, ConstSym>? wasConstants = _capturedConstants;
        // The body's locals too: a constant's value is looked for among them
        // (Named), and one found below a lambda's floor is captured.
        LocalScope[]? wasScopes = _scopes.Count == 0 ? null : _scopes.ToArray();
        // And not inside a body while declaring: what its signatures name is
        // not a type a body used (ForceBody).
        bool wasInBodies = _inBodies;
        int errors = _r.Errors.Count;
        bool demanded = _declarationBatch.Any;

        _inBodies = false;
        _scopes.Clear();
        _lambdaFloor = -1;
        _captured = null;
        _capturedConstants = null;
        _member = null;
        _signature = null;
        _scope = null;
        _thisType = null;
        _lexicalType = null;
        _method = null;
        _quiet = 0;
        _namingOnly = false;
        _namingTuples = false;
        try
        {
            _in = d.File;
            // What the members pass does for it first, should it be asked
            // before that pass has reached it.
            if (_basesDeclared.Add(sym)) DeclareMembers(d, sym, members: false);
            Initialisers(d);
            StaticInitialisers(d);
            ImpliedConstructor(d);
            DeclareMembers(d, sym, bases: false);

            if (_constantsEvaluated)
            {
                foreach (var key in _constantDeclarations.Keys.Where(key => ReferenceEquals(key.Owner, sym)).ToList())
                    EvaluateConstant(key.Owner, key.Name);
            }

            if (_layingOut && !IsTemplate(sym))
            {
                LayOut(sym);
            }

            // ITS OWN BODIES, when it is a copy made per argument, checked
            // as every other type's are (BodiesNow); and what its descriptor
            // will name, declared while binding can still check it.
            // Not an imported declaration's, which has none to check
            // (CheckBodies passes over a signature-only one).
            if (d.Canon is null && !d.SignatureOnly) BodiesNow(d, sym);
            if (_contextsResolved) ForceContext(sym);
        }
        finally
        {
            _inBodies = wasInBodies;
            _in = wasIn;
            _member = wasMember;
            _signature = wasSignature;
            _scope = wasScope;
            _thisType = wasThis;
            _lexicalType = wasLexical;
            _method = wasMethod;
            _quiet = wasQuiet;
            _namingOnly = wasNaming;
            _namingTuples = wasTuples;
            _lambdaFloor = wasFloor;
            _captured = wasCaptured;
            _capturedConstants = wasConstants;
            _scopes.Clear();
            if (wasScopes is not null) _scopes.AddRange(wasScopes);
        }

        // AFTER BINDING THERE IS NO ONE TO TELL. A mistake found now would be
        // a message nobody prints, and a declaration wanted now one no pass
        // will load; neither can happen to members whose canonical copy was
        // declared without either, and if one does, it is said here rather
        // than compiled into something wrong.
        if (_bound && (_r.Errors.Count != errors || _declarationBatch.Any != demanded))
        {
            throw new InvalidOperationException($"the members of '{sym.Name}', declared after binding, "
                + (_r.Errors.Count != errors ? "were in error: " + _r.Errors[^1] : "wanted a declaration not loaded"));
        }
    }

    /// <summary>The body work is listed (CheckBodies' list, at the end of Run): a type declared from now on joins it.</summary>
    private bool _bodiesListed;

    /// <summary>The body work is done (CheckBodyWork): a type declared from now on is checked on its own (CheckLate).</summary>
    private bool _bodiesChecked;

    /// <summary>A body is being checked: a type it uses is one lowering may want (ForceBody).</summary>
    private bool _inBodies;

    /// <summary>Every specialisation's arguments and what its shared code makes are resolved (TemplateArgTypes, CanonMadeTypes).</summary>
    private bool _contextsResolved;

    /// <summary>
    /// The bodies of a copy made per argument whose members were just
    /// declared: checked with the others while the others are still to be
    /// checked, and on their own once they have been. Before the list is
    /// made there is nothing to do -- the type is no longer one whose members
    /// wait, and is listed with the rest. In a binding of the fresh bodies
    /// only, these are fresh: no binding has checked them before.
    /// </summary>
    private void BodiesNow(TypeDecl d, TypeSymbol sym)
    {
        if (_freshOnly)
        {
            foreach (MemberDecl m in d.Members) m.Fresh = true;
        }

        if (_bodiesChecked)
        {
            CheckLate(d, sym);
        }
        else if (_bodiesListed)
        {
            _bodyWork.Add((d, sym));
        }
    }

    /// <summary>
    /// A copy's bodies, checked after every other body was: a type first asked
    /// for by the checks that follow the bodies, or by lowering. WHAT BINDING
    /// CAN NO LONGER ANSWER STOPS THE COMPILE: once it is over, a generic
    /// method's copy wanted, an override, a synthesised delegate, static
    /// storage or a declaration is one no round will make, and an error one
    /// nobody prints -- said here rather than compiled into something wrong.
    /// Before it is over, they are recorded and answered as any body's are.
    /// </summary>
    private void CheckLate(TypeDecl d, TypeSymbol sym)
    {
        int wanted = _r.Wanted.Count, overrides = _r.WantedOverrides.Count, anonymous = _r.AnonymousDelegates.Count;
        int statics = _staticNext;
        bool reexpand = _r.Reexpand;

        _inBodies = true;
        try
        {
            _in = d.File;
            _closures = 0;
            CheckBodies(d, sym);
        }
        catch (Metadata.DeclarationDemand demand)
        {
            _declarationBatch.Add(demand);
        }
        finally
        {
            _inBodies = false;
        }
        SettleGenericCaptures();

        if (!_bound)
        {
            _declarationBatch.ThrowIfAny();
            return;
        }
        if (_r.Wanted.Count != wanted || _r.WantedOverrides.Count != overrides || _r.AnonymousDelegates.Count != anonymous
            || _r.Reexpand != reexpand || _staticNext != statics)
        {
            throw new InvalidOperationException($"the bodies of '{sym.Name}', first asked for after binding, "
                + "wanted a copy, a delegate or static storage that no round of binding can now make");
        }
    }

    /// <summary>
    /// A TYPE A BODY USES, made ready for lowering: a copy made per argument
    /// named in it, or in its arguments, has its members declared and its
    /// bodies checked now. Lowering lays down a descriptor for a class an
    /// expression holds, tests for or makes an array of -- its virtual
    /// methods with it -- and those are bodies that must have been checked;
    /// asked for only then, they could want a copy of a generic method no
    /// round is left to make (CheckLate). Once a symbol, so that the walk
    /// costs one test an expression.
    /// </summary>
    private void ForceBody(Type t)
    {
        while (t.IsArray && t.Element is Type element) t = element;
        if (t.Symbol is not TypeSymbol s || s.ReachedFromBodies) return;
        s.ReachedFromBodies = true;
        if (s.MembersPending && s.Decl is { Canon: null }) s.EnsureMembers();
        foreach (Type argument in s.TemplateArgTypes) ForceBody(argument);
    }

    /// <summary>
    /// What a type's descriptor names besides itself, declared with it: the
    /// classes its shared code makes for it (CanonMadeTypes) and, for a
    /// word-shaped copy, its arguments, whose descriptors its type context
    /// holds (Lowering.TypeContext). Lowering makes those descriptors with
    /// this one, and a copy per argument among them must be checked while
    /// binding still can (CheckLate).
    /// </summary>
    private void ForceContext(TypeSymbol t)
    {
        foreach (TypeSymbol? made in t.CanonMadeTypes)
        {
            made?.EnsureMembers();
        }
        if (t.Decl?.Canon is null) return;
        foreach (Type argument in t.TemplateArgTypes)
        {
            Type of = argument;
            while (of.IsArray && of.Element is Type element) of = element;
            // NOT AN IMPORTED CLASS WAITING TO BE READ AGAIN (Monomorphiser.
            // DeferImported): it has no bodies for binding to check, and
            // what declaring it asks of the index was asked where it was
            // declared without its members (DeclareArguments), so lowering
            // may declare it when it lays the descriptor down. Every
            // IEnumerable<Foo> a List<Foo> or a Foo[] brings in is such a
            // copy, and asked here, every class any loaded signature holds
            // in a collection was declared for nothing.
            if (of.Symbol is { Kind: TypeKind.Class } argumentClass && argumentClass.Decl?.SignatureTypes is null)
            {
                argumentClass.EnsureMembers();
            }
        }
    }

    /// <param name="bases">Its base class and interfaces.</param>
    /// <param name="members">Its fields, methods and properties -- or, left
    /// out, its type arguments resolved instead (DeclareArguments).</param>
    private void DeclareMembers(TypeDecl d, TypeSymbol sym, bool bases = true, bool members = true)
    {
        // A SIGNATURE IS WRITTEN INSIDE THIS TYPE, so a name in one may be a
        // type this type holds: `private Section _section;` in Assembler names
        // Assembler's own Section. Bodies get the same courtesy further down,
        // from _thisType; a signature is read before there is a body to be in.
        TypeSymbol? wasScope = _scope;
        _scope = sym;

        try
        {
            if (bases) DeclareBases(d, sym);
            if (members) DeclareMembersIn(d, sym);
            else DeclareArguments(d, sym);
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
    /// <summary>Whether a type was written in the class library's own sources (stdlib, runtime).</summary>
    private static bool IsClassLibrary(TypeDecl d) => d.SourcePath is string path && LibrarySource is { } isLibrary && isLibrary(path);

    /// <summary>Whether a type was written in the program's own sources: a file, and not the class library's (the prelude's are neither).</summary>
    private static bool IsProgramSource(TypeDecl d) => d.SourcePath is string path && LibrarySource is { } isLibrary && !isLibrary(path);

    /// <summary>Whether a field declaration carries [ThreadStatic], however it is spelt.</summary>
    internal static bool IsThreadStatic(FieldDecl f)
        => f.Attributes.Any(a => a.Name is "ThreadStatic" or "System.ThreadStatic" or "ThreadStaticAttribute" or "System.ThreadStaticAttribute");

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

    /// <summary>
    /// The type a base list names. WITH TYPE ARGUMENTS WRITTEN IT IS THE
    /// TEMPLATE OF THAT ARITY first: `IEnumerable<T>` is IEnumerable`1, and
    /// the bare name IEnumerable is the non-generic interface -- a different
    /// type, which the bare name tried first found instead.
    /// </summary>
    private bool FindBase(TypeRef b, out TypeSymbol? based)
    {
        if (b.Args.Count > 0
                ? FindType(Arity(b.Name, b.Args.Count), out based) || FindType(b.Name, out based)
                : FindType(b.Name, out based))
        {
            return true;
        }
        // A QUALIFIED BASE NAMES ITS LAST PART, as a qualified type does
        // anywhere else (ResolveCore): `class Demand : System.Exception` was
        // 'System.Exception is not a known type', while the same name as a
        // variable's type resolved. A library namespace deeper than System's
        // own is the library's, as there.
        int dot = b.Name.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }
        string bare = b.Name[(dot + 1)..];
        if (b.Name.StartsWith(LibraryHome + ".", StringComparison.Ordinal) && b.Name.IndexOf('.', LibraryHome.Length + 1) >= 0
            && TypeCandidate(b.Args.Count > 0 ? Arity(LibraryHome + "." + bare, b.Args.Count) : LibraryHome + "." + bare, out based)
            && based is not null)
        {
            return true;
        }
        return b.Args.Count > 0
            ? FindType(Arity(bare, b.Args.Count), out based) || FindType(bare, out based)
            : FindType(bare, out based);
    }

    /// <summary>The declaration of a type's base class, or null for an interface or nothing.</summary>
    private TypeDecl? BaseDeclOf(TypeDecl d)
    {
        foreach (TypeRef b in d.Bases)
        {
            if (FindBase(b, out TypeSymbol? based)
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

    /// <summary>
    /// The base class and interfaces a declaration names, onto its symbol --
    /// with every delegate's Delegate -- the part of declaring a type that
    /// one whose members wait to be asked for has done straight away.
    /// </summary>
    private void DeclareBases(TypeDecl d, TypeSymbol sym)
    {
        // An enum's underlying type was settled before any members were
        // declared (EnumUnderlying), so the checks below find it in place.

        // CHECKED AND THEN FALLS THROUGH, rather than returning: the member
        // values are worked out after it (DeclareMembersIn), and returning
        // here skipped them -- so every enum compiled to a type with no members
        // at all, and every use of one was 'ExitCode has no member Success'.
        foreach (TypeRef b in d.Kind == TypeKind.Enum ? Enumerable.Empty<TypeRef>() : d.Bases)
        {
            // FROM WHERE THE TYPE WAS WRITTEN, like any other name: a base in
            // the same namespace is named without it, and one in an imported
            // namespace is reached through the using that imported it.
            //
            // A GENERIC BASE IS KEYED BY ARITY, like any other template:
            // `List<T> : IReadOnlyList<T>` names IReadOnlyList`1 (FindBase).
            if (!FindBase(b, out TypeSymbol? based))
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
                sym.WritableInterfaces.Add(based);
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

        // EVERY DELEGATE TYPE IS A Delegate, as every .NET delegate type
        // derives from System.Delegate: the runtime declares it (an interface
        // with GetInvocationList and the statics Combine, Remove and
        // RemoveAll), and nothing writes it in a delegate's declaration, so it
        // is put in here. An Action converts to a Delegate, an array of
        // Delegates holds one, and its members are found on any delegate
        // (MethodsOn). A program with no runtime has no Delegate, and its
        // delegates are what they were.
        if (d.IsDelegate && DelegateRoot() is { } root && !ReferenceEquals(root, sym) && !sym.Interfaces.Contains(root))
        {
            sym.WritableInterfaces.Add(root);
        }
    }

    /// <summary>
    /// A specialisation's type arguments resolved, as its members' signatures
    /// would resolve them, and to no other end: a declaration one of them
    /// needs is asked of the index (DeclarationBatch) while binding can still
    /// go round again for it. Quietly, as a type whose members name none of
    /// its arguments would never have said anything about one; and marking
    /// nothing used, as a specialisation's signatures mark nothing
    /// (BindingElsewhere).
    /// </summary>
    private void DeclareArguments(TypeDecl d, TypeSymbol sym)
    {
        _quiet++;
        try
        {
            foreach (TypeRef argument in d.TemplateArgs)
            {
                Resolve(argument, sym);
            }

            // AND AN IMPORTED DECLARATION'S SIGNATURES, whose members wait
            // to be read again (Monomorphiser.DeferImported): every type they
            // name, resolved here, where declaring them resolved each before.
            // A declaration one needs is asked of the index while the pass
            // can still go round for it, and the shapes they make -- a tuple,
            // a nullable struct -- are made where they always were, in the
            // same order; nothing is marked used, the scope being an imported
            // type's (BindingElsewhere).
            // EACH ON ITS OWN, as each member is declared on its own: one
            // missing declaration does not keep the rest from being asked.
            if (d.SignatureTypes is { } signatures)
            {
                foreach (TypeRef written in signatures)
                {
                    try { Resolve(written, sym); }
                    catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); }
                }
            }
        }
        finally { _quiet--; }
    }

    /// <summary>A declaration's fields, methods, properties and enum values, onto its symbol.</summary>
    private void DeclareMembersIn(TypeDecl d, TypeSymbol sym)
    {
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
                // ONE NAME, ONE MEMBER (CS0102), as for fields: taken quietly,
                // the second silently replaced the first's value -- win32k's
                // Win32Error had PrivateDialogIndex twice, which C# refuses.
                if (sym.EnumValues.ContainsKey(m.Name))
                {
                    Error(m, $"'{sym.Name}' already has a member called '{m.Name}'");
                    next++;
                    continue;
                }
                sym.WritableEnumValues[m.Name] = next++;
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
                    // A loop, and the backing field's name matched in place:
                    // `"<" + name + ">"` was made for every field already
                    // declared, for every field declared.
                    bool declaredTwice = false;
                    foreach (FieldSymbol had in sym.Fields)
                    {
                        declaredTwice |= had.Name == f.Name || TypeSymbol.IsBackingName(had.Name, f.Name);
                    }
                    if (declaredTwice)
                    {
                        Error(f, $"'{sym.Name}' already has a member called '{f.Name}'");
                        break;
                    }

                    sym.WritableFields.Add(new FieldSymbol
                    {
                        Name = f.Name, Type = Resolve(f.Type, sym), Owner = sym,
                        Static = f.Mods.HasFlag(Mods.Static),
                        Volatile = f.Mods.HasFlag(Mods.Volatile),
                        IsEvent = f.IsEvent,
                        ThreadStatic = f.Mods.HasFlag(Mods.Static) && IsThreadStatic(f),
                        Required = f.Mods.HasFlag(Mods.Required),
                        Initialised = f.DeclaredInit is not null || f.Init is not null,
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
                        sym.WritableFields.Add(new FieldSymbol
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
                        getBody.WritableStatements.Add(new ReturnStmt
                        {
                            Value = new NameExpr { Name = p.Name, Line = p.Line, Col = p.Col },
                            Line = p.Line, Col = p.Col,
                        });

                        if (p.HasSetter)
                        {
                            setBody = new Block { Line = p.Line, Col = p.Col };
                            setBody.WritableStatements.Add(new ExprStmt
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
                    // An interface's accessor is abstract when it has no body to
                    // run; one with a body is a default implementation (C# 8).
                    bool aAbstract = p.Mods.HasFlag(Mods.Abstract)
                        || sym.Kind == TypeKind.Interface && !p.Mods.HasFlag(Mods.Static) && getBody is null;
                    bool setAbstract = p.Mods.HasFlag(Mods.Abstract)
                        || sym.Kind == TypeKind.Interface && !p.Mods.HasFlag(Mods.Static) && setBody is null;

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
                            Name = NameTable.Accessor("get_", p.Name), Mods = p.Mods, Returns = p.Type,
                            Body = getBody, Line = p.Line, Col = p.Col,
                            TemplateIndex = p.TemplateIndex,
                            VtableSlotHint = p.VtableSlotHint,
                            OwnedImplementation = p.OwnedImplementation, File = p.File, Scope = p.Scope, Namespace = p.Namespace,
                            Fresh = p.Fresh, AutoAccessor = p.Auto && !abstractAccessors,
                        };

                        MethodSymbol gs = new()
                        {
                            Name = p.ExplicitInterface is null ? getter.Name : p.ExplicitInterface + "." + getter.Name,
                            ExplicitInterface = p.ExplicitInterface,
                            ExplicitMember = p.ExplicitInterface is null ? null : getter.Name,
                            Returns = propType, Owner = sym,
                            Static = p.Mods.HasFlag(Mods.Static), Decl = getter,
                            Virtual = aVirtual, Override = aOverride, Abstract = aAbstract,
                        };

                        // AN INDEXER'S PARAMETERS COME FIRST, which is the whole
                        // of what makes get_Item different from get_Name.
                        foreach (Param ip in p.Params)
                        {
                            getter.WritableParams.Add(ip);
                            gs.WritableParams.Add(new ParamSymbol { Name = ip.Name, Type = Resolve(ip.Type, sym) });
                        }
                        sym.WritableMethods.Add(gs);
                        _r.Methods[getter] = gs;
                        Synthesised(sym, getter);
                    }

                    if (setBody != null || (declared && p.HasSetter))
                    {
                        MethodDecl setter = new()
                        {
                            Name = NameTable.Accessor("set_", p.Name), Mods = p.Mods, Returns = null,
                            Body = setBody, Line = p.Line, Col = p.Col,
                            TemplateIndex = p.TemplateIndex,
                            VtableSlotHint = p.VtableSlotHint,
                            OwnedImplementation = p.OwnedImplementation, File = p.File, Scope = p.Scope, Namespace = p.Namespace,
                            Fresh = p.Fresh, AutoAccessor = p.Auto && !abstractAccessors,
                        };
                        // The indices, and THEN the value -- `set_Item(i, v)`,
                        // which is the order C# uses and the order the use site
                        // below builds its arguments in.
                        foreach (Param ip in p.Params)
                        {
                            setter.WritableParams.Add(ip);
                        }

                        setter.WritableParams.Add(new Param { Name = "value", Type = p.Type, Line = p.Line, Col = p.Col });

                        MethodSymbol ss = new()
                        {
                            Name = p.ExplicitInterface is null ? setter.Name : p.ExplicitInterface + "." + setter.Name,
                            ExplicitInterface = p.ExplicitInterface,
                            ExplicitMember = p.ExplicitInterface is null ? null : setter.Name,
                            Returns = Type.Void, Owner = sym,
                            Static = p.Mods.HasFlag(Mods.Static), Decl = setter,
                            Virtual = aVirtual, Override = aOverride, Abstract = setAbstract,
                        };

                        foreach (Param ip in p.Params)
                        {
                            ss.WritableParams.Add(new ParamSymbol { Name = ip.Name, Type = Resolve(ip.Type, sym) });
                        }
                        ss.WritableParams.Add(new ParamSymbol { Name = "value", Type = propType });
                        sym.WritableMethods.Add(ss);
                        _r.Methods[setter] = ss;
                        Synthesised(sym, setter);
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
                    _signature = md.TypeParams;

                    MethodSymbol ms = new()
                    {
                        Name = md.ExplicitInterface is null ? md.Name : md.ExplicitInterface + "." + md.Name,
                        ExplicitInterface = md.ExplicitInterface,
                        ExplicitMember = md.ExplicitInterface is null ? null : md.Name,
                        Returns = md.IsCtor ? Type.Void : Resolve(md.Returns!, sym),
                        Owner = sym,
                        Static = md.Mods.HasFlag(Mods.Static),
                        Virtual = md.Mods.HasFlag(Mods.Virtual),
                        Override = md.Mods.HasFlag(Mods.Override),
                        // IN AN INTERFACE, abstract unless it has a body: one with a
                        // body is a default implementation (C# 8), and a static
                        // one is an ordinary static method.
                        Abstract = md.Mods.HasFlag(Mods.Abstract)
                            || d.Kind == TypeKind.Interface && !md.Mods.HasFlag(Mods.Static) && md.Body is null,
                        Async = md.Mods.HasFlag(Mods.Async),
                        IsCtor = md.IsCtor,
                        Decl = md,
                    };
                    if (md.TypeParams.Count > 0) AddNames(ms.WritableTypeParamNames, md.TypeParams);

                    for (int pi = 0; pi < md.Params.Count; pi++)
                    {
                        Param p = md.Params[pi];
                        Type resolved = Resolve(p.Type, sym);
                        ms.WritableParams.Add(new ParamSymbol
                        {
                            Name = p.Name,
                            Type = resolved,
                            ByRef = p.IsRef || p.IsOut,
                            ReadOnly = p.IsReadOnlyRef,
                            IsParams = p.IsParams,
                            // A generic local function's captured variable,
                            // by the address of its cell (ParamSym.Cell), a
                            // struct's too: the cell holds the struct.
                            Cell = pi < md.Captures,
                            CapturedVariable = pi < md.Captures,
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
                    // A CONVERSION OPERATOR is told apart by what it returns
                    // as well (C# 15.10.4): JsonNode converts to bool, int,
                    // long and double from the one parameter.
                    bool conversion = ms.Name is "op_Implicit" or "op_Explicit";
                    // A loop over the methods already declared, a name compared
                    // before anything else: as a FirstOrDefault it was a
                    // closure for every method of every type.
                    MethodSymbol? twin = null;
                    foreach (MethodSymbol had in sym.Methods)
                    {
                        if (had.Decl == md || had.Name != ms.Name
                            || had.TypeParams.Count != ms.TypeParams.Count || had.Params.Count != ms.Params.Count) continue;
                        bool same = true;
                        for (int k = 0; k < had.Params.Count && same; k++)
                        {
                            same = MethodSignatures.SameType(had.Params[k].Type, ms.Params[k].Type)
                                && had.Params[k].ByRef == ms.Params[k].ByRef;
                        }
                        if (same && (!conversion || MethodSignatures.SameType(had.Returns, ms.Returns))
                            && !(had.Decl is MethodDecl hd && hd.Mods.HasFlag(Mods.Partial)) && !md.Mods.HasFlag(Mods.Partial))
                        {
                            twin = had;
                            break;
                        }
                    }
                    if (twin != null)
                    {
                        Error(md, $"'{sym.Name}' already defines a member called '{md.Name}' with the same parameter types");
                        // Its body is still checked (its own mistakes are
                        // said too); it is only not a member of the type.
                        _r.Methods[md] = ms;
                        break;
                    }

                    sym.WritableMethods.Add(ms);
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
    /// <summary>MethodsOn(owner, prefix + name), the name never joined: a property's accessors by its name.</summary>
    private static List<MethodSymbol> MethodsOn(TypeSymbol owner, string prefix, string name)
    {
        List<MethodSymbol> found = owner.FindMethods(prefix, name);

        if (found.Count > 0 || owner.Kind != TypeKind.Interface)
        {
            return found;
        }

        foreach (TypeSymbol face in Extended(owner))
        {
            List<MethodSymbol> up = face.FindMethods(prefix, name);

            if (up.Count > 0)
            {
                return up;
            }
        }
        return found;
    }

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

    /// <summary>
    /// Whether a field of type `t` is held in line (FieldSymbol.Inline): a
    /// struct -- held by value, not a pointer to one or a nullable cell --
    /// every instance field of which is a number, a bool, a char, an enum, a
    /// pointer, a reference, or a struct held in line itself. A copy of it is
    /// its bytes. Its references are found where the object holding it is
    /// described to the collector (TracedFields), and written over with the
    /// barrier each one needs (StorePlace).
    ///
    /// A REFERENCE IN IT WAS ONCE REFUSED, and every struct holding one was
    /// a block of its own wherever another held it: a KeyValuePair of a key
    /// and a struct value, made by a dictionary's enumerator for each entry,
    /// was a second allocation each time, and so was every copy.
    /// </summary>
    private bool InlineStruct(Type t)
    {
        // A NULLABLE VALUE IS Nullable<T>, a struct of its own (BindResult.
        // NullableShape), held in line where T can be: a number, an enum, or
        // a struct held in line itself.
        if (t.IsNullableValue && !t.IsPointer && !t.IsArray)
        {
            Type under = t.Underlying;
            if (under.ParamName != null || under.IsError || under.IsReference || under.Prim == Prim.Any) return false;
            if (under.Symbol is { Kind: TypeKind.Struct } held)
            {
                LayOut(held);
                return held.HeldInline;
            }
            return under.Size > 0;
        }
        if (t.IsPointer || t.IsArray || t.Nullable || t.IsNullableValue) return false;
        if (t.Symbol is not { Kind: TypeKind.Struct } sym) return false;
        if (t.ParamName != null) return false;
        LayOut(sym);
        return HoldsInline(sym);
    }

    /// <summary>Whether a laid-out struct can be held in line (InlineStruct), from its fields.</summary>
    /// <summary>
    /// An expression moved one pattern deeper: each subject it reads named
    /// one further out. The right side of a tuple `==` is evaluated inside
    /// the left side's pattern, and when it is itself an item of an outer
    /// comparison's subject -- `((1, 2), 3) == ((1, 2), 3)` -- it read the
    /// left item where it meant the right.
    /// </summary>
    private static Expr Deeper(Expr e) => e switch
    {
        SubjectExpr s => new SubjectExpr { Outer = s.Outer + 1, Line = s.Line, Col = s.Col, File = s.File },
        MemberExpr { Target: var inner } m when Reads(inner) => new MemberExpr { Target = Deeper(inner), Name = m.Name, Guarded = m.Guarded, Line = m.Line, Col = m.Col, File = m.File },
        _ => e,
    };

    /// <summary>Whether a chain of member reads begins at a pattern's subject.</summary>
    private static bool Reads(Expr e) => e is SubjectExpr || e is MemberExpr { Target: var inner } && Reads(inner);

    /// <summary>How many items a tuple type has; null for anything else, a nullable tuple too.</summary>
    private static int? TupleArity(Type t)
        => !t.Nullable && !t.IsPointer && !t.IsArray && t.Symbol is { Kind: TypeKind.Struct } shape && shape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            ? shape.Fields.Count(f => !f.Static) : null;

    private static bool HoldsInline(TypeSymbol sym)
    {
        if (sym.InlineDecided && sym.InstanceSize <= 0) return false;
        foreach (FieldSymbol f in sym.Fields.Where(f => !f.Static))
        {
            if (f.Boxed) return false;
            if (f.Inline) continue;
            Type ft = f.Type;
            if (ft.IsPointer) continue;
            if (ft.Symbol is { Kind: TypeKind.Enum }) continue;
            if (ft.ParamName != null || ft.IsNullableValue) return false;
            if (ft.IsReference || ft.Prim is Prim.Any) continue;
            if (ft.Symbol != null || ft.IsArray || ft.Nullable) return false;
            if (ft.Prim is Prim.Bool or Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64
                or Prim.NInt or Prim.NUInt or Prim.F32 or Prim.F64 or Prim.Char) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A struct's alignment where it is held in line: its widest field's,
    /// at most eight. Its size is left as it is -- a struct's bytes are what
    /// sizeof and every copy of it say -- and each one held in line is put
    /// at this alignment.
    /// </summary>
    private int InlineAlignOf(TypeSymbol sym)
    {
        int widest = 1;
        foreach (FieldSymbol f in sym.Fields.Where(f => !f.Static))
            widest = Math.Max(widest, f.Inline ? Math.Max(1, _r.StructOf(f.Type).InlineAlign) : Math.Min(8, Math.Max(1, f.Type.Size)));
        return Math.Min(8, widest);
    }

    /// <summary>Assigns field offsets and vtable slots.</summary>
    private void LayOut(TypeSymbol sym)
    {
        // Its members first, if they were left to be asked for: declared now,
        // and laid out with them once layout has begun (DeclareMembersNow).
        sym.EnsureMembers();
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
            // Its fields held in line are known by the same rule the library
            // laid them out by (InlineStruct), from their types alone.
            if (!sym.InlineDecided)
            {
                sym.InlineDecided = true;
                foreach (FieldSymbol f in sym.Fields.Where(f => !f.Static)) f.Inline = !f.Boxed && InlineStruct(f.Type);
                if (sym.Kind == TypeKind.Struct)
                {
                    sym.InlineAlign = InlineAlignOf(sym);
                    sym.HeldInline = HoldsInline(sym);
                }
            }
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

        sym.InlineDecided = true;
        foreach (FieldSymbol f in sym.Fields.Where(f => !f.Static))
        {
            int size = f.Type.Size;

            if (size <= 0)
            {
                throw new InvalidOperationException(
                    $"{sym.Name}.{f.Name}: a field of type '{f.Type}' has no size to lay out "
                    + $"(prim {(int)f.Type.Prim}, parameter '{f.Type.ParamName}', declared in {sym.Decl?.File})");
            }

            // A struct holding no reference is laid out in line: its own
            // bytes, at its own alignment, rather than a word pointing at a
            // block made for it (FieldSymbol.Inline).
            f.Inline = !f.Boxed && InlineStruct(f.Type);
            int align = size;
            if (f.Inline)
            {
                TypeSymbol held = _r.StructOf(f.Type);
                size = Math.Max(1, held.InstanceSize);
                align = Math.Max(1, held.InlineAlign);
            }

            at = (at + align - 1) / align * align;   // natural alignment
            f.Offset = at;
            at += size;
        }

        sym.InstanceSize = Math.Max(at,
            sym.Kind == TypeKind.Class ? Target.Current.ObjectHeaderBytes : 1);

        if (sym.Kind == TypeKind.Struct)
        {
            sym.InlineAlign = InlineAlignOf(sym);
            sym.HeldInline = HoldsInline(sym);
        }

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
    /// <summary>
    /// A type's name as source writes it bare: `IEnumerable` for the template
    /// IEnumerable`1 and for a specialisation of it -- what an explicit
    /// implementation's qualifier is compared with.
    /// </summary>
    /// <summary>
    /// What an iterator returning this type yields: the T of IEnumerable&lt;T&gt;
    /// or IEnumerator&lt;T&gt;, object for the non-generic IEnumerable and
    /// IEnumerator, and null for anything else -- which cannot be an iterator.
    /// Read off the type's own members, so a specialisation answers with its
    /// argument in: IEnumerator&lt;T&gt;'s Current, or GetEnumerator's.
    /// </summary>
    internal static Type? IteratorElement(Type sequence)
    {
        if (sequence.Symbol is not TypeSymbol shape || shape.Kind != TypeKind.Interface) return null;
        string name = PlainName(shape.Decl?.Template ?? shape.Name);
        if (name is not ("IEnumerable" or "IEnumerator")) return null;
        TypeSymbol? enumerator = name == "IEnumerator" ? shape
            : shape.Methods.FirstOrDefault(m => m.Name == "GetEnumerator" && m.Params.Count == 0)?.Returns.Symbol;
        return enumerator?.Methods.FirstOrDefault(m => m.Name == "get_Current" && m.Params.Count == 0)?.Returns;
    }

    /// <summary>
    /// How an explicit implementation names this interface: its plain name
    /// and, when generic, its arity -- IEnumerable`1 for IEnumerable&lt;T&gt;, told
    /// apart from IEnumerable (Parser, explicit implementations).
    /// </summary>
    private static string ExplicitName(TypeSymbol iface)
    {
        int arity = iface.Decl is { TemplateArgs.Count: > 0 } made ? made.TemplateArgs.Count : iface.Decl?.TypeParams.Count ?? 0;
        string plain = PlainName(iface.Decl?.Template ?? iface.Name);
        return arity == 0 ? plain : plain + "`" + arity;
    }

    private static string PlainName(string name)
    {
        int cut = name.IndexOfAny(new[] { '`', '$', '<' });
        string bare = cut < 0 ? name : name[..cut];
        int dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare[(dot + 1)..];
    }

    private void AssignSlots(TypeSymbol sym)
    {
        sym.EnsureMembers();
        if (sym.SlotsAssigned) return;
        sym.SlotsAssigned = true;
        // THE BASE FIRST. An override takes the slot of the method it
        // overrides, and a new virtual the next above the base's: read before
        // the base was numbered, both came out of thin air. A unit that met
        // ArgumentException before Exception (abi.cor, throwing one) gave its
        // Message a slot of its own, and the library's link refused the two
        // layouts.
        if (sym.Base is TypeSymbol basis) AssignSlots(basis);
        // A class's own virtual methods are numbered above the interface
        // region -- the LIBRARY's region for a library class, so that it gets
        // the numbers its own build gave it whatever this compilation adds.
        int slot = IsLibraryType(sym) ? _librarySlots : _projectClassSlots;

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
                // A GENERIC METHOD OF THE INTERFACE IS DISPATCHED BY TYPE TEST,
                // not through a slot (MethodSymbol.GenericVirtual), and a copy
                // of one made in this unit is no member of the interface's ABI.
                if (want.TypeParams.Count > 0 || want.Decl?.LocalCopy == true) continue;
                // Merely hiding a base member does not remap an inherited
                // interface. An override does; naming the interface again
                // explicitly requests a fresh implementation search.
                if (!reimplements && sym.Base is not null
                    && sym.Base.InterfaceImplementations.TryGetValue(want.VtableSlot, out MethodSymbol? inherited))
                {
                    MethodSymbol? replacement = sym.Methods.FirstOrDefault(m => m.Override
                        && m.Name == inherited.Name && MethodSignatures.Implements(m, inherited));
                    sym.WritableInterfaceImplementations[want.VtableSlot] = replacement ?? inherited;
                    continue;
                }
                // AN EXPLICIT IMPLEMENTATION FOR THIS INTERFACE FIRST, as C#
                // takes one over a public member of the same name.
                // BY THE TEMPLATE IT WAS MADE FROM when it is a copy: a copy of a
                // namespaced interface is called `System$Collections$Concurrent$
                // IProducerConsumerCollection$__canon`, whose plain name cut at
                // the first '$' is "System".
                string ifaceName = ExplicitName(iface);
                MethodSymbol? impl = sym.Methods.FirstOrDefault(m => m.ExplicitMember == want.Name
                        && m.ExplicitInterface == ifaceName && !m.Abstract && MethodSignatures.Implements(m, want))
                    ?? sym.FindMethods(want.Name).FirstOrDefault(m => !m.Abstract && MethodSignatures.Implements(m, want));

                // A RE-IMPLEMENTED INTERFACE MAPS TO A BASE'S EXPLICIT
                // IMPLEMENTATION TOO (C# 18.6.6): `class OrderedList<T> :
                // List<T>, IOrderedEnumerable<T>` names IEnumerable<T> again,
                // and List<T>'s IEnumerable<T>.GetEnumerator answers it, its
                // public one returning the struct Enumerator not matching.
                for (TypeSymbol? t = sym.Base; impl is null && t != null; t = t.Base)
                {
                    impl = t.Methods.FirstOrDefault(m => m.ExplicitMember == want.Name
                        && m.ExplicitInterface == ifaceName && !m.Abstract && MethodSignatures.Implements(m, want));
                }
                if (impl is null && sym.Base is not null
                    && sym.Base.InterfaceImplementations.TryGetValue(want.VtableSlot, out MethodSymbol? mapped))
                {
                    impl = mapped;
                }

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

                // A DEFAULT IMPLEMENTATION (C# 8): the interface's own body,
                // when nothing on the class answers -- the most specific one,
                // so an interface that re-implements the member explicitly
                // (`string IGreeter.Greet() => …` in a derived interface) is
                // taken before the one that declared it.
                if (impl is null && sym.Kind != TypeKind.Interface)
                {
                    impl = AllInterfaces(sym).Select(face => face.Methods.FirstOrDefault(m => m.ExplicitMember == want.Name
                                   && m.ExplicitInterface == ifaceName && m.Decl?.Body is not null
                                   && MethodSignatures.Implements(m, want)))
                               .FirstOrDefault(found => found is not null)
                        ?? (want.Decl?.Body is not null ? want : null);
                }

                if (impl is null)
                {
                    if (sym.Decl != null)
                    {
                        Error(sym.Decl, $"'{sym.Name}' does not implement '{iface.Name}.{want.Name}({want.Params.Count} args)'");
                    }
                    continue;
                }
                sym.WritableInterfaceImplementations[want.VtableSlot] = impl;
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
        // NOT A GENERIC ONE: a slot holds one address, and a generic virtual
        // method has one per type argument. See MethodSymbol.GenericVirtual.
        foreach (MethodSymbol m in sym.Methods.Where(m =>
                     (m.Virtual || m.Override || m.Abstract)
                     && !m.Static && m.VtableSlot < 0 && m.TypeParams.Count == 0))
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
            //
            // A REFERENCE TYPE'S `?` IS NO PART OF THE SIGNATURE: C# lets
            // `override Write(string value)` override `Write(string? value)`,
            // with a nullability warning at most (MethodSignatures.SameType).
            MethodSymbol? overridden = sameArity
                .FirstOrDefault(b => b.Params.Zip(m.Params).All(p => p.First.Type.Equals(p.Second.Type)
                    || MethodSignatures.SameType(p.First.Type, p.Second.Type)));
            overridden ??= sameArity.Count == 1 ? sameArity[0] : null;

            m.VtableSlot = overridden is { VtableSlot: >= 0 } ? overridden.VtableSlot : slot++;
        }
    }

    // ---- types ----------------------------------------------------------

    /// <summary>
    /// THE TYPES OF A LIST OF REFERENCES, resolved in a loop: Select over a
    /// lambda made an iterator and a closure every time a type with arguments
    /// was resolved, and the binder resolves them by the hundred thousand.
    /// </summary>
    private IReadOnlyList<Type> ResolveAll(List<TypeRef> refs, TypeSymbol? context)
    {
        // None: the one shared empty list, not the empty array, which became
        // a view of its own at every Args it was given to -- 137 thousand.
        if (refs.Count == 0) return Type.NoArgs;
        Type[] made = new Type[refs.Count];
        for (int i = 0; i < made.Length; i++) made[i] = Resolve(refs[i], context);
        return made;
    }

    /// <summary>Whether the named one of these type parameters is `where T : struct`.</summary>
    private static bool StructIn(List<TypeParam>? parameters, string name)
    {
        if (parameters is null) return false;
        foreach (TypeParam p in parameters)
        {
            if (p.Name == name) return p.Struct;
        }
        return false;
    }

    /// <summary>The names of type parameters, added in a loop (as ResolveAll).</summary>
    private static void AddNames(List<string> into, List<TypeParam> parameters)
    {
        foreach (TypeParam p in parameters) into.Add(p.Name);
    }

    private Type Resolve(TypeRef r, TypeSymbol? context)
    {
        Type resolved = ResolveWritten(r, context);
        // A TYPE WRITTEN IN A BODY -- a cast, a test, typeof, an array made
        // of it -- is one lowering lays down a descriptor for: its members
        // are declared now, whatever kind of copy it is, and what that
        // descriptor names with them (ForceBody, ForceContext).
        if (_inBodies)
        {
            Type of = resolved;
            while (of.IsArray && of.Element is Type element) of = element;
            of.Symbol?.EnsureMembers();
            ForceBody(resolved);
        }
        return resolved;
    }

    private Type ResolveWritten(TypeRef r, TypeSymbol? context)
    {
        // A FUNCTION POINTER: an nint that knows its signature.
        Type baseType = r.IsFunctionPointer && r.Args.Count > 0
            ? new Type
            {
                Prim = Prim.NInt,
                Function = new FunctionPointer(r.Args.Take(r.Args.Count - 1).Select(p => Resolve(p, context)).ToList(),
                    Resolve(r.Args[^1], context), r.Name == TypeRef.UnmanagedFunction),
            }
            : ResolveCore(r, context);

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

    /// <summary>
    /// A named type as Resolve finds it: the symbol's one shared plain type
    /// (Type.Plain) when it is written with no arguments, a new one otherwise.
    /// </summary>
    private static Type NamedType(TypeSymbol symbol, IReadOnlyList<Type> args, IReadOnlyList<Type>? useArgs)
    {
        Prim prim = symbol.Kind == TypeKind.Enum ? symbol.EnumUnderlying : Prim.Void;
        if (args.Count == 0 && useArgs is null) return Type.Plain(symbol, prim);
        return new Type { Prim = prim, Symbol = symbol, Args = args, UseArgs = useArgs };
    }

    private Type ResolveCore(TypeRef r, TypeSymbol? context)
    {
        // A TUPLE TYPE is the class this writes for its shape, carrying the
        // element names it was written with. See TupleType.
        if (r.Name == TypeRef.Tuple && r.Args.Count > 1)
        {
            List<Type> elements = new(ResolveAll(r.Args, context));

            return new Type
            {
                Prim = Prim.Void,
                Symbol = TupleType(elements, r.TupleNames),
                Names = r.TupleNames?.ToArray(),
                UseArgs = elements,
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
            // A SHARED METHOD COPY'S TYPE ARGUMENT is object, carrying which
            // of its type parameters it is (TypeRef.CanonIndex).
            case "object": return Type.CanonAny(r.CanonIndex);

            // C#'S `dynamic`: object, with its operations bound when the
            // program runs (Type.Dynamic; Binder.Dynamic.cs).
            case "dynamic": return Type.DynamicAny;

            // .NET'S NAMES FOR THE SAME TYPES, bare or with their namespace:
            // `string` is an alias for System.String and `int` for
            // System.Int32 (C# 8.2.1), so `String s` and `s is System.String`
            // mean exactly what `string` does. As types only -- in an
            // expression `String.Join` still finds the library's static
            // class of that name, which carries the members.
            case "String" or "System.String": return Type.String;
            case "Object" or "System.Object": return Type.Any;
            case "Boolean" or "System.Boolean": return Type.Bool;
            case "SByte" or "System.SByte": return Type.I8;
            case "Byte" or "System.Byte": return Type.U8;
            case "Int16" or "System.Int16": return Type.I16;
            case "UInt16" or "System.UInt16": return Type.U16;
            case "Int32" or "System.Int32": return Type.I32;
            case "UInt32" or "System.UInt32": return Type.U32;
            case "Int64" or "System.Int64": return Type.I64;
            case "UInt64" or "System.UInt64": return Type.U64;
            case "IntPtr" or "System.IntPtr": return Type.NInt;
            case "UIntPtr" or "System.UIntPtr": return Type.NUInt;
            case "Single" or "System.Single": return Type.F32;
            case "Double" or "System.Double": return Type.F64;
            case "Char" or "System.Char": return Type.Char;

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
            //
            // And which of the copy's parameters it is, where the copy was
            // written so (TypeRef.CanonIndex, Type.CanonParam).
            case Monomorphiser.CanonName: return Type.CanonAny(r.CanonIndex);

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
            case "Type" when MeansSystemType(): return Type.TypeHandle;
        }

        if (context != null && context.TypeParams.Contains(r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name, StructParam = StructIn(context.Decl?.TypeParams, r.Name) };
        }

        if (_method != null && _method.TypeParams.Contains(r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name, StructParam = StructIn(_method.Decl?.TypeParams, r.Name) };
        }

        // The same, for a signature being declared -- there is no method symbol
        // to ask yet, because this is what is building one.
        if (_signature != null && _signature.Exists(p => p.Name == r.Name))
        {
            return new Type { Prim = Prim.Void, ParamName = r.Name, StructParam = StructIn(_signature, r.Name) };
        }

        // A NAME THE MONOMORPHISER HAS ALREADY SPELT OUT IN FULL (TypeRef.
        // Resolved) is that type, wherever it is read: a program's `Version`
        // spliced into ReadOnlyCollection<T> is not System.Version because
        // ReadOnlyCollection is declared in a namespace beneath System.
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
                Args = ResolveAll(r.Args, context),
            };
        }

        if (FindType(r.Name, out TypeSymbol? path) && path is not null)
        {
            return NamedType(path, ResolveAll(r.Args, context), r.UseArgs is null ? null : ResolveAll(r.UseArgs, context));
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

        // A NAME QUALIFIED BY A SYSTEM NAMESPACE is the library's, where a
        // program has taken its simple name (MoveToSystem): `System.Index`
        // beside a `class Index` of the program's.
        if (r.Name.StartsWith(LibraryHome + ".", StringComparison.Ordinal) && r.Name.IndexOf('.', LibraryHome.Length + 1) >= 0
            && TypeCandidate(r.Args.Count > 0 ? Arity(LibraryHome + "." + bare, r.Args.Count) : LibraryHome + "." + bare, out TypeSymbol? system)
            && system is not null)
        {
            return NamedType(system, ResolveAll(r.Args, context), null);
        }

        // `System.X` FOR ONE OF THE LIBRARY'S GLOBAL TYPES, which are System's:
        // the library's, whatever the program's own namespace calls X. A
        // program may declare a `Delegate` of its own beside its delegates,
        // and `System.Delegate` -- which the multicast class the parser writes
        // for each of them names (Parser.Multicast) -- is still the runtime's.
        // Read by its last part from where it was written, it was the
        // program's.
        if (r.Name == LibraryHome + "." + bare
            && _r.Types.TryGetValue(r.Args.Count > 0 ? Arity(bare, r.Args.Count) : bare, out TypeSymbol? systemGlobal)
            && systemGlobal.Decl is { } systemDecl && systemDecl.Outer is null
            && (systemDecl.Namespace.Length == 0 || systemDecl.Namespace == LibraryHome))
        {
            return NamedType(systemGlobal, ResolveAll(r.Args, context), null);
        }

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
                Args = ResolveAll(r.Args, context),
            };
        }

        // FROM WHERE IT WAS WRITTEN: a name inside a type may be one of that
        // type's own, and only then is it a name the whole program shares.
        if (FindType(bare, out TypeSymbol? sym) && sym is not null)
        {
            return NamedType(sym, ResolveAll(r.Args, context), r.UseArgs is null ? null : ResolveAll(r.UseArgs, context));
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

    /// <summary>
    /// Only the bodies of Fresh members are checked: a binding done between
    /// the rounds that make generic copies, to learn which copies the new
    /// ones want. Everything else was checked in an earlier round and wants
    /// nothing new; the full binding after the last round checks it all.
    /// </summary>
    private bool _freshOnly;

    private void CheckBodies(TypeDecl d, TypeSymbol sym)
    {
        _thisType = sym;
        _member = null;

        List<MethodDecl> bodies = d.Members.OfType<MethodDecl>().ToList();
        if (_synthesised.TryGetValue(sym, out List<MethodDecl>? accessors))
        {
            bodies.AddRange(accessors.Where(x => _r.Methods.TryGetValue(x, out MethodSymbol? ms) && ReferenceEquals(ms.Owner, sym)));
        }

        foreach (MethodDecl md in bodies)
        {
            _member = md;

            if (md.Body is null || (!md.LocalCopy && (md.OwnedImplementation == false || d.SignatureOnly))
                || (_freshOnly && !md.Fresh))
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
            // ITS FRAME USES ITS SIGNATURE'S TYPES, which no expression need
            // name: a struct parameter held in line is laid out with the
            // frame lowering builds for it (ForceBody).
            foreach (ParamSymbol p in _method.Params) ForceBody(p.Type);
            ForceBody(_method.Returns);

            // AN ENTRY TAKING ITS ARGUMENTS reads them through
            // Environment.GetCommandLineArgs, which the entry stub calls
            // though no source names it (Lowering.EntryArgs). Declarations
            // come from the library's index as they are named, so a program
            // that never wrote `Environment` got an empty array: asked for
            // here, where Main is.
            if (md.Name == "Main" && _method.Static && _method.Params.Count == 1 && _method.Params[0].Type.IsArray)
            {
                FindType("Environment", out _);
            }
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
                                     _method.Params[i].ByRef, _method.Params[i].ReadOnly)
                        {
                            Cell = _method.Params[i].Cell, CapturedVariable = _method.Params[i].CapturedVariable,
                        });
            }

            // A HOISTED GENERIC LOCAL FUNCTION calls itself and the others it
            // could see where it was written by the names written there.
            HashSet<string> visible = new(StringComparer.Ordinal);
            foreach ((string name, string method) in md.LocalGenerics)
            {
                if (visible.Add(name)) DeclareGenericLocal(md, name, method);
            }

            // A constructor's chained call runs before its body, so its
            // arguments are checked in the same scope the parameters are in.
            //
            // CHECKED AS THE `new` IT AMOUNTS TO, so it resolves as one does:
            // the closest overload, named arguments, defaults and caller
            // information, and a `params` tail. Only its own constructor is
            // out of reach of a `: this(...)`.
            if (md.Init != null)
            {
                TypeSymbol? target = md.Init.IsThis ? sym : sym.Base;
                NewExpr chained = new()
                {
                    Type = new TypeRef { Name = target?.Name ?? sym.Name, Line = md.Init.Line, Col = md.Init.Col },
                    Spans = md.Init.Spans, Source = md.Init.Source, Line = md.Init.Line, Col = md.Init.Col,
                };
                chained.Args.AddRange(md.Init.Args);
                chained.WritableArgNames.AddRange(md.Init.ArgNames);

                Type? outerChain = _wanted;
                _wanted = null;
                List<Type> given = chained.Args.Select(argument =>
                    argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null } || HoldsLambda(argument) ? Type.Any : CheckExpr(argument)).ToList();
                _wanted = outerChain;

                if (target is null)
                {
                    Error(md.Init, $"'{sym.Name}' has no base class to chain to");
                }
                else if (ResolveConstructor(chained, target, given, md.Init.IsThis ? md : null) is MethodSymbol runs)
                {
                    _r.Chained[md] = runs;
                }

                md.Init.Args.Clear();
                md.Init.Args.AddRange(chained.Args);
                md.Init.WritableArgNames.Clear();
                // A later round finds the names already consumed and makes
                // no order; the first round's stands.
                if (chained.ArgumentOrder.Count != 0)
                {
                    md.Init.WritableArgumentOrder.Clear();
                    md.Init.WritableArgumentOrder.AddRange(chained.ArgumentOrder);
                }
            }

            CheckBlock(md.Body);
            PopScope();
            SettleGenericCaptures();
            SettleCapturedCells();
            _r.FrameSize[md] = _maxSlot;
            NoteBoundBody(_method, md);
            _method = null;
        }
        _thisType = null;
    }

    private void PushScope(bool functionBoundary = false)
    {
        if (_spareScopes.TryPop(out LocalScope? spare))
        {
            spare.FunctionBoundary = functionBoundary;
            _scopes.Add(spare);
            return;
        }
        _scopes.Add(new LocalScope(functionBoundary));
    }

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
        int locals = 0;
        foreach (Sym held in closing.Values)
        {
            if (held is not LocalSym local) continue;
            _assigned.Remove(local);
            locals++;
        }
        _nextSlot -= locals;
        _scopes.RemoveAt(_scopes.Count - 1);
        if (!closing.Captured)
        {
            closing.Clear();
            closing.NestedNames.Clear();
            _spareScopes.Push(closing);
        }
    }

    private void Declare(Node at, string name, Sym sym)
    {
        // A generic local function being probed is told what it declares
        // itself: never a variable it captures through a call
        // (SettleGenericCaptures).
        foreach (GenericCaptures owner in _probeOwners) owner.OwnNames.Add(name);

        // `(_, _) => ...`: a lambda naming more than one parameter `_` has
        // discards for all of them, as C# 9 has it. The first holds the name;
        // the rest bind nothing.
        if (name == "_" && at is LambdaExpr && _scopes[^1].TryGetValue(name, out Sym? had) && had is ParamSym)
        {
            return;
        }

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

    private Sym? Lookup(string name) => LookupFrom(name, _scopes.Count - 1);

    /// <summary>A name, looked for from scope <paramref name="top"/> outwards (LookupCaptured).</summary>
    private Sym? LookupFrom(string name, int top)
    {
        for (int i = top; i >= 0; i--)
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
                    if (s is ConstSym constant && _capturedConstants is not null)
                    {
                        _capturedConstants[name] = constant;
                    }
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
                        // A parameter too, once something also writes it
                        // (ParamSym.Boxed; SettleCapturedCells).
                        else if (s is ParamSym parameter)
                        {
                            parameter.Captured = true;
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
            DeclareGenericLocal(b, name, method);
        }

        DeclareLocalFunctions(b);

        bool labels = PushLabels(b);
        foreach (Stmt s in b.Statements)
        {
            CheckStmt(s);
        }
        if (labels) _labels.RemoveAt(_labels.Count - 1);

        // WHAT THIS BLOCK'S GENERIC LOCAL FUNCTIONS CAPTURE, found while every
        // variable they may name is still in scope (Binder.GenericCaptures).
        if (b.GenericLocals.Count > 0) DiscoverGenericCaptures(b);
        PopScope();
    }

    /// <summary>
    /// C# LOCAL FUNCTIONS ARE BLOCK-SCOPED, not declaration-scoped: a call
    /// above the declaration is valid and recursion requires the name to be
    /// present while its own body is checked. Their slots and names are
    /// reserved before any statement of the block is bound -- a block, and a
    /// local function's or lambda's own body, where one declared after the
    /// `return` (EscapeFields' LiveFor) was not declared at all.
    /// </summary>
    private void DeclareLocalFunctions(Block b)
    {
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
            foreach (LocalScope kept in _scopes) kept.Captured = true;
            _localFunctionContexts[local] = new(new(_scopes), _scope, _thisType, _lexicalType, _member);
            Declare(local, local.Name, symbol);
            _assigned.Add(symbol);
        }
    }

    /// <summary>
    /// A LABEL IS SEEN FROM THE WHOLE BLOCK it is written in, and every block
    /// inside it, as C# has it: a goto may leave blocks, never enter one. A
    /// lambda's or local function's body is another method, which no goto
    /// crosses into or out of. True when the block had labels to push.
    /// </summary>
    private bool PushLabels(Block b)
    {
        Dictionary<string, LabeledStmt>? labels = null;
        foreach (Stmt statement in b.Statements)
            for (Stmt? at = statement; at is LabeledStmt l; at = l.Body)
            {
                labels ??= new(StringComparer.Ordinal);
                if (labels.ContainsKey(l.Label) || VisibleLabel(l.Label) is not null)
                    Error(l, $"the label '{l.Label}' is already declared in this scope");
                labels[l.Label] = l;
            }
        if (labels is null) return false;
        _labels.Add((_method, _thisType, labels));
        return true;
    }

    private readonly List<(MethodSymbol? Method, TypeSymbol? Type, Dictionary<string, LabeledStmt> Labels)> _labels = new();

    private LabeledStmt? VisibleLabel(string name)
    {
        for (int k = _labels.Count - 1; k >= 0; k--)
        {
            var (method, type, labels) = _labels[k];
            if (!ReferenceEquals(method, _method) || !ReferenceEquals(type, _thisType)) break;
            if (labels.TryGetValue(name, out LabeledStmt? found)) return found;
        }
        return null;
    }

    /// <summary>
    /// AN EMBEDDED STATEMENT IS A SCOPE OF ITS OWN, braces or not: the then
    /// and else of an if, the body of a while, do, for or foreach. What an
    /// expression in it declares -- a pattern's name, an `out var` -- is
    /// that statement's alone, as if it were written in braces (C# 7.3's
    /// rules for expression variables): `if (a) return f(x) is { } runs ?
    /// runs : null;` and a later `if (g() is not { } runs) return;` in a
    /// block beside it are two names, not one declared twice. What an if's
    /// or a while's CONDITION declares is not inside the embedded statement:
    /// an if's belongs to the scope the if is in, and is seen after it
    /// (`if (!int.TryParse(s, out int n)) return; Use(n);`), and a loop's to
    /// the loop. A block is a scope already.
    /// </summary>
    private void CheckEmbedded(Stmt s)
    {
        if (s is Block)
        {
            CheckStmt(s);
            return;
        }
        PushScope();
        CheckStmt(s);
        PopScope();
    }

    private void CheckStmt(Stmt s)
    {
        switch (s)
        {
            case Block b:
            {
                // `checked { }` and `unchecked { }`: what a constant cast may
                // do inside (CastExpr, CS0221).
                int outer = _uncheckedDepth;
                if (b.ArithmeticContext != 0) _uncheckedDepth = b.ArithmeticContext == 1 ? 0 : 1;
                try { CheckBlock(b); }
                finally { _uncheckedDepth = outer; }
                break;
            }

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

                if (d.IsRef)
                {
                    CheckRefLocal(d);
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
                    // A LAMBDA'S NATURAL TYPE (C# 10), and once worked out,
                    // whatever the initialiser: it is spelt on the declaration
                    // (Binder.NaturalTypes).
                    else if (d.Init is LambdaExpr || d.Init.NaturalType is not null)
                    {
                        type = NaturalDelegate(d, null);
                    }
                    else
                    {
                        type = CheckExpr(d.Init);

                        // A METHOD GROUP'S, when it is one method.
                        if (type.IsVoid && _r.Resolved.TryGetValue(d.Init, out Sym? group)
                            && group is MethodGroupSym or CapturedMethodGroupSym)
                        {
                            type = NaturalDelegate(d, group);
                        }
                        else if (type.Prim == Prim.NullLiteral)
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
                // A STRUCT LOCAL MAY BE WRITTEN A FIELD AT A TIME (`Pair p;
                // p.A = 1;`), which C# allows before the whole is assigned.
                // The lowering makes its zero value at the declaration, so the
                // local holds a value from there on and reading it is safe.
                else if (type.Symbol is { Kind: TypeKind.Struct } && !type.IsPointer && !type.Nullable && !type.IsArray)
                {
                    _assigned.Add(made);
                }
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
                HashSet<LocalSym> before = AssignedCopy();

                // THE THEN BRANCH KNOWS WHAT THE CONDITION PROVED, and the else
                // branch knows the opposite. Each is undone afterwards, because
                // what a branch proved is only true inside it.
                List<Sym> inThen = Assume(i.Cond, true);

                CheckEmbedded(i.Then);
                HashSet<LocalSym> afterThen = AssignedCopy();
                Forget(inThen);

                _assigned.Clear();
                _assigned.UnionWith(before);

                List<Sym> inElse = Assume(i.Cond, false);

                if (i.Else != null)
                {
                    CheckEmbedded(i.Else);
                }
                // _assigned IS what the else branch leaves assigned, and is
                // merged in place rather than copied first: only membership
                // is ever asked of these sets, never their order.

                bool thenLeaves = Leaves(i.Then);
                bool elseLeaves = i.Else != null && Leaves(i.Else);

                // Definite assignment is merged only across branches which can
                // reach the following statement. A branch ending in return or
                // throw contributes no path at all; intersecting it used to
                // reject the ordinary `if (...) x = a; else return; use(x);`
                // form even though every reaching path assigned x.
                if (!thenLeaves && elseLeaves)
                {
                    _assigned.Clear();
                    _assigned.UnionWith(afterThen);
                }
                else if (!thenLeaves && !elseLeaves)
                {
                    _assigned.IntersectWith(afterThen);
                }
                SpareAssigned(before);
                SpareAssigned(afterThen);

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

                EnterBreakable();
                CheckEmbedded(w.Body);
                HashSet<LocalSym>? whileBroke = LeaveBreakable();
                Forget(inLoop);
                PopScope();
                AfterEndlessLoop(w.Cond, whileBroke);
                break;
            }

            case DoStmt dd:
                EnterBreakable();
                CheckEmbedded(dd.Body);
                LeaveBreakable();
                // What the condition declares is the do statement's alone:
                // nothing after the loop sees it.
                PushScope();
                CheckCondition(dd.Cond);
                PopScope();
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
                EnterBreakable();
                CheckEmbedded(f.Body);
                HashSet<LocalSym>? forBroke = LeaveBreakable();

                foreach (Expr step in f.Step)
                {
                    CheckExpr(step);
                }

                Forget(proved);
                PopScope();
                AfterEndlessLoop(f.Cond, forBroke);
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

                // A POSITIONAL PATTERN OVER A NULLABLE VALUE takes apart what
                // is in it (C# 11.2.5): `x is (long v, Type t)` over a `(long,
                // Type)?` has tested x for a value already, and the tuple taken
                // apart is the value, unwrapped by the conversion to it -- not
                // by `.Value`, which a tuple with an item named Value answers
                // with that item.
                Expr whole = taken.Value;
                if (had.IsNullableValue && RefOf(had.Underlying) is TypeRef inner)
                {
                    whole = new CastExpr { Type = inner, Operand = taken.Value, Line = taken.Line, Col = taken.Col, File = taken.File };
                    had = had.Underlying;
                }

                block.WritableStatements.Add(new LocalDecl
                {
                    Name = held, Init = whole, Line = taken.Line, Col = taken.Col,
                });
                block.WritableStatements.AddRange(Deconstruct(
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

                // OVER A DYNAMIC VALUE: over what the binder enumerates of it.
                if (LateForeach(fe, seq) is Stmt lateLoop)
                {
                    _r.Lowered[fe] = lateLoop;
                    CheckStmt(lateLoop);
                    break;
                }

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
                // Recorded as a pattern's binding is: a lambda that captures
                // it makes it a cell, and the lowering stores into the cell.
                _r.PatternSym[fe] = iteration;
                EnterBreakable();
                CheckEmbedded(fe.Body);
                LeaveBreakable();
                PopScope();
                break;
            }

            case YieldStmt y:
            {
                Type sequence = _method?.Returns ?? Type.Void;
                Type? element = IteratorElement(sequence);
                if (element is null)
                {
                    Error(y, $"'yield' needs a method that returns IEnumerable<T>, IEnumerator<T>, IEnumerable or IEnumerator, not '{sequence}'");
                    if (y.Value is not null) CheckExpr(y.Value);
                    break;
                }
                if (y.Value is not null)
                {
                    Type? outerWanted = _wanted;
                    _wanted = element;
                    Type produced = CheckExpr(y.Value);
                    _wanted = outerWanted;
                    CheckAssignable(produced, element, y.Value, "yield return value");
                }
                break;
            }

            case ReturnStmt r:
            {
                // INFERRING A BLOCK LAMBDA'S RESULT: what is returned is the
                // question, so nothing is wanted of it (see Produces).
                if (_inferredReturns is not null)
                {
                    Type? wasWanted = _wanted;
                    _wanted = null;
                    _inferredReturns.Add(r.Value is null ? Type.Void : CheckExpr(r.Value));
                    _wanted = wasWanted;
                    break;
                }

                Type want = _method is { Async: true } running ? AsyncResult(running, r) : _method?.Returns ?? Type.Void;

                // AN ITERATOR RETURNS NOTHING: what it produces is what it
                // yields, and it ends with `yield break` (C# CS1622).
                if (_method?.Decl is MethodDecl { Body.Iterator: true })
                {
                    Error(r, "an iterator cannot return a value; use 'yield return' to produce one and 'yield break' to stop");
                    if (r.Value is not null) CheckExpr(r.Value);
                    break;
                }

                // `return ref x;` (Binder.RefLocals).
                if (r.Value is RefArgExpr { IsOut: false, Name: null } || _method is { RefReturn: true })
                {
                    CheckRefReturn(r);
                    break;
                }

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

            case GotoStmt jump:
                if (VisibleLabel(jump.Label) is { } labeled) _r.Gotos[jump] = labeled;
                else Error(jump, $"no label '{jump.Label}' within the scope of the goto statement");
                break;

            // A LABEL JOINS whatever jumps to it with what falls into it, and
            // what one path proved about a nullable the other may not have.
            case LabeledStmt marked:
                _notNull.Clear();
                _notNullPaths.Clear();
                CheckStmt(marked.Body);
                break;

            case BreakStmt or ContinueStmt:
                if (_loopDepth == 0)
                {
                    Error(s, $"'{(s is BreakStmt ? "break" : "continue")}' is only valid inside a loop");
                }
                else if (s is BreakStmt && _breaks.Count > 0)
                {
                    HashSet<LocalSym>? broke = _breaks[^1];
                    if (broke is null) _breaks[^1] = new HashSet<LocalSym>(_assigned, ReferenceEqualityComparer.Instance);
                    else broke.IntersectWith(_assigned);
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
                HashSet<LocalSym> beforeSwitch = AssignedCopy();
                // What every arm that reaches the following statement assigned,
                // met as each one is checked rather than kept arm by arm.
                HashSet<LocalSym>? continuing = null;

                // `case null:` PROVES THE OTHER ARMS. Reaching any of them
                // means the subject was not null, which is what C# knows and
                // what makes `switch (s) { case null: …; default: s.GetType() }`
                // -- the shape this compiler's own Gir.cs uses three times --
                // legal.
                List<Sym> proven = new();

                bool caseNull = false;
                foreach (SwitchCase c in sw.Cases)
                {
                    caseNull |= c.Pattern is BinaryExpr
                    {
                        Op: BinOp.Eq,
                        Left: SubjectExpr,
                        Right: LiteralExpr { Kind: Lit.Null },
                    };
                }
                if (caseNull && Path(sw.Subject) is string spelt && _notNullPaths.Add(spelt))
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
                    EnterBreakable();

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
                        if (continuing is null) continuing = AssignedCopy();
                        else continuing.IntersectWith(_assigned);
                    }
                    LeaveBreakable();
                    Forget(caseProof);
                    PopScope();
                }

                // A switch with no default may match no arm at all; that path
                // carries only what was assigned before the switch. Otherwise
                // the intersection of every arm that reaches the following
                // statement is exactly C#'s definite-assignment result.
                bool hasDefault = false;
                foreach (SwitchCase c in sw.Cases)
                {
                    hasDefault |= c.Pattern is null;
                }

                _assigned.Clear();
                if (continuing is not null)
                {
                    _assigned.UnionWith(continuing);
                    if (!hasDefault)
                    {
                        _assigned.IntersectWith(beforeSwitch);
                    }
                    SpareAssigned(continuing);
                }
                else
                {
                    _assigned.UnionWith(beforeSwitch);
                }
                SpareAssigned(beforeSwitch);

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
                                ? Type.Plain(root, Prim.Void)
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
                        _r.PatternSym[c] = caughtLocal;
                    }
                    // THE FILTER IS CHECKED WITH THE NAME IN SCOPE, which is
                    // the point of it: `catch (E e) when (e.Code == 2)`.
                    // AND WHAT IT PROVED HOLDS IN THE BODY, which runs only when
                    // it was true: `when (e.InnerException is not null)` lets the
                    // handler read e.InnerException.Message, as C# lets it.
                    List<Sym> filtered = new();
                    if (c.When != null)
                    {
                        Type filter = CheckExpr(c.When);

                        if (!filter.IsError && filter.Prim != Prim.Bool)
                        {
                            Error(c.When, $"an exception filter must be a bool, not '{filter}'");
                        }
                        else
                        {
                            filtered = Assume(c.When, true);
                        }
                    }

                    CheckBlock(c.Body);
                    Forget(filtered);
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

        // A DYNAMIC CONDITION: its operator true, asked when the program runs.
        if (LateCondition(e, t))
        {
            return;
        }

        if (!t.IsError && t.Prim != Prim.Bool)
        {
            // No truthiness. An integer is not a condition, and saying so is
            // the difference between catching `if (x = 1)` and shipping it.
            Error(e, $"a condition must be 'bool', not '{t}'");
        }
    }

    /// <summary>
    /// Whether a value of one tuple shape has to be rebuilt to be another: two
    /// shapes, element for element convertible, and some element not held the
    /// same way in both -- a number of another width, a value into a cell. Two
    /// references are the same word however they are typed.
    /// </summary>
    private bool TupleRebuilt(Type from, Type to)
    {
        if (from.IsArray || to.IsArray || from.IsNullableValue || to.IsNullableValue
            || from.Symbol is not { } a || to.Symbol is not { } b || ReferenceEquals(a, b)
            || !a.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            || !b.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal))
        {
            return false;
        }
        List<FieldSymbol> have = a.Fields.Where(f => !f.Static).ToList();
        List<FieldSymbol> want = b.Fields.Where(f => !f.Static).ToList();
        if (have.Count != want.Count)
        {
            return false;
        }
        bool differs = false;
        for (int i = 0; i < have.Count; i++)
        {
            Type x = have[i].Type, y = want[i].Type;
            if (!Convertible(x, y))
            {
                return false;
            }
            bool sameWord = x.Equals(y) || MethodSignatures.SameType(x, y)
                || (x.IsReference || x.Prim == Prim.Any) && (y.IsReference || y.Prim == Prim.Any) && !x.IsNullableValue && !y.IsNullableValue;
            differs |= !sameWord || have[i].Offset != want[i].Offset;
        }
        return differs;
    }

    /// <summary>
    /// A struct value (not a tuple, an enum, a Nullable or a type parameter)
    /// whose type declares no operator ==: `==` between two is an error.
    /// </summary>
    static bool StructWithoutEquality(Type t)
    {
        if (t.IsError || t.IsReference || t.IsNullableValue || t.IsPointer || t.IsArray || t.ParamName is not null) return false;
        if (t.Symbol is not { Kind: TypeKind.Struct } s) return false;
        if (s.Name.StartsWith(TypeRef.Tuple, StringComparison.Ordinal)) return false;
        for (TypeSymbol? at = s; at != null; at = at.Base)
            if ((at.MayHave("op_Equality") || at.MayHave("op_Inequality"))
                && at.Methods.Any(m => m.Static && m.Name is "op_Equality" or "op_Inequality")) return false;
        return true;
    }

    /// <summary>The one place assignability and nullability are decided.</summary>
    private void CheckAssignable(Type from, Type to, Node at, string what)
    {
        if (from.IsError || to.IsError || Unmade(to))
        {
            return;
        }

        // DYNAMIC, EITHER WAY. From it to anything but object, the binder's
        // implicit conversion at run time (Binder.Dynamic); to it, or from it
        // to object, the identity conversion C# has between dynamic and
        // object -- with no null check, dynamic being oblivious as C#'s is.
        if (LateConversion(from, to, at))
        {
            return;
        }
        if (from.Dynamic)
        {
            from = Type.Any;
        }
        if (to.Dynamic)
        {
            to = Type.Any.AsNullable();
        }
        // A METHOD GROUP CONVERTED TO OBJECT is its natural type (C# 10):
        // converted to that delegate, which is the object.
        if (NaturalTarget(to) && at is Expr groupSource && !_r.Rewrites.ContainsKey(groupSource)
            && _r.Resolved.TryGetValue(groupSource, out Sym? objectGroup) && objectGroup is MethodGroupSym or CapturedMethodGroupSym)
        {
            if (NaturalTypeOf(groupSource, objectGroup, "this method group") is Type natural)
            {
                CheckAssignable(from, natural, at, what);
            }
            return;
        }

        // Method groups have the same contextual delegate conversion in
        // assignments and returns as in arguments -- static ones, `this`'s,
        // and another object's (bound: MethodGroupLambda). Reuse the closure
        // lowering path rather than treating the unresolved group as void.
        if (at is Expr methodSource && !_r.Rewrites.ContainsKey(methodSource)
            && _r.Resolved.TryGetValue(methodSource, out Sym? methodSym)
            && (methodSym is MethodGroupSym or CapturedMethodGroupSym || LocalFunctionConverts(methodSource, to))
            && MethodGroupLambda(methodSource, to, localFunctions: true) is LambdaExpr methodWrapper)
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
            // A READ-ONLY SPAN OF CONSTANT BYTES IS DATA, as Roslyn makes it:
            // `ReadOnlySpan<byte> b = new byte[] { 1, 2, 3 }` reads the bytes
            // where the program's data is and allocates nothing, the same as
            // a u8 literal (EmitNew). Only a read-only span of one-byte
            // elements, all of them constants: nothing can write the one copy.
            Expr wrapped = array;
            if (span.Template == "ReadOnlySpan" && held.Prim is Prim.U8 or Prim.I8 or Prim.Bool
                && array is NewExpr { Elements: { Count: > 0 } items, Utf8Bytes: null, ArraySize: null } literal)
            {
                byte[] bytes = new byte[items.Count];
                bool constant = true;
                for (int i = 0; i < items.Count && constant; i++)
                {
                    if (ConstantValue(items[i], _thisType) is long value) bytes[i] = (byte)value;
                    else if (items[i] is LiteralExpr { Kind: Lit.Bool } truth) bytes[i] = (byte)(truth.IntValue != 0 ? 1 : 0);
                    else constant = false;
                }
                if (constant)
                {
                    wrapped = new NewExpr
                    {
                        Type = literal.Type, Elements = literal.Elements, Utf8Bytes = bytes,
                        Line = literal.Line, Col = literal.Col,
                    };
                }
            }
            NewExpr made = new()
            {
                Type = new TypeRef { Name = spanOwner.Name, Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };

            made.Args.Add(wrapped);
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
            && ConstantValue(written, _thisType) is long fits && ConstantConverts(from, fits, to))
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
                // CS8625: a null LITERAL into a non-nullable reference. Real
                // C# gives the literal its own code rather than the general
                // "possibly null" one below, because a literal null is not
                // possibly anything -- it is certainly null, every time.
                Warning(at, NullLiteralCode(what), $"{what}: '{to}' is not nullable; declare it as '{to}?' to allow null");
            }
            return;
        }

        // Reference annotations warn, but do not waive the underlying type
        // conversion check. A nullable Foo is still not an unrelated Bar.
        if (from.Nullable && !to.Nullable && to.IsReference)
        {
            Warning(at, NullCode(what), $"{what}: '{from}' may be null but '{to}' may not");
        }
        else if (WeakensPromise(from, to))
        {
            Warning(at, ElementNullCode(what), $"{what}: an element of '{from}' may be null but '{to}' says its elements may not");
        }

        // A Nullable<T> WHERE A T IS WANTED is not a conversion the compiler
        // may make on its own -- it can fail, and C# makes you write .Value or
        // a cast so the place it can fail is visible. Where an OBJECT or an
        // interface is wanted it is boxing, which C# does implicitly: no value
        // boxes to null, a value to the boxed T (the library's comparers are
        // handed a T? this way whenever T is a nullable struct).
        if (from.IsNullableValue && !to.IsNullableValue && !to.Nullable && !to.IsReference && to.Prim != Prim.Any
            && Convertible(from.Underlying, to))
        {
            Error(at, $"{what}: '{from}' may have no value; use '.Value' or cast it to '{to}'");
            return;
        }

        // A TUPLE INTO A SHAPE WITH OTHER ELEMENTS IS A NEW TUPLE (C# 10.2.13),
        // each element converted: `(long, long) b = a` over an `(int, int)`
        // is built from a.Item1 and a.Item2 widened, not the same object read
        // at another layout -- which answered garbage for every element after
        // the first width change.
        //
        // CHECKED AGAIN WHEN IT WAS MADE BEFORE. A look ahead (Peek) checks an
        // expression and then gives back the frame slots it took, so a rebuild
        // made during one holds its subject in a slot the real check hands to
        // somebody else: `(long x, long y) = c ? (1, 2) : (3L, 4L)` kept the
        // (int, int) arm's value in the very slot of the tuple it was being
        // copied into. The real check checks the same rebuild again, and it
        // takes a slot that is its own.
        // A TUPLE LITERAL IS TARGET-TYPED (C# 10.2.13): converted to a tuple
        // type, each element converts on its own -- a constant by the
        // constant conversion -- so `(8u, 3)` is a (uint, uint) and `(7u, 1, 2,
        // 0, 0)` a (uint, byte, ushort, ulong, ulong). Checked again as a
        // copy of itself with the target wanted, it is made in that shape.
        // A SWITCH EXPRESSION CONVERTS TO ANY TYPE EACH ARM CONVERTS TO (C#
        // 10.2.17), whatever type its arms have in common: `k switch { 0 =>
        // (8u, 3), _ => (1u, 0) }` is a (uint, uint) where one is wanted. Each
        // arm is converted, and the switch is that type from then on.
        if (at is SwitchExpr matched && !from.IsError && !to.IsError && !from.Equals(to) && !Convertible(from, to)
            && matched.Arms.All(arm => arm.Result is ThrowExpr || Fits(_r.TypeOf(arm.Result), to, arm.Result)))
        {
            foreach (SwitchArm arm in matched.Arms)
            {
                if (arm.Result is ThrowExpr) continue;
                CheckAssignable(_r.TypeOf(arm.Result), to, arm.Result, what);
            }
            _r.ExprType[matched] = to;
            return;
        }

        if (at is TupleExpr tupleLiteral && !_r.Rewrites.ContainsKey(tupleLiteral) && !from.Equals(to)
            && !Convertible(from, to) && TupleLiteralFits(tupleLiteral, to))
        {
            TupleExpr retyped = new() { Line = tupleLiteral.Line, Col = tupleLiteral.Col, File = tupleLiteral.File };
            retyped.Items.AddRange(tupleLiteral.Items);
            retyped.Names.AddRange(tupleLiteral.Names);
            _r.Rewrites[tupleLiteral] = retyped;
            Type? outsideWanted = _wanted;
            _wanted = to;
            CheckExpr(retyped);
            _wanted = outsideWanted;
            return;
        }

        if (at is Expr source && TupleRebuilt(from, to)
            && (!_r.Rewrites.TryGetValue(source, out Expr? earlier) || earlier is PatternExpr { Test: TupleExpr }))
        {
            if (earlier is not PatternExpr rebuilt)
            {
                SubjectExpr Held() => new() { Line = source.Line, Col = source.Col };
                TupleExpr items = new() { Line = source.Line, Col = source.Col };
                for (int i = 0; i < to.Symbol!.Fields.Count(f => !f.Static); i++)
                {
                    items.Items.Add(new MemberExpr { Target = Held(), Name = "Item" + (i + 1), Guarded = true, Line = source.Line, Col = source.Col });
                }
                rebuilt = new() { Subject = source, Test = items, Line = source.Line, Col = source.Col };
            }
            _r.Rewrites[source] = rebuilt;
            Type? outside = _wanted;
            _wanted = to;
            CheckExpr(rebuilt);
            _wanted = outside;
            return;
        }

        // A USER-DEFINED IMPLICIT CONVERSION, where no standard one applies.
        if (at is Expr converted && !StandardConvertible(from, to) && !Variant(from, to)
            && (!_r.Rewrites.TryGetValue(converted, out Expr? priorRewrite) || priorRewrite is CallExpr { Target: MemberExpr { Name: "op_Implicit" } })
            && UserConversion(from, to, explicitToo: false, IntegerConstant(converted, from)) is { } implicitOp)
        {
            // CHECKED AGAIN, it is the operator's call already: the operands
            // of `t - 1 - x` are checked once to choose the operator and again
            // as its arguments.
            if (priorRewrite is null) ConvertByOperator(converted, implicitOp);
            return;
        }

        // AN INTEGER CONSTANT INTO A NULLABLE OF A TYPE IT FITS: converted,
        // then put in a cell, as `ulong? x = 6;` is in C#.
        if (to.IsNullableValue && !from.IsNullableValue && at is Expr fitted && IntegerConstantFits(fitted, from, to)
            && !Convertible(from, to))
        {
            _r.Boxes.Add(fitted);
            _r.BoxedAs[fitted] = to;
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
                _r.BoxedAs[boxed] = to;
            }

            // AND AN ARRAY GETS ITS HELPER (ArrayBecomes). The conversion is
            // real -- something has to answer Count and the indexer -- and
            // this is the one place that knows both what was written and what
            // was wanted.
            // AND A COVARIANT ONE THE HELPER OF WHAT IS WANTED: a string[] as
            // an IEnumerable<object> is read through the view that answers
            // IEnumerable<object>, which reads each element as the word it is
            // -- the same words, taken as the wider type, which is what array
            // covariance means. A view is one per element size and interface.
            if (at is Expr viewed && !ArrayBecomes(viewed, from, to)
                && from.IsArray && at is Expr covariant && !Convertible(from, to)
                && to.AsNonNullable().Symbol is { Kind: TypeKind.Interface } wider)
            {
                _r.Views[covariant] = ArrayView(from.Element!, wider);
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
        => had.IsError || (!NullableIntoValue(had, want) && (Convertible(had, want) || Variant(had, want)))
        || (written is not null && (MethodGroupFits(written, want) || LocalFunctionConverts(written, want)))
        || IntegerConstantFits(written, had, want)
        || TupleLiteralFits(written, want);

    /// <summary>
    /// Whether a tuple literal converts to a tuple type element by element
    /// (C# 10.2.13): each element as it would on its own -- a constant by the
    /// constant conversion, a null or a bare default to anything that takes
    /// one, an inner literal the same way again.
    /// </summary>
    private bool TupleLiteralFits(Expr? written, Type want)
    {
        if (written is not TupleExpr literal || want.IsNullableValue || want.IsArray
            || want.Symbol is not { } shape || !shape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal))
        {
            return false;
        }
        List<FieldSymbol> fields = shape.Fields.Where(f => !f.Static).ToList();
        if (fields.Count != literal.Items.Count) return false;
        for (int k = 0; k < fields.Count; k++)
        {
            Expr item = literal.Items[k];
            Type itemType = _r.TypeOf(item);
            Type wantedItem = fields[k].Type;
            if (item is LiteralExpr { Kind: Lit.Null } or DefaultExpr { Type.Name.Length: 0 }) continue;
            if (!Fits(itemType, wantedItem, item)) return false;
        }
        return true;
    }

    /// <summary>
    /// A Nullable&lt;T&gt; where a plain value type is wanted: no implicit
    /// conversion, so no overload that asks for one applies (C# 10.6.1 has
    /// only the explicit one). `Console.WriteLine(s?.Length)` is
    /// WriteLine(object), boxed or null -- not WriteLine(long) and an error.
    /// </summary>
    private static bool NullableIntoValue(Type had, Type want)
        => had.IsNullableValue && !want.IsNullableValue && !want.Nullable && !want.IsReference
        && want.Prim != Prim.Any && want.ParamName is null;

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

    /// <summary>
    /// Whether a value of one type may stand where another is wanted with no
    /// cast written: a standard conversion, or one user-defined `implicit
    /// operator` on either type (C# 10.5.4), the way `XElement e = new("a")`
    /// makes its XName from a string.
    /// </summary>
    private bool Convertible(Type from, Type to)
        => StandardConvertible(from, to) || UserConversion(from, to, explicitToo: false) is not null;

    /// <summary>
    /// The user-defined conversion operator taking a `from` to a `to`, or
    /// null. Declared on the source type or the target type, as C# looks
    /// (C# 10.5.3); its operand and result reached by standard conversions
    /// only, since C# never chains two user-defined conversions. A cast may
    /// use an `explicit` one as well.
    /// </summary>
    /// <summary>The value of an integer constant expression of this type, or null.</summary>
    private long? IntegerConstant(Expr? e, Type type)
        => e is not null && type.IsInteger && !type.IsNullableValue && ConstantValue(e, _thisType) is long value ? value : null;

    /// <summary>
    /// ... and a CONSTANT reaches an operator's operand through the constant
    /// conversion first (C# 10.5.3's standard conversions include it): the
    /// `2` of `2 * q` becomes a uint, and UInt128's `implicit operator
    /// UInt128(uint)` makes the rest.
    /// </summary>
    private MethodSymbol? UserConversion(Type from, Type to, bool explicitToo, long? constant)
    {
        if (constant is not long value)
        {
            return UserConversion(from, to, explicitToo);
        }
        return UserConversion(from, to, explicitToo) ?? UserConversionWhere(from, to, explicitToo,
            operand => operand.IsInteger && !operand.IsNullableValue && operand.Prim != Prim.Char && Binder.Fits(value, operand));
    }

    private MethodSymbol? UserConversion(Type from, Type to, bool explicitToo)
        => UserConversionWhere(from, to, explicitToo, operand => StandardConvertible(from, operand));

    private MethodSymbol? UserConversionWhere(Type from, Type to, bool explicitToo, Func<Type, bool> takes)
    {
        if (from.IsError || to.IsError || from.Prim is Prim.Any or Prim.NullLiteral || to.Prim == Prim.Any
            || from.IsArray && to.IsArray)
        {
            return null;
        }

        // AN ARRAY CONVERTS BY THE OTHER SIDE'S OPERATOR: `Memory<byte> m =
        // bytes` is Memory's `implicit operator Memory<T>(T[] array)`, and
        // Stream.ReadAsync(buffer) over a byte[] needs exactly that. An array
        // declares no operators, so it is never the holder -- its Symbol is
        // its element's, whose operators convert the element, not the array.
        TypeSymbol? source = from.IsArray ? null : from.AsNonNullable().Symbol;
        TypeSymbol? target = to.IsArray ? null : to.AsNonNullable().Symbol;
        List<MethodSymbol> candidates = new();

        foreach (TypeSymbol? holder in new[] { source, target == source ? null : target })
        {
            if (holder is null || holder.Kind == TypeKind.Interface)
            {
                continue;
            }

            foreach (string name in explicitToo ? new[] { "op_Implicit", "op_Explicit" } : new[] { "op_Implicit" })
            {
                foreach (MethodSymbol m in holder.FindMethods(name))
                {
                    if (m.Static && m.Params.Count == 1
                        && takes(m.Params[0].Type) && StandardConvertible(m.Returns, to))
                    {
                        candidates.Add(m);
                    }
                }
            }
        }

        // THE MOST SPECIFIC OPERATOR (C# 10.5.5): among those whose operand
        // is the source itself when any is, the one whose result is the
        // target itself. `(UInt128)(-1)` is UInt128's operator from int, not
        // the one from uint that the int also reaches by a cast; `(long)node`
        // is JsonNode's operator to long, not the one to int that widens.
        List<MethodSymbol> fromExactly = candidates.Where(m => MethodSignatures.SameType(m.Params[0].Type, from)).ToList();
        if (fromExactly.Count > 0) candidates = fromExactly;
        return candidates.FirstOrDefault(m => MethodSignatures.SameType(m.Returns, to)) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// A user-defined conversion made where it is needed: the expression
    /// rewritten as the call its operator is -- `XName.op_Implicit("a")` --
    /// the way CheckAssignable already writes the array-to-span and
    /// string-to-span conversions. The lowering evaluates the original inside
    /// the call for what it is.
    /// </summary>
    private Type ConvertByOperator(Expr value, MethodSymbol op)
    {
        CallExpr call = new()
        {
            Target = new MemberExpr { Target = Qualified(op.Owner.Key, value), Name = op.Name, Line = value.Line, Col = value.Col, File = value.File },
            Line = value.Line, Col = value.Col, File = value.File,
        };
        call.Args.Add(value);
        _r.Rewrites[value] = call;
        _r.UserConversions.Add(call);
        CheckExpr(call);
        return Converting(call, op);
    }

    /// <summary>
    /// A conversion's call bound to the operator chosen for it. Overload
    /// resolution sees only the argument, and a type's conversions can differ
    /// in nothing else -- JsonNode's to bool, int, long and double -- so the
    /// call would have taken the first of them.
    /// </summary>
    private Type Converting(CallExpr call, MethodSymbol op)
    {
        _r.Calls[call] = op;
        _r.ExprType[call] = op.Returns;
        return op.Returns;
    }

    private bool StandardConvertible(Type from, Type to)
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

        // ARRAY COVARIANCE (C# 10.2.8): a T[] is a U[] when T and U are
        // references and T reaches U by a reference conversion -- the same
        // words, read as the wider type. `RegOperand[]` handed to a `params
        // Operand[]`, `string[]` to `object[]`; never `int[]` to `object[]`,
        // nor `object[]` to `string[]`.
        if (from.IsArray && to.IsArray && from.Element is Type fromElement && to.Element is Type toElement
            && Carried(fromElement) && Carried(toElement) && !fromElement.IsNullableValue && !toElement.IsNullableValue
            && ReferenceConvertible(fromElement, toElement))
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
                if (!StandardConvertible(fromTuple.Fields[i].Type, toTuple.Fields[i].Type))
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
        if (to.IsNullableValue && StandardConvertible(from.Underlying, to.Underlying))
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

        // AN ENUM IS ITSELF, whatever width either side of it was read with:
        // the element of a tuple shape names the enum by its symbol and may
        // carry another word than its declaration's `: byte`.
        if (from.Symbol is { Kind: TypeKind.Enum } fromEnum && to.Symbol is { Kind: TypeKind.Enum } toEnum
            && (ReferenceEquals(fromEnum, toEnum) || fromEnum.Key == toEnum.Key))
        {
            return true;
        }

        // Widening only. A narrowing conversion loses information and must be
        // written as a cast so it is visible at the point it happens.
        if (from.IsInteger && to.IsInteger)
        {
            // C#'S OWN (10.2.3): only where every value is kept. Into char
            // never, from signed into unsigned never; anything else is a
            // cast. A constant that fits is the constant conversion's
            // (ConstantConverts), not this.
            return IntegerWidens(from.Prim, to.Prim);
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
        if (from.IsArray && (ArrayFace(from, to) != null || ListFace(from, to) != null))
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

        // AND THE OTHER WAY, INSIDE A CANONICAL COPY: there a type parameter
        // binds as the word it is, `object`, so a generic method called with
        // the copy's own `ReadOnlySpan<T>` and a `T` infers T as object and
        // asks for `ReadOnlySpan$object` -- whose canonical copy is the very
        // `ReadOnlySpan$__canon` it was handed: one layout, one routine.
        if (InCanonicalCopy && to.Symbol?.Decl?.Canon is string wanted && wanted == from.Symbol?.Name)
        {
            return true;
        }

        // A NUMBER, A BOOL, A CHAR OR AN ENUM BOXED, AND A STRING AS IT IS,
        // IS EACH SYSTEM INTERFACE .NET DECLARES IT TO IMPLEMENT (BoxedFaces):
        // `IComparable x = 5;`, `IEquatable<int> e = 3;`, `IComparable s =
        // "a";` -- implicit, as C#'s boxing and reference conversions are.
        if (!to.IsArray && to.AsNonNullable().Symbol is { Kind: TypeKind.Interface } face
            && BoxedFaces.Implements(from, face, FaceArgument(to.AsNonNullable(), face)))
        {
            return true;
        }

        // AND ONE TYPE SPELT TWO WAYS THERE: a template with its arguments --
        // `IEnumerator<(object, object)>`, what a member answers with the copy's
        // type parameters bound as the words they are -- and the copy made of
        // it, `IEnumerator$ValueTuple___canon___canon`, which a local declared
        // with those parameters is. The same routine and layout, named as the
        // monomorphiser names it with every word `__canon`, at any depth: a
        // PriorityQueue built from (element, priority) pairs met it in a foreach.
        if (InCanonicalCopy && (SameCanonical(from, to) || SameCanonical(to, from)))
        {
            return true;
        }

        if (from.Symbol != null && to.Symbol != null)
        {
            return from.Symbol.DerivesFrom(to.Symbol);
        }
        return false;
    }

    /// <summary>The type argument a one-argument specialisation of an interface was made with, or null.</summary>
    private Type? FaceArgument(Type to, TypeSymbol face)
    {
        if (face.TemplateArgTypes.Count == 1) return face.TemplateArgTypes[0];
        if (to.Args.Count == 1) return to.Args[0];
        return face.Decl is { Template: not null, TemplateArgs.Count: 1 } made ? Resolve(made.TemplateArgs[0], _thisType) : null;
    }

    /// <summary>
    /// Whether `spelt`, a template with its arguments, names the copy `made`
    /// is, inside a canonical copy: its name as Monomorphiser.MangledName
    /// gives it with every reference argument -- object, or __canon -- the
    /// canonical word, in tuples and nested arguments too.
    /// </summary>
    private static bool SameCanonical(Type spelt, Type made)
    {
        if (spelt.Args.Count == 0 || made.Args.Count > 0 || spelt.Symbol is not TypeSymbol template || made.Symbol is not TypeSymbol copy
            || spelt.IsArray || made.IsArray || spelt.PointerDepth != 0 || made.PointerDepth != 0)
        {
            return false;
        }
        List<TypeRef> args = new(spelt.Args.Count);
        foreach (Type a in spelt.Args)
        {
            if (RefOf(a) is not TypeRef r) return false;
            args.Add(AsCanonical(r));
        }
        string name = template.Decl?.Outer is string outer ? outer + "." + template.Name : template.Name;
        return Monomorphiser.MangledName(name, args) == copy.Name;

        static TypeRef AsCanonical(TypeRef r)
        {
            if (r.ArrayRank == 0 && r.PointerDepth == 0 && r.Args.Count == 0 && r.Name is "object" or "Object" or "System.Object" or Monomorphiser.CanonName)
            {
                return new TypeRef { Name = Monomorphiser.CanonName, Line = r.Line, Col = r.Col };
            }
            if (r.Args.Count == 0) return r;
            return new TypeRef
            {
                Name = r.Name, ArrayRank = r.ArrayRank, PointerDepth = r.PointerDepth,
                Arguments = r.Args.Select(AsCanonical).ToList(), Line = r.Line, Col = r.Col,
            };
        }
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
    /// <summary>C#'s implicit numeric conversions between integers (10.2.3): the value always kept.</summary>
    private static bool IntegerWidens(Prim from, Prim to) => from == to || from switch
    {
        Prim.I8 => to is Prim.I16 or Prim.I32 or Prim.I64 or Prim.NInt,
        Prim.U8 => to is Prim.I16 or Prim.U16 or Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt,
        Prim.I16 => to is Prim.I32 or Prim.I64 or Prim.NInt,
        Prim.U16 => to is Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt,
        Prim.Char => to is Prim.U16 or Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt,
        Prim.I32 => to is Prim.I64 or Prim.NInt,
        Prim.U32 => to is Prim.I64 or Prim.U64 or Prim.NUInt,
        Prim.NInt => to is Prim.I64,
        Prim.NUInt => to is Prim.U64,
        _ => false,
    };

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
                return Type.Plain(candidate, Prim.Void, first.Nullable || second.Nullable);
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
                Type candidate = Type.Plain(face, Prim.Void);
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

        same.WritableParams.Add(new ParamSymbol { Name = "other", Type = Type.Any });

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

        identical.WritableParams.Add(new ParamSymbol { Name = "a", Type = Type.Any });
        identical.WritableParams.Add(new ParamSymbol { Name = "b", Type = Type.Any });

        MethodSymbol pair = new()
        {
            Name = "Equals", Returns = Type.Bool, Owner = _rooted, Static = true,
        };

        pair.WritableParams.Add(new ParamSymbol { Name = "a", Type = Type.Any });
        pair.WritableParams.Add(new ParamSymbol { Name = "b", Type = Type.Any });

        _rooted.WritableMethods.Add(str);
        _rooted.WritableMethods.Add(same);
        _rooted.WritableMethods.Add(hashed);
        _rooted.WritableMethods.Add(identical);
        _rooted.WritableMethods.Add(pair);
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
            // Plain loops, in the same order the queries had: this is every
            // type of the unit for every extension call looked up, and each
            // query's closure and iterator was an allocation per look.
            if (namespaces.Count == 0) return found;
            // THE CANDIDATES FIRST, THEN WHICH OF THEM APPLY. Asking whether
            // the receiver converts can declare a type's members on first use
            // (DeclareMembersNow), and a signature declared can make a tuple
            // shape, which is a new entry in the very table being walked.
            foreach (TypeSymbol holder in _r.Types.Values)
            {
                if (!namespaces.Contains(holder.Decl?.Namespace ?? "")) continue;
                // Not one whose members were never asked for: it holds no
                // extension method to find (Monomorphiser.MembersLater), and
                // looking would declare every one of them.
                if (holder.MembersPending) continue;
                foreach (MethodSymbol m in holder.Methods)
                {
                    if (m.Name != name) continue;
                    if (m.Static && m.Params.Count > 0 && m.Decl?.Params.FirstOrDefault()?.IsThis == true)
                    {
                        found.Add(m);
                    }
                }
            }
            if (found.Count == 0) return found;
            List<MethodSymbol> applying = new(found.Count);
            foreach (MethodSymbol m in found)
            {
                if (Convertible(target, m.Params[0].Type)
                    // As an argument would be accepted: an IEnumerable<Box>
                    // is the IEnumerable<Box?> a copy of `Elements<T>(this
                    // IEnumerable<T?>)` takes, the annotation being no type.
                    || Variant(target, m.Params[0].Type)
                    || m.Params[0].Type.ParamName != null
                    || Applies(m, m.Params[0].Type, target))
                {
                    applying.Add(m);
                }
            }
            return applying;
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
        // CONVERTED TO OBJECT, a lambda is its natural type (C# 10), and that
        // delegate is the object (Binder.NaturalTypes).
        if (NaturalTarget(wanted))
        {
            return NaturalTypeOf(lam, null, "this lambda") is Type natural ? CheckLambda(lam, natural) : Type.Error;
        }

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

        // A RESULT WRITTEN IN FRONT, `ref int (int[] a) => ref a[0]` (C# 10),
        // is the delegate's exactly: its type, and whether and how it is
        // returned by reference.
        if (lam.Returns is not null)
        {
            bool byReference = (lam.ReturnMods & Mods.RefReturn) != 0;
            bool readOnly = (lam.ReturnMods & Mods.RefReadonlyReturn) != 0;
            static string How(bool reference, bool readOnlyReference)
                => reference ? readOnlyReference ? "by 'ref readonly'" : "by 'ref'" : "by value";
            Type written = Resolve(lam.Returns, _thisType);
            Type delegated = ContextualMemberResult(wanted, invoke);
            if (byReference != invoke.RefReturn || readOnly != invoke.RefReturnReadOnly)
            {
                Error(lam, $"the lambda returns {How(byReference, readOnly)}, and '{face!.Name}' returns {How(invoke.RefReturn, invoke.RefReturnReadOnly)}");
            }
            else if (!written.IsError && !delegated.IsError
                     && !MethodSignatures.SameType(written.AsNonNullable(), delegated.AsNonNullable()))
            {
                Error(lam, $"the lambda returns '{written}', and '{face!.Name}' returns '{delegated}'");
            }
        }

        // PARAMETER TYPES WRITTEN ARE THE DELEGATE'S EXACTLY (CS1678): `(long
        // x) => ...` is no Func<int, int>, however an int would convert.
        if (lam.TypesWritten)
        {
            for (int i = 0; i < lam.Params.Count && i < invoke.Params.Count; i++)
            {
                if (WrittenParameterMismatch(lam, i, wanted, invoke) is (Type written, Type delegated))
                {
                    Error(lam, $"parameter {i + 1} of the lambda is declared '{written}', and the delegate's is '{delegated}'");
                }
            }
        }

        // ---- pass one: which of the enclosing locals does it read? ----------
        Dictionary<string, Type>? outerCaptured = _captured;
        Dictionary<string, ConstSym>? outerConstants = _capturedConstants;
        int outerFloor = _lambdaFloor;
        Dictionary<string, Type> captured = new(StringComparer.Ordinal);
        Dictionary<string, ConstSym> constants = new(StringComparer.Ordinal);

        _captured = captured;
        _capturedConstants = constants;
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
        RefuseCapturedRefLocals(lam, captured.Keys);
        _captured = outerCaptured;
        _capturedConstants = outerConstants;
        _lambdaFloor = outerFloor;
        // And a lambda inside a lambda reads its constants through the outer
        // one's declarations of them.
        if (outerConstants != null)
        {
            foreach ((string name, ConstSym constant) in constants)
            {
                outerConstants[name] = constant;
            }
        }

        // A lambda inside a lambda captures through the outer one, so anything
        // the inner one reached for has to be captured by the outer one too.
        if (outerCaptured != null)
        {
            foreach ((string name, Type held) in captured)
            {
                // A local or parameter of the enclosing code, or -- the
                // enclosing lambda itself being checked inside an outer
                // closure -- one of that closure's capture fields: three
                // lambdas deep in an instance method, the middle one never
                // took what only the innermost read, and the innermost found
                // nothing to read it from.
                // A local or parameter is written down by Lookup itself, and
                // only when it is the enclosing code's: one of the outer
                // lambda's own is no capture of it, and a generic local
                // function probed around this one (ProbeGenericLocal) would
                // take it for a parameter it does not have.
                if (Lookup(name) is LocalSym or ParamSym)
                {
                    continue;
                }
                if (_thisType is { } enclosing && enclosing.Name.StartsWith("Lambda$", StringComparison.Ordinal)
                    && (enclosing.FindField(name) ?? enclosing.FindBackingField(name)) is not null)
                {
                    outerCaptured[name] = held;
                }
            }
        }

        // WHILE A GENERIC LOCAL FUNCTION IS PROBED for what it captures
        // (ProbeGenericLocal), a lambda inside it is wanted for the same and
        // nothing more: what it read is passed up above, and no class is
        // made of a body that is never compiled.
        if (_probing > 0)
        {
            return wanted;
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
        // NOR IN A STATIC METHOD WHEN IT CAPTURED `this`: made first in one of
        // the type's instance methods, it holds a `$this` field a static one
        // has nothing to fill from, and lowering it there read a `this` that
        // was not -- the compiler failed compiling its own escape engine, an
        // outer type's static method group passed from a nested type's
        // instance method and then from its static one (test 1309).
        // The static methods' own is named apart, and shared among them.
        if (!bound && lam.GroupIdentity is not null && _method is { Static: true }
            && _groupClosures.TryGetValue(name2, out ClosureInfo? holdsThis) && holdsThis.Captures.Count > 0)
        {
            name2 += "$Static";
        }
        if (!bound && lam.GroupIdentity is not null && _groupClosures.TryGetValue(name2, out ClosureInfo? sharedClosure)
            && (_method is { Static: true } ? sharedClosure.Captures.Count == 0 : _capturedThisType is null && _thisType is not null))
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

        closure.WritableInterfaces.Add(face!);

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
            closure.WritableFields.Add(targetField);
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
                        Type.Plain(enclosingThis, Prim.Void));
                }
            }
        }
        FieldSymbol? thisField = null;

        if (enclosingThis is not null && enclosingThisSource is not null)
        {
            Type thisType = Type.Plain(enclosingThis, Prim.Void);
            thisField = new FieldSymbol
            {
                Name = "$this", Type = thisType, Owner = closure, Offset = at,
            };
            at += 8;
            closure.WritableFields.Add(thisField);
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
                Boxed = CellSource(from),
            };

            at += Math.Max(8, held.Size);
            closure.WritableFields.Add(f);
            fields.Add((f, from));
            if (!f.Boxed) _closureCopies.Add((f, from));
            if (LocalFunctionDeclaration(from) is { } localFunction)
                _capturedLocalFunctions[f] = localFunction;
        }

        closure.InstanceSize = Math.Max(Target.Current.ObjectHeaderBytes, at);
        closure.InlineDecided = true;                    // laid out here, nothing in line

        Type closureReturns = ContextualMemberResult(wanted, invoke);
        MethodDecl body = new()
        {
            // RETURNED BY REFERENCE AS THE DELEGATE SAYS: for `delegate ref
            // int D(...)`, and a ref-returning local function's delegate
            // (Parser.LocalFunctionDelegate), the body answers a variable --
            // `=> ref a[i]`, `return ref a[i];` -- and its Invoke the address.
            Name = "Invoke", Returns = new TypeRef { Name = "" },
            Mods = Mods.Public | (invoke.RefReturn ? Mods.RefReturn : Mods.None)
                 | (invoke.RefReturnReadOnly ? Mods.RefReadonlyReturn : Mods.None),
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
            // BY REFERENCE AS THE DELEGATE SAYS (C# 12.19.2): `(string s, out
            // int v) => ...` for `delegate bool TryIt(string s, out int v)`
            // writes v through the caller's address. A lambda that says ref,
            // out or in must say it where Invoke does, and only there.
            ParamSymbol delegated = invoke.Params[i];
            Param written = lam.Params[i];
            bool writtenByRef = written.IsRef || written.IsOut;
            if (lam.GroupIdentity is null && (writtenByRef != delegated.ByRef || written.IsReadOnlyRef != delegated.ReadOnly))
            {
                Error(lam, $"parameter {i + 1} of the lambda is passed {Passing(written.IsOut ? "out" : written.IsRef ? "ref" : written.IsReadOnlyRef ? "in" : "")}, "
                         + $"and the delegate's is passed {Passing(delegated.ByRef ? (delegated.ReadOnly ? "in" : "out or ref") : delegated.ReadOnly ? "in" : "")}");
            }
            run.WritableParams.Add(new ParamSymbol
            {
                Name = written.Name, Type = ContextualParameterType(wanted, invoke, i),
                ByRef = delegated.ByRef, ReadOnly = delegated.ReadOnly,
            });
            body.WritableParams.Add(new Param
            {
                Name = written.Name, Type = new TypeRef { Name = "" },
                IsRef = delegated.ByRef && !written.IsOut, IsOut = written.IsOut, IsReadOnlyRef = delegated.ReadOnly,
                Line = lam.Line, Col = lam.Col,
            });
        }

        static string Passing(string how) => how.Length == 0 ? "by value" : "with '" + how + "'";

        closure.WritableMethods.Add(run);
        // WHAT IT IS AS A DELEGATE: the members every delegate has
        // (DelegateMembers), and -- a method group's -- the method it calls,
        // by which its delegates are equal to those another class made of
        // the same method (TypeSymbol.DelegateGroup).
        if (face is not null) DelegateMembers(closure, face);
        if (lam.GroupIdentity is string group) closure.DelegateGroup = group.Split('$')[0];
        else if (lam.LocalGroup is string local) closure.DelegateGroup = local;
        ImplementDefaults(closure);
        RegisterType(name2, closure);
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
        // NO CAPTURE IS BEING DISCOVERED IN HERE: the body's own reads of
        // what it captured are fields by now, and a lambda inside it runs a
        // discovery of its own. Left as they were, an enclosing lambda's
        // discovery -- its floor counted on the stack this replaces -- took
        // this body's own locals for captures of the enclosing method's, and
        // they went to cells the reads never looked in.
        Dictionary<string, Type>? wasCaptured = _captured;
        Dictionary<string, ConstSym>? wasCapturedConstants = _capturedConstants;
        int wasFloor = _lambdaFloor;
        _captured = null;
        _capturedConstants = null;
        _lambdaFloor = -1;

        _scopes.Clear();
        _thisType = closure;
        _lexicalType = wasLexicalType ?? wasThis;
        _capturedThisType = enclosingThis;
        _capturedThisField = thisField;
        _method = run;
        _nextSlot = 0;
        // A LAMBDA'S RETURNS ARE ITS OWN: one inside a body whose result is
        // being inferred says nothing about that body's result.
        List<Type>? wasInferred = _inferredReturns;
        _inferredReturns = null;
        // THE ENCLOSING METHOD'S CONSTANTS THE BODY READS, declared again
        // here, outside the parameters so a parameter may shadow one as C#
        // lets it: `const string rid = ...; names.Where(n => n.EndsWith(rid))`
        // said rid was not declared.
        PushScope(functionBoundary: true);
        foreach ((string name, ConstSym constant) in constants)
        {
            Declare(lam, name, constant);
        }
        // AND THE GENERIC LOCAL FUNCTIONS IN SCOPE WHERE IT WAS WRITTEN: they
        // are methods of the type, called from in here through the `this`
        // the closure holds when they are instance methods.
        Dictionary<string, Sym> generics = new(StringComparer.Ordinal);
        foreach (LocalScope scope in wasScopes)
        {
            foreach ((string name, Sym named) in scope)
            {
                if (_genericLocalSyms.Contains(named)) generics[name] = named;
            }
        }
        foreach ((string name, Sym named) in generics)
        {
            if (constants.ContainsKey(name)) continue;
            List<MethodSymbol> methods = named is CapturedMethodGroupSym held ? held.Methods : ((MethodGroupSym)named).Methods;
            Sym again = thisField is not null && methods.Any(m => !m.Static)
                ? new CapturedMethodGroupSym(thisField, methods)
                : new MethodGroupSym(methods);
            _genericLocalSyms.Add(again);
            Declare(lam, name, again);
        }
        PushScope(functionBoundary: true);

        for (int i = 0; i < lam.Params.Count; i++)
        {
            // A LAMBDA'S PARAMETERS ARE THE INVOKE'S PARAMETERS, not locals of
            // it. Declared as locals they read as zero: the caller puts
            // arguments where a parameter lives, and nothing had put anything
            // in the slot a local would have used.
            Declare(lam, lam.Params[i].Name,
                    new ParamSym(i, run.Params[i].Type, lam.Params[i].Name, run.Params[i].ByRef, run.Params[i].ReadOnly));
        }

        Look(lam, closureReturns, final: true);
        _r.FrameSize[body] = _nextSlot;
        PopScope();
        PopScope();

        _scopes.Clear();
        _scopes.AddRange(wasScopes);
        _captured = wasCaptured;
        _capturedConstants = wasCapturedConstants;
        _lambdaFloor = wasFloor;
        _thisType = wasThis;
        _lexicalType = wasLexicalType;
        _capturedThisType = wasCapturedThis;
        _capturedThisField = wasCapturedThisField;
        _method = wasMethod;
        _nextSlot = wasSlot;
        _inferredReturns = wasInferred;
        Forget(insideLambda);

        _notNull.Clear();
        _notNull.UnionWith(outerNotNull);
        _notNullPaths.Clear();
        _notNullPaths.UnionWith(outerPaths);

        return wanted;
    }

    /// <summary>
    /// A lambda's written parameter type that is not the delegate's (CS1678),
    /// with the delegate's, or null when they are the same or the delegate's
    /// is not settled yet (a type parameter still open). Nullable annotations
    /// on references are no difference, as they are only a warning in C#.
    /// </summary>
    private (Type Written, Type Delegated)? WrittenParameterMismatch(LambdaExpr lam, int i, Type delegateType, MethodSymbol invoke)
    {
        if (!lam.TypesWritten || i >= lam.Params.Count || i >= invoke.Params.Count) return null;
        Type delegated = ContextualParameterType(delegateType, invoke, i);
        if (delegated.IsError || Open(delegated) || Unmade(delegated)) return null;
        _quiet++;
        Type written = Resolve(lam.Params[i].Type, _thisType);
        _quiet--;
        if (written.IsError) return null;
        return MethodSignatures.SameType(written.AsNonNullable(), delegated.AsNonNullable()) ? null : (written, delegated);

        static bool Open(Type t)
            => t.ParamName is not null || t.Args.Any(Open) || t.Element is Type e && Open(e);
    }

    /// <summary>
    /// Checks a lambda's body, whichever of the two shapes it is.
    /// <paramref name="final"/> when it is checked as its closure's own
    /// Invoke (<see cref="_method"/>), rather than for what it captures.
    /// </summary>
    private void Look(LambdaExpr lam, Type returns, bool final = false)
    {
        if (lam.BlockBody != null)
        {
            // ITS GENERIC LOCAL FUNCTIONS TOO, as CheckBlock declares a
            // block's: a lambda's body is that block, read here statement by
            // statement, and `T Mark<T>(T v)` inside one was never named.
            foreach ((string name, string method) in lam.BlockBody.GenericLocals)
            {
                DeclareGenericLocal(lam.BlockBody, name, method);
            }
            DeclareLocalFunctions(lam.BlockBody);
            bool labels = PushLabels(lam.BlockBody);
            foreach (Stmt s in lam.BlockBody.Statements)
            {
                CheckStmt(s);
            }
            if (labels) _labels.RemoveAt(_labels.Count - 1);
            if (lam.BlockBody.GenericLocals.Count > 0) DiscoverGenericCaptures(lam.BlockBody);
            return;
        }

        // `=> ref a[0]`, OR ANY BODY OF ONE THAT RETURNS BY REFERENCE, is
        // `return ref a[0];` and is checked as that is (CheckRefReturn): a
        // variable, of the delegate's type exactly, that outlives the call.
        // The wrapped return the closure's Invoke is lowered from (Wrap) is
        // this same expression.
        if (final && (lam.Body is RefArgExpr { IsOut: false, Name: null } || _method is { RefReturn: true }))
        {
            CheckRefReturn(new ReturnStmt { Value = lam.Body, Line = lam.Body!.Line, Col = lam.Body.Col });
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

        block.WritableStatements.Add(returns.IsVoid
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
            c.WritableArgNames.Clear();
            return;
        }

        if (!_r.Resolved.TryGetValue(c.Target, out Sym? sym) || sym is not MethodGroupSym group)
        {
            // The target has not been resolved yet on this path -- a call
            // through a value, say. Names cannot be matched to anything, and
            // the ordinary "not a method" diagnostic below is the right one.
            return;
        }

        // A DYNAMIC ARGUMENT leaves the names to the binding made when the
        // program runs, each candidate matching them for itself
        // (Binder.Dynamic's LateOverloads).
        if (_usesDynamic && c.Args.Any(a => !IsFunctionSource(a) && !HoldsLambda(a) && Peek(a is RefArgExpr { Declare: null, Name: null } ra ? ra.Target : a).Dynamic))
        {
            return;
        }

        // EVERY OVERLOAD THE NAMES FIT, and of those the ones the written
        // arguments' types reach (C# 12.6.4.2 applies both). The first whose
        // names fit was taken, and the order a partial class's overloads
        // arrive in decides which is first: `Link(inputs, entry, address,
        // physical, longMode: m)` is Linker.cs's six-parameter Link, and was
        // laid out for Dynamic.cs's eight -- whose names fit too, the gaps
        // having defaults -- when that part came first.
        List<(MethodSymbol Method, Expr?[] Placed, int Filled)> fitting = new();
        List<Type?>? written = null;
        foreach (MethodSymbol m in group.Methods)
        {
            Expr?[] placed = new Expr?[m.Params.Count];
            int[] from = new int[m.Params.Count];
            Array.Fill(from, -1);
            bool fits = true;
            int filled = 0;

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
                from[at] = i;
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
                        // Names are put in order before the receiver of an
                        // extension joins the arguments: written argument k
                        // is span pair k + 1.
                        // The variables a generic local function captured
                        // come first and were never written (Hidden).
                        int hidden = Hidden(c, m);
                        placed[i] = CallerValue(spare, m.Decl.Params, CallLine(c), k => from[k] < hidden ? null : SpanText(c.Spans, c.Source, from[k] - hidden + 1))
                                    ?? Written(m, spare);
                        filled++;
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

            // WHAT THE WRITTEN ARGUMENTS ARE, asked quietly once: one that
            // takes its type from the parameter -- a lambda, a null, a
            // default, a new() -- fits whatever it is placed against.
            written ??= c.Args.Select(a => IsFunctionSource(a) || HoldsLambda(a) || Typeless(a)
                                           || a is LiteralExpr { Kind: Lit.Null } || a is DefaultExpr || a is RefArgExpr
                                           ? null : (Type?)Peek(a)).ToList();
            bool reaches = true;
            for (int i = 0; i < placed.Length && reaches; i++)
            {
                if (from[i] < 0 || written[from[i]] is not Type given || given.IsError) continue;
                Type want = m.Params[i].Type;
                reaches = Convertible(given, want) || Variant(given, want) || Unmade(want)
                    || given.IsInteger && want.IsInteger && ConstantValue(c.Args[from[i]], _thisType) is long v && Binder.Fits(v, want)
                    || want.Symbol is not null && UserConversion(given, want, false, IntegerConstant(c.Args[from[i]], given)) is not null;
            }
            fitting.Add((m, placed, reaches ? filled : int.MaxValue));
        }

        // Reached by the written arguments first; of those, the one needing
        // the fewest defaults (C# 12.6.4.3's last tie-break); names alone
        // decide only when no candidate's types were reached.
        if (fitting.Count > 0)
        {
            (MethodSymbol _, Expr?[] chosen, int _) = fitting.OrderBy(f => f.Filled).First();
            c.Args.Clear();
            c.Args.AddRange(chosen!);
            c.WritableArgNames.Clear();
            return;
        }

        Error(c, $"no overload of '{group.Methods[0].Name}' takes arguments named "
                + string.Join(", ", c.ArgNames.Where(n => n != null).Select(n => $"'{n}'")));
        c.WritableArgNames.Clear();
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
            // A METHOD GROUP'S OUTPUT TYPE IS ITS METHOD'S RETURN (C#
            // 12.6.3.7): `names.Select(table.Add)` is a Select to what Add
            // answers. Read from the method, not from a lambda standing in for
            // the group: one bound to a receiver calls through the closure's
            // target field, which nothing outside the closure can name.
            if (written[i] is not LambdaExpr && GroupReturns(written[i], Close(m.Params[i].Type, bound)) is Type answered)
            {
                Type gives = Substitute(Invoked(m.Params[i].Type)?.Returns ?? Type.Error, Applied(m.Params[i].Type));
                if (!Unify(m, gives, answered, bound)) return false;
                continue;
            }
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
    /// <summary>
    /// A conditional one of whose arms is a lambda, however deep: like a bare
    /// lambda it has no type until the parameter it is passed to gives it one
    /// (`compile ? count => Args(count) : null` for a Func&lt;int, string[]&gt;?),
    /// so it waits for the overload as a lambda does and is checked after.
    /// </summary>
    /// <summary>
    /// `a + b` OR `a - b` OVER A DELEGATE, as a call: __Delegates.Combine or
    /// __Delegates.Remove, which are .NET's Delegate.Combine and Remove typed
    /// as the delegate (the runtime's, generic over it). Built as ordinary
    /// syntax, to be bound like anything the program could have written;
    /// `+=` and `-=` remember it for lowering, which stores its result back
    /// into the same place, and the binary operators become it. The right
    /// side is an argument, so a lambda or a method group there becomes the
    /// delegate the left side is, as C# target-types it.
    ///
    /// NOT the multicast class the parser writes beside the delegate, by its
    /// name: the name of a generic delegate's, or one nested in a generic
    /// type, is not something a call written here can spell, and a delegate
    /// held as one of another type by variance must be refused as .NET's
    /// Delegate.Combine refuses it. Delegate.Combine asks the delegate itself,
    /// whose type knows its multicast (Parser.ParseDelegateDeclaration).
    /// </summary>
    private static CallExpr DelegateCombination(bool add, Expr left, Expr right, Node at, Type delegateType)
    {
        MemberExpr helper = new()
        {
            Target = new NameExpr { Name = DelegatesHelper, Line = at.Line, Col = at.Col },
            Name = add ? "Combine" : "Remove", Line = at.Line, Col = at.Col,
        };
        // THE DELEGATE NAMED, not inferred: a lambda or a method group on the
        // right has no type for inference to read until it is converted, and
        // the left side's is the one C# converts it to.
        // Named as maybe null: either side of `+=` may be, and Combine's T?
        // parameters with T named bare were taken as not.
        if (RefOf(delegateType.AsNullable()) is TypeRef spelt) helper.WritableTypeArgs.Add(spelt);
        CallExpr made = new() { Target = helper, Line = at.Line, Col = at.Col };
        made.Args.Add(left);
        made.Args.Add(right);
        made.WritableArgNames.Add(null);
        made.WritableArgNames.Add(null);
        return made;
    }

    /// <summary>The runtime's class of what delegate operators become, under a name no program writes.</summary>
    private const string DelegatesHelper = "__Delegates";

    /// <summary>
    /// The runtime's Delegate, which every delegate type has for its base
    /// (DeclareMembersIn): the library's, moved into System when a program
    /// took its name. Null in a program compiled without the runtime.
    /// </summary>
    private TypeSymbol? DelegateRoot()
    {
        if (_r.Types.TryGetValue(LibraryHome + ".Delegate", out TypeSymbol? moved)
            && moved.Decl is { MovedToSystem: true } && moved.Kind == TypeKind.Interface)
        {
            return moved;
        }
        return _r.Types.TryGetValue("Delegate", out TypeSymbol? plain)
               && plain.Kind == TypeKind.Interface && plain.Decl is { IsDelegate: false } ? plain : null;
    }

    /// <summary>
    /// Whether a value of this type is a delegate: of a delegate type, or of
    /// Delegate itself. Two of them compare as delegates (CheckBinary).
    /// </summary>
    private bool IsDelegateValue(Type t)
        => !t.IsError && !t.IsArray && !t.IsNullableValue && t.PointerDepth == 0
        && t.Symbol is { Kind: TypeKind.Interface } face
        && (face.Decl?.IsDelegate == true || ReferenceEquals(face, DelegateRoot()));

    /// <summary>
    /// THE MEMBERS EVERY DELEGATE HAS, given to a closure the compiler made of
    /// a lambda or a method group. A closure is a class written here
    /// (CheckLambda) with one method, Invoke, in the delegate's slot; the
    /// runtime's Delegate, which its delegate type extends, has members of
    /// its own with bodies -- GetInvocationList, and CombineImpl and
    /// RemoveImpl, which each delegate type answers for itself with explicit
    /// implementations written into its interface (Parser.
    /// ParseDelegateDeclaration). A class the program declares has those
    /// found for it as it is laid out (AssignSlots); a closure never is, so
    /// they are found here by the same rule: the most specific explicit
    /// implementation among the interfaces it has, else the member's own body.
    /// </summary>
    private static void DelegateMembers(TypeSymbol closure, TypeSymbol face)
    {
        if (IsTemplate(face))
        {
            return;
        }
        foreach (TypeSymbol iface in Extended(face))
        {
            if (ReferenceEquals(iface, face))
            {
                continue;
            }
            string ifaceName = ExplicitName(iface);
            foreach (MethodSymbol want in iface.Methods)
            {
                if (want.Static || want.VtableSlot < 0 || want.TypeParams.Count > 0 || want.ExplicitInterface is not null)
                {
                    continue;
                }
                MethodSymbol? impl = Extended(face)
                    .Select(f => f.Methods.FirstOrDefault(m => m.ExplicitMember == want.Name && m.ExplicitInterface == ifaceName
                                                            && m.Decl?.Body is not null && MethodSignatures.Implements(m, want)))
                    .FirstOrDefault(found => found is not null)
                    ?? (want.Decl?.Body is not null ? want : null);
                if (impl is not null)
                {
                    closure.WritableInterfaceImplementations[want.VtableSlot] = impl;
                }
            }
        }
    }

    /// <summary>
    /// A FIELD-LIKE EVENT USED FROM OUTSIDE THE TYPE THAT DECLARES IT, which
    /// C# allows only on the left of += and -= (CS0070): outside, an event
    /// is something to subscribe to, and raising it, reading it or assigning
    /// it is the declaring type's business. Inside that type -- its nested
    /// types and the closures written in it included -- it is the field it
    /// looks like. The target of a += or -= is let through (_eventOperands).
    /// A derived type is outside, as in C#: it raises a base's event through
    /// a method the base gives it.
    /// </summary>
    private void EventFromOutside(Expr e, FieldSymbol field)
    {
        if (_eventOperands.Contains(e))
        {
            return;
        }
        string owner = field.Owner.Key;
        string? here = _thisType?.Key;
        if (here is not null && (here == owner || here.StartsWith(owner + ".", StringComparison.Ordinal)))
        {
            return;
        }
        Error(e, $"the event '{field.Owner.Name}.{field.Name}' can only appear on the left hand side of += or -= "
                 + $"(except when used from within the type '{field.Owner.Name}')");
    }

    /// <summary>The targets of the += and -= checked so far: an event there is subscribed to, not used.</summary>
    private readonly HashSet<Expr> _eventOperands = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The call an event's accessor makes of `+=` or `-=`, or null when the
    /// target is not an event with accessors: a name whose type has an
    /// add_ method of it and no field or property of the name itself (a
    /// field-like event is a field, and += combines into it).
    /// </summary>
    private CallExpr? EventAccessorCall(AssignExpr a)
    {
        string prefix = a.Op == BinOp.Add ? "add_" : "remove_";
        TypeSymbol? owner;
        string name;
        Expr? receiver;

        switch (a.Target)
        {
            case MemberExpr m:
            {
                name = m.Name;
                receiver = m.Target;
                if (m.Target is NameExpr typeName && Lookup(typeName.Name) is null
                    && FindType(typeName.Name, out TypeSymbol? named) && named is not null)
                {
                    owner = named;
                    break;
                }
                _quiet++;
                Type held = CheckExpr(m.Target);
                _quiet--;
                owner = held.AsNonNullable().Symbol;
                break;
            }
            case NameExpr n when Lookup(n.Name) is null:
                name = n.Name;
                receiver = null;
                owner = _thisType;
                break;
            default:
                return null;
        }

        if (owner is null || owner.FindField(name) is not null || owner.FindMethods("get_", name).Count > 0
            || MethodsOn(owner, prefix + name).Count == 0)
        {
            return null;
        }

        CallExpr call = new()
        {
            Target = receiver is null
                ? new NameExpr { Name = prefix + name, Line = a.Line, Col = a.Col, File = a.File }
                : new MemberExpr { Target = receiver, Name = prefix + name, Line = a.Line, Col = a.Col, File = a.File },
            Line = a.Line, Col = a.Col, File = a.File,
        };
        call.Args.Add(a.Value);
        return call;
    }

    /// <summary>
    /// An argument whose type the parameter decides: a conditional with a
    /// lambda arm, or a TUPLE LITERAL with an element that has no type of its
    /// own. C# gives `(key, null)` no natural type at all -- it is converted
    /// to the tuple type it is passed as, element by element, exactly as a
    /// lambda is converted to its delegate -- so it waits for the overload to
    /// be chosen. Checked early, it became a tuple whose second element was
    /// the type of null, and the class made for it named a shape no C# type
    /// has.
    /// </summary>
    private static bool HoldsLambda(Expr e) => e switch
    {
        ConditionalExpr c => c.Then is LambdaExpr || c.Else is LambdaExpr || HoldsLambda(c.Then) || HoldsLambda(c.Else),
        TupleExpr t => t.Items.Any(Typeless),
        _ => false,
    };

    /// <summary>
    /// AN INDEX WHOSE TYPE THE INDEXER DECIDES -- `d[(null, "x")]` on a
    /// Dictionary keyed by a tuple of a class and a string -- checked as the
    /// parameter it is passed as, when every indexer taking that many has
    /// the same one there: C# converts the literal to the key's tuple type,
    /// element by element, as it does an argument of a method. Checked with
    /// nothing wanted, its null element had no type and the access was
    /// refused.
    /// </summary>
    private List<Type> CheckIndexArgs(Type target, List<Expr> args, IEnumerable<MethodSymbol> indexers)
    {
        // The indexers taking this many, gathered only when an argument holds
        // a lambda and so asks what they want: most index with a plain value.
        List<MethodSymbol>? taking = null;
        List<Type> index = new(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            Type? want = null;
            if (HoldsLambda(args[i]))
            {
                if (taking is null)
                {
                    taking = new List<MethodSymbol>();
                    foreach (MethodSymbol m in indexers)
                    {
                        if (m.Params.Count == args.Count) taking.Add(m);
                    }
                }
                if (taking.Count > 0)
                {
                    List<Type> wants = taking.Select(m => ThroughUnmade(target, m, m.Params[i].Type)).Distinct().ToList();
                    if (wants.Count == 1) want = wants[0];
                }
            }
            if (want is null)
            {
                index.Add(CheckExpr(args[i]));
                continue;
            }
            Type? outer = _wanted;
            _wanted = want;
            try { index.Add(CheckExpr(args[i])); }
            finally { _wanted = outer; }
        }
        return index;
    }

    /// <summary>An expression with no type until something waiting for it gives it one.</summary>
    private static bool Typeless(Expr e)
        => e is LambdaExpr or LiteralExpr { Kind: Lit.Null } or DefaultExpr { Type.Name.Length: 0 }
                or NewExpr { Type.Name.Length: 0, Elements: null }
        || HoldsLambda(e);

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
    /// <summary>
    /// What a method group answers converted to `wanted`: the return type of
    /// its one non-generic method of the delegate's arity; null for a group
    /// with none or more than one such, or anything else.
    /// </summary>
    private Type? GroupReturns(Expr source, Type wanted)
    {
        if (!_r.Resolved.TryGetValue(source, out Sym? sym)) return null;
        IReadOnlyList<MethodSymbol>? candidates = sym switch
        {
            MethodGroupSym mg => mg.Methods,
            CapturedMethodGroupSym cg => cg.Methods,
            _ => null,
        };
        if (candidates is null || Invoked(wanted) is not { } invoke) return null;
        // Each of the delegate's parameters, as far as it is known, converts
        // to the method's: Select's (T, int) overload is no GetFullPath(string,
        // string).
        Dictionary<string, Type> applied = Applied(wanted);
        bool Accepts(MethodSymbol c)
        {
            for (int k = 0; k < c.Params.Count; k++)
            {
                Type given = Substitute(invoke.Params[k].Type, applied);
                if (given.ParamName is null && !given.IsError && !Convertible(given, c.Params[k].Type)) return false;
            }
            return true;
        }
        List<MethodSymbol> fits = candidates.Where(c => c.Params.Count == invoke.Params.Count && c.TypeParams.Count == 0 && Accepts(c)).ToList();
        if (fits.Count != 1 || fits[0].Returns.IsVoid) return null;
        return fits[0].Returns;
    }

    private LambdaExpr? MethodGroupLambda(Expr source, Type wanted, bool localFunctions = false)
    {
        if (!_r.Resolved.TryGetValue(source, out Sym? sym))
        {
            return null;
        }

        if (sym is not (MethodGroupSym or CapturedMethodGroupSym))
        {
            return localFunctions && LocalFunctionConverts(source, wanted) ? LocalFunctionLambda((NameExpr)source, wanted) : null;
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
            onTarget.WritableTypeArgs.AddRange(member.TypeArgs);
            callTarget = onTarget;
        }

        CallExpr call = new() { Target = callTarget, Line = source.Line, Col = source.Col };
        // A DELEGATE THAT RETURNS BY REFERENCE hands on the variable the
        // method answers: `=> ref Method(...)`.
        LambdaExpr made = new() { Body = Answer(invoke, call), Line = source.Line, Col = source.Col };
        // Which method the group means here is the one whose arity the
        // delegate's Invoke has; its identity names the closure class, so a
        // second conversion of the same method anywhere in the type is the
        // same class and the two compare equal, as C# requires of delegates.
        IReadOnlyList<MethodSymbol> candidates = sym is MethodGroupSym mg ? mg.Methods : ((CapturedMethodGroupSym)sym).Methods;
        MethodSymbol? chosen = candidates.FirstOrDefault(m => m.Params.Count == invoke.Params.Count);
        // Not a generic local function's: its closure holds the variables
        // that one captured where it was converted, which are no other's.
        if (chosen is not null && candidates.All(m => m.Decl is not MethodDecl { HoistedName: not null }))
            made.GroupIdentity = ClosureIdentity.Of(chosen) + (receiver is null ? "" : "$bound");
        if (receiver is not null) _boundTargets[made] = receiver;

        for (int i = 0; i < invoke.Params.Count; i++)
        {
            string name = "$arg" + i;
            ParamSymbol delegated = invoke.Params[i];
            // PASSED ON AS IT CAME: an out or ref parameter of the delegate is
            // one of the wrapper's too, and the method is called with the word
            // -- `TryIt f = Len;` over `bool Len(string s, out int v)`.
            bool isOut = delegated.ByRef && chosen is not null && i < chosen.Params.Count
                      && chosen.Decl is MethodDecl { } declared && i < declared.Params.Count && declared.Params[i].IsOut;
            made.WritableParams.Add(new Param
            {
                Name = name,
                Type = new TypeRef { Name = "object", Line = source.Line, Col = source.Col },
                IsRef = delegated.ByRef && !isOut, IsOut = isOut, IsReadOnlyRef = delegated.ReadOnly,
                Line = source.Line,
                Col = source.Col,
            });
            NameExpr passed = new() { Name = name, Line = source.Line, Col = source.Col };
            call.Args.Add(delegated.ByRef && !delegated.ReadOnly
                ? new RefArgExpr { Target = passed, IsOut = isOut, Line = source.Line, Col = source.Col }
                : passed);
        }
        return made;
    }

    /// <summary>
    /// What a delegate made of a method group answers: the call, or for an
    /// Invoke that returns by reference, the variable the call answers.
    /// </summary>
    private static Expr Answer(MethodSymbol invoke, CallExpr call)
        => invoke.RefReturn ? new RefArgExpr { Target = call, Line = call.Line, Col = call.Col } : call;

    /// <summary>
    /// Whether a local function's name, written where a delegate of ANOTHER
    /// type is wanted, converts to it: a local function is a method group
    /// (C# 10.8), and `values.RemoveAll(Big)` over `bool Big(long v)` is a
    /// Predicate&lt;long&gt;, not the Func&lt;long, bool&gt; the local holds. Same
    /// arity, and each parameter and the result the same type.
    /// </summary>
    private bool LocalFunctionConverts(Expr source, Type wanted)
    {
        if (source is not NameExpr || !_r.Resolved.TryGetValue(source, out Sym? sym)
            || LocalFunctionDeclaration(sym) is null
            || _r.TypeOf(source) is not { Symbol: { } own } held
            || wanted.Symbol is not { } target || ReferenceEquals(own, target)
            || Unmade(wanted) || wanted.ParamName is not null)
        {
            return false;
        }

        MethodSymbol? mine = own.FindMethods("Invoke").FirstOrDefault();
        MethodSymbol? theirs = target.FindMethods("Invoke").FirstOrDefault();
        return mine is not null && theirs is not null && !held.IsNullableValue
            && mine.Params.Count == theirs.Params.Count
            && mine.Params.Zip(theirs.Params).All(pair => MethodSignatures.SameType(pair.First.Type, pair.Second.Type)
                                                        && pair.First.ByRef == pair.Second.ByRef)
            && MethodSignatures.SameType(mine.Returns, theirs.Returns)
            && mine.RefReturn == theirs.RefReturn && mine.RefReturnReadOnly == theirs.RefReturnReadOnly;
    }

    /// <summary>
    /// The delegate a local function converts to: a lambda of the wanted
    /// type calling it with the arguments it is given.
    /// </summary>
    private LambdaExpr LocalFunctionLambda(NameExpr source, Type wanted)
    {
        MethodSymbol invoke = wanted.Symbol!.FindMethods("Invoke").First();
        CallExpr call = new() { Target = new NameExpr { Name = source.Name, Line = source.Line, Col = source.Col },
                                Line = source.Line, Col = source.Col };
        LambdaExpr made = new() { Body = Answer(invoke, call), Line = source.Line, Col = source.Col };
        // WHICH LOCAL FUNCTION, wherever it is converted: the closure of every
        // conversion holds the local function's own cell, so two made over
        // one run of its scope are the same method on the same target.
        if (_r.Resolved.TryGetValue(source, out Sym? named) && LocalFunctionDeclaration(named) is { } declared)
        {
            made.LocalGroup = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("local$" + declared.File + ":" + declared.Line + ":" + declared.Col + ":" + source.Name)));
        }
        for (int i = 0; i < invoke.Params.Count; i++)
        {
            string name = "$arg" + i;
            ParamSymbol delegated = invoke.Params[i];
            made.WritableParams.Add(new Param
            {
                Name = name,
                Type = new TypeRef { Name = "object", Line = source.Line, Col = source.Col },
                IsRef = delegated.ByRef, IsReadOnlyRef = delegated.ReadOnly,
                Line = source.Line,
                Col = source.Col,
            });
            NameExpr passed = new() { Name = name, Line = source.Line, Col = source.Col };
            call.Args.Add(delegated.ByRef && !delegated.ReadOnly
                ? new RefArgExpr { Target = passed, Line = source.Line, Col = source.Col }
                : passed);
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
    /// <summary>
    /// Which lengths of a countable subject a set of list patterns admits: one
    /// flag per length up to a bound, and one for every length past it.
    /// </summary>
    private sealed class Coverage
    {
        private const int Bound = 64;
        private readonly bool[] _each = new bool[Bound];
        private bool _beyond;

        public static Coverage Everything()
        {
            Coverage c = new();
            Array.Fill(c._each, true);
            c._beyond = true;
            return c;
        }

        public static Coverage Exactly(long n)
        {
            Coverage c = new();
            if (n >= 0 && n < Bound) c._each[n] = true;
            return c;
        }

        public static Coverage AtLeast(long n)
        {
            Coverage c = new();
            for (long i = Math.Max(0, n); i < Bound; i++) c._each[i] = true;
            c._beyond = n <= Bound;
            return c;
        }

        public bool All => _beyond && _each.All(x => x);

        public void Union(Coverage other)
        {
            for (int i = 0; i < Bound; i++) _each[i] |= other._each[i];
            _beyond |= other._beyond;
        }

        public Coverage Intersect(Coverage other)
        {
            Coverage c = new();
            for (int i = 0; i < Bound; i++) c._each[i] = _each[i] && other._each[i];
            c._beyond = _beyond && other._beyond;
            return c;
        }
    }

    /// <summary>
    /// The lengths a pattern's test is sure to accept, when LENGTH is the only
    /// thing it can fail on; null when anything else about it might fail.
    /// Reads the tests the parser writes for a list pattern (ParseListPattern):
    /// a null test of a subject that cannot be null, a count compared with a
    /// number, and element tests that are discards or var patterns.
    /// </summary>
    private Coverage? Lengths(Expr test)
    {
        switch (test)
        {
            case LiteralExpr { Kind: Lit.Bool, IntValue: 1 }:
                return Coverage.Everything();
            case SequenceExpr seq:
                return Lengths(seq.Value);
            case PatternExpr pat:
                return Lengths(pat.Test);
            case IsExpr { Binding: not null } named when named.Type.Name == TypeRef.Anything:
                return Coverage.Everything();
            case IsExpr named when named.Type.Name == TypeRef.Same:
                return NeverNull(named.Operand) ? Coverage.Everything() : null;
            case BinaryExpr { Op: BinOp.Ne, PatternNullTest: true } nonNull:
                return NeverNull(nonNull.Left) ? Coverage.Everything() : null;
            case BinaryExpr { Op: BinOp.Eq or BinOp.Ge, Left: MemberExpr { Else: not null }, Right: LiteralExpr { Kind: Lit.Int } n } count:
                return count.Op == BinOp.Eq ? Coverage.Exactly(n.IntValue) : Coverage.AtLeast(n.IntValue);
            case BinaryExpr { Op: BinOp.AndAlso } both:
                return Lengths(both.Left) is { } first && Lengths(both.Right) is { } second ? first.Intersect(second) : null;
            case BinaryExpr { Op: BinOp.OrElse } either:
            {
                Coverage? l = Lengths(either.Left), r = Lengths(either.Right);
                if (l is null) return r;
                if (r is not null) l.Union(r);
                return l;
            }
            default:
                return null;
        }
    }

    /// <summary>Whether what an expression was checked to be cannot hold null.</summary>
    private bool NeverNull(Expr e)
        => _r.ExprType.TryGetValue(e, out Type? t) && !t.Nullable && !t.IsNullableValue && t.Prim != Prim.NullLiteral;

    /// <summary>Whether a type has a readable member of this name: an array or a string its Length.</summary>
    private static bool Countable(Type t, string name)
        => (t.IsArray || t.Prim == Prim.String) ? name == "Length"
         : t.Symbol is { } sym && (Reachable(sym, "get_" + name).Any(g => g.Params.Count == 0) || sym.FindField(name) is not null);

    private Type Peek(Expr e)
    {
        int errors = _r.Errors.Count;
        int warnings = _r.Warnings.Count;
        int wanted = _r.Wanted.Count;
        int overrides = _r.WantedOverrides.Count;
        // WHAT THE LOOK DECLARED IS TAKEN BACK TOO. An `out var` or a pattern
        // variable in the expression declares a name in the scope it is
        // checked in, and the real check that follows declares it again: a
        // `foreach` over `(t.TryGetValue(k, out string? v) ? v : "")` said v
        // was already declared.
        LocalScope scope = _scopes[^1];
        HashSet<string> names = new(scope.Keys, StringComparer.Ordinal);
        HashSet<string> nested = new(scope.NestedNames, StringComparer.Ordinal);
        int slot = _nextSlot;
        Type had = CheckExpr(e);

        foreach (string name in scope.Keys.Where(name => !names.Contains(name)).ToList())
        {
            if (scope[name] is LocalSym local) _assigned.Remove(local);
            scope.Remove(name);
        }
        scope.NestedNames.IntersectWith(nested);
        _nextSlot = slot;
        _r.Errors.RemoveRange(errors, _r.Errors.Count - errors);
        _r.Warnings.RemoveRange(warnings, _r.Warnings.Count - warnings);
        _r.Wanted.RemoveRange(wanted, _r.Wanted.Count - wanted);
        _r.WantedOverrides.RemoveRange(overrides, _r.WantedOverrides.Count - overrides);
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
            inside.WritableStatements.Add(new LocalDecl
            {
                Name = element, Init = take, Line = fe.Line, Col = fe.Col,
            });
            inside.WritableStatements.AddRange(Deconstruct(
                fe,
                new NameExpr { Name = element, Line = fe.Line, Col = fe.Col },
                split,
                seq.Element ?? Type.Error));
            inside.WritableStatements.Add(fe.Body);

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

            whole.WritableStatements.Add(new LocalDecl
            {
                Name = walked, Init = fe.Sequence, Line = fe.Line, Col = fe.Col,
            });
            whole.WritableStatements.Add(stepping);
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

            // A LIST OR AN ARRAY BEHIND A SEQUENCE INTERFACE is walked without
            // the enumerator box the interface hands out: the sequence's exact
            // type is asked once, and a List<E> is walked with its own struct
            // enumerator -- the same MoveNext, the same version check, so the
            // same answers and the same exception when it is changed under
            // the loop -- and an E[] by index, which is what its enumerator
            // does. Anything else, a subclass of List<E> included (it may
            // implement the interface again), takes the interface's own
            // enumerator as before. One loop and one body serve all three,
            // the way taken chosen by a word per step. This is the guarded
            // devirtualisation .NET's JIT does for the same loop.
            var fast = FastSequence(had);
            string mode = $"$mode${n}", listWalker = $"$list${n}", array = $"$array${n}", step = $"$at${n}";
            string held = $"$walked${n}";
            Expr Num(int v) => new LiteralExpr { Kind = Lit.Int, Text = v.ToString(), IntValue = v, Line = fe.Line, Col = fe.Col };
            Expr Is(int v) => new BinaryExpr { Op = BinOp.Eq, Left = Named(mode), Right = Num(v), Line = fe.Line, Col = fe.Col };
            Expr Pick(Func<Expr> list, Expr arr, Expr other)
            {
                Expr rest = new ConditionalExpr { Cond = Is(2), Then = arr, Else = other, Line = fe.Line, Col = fe.Col };
                return fast is { List: not null }
                    ? new ConditionalExpr { Cond = Is(1), Then = list(), Else = rest, Line = fe.Line, Col = fe.Col }
                    : rest;
            }
            Expr Current()
            {
                if (fast is null) return On(Named(walker), "Current");
                IndexExpr item = new() { Target = Named(array), Line = fe.Line, Col = fe.Col };
                item.Args.Add(Named(step));
                return Pick(() => On(Named(listWalker), "Current"), item, On(Named(walker), "Current"));
            }

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
                Type walked = Close(walk.Returns, Received(seq, walk.Owner ?? had));

                Type each = walked.Symbol is TypeSymbol e
                          ? Close(Reachable(e, "get_Current").FirstOrDefault()?.Returns ?? Type.Error,
                                  Received(walked, e))
                          : Type.Error;

                body.WritableStatements.Add(new LocalDecl
                {
                    Name = element, Init = Current(),
                    Line = fe.Line, Col = fe.Col,
                });
                body.WritableStatements.AddRange(Deconstruct(fe, Named(element), taken, each));
            }
            else
            {
                body.WritableStatements.Add(new LocalDecl
                {
                    Type = fe.Type, Name = fe.Name, Init = Current(),
                    Line = fe.Line, Col = fe.Col,
                });
            }

            body.WritableStatements.Add(fe.Body);

            Expr moving = Called(Named(walker), "MoveNext");
            if (fast is { } chosen)
            {
                TypeRef written = RefOf(seq)!;
                TypeRef arrayRef = RefOf(Type.ArrayOf(chosen.Element))!;
                Expr Exactly(TypeRef t) => new BinaryExpr
                {
                    Op = BinOp.Eq,
                    Left = new CallExpr { Target = On(Named(held), "GetType"), Line = fe.Line, Col = fe.Col },
                    Right = new TypeOfExpr { Type = t, Line = fe.Line, Col = fe.Col },
                    Line = fe.Line, Col = fe.Col,
                };
                Expr Null() => new LiteralExpr { Kind = Lit.Null, Text = "null", Line = fe.Line, Col = fe.Col };
                Stmt Set(string name, Expr value) => new ExprStmt
                {
                    Expr = new AssignExpr { Target = Named(name), Value = value, Line = fe.Line, Col = fe.Col },
                    Line = fe.Line, Col = fe.Col,
                };

                outer.WritableStatements.Add(new LocalDecl { Type = written, Name = held, Init = fe.Sequence, Line = fe.Line, Col = fe.Col });
                outer.WritableStatements.Add(new LocalDecl
                {
                    Type = new TypeRef { Name = "int", Line = fe.Line, Col = fe.Col },
                    Name = mode,
                    Init = new ConditionalExpr
                    {
                        Cond = new BinaryExpr { Op = BinOp.Eq, Left = Named(held), Right = Null(), Line = fe.Line, Col = fe.Col },
                        Then = Num(0),
                        Else = chosen.List is { } listed
                            ? new ConditionalExpr
                            {
                                Cond = Exactly(RefOf(Type.Plain(listed, Prim.Void))!), Then = Num(1),
                                Else = new ConditionalExpr { Cond = Exactly(arrayRef), Then = Num(2), Else = Num(0), Line = fe.Line, Col = fe.Col },
                                Line = fe.Line, Col = fe.Col,
                            }
                            : new ConditionalExpr { Cond = Exactly(arrayRef), Then = Num(2), Else = Num(0), Line = fe.Line, Col = fe.Col },
                        Line = fe.Line, Col = fe.Col,
                    },
                    Line = fe.Line, Col = fe.Col,
                });
                if (chosen.Walker is { } listWalking)
                {
                    outer.WritableStatements.Add(new LocalDecl
                    {
                        // NO INITIALISER: read only where the List was taken,
                        // after the one assignment there. A default made a
                        // block of its own, the assignment a second, and a
                        // register given both was one the lifetime rules
                        // could not follow: every List walk left its
                        // enumerator to the collector.
                        Type = RefOf(listWalking)!, Name = listWalker,
                        Line = fe.Line, Col = fe.Col,
                    });
                }
                outer.WritableStatements.Add(new LocalDecl
                {
                    Type = RefOf(Type.ArrayOf(chosen.Element).AsNullable())!, Name = array, Init = Null(), Line = fe.Line, Col = fe.Col,
                });
                outer.WritableStatements.Add(new LocalDecl { Type = new TypeRef { Name = "int", Line = fe.Line, Col = fe.Col }, Name = step, Init = Num(-1), Line = fe.Line, Col = fe.Col });
                outer.WritableStatements.Add(new LocalDecl
                {
                    Type = RefOf(Close(walk.Returns, Received(seq, walk.Owner ?? had)).AsNullable())!, Name = walker, Init = Null(),
                    Line = fe.Line, Col = fe.Col,
                });
                IfStmt notList = new()
                {
                    Cond = Is(2),
                    Then = Set(array, new CastExpr { Type = RefOf(Type.ArrayOf(chosen.Element))!, Operand = Named(held), Line = fe.Line, Col = fe.Col }),
                    Else = Set(walker, Called(Named(held), "GetEnumerator")),
                    Line = fe.Line, Col = fe.Col,
                };
                outer.WritableStatements.Add(chosen.List is { } listType
                    ? new IfStmt
                    {
                        Cond = Is(1),
                        Then = Set(listWalker, Called(new CastExpr { Type = RefOf(Type.Plain(listType, Prim.Void))!, Operand = Named(held), Line = fe.Line, Col = fe.Col }, "GetEnumerator")),
                        Else = notList,
                        Line = fe.Line, Col = fe.Col,
                    }
                    : notList);
                moving = Pick(
                    () => Called(Named(listWalker), "MoveNext"),
                    new BinaryExpr
                    {
                        Op = BinOp.Lt,
                        Left = new UnaryExpr { Op = UnOp.PreInc, Operand = Named(step), Line = fe.Line, Col = fe.Col },
                        Right = On(new SuppressExpr { Operand = Named(array), Line = fe.Line, Col = fe.Col }, "Length"),
                        Line = fe.Line, Col = fe.Col,
                    },
                    Called(new SuppressExpr { Operand = Named(walker), Line = fe.Line, Col = fe.Col }, "MoveNext"));
            }
            else
            {
                outer.WritableStatements.Add(new LocalDecl
                {
                    Name = walker, Init = Called(fe.Sequence, "GetEnumerator"),
                    Line = fe.Line, Col = fe.Col,
                });
            }
            WhileStmt stepping = new()
            {
                Cond = moving, Body = body,
                Line = fe.Line, Col = fe.Col,
            };

            // AND THE ENUMERATOR IS DISPOSED however the loop ends -- run out,
            // broken out of, or thrown out of -- as C# disposes it (15.8.4): a
            // disposable one directly, a class one only when it is there, and
            // an unsealed class that is not known to be disposable when it
            // turns out to be. A struct whose Dispose does nothing is left
            // alone, which is the same as calling it; List<T>'s is one, and
            // a list loop keeps no handler for it.
            Type enumeratorType = Close(walk.Returns, Received(seq, walk.Owner ?? had));
            Stmt? release = null;
            if (enumeratorType.Symbol is TypeSymbol walking)
            {
                MethodSymbol? dispose = Reachable(walking, "Dispose").FirstOrDefault(m => m.Params.Count == 0 && !m.Static);
                bool disposable = dispose is not null
                    && (walking.Kind == TypeKind.Struct || AllInterfaces(walking).Any(i => i.Name == "IDisposable") || walking.Name == "IDisposable");
                bool empty = walking.Kind == TypeKind.Struct && dispose?.Decl is MethodDecl { Body.Statements.Count: 0 };
                if (disposable && !empty)
                {
                    Stmt call = new ExprStmt { Expr = Called(Named(walker), "Dispose"), Line = fe.Line, Col = fe.Col };
                    release = walking.Kind == TypeKind.Struct ? call : new IfStmt
                    {
                        Cond = new BinaryExpr
                        {
                            Op = BinOp.Ne, Left = Named(walker),
                            Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = fe.Line, Col = fe.Col },
                            Line = fe.Line, Col = fe.Col,
                        },
                        Then = call, Line = fe.Line, Col = fe.Col,
                    };
                }
                else if (!disposable && walking.Kind == TypeKind.Class && walking.Decl?.Mods.HasFlag(Mods.Sealed) != true
                         && _r.Types.ContainsKey("IDisposable"))
                {
                    string maybe = $"$disposable${n}";
                    release = new IfStmt
                    {
                        Cond = new IsExpr
                        {
                            Operand = Named(walker), Type = new TypeRef { Name = "IDisposable", Line = fe.Line, Col = fe.Col },
                            Binding = maybe, Line = fe.Line, Col = fe.Col,
                        },
                        Then = new ExprStmt { Expr = Called(Named(maybe), "Dispose"), Line = fe.Line, Col = fe.Col },
                        Line = fe.Line, Col = fe.Col,
                    };
                }
            }

            if (release is null)
            {
                outer.WritableStatements.Add(stepping);
            }
            else
            {
                Block guarded = new() { Line = fe.Line, Col = fe.Col };
                guarded.WritableStatements.Add(stepping);
                Block finish = new() { Line = fe.Line, Col = fe.Col };
                // Only the interface's enumerator is the loop's to dispose: a
                // List<E>'s does nothing, and an array has none.
                finish.WritableStatements.Add(fast is null ? release : new IfStmt { Cond = Is(0), Then = release, Line = fe.Line, Col = fe.Col });
                outer.WritableStatements.Add(new TryStmt { Body = guarded, Finally = finish, Line = fe.Line, Col = fe.Col });
            }
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


            stepped.WritableStatements.Add(new LocalDecl
            {
                Name = element, Init = read, Line = fe.Line, Col = fe.Col,
            });
            stepped.WritableStatements.AddRange(Deconstruct(fe, Named(element), names, each));
        }
        else
        {
            stepped.WritableStatements.Add(new LocalDecl
            {
                Type = fe.Type, Name = fe.Name, Init = read, Line = fe.Line, Col = fe.Col,
            });
        }

        stepped.WritableStatements.Add(fe.Body);

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

        outer.WritableStatements.Add(new LocalDecl
        {
            Name = over, Init = fe.Sequence, Line = fe.Line, Col = fe.Col,
        });
        outer.WritableStatements.Add(loop);
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
    /// <summary>
    /// For a foreach over a sequence interface (Iterate): its element, the
    /// List of that element this unit has, and that list's struct enumerator
    /// -- or null when the interface is not one a List implements, or no
    /// List of the element is specialised here, in which case no list of it
    /// is made here either and the interface's path is all there is.
    /// </summary>
    private (Type Element, TypeSymbol? List, Type? Walker)? FastSequence(TypeSymbol had)
    {
        if (had.Kind != TypeKind.Interface
            || had.Decl is not { Template: "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "IList" or "ICollection", TemplateArgs.Count: 1 } made)
        {
            return null;
        }
        Type element = Resolve(made.TemplateArgs[0], _thisType);
        if (element.IsError || element.ParamName is not null || element.IsPointer || RefOf(element) is not TypeRef written
            || RefOf(Type.ArrayOf(element)) is null)
        {
            return null;
        }
        // THE LIST ONLY WHERE ONE UNIT COMPILES THE LOOP. A generic copy --
        // a specialised type's member, a generic method's copy and what is
        // inside them -- is compiled by every unit that uses it and the link
        // keeps one, so all of them must be the same code; whether this unit
        // has a List of the element is this unit's accident, and an array's
        // descriptor is anybody's to lay down. LINQ's own fast paths
        // (Enumerable) name List<T> in the template, so every copy has it.
        // (Not TemplateIndex: that is every member's place in its type, set
        // for all of them, and asking for -1 turned the List walk off nearly
        // everywhere -- a foreach over a List behind an interface boxed an
        // enumerator in every pass of the compiler.)
        bool once = _thisType?.Decl is null or { Template: null, TypeParams.Count: 0 }
                 && _member is null or { LocalCopy: false }
                 && _member is not MethodDecl { TypeParams.Count: > 0 }
                 && _member?.Name.Contains('$') != true;
        if (!once
            || !_r.Types.TryGetValue(Monomorphiser.MangledName("List", new List<TypeRef> { written }), out TypeSymbol? list)
            || list.Decl is not { Template: "List" })
        {
            return (element, null, null);
        }
        MethodSymbol? get = Reachable(list, "GetEnumerator")
            .FirstOrDefault(m => m.Params.Count == 0 && !m.Static && m.Returns.Symbol is { Kind: TypeKind.Struct });
        return get is null || RefOf(get.Returns) is null ? (element, null, null) : (element, list, get.Returns);
    }

    private static List<MethodSymbol> Reachable(TypeSymbol t, string name)
    {
        List<MethodSymbol> found = new();
        Reach(t, name, found);
        return found;
    }

    /// <summary>Reachable's walk: a method of its own, not a local function closing over the list and the name.</summary>
    private static void Reach(TypeSymbol at, string name, List<MethodSymbol> found)
    {
        // ITS OWN only when it may have one by this name (TypeSymbol.
        // MayHave): a GetEnumerator, Dispose or get_Item asked of a type whose
        // members wait is not a reason to declare them all, and its base and
        // interfaces below are asked all the same.
        if (at.MayHave(name))
        {
            foreach (MethodSymbol m in at.Methods)
                if (m.Name == name) found.Add(m);
        }

        if (at.Base != null)
        {
            Reach(at.Base, name, found);
        }

        foreach (TypeSymbol face in at.Interfaces)
        {
            Reach(face, name, found);
        }
    }

    /// <summary>Whether any of these methods takes this many arguments.</summary>
    private static bool TakesArgs(List<MethodSymbol> methods, int count)
    {
        foreach (MethodSymbol m in methods)
        {
            if (m.Params.Count == count) return true;
        }
        return false;
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
        // AND FROM THE VALUE, as the generic overloads infer TEnum:
        // `Enum.IsDefined(type)` and `Enum.GetName(kind)` over an enum value
        // are IsDefined<IrType> and GetName<Kind>.
        else if (named.Name is "IsDefined" or "GetName" && call.Args.Count == 1)
        {
            _quiet++;
            Type given = CheckExpr(call.Args[0]);
            _quiet--;
            if (given.AsNonNullable().Symbol is { Kind: TypeKind.Enum } byValue)
                chosen = byValue;
        }

        if (chosen is null)
        {
            return null;
        }

        Type asEnum = Type.Plain(chosen, chosen.EnumUnderlying);
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
                    Target = new NameExpr { Name = "System", Global = true, Line = at.Line, Col = at.Col },
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
    /// <summary>A reference without its `?`; anything else as it is.</summary>
    private static Type Unmarked(Type e) => e.Nullable && e.IsReference && !e.IsPointer ? e.AsNonNullable() : e;

    /// <summary>
    /// Item <paramref name="which"/> of a tuple as THIS use wrote it: `(int,
    /// Box?)` reads a Box? where the shared shape stores a Box.
    /// </summary>
    private static Type TupleItem(Type tuple, TypeSymbol shape, int which)
        => tuple.UseArgs is { } items && items.Count == shape.Fields.Count(f => !f.Static) && which < items.Count
            && Unmarked(items[which]).Equals(shape.Fields[which].Type)
            ? items[which] : shape.Fields[which].Type;

    private TypeSymbol TupleType(IReadOnlyList<Type> elements, IReadOnlyList<string>? names = null)
    {
        // SPELLED THE WAY THE MONOMORPHISER SPELLS IT. A specialisation's name
        // writes a nested or namespaced type with the separator the name itself
        // uses, so `Corsac.Lang.ParamSymbol` is `Corsac$Lang$ParamSymbol`.
        // Naming the shape with the dots left in made a SECOND class for a
        // shape that already had one, with no element names on it -- and
        // `p.First` over a Zip of two ParamSymbols was told the tuple has no
        // such member.
        // One builder and one string for the whole name (TupleElementName).
        System.Text.StringBuilder spelling = Interned.Builder();
        spelling.Append(TypeRef.Tuple).Append('$');
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0) spelling.Append('$');
            TupleElementName(spelling, elements[i]);
        }
        string name = Interned.Return(spelling);

        if (_r.Types.TryGetValue(name, out TypeSymbol? already))
        {
            if (!_namingOnly) Remember(already, names);
            return already;
        }

        // LOOKED UP FOR A NAME ONLY (_namingOnly): a shape no source made is
        // made for the name and kept nowhere -- registered, one whose items
        // did not resolve in the scope asked was emitted as a type.
        // And the tuple namings read before anything is declared: a shape
        // whose items did not resolve there -- `(Block Block, ...)` read in no
        // scope -- names nothing, and registered it was emitted as a type.
        if (_namingOnly || _namingTuples && elements.Any(e => Unresolved(e)))
        {
            TypeSymbol transient = new() { Name = name, Kind = TypeKind.Struct, Structural = true };
            for (int i = 0; i < elements.Count; i++)
                transient.WritableFields.Add(new FieldSymbol { Name = "Item" + (i + 1), Type = Unmarked(elements[i]), Owner = transient });
            return transient;
        }

        // A STRUCT, as ValueTuple is: a copy of one is a copy, `u = t; u.Item1
        // = 5` leaves t alone, and a tuple held by another struct, an object
        // or an array is held in line. It was a class, and assigning one
        // shared it.
        TypeDecl decl = new() { Name = name, Kind = TypeKind.Struct, File = _in };
        TypeSymbol tuple = new() { Name = name, Kind = TypeKind.Struct, Decl = decl, Structural = true };

        for (int i = 0; i < elements.Count; i++)
        {
            // ITS ITEMS AS STORAGE HAS THEM: a reference's `?` is the use's
            // (Type.UseArgs), and one shape serves `(int, Box?)` and `(int, Box)`.
            tuple.WritableFields.Add(new FieldSymbol { Name = "Item" + (i + 1), Type = Unmarked(elements[i]), Owner = tuple });
        }

        // VALUETUPLE'S OWN MEMBERS, written by the code generator from the
        // items (Lowering.EmitTupleMethod): Equals of a tuple and of an object,
        // GetHashCode and ToString, CompareTo of a tuple -- and IComparable's
        // and ITuple's members, which .NET implements explicitly. The
        // interfaces are added when the library's have their slots
        // (TupleFaces).
        Type self = Type.Plain(tuple, Prim.Void);
        MethodSymbol Member(string name, Type returns, params (string Name, Type Type)[] parameters)
        {
            MethodSymbol m = new() { Name = name, Returns = returns, Owner = tuple };
            foreach ((string n, Type t) in parameters) m.WritableParams.Add(new ParamSymbol { Name = n, Type = t });
            tuple.WritableMethods.Add(m);
            return m;
        }
        Member("Equals", Type.Bool, ("other", self));
        Member("Equals", Type.Bool, ("obj", Type.Any.AsNullable()));
        Member("GetHashCode", Type.I32);
        Member("ToString", Type.String);
        Member("CompareTo", Type.I32, ("other", self));

        RegisterType(name, tuple);
        if (elements.All(e => e.Size > 0))
        {
            // NOT BEFORE THE TYPES HAVE THEIR MEMBERS: a shape is first met
            // reading tuple namings, and laying it out then laid its items'
            // structs out with no fields -- size one, every offset zero, kept
            // for good. The pass that lays out every type (Run) takes it then.
            if (_layingOut) LayOut(tuple);
        }
        else
        {
            // A SHAPE OVER A TEMPLATE'S OWN PARAMETERS -- `(T, U)` inside a
            // generic before it is copied -- holds no value ever made: a word
            // an item, so that it has offsets to name.
            int at = 0;
            foreach (FieldSymbol item in tuple.Fields)
            {
                int size = Math.Max(1, item.Type.Size);
                at = (at + size - 1) / size * size;
                item.Offset = at;
                at += size;
            }
            tuple.InstanceSize = Math.Max(1, at);
            tuple.InlineDecided = true;
            tuple.SlotsAssigned = true;
        }
        if (_tupleFacesReady) TupleFaces(tuple);
        Remember(tuple, names);
        return tuple;
    }

    /// <summary>Whether a type did not resolve anywhere in it: itself, an element, an argument, a tuple's item.</summary>
    private static bool Unresolved(Type t, int depth = 0)
        => depth > 8 || t.IsError
        || t.Element is Type e && Unresolved(e, depth + 1)
        || t.Args.Any(a => Unresolved(a, depth + 1))
        || t.Symbol is { Structural: true } shape && shape.Fields.Any(f => !f.Static && Unresolved(f.Type, depth + 1));

    /// <summary>Whether every type has its members, so a type made now can be laid out at once.</summary>
    private bool _layingOut;

    /// <summary>Types are being looked up for their names alone: none is marked used, none is asked for.</summary>
    private bool _namingOnly;

    /// <summary>Whether the library's interfaces have their slots, so a tuple shape can be given them.</summary>
    private bool _tupleFacesReady;

    /// <summary>
    /// The interfaces a ValueTuple implements, given to a shape and mapped to
    /// its members: IComparable and ITuple, which it implements explicitly.
    /// What `(IComparable)(1, "a")`, a sort over boxed tuples and `t is
    /// ITuple` reach.
    /// </summary>
    private void TupleFaces(TypeSymbol tuple)
    {
        // A SHAPE NO VALUE IS EVER MADE OF -- an item unresolved where it was
        // met, a template's own parameter, a pointer -- implements nothing.
        if (tuple.TupleFacesGiven) return;
        // object's three, in the slots every type has them in -- numbered
        // only now: a shape laid out before the numbering had all three in
        // slot zero in one unit and in their own in another.
        foreach (MethodSymbol m in tuple.Methods.Where(m => m.ExplicitInterface is null && !m.Static))
        {
            if (m.Name == "ToString" && m.Params.Count == 0) m.VtableSlot = _r.ToStringSlot;
            else if (m.Name == "GetHashCode" && m.Params.Count == 0) m.VtableSlot = _r.HashSlot;
            else if (m.Name == "Equals" && m.Params.Count == 1 && m.Params[0].Type.Prim == Prim.Any) m.VtableSlot = _r.EqualsSlot;
        }
        if (tuple.Fields.Any(f => !f.Static && (f.Type.Size <= 0 || f.Type.IsError || f.Type.ParamName is not null
                || f.Type.IsPointer || f.Type.Function is not null || f.Type.Prim == Prim.Void && f.Type.Symbol is null && !f.Type.IsArray))) return;
        tuple.TupleFacesGiven = true;
        List<TypeSymbol> faces = new();
        foreach (string plain in new[] { "IComparable", "System.Runtime.CompilerServices.ITuple" })
        {
            if (_r.Types.TryGetValue(plain, out TypeSymbol? face) && face.Kind == TypeKind.Interface) faces.Add(face);
        }
        // NOT IEquatable OR IComparable OF ITSELF: a specialisation of those
        // exists only in a unit whose source names it, and a shape is one
        // type in every unit -- given them in one and not another, the link
        // refused the pair. Its typed Equals and CompareTo are public members.
        foreach (TypeSymbol face in faces)
        {
            if (tuple.Interfaces.Contains(face)) continue;
            tuple.WritableInterfaces.Add(face);
            bool explicitly = face.Name is "IComparable" || face.Name.EndsWith("ITuple", StringComparison.Ordinal);
            foreach (MethodSymbol want in face.Methods.Where(m => !m.Static && m.TypeParams.Count == 0))
            {
                MethodSymbol? impl = explicitly ? null
                    : tuple.Methods.FirstOrDefault(m => m.ExplicitInterface is null && m.Name == want.Name && MethodSignatures.Implements(m, want));
                if (impl is null)
                {
                    impl = new MethodSymbol
                    {
                        Name = ExplicitName(face) + "." + want.Name, ExplicitInterface = ExplicitName(face), ExplicitMember = want.Name,
                        Returns = want.Returns, Owner = tuple,
                    };
                    foreach (ParamSymbol p in want.Params) impl.WritableParams.Add(new ParamSymbol { Name = p.Name, Type = p.Type });
                    tuple.WritableMethods.Add(impl);
                }
                if (want.VtableSlot >= 0) tuple.WritableInterfaceImplementations[want.VtableSlot] = impl;
            }
        }
    }

    /// <summary>
    /// How an element type is written in its tuple's class name: as C# writes
    /// it, `byte[]`, `int?`, `byte*`, the way a specialisation's name writes an
    /// argument.
    ///
    /// EVERY TYPE A DIFFERENT NAME. Arrays, pointers and nullable values once
    /// all fell through to one word, so `(string, byte[])` and
    /// `(string, int[])` were a single class -- whose second field had the
    /// type of whichever shape was made first. Indexing it read bytes out of an
    /// int array, and two units that met the shapes in a different order laid
    /// the class out differently and could not be linked.
    /// </summary>
    /// <summary>An element's part of a tuple shape's name, written onto the end of `b`.</summary>
    private static void TupleElementName(System.Text.StringBuilder b, Type e)
    {
        if (e.IsPointer)
        {
            TupleElementName(b, e.Pointee!);
            b.Append('*');
            return;
        }

        if (e.IsArray)
        {
            TupleElementName(b, e.Element!);
            b.Append('[').Append(',', e.ArrayRank - 1).Append(']');
            return;
        }

        if (e.IsNullableValue)
        {
            TupleElementName(b, e.Underlying);
            b.Append('?');
            return;
        }

        int start = b.Length;
        b.Append(NameOf(e) ?? e.ParamName ?? e.Prim.ToString());
        Monomorphiser.Mangle(b, start);
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
            shape.WritableTupleNamings.Add(names.ToArray());
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

    /// <summary>
    /// AN ARRAY BECOMING ONE OF ITS INTERFACES, wherever it does -- an
    /// assignment, an argument, an arm of a conditional or of `??`, a cast:
    /// an IList&lt;T&gt; or ICollection&lt;T&gt; is .NET's SZArrayHelper over it,
    /// made by rewriting the array to `SZArrayHelper.Of&lt;X&gt;(array)`; a
    /// read-only sequence interface is the view the checker writes. One
    /// place, so an arm cannot be given the one and forget the other --
    /// an array arm of a conditional typed IList&lt;int&gt; was handed over raw,
    /// and GetEnumerator was called through a slot it does not have.
    /// </summary>
    private bool ArrayBecomes(Expr at, Type from, Type to)
    {
        if (!from.IsArray || to.IsArray)
        {
            return false;
        }
        if (ListFace(from, to) is Type listElement && !_r.Rewrites.ContainsKey(at) && RefOf(listElement) is TypeRef listRef)
        {
            MemberExpr maker = new()
            {
                Target = new NameExpr { Name = "SZArrayHelper", Line = at.Line, Col = at.Col },
                Name = "Of", Line = at.Line, Col = at.Col,
            };
            maker.WritableTypeArgs.Add(listRef);
            CallExpr helper = new() { Target = maker, Line = at.Line, Col = at.Col, File = at.File };
            helper.Args.Add(at);
            helper.WritableArgNames.Add(null);
            _r.Rewrites[at] = helper;
            CheckExpr(helper);
            return true;
        }
        if (ArrayFace(from, to) is { } through && _r.Types.TryGetValue(TypeKey(through), out TypeSymbol? face))
        {
            _r.Views[at] = ArrayView(from.Element!, face);
            return true;
        }
        return false;
    }

    /// <summary>An array operand of `??` going to one of its interfaces: the view that answers them.</summary>
    private void CoalesceView(Expr operand, Type from, Type to)
    {
        if (ArrayBecomes(operand, from, to))
        {
        }
    }

    /// <summary>
    /// A method on a compiler-made class for every slot of these interfaces
    /// and of every interface they extend, the generic ones first. One whose
    /// name and arity a method already has takes the explicit form --
    /// `IEnumerator.get_Current` beside IEnumerator&lt;T&gt;'s get_Current --
    /// as a class written in C# would implement it (Lowering.ArrayViewMethod).
    /// </summary>
    internal static void ImplementSlots(TypeSymbol made, List<TypeSymbol> faces)
    {
        List<TypeSymbol> all = new();
        foreach (TypeSymbol face in faces)
            foreach (TypeSymbol one in new[] { face }.Concat(AllInterfaces(face)))
                if (!all.Contains(one)) all.Add(one);
        foreach (TypeSymbol face in all)
        {
            foreach (MethodSymbol want in face.Methods)
            {
                if (made.Methods.Any(had => had.VtableSlot == want.VtableSlot)) continue;
                bool clash = made.Methods.Any(had => had.Name == want.Name && had.Params.Count == want.Params.Count);
                MethodSymbol method = new()
                {
                    Name = clash ? ExplicitName(face) + "." + want.Name : want.Name,
                    ExplicitInterface = clash ? ExplicitName(face) : null,
                    ExplicitMember = clash ? want.Name : null,
                    Returns = want.Returns, Owner = made, VtableSlot = want.VtableSlot,
                };
                foreach (ParamSymbol p in want.Params)
                {
                    method.WritableParams.Add(new ParamSymbol { Name = p.Name, Type = p.Type });
                }
                made.WritableMethods.Add(method);
            }
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
            view.WritableInterfaces.Add(also);
        }

        view.InstanceSize = Target.Current.ObjectHeaderBytes + Target.Current.WordSize;
        view.InlineDecided = true;                    // laid out here, nothing in line
        view.WritableFields.Add(new FieldSymbol
        {
            Name = "items", Type = Type.ArrayOf(element), Owner = view,
            Offset = Target.Current.ObjectHeaderBytes,
        });

        // THE INTERFACES' OWN SLOTS, so a caller that has only one of them
        // reaches these without knowing what it is holding.
        ImplementSlots(view, view.Interfaces.ToList());

        RegisterType(name, view);
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

        walker.WritableInterfaces.Add(face);
        walker.InstanceSize = header + word + 4;
        walker.InlineDecided = true;                    // laid out here, nothing in line
        walker.WritableFields.Add(new FieldSymbol
        {
            Name = "items", Type = Type.ArrayOf(element), Owner = walker, Offset = header,
        });
        walker.WritableFields.Add(new FieldSymbol
        {
            Name = "at", Type = Type.I32, Owner = walker, Offset = header + word,
        });

        // EVERY METHOD OF THE INTERFACE AND OF THOSE IT EXTENDS: IEnumerator<T>
        // is an IDisposable and an IEnumerator, as .NET's is.
        ImplementSlots(walker, new List<TypeSymbol> { face });

        RegisterType(name, walker);
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
    /// <summary>
    /// The element an IList&lt;X&gt; or ICollection&lt;X&gt; an array is being
    /// converted to holds, or null: X itself where the array's element is X,
    /// or a reference type the element converts to -- an array of Dog is an
    /// IList&lt;Animal&gt;, as array covariance has it.
    /// </summary>
    private Type? ListFace(Type from, Type to)
    {
        if (!from.IsArray || from.Element is not Type element
            || to.AsNonNullable().Symbol is not { Kind: TypeKind.Interface, Decl: { Template: "IList" or "ICollection", TemplateArgs.Count: 1 } made })
        {
            return null;
        }
        Type wanted = Resolve(made.TemplateArgs[0], _thisType);
        if (wanted.Equals(element) || MethodSignatures.SameType(wanted, element))
        {
            return wanted;
        }
        return element.IsReference && wanted.IsReference && ReferenceConvertible(element, wanted) ? wanted : null;
    }

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
        // THE NON-GENERIC IEnumerable, which every array is too: the view made
        // for IEnumerable<T> of its element answers it, IEnumerable<T>
        // extending it. `objects.OfType<string>()` is called on exactly this.
        if (face.Decl is { Template: null, TypeParams.Count: 0 } && Bare(face.Name) == "IEnumerable"
            && to.Args.Count == 0 && RefOf(element) is TypeRef each
            && _r.Types.TryGetValue(Monomorphiser.MangledName("IEnumerable", new List<TypeRef> { each }),
                                    out TypeSymbol? generic)
            && generic.Decl is { Template: not null } sequence)
        {
            return sequence;
        }

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

        // THE ELEMENT, its `?` aside: a `string[]` is the IEnumerable<string?>
        // a parameter declares, the annotation being no type.
        Type wantedElement = Resolve(made.TemplateArgs[0], _thisType);
        return wantedElement.Equals(element) || MethodSignatures.SameType(wantedElement, element) ? made : null;
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
    /// <summary>
    /// A written type argument of a template's base, with the template's own
    /// parameters put in: `IGrouping&lt;K, E&gt;` in `ILookup&lt;K, E&gt; :
    /// IEnumerable&lt;IGrouping&lt;K, E&gt;&gt;` is an IGrouping&lt;char, string&gt;
    /// for an ILookup&lt;char, string&gt; -- the specialisation when one exists,
    /// the template with its arguments beside it when it does not yet. Null
    /// when some part of it names nothing.
    /// </summary>
    private Type? Opened(TypeRef a, Dictionary<string, Type> mine, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        Type? made;
        if (a.Args.Count == 0 && mine.TryGetValue(a.Name, out Type? bound))
        {
            made = bound;
        }
        else if (a.Args.Count == 0)
        {
            // A CLOSED ONE -- `IComparable<string>` -- is what it names. Asked
            // quietly: a name that means nothing here is no match, not an error
            // in the code that asked.
            _quiet++;
            made = Resolve(new TypeRef { Name = a.Name, Line = a.Line, Col = a.Col }, _thisType);
            _quiet--;
        }
        else
        {
            List<Type> args = new();
            foreach (TypeRef inner in a.Args)
            {
                if (Opened(inner, mine, depth + 1) is not { } one) return null;
                args.Add(one);
            }
            if (!FindType(Arity(a.Name, args.Count), out TypeSymbol? generic) || generic?.Decl is not { } decl
                || decl.TypeParams.Count != args.Count)
            {
                return null;
            }
            made = Close(WithArgs(Type.Plain(generic, Prim.Void), args), new Dictionary<string, Type>());
        }

        if (made is null || made.IsError) return null;
        if (a.PointerDepth > 0) return null;
        if (a.ArrayRank > 0) made = Type.ArrayOf(made, a.ArrayRank);
        return a.Nullable ? made.AsNullable() : made;
    }


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
                if (Opened(a, mine, depth) is { } bound)
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

            if (!FindBase(b, out TypeSymbol? holder)
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
            || !varies.Any(v => v != Variance.None))
        {
            return false;
        }

        List<Type>? given = null;

        // AN UNMADE SPECIALISATION varies as the one it will be: an
        // `IEnumerable<string>` inferred this round, before any copy spells
        // it, is an IEnumerable<object> as surely as the made one is -- and
        // `Seq(list)` beside Seq(IEnumerable<object>) and Seq<T>(IEnumerable<T>)
        // is the generic one only if that is known.
        if (Unmade(from.AsNonNullable()) && from.Symbol?.Decl is { } openFrom)
        {
            if ((openFrom.Template ?? Bare(from.Symbol.Name)) == faceName && from.Args.Count == wantedArguments.Count)
            {
                given = from.Args.ToList();
            }
            else
            {
                Dictionary<string, Type> mine = new(StringComparer.Ordinal);
                for (int i = 0; i < from.Args.Count; i++)
                {
                    mine[openFrom.TypeParams[i].Name] = from.Args[i];
                }
                given = OpenAs(openFrom, faceName, wantedArguments.Count, mine, 0);
            }
        }
        else if (from.IsArray && from.Element is Type arrayElement && wantedArguments.Count == 1
                 && faceName is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection")
        {
            // AN ARRAY IS A SEQUENCE OF ITS ELEMENT (ArrayFace), and those
            // three are declared `out T`: a string[] is an IEnumerable<object>.
            given = new List<Type> { arrayElement };
        }
        else if (Instance(from.Symbol, faceName, wantedArguments.Count) is { } implemented)
        {
            given = implemented.TemplateArgs.Select(a => Resolve(a, _thisType)).ToList();
        }

        if (given is null)
        {
            return false;
        }

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
                             && ReferenceConvertible(given[i], wantedArguments[i]),
                Variance.In => Carried(given[i]) && Carried(wantedArguments[i])
                            && ReferenceConvertible(wantedArguments[i], given[i]),
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
    /// An implicit REFERENCE conversion, which is all variance carries (C#
    /// 18.2.3.3): not `object` to string, which a machine word allows and C#
    /// does not -- with it, an IEnumerable&lt;object&gt; was an
    /// IEnumerable&lt;string&gt;, and no overload over the two could be told
    /// better than the other. Through variance again for nested arguments.
    /// </summary>
    private bool ReferenceConvertible(Type from, Type to)
        => (from.Prim != Prim.Any || to.Prim == Prim.Any) && (Convertible(from, to) || Variant(from, to));

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
            || lam.Body is null && lam.BlockBody is null)
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
            // CLOSED, not merely substituted: the second parameter of
            // GroupBy's resultSelector is `IEnumerable<T>`, and a bare
            // Substitute only replaces a T standing alone or as an array's
            // element, so `ps.First()` was asked of an open IEnumerable<T>.
            Type filled = Close(Substitute(p.Type, applied), bound);

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

        Type? produced;
        if (lam.Body is not null)
        {
            produced = CheckExpr(lam.Body);
        }
        else
        {
            // A BLOCK BODY RESULTS IN WHAT IT RETURNS (C# 12.6.3.13, the
            // inferred return type): the best common type of its return
            // expressions. `Select(line => { ...; return (Version: v, Path:
            // p); })` is a sequence of that tuple, not of an unbound R.
            List<Type>? wasInferred = _inferredReturns;
            int wasSlot = _nextSlot;
            _inferredReturns = new List<Type>();
            try
            {
                CheckBlock(lam.BlockBody!);
                produced = BestCommonType(_inferredReturns);
            }
            finally
            {
                _inferredReturns = wasInferred;
                _nextSlot = wasSlot;
            }
        }

        PopScope();
        _quiet--;
        return produced is null || produced.IsError ? null : produced;
    }

    /// <summary>What a block lambda's returns are being gathered into, while one is inferred.</summary>
    private List<Type>? _inferredReturns;

    /// <summary>
    /// The best common type of a set of expressions (C# 12.6.3.16): the one
    /// every other converts to. A null among references makes it nullable;
    /// none that all convert to, or no value at all, is no answer.
    /// </summary>
    private Type? BestCommonType(List<Type> types)
    {
        List<Type> values = types.Where(t => !t.IsError && t.Prim != Prim.NullLiteral && !t.IsVoid).ToList();
        if (values.Count == 0) return null;
        bool sawNull = types.Any(t => t.Prim == Prim.NullLiteral);
        foreach (Type candidate in values)
        {
            // IMPLICITLY, as C# counts it: an object is no string, however a
            // machine word may be read. And a null among values of a value
            // type has no best type (CS0826) -- `new[] { 1, null }` is not an
            // int?[] to C#.
            if (values.All(other => other.Equals(candidate)
                                    || (other.Prim != Prim.Any || candidate.Prim == Prim.Any) && Convertible(other, candidate)))
            {
                if (!sawNull) return candidate;
                if (candidate.IsReference || candidate.Prim == Prim.Any) return candidate.AsNullable();
                if (candidate.IsNullableValue) return candidate;
                return null;
            }
        }
        return null;
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
            // `T?` OVER `where T : struct` takes T from inside the cell: a
            // Path? argument and a plain Path both make T Path (C# 12.6.3.9's
            // lower-bound inference through Nullable<T>), and a bare null says
            // nothing about T at all.
            if (want.Nullable && want.StructParam)
            {
                if (got.Prim == Prim.NullLiteral) return true;
                if (got.IsNullableValue) got = got.AsNonNullable();
            }

            if (!bound.TryGetValue(name, out Type? already))
            {
                bound[name] = got;
                return true;
            }

            // The SAME T twice has to mean the same thing both times, so
            // `Pick(1, "two")` finds no overload rather than quietly taking one
            // of the two and mistyping the result.
            //
            // AND IT IS THE ONE THE OTHERS CONVERT TO (C# 12.6.3.12), whichever
            // came first: `Many(dog, animal)` is Many<Animal>, and
            // `Pick("s", o)` over an object is Pick<object> -- `object` reaching
            // a string only as a machine word, which is no conversion to C#.
            // Through VARIANCE too, as C#'s inference has it: a Func<string>
            // and a Func<object> make T the Func<object> the other converts
            // to.
            if (got.IsError || already.IsError) return true;
            if ((got.Prim != Prim.Any || already.Prim == Prim.Any) && (Convertible(got, already) || Variant(got, already))) return true;
            if ((already.Prim != Prim.Any || got.Prim == Prim.Any) && (Convertible(already, got) || Variant(already, got)))
            {
                bound[name] = got;
                return true;
            }
            return false;
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
            // `T?` OVER `where T : struct` IS Nullable<T>, so the '?' stays on
            // what T is bound to. Over any other T it is an annotation and the
            // bound type is the whole answer (Monomorphiser.Sub, the same rule
            // for the copy this call will reach).
            if (t.Nullable && t.StructParam && !actual.IsError && actual.Prim != Prim.Any && actual.ParamName is null)
            {
                return actual.AsNullable();
            }
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
    /// <summary>Whether what is being bound is a canonical copy: the one
    /// specialisation of a type or method that every word argument shares.</summary>
    /// Exactly: every argument __canon, not merely one somewhere --
    /// `Foo<List<T>>` made inside a canonical body is `Foo$List$__canon`, a
    /// specialisation over a canonical type and no canonical copy itself.
    private bool InCanonicalCopy
        => _thisType?.Decl is { Specialised: true, TemplateArgs.Count: > 0 } type
           && type.TemplateArgs.All(a => a.Name == Monomorphiser.CanonName && a.Args.Count == 0)
        || _member?.Name is string name && CanonicalMethodName(name);

    /// `FromResult$__canon$3`: the method's name, then only __canon for each
    /// argument, then the member index its copies are told apart by.
    private static bool CanonicalMethodName(string name)
    {
        string[] parts = name.Split('$');
        if (parts.Length < 2) return false;
        int last = parts.Length - 1;
        if (parts[last].Length > 0 && parts[last].All(char.IsAsciiDigit)) last--;
        if (last < 1) return false;
        for (int i = 1; i <= last; i++)
            if (parts[i] != Monomorphiser.CanonName) return false;
        return true;
    }

    private Type Close(Type t, Dictionary<string, Type>? bound)
    {
        Type made = Substitute(t, bound);

        // A TUPLE OVER TYPE PARAMETERS IS REMADE OVER WHAT THEY ARE BOUND TO.
        // A tuple is a shape of its element types, not a template with
        // arguments, so `(T First, U Second)` -- Zip's element -- stayed the
        // shape over T and U, and a lambda over the pairs could not be typed.
        if (bound is not null && made.Symbol is { } shape && !made.IsArray
            && shape.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
            && shape.Fields.Any(f => !f.Static && bound.Keys.Any(k => Mentions(f.Type, k))))
        {
            List<Type> elements = shape.Fields.Where(f => !f.Static).Select(f => Close(f.Type, bound)).ToList();
            Type remade = new() { Prim = Prim.Void, Symbol = TupleType(elements, shape.TupleNames), UseArgs = elements };
            return made.Nullable ? remade.AsNullable() : remade;
        }
        if (bound is not null && made.IsArray && made.Element is Type inner && Close(inner, bound) is { } closedInner
            && !ReferenceEquals(closedInner.Symbol, inner.Symbol))
        {
            Type array = Type.ArrayOf(closedInner, 1);
            return made.Nullable ? array.AsNullable() : array;
        }

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

        // BY ITS KEY, which keeps a nested template's outer: List<T>.Enumerator
        // closed over a tuple is List$Enumerator$ValueTuple_int_string, and
        // its bare name found nothing and left the template standing.
        //
        // KEEPING ITS `?`: `IComparer<K>? comparer` closed over string is an
        // IComparer$string that may be null, and dropping the annotation had
        // every null a caller passed for it refused.
        if (_r.Types.TryGetValue(Monomorphiser.MangledName(Bare(template.Key), SpecialisationKeys(args, closed)), out TypeSymbol? real))
        {
            Type specialised = new()
            {
                Prim = real.Kind == TypeKind.Enum ? Prim.I32 : Prim.Void,
                Symbol = real, UseArgs = closed,
            };
            return made.Nullable ? specialised.AsNullable() : specialised;
        }

        // INSIDE A CANONICAL COPY, A WORD IS __canon. `ValueTask<R>.AsTask`
        // returns `_task ?? Task.FromResult(_result)`, and in the copy every
        // other library shares, R binds as the word it is -- which spells
        // `object`, and `Task$object` is a type nothing in that body makes.
        // What the body means is the shared copy: a template closed over
        // nothing but words, there, is the template's canonical copy, the
        // `Task$__canon` its own return type already names.
        if (InCanonicalCopy && closed.All(a => a.Prim == Prim.Any && a.Symbol is null && a.Args.Count == 0)
            && _r.Types.TryGetValue(Monomorphiser.CanonNameOf(Bare(template.Key), closed.Count), out TypeSymbol? shared))
        {
            Type canonical = new() { Prim = Prim.Void, Symbol = shared, UseArgs = closed };
            return made.Nullable ? canonical.AsNullable() : canonical;
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

    private Dictionary<string, Type>? Received(Type target, TypeSymbol owner)
    {
        List<TypeParam>? parameters = owner.Decl?.TypeParams;

        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        // A MEMBER OF A BASE: the receiver's arguments are its OWN template's,
        // and reach the declaring type through the base list. An ILookup<K, E>
        // walked by foreach has the GetEnumerator of IEnumerable<IGrouping<K,
        // E>>, whose T is not ILookup's K -- matching by count alone bound the
        // wrong parameters, or (as here) none and left the element a bare T.
        Type on = target.AsNonNullable();
        if (on.Symbol is { } held && !ReferenceEquals(held, owner)
            && held.Decl is { TypeParams.Count: > 0 } template && on.Args.Count == template.TypeParams.Count)
        {
            Dictionary<string, Type> mine = new(StringComparer.Ordinal);
            for (int i = 0; i < template.TypeParams.Count; i++)
            {
                mine[template.TypeParams[i].Name] = on.Args[i];
            }
            if (OpenAs(template, Bare(owner.Name), parameters.Count, mine, 0) is not { } through)
            {
                return null;
            }
            Dictionary<string, Type> theirs = new(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                theirs[parameters[i].Name] = through[i];
            }
            return theirs;
        }

        if (target.Args.Count != parameters.Count)
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
            if (collection.Adds.Count > 0) made.Body.WritableAdds.AddRange(collection.Adds);
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
                    Target = new NameExpr { Name = "System", Global = true, Line = at.Line, Col = at.Col },
                    Name = "CollectionExpressionBuilder", Line = at.Line, Col = at.Col,
                },
                Name = method, Line = at.Line, Col = at.Col,
            };
            if (into is not null) step.WritableTypeArgs.Add(into);
            step.WritableTypeArgs.Add(elementRef);
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

            for (int which = 0; which < tuple.Fields.Count; which++)
            {
                if (RefOf(TupleItem(bare, tuple, which)) is not TypeRef element)
                {
                    return null;
                }
                structural.Arguments.Add(element);
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
            Arguments = typeArguments,
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
    /// first of int, uint, long and ulong that its value fits.
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

        // A NEGATIVE VALUE IS A WRAPPED ULONG unless the text says minus:
        // the parser keeps 64 bits, so `0xFFFFFFFFFFFFFFFF` arrives as -1, and
        // only the int.MinValue and long.MinValue literals a unary minus was
        // folded into are written with their sign.
        bool negative = l.Text.StartsWith('-');
        bool wrapped = l.IntValue < 0 && !negative;

        if (unsigned)
        {
            return !wide && !wrapped && l.IntValue <= uint.MaxValue ? Type.U32 : Type.U64;
        }

        if (wide)
        {
            return wrapped ? Type.U64 : Type.I64;
        }

        // THE FIRST TYPE THAT CAN HOLD IT, which is C#'s rule (6.4.5.3) for
        // every unsuffixed literal, decimal or hex: int, uint, long, ulong.
        // `2147483648` is a uint, as `0x80000000` is.
        if (wrapped)
        {
            return Type.U64;
        }
        if (l.IntValue is >= int.MinValue and <= int.MaxValue)
        {
            return Type.I32;
        }
        if (!negative && l.IntValue <= uint.MaxValue)
        {
            return Type.U32;
        }
        return Type.I64;
    }

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
                              ?? owner.FindBackingField(init.Name);

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

                MethodSymbol? getter = owner.FindMethods("get_", init.Name)
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
                                 ? owner.FindMethods("set_", init.Name).FirstOrDefault()
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
                argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null, Collection: false } || HoldsLambda(argument)
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
                if (add.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null, Collection: false } || HoldsLambda(add.Args[i]))
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
                Type key = ThroughUnmade(type, setter, setter.Params[i].Type);
                index[i] = Settle(one.Args[i], key, index[i]);
                CheckAssignable(index[i], key, one.Args[i], "index");
            }

            Type stored = ThroughUnmade(type, setter, setter.Params[index.Count].Type);
            value = Settle(one.Value, stored, value);
            CheckAssignable(value, stored, one.Value, "the value");
            _r.InitIndexer[one] = setter;
        }
    }

    /// <summary>The member a call is checking as its callee, for CheckMember.</summary>
    private MemberExpr? _callee;
    // The argument count of the call _callee is the target of; -1 when unknown.
    private int _calleeArgs = -1;

    // THE NAME BEING CALLED, while CheckCall checks its target: C# looks a
    // name up for an invocation among the members that can be invoked
    // (§12.8.10.2, "if the member is invoked"), passing over a field or a
    // property that is no delegate to the method further out. `Fields(path)`
    // in a nested class with a field Fields calls the outer class's method.
    private NameExpr? _invokedName;

    /// <summary>Whether `n` is being called and a member of type `t` could not be: no delegate.</summary>
    private bool NotInvocable(NameExpr n, Type t)
        => ReferenceEquals(n, _invokedName) && !t.IsError
           && !(t.AsNonNullable().Symbol is TypeSymbol held && held.FindMethods("Invoke").Any());

    /// <summary>Nullable&lt;T&gt; methods CheckMember found being called, with the cell's type.</summary>
    private readonly Dictionary<MemberExpr, Type> _cellMethods = new(ReferenceEqualityComparer.Instance);

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
            PatternNullTest = b.PatternNullTest, PatternConstant = b.PatternConstant,
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
                    || at.FindBackingField(n.Name) is { Static: true }
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

    /// <summary>
    /// Whether a bare name is a const where it is written: on this type or a
    /// base, or OUTWARDS through the types it is nested in, as an unqualified
    /// name resolves. A switch arm asked only this type, so in a nested class
    /// `HeldClass => ...` naming the outer class's const read as a type
    /// pattern -- 'HeldClass' is not a known type -- and corc stopped
    /// compiling its own escape engine (test 1317).
    /// </summary>
    private bool NamesConstant(string name)
    {
        if (FindConstant(_thisType, name) is not null || FindText(_thisType, name) is not null) return true;
        for (string? outerKey = OuterKey(_thisType ?? _lexicalType ?? _scope); outerKey is not null; outerKey = Enclosing(outerKey))
        {
            if (_r.Types.TryGetValue(outerKey, out TypeSymbol? outer)
                && (FindConstant(outer, name) is not null || FindText(outer, name) is not null))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The type this one is written inside, if any.</summary>
    private TypeSymbol? Outer(TypeSymbol t)
        => OuterKey(t) is { } key && _r.Types.TryGetValue(key, out TypeSymbol? outer) ? outer : null;

    /// <summary>
    /// The key of the type `t` is written inside, or null at the top level.
    ///
    /// A SPECIALISED NESTED TYPE IS NOT NESTED BY ITS KEY: the monomorphiser
    /// gives every copy a unique top-level name, so `Chunks<int>.Enumerator`
    /// has no dot to cut, and its members naming the outer class's private
    /// constants by their bare names found nothing. Its template path still
    /// says where it was written, and its leading type arguments are the
    /// outer class's (a nested type takes its outer's parameters first), so
    /// the outer is the copy of that template over those arguments.
    /// </summary>
    private string? OuterKey(TypeSymbol? t)
    {
        if (t?.Decl is not { Specialised: true, Template: string template } made
            || Enclosing(template) is not string outerTemplate)
        {
            return Enclosing(t?.Key ?? "");
        }
        if (_specialisedOuter.TryGetValue(t, out string? known)) return known;
        string? found = _r.Types.ContainsKey(outerTemplate) ? outerTemplate
            : CopyOver(outerTemplate, made, t, inner: false)?.Key;
        _specialisedOuter[t] = found;
        return found;
    }

    /// <summary>
    /// The copy of `template` whose type arguments and those of `made` (a copy
    /// itself, read in `context`) agree as far as the shorter list goes: an
    /// outer class's copy for a nested one's leading arguments, or (`inner`)
    /// a nested type's copy for its outer's arguments. Null when none was made.
    /// </summary>
    private TypeSymbol? CopyOver(string template, TypeDecl made, TypeSymbol context, bool inner)
    {
        foreach (TypeSymbol candidate in _r.Types.Values)
        {
            if (candidate.Decl is not { Template: string candidateTemplate } other
                || candidateTemplate != template
                || (inner ? other.TemplateArgs.Count < made.TemplateArgs.Count
                          : other.TemplateArgs.Count > made.TemplateArgs.Count)) continue;
            int count = Math.Min(other.TemplateArgs.Count, made.TemplateArgs.Count);
            bool same = true;
            for (int i = 0; i < count && same; i++)
            {
                same = MethodSignatures.SameType(Resolve(other.TemplateArgs[i], candidate), Resolve(made.TemplateArgs[i], context));
            }
            if (same) return candidate;
        }
        return null;
    }

    private readonly Dictionary<TypeSymbol, string?> _specialisedOuter = new(ReferenceEqualityComparer.Instance);

    private Type CheckExpr(Expr e)
    {
        Type t = CheckExprCore(e);
        _r.ExprType[e] = t;
        if (_inBodies) ForceBody(t);
        // An event, read from outside the type that declares it, is refused
        // here, wherever the read is: a call of it, a ?.Invoke, an assignment
        // to it, or a plain read (EventFromOutside).
        if (e is MemberExpr or NameExpr && _r.Resolved.TryGetValue(e, out Sym? read))
        {
            FieldSymbol? field = read switch
            {
                FieldSym f => f.Field,
                CapturedFieldSym f => f.Field,
                _ => null,
            };
            if (field is { IsEvent: true })
            {
                EventFromOutside(e, field);
            }
        }
        return t;
    }

    private Type CheckExprCore(Expr e)
    {
        // Something may suspend in this unit (CheckAsyncSafety): an await, or an async lambda's body.
        if (e is AwaitExpr or LambdaExpr { Async: true }) _mayAwait = true;
        switch (e)
        {
            // A TUPLE, WRITTEN OUT. Its type is its elements' types, which is
            // why this cannot be resolved before anything is checked -- and the
            // construction it becomes is an object initialiser, so no
            // constructor has to exist for a class nobody declared.
            case TupleExpr tup:
            {
                // THE SHAPE SOMETHING IS WAITING FOR WINS, element by element.
                //
                // `(int Reg, long Off) Named() { return (3, 40); }` writes two
                // ints and returns a tuple whose second element is a long, and
                // C# converts each element on the way. Without this the literal
                // is its own shape and the return is a type error about a
                // conversion nobody would think to write.
                //
                // AND EACH ELEMENT IS CHECKED AGAINST ITS OWN TARGET, not the
                // tuple's: a `null`, a bare `default`, a `new()` or a lambda in
                // the literal is typed by the element it becomes. Checked with
                // the whole tuple as its target, a `new()` inside was told to be
                // the tuple.
                TypeSymbol? want = _wanted?.Symbol is TypeSymbol waiting
                                && waiting.Name.StartsWith(TypeRef.Tuple + "$", StringComparison.Ordinal)
                                && waiting.Fields.Count == tup.Items.Count
                                 ? waiting : null;
                Type? outerWanted = _wanted;
                List<Type> elements = new();

                try
                {
                    for (int i = 0; i < tup.Items.Count; i++)
                    {
                        Type? target = want?.Fields[i].Type;

                        _wanted = target;
                        elements.Add(tup.Items[i] is LambdaExpr lambda && target is not null
                            ? CheckLambda(lambda, target)
                            : CheckExpr(tup.Items[i]));
                    }
                }
                finally
                {
                    _wanted = outerWanted;
                }

                if (elements.Any(t => t.IsError))
                {
                    return Type.Error;
                }

                if (want is not null)
                {
                    bool fits = true;

                    // Each element as it converts on its own: a constant by the
                    // constant conversion (Fits), as C# converts a literal's.
                    for (int i = 0; i < elements.Count; i++)
                    {
                        fits &= Fits(elements[i], want.Fields[i].Type, tup.Items[i]);
                    }

                    if (fits)
                    {
                        elements = want.Fields.Select(f => f.Type).ToList();
                    }
                }

                // NOTHING GAVE AN ELEMENT A TYPE: `var pair = (1, null)`. C#
                // refuses it, and so does this -- a tuple class whose field is
                // the type of null is a shape no program can name.
                for (int i = 0; i < elements.Count; i++)
                {
                    if (elements[i].Prim == Prim.NullLiteral)
                    {
                        Error(tup.Items[i], $"tuple element {i + 1} is null, and nothing here says what type it should be");
                        return Type.Error;
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
                    made.Body.WritableInits.Add(one);
                }

                Type shaped = new() { Prim = Prim.Void, Symbol = shape, Names = tup.Names.ToArray(), UseArgs = elements };

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
                // INSIDE A LAMBDA OR A LOCAL FUNCTION `this` is still the
                // instance the method was called on, which the closure holds in
                // its hidden field: not the closure. `new Needs(this)` in a local
                // function handed the closure where a Needs wanted an Escape.
                if (_capturedThisType is not null && _capturedThisField is not null)
                {
                    _r.Resolved[e] = new FieldSym(_capturedThisField);
                    return Type.Plain(_capturedThisType, Prim.Void);
                }
                if (_thisType is null || _method is { Static: true })
                {
                    Error(e, "'this' is not available in a static method");
                    return Type.Error;
                }
                return Type.Plain(_thisType, Prim.Void);

            case NameExpr n:
                return CheckName(n);

            case MemberExpr m:
                // `Length`, OR `Count` when that is what the type has.
                if (m.Else is not null)
                {
                    Type on = Peek(m.Target).AsNonNullable();
                    if (!Countable(on, m.Name) && Countable(on, m.Else))
                    {
                        m.Name = m.Else;
                    }
                }
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

            // Wanted as an object, it is its natural type (C# 10).
            case LambdaExpr lam when _wanted is { } objectWanted && NaturalTarget(objectWanted):
                return CheckLambda(lam, objectWanted);

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

            // The statement's names belong to the scope the expression is in,
            // as a pattern's own bindings do.
            // A POSITIONAL PATTERN OVER AN OBJECT IS ASKED OF ITuple (C#
            // 11.2.5): with no Deconstruct to call, `o is (int, string)`
            // matches what is an ITuple of that Length whose items match, as
            // every boxed ValueTuple is.
            case SequenceExpr { Effect: DeconstructStmt { Names.Count: > 0 } apart, Value: LiteralExpr { Kind: Lit.Bool, IntValue: 1 } } seq
                when Peek(apart.Value) is { } over && (over.Prim == Prim.Any || over.Symbol?.Name.EndsWith("ITuple", StringComparison.Ordinal) == true)
                     && !over.IsError && _r.Types.ContainsKey("System.Runtime.CompilerServices.ITuple"):
            {
                Node at = seq;
                NameExpr Named(string name) => new() { Name = name, Line = at.Line, Col = at.Col };
                string tuple = $"$tuple${_iterations++}";
                Expr matched = new IsExpr
                {
                    Operand = apart.Value,
                    Type = new TypeRef { Name = "System.Runtime.CompilerServices.ITuple", Line = at.Line, Col = at.Col },
                    Binding = tuple, Line = at.Line, Col = at.Col,
                };
                Expr parts = new LiteralExpr { Kind = Lit.Bool, Text = "true", IntValue = 1, Line = at.Line, Col = at.Col };
                for (int i = apart.Names.Count - 1; i >= 0; i--)
                {
                    IndexExpr item = new() { Target = Named(tuple), Line = at.Line, Col = at.Col };
                    item.Args.Add(new LiteralExpr { Kind = Lit.Int, Text = i.ToString(), IntValue = i, Line = at.Line, Col = at.Col });
                    parts = new SequenceExpr
                    {
                        Effect = new LocalDecl { Name = apart.Names[i].Name, Init = item, Line = at.Line, Col = at.Col },
                        Value = parts, Line = at.Line, Col = at.Col,
                    };
                }
                Expr whole = new BinaryExpr
                {
                    Op = BinOp.AndAlso, Line = at.Line, Col = at.Col,
                    Left = new BinaryExpr
                    {
                        Op = BinOp.AndAlso, Left = matched, Line = at.Line, Col = at.Col,
                        Right = new BinaryExpr
                        {
                            Op = BinOp.Eq, Line = at.Line, Col = at.Col,
                            Left = new MemberExpr { Target = Named(tuple), Name = "Length", Line = at.Line, Col = at.Col },
                            Right = new LiteralExpr { Kind = Lit.Int, Text = apart.Names.Count.ToString(), IntValue = apart.Names.Count, Line = at.Line, Col = at.Col },
                        },
                    },
                    Right = parts,
                };
                _r.Rewrites[seq] = whole;
                return CheckExpr(whole);
            }

            case SequenceExpr seq:
                CheckStmt(seq.Effect);
                return CheckExpr(seq.Value);

            case SubjectExpr subject when _subject.Count > subject.Outer:
            {
                var held = _subject[^(1 + subject.Outer)];
                _r.Resolved[subject] = new LocalSym(held.Slot, held.Type, "");
                return held.Type;
            }

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
                // NOT a nullable VALUE element: `int?[]` holds Nullable<int>
                // cells, a type of their own, and `a!` of an `int?[]?` is an
                // `int?[]` (Span<int?> refused its own array without this).
                if (suppressed.IsArray && suppressed.Element is Type element)
                {
                    return Type.ArrayOf(element.IsNullableValue ? element : element.AsNonNullable(), suppressed.ArrayRank);
                }
                // `x!` OF A NULLABLE VALUE TYPE IS STILL A `T?`, as in C#: the
                // operator changes the null state of a reference and nothing
                // else, so `n!.Value` reads the cell and `int i = n!` is an
                // error. Taking the '?' off here typed the cell's address as
                // the value inside it. The receiver `x?.M()` was rewritten
                // around is the one that means the value, and says so.
                if (suppressed.IsNullableValue)
                {
                    if (!sure.OpensCell) return suppressed;
                    MemberExpr inside = new() { Target = sure.Operand, Name = "Value", Guarded = true, Line = sure.Line, Col = sure.Col };
                    _r.Rewrites[sure] = inside;
                    return CheckExpr(inside);
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
            // IN A NULL-CONDITIONAL CHAIN the count is taken of the value the
            // chain reached, once, and only when it reached one:
            // `h?.Params[^1]` is null when h is, and otherwise the last of the
            // Params it read -- not `h?.Params[h?.Params.Count - 1]`, whose
            // index is itself an int? and which reads the chain twice.
            case IndexExpr { Args.Count: 1 } backOf when backOf.Args[0] is FromEndExpr endOf
                                                     && (backOf.NullConditional || HasConditionalMember(backOf.Target)):
            {
                SubjectExpr Reached() => new() { Outer = 0, Line = backOf.Line, Col = backOf.Col };
                IndexExpr at = new() { Target = Reached(), NullConditional = true, Line = backOf.Line, Col = backOf.Col };
                at.Args.Add(new BinaryExpr
                {
                    Op = BinOp.Sub,
                    Left = new MemberExpr
                    {
                        Target = new SuppressExpr { Operand = Reached(), Line = endOf.Line, Col = endOf.Col },
                        Name = "Length", Else = "Count", Line = endOf.Line, Col = endOf.Col,
                    },
                    Right = endOf.Offset,
                    Line = endOf.Line, Col = endOf.Col,
                });
                PatternExpr once = new() { Subject = backOf.Target, Test = at, Line = backOf.Line, Col = backOf.Col };
                _r.Rewrites[backOf] = once;
                return CheckExpr(once);
            }

            // `Make()[^1]`: THE TARGET ONCE. `^k` is the count less k, and the
            // count is the target's -- read by writing the target twice, Make
            // ran twice. A local, a parameter or `this` is read twice for
            // nothing; anything else is evaluated once and held, as C# does.
            case IndexExpr { Args.Count: 1 } back when back.Args[0] is FromEndExpr end:
            {
                if (!ReadTwiceForNothing(back.Target))
                {
                    PatternExpr once = FromEndOnce(back.Target, end, held => held);
                    _r.Rewrites[back] = once;
                    return CheckExpr(once);
                }
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

            // AN INDEX IN A `?.` CHAIN, or written `a?[i]`: null when what is
            // indexed is, and the element otherwise -- the target evaluated
            // once, as the call in a chain is (CheckCall). A value-typed
            // element comes out as its T?, as C# lifts it.
            case IndexExpr ix when ix.NullConditional || HasConditionalMember(ix.Target):
            {
                SubjectExpr forTest = new() { Line = ix.Line, Col = ix.Col };
                IndexExpr safe = new()
                {
                    Target = new SuppressExpr { Operand = new SubjectExpr { Line = ix.Line, Col = ix.Col }, OpensCell = true, Line = ix.Line, Col = ix.Col },
                    Line = ix.Line, Col = ix.Col,
                };
                safe.Args.AddRange(ix.Args);
                ConditionalExpr choose = new()
                {
                    Cond = new BinaryExpr
                    {
                        Op = BinOp.Eq, Left = forTest,
                        Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = ix.Line, Col = ix.Col },
                        Line = ix.Line, Col = ix.Col,
                    },
                    Then = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = ix.Line, Col = ix.Col },
                    Else = safe, Line = ix.Line, Col = ix.Col,
                };
                PatternExpr once = new() { Subject = ix.Target, Test = choose, Line = ix.Line, Col = ix.Col };

                // The arms have to agree, and one is null: a value-typed
                // element needs its cell said out loud. Asked quietly first,
                // because asking is how its type is learnt.
                _quiet++;
                Type element = CheckExpr(once);
                _quiet--;
                if (!element.IsError && !element.IsReference && !element.IsNullableValue
                    && RefOf(element) is TypeRef cell)
                {
                    choose.Else = new CastExpr
                    {
                        Type = new TypeRef
                        {
                            Name = cell.Name, Arguments = cell.Args, ArrayRank = cell.ArrayRank,
                            PointerDepth = cell.PointerDepth, TupleNames = cell.TupleNames,
                            Nullable = true, Line = ix.Line, Col = ix.Col,
                        },
                        Operand = safe, Line = ix.Line, Col = ix.Col,
                    };
                }

                _r.Rewrites[ix] = once;
                return CheckExpr(once);
            }

            // AN INDEX OR A RANGE HELD IN A VALUE: `arr[r]` with `Range r`,
            // `list[i]` with `Index i`. C# gives every countable type both
            // (C# 12.8.12.3's implicit Index and Range support): an Index is
            // turned into the offset it names, and a Range becomes a call of
            // the type's slicing member -- GetSubArray for an array, Substring
            // for a string, Slice for anything that has one. Only when the
            // type has no indexer taking the Index or Range itself.
            case IndexExpr { Args.Count: 1, NullConditional: false } valued
                when valued.Args[0] is not (RangeExpr or FromEndExpr or LiteralExpr)
                     && Peek(valued.Args[0]).AsNonNullable() is { } held && (IsIndexType(held) || IsRangeType(held))
                     && Peek(valued.Target).AsNonNullable() is { IsError: false } indexed
                     && !(indexed.Symbol is { } owner && Reachable(owner, "get_Item").Any(g => g.Params.Count == 1
                          && MethodSignatures.SameType(g.Params[0].Type.AsNonNullable(), held))):
            {
                Expr made = ThroughIndexValue(valued, indexed, IsRangeType(held));
                _r.Rewrites[valued] = made;
                return CheckExpr(made);
            }

            // `^1` AND `1..^1` AS VALUES, a System.Index and a System.Range,
            // made the way C# makes them.
            case FromEndExpr end:
            {
                NewExpr index = new()
                {
                    Type = new TypeRef { Name = LibraryType("Index"), Line = end.Line, Col = end.Col },
                    Line = end.Line, Col = end.Col,
                };
                index.Args.Add(end.Offset);
                index.WritableArgNames.Add(null);
                index.Args.Add(new LiteralExpr { Kind = Lit.Bool, Text = "true", IntValue = 1, Line = end.Line, Col = end.Col });
                index.WritableArgNames.Add(null);
                _r.Rewrites[end] = index;
                return CheckExpr(index);
            }

            case RangeExpr range:
            {
                Expr Bound(Expr? given, string otherwise) => given ?? new MemberExpr
                {
                    Target = new NameExpr { Name = LibraryType("Index"), Line = range.Line, Col = range.Col },
                    Name = otherwise, Line = range.Line, Col = range.Col,
                };
                NewExpr made = new()
                {
                    Type = new TypeRef { Name = LibraryType("Range"), Line = range.Line, Col = range.Col },
                    Line = range.Line, Col = range.Col,
                };
                made.Args.Add(Bound(range.From, "Start"));
                made.WritableArgNames.Add(null);
                made.Args.Add(Bound(range.To, "End"));
                made.WritableArgNames.Add(null);
                _r.Rewrites[range] = made;
                return CheckExpr(made);
            }

            case IndexExpr ix:
            {
                Type target = CheckExpr(ix.Target);

                // AN ELEMENT OF A DYNAMIC VALUE, bound when the program runs.
                if (_usesDynamic && target.Dynamic && !target.IsError && !target.IsArray)
                {
                    return LateIndex(ix);
                }

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
                // The indexers are gathered once for the test and the checking
                // of the arguments, nothing running between the two.
                List<MethodSymbol>? itemGetters = target.Symbol != null && !target.IsArray && !target.IsError
                    ? Reachable(target.Symbol, "get_Item") : null;
                if (itemGetters is not null && TakesArgs(itemGetters, ix.Args.Count))
                {
                    List<Type> index = CheckIndexArgs(target, ix.Args, itemGetters);
                    MethodSymbol? getter = IndexerFor(
                        Reachable(target.Symbol, "get_Item"), index, ix.Args.Count);

                    if (getter != null)
                    {
                        for (int i = 0; i < ix.Args.Count; i++)
                        {
                            CheckAssignable(index[i], ThroughUnmade(target, getter, getter.Params[i].Type),
                                            ix.Args[i], "index");
                        }

                        RequireNonNull(target, ix.Target, "index into");
                        _r.Indexers[ix] = getter;
                        return Unmade(target.AsNonNullable())
                            ? ThroughUnmade(target, getter, getter.Returns)
                            : ContextualMemberResult(target, getter);
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
                // the same load Sys.GetChar does -- our own spelling of it, and
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
                if (nw.CanonSelf is { } nwSelf) CheckExpr(nwSelf);
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

                    // `int x = new();` IS `new int()`, which is 0 -- and for a
                    // `T?` waiting, a T, as below. Every primitive value type has
                    // the parameterless constructor C# gives a struct.
                    Type primitive = _wanted is { IsNullableValue: true } cell ? cell.Underlying : _wanted ?? Type.Error;
                    if (primitive is { Symbol: null, IsArray: false, IsPointer: false } && (primitive.IsNumeric || primitive.Prim is Prim.Bool or Prim.Char)
                        && nw.Args.Count == 0 && nw.Body.IsEmpty && RefOf(primitive) is TypeRef zeroType)
                    {
                        DefaultExpr zero = new() { Type = zeroType, Line = nw.Line, Col = nw.Col };
                        _r.Rewrites[nw] = zero;
                        return CheckExpr(zero);
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
                //
                // A `T?` WAITING FOR IT GETS A T, as C# makes one: `Address? a =
                // new(slot, 0)` constructs an Address, and the conversion to
                // Address? that follows puts it in its cell. Taken as the T? it
                // was a struct's block read as a cell.
                Type type = nw.Type.Name.Length == 0
                          ? (_wanted is { IsNullableValue: true } lifted ? lifted.Underlying : _wanted ?? Type.Error)
                          : Resolve(nw.Type, _thisType);

                // `new int()`, `new E()`: ZERO, as a value type's parameterless
                // constructor makes it -- written so, or a copy's `new T()`
                // over a number, a bool, a char or an enum.
                if (nw.Type.Name.Length != 0 && nw.Elements is null && nw.ArraySize is null && nw.Utf8Bytes is null && !nw.Collection
                    && nw.Args.Count == 0 && nw.Body.IsEmpty && !type.IsArray && !type.IsPointer && !type.IsNullableValue
                    && (type.Symbol is null ? type.IsNumeric || type.Prim is Prim.Bool or Prim.Char : type.Symbol.Kind == TypeKind.Enum))
                {
                    DefaultExpr zero = new() { Type = nw.Type, Line = nw.Line, Col = nw.Col };
                    _r.Rewrites[nw] = zero;
                    return CheckExpr(zero);
                }

                if (nw.Elements is { } written)
                {
                    // `new[] { a, b }` TAKES THE BEST COMMON TYPE of what is
                    // written (C# 12.8.17.5): `new[] { "a", null }` is a
                    // string?[] and `new[] { 1, 2L }` a long[]; `new Op[] {
                    // ... }` was told. Every element is then checked against
                    // it, so one that fits nothing says so where it is.
                    if (written.Count == 0 && nw.Type.Name.Length == 0)
                    {
                        Error(nw, "an implicitly-typed array needs at least one element to take its type from");
                        return Type.Error;
                    }

                    List<Type> had = new(written.Count);
                    foreach (Expr one in written)
                    {
                        Type checkedOne = nw.Type.Name.Length == 0 ? CheckExpr(one) : Type.Error;
                        // `null!` IS STILL THE NULL LITERAL to the best common
                        // type, as C# has it: `new[] { "", null! }` is a
                        // string[], not an object[].
                        if (one is SuppressExpr { Operand: LiteralExpr { Kind: Lit.Null } }) checkedOne = Type.Null;
                        had.Add(checkedOne);
                    }
                    Type element = type;
                    if (nw.Type.Name.Length == 0)
                    {
                        if (BestCommonType(had) is not Type best)
                        {
                            if (had.All(t => !t.IsError))
                            {
                                Error(nw, "no best type found for the implicitly-typed array");
                            }
                            return Type.Error;
                        }
                        element = best;
                    }

                    for (int i = 0; i < written.Count; i++)
                    {
                        // AN ELEMENT IS CONVERTED TO THE ELEMENT TYPE, so it is
                        // checked with that as what is wanted: `RegOperand[] r =
                        // { new() { N = 1 } }` makes RegOperands, and a lambda or
                        // a tuple in a typed array takes its shape from it.
                        Type one;
                        if (nw.Type.Name.Length == 0)
                        {
                            one = had[i];
                        }
                        else
                        {
                            Type? outer = _wanted;
                            _wanted = element;
                            one = CheckExpr(written[i]);
                            _wanted = outer;
                        }

                        CheckAssignable(one, element, written[i], $"element {i}");
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

                List<Type> constructorArgs = new(nw.Args.Count);
                foreach (Expr argument in nw.Args)
                {
                    constructorArgs.Add(argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null } || HoldsLambda(argument)
                        ? Type.Any : CheckExpr(argument));
                }

                _wanted = outerNew;

                if (type.Symbol is TypeSymbol constructed)
                {
                    if (ResolveConstructor(nw, constructed, constructorArgs, made: type) is MethodSymbol ctor)
                    {
                        _r.NewConstructors[nw] = ctor;
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
                // `-2147483648` IS AN INT (C# 6.4.5.3): the literal 2147483648
                // right after a unary minus is int.MinValue, where on its own
                // it would be a uint and its negation a long.
                // CHECKED AGAIN -- overloads are chosen by checking arguments
                // more than once -- it is the literal it was rewritten to,
                // not a negated uint (a long) or a negated ulong (an error).
                if (u.Op == UnOp.Neg && _r.Rewrites.TryGetValue(u, out Expr? folded) && folded is LiteralExpr)
                {
                    return CheckExpr(folded);
                }
                if (u.Op == UnOp.Neg && u.Operand is LiteralExpr { Kind: Lit.Int, IntValue: 2147483648 } minimal
                    && minimal.Text.All(char.IsDigit) && !_r.Rewrites.ContainsKey(u))
                {
                    LiteralExpr least = new() { Kind = Lit.Int, Text = "-2147483648", IntValue = int.MinValue, Line = u.Line, Col = u.Col };
                    _r.Rewrites[u] = least;
                    return CheckExpr(least);
                }
                // AND `-9223372036854775808` IS A LONG, long.MinValue, by the
                // same rule: the literal on its own is a ulong, and negated it
                // was that ulong, 9223372036854775808, with the sign lost.
                if (u.Op == UnOp.Neg && u.Operand is LiteralExpr { Kind: Lit.Int } widest
                    && widest.Text == "9223372036854775808" && !_r.Rewrites.ContainsKey(u))
                {
                    LiteralExpr least = new() { Kind = Lit.Int, Text = "-9223372036854775808L", IntValue = long.MinValue, Line = u.Line, Col = u.Col };
                    _r.Rewrites[u] = least;
                    return CheckExpr(least);
                }

                // `&Method`: a static method's address, as a function pointer.
                if (u.Op == UnOp.AddressOf && AddressedMethod(u.Operand) is MethodSymbol addressed)
                {
                    _r.MethodAddresses[u] = addressed;
                    return new Type
                    {
                        Prim = Prim.NInt,
                        Function = new FunctionPointer(addressed.Params.Select(p => p.Type).ToList(), addressed.Returns,
                            addressed.Decl is MemberDecl md && md.Attributes.Any(a => a.Is("UnmanagedCallersOnly"))),
                    };
                }

                int outerContext = _uncheckedDepth;
                if (u.Op == UnOp.Unchecked) _uncheckedDepth = 1;
                else if (u.Op == UnOp.Checked) _uncheckedDepth = 0;
                Type t;
                try { t = CheckExpr(u.Operand); }
                finally { _uncheckedDepth = outerContext; }

                if (t.IsError)
                {
                    return Type.Error;
                }

                // AN OPERATOR ON A DYNAMIC VALUE, bound when the program runs.
                if (_usesDynamic && LateUnary(u, t) is Type lateUnary)
                {
                    return lateUnary;
                }

                // A TYPE'S OWN OPERATOR (C# 12.9): `-v`, `~flags`, `!ok`,
                // `+x` and `i++` on a struct or class that declares one.
                if (UserUnary(u, t) is Type byOperator)
                {
                    return byOperator;
                }

                switch (u.Op)
                {
                    case UnOp.Plus:
                        if (!t.IsNumeric)
                        {
                            Error(u, $"'+' needs a number, not '{t}'");
                            return Type.Error;
                        }
                        return t.IsNullableValue ? t : Promote(t);

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
                        {
                            // A UINT NEGATED IS A LONG, and a ulong cannot be
                            // negated at all (C# 12.9.3): the value is widened
                            // to a type that holds its negation first.
                            Type negated = Promote(t.IsNullableValue ? t.Underlying : t);
                            if (negated.Prim == Prim.U64 && negated.Symbol is null)
                            {
                                Error(u, "CS0023: operator '-' cannot be applied to an operand of type 'ulong'");
                                return Type.Error;
                            }
                            if (negated.Prim == Prim.U32 && negated.Symbol is null) negated = Type.I64;
                            return t.IsNullableValue ? negated.AsNullable() : negated;
                        }

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

            // `Make()[^1] = v` and `Make()[^1] += v`: the target once, as for a
            // read of it (FromEndOnce), with the assignment made to the
            // element of what was held -- target, then index, then value,
            // C#'s order.
            case AssignExpr fromEnd when fromEnd.Target is IndexExpr { Args.Count: 1, NullConditional: false } backAt
                                         && backAt.Args[0] is FromEndExpr backEnd && !ReadTwiceForNothing(backAt.Target):
            {
                PatternExpr once = FromEndOnce(backAt.Target, backEnd, element => new AssignExpr
                {
                    Target = element, Op = fromEnd.Op, Value = fromEnd.Value,
                    Line = fromEnd.Line, Col = fromEnd.Col,
                });
                _r.Rewrites[fromEnd] = once;
                return CheckExpr(once);
            }

            case AssignExpr a:
            {
                // AN EVENT WITH ACCESSORS IS TWO METHODS: `h += x` over one
                // is add_h(x) and `-=` is remove_h(x), as C# compiles them.
                if (a.Op is BinOp.Add or BinOp.Sub && EventAccessorCall(a) is CallExpr accessed)
                {
                    _r.Rewrites[a] = accessed;
                    CheckExpr(accessed);
                    return Type.Void;
                }

                // A FIELD-LIKE EVENT on the left of += or -= is subscribed to,
                // which anyone may do (EventFromOutside): the target is read
                // here and again as Combine's argument, and both are let by.
                if (a.Op is BinOp.Add or BinOp.Sub)
                {
                    _eventOperands.Add(a.Target);
                }

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

                // `r = ref other;` POINTS A REF LOCAL ELSEWHERE; nothing is
                // written through it (Binder.RefLocals).
                if (a.Op is null && a.Value is RefArgExpr { IsOut: false, Name: null } rebound)
                {
                    return CheckRefAssignment(a, rebound);
                }

                // AND A `ref readonly` LOCAL IS NEVER WRITTEN THROUGH. A field
                // of the struct it refers to is asked once the target has
                // been checked, below.
                if (a.Target is NameExpr readOnlyLocal && Lookup(readOnlyLocal.Name) is LocalSym { ReadOnlyRef: true })
                {
                    Error(a, $"'{readOnlyLocal.Name}' is a ref readonly local and cannot be written through");
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
                        // A PROPERTY TOO: `Items = new()` in a constructor, a
                        // get-only auto-property's one place to be set, is a
                        // List because Items is (its getter's type).
                        PropertyGetSym g => g.Getter.Returns,
                        CapturedPropertyGetSym g => g.Getter.Returns,
                        PropertySetSym p => p.Setter.Params[^1].Type,
                        _ => _thisType?.FindField(assignmentName.Name)?.Type
                          ?? _capturedThisType?.FindField(assignmentName.Name)?.Type
                          ?? PropertyType(_thisType, assignmentName.Name)
                          ?? PropertyType(_capturedThisType, assignmentName.Name),
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

                    // A STORE BOUND LATE: a dynamic value's member or element,
                    // or a compound assignment to a dynamic variable.
                    if (_usesDynamic && LateAssignment(a, target) is Type lateStore)
                    {
                        return lateStore;
                    }

                    // A FIELD WRITTEN THROUGH SOMETHING READ-ONLY -- a `ref
                    // readonly` local's struct, an `in` struct, a ref readonly
                    // call's -- writes the read-only variable itself.
                    if (a.Target is not NameExpr && ReadOnlyVariable(a.Target) is string readOnlyHolder)
                    {
                        Error(a, $"{readOnlyHolder} and cannot be written through");
                    }

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
                    if (property is PropertySetSym setOnly)
                    {
                        _r.PropertySetters[a.Target] = setOnly.Setter;
                        target = setOnly.Setter.Params[0].Type;
                    }
                    else if (getter != null && propertyName != null)
                    {
                        MethodSymbol? setter = getter.Owner
                            .FindMethods("set_", propertyName)
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
                    CheckAssignable(value, target, a.Value, assignedLocal is not null ? "assignment to a local" : "assignment");
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
                        CallExpr synthesised = DelegateCombination(a.Op is BinOp.Add, a.Target, a.Value, a, target);
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
                        _r.BoxedAs[arm] = whole;
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
                    _r.BoxedAs[c2.Else] = a2;
                    return a2;
                }
                if (b2.IsNullableValue && !a2.IsNullableValue && Convertible(a2, b2.Underlying))
                {
                    _r.Boxes.Add(c2.Then);
                    _r.BoxedAs[c2.Then] = b2;
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
                        ArrayBecomes(arm, had, whole);
                        // AND A TUPLE ARM OF ANOTHER SHAPE IS REBUILT AS THE
                        // WHOLE'S, element by element: `c ? (1, 2) : (3L, 4L)`
                        // is two longs either way.
                        if (!had.IsError && !whole.IsError && TupleRebuilt(had, whole))
                        {
                            CheckAssignable(had, whole, arm, "arm");
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

                // NO NATURAL TYPE, AND A TYPE IS WANTED: the conditional is
                // that type, as C#'s target-typed conditional makes `found ?
                // list : Array.Empty<T>()` an IReadOnlyList<T> where one is
                // returned. Any interface the two share is a guess; the one
                // asked for is the answer, and the guess may not convert to it.
                if (_wanted is { IsError: false, IsVoid: false } target
                    && Convertible(a2, target) && Convertible(b2, target))
                {
                    return Joined(target);
                }

                if (CommonInterface(a2, b2) is Type shared)
                {
                    return Joined(shared);
                }

                // TWO NUMBERS WITH NO NATURAL TYPE -- `signed ? ToInt32(...) :
                // ToUInt32(...)` -- as an argument, where the type it is wanted
                // as is not known until the overload is: the narrowest both
                // convert to, which is what C#'s target typing makes of it for
                // every target that takes both (long, double). A target that
                // takes neither is refused where the argument is matched.
                if (a2.Prim is not (Prim.Void or Prim.Any or Prim.String or Prim.NullLiteral) && b2.Prim is not (Prim.Void or Prim.Any or Prim.String or Prim.NullLiteral)
                    && a2.Symbol is null && b2.Symbol is null && !a2.IsArray && !b2.IsArray && !a2.IsPointer && !b2.IsPointer)
                {
                    foreach (Type wider in new[] { Type.I64, Type.F64 })
                        if (Convertible(a2, wider) && Convertible(b2, wider)) return Joined(wider);
                }

                Error(c2, $"the branches of a conditional have unrelated types '{a2}' and '{b2}'");
                return Type.Error;
            }

            case CastExpr cast:
            {
                // A LAMBDA OR A METHOD GROUP CAST TO A DELEGATE is converted to
                // it (C# 12.9.7): `(Action)(() => { })` says what the lambda
                // is, which nothing else around it may. Checked as wanted by
                // the type, and then converted as an assignment converts it.
                if (cast.Operand is LambdaExpr || IsFunctionSource(cast.Operand))
                {
                    Type target = Resolve(cast.Type, _thisType);
                    Type? outerWanted = _wanted;
                    _wanted = target;
                    Type converted = CheckExpr(cast.Operand);
                    _wanted = outerWanted;
                    if (!converted.IsError && !target.IsError) CheckAssignable(converted, target, cast.Operand, "cast");
                    NoteShape(cast, target);
                    return target;
                }
                Type operand = CheckExpr(cast.Operand);
                if (cast.CanonSelf is { } castSelf) CheckExpr(castSelf);
                Type wanted = Resolve(cast.Type, _thisType);
                NoteShape(cast, wanted);
                // A method group, known as one only now that it is checked.
                if (IsFunctionSource(cast.Operand) && !wanted.IsError)
                {
                    CheckAssignable(operand, wanted, cast.Operand, "cast");
                    return wanted;
                }

                // A DYNAMIC VALUE CAST: the binder's explicit conversion.
                if (LateCast(cast, operand, wanted) is Type lateCast)
                {
                    return lateCast;
                }

                // AN ARRAY OR A STRING CAST TO A SPAN IS MADE ONE, as its
                // implicit conversion makes one where it is assigned: the cast
                // only says which. Left alone, `(ReadOnlySpan<int>)array` was
                // the array's reference read as a span -- its descriptor and
                // its length as the span's first two elements.
                if ((operand.IsArray || operand.Prim == Prim.String && !operand.IsArray) && SpanHolds(wanted) is not null
                    && !operand.IsError && !_r.Rewrites.ContainsKey(cast.Operand))
                {
                    CheckAssignable(operand, wanted, cast.Operand, "cast");
                }

                // A CONSTANT THAT DOES NOT FIT IS REFUSED (C# 12.23, CS0221):
                // a constant expression is checked at compile time unless it is
                // written unchecked. `(short)100000` is an error in .NET, and a
                // compiler that took it compiled what .NET's would not.
                if (_uncheckedDepth == 0 && !operand.IsError && !wanted.IsError && wanted.Symbol is null && !wanted.Nullable
                    && (wanted.IsInteger || wanted.Prim == Prim.Char) && operand.Symbol is null)
                {
                    if (operand.IsInteger && ConstantValue(cast.Operand, _thisType) is long whole && !Binder.Fits(whole, wanted)
                        && !(operand.Prim == Prim.U64 && wanted.Prim == Prim.U64))
                    {
                        Error(cast, $"CS0221: constant value '{(operand.IsUnsigned ? ((ulong)whole).ToString() : whole.ToString())}' cannot be converted to a '{wanted}' (use 'unchecked' syntax to override)");
                    }
                    else if (operand.Prim is Prim.F32 or Prim.F64 && RealConstant(cast.Operand, _thisType) is double real
                        && !RealFits(real, wanted))
                    {
                        Error(cast, $"CS0221: constant value '{real.ToString(System.Globalization.CultureInfo.InvariantCulture)}' cannot be converted to a '{wanted}' (use 'unchecked' syntax to override)");
                    }
                }

                // `(string?)attribute` CALLS THE OPERATOR the type declares,
                // implicit or explicit, where no standard conversion either
                // way does the job (a downcast is a standard one).
                if (!StandardConvertible(operand, wanted) && !StandardConvertible(wanted, operand)
                    && !_r.Rewrites.ContainsKey(cast) && UserConversion(operand, wanted, explicitToo: true) is { } castOp)
                {
                    CallExpr call = new()
                    {
                        Target = new MemberExpr { Target = Qualified(castOp.Owner.Key, cast), Name = castOp.Name, Line = cast.Line, Col = cast.Col, File = cast.File },
                        Line = cast.Line, Col = cast.Col, File = cast.File,
                    };
                    call.Args.Add(cast.Operand);
                    _r.Rewrites[cast] = call;
                    CheckExpr(call);
                    Type produced = Converting(call, castOp);
                    return wanted.Nullable && !produced.Nullable ? wanted : produced;
                }

                // A TUPLE CAST TO ANOTHER SHAPE IS ITEM BY ITEM (C# 10.3.6):
                // `((long, double))(1, 2)` is a new tuple, each item cast
                // explicitly -- not the same bytes read as another layout.
                if (!_r.Rewrites.ContainsKey(cast) && TupleArity(operand) is int items && TupleArity(wanted) == items
                    && !ReferenceEquals(operand.Symbol, wanted.Symbol))
                {
                    List<FieldSymbol> into = wanted.Symbol!.Fields.Where(f => !f.Static).ToList();
                    TupleExpr rebuilt = new() { Line = cast.Line, Col = cast.Col, File = cast.File };
                    for (int i = 0; i < items; i++)
                    {
                        Expr item = new MemberExpr { Target = new SubjectExpr { Line = cast.Line, Col = cast.Col }, Name = "Item" + (i + 1), Line = cast.Line, Col = cast.Col, File = cast.File };
                        rebuilt.Items.Add(RefOf(into[i].Type) is TypeRef element
                            ? new CastExpr { Type = element, Operand = item, Line = cast.Line, Col = cast.Col, File = cast.File }
                            : item);
                    }
                    PatternExpr each = new() { Subject = cast.Operand, Test = rebuilt, Line = cast.Line, Col = cast.Col, File = cast.File };
                    _r.Rewrites[cast] = each;
                    Type? outside = _wanted;
                    _wanted = wanted;
                    CheckExpr(each);
                    _wanted = outside;
                    return wanted;
                }

                // `(int?)5` PUTS THE NUMBER IN A CELL, which is a conversion
                // and not merely a name for the same bits. Marked on the
                // operand, because that is the value the cell is made from.
                if (wanted.IsNullableValue && !operand.IsNullableValue
                    && operand.Prim is not (Prim.NullLiteral or Prim.Any) && !operand.IsError
                    && !operand.IsReference && operand.Symbol is not { Kind: TypeKind.Interface })
                {
                    _r.Boxes.Add(cast.Operand);
                    _r.BoxedAs[cast.Operand] = wanted;
                }

                // `(IReadOnlyList<T>)array` IS THE CONVERSION it names, and the
                // view that answers Count and the indexer is made here as it is
                // where the conversion is implicit.
                ArrayBecomes(cast.Operand, operand, wanted);
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
                // Every arm's type, and whether two disagreed pending the rest (below).
                List<(SwitchArm Arm, Type Type)>? armTypes = null;
                List<SwitchArm>? undecided = null;
                bool exhaustive = false;
                Coverage covered = new();
                bool sawTrue = false, sawFalse = false, sawNull = false, sawSome = false;
                List<Sym> pastNull = new();

                foreach (SwitchArm arm in sx.Arms)
                {
                    if (arm.CanonSelf is { } armSelf) CheckExpr(armSelf);
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
                        && (NamesConstant(bare.Name) || Lookup(bare.Name) is ConstSym))
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

                        // A CONSTANT THAT FITS THE SUBJECT IS OF ITS TYPE, as
                        // C#'s constant pattern converts it: `ReadByte() switch
                        // { 0 => false, 1 => true, ... }` compares a byte with
                        // the bytes 0 and 1, not with two ints it cannot hold.
                        bool fits = constant.IsInteger && subject.AsNonNullable().IsInteger
                            && ConstantValue(arm.Value, _thisType) is long armValue && Binder.Fits(armValue, subject.AsNonNullable());
                        if (!fits && !Convertible(constant, subject) && !constant.IsError && !subject.IsError)
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

                    // PAST A `null =>` ARM THE SUBJECT IS NOT NULL: every later
                    // arm is only reached by a value that arm did not take, and
                    // C#'s flow analysis knows it -- `null => "none", _ =>
                    // o.GetType().Name` reads o without a check.
                    if (arm.When is BinaryExpr { Op: BinOp.Eq, Left: SubjectExpr { Outer: 0 }, Right: LiteralExpr { Kind: Lit.Null } }
                        || arm.Value is LiteralExpr { Kind: Lit.Null } && arm.When is null)
                    {
                        pastNull.AddRange(Assume(new BinaryExpr
                        {
                            Op = BinOp.Ne, Left = sx.Subject,
                            Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = arm.Line, Col = arm.Col },
                            Line = arm.Line, Col = arm.Col,
                        }, true));
                    }

                    // `_` WITH NO GUARD IS THE ONE THAT MAKES IT EXHAUSTIVE.
                    // One behind a guard matches only sometimes, so it settles
                    // nothing -- and a switch expression that falls off the end
                    // has no value to be.
                    if (!arm.Fallback)
                    {
                        exhaustive = exhaustive || (arm.Discard && arm.When is null);

                        // A PATTERN THAT CAN ONLY FAIL ON LENGTH -- `[]`, `[var
                        // x]`, `[_, _, ..]` -- covers those lengths, and
                        // together they may cover them all.
                        if (arm.Discard && arm.When is not null && Lengths(arm.When) is { } lengths)
                        {
                            covered.Union(lengths);
                            exhaustive = exhaustive || covered.All;
                        }

                        // AND `null` WITH `{ }` IS EVERYTHING: no value, and
                        // any value at all.
                        if (arm.Value is LiteralExpr { Kind: Lit.Null } && arm.When is null
                            || arm.Discard && arm.When is BinaryExpr { Op: BinOp.Eq, Left: SubjectExpr { Outer: 0 }, Right: LiteralExpr { Kind: Lit.Null } })
                        {
                            sawNull = true;
                        }
                        if (arm.Discard && arm.When is BinaryExpr { Op: BinOp.Ne, PatternNullTest: true, Left: SubjectExpr { Outer: 0 }, Right: LiteralExpr { Kind: Lit.Null } })
                        {
                            sawSome = true;
                        }
                        exhaustive = exhaustive || sawNull && sawSome;

                        // AND `true` WITH `false` IS EVERY bool.
                        if (arm.Value is not null && arm.When is null && subject.Prim == Prim.Bool && !subject.Nullable
                            && ConstantValue(arm.Value, _thisType) is long truth)
                        {
                            sawTrue |= truth != 0;
                            sawFalse |= truth == 0;
                            exhaustive = exhaustive || sawTrue && sawFalse;
                        }
                    }

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
                        // NOT YET A MISMATCH: C# types a switch expression by the
                        // best common type of ALL its arms, and a later arm may
                        // be the one the others convert to --
                        // `o switch { R r => new R(), S s => new S(), _ => o }`
                        // is an Operand. Decided once every arm is seen.
                        undecided ??= new();
                    }
                    if (!value.IsError && value.Prim != Prim.NullLiteral) (armTypes ??= new()).Add((arm, value));
                }

                if (undecided is not null && !result.IsError)
                {
                    bool nullable = result.Nullable;
                    Type? common = armTypes!.Select(a => a.Type).FirstOrDefault(t => armTypes!.All(a => Convertible(a.Type, t)));
                    if (common is not null) result = nullable ? common.AsNullable() : common;
                    else
                    {
                        (SwitchArm bad, Type badType) = armTypes!.First(a => !Convertible(a.Type, armTypes![0].Type));
                        Error(bad, $"this arm is '{badType}' and the ones before it are '{armTypes![0].Type}'");
                        result = Type.Error;
                    }
                }

                _subject.RemoveAt(_subject.Count - 1);
                Forget(pastNull);

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

                // AN ARM THAT IS A DIFFERENT TUPLE IS REBUILT AS THE RESULT'S:
                // `(int.MinValue, int.MaxValue)` as an arm of a (long, long)
                // switch is two longs, element by element. Left to itself the
                // arm's two ints were read as one long, and the self-compiled
                // compiler's range for an int element came out as nonsense.
                if (!result.IsError)
                {
                    foreach (SwitchArm arm in sx.Arms)
                    {
                        if (_r.ExprType.TryGetValue(arm.Result, out Type? had) && !had.IsError
                            && TupleRebuilt(had, result))
                        {
                            CheckAssignable(had, result, arm.Result, "arm");
                        }
                    }
                }

                // NOT A WARNING. An expression has to have a value on every
                // path, and there is nothing sensible to produce when nothing
                // matched -- C# throws, and throwing here would mean inventing
                // an exception type and a control-flow path nobody asked for.
                // A WARNING, AS C#'s CS8509 IS: the arm the parser added throws
                // SwitchExpressionException for a value no written arm took.
                if (!exhaustive)
                {
                    Warning(sx, "CS8509", "the switch expression does not handle all possible values of its input type (it is not exhaustive)");
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
                if (to.CanonSelf is { } toSelf) CheckExpr(toSelf);
                // typeof(T). Recorded the same way and for the same reason as
                // sizeof: the code generator has a TypeRef and nowhere to
                // resolve it, and inside a generic the answer depends on which
                // instantiation is being compiled.
                Type named = Resolve(to.Type, _thisType);

                // A TYPE ARGUMENT ONLY RUN TIME KNOWS (Type.CanonParam): a
                // shared method copy's, handed in as a hidden argument, or a
                // shared class copy's where no mark reads it (ICanonSlot) --
                // what it is for this call, and object where nothing says.
                if (named is { CanonParam: not -1, Prim: Prim.Any, Symbol: null, ArrayRank: 0, PointerDepth: 0 })
                {
                    _r.RunTimeTypeOfs[to] = named;
                    return Type.TypeHandle;
                }

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
                // A WRITABLE REFERENCE TO SOMETHING READ-ONLY would let the
                // write the read-only promise refuses happen through it.
                if (!_readOnlyReference && ReadOnlyVariable(ra.Target) is string fixedVariable)
                {
                    Error(ra, $"{fixedVariable}, so it can only be referred to by 'ref readonly' or 'in'");
                }
                return target;
            }

            case IsExpr isx:
            {
                Type operand = CheckExpr(isx.Operand);
                if (isx.CanonSelf is { } isxSelf) CheckExpr(isxSelf);

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

                if (isx.Binding is null && isConstant && ConstantHidesType(isx.Type.Name))
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

                // AN ARRAY IS EVERY SEQUENCE INTERFACE OF ITS ELEMENT, as .NET's
                // arrays are -- IList<T>, ICollection<T>, IReadOnlyList<T> --
                // and here it is one by conversion (a view, SZArrayHelper), not
                // by its descriptor. Known from the operand's type, the test is
                // whether there is an array at all.
                if (isx.Binding is null && operand.IsArray && !tested.IsArray
                    && tested.AsNonNullable().Symbol is { Kind: TypeKind.Interface }
                    && (ArrayFace(operand, tested) is not null || ListFace(operand, tested) is not null))
                {
                    BinaryExpr present = new()
                    {
                        Op = BinOp.Ne, Left = isx.Operand,
                        Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = isx.Line, Col = isx.Col },
                        Line = isx.Line, Col = isx.Col,
                    };
                    _r.Rewrites[isx] = present;
                    return CheckExpr(present);
                }

                if (tested.Symbol is { } testedSymbol)
                {
                    _r.TestedTypes[isx] = testedSymbol;
                    NoteShape(isx, tested);
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
                else if (tested.Prim == Prim.Any && tested.Symbol is null && !tested.IsArray && (operand.IsReference || operand.Prim == Prim.Any))
                {
                    // `x is object y` OVER A REFERENCE asks only whether there is
                    // one. Taken for a value pattern -- `object` is no class
                    // here, so the operand looked like a value -- it was always
                    // true, and the native compiler's escape analysis put a null
                    // where `FrameSlotOf(from, 0) is object slot` had found none.
                    _r.NonNullPatterns.Add(isx);
                }
                else if (tested.Prim == Prim.Any && tested.Symbol is null && !tested.IsArray && !operand.IsError && !operand.IsVoid)
                {
                    // A VALUE IS ALWAYS AN OBJECT: `5 is object` is true, and
                    // `o` in `5 is object o` the value boxed.
                    _r.BoxPatterns.Add(isx);
                }
                else if (!operand.IsReference && operand.Prim != Prim.Any && tested.Equals(operand))
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
                                      ?? owner.FindBackingField(init.Name);

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
                if (asx.CanonSelf is { } asxSelf) CheckExpr(asxSelf);
                Type type = Resolve(asx.Type, _thisType);

                if (type.Symbol is { } asSymbol)
                {
                    _r.TestedTypes[asx] = asSymbol;
                    NoteShape(asx, type);
                }
                else if (type.IsArray)
                {
                    _r.TestedArrays[asx] = type;
                }

                // `as object` -- and a shared copy's `as T`, which is one -- is C#.
                if (!type.IsReference && !(type.Prim == Prim.Any && type.Symbol is null && !type.IsNullableValue) && !type.IsError)
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
                return Type.Plain(_thisType.Base, Prim.Void);

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

    /// <summary>
    /// A member's declared type as seen through an UNMADE receiver: the
    /// template's parameters replaced by the arguments beside it, through the
    /// base list when the member is an interface's the template extends.
    /// `people.ToLookup(p => p.City)["Rome"]` indexes an ILookup&lt;string,
    /// Person&gt; no copy spells yet, whose indexer takes a K and hands back an
    /// IEnumerable&lt;E&gt; -- a string and an IEnumerable&lt;Person&gt; here, or the
    /// Select after it is asked of an open sequence. Any other receiver: the
    /// declared type unchanged.
    /// </summary>
    private Type ThroughUnmade(Type receiver, MethodSymbol member, Type declared)
        => Unmade(receiver.AsNonNullable()) && member.Owner is { } owner
            && Received(receiver, owner) is { } bound
            ? Close(declared, bound)
            : declared;

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

    /// <summary>A closure field over this holds its cell, not its value.</summary>
    private static bool CellSource(Sym? from)
        => from is LocalSym { Boxed: true } || from is FieldSym { Field.Boxed: true } || from is ParamSym { Boxed: true }
        // A generic local function's captured variable is the cell itself.
        || from is ParamSym { Cell: true };

    // The closure fields made holding a copy, and what they copied: a
    // parameter written AFTER the lambda that captured it, or by a lambda
    // checked later, is a cell only once the whole body has been checked.
    private readonly List<(FieldSymbol Field, Sym From)> _closureCopies = new();

    /// <summary>
    /// THE METHOD IS CHECKED, so whether each captured parameter is written
    /// is known: a closure field over one that is holds its cell, and so does
    /// a nested closure's field over that field.
    /// </summary>
    private void SettleCapturedCells()
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((FieldSymbol field, Sym from) in _closureCopies)
                if (!field.Boxed && CellSource(from)) { field.Boxed = true; changed = true; }
        }
        _closureCopies.Clear();
    }

    /// <summary>A write of a parameter, which a closure that captured it must see.</summary>
    private void NoteWritten(Expr target)
    {
        while (target is SuppressExpr suppressed) target = suppressed.Operand;
        if (target is NameExpr n && _r.Resolved.TryGetValue(n, out Sym? s) && s is ParamSym parameter) parameter.Written = true;
    }

    private void RequireAssignable(Expr target, string what)
    {
        NoteWritten(target);
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
            // So is the variable a ref-returning call answers.
            CallExpr call => RefCallee(call) is not null,
            _           => false,
        };

        if (!ok)
        {
            Error(target, $"cannot {what} this expression");
        }
        else if (ReadOnlyVariable(target) is string readOnly)
        {
            Error(target, $"{readOnly}, so it cannot {what} it");
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
        NoteWritten(target);
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
            // A CALL OF A METHOD THAT RETURNS BY REFERENCE IS A VARIABLE.
            CallExpr call => RefCallee(call) is not null,
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

        if (resolved is PropertySetSym setOnly && !_r.PropertySetters.ContainsKey(target))
        {
            _r.PropertySetters[target] = setOnly.Setter;
            return;
        }

        if (getter is null || propertyName is null
            || _r.PropertySetters.ContainsKey(target))
        {
            return;
        }

        MethodSymbol? propertySetter = getter.Owner
            .FindMethods("set_", propertyName)
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

        // CS8602: dereference of a possibly null reference, whatever shape
        // the dereference takes -- a member read, a call, an index. A value
        // type has no null representation to check for at run time, so the
        // same condition is an ERROR there rather than a warning.
        if (t.IsReference) Warning(at, "CS8602", message);
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
    /// Emptied sets for AssignedCopy to fill again. Every `if` and `switch`
    /// copied _assigned two or three times to merge its branches, and each
    /// copy was dropped as the statement ended.
    /// </summary>
    private readonly List<HashSet<LocalSym>> _spareAssigned = new();

    /// <summary>A copy of what is definitely assigned now, in a set from the spares where there is one.</summary>
    private HashSet<LocalSym> AssignedCopy()
    {
        HashSet<LocalSym> copy;
        if (_spareAssigned.Count > 0)
        {
            copy = _spareAssigned[^1];
            _spareAssigned.RemoveAt(_spareAssigned.Count - 1);
        }
        else
        {
            copy = new HashSet<LocalSym>(ReferenceEqualityComparer.Instance);
        }
        copy.UnionWith(_assigned);
        return copy;
    }

    /// <summary>A set from AssignedCopy that nothing holds any longer, emptied for the next.</summary>
    private void SpareAssigned(HashSet<LocalSym> set)
    {
        set.Clear();
        _spareAssigned.Add(set);
    }

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
    /// The sets returned are READ ONLY: nothing proved is the one shared empty
    /// set, and a set is made only where something is. Every condition the
    /// binder checks asks this, and two new sets for each -- nearly always
    /// empty -- were a large part of what binding allocated.
    /// </summary>
    private static readonly HashSet<string> NoPaths = new(StringComparer.Ordinal);

    private (HashSet<string> WhenTrue, HashSet<string> WhenFalse) OutPaths(Expr expression)
    {
        if (expression is CallExpr call && _r.Calls.TryGetValue(call, out MethodSymbol? method)
            && method.Returns.Prim == Prim.Bool)
        {
            HashSet<string>? proved = null;
            foreach (Expr argument in call.Args)
            {
                if (argument is RefArgExpr { IsOut: true } output
                    && Path(output.Target) is string path)
                {
                    (proved ??= new(StringComparer.Ordinal)).Add(path);
                }
            }
            return (proved ?? NoPaths, NoPaths);
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
            // True is left-true and right-true; false is either left-false,
            // or left-true and right-false.
            return (Union(leftTrue, rightTrue), Meet(leftFalse, leftTrue, rightFalse));
        }

        if (expression is BinaryExpr { Op: BinOp.OrElse } orExpr)
        {
            (HashSet<string> leftTrue, HashSet<string> leftFalse) = OutPaths(orExpr.Left);
            (HashSet<string> rightTrue, HashSet<string> rightFalse) = OutPaths(orExpr.Right);
            // False is left-false and right-false; true is either left-true,
            // or left-false and right-true.
            return (Meet(leftTrue, leftFalse, rightTrue), Union(leftFalse, rightFalse));
        }

        return (NoPaths, NoPaths);

        // a ∪ b, made only when both hold something.
        static HashSet<string> Union(HashSet<string> a, HashSet<string> b)
        {
            if (b.Count == 0) return a;
            if (a.Count == 0) return b;
            HashSet<string> both = new(a, StringComparer.Ordinal);
            both.UnionWith(b);
            return both;
        }

        // either ∩ (other ∪ then): what both ways out prove.
        static HashSet<string> Meet(HashSet<string> either, HashSet<string> other, HashSet<string> then)
        {
            if (either.Count == 0 || other.Count == 0 && then.Count == 0) return NoPaths;
            HashSet<string> met = new(StringComparer.Ordinal);
            foreach (string path in either)
                if (other.Contains(path) || then.Contains(path)) met.Add(path);
            return met.Count == 0 ? NoPaths : met;
        }
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
        ReturnStmt or ThrowStmt or BreakStmt or ContinueStmt or GotoStmt => true,
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
        ReturnStmt or ThrowStmt or ContinueStmt or GotoStmt => false,
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
            return Type.Plain(globalType, Prim.Void);
        }

        // A generic local function's captured variable, handed to it at a
        // call: the one it saw, whatever is called the same here.
        Sym? sym = n.CaptureOf is string function ? LookupCaptured(n.Name, function) : Lookup(n.Name);
        // A VARIABLE HANDED TO A GENERIC LOCAL FUNCTION IS A CELL, made one
        // where the hidden argument naming it is bound: the pass that binds a
        // lambda's body for its closure makes its locals afresh, and the call
        // there is not always where PassCaptures saw it.
        if (n.CaptureOf is not null)
        {
            switch (sym)
            {
                case LocalSym { IsRef: false } local:
                    local.Boxed = true;
                    if (_declOf.TryGetValue(local, out LocalDecl? where)) _r.BoxedLocals.Add(where);
                    break;
                case ParamSym { ByRef: false } parameter:
                    parameter.ForcedCell = true;
                    break;
            }
        }

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
                // as any method of the type is -- through the `this` a
                // closure holds, from inside one (DeclareGenericLocal).
                MethodGroupSym or CapturedMethodGroupSym => Type.Void,
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
                return Type.Plain(_thisType, Prim.Void);
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

            FieldSymbol? f = _thisType.FindField(n.Name) ?? _thisType.FindBackingField(n.Name);
            if (f is not null && NotInvocable(n, f.Type)) f = null;

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

            List<MethodSymbol> methods = Members(_thisType, n.Name);

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

            MethodSymbol? getter = Members(_thisType, Interned.Prefixed("get_", n.Name)).FirstOrDefault();
            if (getter is not null && NotInvocable(n, getter.Returns)) getter = null;

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

            if (Members(_thisType, Interned.Prefixed("set_", n.Name)).FirstOrDefault(s => s.Params.Count == 1) is { } onlySetter
                && !(onlySetter is { Static: false } && InStaticContext))
            {
                _r.Resolved[n] = new PropertySetSym(onlySetter);
                return onlySetter.Params[0].Type;
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
                return Type.Plain(_lexicalType, Prim.Void);
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
                                      ?? _lexicalType.FindBackingField(n.Name);
            if (staticField is { Static: true } && !NotInvocable(n, staticField.Type))
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

            MethodSymbol? staticGetter = _lexicalType.FindMethods("get_", n.Name)
                                                     .FirstOrDefault(m => m.Static);
            if (staticGetter is not null && !NotInvocable(n, staticGetter.Returns))
            {
                _r.Resolved[n] = new PropertyGetSym(staticGetter);
                return staticGetter.Returns;
            }

            MethodSymbol? staticSetter = _lexicalType.FindMethods("set_", n.Name)
                                                     .FirstOrDefault(m => m.Static && m.Params.Count == 1);
            if (staticSetter is not null)
            {
                _r.Resolved[n] = new PropertySetSym(staticSetter);
                return staticSetter.Params[0].Type;
            }
        }

        if (_capturedThisType is not null && n.Name == _capturedThisType.Name)
        {
            _r.Resolved[n] = new TypeNameSym(_capturedThisType);
            return Type.Plain(_capturedThisType, Prim.Void);
        }

        // An instance local function is lowered to a closure but retains the
        // enclosing method's `this`. Reads, writes, properties and method calls
        // therefore go through the hidden reference captured in that closure.
        if (_capturedThisType is not null && _capturedThisField is not null)
        {
            FieldSymbol? outerField = _capturedThisType.FindField(n.Name)
                                   ?? _capturedThisType.FindBackingField(n.Name);
            if (outerField is not null && !NotInvocable(n, outerField.Type))
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

            MethodSymbol? outerGetter = _capturedThisType.FindMethods("get_", n.Name).FirstOrDefault();
            if (outerGetter is not null && !NotInvocable(n, outerGetter.Returns))
            {
                _r.Resolved[n] = new CapturedPropertyGetSym(_capturedThisField, outerGetter);
                return outerGetter.Returns;
            }

            MethodSymbol? outerSetter = _capturedThisType.FindMethods("set_", n.Name).FirstOrDefault(m => m.Params.Count == 1);
            if (outerSetter is not null)
            {
                _r.Resolved[n] = new PropertySetSym(outerSetter, _capturedThisField);
                return outerSetter.Params[0].Type;
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
        for (string? outerKey = OuterKey(_thisType ?? _lexicalType ?? _scope);
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
                                    ?? outer.FindBackingField(n.Name);
            if (outerStatic is { Static: true } && !NotInvocable(n, outerStatic.Type))
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

            MethodSymbol? outerStaticGetter = outer.FindMethods("get_", n.Name)
                                                   .FirstOrDefault(m => m.Static);
            if (outerStaticGetter is not null && !NotInvocable(n, outerStaticGetter.Returns))
            {
                _r.Resolved[n] = new PropertyGetSym(outerStaticGetter);
                return outerStaticGetter.Returns;
            }

            MethodSymbol? outerStaticSetter = outer.FindMethods("set_", n.Name)
                                                   .FirstOrDefault(m => m.Static && m.Params.Count == 1);
            if (outerStaticSetter is not null)
            {
                _r.Resolved[n] = new PropertySetSym(outerStaticSetter);
                return outerStaticSetter.Params[0].Type;
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

        // A GENERIC TYPE NAMED WITH ITS ARGUMENTS, `Comparer<int>` of
        // `Comparer<int>.Default`: resolved as a written type is, which asks for
        // the specialisation. A generic method's copy leaves it so, its type
        // parameters bound only in the copy (Monomorphiser, NameExpr).
        if (n.TypeArgs.Count > 0)
        {
            TypeRef written = new() { Name = n.Name, Line = n.Line, Col = n.Col };
            written.Arguments.AddRange(n.TypeArgs);
            Type made = Resolve(written, _thisType);
            if (!made.IsError && made.Symbol is TypeSymbol generic)
            {
                _r.Resolved[n] = new TypeNameSym(generic);
                return made;
            }
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
            return Type.Plain(root, Prim.Void);
        }
        if ((FindType(n.Name, out TypeSymbol? type) && type is not null)
            || (Alias(n.Name) is string full && _r.Types.TryGetValue(full, out type)))
        {
            _r.Resolved[n] = new TypeNameSym(type);
            return Type.Plain(type, Prim.Void);
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
        // System.Type's statics and the members of a Type the compiler does
        // not answer itself (Name, FullName and the Is* flags it reads from
        // the descriptor): GetTypeFromProgID, GetTypeFromCLSID, Missing,
        // InvokeMember, as String's are String's.
        "Type"   => "SystemType",
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
            || type.FindMethods("get_", name).Any(method => method.Static)
            || type.FindMethods("set_", name).Any(method => method.Static)
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
        // STILL IN A `?.` CHAIN past a call or an indexer: `n?.Self().Label`
        // is null when n is, and its members are lifted and not warned of
        // (C# 12.8.8), as HasConditionalMember walks it for calls.
        bool ConditionalChain(Expr expression) => HasConditionalMember(expression);

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
                    || owner.FindBackingField(name) is not null
                    || owner.FindMethods(name).Count > 0
                    || owner.FindMethods("get_", name).Count > 0))
            || FindConstant(_thisType, name) is not null
            || FindText(_thisType, name) is not null;

        bool NamespaceOnly(Expr expression) => expression switch
        {
            // Namespace identifiers in the compiler's C# sources follow the
            // standard capitalised convention. Requiring that also prevents an
            // as-yet-unmaterialised captured local (`a.Args` inside a lambda)
            // from being mistaken for the namespace-qualified type `Args`.
            // `global::` SAYS NO VALUE IS MEANT: `global::Corsac.Lang.X86`
            // inside a class with a static member called Corsac is the
            // namespace, which is the whole reason the qualifier is written.
            NameExpr n => (n.Global || n.Name.Length > 0 && char.IsUpper(n.Name[0]) && !NamesAValue(n.Name))
                       && !IsTypeName(n.Name),
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
        // UNLESS THE LAST PART OF THE QUALIFIER IS A TYPE WITH THIS MEMBER:
        // `System.Security.Cryptography.HashAlgorithmName.SHA256` is the
        // struct's static property, and with the whole path naming no type in
        // the flat table the qualifier read as a namespace and SHA256 as the
        // class of that name (test 2011). A namespace has no members, so a
        // type that answers the name is what the source meant.
        if (NamespaceOnly(m.Target)
            && !(m.Target is MemberExpr { Name: string last } && _r.Types.TryGetValue(last, out TypeSymbol? lastType)
                 && (lastType.FindField(m.Name) is not null || lastType.FindMethods("get_", m.Name).Count > 0
                     || lastType.FindMethods(m.Name).Count > 0 || FindConstant(lastType, m.Name) is not null
                     || lastType.Kind == TypeKind.Enum && lastType.EnumValues.ContainsKey(m.Name))))
        {
            // THE WHOLE PATH FIRST, because a namespace is real: `Corsac.Lang.Block`
            // and `Corsac.Lang.Ir.Block` differ in nothing else, and reading
            // only the last part is how one becomes the other.
            if (Spelt(m) is string path && FindType(path, out TypeSymbol? byPath) && byPath is not null)
            {
                _r.Resolved[m] = new TypeNameSym(byPath);
                return Type.Plain(byPath, Prim.Void);
            }

            // AND THEN THE LAST PART ALONE, for the namespaces this compiler
            // models as nothing at all: `System.Text.StringBuilder` is a
            // StringBuilder declared in the global namespace here.
            if (_r.Types.TryGetValue(m.Name, out TypeSymbol? qualified))
            {
                _r.Resolved[m] = new TypeNameSym(qualified);
                return Type.Plain(qualified, Prim.Void);
            }
        }

        // The type the target names, when the caller has already settled that
        // the target is a TYPE NAME and not a value -- see CheckMember.
        Type target;

        if (asType is not null)
        {
            _r.Resolved[m.Target] = new TypeNameSym(asType);
            target = Type.Plain(asType, Prim.Void);
        }
        else
        {
            target = CheckExpr(m.Target);
        }

        if (target.IsError)
        {
            return Type.Error;
        }

        // A MEMBER OF A DYNAMIC VALUE is bound when the program runs
        // (Binder.Dynamic).
        if (_usesDynamic && target.Dynamic && asType is null)
        {
            return LateMember(m);
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
            Type asEnum = Type.Plain(shadowed, shadowed.EnumUnderlying);

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
        // A type another unit declares is loaded from its index only when it
        // is asked for by name (FindType), so the nested one is asked too.
        TypeSymbol? nested = null;
        // By its two parts first (TryGetWithin), and spelt once for FindType
        // only when the table has no such type: `Opcode.Add`, a member of a
        // type and no type at all, spelt its path twice at every mention.
        if (_r.Resolved.TryGetValue(m.Target, out Sym? qualifier) && qualifier is TypeNameSym holder
            && (_r.Types.TryGetWithin(holder.Symbol.Key, m.Name, out nested)
                || FindType(holder.Symbol.Key + "." + m.Name, out nested) && nested is not null))
        {
            _r.Resolved[m] = new TypeNameSym(nested);
            return Type.Plain(nested, Prim.Void);
        }

        // AND THROUGH A COPY OF A GENERIC ONE: `Chunks<int>.Enumerator` is the
        // copy of the nested template `Chunks.Enumerator` made over the outer's
        // arguments, since a copy's key is a unique top-level name with no
        // nested path under it.
        if (qualifier is TypeNameSym { Symbol.Decl: { Specialised: true, Template: string outerTemplate } outerMade } copyHolder
            && CopyOver(outerTemplate + "." + m.Name, outerMade, copyHolder.Symbol, inner: true) is TypeSymbol nestedCopy)
        {
            _r.Resolved[m] = new TypeNameSym(nestedCopy);
            return Type.Plain(nestedCopy, Prim.Void);
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
        // Not of a NULLABLE tuple, which is a Nullable<ValueTuple>: its
        // members are HasValue and Value, and `x?.Length` reads the item of
        // the value inside, below.
        if (!target.IsNullableValue && target.Symbol is TypeSymbol shaped && shaped.TupleNamings.Count > 0)
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
            // Nor are names carried through a generic's delegate always this
            // use's: `list.RemoveAll(w => w.Call ...)` over a List of
            // `(Block Block, Instr Call)` handed its lambda the names another
            // `(Block, Instr)` list was declared with. A name the carried
            // naming lacks is asked of every naming the same way.
            if (which < 0)
            {
                which = shaped.TupleElement(m.Name);
            }

            if (which >= 0 && which < shaped.Fields.Count)
            {
                RequireNonNull(target, m.Target, "read");
                _r.Resolved[m] = new FieldSym(shaped.Fields[which]);
                return TupleItem(target, shaped, which);
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
                            Name = cell.Name, Arguments = cell.Args, ArrayRank = cell.ArrayRank,
                            PointerDepth = cell.PointerDepth, TupleNames = cell.TupleNames,
                            Nullable = true, Line = m.Line, Col = m.Col,
                        },
                        Operand = instead.Then, Line = m.Line, Col = m.Col,
                    };
                }

                _r.Rewrites[m] = once;
                return CheckExpr(once);
            }

            // A PROPERTY PATTERN READS THE VALUE'S MEMBER, as C# reads it:
            // `fast is { List: not null }` over a (T, U)? matches on the
            // tuple's List once the pattern's own null test -- to its left,
            // which is what Guarded says -- has found a value there. The
            // subject is the pattern's hoisted one, read again for nothing.
            if (m.Guarded && !m.NullConditional && !ReferenceEquals(m, _callee))
            {
                MemberExpr inside = new()
                {
                    Target = new MemberExpr { Target = m.Target, Name = "Value", Guarded = true, Line = m.Line, Col = m.Col },
                    Name = m.Name, Guarded = true, Line = m.Line, Col = m.Col,
                };
                _r.Rewrites[m] = inside;
                return CheckExpr(inside);
            }

            // ITS METHODS, when this is the one being called: the call writes
            // them out over HasValue and Value (NullableMemberCall).
            if (ReferenceEquals(m, _callee) && m.Name is "GetValueOrDefault" or "ToString" or "GetHashCode" or "Equals")
            {
                _cellMethods[m] = target;
                return Type.Void;
            }

            Error(m, $"'{target}' has no member '{m.Name}'; a nullable value type has 'HasValue', 'Value', "
                   + "GetValueOrDefault, ToString, GetHashCode and Equals");
            return Type.Error;
        }

        // A TUPLE OF EIGHT OR MORE HAS Rest, as ValueTuple`8 does: the items
        // from the eighth on, a tuple of their own -- whose own Rest goes on
        // from the fifteenth. Held flat here; read as .NET's nesting reads it.
        if (m.Name == "Rest" && !m.NullConditional && TupleArity(target) is int count && count >= 8)
        {
            TupleExpr rest = new() { Line = m.Line, Col = m.Col, File = m.File };
            for (int i = 8; i <= count; i++)
            {
                rest.Items.Add(new MemberExpr { Target = new SubjectExpr { Line = m.Line, Col = m.Col }, Name = "Item" + i, Line = m.Line, Col = m.Col, File = m.File });
            }
            PatternExpr taken = new() { Subject = m.Target, Test = rest, Line = m.Line, Col = m.Col, File = m.File };
            _r.Rewrites[m] = taken;
            return CheckExpr(taken);
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
            // Lifted in a `?.` chain, as any member there is: `s?.Length` and
            // `n?.Self().Label.Length` are int? (null when the chain is).
            if (m.Name == "Length" && (target.IsArray || target.Prim == Prim.String))
            {
                return conditional ? Type.I32.AsNullable() : Type.I32;
            }

            // AN ARRAY'S LENGTH AS A LONG (Array.LongLength): the same count,
            // widened, as .NET has it for an array of any size.
            if (m.Name == "LongLength" && target.IsArray)
            {
                return conditional ? Type.I64.AsNullable() : Type.I64;
            }

            // A TYPE'S NAME, which is the whole of reflection tier 1's surface
            // alongside comparing two of them. Read straight out of the
            // descriptor the code generator put in front of the vtable.
            if (m.Name is "Name" or "FullName" && target.Prim == Prim.Type)
            {
                return Type.String;
            }

            // WHAT KIND OF TYPE IT IS, read from the flags of the same
            // descriptor (Lowering.IntrinsicMember).
            if (m.Name is "IsValueType" or "IsEnum" or "IsInterface" or "IsPrimitive" or "IsArray" or "IsClass" && target.Prim == Prim.Type)
            {
                return Type.Bool;
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
                                Target = new NameExpr { Name = "System", Global = true, Line = m.Line, Col = m.Col },
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
            // A REFERENCE'S `?` IS NOT ANOTHER TYPE: `s.Split` on a string? is
            // String's Split, with the warning already given above that s may
            // be null. Spelt with its `?`, the name matched nothing and a
            // warning became an error.
            Type unmarked = target.Nullable && target.IsReference ? target.AsNonNullable() : target;
            if (Alias(unmarked.ToString()) is { } primitiveName
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

        FieldSymbol? field = owner.FindField(m.Name) ?? owner.FindBackingField(m.Name);

        if (field != null)
        {
            FieldSym read = new(field);
            _r.Resolved[m] = read;
            Type fieldType = Close(ContextualFieldResult(target, field), received);
            // `t.Item2` of an `(int, Box?)` is the use's Box?.
            if (owner.Structural && owner.Fields.IndexOf(field) is int item and >= 0 && !field.Static)
                fieldType = TupleItem(target, owner, item);
            if (!fieldType.IsNullableValue
                && ((Path(m) is string path && _notNullPaths.Contains(path)) || _notNull.Contains(read)))
            {
                fieldType = fieldType.AsNonNullable();
            }
            return conditional ? fieldType.AsNullable() : fieldType;
        }

        List<MethodSymbol> group = MethodsOn(owner, m.Name);

        // OBJECT'S OWN STAY IN THE GROUP beside a type's overloads of the
        // name, as C#'s member lookup has them (12.5): a record that writes
        // Equals(LocalSym) still answers Equals(object), and with only its
        // own in the group `a.Equals((object)f)` had nowhere to go but the
        // typed one. Not where the type has one of the same parameters -- its
        // override, which is the one called.
        if (group.Count > 0 && owner.Kind is TypeKind.Class or TypeKind.Interface or TypeKind.Struct
            && m.Name is "Equals" or "GetHashCode" or "ToString" or "GetType"
            && Rooted().FindMethods(m.Name) is { Count: > 0 } rootMethods)
        {
            List<MethodSymbol>? widened = null;
            foreach (MethodSymbol root in rootMethods)
            {
                if (root.Static) continue;
                bool overridden = group.Any(had => !had.Static && had.Params.Count == root.Params.Count
                    && had.Params.Zip(root.Params).All(pair => MethodSignatures.SameType(pair.First.Type, pair.Second.Type)));
                if (!overridden) (widened ??= new List<MethodSymbol>(group)).Add(root);
            }
            if (widened is not null) group = widened;
        }

        if (group.Count > 0)
        {
            _r.Resolved[m] = new MethodGroupSym(group);
            return Type.Void;
        }

        MethodSymbol? getter = MethodsOn(owner, "get_", m.Name).FirstOrDefault();

        if (getter != null)
        {
            _r.Resolved[m] = new PropertyGetSym(getter);
            Type read = Close(ContextualMemberResult(target, getter), received);
            return conditional ? read.AsNullable() : read;
        }

        // A SET-ONLY PROPERTY: assigned, never read (PropertySetSym).
        if (MethodsOn(owner, "set_", m.Name).FirstOrDefault(s => s.Params.Count == 1) is { } onlySetter)
        {
            _r.Resolved[m] = new PropertySetSym(onlySetter);
            return Close(onlySetter.Params[0].Type, received);
        }

        // WHAT EVERY OBJECT ANSWERS. Every type derives from object, so
        // ToString, Equals, GetHashCode and GetType are on it whether or not it
        // declared one -- and a class that declares none reached nothing at
        // all. `operand.ToString()` on an abstract MOperand is an ordinary line
        // and was reported as a member the type does not have.
        //
        // Before the extensions, because these are real members and an
        // extension must never shadow one.
        // A STRUCT TOO: it derives from ValueType, whose Equals, GetHashCode
        // and ToString answer over its fields -- the boxed value's slots,
        // which is what a call on one becomes. `pair.Equals(other)` on a
        // struct that declares none was 'no member Equals'.
        // Unless the call has an argument count none of object's instance
        // members takes: C# (12.8.10.3) tries the extensions when no instance
        // method applies, and `span.Equals(other, StringComparison.Ordinal)`
        // is MemoryExtensions.Equals, where object's static Equals(a, b) took
        // the two arguments and the receiver was dropped.
        if (owner.Kind is TypeKind.Class or TypeKind.Interface or TypeKind.Struct
            && Rooted().FindMethods(m.Name) is { Count: > 0 } inherited
            && !(ReferenceEquals(m, _callee) && _calleeArgs >= 0
                && !inherited.Any(root => !root.Static && root.Params.Count == _calleeArgs)
                && Extension(target, m.Name) is { Count: > 0 }))
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

        // AN EVENT WITH NO FIELD -- an interface's, an abstract one, or one
        // written with its own add and remove -- is its two accessors and
        // nothing that can be read or raised (C#'s CS0079); += and -= reach
        // the accessors before any of this (EventAccessorCall).
        if (MethodsOn(owner, "add_" + m.Name).Count > 0 && MethodsOn(owner, "remove_" + m.Name).Count > 0)
        {
            Error(m, $"the event '{owner.Name}.{m.Name}' can only appear on the left hand side of += or -=");
            return Type.Error;
        }

        Error(m, $"'{owner.Name}' has no member '{m.Name}'");
        return Type.Error;
    }

    /// <summary>
    /// Whether the code being checked can see member `m` as C# lets it: a
    /// public or internal one anywhere; a private one -- written so, or by
    /// default in a class or struct -- only inside its own type or a type
    /// nested in it; a protected one there or in a type derived from it.
    /// </summary>
    private bool Visible(MethodSymbol m)
    {
        if (m.Decl is not MethodDecl d || m.Owner is not TypeSymbol owner || owner.Kind == TypeKind.Interface) return true;
        Mods mods = d.Mods;
        if ((mods & (Mods.Public | Mods.Internal)) != 0) return true;
        TypeSymbol? here = _thisType is not null && IsClosure(_thisType) ? _capturedThisType ?? _lexicalType : _thisType;
        if (here is null) return true;
        if (Inside(here, owner)) return true;
        return (mods & Mods.Protected) != 0 && here.DerivesFrom(owner);

        // Within: the type itself, or one written inside it, however deep.
        bool Inside(TypeSymbol t, TypeSymbol of)
        {
            if (ReferenceEquals(t, of)) return true;
            string ofPath = of.Decl is { Outer: string o } ? o + "." + of.Decl.Name : of.Decl?.Name ?? of.Name;
            for (string? at = t.Decl?.Outer; at is not null; at = at.Contains('.') ? at[..at.LastIndexOf('.')] : null)
                if (at == ofPath || at.EndsWith("." + ofPath, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// The constructor a `new` -- or a constructor's `: base(...)` or
    /// `: this(...)`, checked as one -- calls, with its arguments put in
    /// parameter order, defaults and caller information filled in, a `params`
    /// tail packed, and each argument settled to its parameter's type. Null,
    /// with an error, when none accepts them; <paramref name="except"/> is a
    /// constructor that may not be chosen, the one a `: this(...)` is on.
    /// </summary>
    private MethodSymbol? ResolveConstructor(NewExpr nw, TypeSymbol constructed, List<Type> constructorArgs, MethodDecl? except = null, Type? made = null)
    {
        List<MethodSymbol> methods = except is null ? constructed.Methods
            : constructed.Methods.Where(m => !ReferenceEquals(m.Decl, except)).ToList();
        // ONLY THE CONSTRUCTORS THIS CODE CAN SEE are candidates (C#
        // 12.6.4.2): Thread's own `Thread(Action body, int slot)` took
        // `new Thread(() => ..., 64 * 1024 * 1024)` from a program, a slot
        // number for a stack size. Where none can be seen the set stands, for
        // library code that has always reached what it should not.
        List<MethodSymbol> visible = methods.Where(m => !m.IsCtor || Visible(m)).ToList();
        if (visible.Any(m => m.IsCtor)) methods = visible;
        NormalizeConstructorArguments(nw, methods, constructorArgs);
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
        if (!methods.Any(m => m.IsCtor && m.Params.Count == constructorArgs.Count
                                       && constructorArgs.Where((a, i) =>
                                            !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0))
        {
            MethodSymbol? variadic = methods.FirstOrDefault(
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
        if (!methods.Any(m => m.IsCtor && m.Params.Count == constructorArgs.Count
            && constructorArgs.Where((a, i) => !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0))
        {
            MethodSymbol? shorter = methods.FirstOrDefault(
                m => m.IsCtor && m.Params.Count > constructorArgs.Count
                  && constructorArgs.Where((a, i) => !Fits(a, m.Params[i].Type, nw.Args[i])).Count() == 0
                  && Enumerable.Range(constructorArgs.Count,
                                      m.Params.Count - constructorArgs.Count)
                               .All(i => m.Decl?.Params.ElementAtOrDefault(i)?.Default is not null));

            if (shorter != null)
            {
                int writtenCount = constructorArgs.Count;
                for (int i = constructorArgs.Count; i < shorter.Params.Count; i++)
                {
                    Expr fallback = CallerValue(shorter.Decl!.Params[i], shorter.Decl.Params, nw.Line,
                                        k => k >= writtenCount ? null : SpanText(nw.Spans, nw.Source, k + 1))
                                    ?? Written(shorter, shorter.Decl.Params[i]);

                    nw.Args.Add(fallback);
                    Type? outerWanted = _wanted;
                    _wanted = shorter.Params[i].Type;
                    constructorArgs.Add(CheckExpr(fallback));
                    _wanted = outerWanted;
                }
            }
        }

        List<MethodSymbol> ctors = methods
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
            for (int i = 0; i < constructorArgs.Count; i++)
            {
                if (nw.Args[i] is LambdaExpr lambda)
                {
                    constructorArgs[i] = CheckLambda(lambda, ctor.Params[i].Type);
                    continue;
                }
                if (MethodGroupLambda(nw.Args[i], ctor.Params[i].Type, localFunctions: true) is LambdaExpr wrapper)
                {
                    _r.Rewrites[nw.Args[i]] = wrapper;
                    constructorArgs[i] = CheckLambda(wrapper, ctor.Params[i].Type);
                    continue;
                }
                if (nw.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null } || HoldsLambda(nw.Args[i]))
                {
                    Type? saved = _wanted;
                    _wanted = ctor.Params[i].Type;
                    constructorArgs[i] = CheckExpr(nw.Args[i]);
                    _wanted = saved;
                }
                // `new List<Node?>(...)` takes what the use says, `?` and all.
                Type want = made is null ? ctor.Params[i].Type : ContextualParameterType(made, ctor, i);
                constructorArgs[i] = Settle(nw.Args[i], want, constructorArgs[i]);
                CheckAssignable(constructorArgs[i], want, nw.Args[i], $"constructor argument {i + 1}");
            }
        }
        // A STRUCT'S `new S()` NEEDS NO CONSTRUCTOR: every struct has the
        // parameterless one C# gives it, which is its zero -- whatever others
        // it declares.
        else if (constructed.Kind == TypeKind.Struct && constructorArgs.Count == 0)
        {
        }
        else if (methods.Any(m => m.IsCtor))
        {
            // NOTHING TO CALL IS AN ERROR, and was silence: the
            // object was allocated, no constructor ran, and every
            // field held its zero.
            Error(nw, $"no constructor of '{constructed.Name}' accepts "
                    + $"({string.Join(", ", constructorArgs)})");
        }
        return ctor;
    }

    /// <summary>
    /// What a caller-information attribute on a parameter the call left out
    /// passes, or null when it has none or there is nothing to pass -- a
    /// [CallerArgumentExpression] whose argument was not written, a
    /// [CallerMemberName] outside any member -- and the default is used.
    /// <paramref name="textOf"/> gives the text written for a parameter's
    /// argument, by the parameter's index.
    /// </summary>
    private Expr? CallerValue(Param p, IReadOnlyList<Param> parameters, int line, Func<int, string?> textOf)
    {
        string? text;
        switch (p.Caller)
        {
            case CallerInfo.LineNumber:
                return new LiteralExpr { Kind = Lit.Int, Text = line.ToString(), IntValue = line, Line = line, Col = p.Col };
            case CallerInfo.FilePath:
                text = Where() is { Length: > 0 } file ? System.IO.Path.GetFullPath(file) : null;
                break;
            case CallerInfo.MemberName:
                text = CallerMemberName();
                break;
            case CallerInfo.ArgumentExpression:
                text = null;
                for (int i = 0; i < parameters.Count; i++)
                    if (parameters[i].Name == p.CallerArgument) { text = textOf(i); break; }
                break;
            default:
                return null;
        }
        return text is null ? null : new LiteralExpr { Kind = Lit.Str, Text = text, Line = line, Col = p.Col };
    }

    /// <summary>
    /// The member a call is written in, named as [CallerMemberName] names it:
    /// a method's name; the property's or event's for one of its accessors
    /// (an indexer's is "Item"); ".ctor" and ".cctor" for constructors; a
    /// field's or property's for its initialiser. A lambda or local function
    /// is part of the member it is written in. Null outside any member.
    /// </summary>
    private string? CallerMemberName()
    {
        switch (_member)
        {
            case null:
                return null;
            case MethodDecl { IsCtor: true } ctor:
                return ctor.Mods.HasFlag(Mods.Static) ? ".cctor" : ".ctor";
            case MethodDecl method:
                foreach (string prefix in new[] { "get_", "set_", "add_", "remove_" })
                {
                    if (!method.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string owner = method.Name[prefix.Length..];
                    if ((_lexicalType ?? _thisType)?.Decl?.Members.Any(m => m is not MethodDecl && m.Name == owner) == true) return owner;
                }
                return method.Name;
            default:
                return _member.Name;
        }
    }

    /// <summary>The text of span pair <paramref name="pair"/> -- 0 the receiver, then each argument as written -- or null.</summary>
    private static string? SpanText(int[]? spans, string? source, int pair)
        => spans is null || source is null || pair < 0 || 2 * pair + 1 >= spans.Length || spans[2 * pair] < 0
            ? null : source[spans[2 * pair]..spans[2 * pair + 1]];

    /// <summary>How many arguments in front of a call are a generic local function's captured variables (PassCaptures).</summary>
    private static int Hidden(CallExpr c, MethodSymbol m)
        => c.CapturesPassed && m.Decl is MethodDecl { Captures: > 0 } d ? d.Captures : 0;

    /// <summary>The line [CallerLineNumber] gives a call: where the method's name is.</summary>
    private static int CallLine(CallExpr c) => c.Target is NameExpr or MemberExpr ? c.Target.Line : c.Line;

    /// <summary>
    /// NULLABLE&lt;T&gt;'S METHODS, as .NET declares them: GetValueOrDefault()
    /// and GetValueOrDefault(T), and the three object members -- ToString is
    /// "" and GetHashCode 0 when there is no value, and Equals(object) is true
    /// of an empty cell only against null. Nothing declares them here, since a
    /// cell is no class, so each is written out over HasValue and Value with
    /// the receiver evaluated once, and GetValueOrDefault's argument evaluated
    /// after it and always, as an argument is. Null when the receiver is not
    /// a nullable value type or the call is not one of these.
    /// </summary>
    private Expr? NullableMemberCall(CallExpr c, MemberExpr m, Type on)
    {
        int wanted = m.Name switch { "Equals" => 1, "GetValueOrDefault" => c.Args.Count is 0 or 1 ? c.Args.Count : -1, _ => 0 };
        // The one argument may be named as .NET names it.
        string? named = c.ArgNames.Count > 0 ? c.ArgNames[0] : null;
        if (c.Args.Count != wanted || m.TypeArgs.Count > 0
            || (named is not null && named != (m.Name == "Equals" ? "other" : "defaultValue"))) return null;
        c.WritableArgNames.Clear();
        if (!on.IsNullableValue || RefOf(on.Underlying) is not TypeRef inner) return null;

        int line = m.Line, col = m.Col;
        MemberExpr Has(int outer) => new() { Target = new SubjectExpr { Outer = outer, Line = line, Col = col }, Name = "HasValue", Guarded = true, Line = line, Col = col };
        MemberExpr Value(int outer) => new() { Target = new SubjectExpr { Outer = outer, Line = line, Col = col }, Name = "Value", Guarded = true, Line = line, Col = col };
        Expr Called(string name, params Expr[] args)
        {
            CallExpr call = new() { Target = new MemberExpr { Target = Value(0), Name = name, Line = line, Col = col }, Line = line, Col = col };
            call.Args.AddRange(args);
            return call;
        }

        Expr test;
        switch (m.Name)
        {
            case "GetValueOrDefault" when c.Args.Count == 0:
                test = new ConditionalExpr { Cond = Has(0), Then = Value(0), Else = new DefaultExpr { Type = inner, Line = line, Col = col }, Line = line, Col = col };
                break;

            case "GetValueOrDefault":
            {
                // The argument converts to T as an argument would; the cast
                // below only says so to the conditional, which would
                // otherwise widen `b.GetValueOrDefault(5)` on a byte? to int.
                Type given = _r.TypeOf(c.Args[0]);
                CheckAssignable(given, on.Underlying, c.Args[0], "argument 1 of 'GetValueOrDefault'");
                ConditionalExpr pick = new()
                {
                    Cond = Has(1), Then = Value(1),
                    Else = new CastExpr { Type = inner, Operand = new SubjectExpr { Line = line, Col = col }, Line = line, Col = col },
                    Line = line, Col = col,
                };
                test = new PatternExpr { Subject = c.Args[0], Test = pick, Line = line, Col = col };
                break;
            }

            case "ToString":
                test = new ConditionalExpr { Cond = Has(0), Then = Called("ToString"), Else = new LiteralExpr { Kind = Lit.Str, Text = "", Line = line, Col = col }, Line = line, Col = col };
                break;

            case "GetHashCode":
                test = new ConditionalExpr { Cond = Has(0), Then = Called("GetHashCode"), Else = new LiteralExpr { Kind = Lit.Int, Text = "0", IntValue = 0, Line = line, Col = col }, Line = line, Col = col };
                break;

            default:
            {
                // Equals(object): the other side is an object, so a value
                // there is boxed and is never null.
                Expr other = new CastExpr { Type = new TypeRef { Name = "object", Line = line, Col = col }, Operand = c.Args[0], Line = line, Col = col };
                test = new ConditionalExpr
                {
                    Cond = Has(0), Then = Called("Equals", other),
                    Else = new BinaryExpr { Op = BinOp.Eq, Left = other, Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = line, Col = col }, Line = line, Col = col },
                    Line = line, Col = col,
                };
                break;
            }
        }
        return new PatternExpr { Subject = m.Target, Test = test, Line = c.Line, Col = c.Col };
    }

    /// <summary>
    /// `{value:format}` (Parser.Formatted) as C#'s interpolation formats it:
    /// the call kept where the value's type takes a format -- a number's
    /// ToString(format), a type's own ToString(string) -- or made
    /// ToString(format, null) on an IFormattable; for a `T?`, its value's,
    /// and "" when there is none; and anything else as it is, the format
    /// ignored. Null to keep the call as written.
    /// </summary>
    private Expr? FormatHole(CallExpr c, MemberExpr hole, Type value)
    {
        int line = c.Line, col = c.Col;
        if (value.IsError) return null;
        if (value.IsNullableValue)
        {
            CallExpr inner = new()
            {
                Target = new MemberExpr
                {
                    Target = new MemberExpr { Target = new SubjectExpr { Line = line, Col = col }, Name = "Value", Guarded = true, Line = line, Col = col },
                    Name = "ToString", Line = line, Col = col,
                },
                FormatHole = true, Line = line, Col = col,
            };
            inner.Args.AddRange(c.Args);
            inner.WritableArgNames.Add(null);
            return new PatternExpr
            {
                Subject = hole.Target,
                Test = new ConditionalExpr
                {
                    Cond = new MemberExpr { Target = new SubjectExpr { Line = line, Col = col }, Name = "HasValue", Guarded = true, Line = line, Col = col },
                    Then = inner,
                    Else = new LiteralExpr { Kind = Lit.Str, Text = "", Line = line, Col = col },
                    Line = line, Col = col,
                },
                Line = line, Col = col,
            };
        }

        bool TakesFormat(MethodSymbol m, int at) => m.Params.Count == at + 1 && m.Params[at].Type.Prim == Prim.String;
        if (Alias(value.ToString()) is { } primitive && _r.Types.TryGetValue(primitive, out TypeSymbol? numbers)
            && numbers.FindMethods("ToString").Any(m => m.Static && TakesFormat(m, 1) && m.Params[0].Type.Prim == value.Prim))
            return null;
        if (value.Symbol is TypeSymbol shape)
        {
            if (Reachable(shape, "ToString").Any(m => !m.Static && TakesFormat(m, 0))) return null;
            if (AllInterfaces(shape).Any(i => i.Name == "IFormattable"))
            {
                CallExpr formattable = new()
                {
                    Target = new MemberExpr { Target = hole.Target, Name = "ToString", Line = line, Col = col },
                    Line = line, Col = col,
                };
                formattable.Args.AddRange(c.Args);
                formattable.Args.Add(new LiteralExpr { Kind = Lit.Null, Text = "null", Line = line, Col = col });
                return formattable;
            }
        }
        return hole.Target;
    }

    /// <summary>
    /// The static method `&name` or `&Type.name` takes the address of, or
    /// null when the operand is not a method group (a variable is taken the
    /// ordinary way). One method of the name, as C# requires when nothing
    /// says which overload is meant.
    /// </summary>
    private MethodSymbol? AddressedMethod(Expr operand)
    {
        List<MethodSymbol> found = new();
        if (operand is NameExpr name && Lookup(name.Name) is null && _thisType is not null)
        {
            found.AddRange(_thisType.FindMethods(name.Name).Where(m => m.Static));
        }
        else if (operand is MemberExpr member && ConstantOwner(member.Target) is TypeSymbol owner)
        {
            found.AddRange(owner.FindMethods(member.Name).Where(m => m.Static));
        }
        if (found.Count == 0)
        {
            return null;
        }
        if (found.Count > 1)
        {
            Error(operand, $"'&{(operand as NameExpr)?.Name ?? (operand as MemberExpr)?.Name}' names {found.Count} methods; give the one meant a name of its own");
        }
        return found[0];
    }

    /// <summary>The function pointer a call is made through, when its target is a variable or field holding one.</summary>
    private FunctionPointer? CalledPointer(Expr target)
    {
        switch (target)
        {
            case NameExpr name when Lookup(name.Name) is not null:
            // A FIELD ONLY WHEN IT HOLDS A POINTER: checking any other field
            // named like the call here bound the name before CheckCall had
            // marked it invoked, so `Fields(path)` in a nested class with a
            // table called Fields was refused instead of reaching the outer
            // class's method (C# passes over what cannot be invoked).
            case NameExpr field when _thisType?.FindField(field.Name) is { Type.Function: not null } && _thisType.FindMethods(field.Name).Count == 0:
            case CastExpr:
                return CheckExpr(target).Function;
            case MemberExpr member when member.Target is not null && ConstantOwner(member.Target) is TypeSymbol owner
                && owner.FindField(member.Name) is { Type.Function: not null } && owner.FindMethods(member.Name).Count == 0:
                return CheckExpr(target).Function;
            default:
                return null;
        }
    }

    /// <summary>An empty method list, read and never written, for a parameter type with no Invoke.</summary>
    private static readonly List<MethodSymbol> NoMethods = new();

    /// <summary>Whether any argument was passed by name.</summary>
    private static bool AnyNamed(List<string?> names)
    {
        foreach (string? name in names)
        {
            if (name != null) return true;
        }
        return false;
    }

    private Type CheckCall(CallExpr c)
    {
        if (c.FormatHole && c.Target is MemberExpr { Name: "ToString" } hole && c.Args.Count == 1)
        {
            _quiet++;
            Type formatted = CheckExpr(hole.Target);
            _quiet--;
            if (FormatHole(c, hole, formatted) is Expr formatting)
            {
                _r.Rewrites[c] = formatting;
                return CheckExpr(formatting);
            }
        }

        // A CALL THROUGH A FUNCTION POINTER: `compare(a, b)` where compare is
        // a delegate*. The arguments are converted to its parameters, as a
        // method's are; the call itself is made by the pointer's address.
        if (CalledPointer(c.Target) is FunctionPointer pointer)
        {
            if (c.Args.Count != pointer.Params.Count)
            {
                Error(c, $"the function pointer takes {pointer.Params.Count} argument(s), not {c.Args.Count}");
                return Type.Error;
            }
            for (int i = 0; i < c.Args.Count; i++)
            {
                Type given = CheckExpr(c.Args[i]);
                if (!given.IsError && !Convertible(given, pointer.Params[i]))
                {
                    Error(c.Args[i], $"argument {i + 1}: a '{given}' is not a '{pointer.Params[i]}'");
                }
            }
            _r.PointerCalls[c] = pointer;
            return pointer.Returns;
        }

        // Activator.CreateInstance<T>() IS `new T()` of the T the copy knows
        // (stdlib Activator): its constructor called directly, which every
        // analysis sees, or CannotCreate's throw where T has none to call.
        if (c.Args.Count == 0 && c.Target is MemberExpr { Name: "CreateInstance", TypeArgs: [TypeRef madeRef], Target: NameExpr { Name: "Activator", TypeArgs.Count: 0 } }
            && Lookup("Activator") is null && FindType("Activator", out TypeSymbol? activator)
            && activator?.FindMethods("CannotCreate").Count > 0
            && ActivatorMade(c, madeRef) is Expr activated)
        {
            _r.Rewrites[c] = activated;
            return CheckExpr(activated);
        }

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
                    OpensCell = true,
                    Line = c.Line,
                    Col = c.Col,
                },
                Name = conditionalTarget.Name,
                Guarded = true,
                Line = conditionalTarget.Line,
                Col = conditionalTarget.Col,
            };
            safeTarget.WritableTypeArgs.AddRange(conditionalTarget.TypeArgs);

            CallExpr safeCall = new()
            {
                Target = safeTarget,
                Line = c.Line,
                Col = c.Col,
            };
            safeCall.Args.AddRange(c.Args);
            safeCall.WritableArgNames.AddRange(c.ArgNames);
            safeCall.Spans = c.Spans;
            safeCall.Source = c.Source;

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

        // AN ENUM'S Equals AND GetHashCode are System.Enum's: the value boxed,
        // compared by type and value, hashed as its underlying number -- which
        // is what a boxed enum's own slots answer here.
        if (c.Target is MemberExpr { Name: "Equals" or "GetHashCode" } enumCall
            && (enumCall.Name == "Equals" ? c.Args.Count == 1 : c.Args.Count == 0)
            && !_r.Rewrites.ContainsKey(c)
            && Peek(enumCall.Target).AsNonNullable() is { Symbol: { Kind: TypeKind.Enum } enumType } enumValue
            && !enumValue.IsNullableValue && enumType.FindMethods(enumCall.Name).Count == 0)
        {
            CallExpr boxed = new()
            {
                Target = new MemberExpr
                {
                    Target = new CastExpr
                    {
                        Type = new TypeRef { Name = "object", Line = c.Line, Col = c.Col },
                        Operand = enumCall.Target, Line = c.Line, Col = c.Col,
                    },
                    Name = enumCall.Name, Line = enumCall.Line, Col = enumCall.Col,
                },
                Line = c.Line, Col = c.Col,
            };
            boxed.Args.AddRange(c.Args);
            boxed.WritableArgNames.AddRange(c.ArgNames);
            _r.Rewrites[c] = boxed;
            return CheckExpr(boxed);
        }

        // AN ENUM'S CompareTo is System.Enum's: the underlying numbers
        // compared. Given another value of the same enum, it is that.
        if (c.Target is MemberExpr { Name: "CompareTo" } compareCall && c.Args.Count == 1
            && CheckExpr(compareCall.Target).AsNonNullable() is { Symbol: { Kind: TypeKind.Enum } compared } comparedValue
            && !comparedValue.IsNullableValue && compared.FindMethods("CompareTo").Count == 0
            && CheckExpr(c.Args[0]).Equals(comparedValue)
            && compared.EnumUnderlying switch
            {
                Prim.I8 => "sbyte", Prim.U8 => "byte", Prim.I16 => "short", Prim.U16 => "ushort",
                Prim.I32 => "int", Prim.U32 => "uint", Prim.I64 => "long", Prim.U64 => "ulong",
                _ => null,
            } is string underlying)
        {
            TypeRef number = new() { Name = underlying, Line = c.Line, Col = c.Col };
            CallExpr numbers = new()
            {
                Target = new MemberExpr
                {
                    Target = new CastExpr { Type = number, Operand = compareCall.Target, Line = c.Line, Col = c.Col },
                    Name = "CompareTo", Line = compareCall.Line, Col = compareCall.Col,
                },
                Line = c.Line, Col = c.Col,
            };
            numbers.Args.Add(new CastExpr
            {
                Type = new TypeRef { Name = underlying, Line = c.Line, Col = c.Col }, Operand = c.Args[0], Line = c.Line, Col = c.Col,
            });
            numbers.WritableArgNames.AddRange(c.ArgNames);
            _r.Rewrites[c] = numbers;
            return CheckExpr(numbers);
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
        // AN ARRAY IS AN OBJECT, and answers object's ToString, Equals and
        // GetHashCode (C#'s System.Array inherits them): asked of the array as
        // an object, through the slots every object has.
        if (c.Target is MemberExpr { Name: "ToString" or "Equals" or "GetHashCode" } ofArray && !_r.Rewrites.ContainsKey(c)
            && !ofArray.NullConditional && ofArray.Target is not BaseExpr)
        {
            _quiet++;
            Type arrayReceiver;
            try { arrayReceiver = CheckExpr(ofArray.Target); }
            finally { _quiet--; }
            if (arrayReceiver.IsArray && !arrayReceiver.IsError)
            {
                CallExpr asObject = new()
                {
                    Target = new MemberExpr
                    {
                        Target = new CastExpr { Type = new TypeRef { Name = "object", Line = ofArray.Line, Col = ofArray.Col }, Operand = ofArray.Target, Line = ofArray.Line, Col = ofArray.Col },
                        Name = ofArray.Name, Line = ofArray.Line, Col = ofArray.Col,
                    },
                    Line = c.Line, Col = c.Col,
                };
                asObject.Args.AddRange(c.Args);
                _r.Rewrites[c] = asObject;
                return CheckExpr(asObject);
            }
        }

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

            // A VALUE IS BOXED AND THE BOX IS ASKED, which is what C# does:
            // GetType is object's, not the value type's, so `5.GetType()` is
            // `((object)5).GetType()` and says System.Int32. A `T?` boxes to
            // its value or to null, and null throws, as it does in .NET.
            Type held = on.AsNonNullable();
            if (!on.IsError && !on.IsPointer && on.ParamName is null
                && (held.Symbol is { Kind: TypeKind.Enum or TypeKind.Struct } || held.IsNumeric || held.Prim is Prim.Bool or Prim.Char))
            {
                CallExpr boxed = new()
                {
                    Target = new MemberExpr
                    {
                        Target = new CastExpr { Type = new TypeRef { Name = "object", Line = asked.Line, Col = asked.Col }, Operand = asked.Target, Line = asked.Line, Col = asked.Col },
                        Name = "GetType", Line = asked.Line, Col = asked.Col,
                    },
                    Line = c.Line, Col = c.Col,
                };
                _r.Rewrites[c] = boxed;
                return CheckExpr(boxed);
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
            // A GENERIC LOCAL FUNCTION BY ITS TEMPLATE, however its name is
            // reached: inside a lambda or another local function it is the
            // closure's group (CapturedMethodGroupSym), not the symbol its
            // block declared, and its calls there took no captures.
            // A ROUND LATER THE CALL NAMES THE COPY made for it, which no
            // scope declares: it is a method of the type the function was
            // hoisted into, this one or, inside a closure, the one it captured.
            if (localTarget is null && localName.Name.Contains('$'))
            {
                List<MethodSymbol> copies = _thisType?.FindMethods(localName.Name) ?? new();
                if (copies.Count == 0 && _capturedThisType is not null) copies = _capturedThisType.FindMethods(localName.Name);
                if (copies.Count > 0) localTarget = new MethodGroupSym(copies);
            }
            if (localTarget is not null && GenericLocalTemplate(localTarget) is MethodDecl hoisted)
                PassCaptures(c, hoisted, hoisted.HoistedName ?? localName.Name);
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
        if (AnyNamed(c.ArgNames))
        {
            MemberExpr? outerNamedCallee = _callee;
            NameExpr? outerNamedInvoked = _invokedName;
            int outerNamedArgs = _calleeArgs;
            _callee = c.Target as MemberExpr;
            _invokedName = c.Target as NameExpr;
            _calleeArgs = c.Args.Count;
            CheckExpr(c.Target);
            _callee = outerNamedCallee;
            _invokedName = outerNamedInvoked;
            _calleeArgs = outerNamedArgs;
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

        // A RECEIVER ALREADY MOVED INTO THE ARGUMENTS (a string's method,
        // below) is still the member's target too, and is checked there: a
        // call checked a second time -- the next round, a generic
        // instantiation -- checked it twice, and an `out` or pattern variable
        // inside it was declared twice. A body copied for the next round
        // copies the two separately, so the argument is made the target's
        // node again first: one expression, checked and lowered once.
        Expr? movedReceiver = null;
        if (c.ReceiverAdded && c.Target is MemberExpr moved && c.Args.Count > 0)
        {
            c.Args[0] = moved.Target;
            movedReceiver = moved.Target;
        }
        List<Type> args = new();
        foreach (Expr argument in c.Args)
        {
            if (argument is LambdaExpr or NewExpr { Type.Name.Length: 0, Elements: null }
                || ReferenceEquals(argument, movedReceiver) || HoldsLambda(argument))
            {
                args.Add(Type.Any);
                continue;
            }

            Type argumentType = CheckExpr(argument);
            args.Add(IsFunctionSource(argument) ? Type.Any : argumentType);
        }
        _wanted = outerTarget;

        MemberExpr? outerCallee = _callee;
        NameExpr? outerInvoked = _invokedName;
        int outerCalleeArgs = _calleeArgs;
        _callee = c.Target as MemberExpr;
        _invokedName = c.Target as NameExpr;
        _calleeArgs = c.Args.Count;
        Type targetType = CheckExpr(c.Target);
        _callee = outerCallee;
        _invokedName = outerInvoked;
        _calleeArgs = outerCalleeArgs;
        if (movedReceiver is not null)
        {
            args[0] = _r.TypeOf(movedReceiver);
        }

        // A CALL OF A DYNAMIC VALUE'S MEMBER, or of a dynamic value, is bound
        // when the program runs (Binder.Dynamic).
        if (_usesDynamic && LateInvocation(c, targetType) is Type lateCall)
        {
            return lateCall;
        }

        // ONE OF NULLABLE<T>'S METHODS, which CheckMember found on a cell and
        // left for the call to write out; see NullableMemberCall.
        if (c.Target is MemberExpr cellMember && _cellMethods.Remove(cellMember, out Type? cell))
        {
            if (NullableMemberCall(c, cellMember, cell) is Expr opened)
            {
                _r.Rewrites[c] = opened;
                return CheckExpr(opened);
            }
            Error(c, $"no overload of Nullable<T>.{cellMember.Name} takes {c.Args.Count} argument(s)");
            return Type.Error;
        }

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
                    // `out var x` AND `out _` take their type from the
                    // parameter here too, as a method call's do: a local
                    // function called `Judge(g, b, call, out _)` had nowhere
                    // for its discard to go.
                    if (c.Args[i] is RefArgExpr { Declare: null, Name: not null } inferred)
                    {
                        LocalSym made = new(NewSlot(), invoke.Params[i].Type, inferred.Name);
                        Declare(inferred, inferred.Name, made);
                        _assigned.Add(made);
                        CheckExpr(inferred.Target);
                        args[i] = invoke.Params[i].Type;
                        continue;
                    }
                    if (c.Args[i] is LambdaExpr invoked)
                    {
                        args[i] = CheckLambda(invoked, invoke.Params[i].Type);
                    }
                    else if (c.Args[i] is NewExpr { Type.Name.Length: 0, Elements: null } || HoldsLambda(c.Args[i]))
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

        // A DYNAMIC ARGUMENT: the overload chosen when the program runs, by
        // the arguments' own types, among those that could take them
        // (Binder.Dynamic) -- before the arguments are put in parameter
        // order, packed into a params array or joined by a receiver, which
        // are each candidate's own.
        if (_usesDynamic && args.Any(a => a.Dynamic)
            && LateOverloads(c, group, args, Implicitly,
                             (had, want, written) => WrittenFits(had, want, written)
                                                     && !(ObjectNarrowed(had, want) && !IsFunctionSource(written) && !TargetTyped(written))) is Type lateChoice)
        {
            return lateChoice;
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
            // A DYNAMIC ARGUMENT converts to anything (C# 10.2.10), at run time.
            if (had.Dynamic && !want.IsPointer) return true;
            if (written is NewExpr { Type.Name.Length: 0, Elements: null } && (want.Symbol is not null || (written is NewExpr { Collection: true } && want.IsArray))) return true;
            if (NullableIntoValue(had, want)) return false;
            if (Convertible(had, want) || had.IsError || Unmade(want) || Variant(had, want)
                || LocalFunctionConverts(written, want))
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
            return IntegerConstantFits(written, had, want);
        }

        // An argument typed object against a parameter of another type -- a
        // class, an interface, an array, a value -- which C# reaches only by
        // a cast. Not against a parameter still open (a T), nor dynamic.
        // An argument whose type comes from what it is converted to -- a
        // tuple literal, a `new()`, a null, a default, a conditional or a
        // switch expression -- reads as object until then; it is no object.
        static bool TargetTyped(Expr e) => e switch
        {
            SuppressExpr sure => TargetTyped(sure.Operand),
            TupleExpr or ConditionalExpr or SwitchExpr => true,
            NewExpr { Type.Name.Length: 0 } => true,
            LiteralExpr { Kind: Lit.Null } => true,
            DefaultExpr { Type.Name.Length: 0 } => true,
            _ => false,
        };

        static bool ObjectNarrowed(Type had, Type want)
            => had.Prim == Prim.Any && had.Symbol is null && !had.IsError
            && !(want.Prim == Prim.Any && want.Symbol is null) && !want.IsError
            && want.ParamName is null && !want.IsPointer;

        bool OrdinaryFits(MethodSymbol m) => m.Params.Count == args.Count
            && AllWritten(m, 0)
            && (m.TypeParams.Count == 0 || OrdinaryInferred(m) is not null);

        // Whether every argument from `from` on is passed with the word its
        // parameter wants and converts to what it wants: a loop, where a range
        // and a closure over it were made for every candidate of every call.
        bool AllWritten(MethodSymbol m, int from)
        {
            for (int i = from; i < args.Count; i++)
            {
                if (!WordFits(m, i) || !WrittenFits(args[i], Wants(m, i), c.Args[i])) return false;
            }
            return true;
        }

        // A GENERIC METHOD FITS IN ITS ORDINARY FORM WHEN ITS TYPE ARGUMENTS
        // CAN BE INFERRED, not because an open parameter takes anything:
        // `Pick(",", 5)` beside Pick<T>(string, IEnumerable<T>) is no fit for
        // the generic -- an int is no sequence -- and Pick(string, params
        // object[]) in its expanded form is the call. The inferred arguments,
        // or null. A lambda or a method group is left to the later rounds,
        // as everywhere: taken to fit.
        Dictionary<string, Type>? OrdinaryInferred(MethodSymbol m)
        {
            Dictionary<string, Type> got = new(StringComparer.Ordinal);
            for (int i = 0; i < args.Count && i < m.Params.Count; i++)
            {
                if (i < c.Args.Count && (IsFunctionSource(c.Args[i]) || c.Args[i] is RefArgExpr)) return got;
                Type want = fromReceiver is null ? m.Params[i].Type : Close(m.Params[i].Type, fromReceiver);
                if (!Unify(m, want, args[i], got)) return null;
            }
            for (int i = 0; i < args.Count && i < m.Params.Count; i++)
            {
                Type want = Close(Wants(m, i), got);
                if (!Unmade(want) && !WrittenFits(args[i], want, c.Args[i])) return null;
            }
            return got;
        }

        MethodSymbol? expandedParams = null;

        // The type arguments of a generic method called in expanded form:
        // the fixed parameters against their arguments, the params array's
        // element against each argument it gathers.
        bool InferExpanded(MethodSymbol m, Type element, out Dictionary<string, Type> got)
        {
            got = new Dictionary<string, Type>(StringComparer.Ordinal);
            int fixedCount = m.Params.Count - 1;
            for (int i = 0; i < args.Count; i++)
            {
                if (i < c.Args.Count && (IsFunctionSource(c.Args[i]) || c.Args[i] is RefArgExpr)) return false;
                Type want = i < fixedCount ? m.Params[i].Type : element;
                if (fromReceiver is not null) want = Close(want, fromReceiver);
                if (!Unify(m, want, args[i], got)) return false;
            }
            if (!got.Values.All(IsWord)) return false;
            Type closedElement = Close(fromReceiver is null ? element : Close(element, fromReceiver), got);
            for (int i = 0; i < args.Count; i++)
            {
                Type want = i < fixedCount ? Close(Wants(m, i), got) : closedElement;
                if (Unmade(want) || !WrittenFits(args[i], want, c.Args[i])) return false;
            }
            return true;
        }

        // Whether one expanded form takes every argument better than another
        // does, or as well and some better (C# 12.6.4.3).
        bool ExpandedBetter(MethodSymbol one, Dictionary<string, Type> oneBound, Type oneElement,
                            MethodSymbol other, Type otherElement)
        {
            bool better = false;
            for (int i = 0; i < args.Count; i++)
            {
                Type a = i < one.Params.Count - 1 ? Close(Wants(one, i), oneBound) : oneElement;
                Type b = i < other.Params.Count - 1 ? Wants(other, i) : otherElement;
                int said = BetterConversion(args[i], a, b);
                if (said < 0) return false;
                if (said > 0) better = true;
            }
            return better;
        }

        List<MethodSymbol> ordinaryFits = new();
        foreach (MethodSymbol m in group.Methods)
        {
            if (OrdinaryFits(m)) ordinaryFits.Add(m);
        }
        {
            // THE BEST EXPANDED FORM, not the first declared: Join(",", "a")
            // takes params string[] over params object[], a string being
            // better served by a string than by an object.
            foreach (MethodSymbol m in group.Methods)
            {
                if (!c.ParamsPacked && m.TypeParams.Count == 0
                    && m.Params.Count > 0
                    && m.Params[^1].IsParams
                    && m.Params[^1].Type.IsArray
                    && m.Params[^1].Type.Element is Type element
                    && args.Count >= m.Params.Count - 1
                    && Enumerable.Range(0, m.Params.Count - 1)
                                 .All(i => WrittenFits(args[i], Wants(m, i), c.Args[i]))
                    && Enumerable.Range(m.Params.Count - 1, args.Count - (m.Params.Count - 1))
                                 .All(i => WrittenFits(args[i], element, c.Args[i]))
                    && (expandedParams is null
                        || ExpandedBetter(m, new Dictionary<string, Type>(), element, expandedParams, expandedParams.Params[^1].Type.Element!)))
                {
                    expandedParams = m;
                }
            }

            // A GENERIC METHOD'S PARAMS ARRAY EXPANDS TOO, its element type
            // inferred from the arguments it gathers: `Task.WhenAll(a, b)`
            // over two Task<int> is WhenAll<int>(params Task<int>[]), whose
            // result is the int[] of both, where WhenAll(params Task[]) has
            // none. The two expanded forms compete as any two overloads do,
            // and the ordinary one wins a tie (C# 12.6.4.3).
            Type? expandedElement = expandedParams?.Params[^1].Type.Element;
            foreach (MethodSymbol m in group.Methods)
            {
                if (c.ParamsPacked || m.TypeParams.Count == 0 || m.Params.Count == 0 || !m.Params[^1].IsParams
                    || m.Params[^1].Type is not { IsArray: true, Element: Type open }
                    || args.Count < m.Params.Count - 1
                    || !InferExpanded(m, open, out Dictionary<string, Type> got))
                {
                    continue;
                }

                Type closedElement = Close(fromReceiver is null ? open : Close(open, fromReceiver), got);
                if (Unmade(closedElement) || RefOf(closedElement) is null) continue;

                if (expandedParams is null || ExpandedBetter(m, got, closedElement, expandedParams, expandedElement!))
                {
                    expandedParams = m;
                    expandedElement = closedElement;
                }
            }

            // AND THE EXPANDED FORM COMPETES WITH THE ORDINARY ONES (C#
            // 12.6.4.3): it is the call only when it is the better function
            // member against every overload that fits as written, and an
            // ordinary form wins a tie. `string.Join(",", "abc")` is Join(
            // string, params string[]) -- a string to a string beats a string
            // to IEnumerable<char> -- and so "abc", not "a,b,c".
            if (expandedParams is { } challenger && ordinaryFits.Count > 0)
            {
                Type challengerElement = expandedElement!;
                Dictionary<string, Type> none = new(StringComparer.Ordinal);
                foreach (MethodSymbol ordinary in ordinaryFits)
                {
                    Dictionary<string, Type> inferred = ordinary.TypeParams.Count == 0 ? none : OrdinaryInferred(ordinary) ?? none;
                    bool better = false, worse = false;
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (i < c.Args.Count && IsFunctionSource(c.Args[i])) continue;
                        Type expandedWant = i < challenger.Params.Count - 1 ? Wants(challenger, i) : challengerElement;
                        Type ordinaryWant = Close(Wants(ordinary, i), inferred);
                        int said = BetterConversion(args[i], expandedWant, ordinaryWant);
                        if (said > 0) better = true;
                        if (said < 0) worse = true;
                    }
                    if (worse || !better)
                    {
                        expandedParams = null;
                        break;
                    }
                }
            }

            if (expandedParams is { } variadic)
            {
                int fixedCount = variadic.Params.Count - 1;
                Type element = expandedElement!;
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
                c.ParamsPacked = true;
                args.RemoveRange(fixedCount, args.Count - fixedCount);
                args.Add(CheckExpr(packed));
            }
        }

        // Overload resolution by arity, then by exact-then-convertible match.
        List<MethodSymbol> byArity;
        if (expandedParams is null)
        {
            byArity = new List<MethodSymbol>();
            foreach (MethodSymbol m in group.Methods)
            {
                if (m.Params.Count == args.Count) byArity.Add(m);
            }
        }
        else
        {
            byArity = new List<MethodSymbol> { expandedParams };
        }

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
        bool anyFits = false;
        foreach (MethodSymbol m in byArity)
        {
            if (m.TypeParams.Count > 0 || AllWritten(m, 0))
            {
                anyFits = true;
                break;
            }
        }
        if (byArity.Count > 0 && !anyFits)
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

            //
            // AND OF THOSE, THE BETTER MEMBER by the arguments written, as C#
            // judges it -- the defaults take no part. `Take(s, long n = 0, …)`
            // and `Take(s, int n = 0, …)` both fit `Take("x", small)`, and the
            // first written won; a second round of binding hid it, because by
            // then the call carried its completed arguments and the full-arity
            // rule chose the int. A unit bound once took the long.
            MethodSymbol? shorter = BetterMember(group.Methods.Where(
                m => Completable(m)
                  && Enumerable.Range(0, args.Count)
                               .All(i => WordFits(m, i) && WrittenFits(args[i], Wants(m, i), c.Args[i])))
                .ToList())
                ?? group.Methods.FirstOrDefault(Completable);

            if (shorter != null)
            {
                int writtenCount = args.Count;
                int hidden = Hidden(c, shorter);
                for (int i = args.Count; i < shorter.Params.Count; i++)
                {
                    // Argument k is span pair k + 1, or k once an extension's
                    // receiver has become argument 0 (pair 0 is the receiver),
                    // counted after the variables a generic local function
                    // captured, which come first and were never written.
                    Expr fallback = CallerValue(shorter.Decl!.Params[i], shorter.Decl.Params, CallLine(c),
                                        k => k >= writtenCount || k < hidden ? null
                                           : SpanText(c.Spans, c.Source, (c.ReceiverAdded ? k : k + 1) - hidden))
                                    ?? Written(shorter, shorter.Decl.Params[i]);

                    c.Args.Add(fallback);
                    // AS ITS PARAMETER'S TYPE, not the call's surroundings':
                    // `return await s.ReadAsync(buffer)` in a Task<int> method
                    // wants an int, and the completed `CancellationToken
                    // cancellationToken = default` took it and became 0.
                    Type? outerWanted = _wanted;
                    _wanted = Wants(shorter, i);
                    args.Add(CheckExpr(fallback));
                    _wanted = outerWanted;
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
                // THE ONES THERE ARE, so the reader can see which was meant
                // and what it takes -- the question this answers is usually
                // "then which Link was I calling?".
                // And for one of the right arity, the argument that did not
                // fit it -- CS1503's answer, which is what the reader needs.
                string Misfit(MethodSymbol m)
                {
                    if (m.Params.Count != args.Count) return "";
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (!WordFits(m, i)) return $" (argument {i + 1} is {(c.Args[i] is RefArgExpr ? "" : "not ")}passed by reference)";
                        if (!WrittenFits(args[i], Wants(m, i), c.Args[i])) return $" (argument {i + 1}: '{args[i]}' is not '{Wants(m, i)}')";
                    }
                    return "";
                }
                string there = string.Join("; ", group.Methods.Take(6).Select(m =>
                    $"{m.Owner?.Name}.{m.Name}({m.Params.Count}){(m.Decl?.File is { Length: > 0 } f ? " in " + System.IO.Path.GetFileName(f) : "")}{Misfit(m)}"));
                Error(c, $"no overload of '{group.Methods[0].Name}' takes {args.Count} argument{(args.Count == 1 ? "" : "s")}; there are {there}");
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
                                          ?? NoMethods;

                // AND BY THE TYPES IT WROTE, when it wrote them: `(string s)
                // => ...` is no Func<int, ...> (C# 7.5.3.1 -- an explicitly
                // typed lambda is applicable only where they are the same).
                // (Not where the parameter is a type parameter: what it is,
                // inference says, and `__Delegates.Combine<T>(T? a, T? b)`
                // with a lambda on the right refused every lambda of all.)
                // (Nor where it takes object or Delegate and the lambda wrote
                // its types: it goes as its natural type, C# 10's Func or
                // Action -- `Describe((int y) => y * 10)` over Describe(object).)
                if (c.Args[i] is LambdaExpr lam && m.Params[i].Type.ParamName is null
                    && !(lam.TypesWritten && (NaturalTarget(m.Params[i].Type) || ReferenceEquals(m.Params[i].Type.Symbol, DelegateRoot())))
                    && !invokes.Any(v => v.Params.Count == lam.Params.Count
                                      && (!lam.TypesWritten
                                          || Enumerable.Range(0, lam.Params.Count)
                                                       .All(k => WrittenParameterMismatch(lam, k, m.Params[i].Type, v) is null))))
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

                // AND A METHOD GROUP IS ONLY EVER A DELEGATE (C# 10.8). A
                // parameter with no Invoke is no delegate, whatever it has:
                // `chunk.Sort(Compare)` is Sort(Comparison<T>), and taking
                // Sort(IComparer<T>) -- an interface, whose Compare matched
                // nothing because nothing was asked of it -- left the group
                // unconverted, and it reached the code generator as a name.
                if (c.Args[i] is not LambdaExpr && invokes.Count == 0 && m.Params[i].Type.Prim != Prim.Any && m.Params[i].Type.ParamName is null
                    && _r.Resolved.TryGetValue(c.Args[i], out Sym? groupOnly)
                    && Grouped(groupOnly) is { Count: > 0 })
                {
                    return false;
                }
            }
            return true;
        }

        // Copied only when one does not fit: the list is never changed after
        // this, so where every one fits it serves as it is.
        List<MethodSymbol>? fitting = null;
        for (int k = 0; k < byArity.Count; k++)
        {
            if (Fits(byArity[k]))
            {
                fitting?.Add(byArity[k]);
            }
            else if (fitting is null)
            {
                fitting = new List<MethodSymbol>(byArity.Count);
                for (int j = 0; j < k; j++) fitting.Add(byArity[j]);
            }
        }
        if (fitting is not null) byArity = fitting;

        // ONLY WHAT THIS CODE CAN SEE, as for a constructor (Visible): a
        // private overload of another type is no candidate where a visible
        // one fits.
        if (byArity.Count > 1 && byArity.Any(Visible) && !byArity.All(Visible))
        {
            byArity = byArity.Where(Visible).ToList();
        }

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
            // IMPLICITLY, as C# counts it: `object` reaches any type here (it
            // is a machine word), but to C# object -> IEnumerable is a cast,
            // so it says nothing about which of the two is more specific.
            bool toSecond = Implicitly(first, second);
            bool toFirst = Implicitly(second, first);
            if (toSecond != toFirst) return toSecond ? 1 : -1;
            if (SignedIntegral(first) && UnsignedIntegral(second)) return 1;
            if (SignedIntegral(second) && UnsignedIntegral(first)) return -1;
            return 0;
        }

        // THE SAME TYPE, annotations aside: `IEnumerable<string>` is the
        // `IEnumerable<string?>` a parameter declares, the `?` being no type.
        static bool Same(Type a, Type b)
            => a.Equals(b) || (!a.IsNullableValue && !b.IsNullableValue
                               && (a.AsNonNullable().Equals(b.AsNonNullable()) || MethodSignatures.SameType(a, b)));

        bool Implicitly(Type from, Type to)
            => (from.Prim != Prim.Any || to.Prim == Prim.Any) && (Convertible(from, to) || Variant(from, to));

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
                    // Nullability aside all the way down: `out C?[]?` is the
                    // `C[]` slot of a Dictionary of them, a class element's
                    // mark being no more part of the type than the array's.
                    if (args[i].IsError || args[i].AsNonNullable().Equals(want.AsNonNullable())
                        || MethodSignatures.SameType(args[i].AsNonNullable(), want.AsNonNullable())) continue;
                    return false;
                }

                // A DYNAMIC ARGUMENT converts to anything, at run time.
                if (args[i].Dynamic && !want.IsPointer) continue;
                if (NullableIntoValue(args[i], want)) return false;
                // AN OBJECT IS NO NARROWER TYPE without a cast (C# 10.2): the
                // machine word goes anywhere here, but an overload that wants
                // a LocalSym is not applicable to an object, and choosing it
                // read a FieldSym as one -- `a.Equals((object)f)` beside a
                // record's own Equals(LocalSym) and the inherited Equals(object).
                if (ObjectNarrowed(args[i], want) && !(i < c.Args.Count && (IsFunctionSource(c.Args[i]) || TargetTyped(c.Args[i])))) return false;
                if (Convertible(args[i], want) || args[i].IsError || Unmade(want)
                    || (variant && Variant(args[i], want))
                    || (i < c.Args.Count && LocalFunctionConverts(c.Args[i], want)))
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
                // And into a nullable of what it fits (IntegerConstantFits).
                if (args[i].Prim != Prim.Char && i < c.Args.Count && IntegerConstantFits(c.Args[i], args[i], want))
                {
                    continue;
                }

                // A CONSTANT INTO A TYPE WITH A CONVERSION FROM A NARROWER
                // INTEGER: `UInt128.Max(x, 1)` takes the 1 as a uint first.
                if (i < c.Args.Count && want.Symbol is not null
                    && UserConversion(args[i], want, false, IntegerConstant(c.Args[i], args[i])) is not null)
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
        List<MethodSymbol> byWord = new();
        foreach (MethodSymbol m in byArity)
        {
            bool words = true;
            for (int i = 0; i < args.Count && words; i++) words = WordFits(m, i);
            if (words) byWord.Add(m);
        }

        if (byWord.Count > 0)
        {
            byArity = byWord;
        }

        // The first that takes every argument as exactly its own type.
        bool Exact(MethodSymbol m)
        {
            if (m.TypeParams.Count != 0) return false;
            for (int i = 0; i < Math.Min(args.Count, m.Params.Count); i++)
            {
                if (!(args[i].Equals(Wants(m, i))
                      || (!args[i].IsNullableValue && !Wants(m, i).IsNullableValue
                          && args[i].AsNonNullable().Equals(Wants(m, i).AsNonNullable())))) return false;
            }
            return true;
        }
        MethodSymbol? exact = null;
        foreach (MethodSymbol m in byArity)
        {
            if (Exact(m))
            {
                exact = m;
                break;
            }
        }
        MethodSymbol? best = exact
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
                          //
                          // EVERY APPLICABLE ONE, a variant conversion being as
                          // ordinary an implicit conversion as any (C# 10.2.8):
                          // `Concat(IEnumerable<string?>)` beside
                          // `Concat(object?)` takes a sequence of strings, and
                          // leaving the variant one out until nothing else fit
                          // handed the sequence to the object overload.
                          ?? BetterMember(byArity.Where(m => m.TypeParams.Count == 0 && Accepts(m, variant: true)).ToList());

        // A GENERIC METHOD COMPETES ON THE SAME TERMS (C# 12.6.4.3), and loses
        // only a tie: `One(5)` beside One(object) and One<T>(T) is One<int>,
        // an int being better served by an int than by an object, while
        // `Three(dog)` beside Three(Dog) is Three(Dog), neither being better
        // and the non-generic one winning the tie. Tried last, the generic was
        // chosen only when nothing else fit at all -- and
        // `Task.WhenAll(tasks<int>)` became the plain WhenAll(Task[]) whose
        // result carries no values.
        Dictionary<string, Type>? bound = fromReceiver;
        MethodSymbol? plain = best;
        best = null;

        bool anyGeneric = false;
        foreach (MethodSymbol m in byArity) anyGeneric |= m.TypeParams.Count > 0;
        if (anyGeneric)
        {
            foreach (MethodSymbol candidate in byArity)
            {
                if (candidate.TypeParams.Count == 0) continue;
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

        // TYPE ARGUMENTS WRITTEN AT THE CALL name a generic method; an
        // ordinary one is no candidate at all then.
        bool typeArgsWritten = c.Target switch
        {
            MemberExpr { TypeArgs.Count: > 0 } => true,
            NameExpr { TypeArgs.Count: > 0 } => true,
            _ => false,
        };

        if (plain is not null && (best is null || (!typeArgsWritten && !GenericBetter(best, bound, plain))))
        {
            best = plain;
            bound = fromReceiver;
        }

        // Whether the generic method, at the type arguments inferred for it,
        // takes every argument and takes some better than the ordinary one
        // does. A lambda or a method group decides nothing here: both
        // overloads were already chosen for its arity.
        bool GenericBetter(MethodSymbol generic, Dictionary<string, Type>? inferred, MethodSymbol ordinary)
        {
            bool better = false;
            for (int i = 0; i < args.Count && i < generic.Params.Count && i < ordinary.Params.Count; i++)
            {
                if (i < c.Args.Count && IsFunctionSource(c.Args[i]))
                {
                    // A LAMBDA WITH A VALUE converts better to a delegate that
                    // returns one than to one returning void (C# 12.6.4.5):
                    // Ui<T>(Func<T>) beside Ui(Action), called with
                    // `() => form.Handle`, is the Func -- it was the Action,
                    // and its answer void.
                    if (c.Args[i] is LambdaExpr lam)
                    {
                        Dictionary<string, Type> known = inferred ?? new();
                        Type ga = Close(Substitute(Invoked(generic.Params[i].Type)?.Returns ?? Type.Error, Applied(generic.Params[i].Type)), known);
                        Type gb = Substitute(Invoked(ordinary.Params[i].Type)?.Returns ?? Type.Error, Applied(ordinary.Params[i].Type));
                        bool aVoid = ga.IsVoid, bVoid = gb.IsVoid;
                        if (!ga.IsError && !gb.IsError && aVoid != bVoid)
                        {
                            Type? made = Produces(generic, generic.Params[i].Type, lam, known);
                            if (made is not null && !made.IsError && !made.IsVoid)
                            {
                                if (bVoid) better = true;
                                else return false;
                            }
                        }
                    }
                    continue;
                }
                Type closed = inferred is null ? Wants(generic, i) : Close(Wants(generic, i), inferred);
                // An unmade parameter takes what it was inferred from, as
                // Accepts has it.
                if (!Unmade(closed) && !WrittenFits(args[i], closed, c.Args[i])) return false;
                int said = BetterConversion(args[i], closed, Wants(ordinary, i));
                if (said < 0) return false;
                if (said > 0) better = true;
            }
            return better;
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
                // AS THE RECEIVER'S USE WROTE IT, names and all, as an argument
                // passed by value is checked against: `parent.TryGetValue(at,
                // out var up)` on a Dictionary<int, (int From, int Offset)> made
                // `up` the shared copy's (int, int), and `up.From` was asked of
                // every naming of the shape -- ambiguous wherever another names
                // its elements otherwise, `(int O, int From)`.
                Type declared = !c.ReceiverAdded && c.Target is MemberExpr declaredOn
                    ? ContextualParameterType(_r.TypeOf(declaredOn.Target), best, i)
                    : best.Params[i].Type;
                if (!declared.AsNonNullable().Equals(best.Params[i].Type.AsNonNullable())
                    && !MethodSignatures.SameType(declared.AsNonNullable(), best.Params[i].Type.AsNonNullable()))
                    declared = best.Params[i].Type;
                LocalSym made = new(NewSlot(), declared, inferred.Name);
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
                && !MethodSignatures.SameType(args[i].AsNonNullable(), wantByRef.AsNonNullable())
                && !args[i].IsError)
            {
                Error(c.Args[i],
                      $"argument {i + 1} of '{best.Name}' is '{wantByRef}' "
                    + $"and this is '{args[i]}'; a by-reference argument must match exactly");
                continue;
            }

            if (!passed)
            {
                // `_values.Add(v)` on a List<Node?> takes a Node?: the `?` is the
                // receiver's use, not the specialisation's.
                Type want = Close(!c.ReceiverAdded && c.Target is MemberExpr wantOn
                    ? ContextualParameterType(_r.TypeOf(wantOn.Target), best, i)
                    : best.Params[i].Type, bound);
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

                if (MethodGroupLambda(c.Args[i], want, localFunctions: true) is LambdaExpr wrapper)
                {
                    _r.Rewrites[c.Args[i]] = wrapper;
                    args[i] = CheckLambda(wrapper, want);
                    continue;
                }

                // A TUPLE LITERAL WITH AN UNTYPED ELEMENT, checked now that the
                // tuple type it converts to is known.
                if (c.Args[i] is TupleExpr { } untyped && HoldsLambda(untyped))
                {
                    Type? saved = _wanted;
                    _wanted = want;
                    args[i] = CheckExpr(untyped);
                    _wanted = saved;
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
            List<Type> given = new();

            foreach (string p in best.TypeParams)
            {
                if (!bound.TryGetValue(p, out Type? was) || RefOf(was) is not TypeRef spell)
                {
                    spelt.Clear();
                    break;
                }

                spelt.Add(spell);
                given.Add(was);
            }

            // `where T : new()` OF THE METHOD, against what T was given or
            // worked out to be (CS0310).
            if (spelt.Count == best.TypeParams.Count)
            {
                for (int i = 0; i < given.Count && i < generic.TypeParams.Count; i++)
                {
                    if (generic.TypeParams[i].New && !HasPublicParameterless(given[i]))
                    {
                        Error(c, NotConstructible(given[i], generic.TypeParams[i].Name,
                            best.Name + "<" + string.Join(", ", generic.TypeParams.Select(p => p.Name)) + ">"));
                    }
                }
            }

            // A TYPE ARGUMENT ONLY RUN TIME KNOWS: a shared copy's T, or a
            // shared method copy's own, handed on (Type.CanonParam). The
            // call reaches the shared method copy (Monomorphiser.CopyName),
            // which is given each such argument's descriptor as a hidden
            // argument (Lowering.HiddenTypeArguments), so that its tests of
            // an interface over it ask the object at hand. Not a generic
            // virtual method, whose copies are reached by a dispatch that
            // passes nothing more, and not an iterator or an async method,
            // whose bodies run in a state machine the hidden arguments do not
            // reach: those stay the copy over object, as every call was.
            int[]? hidden = null;
            if (spelt.Count == best.TypeParams.Count && !best.GenericVirtual && !best.Async
                && generic.Body is not { Iterator: true })
            {
                for (int i = 0; i < spelt.Count; i++)
                {
                    if (given[i] is { CanonParam: not -1, Prim: Prim.Any, Symbol: null, ArrayRank: 0, PointerDepth: 0 })
                    {
                        spelt[i].CanonIndex = -2 - i;
                        if (hidden is null)
                        {
                            hidden = new int[spelt.Count];
                            for (int unset = 0; unset < hidden.Length; unset++) hidden[unset] = -1;
                        }
                        hidden[i] = given[i].CanonParam;
                    }
                }
            }
            // On the call, for the round that binds it to the copy (CallExpr.HiddenTypeArgs).
            c.HiddenTypeArgs = hidden;

            // A GENERIC VIRTUAL METHOD IS DISPATCHED ON THE RECEIVER, except
            // through `base.`, which names one implementation and is an
            // ordinary call to its copy.
            if (spelt.Count == best.TypeParams.Count && best.GenericVirtual
                && c.Target is not MemberExpr { Target: BaseExpr })
            {
                GenericVirtualCall(c, best, spelt, bound);
            }
            else if (spelt.Count == best.TypeParams.Count)
            {
                int member = generic.TemplateIndex;

                if (member < 0 && best.Owner.Decl is TypeDecl declaring)
                {
                    member = declaring.Members.IndexOf(generic);
                }

                string wanted = Monomorphiser.CopyName(generic.Name, spelt, member);
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
                    if (_member is MethodDecl body) _r.Wanting.Add((_thisType, body));
                }
            }
        }

        _r.Calls[c] = called;

        // A STRING IS IMMUTABLE, as C#'s is. Sys.NewChars, SetChar and the
        // string Copy are how String itself is built, and nothing outside the
        // runtime and the class library may use them: a string written into
        // after it was made is a string some other holder -- an interned
        // literal, a cached hash, a dictionary's key -- sees change. Bytes are
        // a byte[], text being assembled a char[] or a StringBuilder.
        if (called.Owner.Name == Prelude.TypeName && called.Owner.Decl?.File == "<prelude>"
            && (called.Name is Prelude.NewChars or Prelude.SetChar
                || (called.Name == "Copy" && called.Params.Count > 0 && called.Params[0].Type.Prim == Prim.String))
            && !BuildsStrings())
        {
            // What C# says of an internal member (CS0122): these are the class
            // library's own, as FastAllocateString is System.Private.CoreLib's.
            Error(c, $"'Sys.{called.Name}' is inaccessible due to its protection level");
        }

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

    /// <summary>
    /// A shared method copy's test or cast to a generic interface over its
    /// own type parameters (ICanonShape, Monomorphiser.Shaped): the family
    /// and the arguments, resolved, for the lowering to ask the object
    /// (BindResult.Shapes). Only an interface made from a template, with as
    /// many arguments as were written, and no more than a record holds.
    /// </summary>
    private void NoteShape<T>(T node, Type tested) where T : Expr, ICanonShape
    {
        if (node.ShapeArgs is not { Count: > 0 } written || tested.IsArray || tested.IsPointer
            || tested.Symbol is not { Kind: TypeKind.Interface } face
            || face.Decl is not { Specialised: true, Template: not null } decl
            || decl.TemplateArgs.Count != written.Count || written.Count > CanonShape.MostArguments)
        {
            _r.Shapes.Remove(node);
            return;
        }
        List<Type> args = new(written.Count);
        foreach (TypeRef a in written) args.Add(Resolve(a, _thisType));
        _r.Shapes[node] = new CanonShape { Interface = face, Args = args };
    }

    /// <summary>
    /// A call of a GENERIC VIRTUAL METHOD: `node.GetValue&lt;int&gt;()` where
    /// JsonNode's is virtual and JsonValue overrides it.
    ///
    /// A vtable slot holds one address and this method has one per type
    /// argument, compiled wherever that argument is known -- so it has no slot,
    /// and the call is dispatched the way an ahead-of-time compiler must: every
    /// class that overrides the method (or implements it, when it belongs to an
    /// interface) is found, a copy of each override is made at the call's type
    /// arguments, and the code generator tests the receiver against those
    /// classes deepest first and calls the first that matches directly.
    ///
    /// Every class the program can see is found, not only this file's: the
    /// declaration index keeps every generic instance method by name and
    /// arity, and the ones not loaded yet are demanded. A class the call could
    /// not see -- one in a program linking a library that makes the call --
    /// reaches the method's own implementation, or MissingMethodException
    /// when that one is abstract.
    /// </summary>
    private void GenericVirtualCall(CallExpr c, MethodSymbol best, List<TypeRef> spelt, Dictionary<string, Type>? bound)
    {
        try { _requireOverrides?.Invoke(best.Name, best.TypeParams.Count); }
        catch (Metadata.DeclarationDemand demand) { _declarationBatch.Add(demand); return; }

        TypeSymbol home = best.Owner;
        bool contract = home.Kind == TypeKind.Interface;
        bool complete = true;

        // The copy of `template` on `owner` at these arguments, named as every
        // other copy of a generic method is (the member's position keeps two
        // overloads apart), or null while it waits to be made.
        MethodSymbol? CopyOf(TypeSymbol owner, MethodSymbol template)
        {
            if (owner.Decl is not TypeDecl declaring || template.Decl is not MethodDecl generic || generic.Body is null)
            {
                return null;
            }

            int member = generic.TemplateIndex >= 0 ? generic.TemplateIndex : declaring.Members.IndexOf(generic);
            string name = Monomorphiser.MethodName(generic.Name, spelt) + "$" + member;
            MethodSymbol? made = owner.Methods.FirstOrDefault(m => m.Name == name);

            if (made is null)
            {
                _r.WantedOverrides.Add((declaring, generic, spelt, name));
                complete = false;
            }

            return made;
        }

        List<(TypeSymbol Class, MethodSymbol Copy)> targets = new();

        // ONE TYPE BY TWO NAMES. A word-shaped specialisation and its canonical
        // copy run the same code, but an object is made with the descriptor
        // of whichever code made it: OrderBy's canonical copy makes an
        // OrderedSequence$__canon, and ThenBy<MethodSymbol, string> asks it
        // for CreateOrderedEnumerable through IOrderedEnumerable$MethodSymbol
        // -- an interface the canonical class implements under its other
        // name. Every class implementing either name is a target, matched
        // against the member of the name it really has.
        static string CanonKey(TypeSymbol t) => t.Decl?.Canon ?? t.Name;
        string homeKey = CanonKey(home);

        // The member `best` is on `twin`, one name of home: by its place in
        // the template where it has one, else by its signature.
        MethodSymbol? OnTwin(TypeSymbol twin)
        {
            if (ReferenceEquals(twin, home)) return best;
            int place = best.Decl?.TemplateIndex ?? -1;
            // Not declared to be told it has none (TypeSymbol.MayHave).
            if (!twin.MayHave(best.Name)) return null;
            return twin.Methods.FirstOrDefault(m => m.Name == best.Name && m.TypeParams.Count == best.TypeParams.Count
                                                  && m.Params.Count == best.Params.Count
                                                  && (place < 0 || m.Decl?.TemplateIndex == place));
        }

        // A STRUCT TOO, for an interface's method: a boxed value is reached
        // through the interface like any object, and the dispatch knows its
        // box (Lowering.GenericVirtualDispatch). A struct derives from nothing,
        // so a class's method never lands on one.
        // A COPY OF THE TABLE: a class's methods asked for here may be
        // declared on that first asking, and their signatures may make a
        // tuple shape, a new entry in the table (DeclareMembersNow).
        foreach (TypeSymbol t in _r.Types.Values.Distinct().Where(t => (t.Kind == TypeKind.Class || contract && t.Kind == TypeKind.Struct)
                     && !IsTemplate(t) && t.Decl is not null).ToList())
        {
            MethodSymbol? own;

            if (contract)
            {
                TypeSymbol? face = AllInterfaces(t).Contains(home) ? home
                                 : AllInterfaces(t).FirstOrDefault(i => CanonKey(i) == homeKey);
                if (face is null || OnTwin(face) is not MethodSymbol wanted)
                {
                    continue;
                }

                // As AssignSlots maps an interface member: an explicit
                // implementation for this interface first, then a public one
                // of the name -- on this class or inherited.
                string ifaceName = ExplicitName(face);
                // ITS OWN, explicit or not, only when one of its members may
                // be called this (TypeSymbol.MayHave, which counts an explicit
                // implementation by the member it implements): every class of
                // the unit is walked here, and one that cannot have the method
                // is not declared to find that out. FindMethods asks its bases.
                own = (t.MayHave(best.Name) ? t.Methods.FirstOrDefault(m => m.ExplicitMember == best.Name && m.ExplicitInterface == ifaceName
                          && !m.Abstract && m.TypeParams.Count == best.TypeParams.Count && MethodSignatures.Implements(m, wanted)) : null)
                   ?? t.FindMethods(best.Name).FirstOrDefault(m => !m.Static && !m.Abstract
                          && m.TypeParams.Count == best.TypeParams.Count && MethodSignatures.Implements(m, wanted));
            }
            else
            {
                TypeSymbol? under = null;

                for (TypeSymbol? a = t; a != null && under is null; a = a.Base)
                {
                    if (ReferenceEquals(a, home) || CanonKey(a) == homeKey) under = a;
                }

                if (under is null || OnTwin(under) is not MethodSymbol wanted)
                {
                    continue;
                }

                // ITS OWN override, declared here: a class that inherits one
                // is matched by the class it inherits it from, further down
                // the list.
                own = !t.MayHave(best.Name) ? null : t.Methods.FirstOrDefault(m => ReferenceEquals(m.Owner, t) && m.Name == best.Name && !m.Abstract
                    && (m.Override || ReferenceEquals(m, wanted)) && MethodSignatures.Implements(m, wanted));
            }

            if (own is null)
            {
                continue;
            }

            if (CopyOf(own.Owner, own) is MethodSymbol copy)
            {
                targets.Add((t, copy));
            }
        }

        MethodSymbol? fallback = contract || best.Abstract ? null : CopyOf(home, best);

        if (!complete)
        {
            return;
        }

        _r.GenericDispatches[c] = new GenericDispatch
        {
            Method = home.Name + "." + best.Name + "<" + string.Join(", ", spelt) + ">",
            Targets = targets.Where(target => !ReferenceEquals(target.Copy, fallback) || !ReferenceEquals(target.Class, home))
                             .OrderByDescending(target => target.Class.Depth)
                             .ThenBy(target => target.Class.Key, StringComparer.Ordinal)
                             .ToList(),
            Fallback = fallback,
            Params = best.Params.Select(p => new ParamSymbol
            {
                Name = p.Name, Type = Close(p.Type, bound), ByRef = p.ByRef, ReadOnly = p.ReadOnly, IsParams = p.IsParams,
            }).ToList(),
            Returns = Close(best.Returns, bound),
        };
    }

    private static bool HasConditionalMember(Expr expression) => expression switch
    {
        MemberExpr member => member.NullConditional || HasConditionalMember(member.Target),
        CallExpr call => HasConditionalMember(call.Target),
        IndexExpr index => index.NullConditional || HasConditionalMember(index.Target),
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

        // A STRING ON ONE SIDE still reaches an operator of the other's type
        // through its conversions: `element.Name == "Target"` is XName's
        // op_Equality, the string made an XName by op_Implicit. Two strings
        // are the language's own.
        if (l.ArrayRank > 0 || r.ArrayRank > 0 || (l.Prim == Prim.String && r.Prim == Prim.String)
            || l.Prim == Prim.NullLiteral || r.Prim == Prim.NullLiteral)
        {
            return null;
        }

        if (l.Symbol is null && r.Symbol is null)
        {
            return null;
        }

        long? lc = IntegerConstant(b.Left, l), rc = IntegerConstant(b.Right, r);
        MethodSymbol? found = Operator(l.Symbol, name, l, r, lc, rc) ?? Operator(r.Symbol, name, l, r, lc, rc);

        // LIFTED, C# 12.4.8: `==` and `!=` over a nullable struct whose
        // underlying type declares them -- `path != here` with ListPath? on
        // either side. Each operand is evaluated once, in order, into a
        // subject of its own (two PatternExprs, the outer read through
        // Outer = 1); empty against empty is equal, empty against a value is
        // not, and two values are the operator's own answer.
        if (b.Op is BinOp.Eq or BinOp.Ne && (l.IsNullableValue || r.IsNullableValue)
            && (found is null || !found.Params[0].Type.IsNullableValue && !found.Params[1].Type.IsNullableValue))
        {
            Type lu = l.IsNullableValue ? l.Underlying : l, ru = r.IsNullableValue ? r.Underlying : r;
            if (lu.Symbol is { Kind: TypeKind.Struct } && lu.Equals(ru)
                && Operator(lu.Symbol, name, lu, ru) is MethodSymbol op)
            {
                _r.Rewrites[b] = LiftedEquality(b, op, name, l.IsNullableValue, r.IsNullableValue);
                return CheckExpr(_r.Rewrites[b]);
            }
        }

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

    /// <summary>
    /// `a == b` or `a != b` lifted over nullable structs, each side held once:
    /// for ==, both empty or both holding equal values by the operator; for
    /// !=, its negation by the type's own op_Inequality. A side that is not
    /// nullable always holds its value.
    /// </summary>
    private Expr LiftedEquality(BinaryExpr b, MethodSymbol op, string name, bool leftMay, bool rightMay)
    {
        Expr Held(int outer) => new SubjectExpr { Outer = outer, Line = b.Line, Col = b.Col };
        Expr Has(int outer) => new MemberExpr { Target = Held(outer), Name = "HasValue", Line = b.Line, Col = b.Col };
        Expr Value(int outer, bool may) => may ? new MemberExpr { Target = Held(outer), Name = "Value", Line = b.Line, Col = b.Col } : Held(outer);
        Expr Bin(BinOp o, Expr x, Expr y) => new BinaryExpr { Op = o, Left = x, Right = y, Line = b.Line, Col = b.Col };
        Expr Not(Expr x) => new UnaryExpr { Op = UnOp.Not, Operand = x, Line = b.Line, Col = b.Col };

        CallExpr call = new()
        {
            Target = new MemberExpr { Target = Qualified(op.Owner.Key, b), Name = name, Line = b.Line, Col = b.Col },
            Line = b.Line, Col = b.Col,
        };
        call.Args.Add(Value(1, leftMay));
        call.WritableArgNames.Add(null);
        call.Args.Add(Value(0, rightMay));
        call.WritableArgNames.Add(null);

        bool eq = b.Op == BinOp.Eq;
        Expr test;
        if (leftMay && rightMay)
        {
            // ==: same emptiness, and empty or equal.   !=: different emptiness, or held and unequal.
            test = eq
                ? Bin(BinOp.AndAlso, Bin(BinOp.Eq, Has(1), Has(0)), Bin(BinOp.OrElse, Not(Has(1)), call))
                : Bin(BinOp.OrElse, Bin(BinOp.Ne, Has(1), Has(0)), Bin(BinOp.AndAlso, Has(1), call));
        }
        else
        {
            // One side always holds a value: the other must, and then the operator.
            Expr has = Has(leftMay ? 1 : 0);
            test = eq ? Bin(BinOp.AndAlso, has, call) : Bin(BinOp.OrElse, Not(has), call);
        }

        return new PatternExpr
        {
            Subject = b.Left,
            Test = new PatternExpr { Subject = b.Right, Test = test, Line = b.Line, Col = b.Col },
            Line = b.Line, Col = b.Col,
        };
    }

    /// <summary>
    /// A unary operator a type declares, called: the operand-to-value ones as
    /// the call they are, and `++`/`--` as C# 12.8.16 has them -- the operator
    /// applied and the result stored back, the expression's value the new one
    /// before the operand (prefix) or the old one (postfix). Null when the
    /// operand's type declares none, and the language's own rules apply.
    /// </summary>
    private Type? UserUnary(UnaryExpr u, Type operand)
    {
        string? name = u.Op switch
        {
            UnOp.Neg => "op_UnaryNegation",
            UnOp.Plus => "op_UnaryPlus",
            UnOp.Not => "op_LogicalNot",
            UnOp.BitNot => "op_OnesComplement",
            UnOp.PreInc or UnOp.PostInc => "op_Increment",
            UnOp.PreDec or UnOp.PostDec => "op_Decrement",
            _ => null,
        };
        if (name is null || operand.IsArray || operand.IsPointer
            || operand.AsNonNullable().Symbol is not { Kind: TypeKind.Class or TypeKind.Struct } holder)
        {
            return null;
        }
        MethodSymbol? found = holder.FindMethods(name)
            .FirstOrDefault(m => m.Static && m.Params.Count == 1 && Convertible(operand, m.Params[0].Type));
        if (found is null)
        {
            return null;
        }

        CallExpr Apply(Expr on)
        {
            CallExpr call = new()
            {
                Target = new MemberExpr { Target = Qualified(found.Owner.Key, u), Name = name, Line = u.Line, Col = u.Col },
                Line = u.Line, Col = u.Col,
            };
            call.Args.Add(on);
            call.WritableArgNames.Add(null);
            return call;
        }

        Expr made;
        switch (u.Op)
        {
            case UnOp.PreInc or UnOp.PreDec:
                RequireAssignable(u.Operand, u.Op == UnOp.PreInc ? "increment" : "decrement");
                made = new AssignExpr { Target = u.Operand, Value = Apply(u.Operand), Line = u.Line, Col = u.Col };
                break;

            case UnOp.PostInc or UnOp.PostDec:
            {
                // The old value is held while the new one is stored, and is
                // what the expression is worth.
                RequireAssignable(u.Operand, u.Op == UnOp.PostInc ? "increment" : "decrement");
                SubjectExpr old = new() { Line = u.Line, Col = u.Col };
                made = new PatternExpr
                {
                    Subject = u.Operand,
                    Test = new SequenceExpr
                    {
                        Effect = new ExprStmt
                        {
                            Expr = new AssignExpr
                            {
                                Target = u.Operand, Value = Apply(new SubjectExpr { Line = u.Line, Col = u.Col }),
                                Line = u.Line, Col = u.Col,
                            },
                            Line = u.Line, Col = u.Col,
                        },
                        Value = old, Line = u.Line, Col = u.Col,
                    },
                    Line = u.Line, Col = u.Col,
                };
                break;
            }

            default:
                made = Apply(u.Operand);
                break;
        }
        _r.Rewrites[u] = made;
        return CheckExpr(made);
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
    private MethodSymbol? Operator(TypeSymbol? holder, string name, Type l, Type r, long? lc = null, long? rc = null)
    {
        if (holder is null)
        {
            return null;
        }

        bool Takes(Type given, long? constant, Type want)
            => Convertible(given, want) || constant is not null && UserConversion(given, want, false, constant) is not null;

        foreach (MethodSymbol m in holder.FindMethods(name))
        {
            if (m.Static && m.Params.Count == 2
                && Takes(l, lc, m.Params[0].Type) && Takes(r, rc, m.Params[1].Type))
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
            BinOp.UShr => "op_UnsignedRightShift",
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
        // A DOTTED NAME IN A CONSTANT PATTERN THAT NAMES A TYPE IS A TYPE TEST.
        // The parser cannot tell `t is Prim.I8` (a constant) from
        // `o is System.Text.StringBuilder` (a type) -- both are a name with dots
        // in it and nothing after -- so it reads both as constants, and here,
        // with the symbols known, a name that is a type becomes the IsExpr it
        // means, as C# binds `is` against a type first. The mirror of the bare
        // name an IsExpr turns into a constant below.
        if (b.PatternConstant && b.Op == BinOp.Eq && Dotted(b.Right) is string dotted && dotted.Contains('.') && NamesType(dotted, b.Right))
        {
            IsExpr typeTest = new()
            {
                Operand = b.Left,
                Type = new TypeRef { Name = dotted, Line = b.Right.Line, Col = b.Right.Col },
                Line = b.Line,
                Col = b.Col,
            };
            _r.Rewrites[b] = typeTest;
            return CheckExpr(typeTest);
        }

        // THE RIGHT SIDE OF '&&' KNOWS WHAT THE LEFT SIDE PROVED.
        //
        // `x != null && x.Kind == K` is the commonest line in C# there is, and
        // it only runs the right side when the left was true -- so within it,
        // x is not null. Checking both halves in the same state reported that x
        // may be null in the one place it provably is not.
        //
        // It matters more than it looks, because `is { Kind: K }` lowers to
        // exactly this shape: the null test and the member read, joined by &&.
        if (b.Op is BinOp.AndAlso or BinOp.OrElse && LateLogical(b) is Type lateLogic)
        {
            return lateLogic;
        }

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

        // A LAMBDA ADDED TO OR TAKEN FROM A DELEGATE is that delegate's type,
        // as C# target-types it by the left operand: `d + (x => x * 2)`. It
        // has no type of its own to be checked with first, so the operation
        // is the call it becomes (DelegateCombination), whose argument is
        // wanted as the delegate.
        if (b.Op is BinOp.Add or BinOp.Sub && !l.IsError && !l.IsArray && !l.IsNullableValue
            && l.Symbol is { Decl.IsDelegate: true } && (b.Right is LambdaExpr || HoldsLambda(b.Right)))
        {
            CallExpr withLambda = DelegateCombination(b.Op == BinOp.Add, b.Left, b.Right, b, l);
            _r.Rewrites[b] = withLambda;
            Type lambdaMade = CheckExpr(withLambda);
            // The left's delegate type: the helper answers Delegate, the one value it is.
            return lambdaMade.IsError ? lambdaMade : b.Op == BinOp.Add ? l.AsNonNullable() : l.AsNullable();
        }

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
        // A METHOD GROUP ON THE RIGHT is the left's delegate, as a lambda there
        // is: `_handler ??= Handle` -- which the parser makes `_handler =
        // _handler ?? Handle` -- named a method and was read as its call.
        if (b.Op == BinOp.Coalesce && !l.IsError && b.Right is not LambdaExpr && !_r.Rewrites.ContainsKey(b.Right)
            && l.AsNonNullable() is { Symbol: { Decl.IsDelegate: true } } delegated
            && MethodGroupLambda(b.Right, delegated) is LambdaExpr grouped)
        {
            _r.Rewrites[b.Right] = grouped;
            r = CheckLambda(grouped, delegated);
        }
        _wanted = beforeFallback;

        if (l.IsError || r.IsError)
        {
            return Type.Error;
        }

        // AN OPERATOR WITH A DYNAMIC OPERAND, bound when the program runs.
        if (_usesDynamic && LateBinary(b, l, r) is Type lateBinary)
        {
            return lateBinary;
        }

        AdoptUnsignedConstant(b.Left, ref l, b.Right, ref r);

        // `a + b` AND `a - b` OVER TWO DELEGATES OF ONE TYPE are C#'s too, not
        // only their compound forms: Delegate.Combine and Delegate.Remove,
        // typed as the delegate (DelegateCombination). The sum of anything
        // with a delegate that is there is there; a difference may be nothing.
        // A method group on the right becomes the left's delegate, as C#
        // target-types it.
        if (b.Op is BinOp.Add or BinOp.Sub && !l.IsArray && !l.IsNullableValue
            && l.Symbol is { Decl.IsDelegate: true } combined
            && (r.Prim == Prim.NullLiteral || IsFunctionSource(b.Right)
                || !r.IsArray && !r.IsNullableValue && ReferenceEquals(r.Symbol, combined)
                || IsDelegateValue(r) && Variant(r, l.AsNonNullable())))
        {
            CallExpr call = DelegateCombination(b.Op == BinOp.Add, b.Left, b.Right, b, l);
            _r.Rewrites[b] = call;
            Type made = CheckExpr(call);
            if (made.IsError) return made;
            return b.Op == BinOp.Add && (!l.Nullable || !r.Nullable && r.Prim != Prim.NullLiteral)
                ? l.AsNonNullable() : l.AsNullable();
        }

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
                // AND A THROW ON THE RIGHT GIVES THE LEFT'S TYPE, NULL TAKEN
                // OFF: control never leaves the right side with a value, so
                // `Find(n) ?? throw ...` over a `(ObjectFile, Symbol)?` is the
                // tuple. The throw is typed as the machine word so it fits
                // anywhere, and taken as the answer that made it `object`.
                if (b.Right is ThrowExpr)
                {
                    return l.IsNullableValue ? l.Underlying : l.AsNonNullable();
                }

                if (l.IsNullableValue && !r.Nullable && !r.IsNullableValue
                    && Convertible(r, l.Underlying))
                {
                    return l.Underlying;
                }

                // C#'s RULE for the rest: the left's type when the right
                // converts to it, and otherwise the right's when the left
                // converts to that. An array going to one of its interfaces is
                // wrapped in its view, on whichever side it is.
                {
                    Type left = l.AsNonNullable();
                    Type right = r.AsNonNullable();
                    if (!l.IsNullableValue && r.Prim != Prim.NullLiteral && !right.Equals(left) && Convertible(right, left) && !Convertible(left, right))
                    {
                        CoalesceView(b.Right, right, left);
                        return r.Nullable ? l : left;
                    }
                    if (Convertible(left, right))
                    {
                        CoalesceView(b.Left, left, right);
                    }
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

                // TWO TUPLES COMPARE ITEM BY ITEM (C# 12.12.11): each side
                // evaluated once, left then right, then `==` of each pair of
                // items in order, joined by `&&` -- `||` of `!=` for `!=`. A
                // tuple is an object here, and compared as one it was two
                // references: equal tuples were never `==`.
                if (TupleArity(l) is int arity && arity > 0 && TupleArity(r) == arity)
                {
                    Expr? chain = null;
                    for (int i = 1; i <= arity; i++)
                    {
                        BinaryExpr pair = new()
                        {
                            Op = b.Op,
                            Left = new MemberExpr { Target = new SubjectExpr { Outer = 1, Line = b.Line, Col = b.Col }, Name = "Item" + i, Line = b.Line, Col = b.Col, File = b.File },
                            Right = new MemberExpr { Target = new SubjectExpr { Outer = 0, Line = b.Line, Col = b.Col }, Name = "Item" + i, Line = b.Line, Col = b.Col, File = b.File },
                            Line = b.Line, Col = b.Col, File = b.File,
                        };
                        chain = chain is null ? pair : new BinaryExpr
                        {
                            Op = b.Op == BinOp.Eq ? BinOp.AndAlso : BinOp.OrElse, Left = chain, Right = pair,
                            Line = b.Line, Col = b.Col, File = b.File,
                        };
                    }
                    PatternExpr items = new()
                    {
                        Subject = b.Left,
                        Test = new PatternExpr { Subject = Deeper(b.Right), Test = chain!, Line = b.Line, Col = b.Col, File = b.File },
                        Line = b.Line, Col = b.Col, File = b.File,
                    };
                    _r.Rewrites[b] = items;
                    CheckExpr(items);
                    return Type.Bool;
                }

                // A CONSTANT PATTERN OVER AN `object` asks whether the value IS
                // the constant -- the same type and equal -- which for a boxed
                // value is object.Equals of the two, not whether they are one
                // reference. `object o = 5; o is 5` answered false, and so did
                // every constant label switched over an object.
                if (b.PatternConstant && l.Prim == Prim.Any && r.Prim is not (Prim.NullLiteral or Prim.Any))
                {
                    CallExpr same = new()
                    {
                        Target = new MemberExpr
                        {
                            Target = new NameExpr { Name = "object", Line = b.Line, Col = b.Col, File = b.File },
                            Name = "Equals", Line = b.Line, Col = b.Col, File = b.File,
                        },
                        Line = b.Line, Col = b.Col, File = b.File,
                    };
                    same.Args.Add(b.Left);
                    same.Args.Add(new CastExpr
                    {
                        Type = new TypeRef { Name = "object", Line = b.Right.Line, Col = b.Right.Col },
                        Operand = b.Right, Line = b.Right.Line, Col = b.Right.Col, File = b.File,
                    });
                    Expr asked = b.Op == BinOp.Eq
                               ? same
                               : new UnaryExpr { Op = UnOp.Not, Operand = same, Line = b.Line, Col = b.Col, File = b.File };
                    _r.Rewrites[b] = asked;
                    return CheckExpr(asked);
                }

                if (l.Prim == Prim.NullLiteral || r.Prim == Prim.NullLiteral)
                {
                    Type other = l.Prim == Prim.NullLiteral ? r : l;

                    // A PATTERN'S OWN NULL TEST OVER A VALUE TYPE IS NOT A
                    // TEST. C# writes no such test there -- a struct is always
                    // there -- so the answer it always gives is written down
                    // instead, and the members are checked as the pattern says.
                    //
                    // AND IN A GENERIC COPY: `default(T) == null` is C# over an
                    // unconstrained T, false for a value type, and the copy made
                    // for an int or a KeyValuePair is where that T became one.
                    bool inCopy = _member is MethodDecl { LocalCopy: true } || _thisType?.Decl?.Specialised == true;
                    if ((b.PatternNullTest || inCopy) && !other.IsReference && !other.IsNullableValue
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

                // TWO DELEGATES ARE EQUAL BY WHAT THEY CALL, not by being one
                // object: C# gives every delegate type `==` and `!=`, and
                // .NET's answer them as Delegate.Equals does -- the same method
                // on the same target, and for a multicast the same targets in
                // the same order. Two lambdas are two methods and never equal;
                // one method group converted twice is one method and is.
                // `d == null` is a reference test as ever, above.
                if (IsDelegateValue(l) && IsDelegateValue(r))
                {
                    CallExpr same = new()
                    {
                        Target = new MemberExpr
                        {
                            Target = new NameExpr { Name = DelegatesHelper, Line = b.Line, Col = b.Col, File = b.File },
                            Name = "Same", Line = b.Line, Col = b.Col, File = b.File,
                        },
                        Line = b.Line, Col = b.Col, File = b.File,
                    };
                    same.Args.Add(b.Left);
                    same.Args.Add(b.Right);
                    same.WritableArgNames.Add(null);
                    same.WritableArgNames.Add(null);
                    Expr compared = b.Op == BinOp.Eq
                                  ? same
                                  : new UnaryExpr { Op = UnOp.Not, Operand = same, Line = b.Line, Col = b.Col, File = b.File };
                    _r.Rewrites[b] = compared;
                    return CheckExpr(compared);
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
                // TWO STRUCTS WITH NO == OF THEIR TYPE'S (C# CS0019): there is
                // no comparison to make, and what was made compared the two
                // blocks' addresses -- Guid had no operator, and two equal
                // Guids answered False.
                else if (StructWithoutEquality(l) && StructWithoutEquality(r)
                         && !(_member is MethodDecl { LocalCopy: true } || _thisType?.Decl?.Specialised == true))
                {
                    Error(b, $"Operator '{(b.Op == BinOp.Eq ? "==" : "!=")}' cannot be applied to operands of type '{l}' and '{r}'");
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
                    return Type.Plain(bits, bits.EnumUnderlying);
                }
                goto case BinOp.Add;

            case BinOp.Shl:
            case BinOp.Shr:
            case BinOp.UShr:
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
