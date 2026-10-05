#nullable enable
namespace Corsac.Lang;

/// <summary>
/// Turns generic declarations into ordinary ones by making a copy per set of
/// type arguments.
///
/// This runs BEFORE binding, as a source-to-source rewrite: <c>Box&lt;int&gt;</c>
/// becomes a real type named <c>Box$int</c> with every <c>T</c> replaced, and
/// the reference is rewritten to name it. Everything downstream — the binder,
/// layout, code generation — then sees only concrete types and needs to know
/// nothing about generics at all.
///
/// Specialising rather than boxing is the right trade WITH an optimiser,
/// because it is what makes inlining and devirtualisation possible: a
/// <c>Box&lt;int&gt;</c> holds an int, not a pointer to one. It costs code size,
/// which is the cheapest thing we have.
/// </summary>
public sealed class Monomorphiser
{
    private readonly Dictionary<string, TypeDecl> _generic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeDecl> _made = new(StringComparer.Ordinal);
    private readonly List<CompileError> _errors = new();
    private readonly string _file;
    private readonly Action<string>? _requireDeclaration;
    private readonly Metadata.DeclarationBatch _templateBatch = new();
    private readonly Queue<Job> _pending = new();

    /// <summary>One specialisation waiting to be made.</summary>
    private readonly record struct Job(
        TypeDecl Template,
        List<TypeRef> Args,
        string Name,
        bool External,
        string? Canon);

    /// <summary>The names asked of the declarations already (GenericPath): once each.</summary>
    private readonly HashSet<string> _demanded = new(StringComparer.Ordinal);

    /// <summary>Whether this compilation is building a library of its own.</summary>
    private bool _library;

    /// <summary>
    /// Type arguments that are one machine word in the integer bank, and
    /// therefore share compiled code. See TypeDecl.Canon.
    /// </summary>
    public const string CanonName = "__canon";

    /// <summary>
    /// How many specialised types this process has made, and how many members
    /// they carried: what a unit's imported generics cost, printed with the
    /// declaration statistics.
    /// </summary>
    public static long Specialisations, SpecialisedMembers;

