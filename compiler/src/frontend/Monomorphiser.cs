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
    private readonly Queue<Job> _pending = new();

    /// <summary>One specialisation waiting to be made.</summary>
    private readonly record struct Job(
        TypeDecl Template,
        List<TypeRef> Args,
        string Name,
        bool External,
        string? Canon);

    /// <summary>Whether this compilation is building a library of its own.</summary>
    private bool _library;

    /// <summary>
    /// Type arguments that are one machine word in the integer bank, and
    /// therefore share compiled code. See TypeDecl.Canon.
    /// </summary>
    public const string CanonName = "__canon";

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
    public static MethodDecl Specialise(MethodDecl template, IReadOnlyList<TypeRef> args, string name)
    {
        Monomorphiser m = new("<specialise>");
        Dictionary<string, TypeRef> map = new(StringComparer.Ordinal);

        for (int i = 0; i < template.TypeParams.Count && i < args.Count; i++)
        {
            map[template.TypeParams[i].Name] = args[i];
        }

        MethodDecl made = (MethodDecl)m.RewriteMember(template, map, template.Name);

        made.TypeParams.Clear();
        made.Name = name;
        return made;
    }

    /// <summary>The name a specialised method gets, readable on purpose.</summary>
    public static string MethodName(string baseName, IReadOnlyList<TypeRef> args)
        => MangledName(baseName, args.ToList());

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
            Args = args,
            ArrayRank = r.ArrayRank,
            Nullable = r.Nullable,
            ElementNullable = r.ElementNullable,
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

        return _soleNested.TryGetValue(name, out string? sole) && sole is not null ? sole : name;
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

        // nint and nuint are a machine word BY DEFINITION, whatever the word is.
        if (r.Name is "nint" or "nuint")
        {
            return true;
        }

        // A long is word-shaped only where the word is 64 bits. On a 32-bit
        // target it is a register pair, and code compiled over one word
        // cannot carry it.
        if (r.Name is "long" or "ulong")
        {
            return Target.Current.NativeI64;
        }

        return r.Name is "string" or "object" or CanonName
            || _byRef.Contains(r.Name);
    }

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
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0))
        {
            _generic[Arity(TemplatePath(t), t.TypeParams.Count)] = t;
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
            foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0 && !t.External && !t.Elsewhere))
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
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count > 0 && t.External))
        {
            Canonicalise(t);
        }

        CompilationUnit output = new() { Line = unit.Line, Col = unit.Col };
        output.Usings.AddRange(unit.Usings);
        output.TupleNamings.AddRange(unit.TupleNamings);

        // Concrete declarations pass through, with their bodies rewritten so
        // any generic reference inside them names a specialisation.
        foreach (TypeDecl t in unit.Types.Where(t => t.TypeParams.Count == 0))
        {
            output.Types.Add(RewriteDecl(t, new Dictionary<string, TypeRef>(StringComparer.Ordinal), t.Name));
        }

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

            for (int i = 0; i < job.Template.TypeParams.Count && i < job.Args.Count; i++)
            {
                map[job.Template.TypeParams[i].Name] = job.Args[i];
            }

            TypeDecl made = RewriteDecl(job.Template, map, job.Name);

            // WHERE ITS CODE LIVES, which is the whole of code sharing.
            //
            // The declaration is complete either way -- every field, every
            // signature, every body -- because that is what checks the caller
            // and lays out the object. Canon says only that the INSTRUCTIONS
            // are somewhere else, and External says that somewhere else is
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

            if (job.External)
            {
                made.LibSlot = job.Template.LibSlot;
            }

            _made[job.Name] = made;
            output.Types.Add(made);
        }
        output.TupleNamings.AddRange(_tupleNamings);
        return output;
    }

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
            List<TypeRef> bodyArgs = template.TypeParams
                .Select(p => Constraining(p)
                          ?? new TypeRef { Name = CanonName, Line = template.Line, Col = template.Col })
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

    private string? GenericPath(string name, int arity, Node location)
    {
        bool Candidate(string candidate)
        {
            string key = Arity(candidate, arity);
            if (_generic.ContainsKey(key)) return true;
            _requireDeclaration?.Invoke(key);
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
                string candidate = import.Namespace + "." + name;
                if (!Candidate(candidate)) continue;
                if (found is not null && found != candidate)
                    throw new CompileError(_file, location.Line, location.Col, "ambiguous generic type '" + name + "': " + found + " or " + candidate);
                found = candidate;
            }
            return found;
        }
        foreach (string from in new[] { _scope, _inNamespace })
            for (string scope = from; scope.Length > 0; )
            {
                string candidate = scope + "." + name;
                if (Candidate(candidate)) return candidate;
                if (Imports(scope) is { } imported) return imported;
                int dot = scope.LastIndexOf('.'); scope = dot < 0 ? "" : scope[..dot];
            }
        if (Candidate(name)) return name;
        return Imports("");
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

        if (word && template.TypeParams.Count > 0)
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
            bool valueBound = bound.ArrayRank == 0 && bound.PointerDepth == 0
                && (Narrow.Contains(bound.Name) || _byValue.Contains(bound.Name)
                    || bound.Name is "long" or "ulong" or "nint" or "nuint" or "decimal");

            return new TypeRef
            {
                Name = bound.Name,
                ArrayRank = r.ArrayRank + bound.ArrayRank,
                Nullable = arrayFromUse ? r.Nullable
                         : ((r.Nullable && !valueBound) || bound.Nullable),
                ElementNullable = arrayFromUse
                                ? bound.Nullable || bound.ElementNullable
                                : r.ElementNullable || bound.ElementNullable,
                PointerDepth = r.PointerDepth + bound.PointerDepth,
                Args = bound.Args.ToList(),
                UseArgs = bound.UseArgs,

                // AND THE ELEMENT NAMES, when what T was bound to is a tuple.
                // `List<(int At, string Label)>` substitutes the whole tuple in
                // here, and a copy that kept its shape but lost its names left
                // `l[0].Label` reporting that the element does not exist.
                TupleNames = bound.TupleNames is null ? null : new List<string>(bound.TupleNames),
                Line = r.Line, Col = r.Col,
            };
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
                ElementNullable = r.ElementNullable,
                PointerDepth = r.PointerDepth,
                TupleNames = r.TupleNames is null ? null : new List<string>(r.TupleNames),
                Line = r.Line, Col = r.Col,
            };

            tuple.Args.AddRange(r.Args.Select(a => Sub(a, map)));
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
        List<TypeRef> args = r.Args.Select(a => Sub(a, map)).ToList();

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

            named.Args.AddRange(args);
            _tupleNamings.Add(named);
        }

        ArrayIsASequence(r);

        string name = args.Count > 0 && !open ? Instantiate(r.Name, args, r) : r.Name;

        TypeRef made = new()
        {
            Name = name, ArrayRank = r.ArrayRank, Nullable = r.Nullable,
            // These annotations cross into the template's scope too, including
            // arguments already hidden inside a nested specialised type name.
            UseArgs = args.Count > 0 && name != r.Name ? args.Select(Qualify).ToList()
                : r.UseArgs?.Select(a => Sub(a, map)).ToList(),
            ElementNullable = r.ElementNullable,
            PointerDepth = r.PointerDepth,
            Line = r.Line, Col = r.Col,
        };

        // A specialised name carries its arguments in the name, so the argument
        // list must not survive or the binder will look for a generic again.
        // An OPEN one keeps them, because looking for a generic is exactly
        // what the checker has to do with it.
        if (args.Count > 0 && name == r.Name)
        {
            made.Args.AddRange(args);
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
    private void ArrayIsASequence(TypeRef r)
    {
        if (r.ArrayRank != 1 || r.Name.Length == 0 || _methodParams.Contains(r.Name))
        {
            return;
        }

        TypeRef element = new()
        {
            Name = r.Name, Args = r.Args, Nullable = r.ElementNullable,
            PointerDepth = r.PointerDepth, TupleNames = r.TupleNames,
            Line = r.Line, Col = r.Col,
        };

        bool Open(TypeRef a)
            => (a.Args.Count == 0 && _methodParams.Contains(a.Name)) || a.Args.Any(Open);

        if (Open(element))
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

        _scope = d.Outer is null ? d.Name : d.Outer + "." + d.Name;
        _inNamespace = d.Namespace;
        _usings = d.Scope;

        try
        {
            return RewriteDeclIn(d, map, name);
        }
        finally
        {
            _scope = wasScope;
            _inNamespace = wasNamespace;
            _usings = wasUsings;
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
            InitialisersPlaced = d.InitialisersPlaced,
            FromLibrary = d.FromLibrary,
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
            Specialised = d.Specialised,
            Template = d.Template,

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

        foreach (EnumMember em in d.EnumMembers)
        {
            made.EnumMembers.Add(new EnumMember { Name = em.Name, Value = em.Value, Line = em.Line, Col = em.Col });
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

            MemberDecl copy = RewriteMember(d.Members[i], map, name);

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

    private MemberDecl RewriteMember(MemberDecl m, Dictionary<string, TypeRef> map, string owner)
    {
        switch (m)
        {
            case FieldDecl f:
                return new FieldDecl
                {
                    Name = f.Name, Mods = f.Mods, Type = Sub(f.Type, map),
                    Init = f.Init is null ? null : Rewrite(f.Init, map),
                    VtableSlotHint = f.VtableSlotHint,
                    Line = f.Line, Col = f.Col,
                };

            case PropertyDecl p:
            {
                PropertyDecl copy = new()
                {
                    Name = p.Name, Mods = p.Mods, Type = Sub(p.Type, map),
                    Getter = p.Getter is null ? null : (Block)Rewrite(p.Getter, map),
                    Setter = p.Setter is null ? null : (Block)Rewrite(p.Setter, map),
                    Auto = p.Auto, HasSetter = p.HasSetter,
                    Init = p.Init is null ? null : Rewrite(p.Init, map),
                    VtableSlotHint = p.VtableSlotHint,
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
                        NotNullWhen = ip.NotNullWhen,
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
                }

                MethodDecl made = new()
                {
                    // A constructor is named for its type, so a specialisation
                    // renames its constructors too or they stop being ones.
                    Name = md.IsCtor ? owner : md.Name,
                    Mods = md.Mods,
                    Returns = md.Returns is null ? null : Sub(md.Returns, map),
                    IsCtor = md.IsCtor,
                    Body = md.Body is null ? null : (Block)Rewrite(md.Body, map),
                    Init = md.Init is null ? null : RewriteCtorInit(md.Init, map),
                    VtableSlotHint = md.VtableSlotHint,
                    NotNullIfNotNull = md.NotNullIfNotNull,
                    Line = md.Line, Col = md.Col,
                };

                // A METHOD'S OWN TYPE PARAMETERS SURVIVE THE CLONE. They belong
                // to the method, not to the class being specialised, so
                // substituting the class's map leaves them untouched -- but
                // dropping them makes the copy's signature name a type nothing
                // declares, and every generic method in the image then reports
                // that its own T is not a known type.
                made.TypeParams.AddRange(md.TypeParams);

                foreach (Param p in md.Params)
                {
                    made.Params.Add(new Param
                    {
                        Name = p.Name, Type = Sub(p.Type, map), IsRef = p.IsRef, IsOut = p.IsOut,
                        IsReadOnlyRef = p.IsReadOnlyRef, IsParams = p.IsParams, IsThis = p.IsThis,
                        NotNullWhen = p.NotNullWhen,
                        Default = p.Default is null ? null : Rewrite(p.Default, map),
                        Line = p.Line, Col = p.Col,
                    });
                }

                foreach (TypeParam tp in md.TypeParams)
                {
                    _methodParams.Remove(tp.Name);
                }

                // WHOSE CODE IT IS SURVIVES THE CLONE. A consumer's own copy
                // of a library's generic method is local however many rounds
                // of rewriting it passes through; dropped here, the flag held
                // for exactly one round and the call went back to being an
                // import of a symbol nothing provides.
                made.LocalCopy = md.LocalCopy;
                made.File = md.File;
                made.TemplateIndex = md.TemplateIndex;

                return made;
            }

            default:
                return m;
        }
    }

    private CtorInit RewriteCtorInit(CtorInit init, Dictionary<string, TypeRef> map)
    {
        CtorInit made = new() { IsThis = init.IsThis, Line = init.Line, Col = init.Col };

        foreach (Expr a in init.Args)
        {
            made.Args.Add(Rewrite(a, map));
        }
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
                Block made = new() { Line = b.Line, Col = b.Col, ArithmeticContext = b.ArithmeticContext };

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

            case ThrowStmt t:
                return new ThrowStmt { Value = Rewrite(t.Value, map), Line = t.Line, Col = t.Col };

            case BreakStmt:
                return new BreakStmt { Line = s.Line, Col = s.Col };

            case ContinueStmt:
                return new ContinueStmt { Line = s.Line, Col = s.Col };

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
            InitAdd copy = new() { Line = add.Line, Col = add.Col };

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
                    return new NameExpr { Name = n.Name, Line = n.Line, Col = n.Col };
                }

                List<TypeRef> args = n.TypeArgs.Select(a => Sub(a, map)).ToList();
                return new NameExpr { Name = Instantiate(n.Name, args, n), Line = n.Line, Col = n.Col };
            }

            case MemberExpr m:
            {
                MemberExpr made = new()
                {
                    Target = Rewrite(m.Target, map), Name = m.Name,
                    NullConditional = m.NullConditional,

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
                made.TypeArgs.AddRange(m.TypeArgs.Select(a => Sub(a, map)));
                return made;
            }

            case CallExpr c:
            {
                CallExpr made = new() { Target = Rewrite(c.Target, map), Line = c.Line, Col = c.Col };

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
                return made;
            }

            case IndexExpr ix:
            {
                IndexExpr made = new() { Target = Rewrite(ix.Target, map), Line = ix.Line, Col = ix.Col };

                foreach (Expr a in ix.Args)
                {
                    made.Args.Add(Rewrite(a, map));
                }
                return made;
            }

            case SizeOfExpr size:
                return new SizeOfExpr { Type = Sub(size.Type, map), Line = size.Line, Col = size.Col };

            case TypeOfExpr typeOf:
                return new TypeOfExpr { Type = Sub(typeOf.Type, map), Line = typeOf.Line, Col = typeOf.Col };

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
                    made.Arms.Add(new SwitchArm
                    {
                        Value = arm.Value is null ? null : Rewrite(arm.Value, map),
                        Type = arm.Type is null ? null : Sub(arm.Type, map),
                        Binding = arm.Binding,
                        When = arm.When is null ? null : Rewrite(arm.When, map),
                        Discard = arm.Discard, Result = Rewrite(arm.Result, map),
                        Line = arm.Line, Col = arm.Col,
                    });
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
                };
                foreach (Param p in lambda.Params)
                {
                    made.Params.Add(new Param
                    {
                        Name = p.Name, Type = Sub(p.Type, map), IsRef = p.IsRef,
                        IsOut = p.IsOut, IsReadOnlyRef = p.IsReadOnlyRef,
                        IsParams = p.IsParams, IsThis = p.IsThis,
                        NotNullWhen = p.NotNullWhen,
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
                NewExpr made = new()
                {
                    Type = Sub(nw.Type, map),
                    ArraySize = nw.ArraySize is null ? null : Rewrite(nw.ArraySize, map),
                    Line = nw.Line, Col = nw.Col,
                };

                foreach (Expr a in nw.Args)
                {
                    made.Args.Add(Rewrite(a, map));
                }
                made.ArgNames.AddRange(nw.ArgNames);
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
                return made;
            }

            // The expressions added with tuples, ranges, throw expressions and
            // once-evaluated patterns. A clone that drops any of them is the
            // same silent failure every other omission here has been.
            case SuppressExpr sure:
                return new SuppressExpr
                {
                    Operand = Rewrite(sure.Operand, map), Line = sure.Line, Col = sure.Col,
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

            case SubjectExpr subject:
                return new SubjectExpr { Line = subject.Line, Col = subject.Col };

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
                    PatternNullTest = b.PatternNullTest,
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
                return new CastExpr { Type = Sub(cast.Type, map), Operand = Rewrite(cast.Operand, map), Line = cast.Line, Col = cast.Col };

            case IsExpr isx:
                return new IsExpr { Operand = Rewrite(isx.Operand, map), Type = Sub(isx.Type, map), Binding = isx.Binding, Line = isx.Line, Col = isx.Col };

            case AsExpr asx:
                return new AsExpr { Operand = Rewrite(asx.Operand, map), Type = Sub(asx.Type, map), Line = asx.Line, Col = asx.Col };

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