    /// <summary>Names that are NOT a machine word: narrower, or in the other bank.</summary>
    private static readonly HashSet<string> Narrow = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "char", "float", "double",
    };

    /// <summary>Every struct and enum declared here, which are not words either.</summary>
    private readonly HashSet<string> _byValue = new(StringComparer.Ordinal);

    /// <summary>Names declared as a class or interface, which always are.</summary>
    private readonly HashSet<string> _byRef = new(StringComparer.Ordinal);

    /// <summary>
    /// Every type declared here, by the path that names it: `Section` for one
    /// written at the top level, `ImageFile.Section` for one written inside.
    /// </summary>
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    /// <summary>
    /// Nested types by their simple name, null where more than one shares it.
    /// The checker keeps the same index and for the same reason.
    /// </summary>
    private readonly Dictionary<string, string?> _soleNested = new(StringComparer.Ordinal);

    /// <summary>The type whose declaration is being rewritten, spelled in full.</summary>
    private string _scope = "";

    /// <summary>
    /// The namespace and the using directives of whatever is being rewritten.
    ///
    /// A type argument is spelled so it means the same thing read from
    /// anywhere, which is what keeps `List<Block>` in Corsac.Lang.Opt and
    /// `List<Block>` in Corsac.Lang two different lists -- and the only thing
    /// that knows which Block is meant is the file the instantiation was
    /// written in, which is where its aliases and imports are.
    /// </summary>
    private string _inNamespace = "";
    private FileScope? _usings;

    public IReadOnlyList<CompileError> Errors => _errors;

    public Monomorphiser(string file, Action<string>? requireDeclaration = null)
    {
        _file = file;
        _requireDeclaration = requireDeclaration;
    }

    /// <summary>
    /// One copy of a generic METHOD, with its type arguments put in.
    ///
    /// The same rewrite a generic type gets, applied to a single member: every
    /// T in the signature and the body becomes what T was inferred to be, the
    /// copy is renamed so the call site can name it, and its type parameters
    /// are gone -- it is an ordinary method now.
    ///
    /// Driven from outside because only the CHECKER knows what T is: a call
    /// says `list.Where(n => n.Ready)` and never says Node anywhere.
    /// </summary>
    /// <param name="valueTypes">The names of the unit's structs and enums,
    /// specialisations included: a `T?` over one of them stays the value
    /// type (Sub's rule for an unconstrained T), which this copy cannot tell
    /// from the arguments' names alone.</param>
    /// <summary>
    /// A hoisted generic local function carried into a copy of the method it
    /// was written in (Frontend.RehostLocals): the copy's type arguments put
    /// in for its type parameters, the function's own type parameters kept.
    /// </summary>
    public static MethodDecl Rehost(MethodDecl local, IReadOnlyList<TypeParam> outer, IReadOnlyList<TypeRef> args,
                                    string name, IEnumerable<string>? valueTypes = null)
    {
        Monomorphiser m = new("<rehost>");
        if (valueTypes is not null)
        {
            m._byValue.UnionWith(valueTypes);
        }
        Dictionary<string, TypeRef> map = new(StringComparer.Ordinal);
        for (int i = 0; i < outer.Count && i < args.Count; i++)
        {
            m.Settled(args[i]);
            map[outer[i].Name] = args[i];
            if (outer[i].Struct) m._structParams.Add(outer[i].Name);
        }
        MethodDecl made = (MethodDecl)m.RewriteMember(local, map, local.Name);
        made.Name = name;
        // A TYPE PARAMETER IT CARRIED FOR WHAT IS AROUND IT (CarriedTypeParams)
        // is put in here with the rest: the copy's captures are of the type
        // itself, and a parameter left over is one no call could infer.
        if (local.CarriedTypeParams.Count > 0)
        {
            made.WritableTypeParams.RemoveAll(tp => map.ContainsKey(tp.Name) && local.CarriedTypeParams.Contains(tp.Name));
            made.CarriedTypeParams = local.CarriedTypeParams.Where(n => !map.ContainsKey(n)).ToList();
        }
        return made;
    }

    public static MethodDecl Specialise(MethodDecl template, IReadOnlyList<TypeRef> args, string name,
                                        IEnumerable<string>? valueTypes = null)
    {
        Monomorphiser m = new("<specialise>");
        if (valueTypes is not null)
        {
            m._byValue.UnionWith(valueTypes);
        }
        Dictionary<string, TypeRef> map = new(StringComparer.Ordinal);

        for (int i = 0; i < template.TypeParams.Count && i < args.Count; i++)
        {
            m.Settled(args[i]);
            map[template.TypeParams[i].Name] = args[i];
            m._paramInfo[template.TypeParams[i].Name] = template.TypeParams[i];
            if (template.TypeParams[i].Struct) m._structParams.Add(template.TypeParams[i].Name);
        }

        // A SHARED METHOD COPY (CopyName): which of its type parameters only
        // run time knows, for the tests of interfaces over them (Shaped).
        for (int i = 0; i < template.TypeParams.Count && i < args.Count; i++)
        {
            if (args[i].CanonIndex <= -2)
            {
                (m._shapeParams ??= new(StringComparer.Ordinal))[template.TypeParams[i].Name] = i;
            }
        }

        MethodDecl made = (MethodDecl)m.RewriteMember(template, map, template.Name);

        made.WritableTypeParams.Clear();
        made.Name = name;

        // A COPY IS NEVER VIRTUAL. A generic virtual method has no slot for its
        // copies to take (MethodSymbol.GenericVirtual): a call reaches them
        // through a dispatch by type, and a copy left `virtual` or `override`
        // was given a slot of its own -- a different vtable in every unit
        // that happened to make a different set of copies.
        //
        // NOR AN EXPLICIT IMPLEMENTATION, for the same reason: the interface
        // member it implements is the generic one, which the dispatch finds by
        // the template; the copy is an ordinary method of its class, found by
        // its own name.
        const Mods dispatch = Mods.Virtual | Mods.Override | Mods.Abstract;
        if ((made.Mods & dispatch) == 0 && made.ExplicitInterface is null)
        {
            return made;
        }

        MethodDecl plain = new()
        {
            Name = made.Name, Mods = made.Mods & ~dispatch, Returns = made.Returns, IsCtor = made.IsCtor,
            Body = made.Body, Init = made.Init, VtableSlotHint = -1, NotNullIfNotNull = made.NotNullIfNotNull,
            ExplicitInterface = null, Line = made.Line, Col = made.Col,
            LocalCopy = made.LocalCopy, File = made.File, TemplateIndex = made.TemplateIndex,
            Scope = made.Scope, Namespace = made.Namespace, OwnedImplementation = made.OwnedImplementation,
        };
        plain.WritableAttributes.AddRange(made.Attributes);
        plain.Params.AddRange(made.Params);
        return plain;
    }

    /// <summary>The name a specialised method gets, readable on purpose.</summary>
    public static string MethodName(string baseName, IReadOnlyList<TypeRef> args)
        => MangledName(baseName, args.ToList());

    /// <summary>What a shared method copy's name ends with, before the count of its hidden arguments.</summary>
    public const string HiddenTypeArgumentsMark = "$__targs";

    /// <summary>
    /// THE NAME OF A GENERIC METHOD'S COPY: its name, its arguments and the
    /// member's place -- two overloads specialise at one T -- and, for a
    /// SHARED METHOD COPY, a mark and how many type parameters it has.
    ///
    /// A shared method copy is the one a shared generic copy's code calls
    /// with a type argument only run time knows (Type.CanonParam): its body
    /// is the copy over object, as every such call reached before, but it is
    /// given each type argument's descriptor as a hidden argument after the
    /// declared ones (Lowering.HiddenTypeArguments), so that `x is IList<U>`
    /// in it asks the object for the IList of what U is for this call
    /// (Runtime.ShapedAs). The mark is in the NAME because the name is what
    /// every unit agrees on: a caller and the copy it reaches, compiled
    /// anywhere, both read the hidden arguments' count off it
    /// (SharedMethodCopy). A copy over object called with object meant is
    /// the plain one, as it always was.
    /// </summary>
    public static string CopyName(string baseName, IReadOnlyList<TypeRef> args, int member)
    {
        string name = MethodName(baseName, args) + "$" + member;
        return args.Any(a => a.CanonIndex <= -2) ? name + HiddenTypeArgumentsMark + args.Count : name;
    }

    /// <summary>How many hidden type arguments a method copy takes (CopyName): 0 for any other method.</summary>
    public static int SharedMethodCopy(string name)
    {
        int at = name.LastIndexOf(HiddenTypeArgumentsMark, StringComparison.Ordinal);
        if (at < 0) return 0;
        int count = 0;
        for (int i = at + HiddenTypeArgumentsMark.Length; i < name.Length; i++)
        {
            if (name[i] < '0' || name[i] > '9') return 0;
            count = count * 10 + (name[i] - '0');
        }
        return count;
    }

    public static CompilationUnit Expand(CompilationUnit unit, string file, out IReadOnlyList<CompileError> errors)
        => Expand(unit, file, false, out errors);

    public static CompilationUnit Expand(CompilationUnit unit, string file, bool library, out IReadOnlyList<CompileError> errors,
        Action<string>? requireDeclaration = null)
    {
        Monomorphiser m = new(file, requireDeclaration) { _library = library };
        CompilationUnit result = m.Run(unit);
        errors = m._errors;
        return result;
    }

    /// <summary>
    /// Whether one compiled copy can serve this type argument.
    ///
    /// A reference is an address, and so is an array; long, ulong and a pointer
    /// are the same width in the same register bank. Every load, store, array
    /// stride and field offset comes out identical, so the instructions for
    /// <c>List&lt;string&gt;</c> ARE the instructions for <c>List&lt;long&gt;</c>.
    ///
    /// Anything narrower, anything in the float bank, and anything held BY VALUE
    /// is not: a struct has a size of its own and an int is four bytes, so the
    /// strides differ and the code genuinely has to differ with them.
    ///
    /// Answers NO when it cannot tell. A wrong yes shares code between two
    /// layouts and corrupts memory a long way from here; a wrong no compiles a
    /// copy that was not needed, which costs bytes and nothing else.
    /// </summary>
    /// <summary>
    /// A type name written inside one type, spelled so it means the same thing
    /// read from anywhere: `Section` inside ImageFile becomes
    /// `ImageFile.Section`.
    ///
    /// The walk is C#'s -- this type, then the type it was written inside, out
    /// to the top level -- with the same last resort the checker uses: a nested
    /// name that belongs to exactly one type in the program is taken to mean
    /// that one. Anything already qualified, and anything that is a top-level
    /// type, is returned untouched.
    /// </summary>
    private TypeRef Qualify(TypeRef r)
    {
        // SPELT WHERE IT WAS WRITTEN already: a type argument, and every copy
        // made of it in substituting it, is never read again from the scope of
        // the template it was spliced into (Settled). Read again, a program's
        // `Version` inside ReadOnlyCollection<T> -- declared in a namespace
        // beneath System -- became System.Version, and the copy's interfaces
        // were IList<System.Version>.
        if (_settled.Contains(r))
        {
            return r;
        }
        List<TypeRef> args = r.Args.Count == 0 ? r.Args : r.Args.Select(Qualify).ToList();
        List<TypeRef>? useArgs = r.UseArgs?.Select(Qualify).ToList();
        string full = Path(r.Name);

        if (ReferenceEquals(args, r.Args) && ReferenceEquals(useArgs, r.UseArgs) && full == r.Name)
        {
            return r;
        }

        return new TypeRef
        {
            Name = full,
            Arguments = args,
            ArrayRank = r.ArrayRank,
            Nullable = r.Nullable,
            ElementNullable = r.ElementNullable,
            InnerNullable = r.InnerNullable,
            PointerDepth = r.PointerDepth,
            TupleNames = r.TupleNames,
            UseArgs = useArgs,
            Line = r.Line,
            Col = r.Col,
        };
    }

    /// <summary>Where a simple name points from the scope being rewritten.</summary>
    private string Path(string name)
    {
        // A DOTTED NAME MAY STILL BE RELATIVE. `Lang.TypeRef` written inside
        // Corsac is Corsac.Lang.TypeRef, and taking it as written mangled
        // `List<Lang.TypeRef>` and `List<Corsac.Lang.TypeRef>` into two names
        // for one type -- which then would not convert to each other.
        // OUTWARDS FROM WHERE IT WAS WRITTEN, and then from the namespace it
        // was written in -- the same walk the checker makes, and it has to be
        // the same or a type argument is mangled under one name and looked for
        // under another.
        // The library's global code finds its own moved type first (MoveToSystem).
        if (_libraryCode && _inNamespace.Length == 0 && _movedToSystem.ContainsKey(name))
        {
            return _movedToSystem[name];
        }
        foreach (string from in new[] { _scope, _inNamespace })
        {
            for (string at = from; at.Length > 0; )
            {
                if (_paths.Contains(at + "." + name))
                {
                    return at + "." + name;
                }

                if (Named(at, name) is string there)
                {
                    return there;
                }

                int cut = at.LastIndexOf('.');
                at = cut < 0 ? "" : at[..cut];
            }
        }

        if (_paths.Contains(name))
        {
            return name;
        }

        if (Named("", name) is string outermost)
        {
            return outermost;
        }

        // WRITTEN WITH ITS NAMESPACE, a library type is still the one the
        // checker finds without it: the library declares its types with no
        // namespace, and the checker trims a qualifier that names no type
        // (Binder.NamesType). Kept as written, `(System.Text.StringBuilder,
        // int)` was mangled one way where it was written and another where
        // the checker closed a template over the tuple it resolved -- and
        // List<T>.Enumerator over it was named by neither, left as the
        // template, with its MoveNext undefined at the link.
        for (int dot = name.IndexOf('.'); dot > 0; dot = name.IndexOf('.', dot + 1))
        {
            if (_paths.Contains(name[..dot]))
            {
                break;                          // a type's nested name, not a namespace
            }
            string rest = name[(dot + 1)..];
            if (_paths.Contains(rest))
            {
                return rest;
            }
        }

        // NOT YET LOADED IS NOT ABSENT. A type's private nested `Entry`, its
        // declaration read from the index only where something asks for it,
        // was missing from a unit that rewrote the type's field
        // `Dictionary<..., Entry>`, and the one other nested `Entry` that unit
        // had loaded -- DeclarationCatalog's -- was taken as the sole one: two
        // layouts of the field's initialiser at the link, in whichever build
        // happened to load them in that order. Asked of the index first, scope
        // by scope; a demand reruns the pass with it loaded.
        if (_requireDeclaration is not null && name.IndexOf('.') < 0)
        {
            foreach (string from in new[] { _scope, _inNamespace })
            {
                for (string at = from; at.Length > 0; )
                {
                    Demand(at + "." + name);
                    int cut = at.LastIndexOf('.');
                    at = cut < 0 ? "" : at[..cut];
                }
            }
        }

        return _soleNested.TryGetValue(name, out string? sole) && sole is not null ? sole : name;
    }

    /// <summary>Asks the declaration index for one type, once, recording what it must load.</summary>
    private void Demand(string key)
    {
        if (_requireDeclaration is null || _demanded.Contains(key)) return;
        string kept = key.Substring(0);
        _demanded.Add(kept);
        try { _requireDeclaration(kept); }
        catch (Metadata.DeclarationDemand demand) { _templateBatch.Add(demand); }
    }

    /// <summary>
    /// What the using directives written in one namespace make this name mean:
    /// an alias first, and then the namespaces imported there -- exactly one of
    /// which may have it, or the name is ambiguous and means nothing.
    /// </summary>
    private string? Named(string at, string name)
    {
        if (_usings is null)
        {
            return null;
        }

        foreach ((string In, string Alias, string Target) alias in _usings.Aliases)
        {
            if (alias.In == at && alias.Alias == name && _paths.Contains(alias.Target))
            {
                return alias.Target;
            }
        }

        string? found = null;

        foreach ((string In, string Namespace) import in _usings.Imports)
        {
            if (import.In != at || !_paths.Contains(import.Namespace + "." + name))
            {
                continue;
            }

            if (found != null)
            {
                return null;
            }
            found = import.Namespace + "." + name;
        }

        return found;
    }

    private bool IsWord(TypeRef r)
    {
        if (r.ArrayRank > 0 || r.PointerDepth > 0)
        {
            return true;                        // an address, whatever it addresses
        }

        if (Narrow.Contains(r.Name) || _byValue.Contains(r.Name))
        {
            return false;
        }

        // A NUMBER THE SIZE OF A WORD IS STILL A NUMBER. The one compiled copy
        // treats its T as a reference -- `held + " "` reads the vtable and calls
        // ToString through it -- so nint, nuint, and long where the word is 64
        // bits, get copies of their own as int does. Sharing theirs with the
        // references ran a long's value as an object's address.
        if (r.Name is "nint" or "nuint" or "long" or "ulong")
        {
            return false;
        }

        if (r.Name is "string" or "object" or CanonName || _byRef.Contains(r.Name))
        {
            return true;
        }

        // A SPECIALISATION IS WHAT ITS TEMPLATE IS, from the moment it is
        // claimed. Its declaration reaches _byRef only once it has been made,
        // so `List<List<int>>` shared the canonical copy in a unit that had
        // made List<int> first and was a copy of its own in one that had not
        // -- two descriptors of one name, which the link refused.
        return _claimedByRef.TryGetValue(r.Name, out bool reference) && reference;
    }

    /// <summary>Every specialisation claimed (Instantiate), and whether its template is a reference type.</summary>
    private readonly Dictionary<string, bool> _claimedByRef = new(StringComparer.Ordinal);

    private CompilationUnit Run(CompilationUnit unit)
    {
        // KEYED BY NAME AND ARITY, because `Func<A, R>` and `Func<A, B, R>` are
        // two types that share a name -- which is how .NET spells them, and
        // therefore how anything hoping to compile C# has to spell them too.
        //
        // Keyed by name alone, the second declaration replaced the first and
        // every use of the other arity was reported as taking the wrong number
        // of type arguments. The CLR does exactly this and calls them Func`2
        // and Func`3; the backtick is the only part not worth copying.
        // A PROGRAM'S TYPE AND THE LIBRARY'S OF ONE NAME, generic or not: the
        // program's is the name's in the program, and the library's moves into
        // System (MoveToSystem), where the library's code finds it first
        // (Path, GenericPath) -- so that `IEquatable<Index>` written in the
        // library's Index is IEquatable<System.Index>, a specialisation of its
        // own, and never the program's. Moved here, before any path is known,
        // so that every path is its moved one.
        HashSet<string> programs = new(unit.Types.Where(t => !t.FromLibrary && t.Outer is null)
            .Select(t => Arity(t.Name, t.TypeParams.Count)), StringComparer.Ordinal);
        // Never the prelude's: its Math is the intrinsic half of the library's
        // Math, not a library type a program's could shadow (Binder's
        // "EXTENDING A PRELUDE TYPE"). A unit bound from the declaration index
        // has the library's Math as an imported header, which is not marked as
        // the library's, and moving the prelude's aside for it put the
        // intrinsics at System.Math -- where library code looks first -- so
        // Math.Min(a, b) on two ints found only Min(double, double).
        foreach (TypeDecl t in unit.Types.Where(t => t.FromLibrary && t.Outer is null && t.Namespace.Length == 0
                                                     && t.File != "<prelude>").ToList())
        {
            if (programs.Contains(Arity(t.Name, t.TypeParams.Count))) MoveToSystem(t);
        }
        if (_movedToSystem.Count > 0)
        {
            foreach (TypeDecl t in unit.Types.Where(t => t.FromLibrary && t.Outer is not null))
            {
                if (MovedPath(t.Outer!) is not string outer) continue;
                t.Outer = outer;
                if (t.Namespace.Length == 0) t.Namespace = "System";
            }
        }

        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0))
        {
            string key = Arity(TemplatePath(t), t.TypeParams.Count);
            // A PROGRAM'S TEMPLATE AND THE LIBRARY'S OF ONE NAME: the program's
            // is the name's in the program, and the library's moves into
            // System, where the library's own code finds it first (GenericPath;
            // Binder.MoveToSystem is the same for a type that is not generic).
            if (_generic.TryGetValue(key, out TypeDecl? other) && other.FromLibrary != t.FromLibrary)
            {
                TypeDecl library = other.FromLibrary ? other : t;
                _generic[key] = other.FromLibrary ? t : other;
                MoveToSystem(library);
                continue;
            }
            _generic[key] = t;
        }
        // Templates nested in one moved go with it.
        if (_movedToSystem.Count > 0)
        {
            foreach (TypeDecl t in unit.Types.Where(t => t.FromLibrary && t.Outer is not null && t.TypeParams.Count > 0).ToList())
            {
                if (MovedPath(t.Outer!) is not string moved) continue;
                string was = Arity(TemplatePath(t), t.TypeParams.Count);
                if (_generic.TryGetValue(was, out TypeDecl? held) && ReferenceEquals(held, t)) _generic.Remove(was);
                t.Outer = moved;
                if (t.Namespace.Length == 0) t.Namespace = "System";
                _generic[Arity(TemplatePath(t), t.TypeParams.Count)] = t;
            }
        }

        // WHICH NAMES ARE A MACHINE WORD, gathered before anything is
        // instantiated, because whether one compiled copy can serve a type
        // argument is a question about that argument's SHAPE and the shape is
        // in its declaration.
        foreach (TypeDecl t in unit.Types)
        {
            string path = t.Outer is null ? t.Name : t.Outer + "." + t.Name;

            _paths.Add(path);

            if (t.Outer is not null)
            {
                _soleNested[t.Name] = _soleNested.ContainsKey(t.Name) ? null : path;
            }

            // BOTH SPELLINGS, because a type argument may arrive either way:
            // qualified once it has been through Qualify, and simple in a
            // declaration that has not.
            if (t.Kind is TypeKind.Struct or TypeKind.Enum)
            {
                _byValue.Add(t.Name);
                _byValue.Add(path);
            }
            else
            {
                _byRef.Add(t.Name);
                _byRef.Add(path);
            }
        }

        // SPECIALISATIONS ALREADY IN THE UNIT ARE ALREADY MADE.
        //
        // This runs more than once now: a generic METHOD is specialised by the
        // checker, and the result has to go round again so the new bodies get
        // their own generic types instantiated. On the second pass `List$Node`
        // is sitting right there as an ordinary declaration, and remaking it
        // from the template is how it came to be declared twice.
        foreach (TypeDecl t in unit.Types.Where(t => t.Specialised))
        {
            _claimed.Add(t.Name);
            _made[t.Name] = t;
        }

        if (_generic.Count == 0 && _requireDeclaration is null)
        {
            return unit;
        }

        // A LIBRARY COMPILES THE CANONICAL COPY OF EVERY TEMPLATE IT OWNS,
        // whether or not it uses one itself.
        //
        // This is the copy every consumer links instead of cloning, so it has to
        // be there before any consumer asks -- and a library cannot know what
        // its consumers will instantiate. Compiling it unconditionally costs the
        // library the one copy that was going to exist somewhere anyway.
        if (_library)
        {
            foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0 && !t.External && !t.Elsewhere && Shareable(t)))
            {
                Canonicalise(t);
            }
        }

        // AND A CONSUMER NAMES EVERY CANONICAL COPY ITS LIBRARIES OWN.
        //
        // A library's header declares the signatures a consumer compiles
        // against, and a generic method's parameter has already been erased to
        // the canonical copy by the time it is written -- `string.Join` takes a
        // `List$__canon`. So the consumer must be able to resolve that name
        // even when it never instantiates a list of its own, or the header it
        // was handed refers to a type it was not told about.
        //
        // External, so this declares the name and imports the code; it does not
        // compile a second copy of anything.
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0 && t.External && Shareable(t)))
        {
            Canonicalise(t);
        }

        CompilationUnit output = new() { Line = unit.Line, Col = unit.Col };
        output.Usings.AddRange(unit.Usings);
        output.TupleNamings.AddRange(unit.TupleNamings);
        output.RegistrySchemas.AddRange(unit.RegistrySchemas);

        // Expansion builds a NEW unit rather than editing this one in place,
        // and the binder that reads warnings out of it -- the only reader of
        // Pragmas -- only ever sees the expanded copy. Left behind here, a
        // `#pragma warning disable` would parse correctly and then silence
        // nothing.
        output.Pragmas.AddRange(unit.Pragmas);

        foreach ((string path, string key) in unit.RegistryKeys)
        {
            output.RegistryKeys[path] = key;
        }

        // Concrete declarations pass through, with their bodies rewritten so
        // any generic reference inside them names a specialisation.
        Metadata.DeclarationBatch required = new();
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count == 0))
        {
            try { output.Types.Add(RewriteDecl(t, new Dictionary<string, TypeRef>(StringComparer.Ordinal), t.Name)); }
            catch (Metadata.DeclarationDemand demand) { required.Add(demand); }
        }
        foreach (string key in _templateBatch.Keys) required.Add(new Metadata.DeclarationDemand(key));
        required.ThrowIfAny();

        // AND THE TEMPLATES SURVIVE, unrewritten and uncompiled.
        //
        // Not to be emitted -- the binder gives them no layout, no type bit and
        // no vtable slots, and the code generator skips them -- but to be
        // NAMED. A generic method declared over `List<T>` has a parameter that
        // is a list of something not yet decided, and the checker cannot look
        // at it at all unless `List` is a type it knows.
        //
        // Dropping them is why a method's own type argument had to be erased to
        // the machine word, and that erasure is what killed the element type at
        // the call.
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0))
        {
            output.Types.Add(t);
        }

        // Specialising can discover further instantiations — a Box<int> whose
        // body mentions Pair<int,int> — so this drains rather than iterating.
        while (_pending.Count > 0)
        {
            Job job = _pending.Dequeue();

            Dictionary<string, TypeRef> map = new(StringComparer.Ordinal);

            _structParams.Clear();
            _paramInfo.Clear();
            for (int i = 0; i < job.Template.TypeParams.Count && i < job.Args.Count; i++)
            {
                Settled(job.Args[i]);
                map[job.Template.TypeParams[i].Name] = job.Args[i];
                _paramInfo[job.Template.TypeParams[i].Name] = job.Template.TypeParams[i];
                if (job.Template.TypeParams[i].Struct) _structParams.Add(job.Template.TypeParams[i].Name);
            }

            // A WORD-SHAPED COPY TAKES NO BODIES. Canon names the one copy
            // whose instructions serve it, the checker skips its bodies and
            // the lowering emits none (Binder.CheckBodies), so a copied body
            // was a whole method's tree held for nothing -- and the walk over
            // it queued the instantiations that body would have needed, which
            // the canonical body already asks for over the machine word.
            // Across a compiler's worth of List, Dictionary and HashSet over
            // reference types that was most of the declarations in memory.
            _bodiesElsewhere = job.Canon != null;
            // THE SHARED COPY OF A CLASS reads what its type arguments are from
            // the object it runs for (ICanonSlot). Known by its name: it is
            // queued by Canonicalise, or by a use written over __canon.
            _canonParams = job.Template.Kind == TypeKind.Class && job.Canon is null
                && job.Name == CanonNameOf(TemplatePath(job.Template), job.Template.TypeParams.Count) ? CanonParams(job.Template) : null;
            _canonMade = _canonParams is null ? null : new List<TypeRef>();
            TypeDecl made;
            try
            {
                made = RewriteDecl(job.Template, map, job.Name);
            }
            finally
            {
                _bodiesElsewhere = false;
                _canonParams = null;
            }

            // WHERE ITS CODE LIVES, which is the whole of code sharing.
            //
            // The declaration is complete either way -- every field, every
            // signature -- because that is what checks the caller and lays
            // out the object. Canon says only that the INSTRUCTIONS are
            // somewhere else, and External says that somewhere else is
            // another image.
            made.External = job.External;
            made.Canon = job.Canon;
            made.Specialised = true;
            // Specializations have globally unique generated names. Keep the
            // original namespace/using scope for checking their bodies, but
            // do not prefix the generated key with the namespace a second time.
            made.Outer = null;

            // WHAT IT WAS MADE FROM, so the checker can find its way back:
            // `List$Node` is `List` applied to `Node`, and a generic method
            // declared over `List<T>` works out that T is Node by asking.
            made.Template = TemplatePath(job.Template);
            made.TemplateArgs.AddRange(job.Args);
            made.TemplateParams = job.Template.TypeParams;

            // WHAT ITS SHARED CODE MAKES, for this copy's arguments (TypeDecl.CanonMade):
            // the canonical copy's list, made before any copy sharing it.
            if (_canonMade is not null)
            {
                made.CanonMadeWritten = _canonMade;
                _canonMadeOf[job.Name] = _canonMade;
            }
            List<TypeRef>? written = _canonMade
                ?? (job.Canon is not null && job.Template.Kind == TypeKind.Class
                    ? _canonMadeOf.GetValueOrDefault(job.Canon) ?? _made.GetValueOrDefault(job.Canon)?.CanonMadeWritten
                    : null);
            if (written is { Count: > 0 })
            {
                made.CanonMade = new List<TypeRef>(written.Count);
                foreach (TypeRef w in written) made.CanonMade.Add(Sub(w, map));
            }
            _canonMade = null;

            if (job.External)
            {
                made.LibSlot = job.Template.LibSlot;
            }

            _made[job.Name] = made;
            output.Types.Add(made);
            Specialisations++;
            SpecialisedMembers += made.Members.Count;
        }
        // AND AGAIN AFTER THE SPECIALISATIONS. Rewriting the queue above names
        // templates too -- EqualityComparer`1 reached only from a specialised
        // body -- and anything recorded there is found after the earlier
        // check has already run. Left unraised it becomes a name the binder
        // reports as undeclared instead of one more round that loads it.
        _templateBatch.ThrowIfAny();
        output.TupleNamings.AddRange(_tupleNamings);
        return output;
    }

    /// <summary>
    /// Whether a template can have the shared word-shaped copy at all: not
    /// when a parameter is `where T : struct`, which no machine word can be
    /// -- that copy made its `T?` an `object?` with no HasValue, and a unit
    /// owning `Box<T> where T : struct` could not compile.
    ///
    /// NOR WHEN ONE IS `new()` (or `unmanaged`), or constructed by
    /// Activator.CreateInstance (TypeParam.Made): `new T()` in a copy of its
    /// own is `new` of what T is, a constructor called directly and known
    /// to every analysis, where a shared copy would have to find one at run
    /// time through a word every type's descriptor carried -- every public
    /// parameterless constructor in the program kept for it. A generic
    /// METHOD is copied per argument already; shared code cannot hand a
    /// `new()` parameter a word, its own parameter being `new()` too.
    /// </summary>
    private static bool Shareable(TypeDecl template)
        => !template.TypeParams.Any(p => p.Struct || p.New || p.Unmanaged || p.Made) && !HasStaticState(template);

    /// <summary>
    /// A GENERIC TYPE'S STATICS ARE EACH INSTANTIATION'S OWN in C#: EmptyArray
    /// of string and of Exception hold two arrays, each of its own element
    /// type. One copy shared by every reference argument held one static for
    /// all of them, made with the shared element type: Array.Empty of string
    /// was no string[], and the compiler built natively, asking that of an
    /// empty target list, took a call that reaches nothing for an escape the
    /// managed build did not see. A type with static storage -- a static
    /// field, a static auto property, a static constructor -- is copied per
    /// argument. A const is no storage.
    /// </summary>
    private static bool HasStaticState(TypeDecl template)
        => template.Members.Any(m => m switch
        {
            FieldDecl f => f.Mods.HasFlag(Mods.Static) && !f.Mods.HasFlag(Mods.Const),
            PropertyDecl p => p.Mods.HasFlag(Mods.Static) && p.Auto,
            MethodDecl c => c.IsCtor && c.Mods.HasFlag(Mods.Static),
            _ => false,
        });

    /// <summary>
    /// Queues the canonical copy of a template and answers what it is called.
    ///
    /// Every type parameter becomes <c>__canon</c>, which is a machine word, so
    /// the result is the one compiled copy that serves every word-shaped set of
    /// arguments this template will ever be given.
    /// </summary>
    private string Canonicalise(TypeDecl template)
    {
        List<TypeRef> canonArgs = template.TypeParams
            .Select(p => new TypeRef { Name = CanonName, Line = template.Line, Col = template.Col })
            .ToList();

        string name = MangledName(TemplatePath(template), canonArgs);

        if (_claimed.Add(name))
        {
            // A CONSTRAINED PARAMETER STANDS ON ITS CONSTRAINT IN THIS COPY.
            //
            // `class Bag<T> where T : IShape` calling `x.Area()` has to compile
            // HERE as well as in the copies made per type argument, and with T
            // a bare machine word there was nothing to call: the checker said
            // 'object' has no member 'Area', and a generic class with an
            // interface constraint could not be written at all. Standing T on
            // IShape makes it an interface call on a word, which is right for
            // every type argument the constraint admits.
            //
            // The NAME is still the all-__canon one, because that is what a
            // consumer asks for and what this copy exists to answer.
            //
            // Only a constraint naming a plain reference type: a value type is
            // not word-shaped and never shares this copy, and a constraint with
            // type arguments of its own would need those substituted too.
            // EACH __canon KNOWS WHICH PARAMETER IT IS (TypeRef.CanonIndex),
            // so that a generic method called with it is handed the argument
            // the object's type context holds (Monomorphiser.CopyName).
            List<TypeRef> bodyArgs = template.TypeParams
                .Select((p, i) => Constraining(p)
                          ?? new TypeRef { Name = CanonName, CanonIndex = i, Line = template.Line, Col = template.Col })
                .ToList();

            // External when it came from a library: the code is in that
            // library's image and this compilation only needs to be able to
            // name it. Ours when we are the library building it.
            _pending.Enqueue(new Job(template, bodyArgs, name, template.External, null));
        }
        return name;
    }

    /// <summary>The constraint a shared copy may stand a type parameter on, or null.</summary>
    private TypeRef? Constraining(TypeParam p)
    {
        foreach (TypeRef c in p.Constraints)
        {
            if (c.Args.Count == 0 && c.ArrayRank == 0 && c.PointerDepth == 0
                && _byRef.Contains(c.Name))
            {
                return new TypeRef { Name = c.Name, Line = c.Line, Col = c.Col };
            }
        }
        return null;
    }

    /// <summary>The type parameters of the template being copied, by name: what each is constrained to (`new()`).</summary>
    private readonly Dictionary<string, TypeParam> _paramInfo = new(StringComparer.Ordinal);

    /// <summary>The same, for the type parameters of the generic method being copied (_methodParams).</summary>
    private readonly Dictionary<string, TypeParam> _methodParamInfo = new(StringComparer.Ordinal);

    /// <summary>Where `new T()` has been refused already: a template is copied once per argument, and said once.</summary>
    private readonly HashSet<(int Line, int Col)> _refusedNew = new();

    /// <summary>
    /// `new T()`, T A TYPE PARAMETER of the template or the method being
    /// copied (C# 12.8.17.2). Refused without `new()`, `struct` or
    /// `unmanaged` on T (CS0304), and with arguments (CS0417). Over a
    /// type argument the copy knows, it is `new` of that type, as written,
    /// with the parameter put in, which is the substitution every `new`
    /// gets. Over one only run time knows -- a shared method copy's
    /// (TypeRef.CanonIndex), or the machine word itself -- it is refused:
    /// the copies are arranged so that never happens. The `new` is
    /// rewritten as any other.
    /// </summary>
    private void ParameterMade(NewExpr nw, Dictionary<string, TypeRef> map)
    {
        TypeRef written = nw.Type;
        if (nw.ArraySize is not null || nw.Elements is not null || nw.Utf8Bytes is not null || nw.Collection
            || written.Name.Length == 0 || written.Args.Count != 0 || written.ArrayRank != 0 || written.PointerDepth != 0)
        {
            return;
        }
        bool classParam = map.ContainsKey(written.Name) && _paramInfo.ContainsKey(written.Name);
        TypeParam? param = classParam ? _paramInfo[written.Name]
                         : _methodParams.Contains(written.Name) ? _methodParamInfo.GetValueOrDefault(written.Name) : null;
        if (param is null)
        {
            return;
        }
        if (!param.Constructible)
        {
            if (_refusedNew.Add((nw.Line, nw.Col)))
            {
                _errors.Add(new CompileError(_file, nw.Line, nw.Col,
                    $"CS0304: Cannot create an instance of the variable type '{written.Name}' because it does not have the new() constraint"));
            }
            return;
        }
        if (nw.Args.Count != 0)
        {
            if (_refusedNew.Add((nw.Line, nw.Col)))
            {
                _errors.Add(new CompileError(_file, nw.Line, nw.Col,
                    $"CS0417: '{written.Name}': cannot provide arguments when creating an instance of a variable type"));
            }
            return;
        }
        // A method's own parameter in its template, not yet bound: its copy decides.
        if (!map.TryGetValue(written.Name, out TypeRef? bound) || !nw.Body.IsEmpty)
        {
            return;
        }
        if (bound.Name != CanonName && bound.CanonIndex == -1)
        {
            return;
        }
        // NEVER A WORD: a class constructing its parameter is copied per
        // argument (Shareable), and a method's copy is made per argument
        // unless shared code hands it one only run time knows -- which a
        // parameter that is not `new()` itself cannot be handed (CS0310).
        if (_refusedNew.Add((nw.Line, nw.Col)))
        {
            _errors.Add(new CompileError(_file, nw.Line, nw.Col,
                $"'new {written.Name}()' over a type argument only run time knows: give the parameter it comes from the new() constraint"));
        }
        return;
    }

    /// <summary>The type parameters of the canonical class copy being made, by name: their places (ICanonSlot).</summary>
    private Dictionary<string, int>? _canonParams;

    /// <summary>Whether the member being copied has a `this` to read them through.</summary>
    private bool _canonSelf;

    /// <summary>Whether the member being copied has been marked to (MemberDecl.ReadsTypeArguments).</summary>
    private bool _canonMarked;

    /// <summary>The canonical copy being made: the generic classes its instance code makes over its parameters (TypeDecl.CanonMadeWritten).</summary>
    private List<TypeRef>? _canonMade;

    /// <summary>Each canonical copy's list, by its name, for the copies that share it.</summary>
    private readonly Dictionary<string, List<TypeRef>> _canonMadeOf = new(StringComparer.Ordinal);

    /// <summary>
    /// `new X&lt;T&gt;(...)` in the canonical copy's instance code, X a class
    /// that shares a canonical copy of its own and every argument one of this
    /// copy's parameters: marked to take its descriptor from the object's
    /// type context, after the parameters' own entries (ICanonSlot).
    /// </summary>
    private NewExpr CanonMadeObject(NewExpr made, NewExpr source)
    {
        if (source.CanonSlot >= 0)
        {
            made.CanonSlot = source.CanonSlot;
            made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
            _canonMarked = true;
            return made;
        }
        TypeRef written = source.Type;
        if (!_canonSelf || _canonParams is null || _canonMade is null || written.Args.Count == 0
            || written.ArrayRank != 0 || written.PointerDepth != 0
            || !written.Args.All(a => a.Args.Count == 0 && a.ArrayRank == 0 && a.PointerDepth == 0 && _canonParams.ContainsKey(a.Name)))
        {
            return made;
        }
        string name = GenericPath(written.Name, written.Args.Count, source) ?? Path(written.Name);
        if (!_generic.TryGetValue(Arity(name, written.Args.Count), out TypeDecl? template)
            || template.Kind != TypeKind.Class || !Shareable(template) || template.TypeParams.Any(p => p.Struct))
        {
            return made;
        }
        string key = written.ToString();
        int at = _canonMade.FindIndex(t => t.ToString() == key);
        if (at < 0)
        {
            at = _canonMade.Count;
            _canonMade.Add(written);
        }
        made.CanonSlot = 2 * _canonParams.Count + at;
        made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
        _canonMarked = true;
        return made;
    }

    /// <summary>
    /// A CONSTRUCTED TYPE OVER THE PARAMETERS, TESTED OR CAST TO in the
    /// canonical class copy's instance code -- `source is ICollection<T>` in
    /// List's copy constructor, `as IReadOnlyList<T>`, `(IList<T>)x`. Over the
    /// machine word it is the __canon instantiation, which no object of a
    /// sharing instantiation lists: a List of KernelModule implements
    /// ICollection of KernelModule. So it is marked, as `new X<T>()` is
    /// (CanonMadeObject), to read the instantiation's own descriptor from
    /// the object's type context, after the parameters' entries: the same
    /// list, an interface's entry its interface descriptor. Every argument
    /// one of this copy's parameters; a class or an interface.
    /// </summary>
    private T CanonTested<T>(T made, T source, TypeRef written) where T : Node, ICanonSlot
    {
        if (source.CanonSlot >= 0)
        {
            made.CanonSlot = source.CanonSlot;
            made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
            _canonMarked = true;
            return made;
        }
        // An array of one too (`is List<T>[]`): its entry is the array's
        // descriptor, which the Binder resolves by its element.
        if (!_canonSelf || _canonParams is null || _canonMade is null || written.ArrayRank > 1 || written.PointerDepth != 0
            || !written.Args.All(a => a.Args.Count == 0 && a.ArrayRank == 0 && a.PointerDepth == 0 && _canonParams.ContainsKey(a.Name)))
        {
            return made;
        }
        string name = GenericPath(written.Name, written.Args.Count, source) ?? Path(written.Name);
        if (!_generic.TryGetValue(Arity(name, written.Args.Count), out TypeDecl? template)
            || template.Kind is not (TypeKind.Class or TypeKind.Interface))
        {
            return made;
        }
        string key = written.ToString();
        int at = _canonMade.FindIndex(t => t.ToString() == key);
        if (at < 0)
        {
            at = _canonMade.Count;
            _canonMade.Add(written);
        }
        made.CanonSlot = 2 * _canonParams.Count + at;
        made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
        _canonMarked = true;
        return made;
    }

    /// <summary>A shared method copy's type parameters that only run time knows, by name: their places (Specialise, Shaped).</summary>
    private Dictionary<string, int>? _shapeParams;

    /// <summary>
    /// A TEST OR A CAST TO A GENERIC INTERFACE OVER A SHARED METHOD COPY'S
    /// OWN TYPE PARAMETERS -- `o is IList<U> l` in a copy whose U only run
    /// time knows (CopyName). Over the machine word the copy names IList of
    /// object, which a list of strings does not implement; so the arguments
    /// are kept as written (ICanonShape), U's as the hidden argument it is,
    /// and the binder and the lowering ask the object for the interface of
    /// that family over those arguments (Runtime.ShapedAs).
    ///
    /// Every argument U itself or no mention of any such parameter: an
    /// argument made over U (`IList<List<U>>`) has no descriptor any call
    /// hands in, and stays the copy's own answer. Whether the type is an
    /// interface is the binder's to say, which knows what it names.
    ///
    /// ONLY INTERFACES. A class test, typeof(U), `new List<U>()` and an
    /// array of U keep the copy over object's answer, as before. Option 1,
    /// left for later: a hidden type-context table for the copy like a
    /// shared class's (TypeContext), made by every caller, with the
    /// descriptors of each class the copy makes or tests over U -- which
    /// needs those classes' descriptors made at each caller's arguments,
    /// and so each caller's unit to instantiate them.
    /// </summary>
    private void Shaped<T>(T made, T source, TypeRef written, Dictionary<string, TypeRef> map) where T : Node, ICanonShape
    {
        if (source.ShapeArgs is { } kept)
        {
            made.ShapeArgs = SubAll(kept, map);
            return;
        }
        if (_shapeParams is null || written.Args.Count == 0 || written.Args.Count > CanonShape.MostArguments
            || written.ArrayRank != 0 || written.PointerDepth != 0)
        {
            return;
        }
        bool any = false;
        foreach (TypeRef a in written.Args)
        {
            if (a.Args.Count == 0 && a.ArrayRank == 0 && a.PointerDepth == 0 && _shapeParams.ContainsKey(a.Name))
            {
                any = true;
            }
            else if (MentionsShaped(a))
            {
                return;
            }
        }
        if (any)
        {
            made.ShapeArgs = SubAll(written.Args, map);
        }
    }

    private bool MentionsShaped(TypeRef a)
        => _shapeParams is not null && (_shapeParams.ContainsKey(a.Name) || a.Args.Any(MentionsShaped));

    private static Dictionary<string, int> CanonParams(TypeDecl template)
    {
        Dictionary<string, int> places = new(StringComparer.Ordinal);
        for (int i = 0; i < template.TypeParams.Count; i++) places[template.TypeParams[i].Name] = i;
        return places;
    }

    /// <summary>
    /// A copied expression of the canonical class copy that asks for a type
    /// parameter at run time, marked with the entry that answers it
    /// (ICanonSlot): the parameter itself, or -- `array`, or `arrayToo` and
    /// written `T[]` -- an array of it. Anything else asked of T stays the
    /// machine word's answer.
    /// </summary>
    private T Canon<T>(T made, T source, TypeRef written, bool arrayToo, bool array = false) where T : Expr, ICanonSlot
    {
        // A COPY ALREADY MARKED keeps its mark when it is copied again: a
        // made declaration passes through the next round with its bodies
        // rewritten.
        if (source.CanonSlot >= 0)
        {
            made.CanonSlot = source.CanonSlot;
            made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
            _canonMarked = true;
            return made;
        }
        // A test or a cast to a constructed type over the parameters reads
        // that type's own descriptor; a typeof or an array of one stays the
        // machine word's, as it was.
        if (written.Args.Count != 0) return made is IsExpr or AsExpr or CastExpr or TypeOfExpr && !array ? CanonTested(made, source, written) : made;
        if (!_canonSelf || _canonParams is null || written.PointerDepth != 0
            || !_canonParams.TryGetValue(written.Name, out int place))
        {
            return made;
        }
        if (written.ArrayRank == 0)
        {
            made.CanonSlot = 2 * place + (array ? 1 : 0);
        }
        else if (written.ArrayRank == 1 && arrayToo && !array)
        {
            made.CanonSlot = 2 * place + 1;
        }
        else
        {
            return made;
        }
        made.CanonSelf = new ThisExpr { Line = made.Line, Col = made.Col };
        _canonMarked = true;
        return made;
    }

    /// <summary>The name a specialisation gets. Readable on purpose: it appears in diagnostics.</summary>
    internal static string MangledName(string baseName, List<TypeRef> args)
        => baseName.Replace(".", "$") + "$" + string.Join("$", args.Select(a => a.ToString()
            .Replace("<", "_").Replace(">", "").Replace(", ", "_")
            // A NESTED ARGUMENT KEEPS ITS OUTER, spelled with the separator
            // this name already uses: the dot is how a nested type is KEYED,
            // and a specialisation is not one.
            .Replace(".", "$")));


    /// <summary>How a generic template is keyed: its name and how many type parameters it takes.</summary>
    private static string Arity(string name, int count) => name + "`" + count;

    private static string TemplatePath(TypeDecl type)
        => type.Outer is null ? type.Name : type.Outer + "." + type.Name;

    /// <summary>Type arguments already spelt where they were written, and their copies (Qualify).</summary>
    private readonly HashSet<TypeRef> _settled = new(ReferenceEqualityComparer.Instance);

    /// <summary>A type argument, and everything inside it, as spelt where it was written.</summary>
    private void Settled(TypeRef r)
    {
        if (!_settled.Add(r))
        {
            return;
        }
        foreach (TypeRef a in r.Args)
        {
            Settled(a);
        }
        if (r.UseArgs is not null)
        {
            foreach (TypeRef a in r.UseArgs)
            {
                Settled(a);
            }
        }
    }

    /// <summary>Paths of library templates moved into System: old path to new.</summary>
    private readonly Dictionary<string, string> _movedToSystem = new(StringComparer.Ordinal);

    /// <summary>Whether the declaration being rewritten is the library's.</summary>
    private bool _libraryCode;

    private void MoveToSystem(TypeDecl library)
    {
        string before = TemplatePath(library);
        library.MovedToSystem = true;
        library.Outer = library.Outer is null ? "System" : "System." + library.Outer;
        if (library.Namespace.Length == 0) library.Namespace = "System";
        _movedToSystem[before] = TemplatePath(library);
        if (library.TypeParams.Count > 0) _generic[Arity(TemplatePath(library), library.TypeParams.Count)] = library;
    }

    private string? MovedPath(string path)
    {
        foreach ((string before, string after) in _movedToSystem)
        {
            if (path == before) return after;
            if (path.StartsWith(before + ".", StringComparison.Ordinal)) return after + path.Substring(before.Length);
        }
        return null;
    }

    /// <summary>A namespace's enclosing one ("" at the top), remembered: every lookup walks outwards.</summary>
    private string Parent(string scope)
    {
        if (_parents.TryGetValue(scope, out string? known)) return known;
        int dot = scope.LastIndexOf('.');
        string parent = dot < 0 ? "" : scope[..dot];
        _parents[scope] = parent;
        return parent;
    }

    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private readonly HashSet<(string Scope, string Name, int Arity)> _absent = new();
    private int _absentAt = -1;

    private string? GenericPath(string name, int arity, Node location)
    {
        bool Candidate(string candidate)
        {
            string key = Arity(candidate, arity);
            if (_generic.ContainsKey(key)) return true;
            // Recorded rather than raised, for the reason Binder.TypeCandidate
            // gives: one template's missing name must not abandon the rewrite
            // of everything else and cost a whole extra round.
            // Once a name, and a copy, as Binder.TypeCandidate hands it: the key
            // is built to be looked up and dropped.
            if (_requireDeclaration is null || _demanded.Contains(key)) return false;
            string kept = key.Substring(0);
            _demanded.Add(kept);
            try { _requireDeclaration(kept); }
            catch (Metadata.DeclarationDemand demand) { _templateBatch.Add(demand); }
            return false;
        }
        // A NAME ASKED IN A NAMESPACE THAT HAS NO SUCH TEMPLATE is asked again
        // from every use, and was spelt `scope.name` and its arity key each time
        // to be told no. Remembered by its parts while the templates known stay
        // the same (a demanded declaration arriving adds one and clears it).
        bool Joined(string scope, out string candidate)
        {
            if (_generic.Count != _absentAt) { _absent.Clear(); _absentAt = _generic.Count; }
            if (_absent.Contains((scope, name, arity))) { candidate = ""; return false; }
            candidate = scope + "." + name;
            if (Candidate(candidate)) return true;
            if (_generic.Count == _absentAt) _absent.Add((scope, name, arity));
            return false;
        }
        string? Imports(string scope)
        {
            if (_usings is null) return null;
            foreach (var alias in _usings.Aliases)
                if (alias.In == scope && alias.Alias == name && Candidate(alias.Target)) return alias.Target;
            string? found = null;
            foreach (var import in _usings.Imports)
            {
                if (import.In != scope) continue;
                if (!Joined(import.Namespace, out string candidate)) continue;
                if (found is not null && found != candidate)
                    throw new CompileError(_file, location.Line, location.Col, "ambiguous generic type '" + name + "': " + found + " or " + candidate);
                found = candidate;
            }
            return found;
        }
        // THE LIBRARY'S GLOBAL CODE IS SYSTEM'S, once a template of its has
        // been moved there for a program's of the same name.
        if (_libraryCode && _inNamespace.Length == 0 && _movedToSystem.ContainsKey(name) && Candidate(_movedToSystem[name]))
        {
            return _movedToSystem[name];
        }
        foreach (string from in new[] { _scope, _inNamespace })
            for (string scope = from; scope.Length > 0; )
            {
                if (Joined(scope, out string candidate)) return candidate;
                if (Imports(scope) is { } imported) return imported;
                scope = Parent(scope);
            }
        if (Candidate(name)) return name;
        if (Imports("") is { } global) return global;

        // OTHERWISE A QUALIFIED NAME NAMES ITS LAST PART, as Binder.Resolve
        // reads one: `System.Collections.Generic.Dictionary<K,V>` is the
        // Dictionary the library declares at the top level, because
        // namespaces are not a tree here and the qualifier has nothing to
        // select between.
        int cut = name.LastIndexOf('.');
        // Qualified by a System namespace: the library's, where a program has
        // taken the simple name (MoveToSystem).
        if (cut >= 0 && name.StartsWith("System.", StringComparison.Ordinal)
            && _generic.ContainsKey(Arity("System." + name[(cut + 1)..], arity)))
        {
            return "System." + name[(cut + 1)..];
        }
        return cut < 0 ? null : GenericPath(name[(cut + 1)..], arity, location);
    }

    /// <summary>
    /// What the canonical copy of a template is called.
    ///
    /// Computed the same way on both sides of a library boundary -- the library
    /// naming what it compiled, and a consumer naming what it wants to call --
    /// so it lives here rather than being spelled out twice.
    /// </summary>
    public static string CanonNameOf(string name, int arity)
        => MangledName(name, Enumerable.Range(0, arity)
                                       .Select(_ => new TypeRef { Name = CanonName })
                                       .ToList());

    /// <summary>
    /// A type argument as the specialisation is named and made: a reference's
    /// `?` dropped, here and in a tuple's elements, a nullable VALUE type's
    /// kept (Nullable&lt;T&gt; is a type of its own).
    /// </summary>
    private TypeRef WithoutReferenceMarks(TypeRef a)
    {
        bool reference = a.Nullable && (a.ArrayRank > 0 || a.PointerDepth == 0 && IsWord(a));
        // `Expr?[]` is `Expr[]`: a reference element's `?` is a mark, not a
        // type. `int?[]` keeps its, the elements being Nullable<int>.
        bool referenceElements = a.ElementNullable && a.ArrayRank > 0 && a.PointerDepth == 0
            && IsWord(new TypeRef { Name = a.Name, Arguments = a.Args, UseArgs = a.UseArgs });
        bool tuple = a.Name == TypeRef.Tuple && a.Args.Count > 0;
        if (!reference && !referenceElements && !tuple) return a;
        List<TypeRef> args = a.Args;
        if (tuple)
        {
            args = new List<TypeRef>(a.Args.Count);
            foreach (TypeRef item in a.Args) args.Add(WithoutReferenceMarks(item));
        }
        return new TypeRef
        {
            Name = a.Name, Arguments = args, UseArgs = a.UseArgs, ArrayRank = a.ArrayRank,
            Nullable = a.Nullable && !reference, ElementNullable = a.ElementNullable && !referenceElements,
            InnerNullable = a.InnerNullable, PointerDepth = a.PointerDepth, TupleNames = a.TupleNames,
            Line = a.Line, Col = a.Col,
        };
    }

    /// <summary>Records that a specialisation is needed and returns its name.</summary>
    private string Instantiate(string name, List<TypeRef> args, Node at)
    {
        // A TYPE ARGUMENT IS SPLICED INTO A COPY OF THE TEMPLATE, and the copy
        // is read in the TEMPLATE's scope rather than in the scope the use was
        // written in. So the argument is qualified here, while the use site is
        // still known: `List<Section>` inside ImageFile becomes a list of
        // ImageFile.Section, which names the same type from anywhere -- and, as
        // importantly, mangles to a different specialisation from a list of
        // Assembler.Section.
        args = args.Select(Qualify).ToList();
        // ONE SPECIALISATION PER TYPE, AS C# HAS IT: a reference argument's `?`
        // is an annotation, not part of the type. List<(int, Box?)> and
        // List<(int, Box)> are one class at run time and one here; the marks
        // stay with the use (UseArgs), where the checker reads them
        // (Binder.ContextualResult). Spelled with the marks, they were two
        // specialisations the checker could not convert between.
        args = args.Select(WithoutReferenceMarks).ToList();

        string writtenName = name;
        name = GenericPath(name, args.Count, at) ?? Path(name);

        if (!_generic.TryGetValue(Arity(name, args.Count), out TypeDecl? template))
        {
            // NAMED, BUT NOT AT THIS ARITY. Worth telling apart from a name
            // nobody declared: `Func<long>` on a machine that has Func<A,R> and
            // Func<A,B,R> is a real mistake with a specific answer, and
            // "no such type" would send the author looking for a missing file.
            if (_generic.Keys.Any(k => k[..k.LastIndexOf('`')] == name))
            {
                string had = string.Join(" or ",
                    _generic.Keys.Where(k => k[..k.LastIndexOf('`')] == name)
                                 .Select(k => k[(k.LastIndexOf('`') + 1)..])
                                 .Distinct());

                _errors.Add(new CompileError(_file, at.Line, at.Col,
                    $"'{name}' takes {had} type argument(s), not {args.Count}"));
            }
            return writtenName;
        }

        string mangled = MangledName(name, args);

        // CLAIMED AT THE MOMENT IT IS QUEUED, not when it is finished.
        //
        // A job is taken off the queue BEFORE its body is rewritten, and
        // rewriting that body can name the very specialisation being made --
        // `List<__canon>` mentioning itself, which is what a generic method
        // over `List<T>` produces. Checking the queue and the finished set
        // leaves a window where the name is in neither, and the specialisation
        // is made twice: "'List$__canon' is declared more than once".
        if (!_claimed.Add(mangled))
        {
            return mangled;
        }
        _claimedByRef[mangled] = template.Kind is not (TypeKind.Struct or TypeKind.Enum);

        // DOES THIS NEED CODE OF ITS OWN, or is it the same instructions as a
        // copy that already exists?
        //
        // Every word-shaped argument produces identical code, so the first such
        // instantiation causes the canonical copy to be named and every one
        // after it links to the same place. `List<long>`, `List<string>` and
        // `List<Node>` are three types and one routine.
        //
        // The canonical copy is asked for even when the template is OURS: a
        // library that instantiates its own generic twice should not carry it
        // twice either, and the same redirection does both.
        bool word = args.All(IsWord);
        string? canon = null;

        if (word && template.TypeParams.Count > 0 && Shareable(template))
        {
            canon = Canonicalise(template);

            if (canon == mangled)
            {
                canon = null;               // this IS the canonical copy
            }
        }

        // External only when the code is in another image. A specialisation of
        // our own template is compiled here even when it shares -- it shares
        // with a copy that is also here.
        _pending.Enqueue(new Job(template, args, mangled, canon != null && template.External, canon));
        return mangled;
    }

    // ---- rewriting ---------------------------------------------------------

    /// <summary>
    /// The type parameters of the generic method being rewritten, if any.
    /// </summary>
    private readonly HashSet<string> _methodParams = new(StringComparer.Ordinal);

    /// <summary>
    /// The type parameters of the template being copied that are `where T :
    /// struct`: over them `T?` is Nullable of what T is bound to, not T
    /// (see Sub).
    /// </summary>
    private readonly HashSet<string> _structParams = new(StringComparer.Ordinal);

    /// <summary>Specialisations queued or finished, by name.</summary>
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    /// <summary>
    /// A method's type parameter, standing where a type argument goes, as the
    /// canonical machine word it will be at run time.
    /// </summary>
    private TypeRef Canonical(TypeRef r)
        => r.Args.Count == 0 && r.ArrayRank == 0 && r.PointerDepth == 0 && _methodParams.Contains(r.Name)
         ? new TypeRef { Name = CanonName, Nullable = r.Nullable, Line = r.Line, Col = r.Col }
         : r;

    /// <summary>Whether a type mentions a type parameter of the method being copied.</summary>
    private bool MentionsMethodParameter(TypeRef a)
        => (a.Args.Count == 0 && _methodParams.Contains(a.Name)) || a.Args.Any(MentionsMethodParameter);

    /// <summary>
    /// Each of a list of types substituted, in a loop: `Select(a => Sub(a,
    /// map))` made a closure over the map for every type with arguments the
    /// copies were made of, a million a self-hosted unit, all the collector's.
    /// </summary>
    private List<TypeRef> SubAll(List<TypeRef> types, Dictionary<string, TypeRef> map)
    {
        List<TypeRef> made = new(types.Count);
        SubInto(made, types, map);
        return made;
    }

    private void SubInto(List<TypeRef> into, List<TypeRef> types, Dictionary<string, TypeRef> map)
    {
        foreach (TypeRef t in types) into.Add(Sub(t, map));
    }

    private TypeRef Sub(TypeRef r, Dictionary<string, TypeRef> map)
    {
        // A bare type parameter becomes whatever it was bound to, keeping any
        // array rank or nullability written at the USE site.
        // THE STARS SURVIVE THE SUBSTITUTION, on both paths. Dropping them
        // turns a `T*` inside a template into a plain T once specialised, and
        // the checker then reports that a value cannot be dereferenced -- in a
        // library file the author never edited. Same family as the object
        // initialiser and the indexer parameters below.
        if (r.Args.Count == 0 && map.TryGetValue(r.Name, out TypeRef? bound))
        {
            // WHERE THE '?' LANDS depends on which side the array came from.
            //
            // `T[]` with T bound to `string?` is an array of nullable strings,
            // not a nullable array of strings. Both were spelled by OR-ing the
            // two flags together, which put the '?' on the outermost thing --
            // so List<string?> declared its backing store as `string[]?` and
            // every use of it inside std.cor became a possible null
            // dereference, in a library file the author never touched.
            //
            // The rule: an array written at the USE site takes the bound type's
            // nullability on its ELEMENT, because that is what was substituted
            // into the element position. A '?' written at the use site is the
            // array's own.
            bool arrayFromUse = r.ArrayRank > 0;

            // `T?` ON AN UNCONSTRAINED T IS AN ANNOTATION, NOT A CELL.
            //
            // C# 9 settled this: without `where T : struct` the '?' on a type
            // parameter says only that the value may be absent, and for a
            // value type T the type is still T. Or-ing the flag together made
            // `T?` into Nullable<int> once T was int, so std.cor's
            // FirstOrDefault -- declared `T?`, as .NET declares it -- handed
            // back the address of a cell where the caller read an int, and a
            // List<int> answered with a pointer.
            //
            // A '?' written at the USE site -- `Pick<string?>` -- is the
            // bound type's own and is kept.
            //
            // `where T : struct` IS THE EXCEPTION C# MAKES: over it `T?` is
            // Nullable<T>, a real cell with a HasValue, and stays one in the
            // copy. Without this `Equal<T>(T? a, T? b) where T : struct` over
            // Path took two Paths and refused the Path? it was written for.
            // A STRUCT SPECIALISATION IS A VALUE TOO: `KeyValuePair<string,
            // Source>` arrives as the copy `KeyValuePair$string$Source` or as
            // the template's name with its arguments, and MinBy's `T?` over it
            // is still the pair, not a cell holding one.
            // AND A TUPLE: `(string Callee, int Argument)` arrives as
            // ValueTuple with its element types, a struct no unit declares,
            // and FirstOrDefault's `T?` over it made the copy hand back a
            // Nullable cell where every caller read the tuple -- the
            // has-value flag read as Callee.
            bool valueBound = bound.ArrayRank == 0 && bound.PointerDepth == 0
                && (Narrow.Contains(bound.Name) || _byValue.Contains(bound.Name)
                    || bound.Name == TypeRef.Tuple && bound.Args.Count > 0
                    || bound.Name is "long" or "ulong" or "nint" or "nuint" or "decimal"
                    || _made.TryGetValue(bound.Name, out TypeDecl? madeDecl) && madeDecl.Kind is TypeKind.Struct or TypeKind.Enum
                    || bound.Args.Count > 0 && _generic.TryGetValue(Arity(bound.Name, bound.Args.Count), out TypeDecl? template)
                       && template.Kind == TypeKind.Struct);

            TypeRef substituted = new TypeRef
            {
                Name = bound.Name,
                ArrayRank = r.ArrayRank + bound.ArrayRank,
                Nullable = arrayFromUse ? r.Nullable
                         : ((r.Nullable && (!valueBound || _structParams.Contains(r.Name))) || bound.Nullable),
                // An ARRAY bound's own `?` marks the inner array (InnerNullable
                // below), not the element: T = long[]? in T[] is long[]?[],
                // whose longs are not long?.
                ElementNullable = arrayFromUse
                                ? bound.Nullable && bound.ArrayRank == 0 || bound.ElementNullable
                                  // `T?[]` over a struct-constrained T is an
                                  // array of Nullable<T>, as `T?` is one.
                                  || r.ElementNullable && bound.ArrayRank == 0 && _structParams.Contains(r.Name)
                                : r.ElementNullable || bound.ElementNullable,
                // The bound's marks sit inside; the use's marks sit above
                // them, shifted by the bound's rank, and the bound's own
                // '?' becomes a mark where its brackets end.
                InnerNullable = bound.InnerNullable | (r.InnerNullable << bound.ArrayRank)
                    | (bound.ArrayRank > 0 && r.ArrayRank > 0 && bound.Nullable ? 1 << (bound.ArrayRank - 1) : 0),
                PointerDepth = r.PointerDepth + bound.PointerDepth,
                Arguments = bound.Args.ToList(),
                UseArgs = bound.UseArgs,

                // AND THE ELEMENT NAMES, when what T was bound to is a tuple.
                // `List<(int At, string Label)>` substitutes the whole tuple in
                // here, and a copy that kept its shape but lost its names left
                // `l[0].Label` reporting that the element does not exist.
                TupleNames = bound.TupleNames is null ? null : new List<string>(bound.TupleNames),
                // Which shared type argument it stands for, if any (CanonIndex).
                CanonIndex = bound.CanonIndex,
                Line = r.Line, Col = r.Col,
            };
            _settled.Add(substituted);
            return substituted;
        }

        // A TUPLE TYPE IS NEVER INSTANTIATED. There is no template called
        // ValueTuple to make a copy of -- the checker writes the class once it
        // knows the element types -- so its arguments are substituted and the
        // shape is left exactly as it was, names and all.
        if (r.Name == TypeRef.Tuple)
        {
            TypeRef tuple = new()
            {
                Name = r.Name, ArrayRank = r.ArrayRank, Nullable = r.Nullable,
                ElementNullable = r.ElementNullable, InnerNullable = r.InnerNullable,
                PointerDepth = r.PointerDepth,
                TupleNames = r.TupleNames is null ? null : new List<string>(r.TupleNames),
                Line = r.Line, Col = r.Col,
            };

            SubInto(tuple.Arguments, r.Args, map);
            return tuple;
        }

        // A METHOD'S OWN TYPE PARAMETER, USED AS A TYPE ARGUMENT, IS THE
        // CANONICAL WORD.
        //
        // `List<T>` inside `Join<T>` is not a request to specialise List for a
        // type called T -- there is no such type. It is a list of whatever T
        // turns out to be, and every T a generic method can be given here is a
        // machine word, so the one canonical copy serves all of them. That is
        // what the CLR does for reference types too.
        //
        // Without this the monomorphiser cloned List for a type argument named
        // "T" and produced a List$T whose every member mentioned a T nothing
        // declared -- five errors inside the standard library, pointing at
        // lines the author of the generic method never saw.
        //
        // Only as an ARGUMENT. A bare T stays a T, so the checker can still
        // infer it at the call site and give `Pick(a, b)` the type of a rather
        // than the type of anything.
        // A TYPE WITH NO ARGUMENTS -- most of them: int, string, Node -- is
        // copied as it is, without the argument walk below, which for it
        // made an empty list and asked it every question.
        if (r.Args.Count == 0)
        {
            if (r.ArrayRank == 1) ArrayIsASequence(r, new List<TypeRef>());
            return new TypeRef
            {
                Name = r.Name, ArrayRank = r.ArrayRank, Nullable = r.Nullable,
                UseArgs = r.UseArgs is null ? null : SubAll(r.UseArgs, map),
                ElementNullable = r.ElementNullable,
                InnerNullable = r.InnerNullable,
                PointerDepth = r.PointerDepth,
                CanonIndex = r.CanonIndex,
                Line = r.Line, Col = r.Col,
            };
        }

        List<TypeRef> args = SubAll(r.Args, map);

        // A TYPE ARGUMENT THAT IS STILL A METHOD'S TYPE PARAMETER IS LEFT
        // ALONE.
        //
        // `List<T>` inside `Where<T>` is not a request to specialise List for a
        // type called T -- there is no such type. It is a list of whatever T
        // turns out to be, and only the checker can find that out, at the call.
        // So the reference stays generic and the TEMPLATE stays in the unit for
        // it to name.
        //
        // This used to erase T to the canonical machine word, which compiled
        // one copy that served everything and lost the element type on the way
        // out: `Where` over a List<Node> handed back a list of `object`.
        // OPENNESS PROPAGATES THROUGH A COMPOSITE ARGUMENT. `List<(T,U)>`
        // inside Zip<T,U> is just as open as List<T>; eagerly instantiating the
        // outer List manufactures a tuple whose T and U are not in scope and
        // then repeats that error for every discovered specialisation.
        bool HasMethodParameter(TypeRef a)
            => (a.Args.Count == 0 && _methodParams.Contains(a.Name))
            || a.Args.Any(HasMethodParameter);

        bool open = args.Any(HasMethodParameter);

        foreach (TypeRef a in args)
        {
            KeepTupleNames(a);
        }

        // AND THIS SHAPE'S OWN NAMES, against the arguments it ends up with.
        //
        // `List<(A First, B Second)>` inside Zip is substituted a piece at a
        // time: the tuple is rewritten first, and by the time the list's
        // argument list is walked the tuple has become a name with the brackets
        // swallowed. The names have to be written down HERE, where both halves
        // are still in hand -- otherwise a Zip of two ParamSymbols produced a
        // shape nobody had named, and `p.First` over it was told the tuple has
        // no such member.
        if (r.Name == TypeRef.Tuple && r.TupleNames is { Count: > 0 } spelt && !open)
        {
            TypeRef named = new()
            {
                Name = TypeRef.Tuple, TupleNames = new List<string>(spelt),
                Line = r.Line, Col = r.Col,
            };

            named.Arguments.AddRange(args);
            _tupleNamings.Add(named);
        }

        // AN ARRAY'S SEQUENCES OVER THE ARGUMENTS IT HAS NOW. The reference as
        // written names the template's own parameters: `KeyValuePair<K, V>[]`
        // in Dictionary<K, V>, copied for int and long, is an array of
        // KeyValuePair<int, long> -- asked with the written K and V, it made
        // IEnumerable<KeyValuePair<K, V>> for parameters no scope has.
        if (!open)
        {
            ArrayIsASequence(r, args);
        }

        string name = args.Count > 0 && !open ? Instantiate(r.Name, args, r) : r.Name;

        TypeRef made = new()
        {
            Name = name, ArrayRank = r.ArrayRank, Nullable = r.Nullable,
            // These annotations cross into the template's scope too, including
            // arguments already hidden inside a nested specialised type name.
            UseArgs = args.Count > 0 && name != r.Name ? args.Select(Qualify).ToList()
                : r.UseArgs is null ? null : SubAll(r.UseArgs, map),
            ElementNullable = r.ElementNullable,
            InnerNullable = r.InnerNullable,
            PointerDepth = r.PointerDepth,
            Line = r.Line, Col = r.Col,
        };

        // A specialised name carries its arguments in the name, so the argument
        // list must not survive or the binder will look for a generic again.
        // An OPEN one keeps them, because looking for a generic is exactly
        // what the checker has to do with it.
        if (args.Count > 0 && name == r.Name)
        {
            made.Arguments.AddRange(args);
        }
        return made;
    }

    /// <summary>
    /// `T[]` IS AN `IEnumerable<T>`, which .NET says of every array and which
    /// nothing in a program has to write down. The interface is instantiated
    /// here for the element, so that an array passed to an operator declared
    /// over a sequence has a sequence to be: `args.Any(a => a.StartsWith("@"))`
    /// names no interface anywhere and needs one to exist.
    ///
    /// Only the interface, which is a descriptor and no code. What answers its
    /// members is the view the checker writes over the array itself.
    /// </summary>
    private void ArrayIsASequence(TypeRef r, List<TypeRef> args)
    {
        if (r.ArrayRank != 1 || r.Name.Length == 0 || _methodParams.Contains(r.Name))
        {
            return;
        }

        TypeRef element = new()
        {
            Name = r.Name, Arguments = args, Nullable = r.ElementNullable,
            PointerDepth = r.PointerDepth, TupleNames = r.TupleNames,
            Line = r.Line, Col = r.Col,
        };

        bool Open(TypeRef a)
            => (a.Args.Count == 0 && _methodParams.Contains(a.Name)) || a.Args.Any(Open);

        if (Open(element))
        {
            return;
        }

        // ONCE FOR EACH ARRAY WHERE IT IS READ. byte[] and string[] are
        // mentioned in nearly every member, and each mention qualified its
        // element and resolved the three names again, to find three
        // specialisations already made: 2.7% of a native self-compile. What
        // the names mean depends only on where they are read -- the scope,
        // the namespace, the file's usings, library code or not.
        string where = string.Concat(_scope, "\n", _inNamespace, _libraryCode ? "\nL\n" : "\nP\n", element.ToString());
        FileScope? usings = _usings;
        HashSet<string> seen;
        if (usings is null) seen = _sequencesSeenUnscoped;
        else if (!_sequencesSeen.TryGetValue(usings, out seen!)) _sequencesSeen[usings] = seen = new HashSet<string>(StringComparer.Ordinal);
        if (!seen.Add(where))
        {
            return;
        }

        foreach (string sequence in new[] { "IEnumerable", "IReadOnlyCollection", "IReadOnlyList" })
        {
            if (_generic.ContainsKey(Arity(sequence, 1)))
            {
                Instantiate(sequence, new List<TypeRef> { element }, r);
            }
        }
    }

    /// <summary>The arrays whose sequences have been asked for, by where they were read (ArrayIsASequence).</summary>
    private readonly Dictionary<FileScope, HashSet<string>> _sequencesSeen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _sequencesSeenUnscoped = new(StringComparer.Ordinal);

    /// <summary>
    /// Remembers what a tuple argument called its elements, before the name it
    /// is spliced into swallows the brackets.
    /// </summary>
    private void KeepTupleNames(TypeRef r)
    {
        if (r.Name == TypeRef.Tuple && r.TupleNames is { Count: > 0 })
        {
            _tupleNamings.Add(r);
        }

        foreach (TypeRef a in r.Args)
        {
            KeepTupleNames(a);
        }
    }

    private readonly List<TypeRef> _tupleNamings = new();

    private TypeDecl RewriteDecl(TypeDecl d, Dictionary<string, TypeRef> map, string name)
    {
        // WHERE THE NAMES IN THIS DECLARATION ARE BEING READ FROM, which is
        // what tells `List<Section>` inside ImageFile from `List<Section>`
        // inside Assembler. Restored on the way out; a rewrite of a nested type
        // happens inside a rewrite of the type that holds it.
        string wasScope = _scope;
        string wasNamespace = _inNamespace;
        FileScope? wasUsings = _usings;
        bool wasLibrary = _libraryCode;

        _scope = d.Outer is null ? d.Name : d.Outer + "." + d.Name;
        _inNamespace = d.Namespace;
        _usings = d.Scope;
        _libraryCode = d.FromLibrary;

        try
        {
            return RewriteDeclIn(d, map, name);
        }
        finally
        {
            _scope = wasScope;
            _inNamespace = wasNamespace;
            _usings = wasUsings;
            _libraryCode = wasLibrary;
        }
    }

    private TypeDecl RewriteDeclIn(TypeDecl d, Dictionary<string, TypeRef> map, string name)
    {
        // EXTERNAL AND ITS SLOT SURVIVE THE COPY.
        //
        // Losing them turns a declaration that came from a library's header
        // into one this compilation is expected to have compiled itself -- so
        // every call to it is emitted as a local label, and the linker says
        // that label was never defined. `String.Concat` is the one that shows
        // it first, because everything calls it.
        //
        // It was invisible for as long as headers carried no generics: with
        // none, Expand returns the unit untouched and this copy never runs. The
        // moment a header carried a template, every consumer of that library
        // went down this path and lost the marking on EVERY type in it.
        //
        // A specialisation is a different matter and is handled by the caller:
        // it is compiled HERE, into whoever asked for it, so it is not external
        // even though its template was.
        TypeDecl made = new()
        {
            Kind = d.Kind, Name = name, Mods = d.Mods, Line = d.Line, Col = d.Col, File = d.File,
            SourcePath = d.SourcePath,
            LocalOnly = d.LocalOnly,
            IsDelegate = d.IsDelegate,
            InitialisersPlaced = d.InitialisersPlaced,
            FromLibrary = d.FromLibrary,
            MovedToSystem = d.MovedToSystem,
            External = d.External && d.TypeParams.Count == 0,

            // A SPECIALISATION IS COMPILED WHERE IT IS ASKED FOR, for the
            // reason the External line above gives: the library holds the
            // template, and the instantiation belongs to whoever wanted it.
            // Which also keeps the libraries in layers -- `List<FileStream>`
            // emitted beside List would make the collections library depend
            // on the file system's.
            Elsewhere = d.Elsewhere && d.TypeParams.Count == 0,
            SignatureOnly = d.SignatureOnly && d.TypeParams.Count == 0,
            LibSlot = d.LibSlot,

            // WHAT IT WAS MADE FROM SURVIVES BEING COPIED AGAIN.
            //
            // A specialisation passes through here on every round after the one
            // that made it, and losing its template made the copy a type with a
            // curious name and no history -- so `IReadOnlyList$Node` stopped
            // being an IReadOnlyList of Node, and an array could not be one.
            Canon = d.Canon,
            CanonMadeWritten = d.CanonMadeWritten,
            CanonMade = d.CanonMade,
            Specialised = d.Specialised,
            Template = d.Template,
            TemplateParams = d.TemplateParams,

            // AND WHERE IT WAS WRITTEN. A nested type's copy is still nested,
            // and losing that makes `Outer.Inner` stop resolving the moment the
            // outer type is generic; the namespace and the file's using
            // directives are the same promise about a wider scope.
            Outer = d.Outer,
            Namespace = d.Namespace,
            Scope = d.Scope,
        };

        made.TemplateArgs.AddRange(d.TemplateArgs);

        foreach (TypeRef b in d.Bases)
        {
            made.Bases.Add(Sub(b, map));
        }

        made.Attributes.AddRange(d.Attributes);
        made.AttributeParts.AddRange(d.AttributeParts);

        foreach (EnumMember em in d.EnumMembers)
        {
            EnumMember copy = new() { Name = em.Name, Value = em.Value, Line = em.Line, Col = em.Col };

            copy.Attributes.AddRange(em.Attributes);
            made.EnumMembers.Add(copy);
        }

        // NUMBERED AS WE GO, so a specialisation can find the same member of the
        // canonical copy. Both are clones of this list in this order, so the
        // index is the only identity that survives substitution -- the name
        // does not, once overloads mangle by their argument types.
        string wasNamespace = _inNamespace;
        FileScope? wasUsings = _usings;

        for (int i = 0; i < d.Members.Count; i++)
        {
            // EACH MEMBER'S OWN FILE, because a partial class is written across
            // several of them and each brought its own using directives. This
            // compiler's own Lowering is eleven files, six of which say `using
            // Block = Corsac.Lang.Ir.Block` and five of which do not.
            if (d.Members[i].Scope != null)
            {
                _inNamespace = d.Members[i].Namespace;
                _usings = d.Members[i].Scope;
            }

            _canonSelf = _canonParams is not null && !d.Members[i].Mods.HasFlag(Mods.Static);
            _canonMarked = false;
            MemberDecl copy;
            try { copy = RewriteMember(d.Members[i], map, name); }
            finally { _canonSelf = false; }
            copy.ReadsTypeArguments = _canonMarked || d.Members[i].ReadsTypeArguments;

            copy.Scope = d.Members[i].Scope;
            copy.OwnedImplementation = d.TypeParams.Count != 0 ? true : d.Members[i].OwnedImplementation;
            copy.Namespace = d.Members[i].Namespace;
            copy.TemplateIndex = i;

            // AND WHICH FILE IT CAME FROM, which every clone above forgets
            // because it builds a fresh node and File is not in the initialiser
            // of any of them.
            //
            // Silent and total when it is missing: an intrinsic is a method
            // DECLARED IN THE PRELUDE, so a prelude method whose file has been
            // forgotten is not an intrinsic any more. Sys.Print became an
            // ordinary call to a stub whose body does nothing, and the machine
            // ran, halted cleanly and said not one word.
            copy.File = d.Members[i].File;
            made.Members.Add(copy);
            _inNamespace = wasNamespace;
            _usings = wasUsings;
        }
        return made;
    }

    /// <summary>
    /// Whether the declaration being copied is a word-shaped specialisation,
    /// whose bodies live in the canonical copy. Its bodies are copied as the
    /// empty markers the parser leaves for a skipped body, so every "has a
    /// body" question still has its answer. A generic method's body is kept:
    /// it is the template that method is specialised from, per call.
    /// </summary>
    private bool _bodiesElsewhere;

    private Block? Body(Block? body, Dictionary<string, TypeRef> map, bool template = false)
        => body is null ? null
         : _bodiesElsewhere && !template ? new Block { Line = body.Line, Col = body.Col }
         : (Block)Rewrite(body, map);

    private MemberDecl RewriteMember(MemberDecl m, Dictionary<string, TypeRef> map, string owner)
    {
        switch (m)
        {
            case FieldDecl f:
            {
                FieldDecl copy = new()
                {
                    Name = f.Name, Mods = f.Mods, Type = Sub(f.Type, map), IsEvent = f.IsEvent,
                    Init = f.Init is null ? null : Rewrite(f.Init, map),
                    DeclaredInit = f.DeclaredInit is null ? null : Rewrite(f.DeclaredInit, map),
                    StaticData = f.StaticData,
                    VtableSlotHint = f.VtableSlotHint,
                    Line = f.Line, Col = f.Col,
                };

                copy.WritableAttributes.AddRange(f.Attributes);
                return copy;
            }

            case PropertyDecl p:
            {
                PropertyDecl copy = new()
                {
                    Name = p.Name, Mods = p.Mods, Type = Sub(p.Type, map),
                    Getter = Body(p.Getter, map),
                    Setter = Body(p.Setter, map),
                    Auto = p.Auto, HasSetter = p.HasSetter,
                    Init = p.Init is null ? null : Rewrite(p.Init, map),
                    VtableSlotHint = p.VtableSlotHint,
                    ExplicitInterface = p.ExplicitInterface,
                    Line = p.Line, Col = p.Col,
                };

                // AN INDEXER'S PARAMETERS, substituted like any other type.
                //
                // Dropping them is silent in a particular way: the accessors
                // are still synthesised, but with no parameters -- so the body
                // that reads `key` reports that key is not declared, in a
                // library file the author did not touch. Same shape as the
                // object-initialiser copy above it; every field added to a
                // declaration has to be added here too.
                foreach (Param ip in p.Params)
                {
                    copy.Params.Add(new Param
                    {
                        Name = ip.Name, Type = Sub(ip.Type, map),
                        IsRef = ip.IsRef, IsOut = ip.IsOut, IsReadOnlyRef = ip.IsReadOnlyRef,
                        NotNullWhen = ip.NotNullWhen, Caller = ip.Caller, CallerArgument = ip.CallerArgument,
                        Line = ip.Line, Col = ip.Col,
                    });
                }
                return copy;
            }

            case MethodDecl md:
            {
                // In scope for the signature AND the body, because `List<T>`
                // can appear in either and means the same thing in both.
                foreach (TypeParam tp in md.TypeParams)
                {
                    _methodParams.Add(tp.Name);
                    _methodParamInfo[tp.Name] = tp;
                }

                MethodDecl made = new()
                {
                    // A constructor is named for its type, so a specialisation
                    // renames its constructors too or they stop being ones.
                    Name = md.IsCtor ? owner : md.Name,
                    Mods = md.Mods,
                    Returns = md.Returns is null ? null : Sub(md.Returns, map),
                    IsCtor = md.IsCtor,
                    Body = Body(md.Body, map, template: md.TypeParams.Count > 0),
                    Init = md.Init is null ? null : RewriteCtorInit(md.Init, map),
                    VtableSlotHint = md.VtableSlotHint,
                    NotNullIfNotNull = md.NotNullIfNotNull,
                    ExplicitInterface = md.ExplicitInterface,
                    Line = md.Line, Col = md.Col,
                };

                // A METHOD'S OWN TYPE PARAMETERS SURVIVE THE CLONE. They belong
                // to the method, not to the class being specialised, so
                // substituting the class's map leaves them untouched -- but
                // dropping them makes the copy's signature name a type nothing
                // declares, and every generic method in the image then reports
                // that its own T is not a known type.
                made.WritableTypeParams.AddRange(md.TypeParams);
                // And its attributes: [DoesNotReturn] is read off the
                // declaration by the checker (Binder.NeverReturns).
                made.WritableAttributes.AddRange(md.Attributes);

                foreach (Param p in md.Params)
                {
                    made.Params.Add(new Param
                    {
                        Name = p.Name, Type = Sub(p.Type, map), IsRef = p.IsRef, IsOut = p.IsOut,
                        IsReadOnlyRef = p.IsReadOnlyRef, IsParams = p.IsParams, IsThis = p.IsThis,
                        NotNullWhen = p.NotNullWhen, Caller = p.Caller, CallerArgument = p.CallerArgument,
                        Default = p.Default is null ? null : Rewrite(p.Default, map),
                        Line = p.Line, Col = p.Col,
                    });
                }

                foreach (TypeParam tp in md.TypeParams)
                {
                    _methodParams.Remove(tp.Name);
                    _methodParamInfo.Remove(tp.Name);
                }

                // WHOSE CODE IT IS SURVIVES THE CLONE. A consumer's own copy
                // of a library's generic method is local however many rounds
                // of rewriting it passes through; dropped here, the flag held
                // for exactly one round and the call went back to being an
                // import of a symbol nothing provides.
                made.LocalCopy = md.LocalCopy;
                made.Fresh = md.Fresh;
                made.File = md.File;
                made.TemplateIndex = md.TemplateIndex;

                // A HOISTED GENERIC LOCAL FUNCTION stays one: its written
                // name, the names it calls by, and how many of its parameters
                // are the variables it captured.
                made.HoistedName = md.HoistedName;
                made.CarriedTypeParams = md.CarriedTypeParams;
                made.LocalGenerics.AddRange(md.LocalGenerics);
                made.Captures = md.Captures;
                made.HoistedIn = md.HoistedIn;
                foreach ((string written, string now) in md.Rehosted) made.Rehosted[written] = now;

                return made;
            }

            default:
                return m;
        }
    }

    private CtorInit RewriteCtorInit(CtorInit init, Dictionary<string, TypeRef> map)
    {
        CtorInit made = new() { IsThis = init.IsThis, Spans = init.Spans, Source = init.Source, Line = init.Line, Col = init.Col };

        foreach (Expr a in init.Args)
        {
            made.Args.Add(Rewrite(a, map));
        }
        made.ArgNames.AddRange(init.ArgNames);
        made.ArgumentOrder.AddRange(init.ArgumentOrder);
        return made;
    }

    /// <summary>
    /// A copy of a statement with the type arguments put in, KEEPING THE FILE
    /// it was written in.
    ///
    /// Every case below builds a fresh node and gives it the line and column
    /// of the original; the file is set in one place instead, because a line
    /// number without its file sends the reader to that line of whichever
    /// source came first on the command line -- which is how an error in
    /// Dynamic.cs was reported against Driver.cs.
    /// </summary>
    private Stmt Rewrite(Stmt s, Dictionary<string, TypeRef> map)
    {
        Stmt made = RewriteStmt(s, map);

        if (made.File.Length == 0)
        {
            made.File = s.File;
        }
        return made;
    }

    private Stmt RewriteStmt(Stmt s, Dictionary<string, TypeRef> map)
    {
        switch (s)
        {
            case Block b:
            {
                Block made = new() { Line = b.Line, Col = b.Col, ArithmeticContext = b.ArithmeticContext, Iterator = b.Iterator };
                made.GenericLocals.AddRange(b.GenericLocals);

                foreach (Stmt inner in b.Statements)
                {
                    made.Statements.Add(Rewrite(inner, map));
                }
                return made;
            }

            case LocalDecl d:
            {
                LocalDecl made = new()
                {
                    Type = d.Type is null ? null : Sub(d.Type, map), Name = d.Name,
                    Init = d.Init is null ? null : Rewrite(d.Init, map),
                    LocalFunction = d.LocalFunction,
                    // A COPY OF A `const` IS STILL A const. Losing it made the
                    // local a variable, and `r is Bx or Bp` -- a pattern over
                    // two of them -- then read the names as types nobody
                    // declared.
                    IsConst = d.IsConst,
                    IsRef = d.IsRef, IsReadOnlyRef = d.IsReadOnlyRef,
                    Line = d.Line, Col = d.Col,
                };

                // AND THE OTHERS DECLARED WITH IT. A copy that lost them is a
                // method whose second and third variables were never declared.
                foreach (LocalDecl also in d.Also)
                {
                    made.Also.Add((LocalDecl)Rewrite(also, map));
                }
                return made;
            }

            case ExprStmt e:
                return new ExprStmt { Expr = Rewrite(e.Expr, map), Line = e.Line, Col = e.Col };

            case IfStmt i:
                return new IfStmt
                {
                    Cond = Rewrite(i.Cond, map), Then = Rewrite(i.Then, map),
                    Else = i.Else is null ? null : Rewrite(i.Else, map),
                    Line = i.Line, Col = i.Col,
                };

            case WhileStmt w:
                return new WhileStmt { Cond = Rewrite(w.Cond, map), Body = Rewrite(w.Body, map), Line = w.Line, Col = w.Col };

            case DoStmt d2:
                return new DoStmt { Cond = Rewrite(d2.Cond, map), Body = Rewrite(d2.Body, map), Line = d2.Line, Col = d2.Col };

            case ForStmt f:
            {
                ForStmt made = new()
                {
                    Init = f.Init is null ? null : Rewrite(f.Init, map),
                    Cond = f.Cond is null ? null : Rewrite(f.Cond, map),
                    Body = Rewrite(f.Body, map), Line = f.Line, Col = f.Col,
                };

                foreach (Expr step in f.Step)
                {
                    made.Step.Add(Rewrite(step, map));
                }
                return made;
            }

            case ForeachStmt fe:
            {
                ForeachStmt made = new()
                {
                    Type = fe.Type is null ? null : Sub(fe.Type, map), Name = fe.Name,
                    Sequence = Rewrite(fe.Sequence, map), Body = Rewrite(fe.Body, map),
                    Line = fe.Line, Col = fe.Col,
                };

                // AND THE NAMES A DECONSTRUCTING LOOP BINDS. A copy that lost
                // them is a loop that binds nothing and whose body cannot see
                // what it was written to see.
                if (fe.Bindings is { } bound)
                {
                    made.Bindings = CopyBindings(bound, map);
                }
                return made;
            }

            case ReturnStmt r:
                return new ReturnStmt { Value = r.Value is null ? null : Rewrite(r.Value, map), Line = r.Line, Col = r.Col };

            case YieldStmt y:
                return new YieldStmt { Value = y.Value is null ? null : Rewrite(y.Value, map), Line = y.Line, Col = y.Col };

            case ThrowStmt t:
                return new ThrowStmt { Value = Rewrite(t.Value, map), IsRethrow = t.IsRethrow, Line = t.Line, Col = t.Col };

            case BreakStmt:
                return new BreakStmt { Line = s.Line, Col = s.Col };

            case ContinueStmt:
                return new ContinueStmt { Line = s.Line, Col = s.Col };

            case GotoStmt g:
                return new GotoStmt { Label = g.Label, Line = g.Line, Col = g.Col };

            case LabeledStmt l:
                return new LabeledStmt { Label = l.Label, Body = Rewrite(l.Body, map), Line = l.Line, Col = l.Col };

            case GotoCaseStmt g:
                return new GotoCaseStmt
                {
                    Value = g.Value is null ? null : Rewrite(g.Value, map),
                    IsDefault = g.IsDefault, Line = g.Line, Col = g.Col,
                };

            case SwitchStmt sw:
            {
                SwitchStmt made = new() { Subject = Rewrite(sw.Subject, map), Line = sw.Line, Col = sw.Col };

                foreach (SwitchCase c in sw.Cases)
                {
                    // ONE FIELD TO COPY, which is the point of a case label
                    // being one expression.
                    //
                    // This was six -- Value, Type, Binding, Property, Accepts
                    // and When -- and it lost two of them at different times.
                    // First the pattern: rebuilt from Value alone, the switch
                    // compiled as though every label were a constant and the
                    // name the pattern introduced was reported as undeclared, in
                    // the body of a method the author never asked to be copied.
                    // Then the guard, which arrived later than the comment
                    // warning about exactly this and was not added to the list:
                    // it vanished silently, so the label matched
                    // unconditionally and a guarded case and the unguarded one
                    // below it both answered the same.
                    //
                    // A copy that forgets a field cannot complain, and neither
                    // of those did. The fix that lasts is not remembering
                    // harder -- it is having one field.
                    SwitchCase madeCase = new()
                    {
                        Pattern = c.Pattern is null ? null : Rewrite(c.Pattern, map),
                        Line = c.Line, Col = c.Col,
                    };

                    foreach (Stmt body in c.Body)
                    {
                        madeCase.Body.Add(Rewrite(body, map));
                    }
                    made.Cases.Add(madeCase);
                }
                return made;
            }

            case TryStmt tr:
            {
                TryStmt made = new()
                {
                    Body = (Block)Rewrite(tr.Body, map),
                    Finally = tr.Finally is null ? null : (Block)Rewrite(tr.Finally, map),
                    Line = tr.Line, Col = tr.Col,
                };

                foreach (CatchClause c in tr.Catches)
                {
                    made.Catches.Add(new CatchClause
                    {
                        Type = c.Type is null ? null : Sub(c.Type, map), Name = c.Name,

                        // AND THE FILTER. The switch guard was dropped by the
                        // equivalent copy a few cases up and matched
                        // unconditionally as a result; this is the same trap
                        // one statement over.
                        When = c.When is null ? null : Rewrite(c.When, map),
                        Body = (Block)Rewrite(c.Body, map), Line = c.Line, Col = c.Col,
                    });
                }
                return made;
            }

            // TAKING A VALUE APART NAMES TYPES TOO. `(Block b, List<VReg> held,
            // bool done) = walk.Pop();` declares three locals, and a copy that
            // left their types as written left `List<VReg>` naming the
            // TEMPLATE -- which converts to nothing, because the value coming
            // out of the tuple is a specialisation of it.
            case DeconstructStmt taken:
            {
                DeconstructStmt made = new()
                {
                    Value = Rewrite(taken.Value, map), Line = taken.Line, Col = taken.Col,
                };

                made.Names.AddRange(CopyBindings(taken.Names, map));
                return made;
            }

            default:
                return s;
        }
    }

    /// <summary>
    /// One pair of initialiser braces, copied element for element.
    ///
    /// Dropping any of it is silent in the worst way: the clone is a perfectly
    /// good `new T(...)` with nothing set, so nothing fails to compile and the
    /// object comes out holding whatever its constructor left.
    /// </summary>
    private void CopyInitBody(InitBody from, InitBody to, Dictionary<string, TypeRef> map)
    {
        foreach (InitAssign init in from.Inits)
        {
            InitBody? nested = null;

            if (init.Nested is InitBody inner)
            {
                nested = new InitBody { Line = inner.Line, Col = inner.Col };
                CopyInitBody(inner, nested, map);
            }

            to.Inits.Add(new InitAssign
            {
                Name = init.Name,
                Value = init.Value is null ? null : Rewrite(init.Value, map),
                Nested = nested,
                Line = init.Line, Col = init.Col,
            });
        }

        foreach (InitAdd add in from.Adds)
        {
            InitAdd copy = new() { Spread = add.Spread, Line = add.Line, Col = add.Col };

            foreach (Expr one in add.Args)
            {
                copy.Args.Add(Rewrite(one, map));
            }

            to.Adds.Add(copy);
        }

        foreach (InitIndex one in from.Indexes)
        {
            InitIndex copy = new()
            {
                Value = Rewrite(one.Value, map), Line = one.Line, Col = one.Col,
            };

            foreach (Expr key in one.Args)
            {
                copy.Args.Add(Rewrite(key, map));
            }

            to.Indexes.Add(copy);
        }
    }

    /// <summary>
    /// The names a deconstruction binds, copied with the lists inside them: a
    /// copy that lost a nested target is a loop whose body cannot see half of
    /// what it was written to see.
    /// </summary>
    private List<Binding> CopyBindings(List<Binding> from, Dictionary<string, TypeRef> map)
        => from.Select(b => new Binding
        {
            Type = b.Type is null ? null : Sub(b.Type, map),
            Name = b.Name,
            Target = b.Target is null ? null : Rewrite(b.Target, map),
            Nested = b.Nested is null ? null : CopyBindings(b.Nested, map),
            Line = b.Line, Col = b.Col,
        }).ToList();

    private Expr Rewrite(Expr e, Dictionary<string, TypeRef> map)
    {
        Expr made = RewriteExpr(e, map);

        if (made.File.Length == 0)
        {
            made.File = e.File;
        }

        // The natural type the checker spelt, substituted -- and so made,
        // which is what it was spelt for.
        if (e.NaturalType is not null && !ReferenceEquals(made, e))
        {
            made.NaturalType = Sub(e.NaturalType, map);
        }
        if (e is NameExpr { CaptureOf: not null } captured && made is NameExpr copied)
        {
            copied.CaptureOf = captured.CaptureOf;
        }
        return made;
    }

    private Expr RewriteExpr(Expr e, Dictionary<string, TypeRef> map)
    {
        switch (e)
        {
            case NameExpr n:
            {
                // A bare name that is a type parameter becomes the type it was
                // bound to; one with type arguments names a specialisation.
                if (n.TypeArgs.Count == 0 && map.TryGetValue(n.Name, out TypeRef? bound))
                {
                    return new NameExpr { Name = bound.Name, Line = n.Line, Col = n.Col };
                }

                if (n.TypeArgs.Count == 0)
                {
                    // A FRESH node, even though nothing changed. Everything
                    // downstream keys its tables by node identity, so two
                    // specialisations sharing one node means the second
                    // binding silently overwrites the first — which presents
                    // as one specialisation using another's field types.
                    return new NameExpr { Name = n.Name, Global = n.Global, Line = n.Line, Col = n.Col };
                }

                List<TypeRef> args = SubAll(n.TypeArgs, map);

                // OPEN WHILE THE METHOD'S OWN TYPE PARAMETERS ARE IN IT, as Sub
                // keeps a type open: `Comparer<T>.Default` inside a generic
                // method is instantiated in each copy of the method, where T is
                // bound. Instantiated here, it made a Comparer$T whose T nothing
                // declares.
                if (args.Any(MentionsMethodParameter))
                {
                    NameExpr open = new() { Name = n.Name, Global = n.Global, Line = n.Line, Col = n.Col };
                    open.TypeArgs.AddRange(args);
                    return open;
                }
                // AND KEPT WITH ITS ARGUMENTS WHERE NO TEMPLATE IS HERE TO
                // MAKE IT: a generic method's copy for one call is made without
                // the templates, and the checker resolves the name there
                // (Binder.CheckName) as it would a written type.
                string made = Instantiate(n.Name, args, n);
                if (made == n.Name)
                {
                    NameExpr kept = new() { Name = n.Name, Global = n.Global, Line = n.Line, Col = n.Col };
                    kept.TypeArgs.AddRange(args);
                    return kept;
                }
                return new NameExpr { Name = made, Global = n.Global, Line = n.Line, Col = n.Col };
            }

            case MemberExpr m:
            {
                MemberExpr made = new()
                {
                    Target = Rewrite(m.Target, map), Name = m.Name,
                    NullConditional = m.NullConditional, Else = m.Else,

                    // AND THE GUARD, which says the null test protecting this
                    // read has already been made -- by the property pattern
                    // that wrote both halves. A copy without it is a read the
                    // checker demands a proof for, and the proof is the line
                    // beside it: `Symbol is { Kind: … }` on a FIELD stopped
                    // compiling, while the same pattern on a local carried on
                    // working because a local is narrowed by the test instead.
                    Guarded = m.Guarded,
                    Line = m.Line, Col = m.Col,
                };
                SubInto(made.TypeArgs, m.TypeArgs, map);
                return made;
            }

            case CallExpr c:
            {
                Expr target;
                if (c.Target is NameExpr { TypeArgs.Count: > 0 } method)
                {
                    // In call position these are method type arguments. Type
                    // instantiation drops unknown names, which used to erase
                    // the only inference input of parameterless M<T>() calls.
                    NameExpr named = new() { Name = method.Name, Line = method.Line, Col = method.Col };
                    SubInto(named.TypeArgs, method.TypeArgs, map);
                    target = named;
                }
                else target = Rewrite(c.Target, map);
                CallExpr made = new() { Target = target, FormatHole = c.FormatHole, Line = c.Line, Col = c.Col };

                foreach (Expr a in c.Args)
                {
                    made.Args.Add(Rewrite(a, map));
                }

                // The names go with them. Losing them turns
                // `With(nullable: true)` in a template into a positional call
                // in every specialisation -- which is right by accident when
                // the named argument is the first one, and silently wrong the
                // moment it is not.
                made.ArgNames.AddRange(c.ArgNames);
                made.LocalArgumentOrder.AddRange(c.LocalArgumentOrder);
                made.Spans = c.Spans;
                made.Source = c.Source;
                made.HiddenTypeArgs = c.HiddenTypeArgs;
                made.ResultTupleNames = c.ResultTupleNames is null ? null : new List<string>(c.ResultTupleNames);
                made.ResultTypeUse = c.ResultTypeUse is null ? null : Sub(c.ResultTypeUse, map);
                if (c.ArgumentTypeUses is not null)
                {
                    made.ArgumentTypeUses = new();
                    foreach (var entry in c.ArgumentTypeUses)
                        made.ArgumentTypeUses[entry.Key] = Sub(entry.Value, map);
                }

                // And whether the receiver has already been moved into the
                // arguments. The checker runs more than once now, and a copy
                // that forgot would have the receiver put in twice.
                made.ReceiverAdded = c.ReceiverAdded;
                made.ParamsPacked = c.ParamsPacked;
                made.CapturesPassed = c.CapturesPassed;
                return made;
            }

            case IndexExpr ix:
            {
                IndexExpr made = new() { Target = Rewrite(ix.Target, map), NullConditional = ix.NullConditional, Line = ix.Line, Col = ix.Col };

                foreach (Expr a in ix.Args)
                {
                    made.Args.Add(Rewrite(a, map));
                }
                return made;
            }

            case SizeOfExpr size:
                return new SizeOfExpr { Type = Sub(size.Type, map), Line = size.Line, Col = size.Col };

            case TypeOfExpr typeOf:
                return Canon(new TypeOfExpr { Type = Sub(typeOf.Type, map), Line = typeOf.Line, Col = typeOf.Col }, typeOf, typeOf.Type, arrayToo: true);

            case RefArgExpr reference:
                return new RefArgExpr
                {
                    Target = Rewrite(reference.Target, map), IsOut = reference.IsOut,
                    Declare = reference.Declare is null ? null : Sub(reference.Declare, map),
                    Name = reference.Name, Line = reference.Line, Col = reference.Col,
                };

            case FromEndExpr fromEnd:
                return new FromEndExpr
                {
                    Offset = Rewrite(fromEnd.Offset, map), Line = fromEnd.Line, Col = fromEnd.Col,
                };

            case WithExpr with:
            {
                WithExpr made = new()
                {
                    Source = Rewrite(with.Source, map), Line = with.Line, Col = with.Col,
                };
                CopyInitBody(with.Body, made.Body, map);
                return made;
            }

            case SwitchExpr choice:
            {
                SwitchExpr made = new()
                {
                    Subject = Rewrite(choice.Subject, map), Line = choice.Line, Col = choice.Col,
                };
                foreach (SwitchArm arm in choice.Arms)
                {
                    SwitchArm copied = new()
                    {
                        Value = arm.Value is null ? null : Rewrite(arm.Value, map),
                        Type = arm.Type is null ? null : Sub(arm.Type, map),
                        Binding = arm.Binding,
                        When = arm.When is null ? null : Rewrite(arm.When, map),
                        Discard = arm.Discard, Result = Rewrite(arm.Result, map),
                        Fallback = arm.Fallback, Line = arm.Line, Col = arm.Col,
                    };
                    // `ICollection<T> c => ...` IN A SHARED COPY, as `is`
                    // (CanonTested): the arm made the test `is` already is --
                    // `_ when <subject> is ICollection<T> c => ...`, which a
                    // switch statement's case label is too, and its name in
                    // scope for the result as a guard's pattern names are.
                    // (Marked as an arm of its own, the type was asked of the
                    // shared copy's own name and no list answered it.)
                    // A shared METHOD copy's arm over its own type parameter
                    // (Shaped) is made the same guard, for the same test.
                    if (arm.Type is { Args.Count: > 0 } written && !arm.Discard && arm.Value is null)
                    {
                        IsExpr test = CanonTested(new IsExpr
                        {
                            Operand = new SubjectExpr { Line = arm.Line, Col = arm.Col },
                            Type = copied.Type!, Binding = arm.Binding, Line = arm.Line, Col = arm.Col,
                        }, new IsExpr { Operand = new SubjectExpr(), Type = written }, written);
                        Shaped(test, new IsExpr { Operand = new SubjectExpr(), Type = written }, written, map);
                        if (test.CanonSlot >= 0 || test.ShapeArgs is not null)
                        {
                            made.Arms.Add(new SwitchArm
                            {
                                Discard = true,
                                When = copied.When is null ? test
                                     : new BinaryExpr { Op = BinOp.AndAlso, Left = test, Right = copied.When, Line = arm.Line, Col = arm.Col },
                                Result = copied.Result, Fallback = copied.Fallback, Line = arm.Line, Col = arm.Col,
                            });
                            continue;
                        }
                    }
                    made.Arms.Add(copied);
                }
                return made;
            }

            case LambdaExpr lambda:
            {
                LambdaExpr made = new()
                {
                    Body = lambda.Body is null ? null : Rewrite(lambda.Body, map),
                    BlockBody = lambda.BlockBody is null ? null : (Block)Rewrite(lambda.BlockBody, map),
                    Async = lambda.Async, Line = lambda.Line, Col = lambda.Col,
                    Returns = lambda.Returns is null ? null : Sub(lambda.Returns, map), ReturnMods = lambda.ReturnMods,
                    TypesWritten = lambda.TypesWritten,
                };
                made.Attributes.AddRange(lambda.Attributes);
                foreach (Param p in lambda.Params)
                {
                    made.Params.Add(new Param
                    {
                        Name = p.Name, Type = Sub(p.Type, map), IsRef = p.IsRef,
                        IsOut = p.IsOut, IsReadOnlyRef = p.IsReadOnlyRef,
                        IsParams = p.IsParams, IsThis = p.IsThis,
                        NotNullWhen = p.NotNullWhen, Caller = p.Caller, CallerArgument = p.CallerArgument,
                        Default = p.Default is null ? null : Rewrite(p.Default, map),
                        Line = p.Line, Col = p.Col,
                    });
                }
                return made;
            }

            // `default(T)` carries a type and therefore has to be substituted:
            // inside List<T> it is default(T), and in List<long> it must become
            // default(long) or the checker is asked about a type parameter that
            // no longer exists.
            case DefaultExpr df:
                return new DefaultExpr
                {
                    Type = Sub(df.Type, map),
                    OfTypeParameter = df.OfTypeParameter
                        || (df.Type.Args.Count == 0 && df.Type.ArrayRank == 0
                            && df.Type.PointerDepth == 0 && map.ContainsKey(df.Type.Name)),
                    Line = df.Line, Col = df.Col,
                };

            case NewExpr nw:
            {
                ParameterMade(nw, map);
                NewExpr made = new()
                {
                    Type = Sub(nw.Type, map),
                    ArraySize = nw.ArraySize is null ? null : Rewrite(nw.ArraySize, map),
                    // A collection expression stays one: the copy is made into
                    // its target's type by the checker, as the original is.
                    Collection = nw.Collection,
                    // Constant bytes stay data (a u8 literal, a constant
                    // byte array read as a span): a copy that forgot would
                    // build the array at every evaluation.
                    Utf8Bytes = nw.Utf8Bytes,
                    Line = nw.Line, Col = nw.Col,
                };

                foreach (Expr a in nw.Args)
                {
                    made.Args.Add(Rewrite(a, map));
                }
                made.ArgNames.AddRange(nw.ArgNames);
                made.Spans = nw.Spans;
                made.Source = nw.Source;
                made.ArgumentOrder.AddRange(nw.ArgumentOrder);

                // AND THE ARRAY'S ELEMENTS. `new[] { a, b }` is the whole of
                // the expression, not decoration on it, and a copy that lost
                // them became `new int` -- a type where an array was written.
                if (nw.Elements is { } listed)
                {
                    made.Elements = new List<Expr>();

                    foreach (Expr one in listed)
                    {
                        made.Elements.Add(Rewrite(one, map));
                    }
                }

                // AND THE OBJECT INITIALISER, which is part of the expression
                // and is therefore part of the copy.
                //
                // Forgetting it is silent in the worst way: the clone is a
                // perfectly good `new T(...)` with no members set, so nothing
                // fails to compile and the object simply comes out holding
                // whatever its constructor left. It only shows up once a
                // generic is instantiated -- which on this machine means the
                // moment the standard library is linked, since List and
                // Dictionary are generic -- so the same source worked alone and
                // stopped working in a real program.
                //
                // AND ITS ELEMENTS, for the same reason and with the same
                // failure: a `new List<int> { 1, 2 }` whose copy lost them is
                // an empty list that compiled.
                CopyInitBody(nw.Body, made.Body, map);
                // An array of a type parameter: its element, as written.
                return nw.ArraySize is not null || nw.Elements is not null ? Canon(made, nw, nw.Type, arrayToo: false, array: true)
                     : nw.Utf8Bytes is null && !nw.Collection ? CanonMadeObject(made, nw) : made;
            }

            // The expressions added with tuples, ranges, throw expressions and
            // once-evaluated patterns. A clone that drops any of them is the
            // same silent failure every other omission here has been.
            case SuppressExpr sure:
                return new SuppressExpr
                {
                    Operand = Rewrite(sure.Operand, map), OpensCell = sure.OpensCell, Line = sure.Line, Col = sure.Col,
                };

            case ThrowExpr th:
                return new ThrowExpr { Value = Rewrite(th.Value, map), Line = th.Line, Col = th.Col };

            case RangeExpr rg:
                return new RangeExpr
                {
                    From = rg.From is null ? null : Rewrite(rg.From, map),
                    To = rg.To is null ? null : Rewrite(rg.To, map),
                    Line = rg.Line, Col = rg.Col,
                };

            case PatternExpr pat:
                return new PatternExpr
                {
                    Subject = Rewrite(pat.Subject, map), Test = Rewrite(pat.Test, map),
                    Line = pat.Line, Col = pat.Col,
                };

            case SequenceExpr seq:
                return new SequenceExpr
                {
                    Effect = Rewrite(seq.Effect, map), Value = Rewrite(seq.Value, map),
                    Line = seq.Line, Col = seq.Col,
                };

            case SubjectExpr subject:
                return new SubjectExpr { Outer = subject.Outer, Line = subject.Line, Col = subject.Col };

            case TupleExpr tup:
            {
                TupleExpr copy = new() { Line = tup.Line, Col = tup.Col };

                foreach (Expr one in tup.Items)
                {
                    copy.Items.Add(Rewrite(one, map));
                }

                copy.Names.AddRange(tup.Names);
                return copy;
            }

            case UnaryExpr u:
                return new UnaryExpr { Op = u.Op, Operand = Rewrite(u.Operand, map), Line = u.Line, Col = u.Col };

            case BinaryExpr b:
                return new BinaryExpr
                {
                    Op = b.Op, Left = Rewrite(b.Left, map), Right = Rewrite(b.Right, map),
                    PatternNullTest = b.PatternNullTest, PatternConstant = b.PatternConstant,
                    Line = b.Line, Col = b.Col,
                };

            case AssignExpr a2:
                return new AssignExpr
                {
                    Op = a2.Op, Target = Rewrite(a2.Target, map), Value = Rewrite(a2.Value, map),
                    Line = a2.Line, Col = a2.Col,
                };

            case ConditionalExpr c2:
                return new ConditionalExpr
                {
                    Cond = Rewrite(c2.Cond, map), Then = Rewrite(c2.Then, map), Else = Rewrite(c2.Else, map),
                    Line = c2.Line, Col = c2.Col,
                };

            case CastExpr cast:
            {
                CastExpr made = new() { Type = Sub(cast.Type, map), Operand = Rewrite(cast.Operand, map), Line = cast.Line, Col = cast.Col };
                // Only a cast to a constructed type over the parameters: a
                // cast to T itself is the word it always was.
                Shaped(made, cast, cast.Type, map);
                return cast.CanonSlot >= 0 || cast.Type.Args.Count > 0 ? Canon(made, cast, cast.Type, arrayToo: false) : made;
            }

            case IsExpr isx:
            {
                IsExpr made = new() { Operand = Rewrite(isx.Operand, map), Type = Sub(isx.Type, map), Binding = isx.Binding, Line = isx.Line, Col = isx.Col };
                Shaped(made, isx, isx.Type, map);
                return Canon(made, isx, isx.Type, arrayToo: false);
            }

            case AsExpr asx:
            {
                AsExpr made = new() { Operand = Rewrite(asx.Operand, map), Type = Sub(asx.Type, map), Line = asx.Line, Col = asx.Col };
                Shaped(made, asx, asx.Type, map);
                return Canon(made, asx, asx.Type, arrayToo: false);
            }

            case AwaitExpr aw:
                return new AwaitExpr { Operand = Rewrite(aw.Operand, map), Line = aw.Line, Col = aw.Col };

            case LiteralExpr l:
                return new LiteralExpr
                {
                    Kind = l.Kind, Text = l.Text, IntValue = l.IntValue, RealValue = l.RealValue,
                    Line = l.Line, Col = l.Col,
                };

            case ThisExpr:
                return new ThisExpr { Line = e.Line, Col = e.Col };

            case BaseExpr:
                return new BaseExpr { Line = e.Line, Col = e.Col };

            default:
                // Anything not cloned above would be SHARED between
                // specialisations, which is the bug described on NameExpr.
                return e;
        }
    }
}
