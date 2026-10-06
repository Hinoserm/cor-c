using System.Text;

namespace Corsac.Lang;

/// <summary>
/// .NET'S COM INTEROP, WRITTEN OUT AS SOURCE before anything is bound: the
/// classes .NET's runtime makes for itself at run time, made here once at
/// compile time from the declarations, as RegistryDeclarations makes a
/// settings class's properties -- and compiled like any other code.
///
/// A RUNTIME-CALLABLE WRAPPER for each `[ComImport, Guid("...")]` interface
/// I: `__Rcw_I`, a sibling of I deriving from System.__ComObject and
/// implementing I (and the [ComImport] interfaces I extends). Each member
/// calls through the native interface pointer's vtable -- IUnknown's three
/// slots first (InterfaceIsIUnknown), or IDispatch's seven (InterfaceIsDual,
/// the default), then the members in declaration order, a property's get
/// before its set -- with the arguments marshalled as .NET marshals them
/// for COM (System.Runtime.InteropServices.ComFrame): a string as a BSTR, a
/// bool as a VARIANT_BOOL, an object as a VARIANT, an interface as its
/// interface pointer, an array as a SAFEARRAY, [MarshalAs] where it says
/// otherwise; the HRESULT turned into an exception and the last [out,
/// retval] into the result, unless the member is [PreserveSig]. An
/// InterfaceIsIDispatch interface's members go through IDispatch::Invoke by
/// [DispId] or by name (ComDispatch). The static `__Wrap(object)` is what
/// a cast of a COM object to I calls (Lowering's ComWrap): I's interface
/// asked of it (QueryInterface), or null.
///
/// A COM-CALLABLE WRAPPER for each class that implements a COM interface --
/// [ComImport], [Guid] or [ComVisible(true)] -- or is [ComVisible(true)]
/// itself: the class made System.Runtime.InteropServices.IComCallable,
/// whose `__ComInterfaces()` answers each interface's IID and native vtable
/// (built by `__Ccw_C`, a sibling class of [UnmanagedCallersOnly] stdcall
/// entries that unmarshal the arguments, call the interface's member on the
/// object, and marshal the results and the HRESULT back), and whose
/// `__ComDispId` and `__ComInvoke` are its IDispatch: its public methods and
/// properties, and its interfaces' [DispId] members, by name and by number.
/// The library's ComCallable keeps the wrapper's identity, reference count
/// and the handle that keeps the object alive while native code holds it.
///
/// What is left out says so: a generic interface or class, an indexer, a
/// struct or a delegate as a parameter.
/// </summary>
public static class ComDeclarations
{
    public const string WrapMethod = "__Wrap";
    const string Ns = "System.Runtime.InteropServices";

    /// <summary>The wrapper class written beside a [ComImport] interface.</summary>
    public static string RcwName(TypeDecl d) => "__Rcw_" + d.Name;

    static string CcwName(TypeDecl d) => "__Ccw_" + d.Name;

    /// <summary>Whether an interface is called through COM: [ComImport].</summary>
    public static bool IsComImport(TypeDecl d)
        => d.Kind == TypeKind.Interface && d.AttributeParts.Any(a => a.Target.Length == 0 && a.Is("ComImport"));

    static bool ComVisible(TypeDecl d)
        => d.AttributeParts.Any(a => a.Target.Length == 0 && a.Is("ComVisible") && a.Argument == "true");

    static bool ComInterface(TypeDecl d)
        => d.Kind == TypeKind.Interface && d.TypeParams.Count == 0
        && (IsComImport(d) || ComVisible(d) || d.AttributeParts.Any(a => a.Target.Length == 0 && a.Is("Guid")));

    /// <summary>Base slots: 3 for IUnknown, 7 for IDispatch and dual, 6 for IInspectable; -1 for a dispinterface.</summary>
    static int BaseSlots(TypeDecl d)
    {
        AttributeRef? kind = d.AttributeParts.FirstOrDefault(a => a.Target.Length == 0 && a.Is("InterfaceType"));
        if (kind is null) return 7;
        string word = kind.Argument ?? kind.Arguments.FirstOrDefault()?.Words.LastOrDefault() ?? "";
        if (kind.Arguments.Count > 0 && kind.Arguments[0].Words.Count > 0) word = kind.Arguments[0].Words[^1];
        return word switch
        {
            "InterfaceIsIUnknown" or "1" => 3,
            "InterfaceIsIDispatch" or "2" => -1,
            "InterfaceIsIInspectable" or "3" => 6,
            _ => 7,
        };
    }

    /// <summary>
    /// An interface's IID: its [Guid], or -- for a [ComVisible] one that has
    /// none -- one made from its full name, the same every compile (not
    /// .NET's, which hashes its assembly too; a program that needs the
    /// interface known elsewhere says its [Guid]).
    /// </summary>
    static string Iid(TypeDecl d)
    {
        AttributeRef? g = d.AttributeParts.FirstOrDefault(a => a.Target.Length == 0 && a.Is("Guid"));
        if (g?.Argument is { Length: > 0 } written) return written.Trim('{', '}').ToUpperInvariant();
        ulong a = 0xCBF29CE484222325, b = 0x84222325CBF29CE4;
        foreach (char c in FullName(d))
        {
            a = (a ^ c) * 0x100000001B3;
            b = (b ^ (ulong)(c * 31)) * 0x100000001B3;
        }
        b = (b & 0xFFFFFFFFFFFF0FFF) | 0x0000000000004000;
        string h = Hex(a) + Hex(b);
        return h[..8] + "-" + h[8..12] + "-" + h[12..16] + "-" + h[16..20] + "-" + h[20..32];
    }

    static string Hex(ulong v)
    {
        const string digits = "0123456789ABCDEF";
        char[] made = new char[16];
        for (int i = 15; i >= 0; i--) { made[i] = digits[(int)(v & 15)]; v >>= 4; }
        return new string(made);
    }

    static string FullName(TypeDecl d) => d.Outer is null ? d.Name : d.Outer + "." + d.Name;

    /// <summary>A type's name as generated code writes it, from the global namespace down: `global::Ns.Outer.Name`.</summary>
    static string Qualified(TypeDecl d) => "global::" + FullName(d);

    // ---- the pass ------------------------------------------------------------------------

    sealed class Pass
    {
        public readonly CompilationUnit Unit;
        public readonly List<CompileError> Errors;
        public readonly Dictionary<string, List<TypeDecl>> ByName = new(StringComparer.Ordinal);

        public Pass(CompilationUnit unit, List<CompileError> errors)
        {
            Unit = unit;
            Errors = errors;
            foreach (TypeDecl t in unit.Types)
            {
                if (!ByName.TryGetValue(t.Name, out List<TypeDecl>? list)) ByName[t.Name] = list = new();
                list.Add(t);
            }
        }

        /// <summary>The declaration a written type names, as near to `at` as it can be found.</summary>
        public TypeDecl? Resolve(TypeRef r, TypeDecl at)
        {
            if (r.Args.Count > 0 || r.IsFunctionPointer) return null;
            string name = r.Name;
            int dot = name.LastIndexOf('.');
            string simple = dot < 0 ? name : name[(dot + 1)..];
            string? qualifier = dot < 0 ? null : name[..dot];
            if (!ByName.TryGetValue(simple, out List<TypeDecl>? all)) return null;
            List<TypeDecl> found = all.Where(t => t.TypeParams.Count == 0).ToList();
            if (qualifier is not null) found = found.Where(t => (t.Outer ?? "").EndsWith(qualifier, StringComparison.Ordinal)).ToList();
            if (found.Count <= 1) return found.FirstOrDefault();
            // Nearest first: a sibling, then one in the same namespace.
            TypeDecl? sibling = found.FirstOrDefault(t => t.Outer == at.Outer || t.Outer == FullName(at));
            if (sibling is not null) return sibling;
            return found.FirstOrDefault(t => t.Namespace == at.Namespace) ?? found[0];
        }

        public void Error(Node where, string message) => Errors.Add(new CompileError(where.File, where.Line, where.Col, message));
    }

    /// <summary>
    /// The wrappers written and parsed into the unit; the classes that get a
    /// COM-callable wrapper made IComCallable. Before binding, after the
    /// partial types are merged (Frontend).
    /// </summary>
    public static void Expand(CompilationUnit unit, IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        Pass pass = new(unit, errors);
        List<TypeDecl> imports = unit.Types.Where(t => IsComImport(t) && !t.SignatureOnly).ToList();
        List<TypeDecl> callable = unit.Types.Where(t => t.Kind == TypeKind.Class && !t.SignatureOnly && !t.IsDelegate
            && !t.Mods.HasFlag(Mods.Static) && !t.Name.StartsWith("__", StringComparison.Ordinal) && Callable(pass, t)).ToList();
        if (imports.Count == 0 && callable.Count == 0) return;

        foreach (TypeDecl i in imports)
        {
            if (i.TypeParams.Count > 0) { pass.Error(i, $"'{i.Name}': a generic interface cannot be a COM interface"); continue; }
            string? text = Rcw(pass, i);
            if (text is not null) AddParsed(unit, text, i, symbols, errors);
        }

        HashSet<TypeDecl> made = new(callable);
        foreach (TypeDecl c in callable)
        {
            if (c.TypeParams.Count > 0) { pass.Error(c, $"'{c.Name}': a generic class cannot be COM-callable"); continue; }
            bool inherits = Ancestors(pass, c).Any(a => made.Contains(a) || (a.SignatureOnly && Callable(pass, a)));
            string? text = Ccw(pass, c, symbols);
            if (text is null) continue;
            AddParsed(unit, text, c, symbols, errors);
            AddCallableMembers(pass, c, inherits, symbols, errors);
        }
    }

    /// <summary>The source parsed, its types made siblings of `beside`: same namespace, outer type and usings.</summary>
    static void AddParsed(CompilationUnit unit, string text, TypeDecl beside, IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        CompilationUnit parsed;
        try { parsed = Parser.ParseText(text, "<com:" + beside.Name + ">", symbols); }
        catch (CompileError e) { errors.Add(e); return; }
        foreach (TypeDecl t in parsed.Types)
        {
            Place(t, beside);
            unit.Types.Add(t);
        }
    }

    static void Place(TypeDecl t, TypeDecl beside)
    {
        t.Outer = beside.Outer;
        t.Namespace = beside.Namespace;
        t.Scope = beside.Scope;
        t.File = beside.File;
        t.SourcePath = beside.SourcePath;
        t.FromLibrary = beside.FromLibrary;
        t.Elsewhere = beside.Elsewhere;
        foreach (MemberDecl m in t.Members)
        {
            m.Scope = beside.Scope;
            m.Namespace = beside.Namespace;
            m.File = beside.File;
            if (beside.Elsewhere) m.OwnedImplementation = false;
        }
    }

    static IEnumerable<TypeDecl> Ancestors(Pass pass, TypeDecl c)
    {
        HashSet<TypeDecl> seen = new();
        TypeDecl at = c;
        while (true)
        {
            TypeDecl? up = at.Bases.Select(b => pass.Resolve(b, at)).FirstOrDefault(d => d is { Kind: TypeKind.Class });
            if (up is null || !seen.Add(up)) yield break;
            yield return up;
            at = up;
        }
    }

    /// <summary>The COM interfaces a class implements, its own and its bases', each once.</summary>
    static List<TypeDecl> Interfaces(Pass pass, TypeDecl c)
    {
        List<TypeDecl> found = new();
        void Walk(TypeDecl i)
        {
            if (found.Contains(i)) return;
            found.Add(i);
            foreach (TypeRef b in i.Bases)
            {
                if (pass.Resolve(b, i) is { Kind: TypeKind.Interface } up && (ComInterface(up) || (ComVisible(c) && Marshallable(pass, up)))) Walk(up);
            }
        }
        foreach (TypeDecl at in new[] { c }.Concat(Ancestors(pass, c)))
        {
            bool visible = ComVisible(at) || ComVisible(c);
            foreach (TypeRef b in at.Bases)
            {
                if (pass.Resolve(b, at) is not { Kind: TypeKind.Interface } i || i.TypeParams.Count > 0 || i.Name == "IComCallable") continue;
                // A [ComVisible] class's other interfaces go to COM only when
                // COM can carry every member: the rest stay .NET's.
                if (ComInterface(i) || (visible && Marshallable(pass, i))) Walk(i);
            }
        }
        return found;
    }

    /// <summary>Whether every member of an interface has a COM form: tried on a pass whose errors are thrown away.</summary>
    static bool Marshallable(Pass pass, TypeDecl i)
    {
        Pass trial = new(pass.Unit, new List<CompileError>());
        List<Slot>? slots = Slots(trial, i);
        if (slots is null) return false;
        foreach (Slot slot in slots)
        {
            foreach (Param p in slot.Params) if (KindOf(trial, p.Type, p.MarshalAs, i, out _) is null) return false;
            if (slot.Returns is not null && KindOf(trial, slot.Returns, slot.ReturnMarshal, i, out _) is null) return false;
        }
        return trial.Errors.Count == 0;
    }

    static bool Callable(Pass pass, TypeDecl c)
        => ComVisible(c) || Interfaces(pass, c).Count > 0;

    // ---- marshalling kinds ---------------------------------------------------------------

    /// <summary>
    /// How a parameter or result is on the native side: the name its
    /// ComFrame and ComMarshal helpers carry (InI4, GetBstr, ArgVariant...),
    /// what the managed value is cast through, and how many stack words it
    /// takes by value on i386 (x86-64 passes everything in one: a VARIANT and
    /// a GUID by reference, as Win64 does).
    /// </summary>
    sealed record Kind(string Name, int Words32, string? Through = null, bool Reference = false, string? Iid = null, string? Enum = null)
    {
        public bool IsInterface => Name is "Interface";
    }

    static Kind? KindOf(Pass pass, TypeRef r, string? marshalAs, TypeDecl at, out string? why)
    {
        why = null;
        if (r.ArrayRank > 0) return marshalAs is null or "SafeArray" ? new Kind("SafeArray", 1, Reference: true) : Unsupported(out why, "an array marshalled as " + marshalAs);
        if (r.PointerDepth > 0) return new Kind("Ptr", 1);
        if (r.IsFunctionPointer) return new Kind("Ptr", 1);
        string n = r.Name.StartsWith("System.", StringComparison.Ordinal) ? r.Name["System.".Length..] : r.Name;
        switch (n)
        {
            case "bool": case "Boolean":
                return marshalAs switch { "Bool" => new Kind("Bool", 1), "I1" or "U1" => new Kind("Bool1", 1), _ => new Kind("VBool", 1) };
            case "sbyte": case "SByte": return new Kind("I1", 1);
            case "byte": case "Byte": return new Kind("U1", 1);
            case "short": case "Int16": return new Kind("I2", 1);
            case "ushort": case "UInt16": return new Kind("U2", 1);
            case "int": case "Int32": return marshalAs == "Error" ? new Kind("I4", 1) : new Kind("I4", 1);
            case "uint": case "UInt32": return new Kind("U4", 1);
            case "long": case "Int64": return new Kind("I8", 2);
            case "ulong": case "UInt64": return new Kind("U8", 2);
            case "float": case "Single": return new Kind("R4", 1);
            case "double": case "Double": return new Kind("R8", 2);
            case "char": case "Char": return new Kind("Char", 1);
            case "nint": case "IntPtr": return new Kind("Ptr", 1);
            case "nuint": case "UIntPtr": return new Kind("Ptr", 1, Through: "nint");
            case "string": case "String":
                return marshalAs switch
                {
                    "LPWStr" => new Kind("LpwStr", 1, Reference: true),
                    "LPStr" or "LPUTF8Str" or "LPTStr" => new Kind("LpStr", 1, Reference: true),
                    _ => new Kind("Bstr", 1, Reference: true),
                };
            case "object": case "Object":
                return marshalAs switch
                {
                    "IUnknown" or "Interface" => new Kind("Unknown", 1, Reference: true),
                    "IDispatch" => new Kind("Dispatch", 1, Reference: true),
                    _ => new Kind("Variant", 4, Reference: true),
                };
            case "Guid": return new Kind("Guid", 4);
            case "void": return new Kind("Void", 0);
        }
        TypeDecl? d = pass.Resolve(r, at);
        if (d is null) return Unsupported(out why, $"'{r}', which is not a type COM can marshal");
        switch (d.Kind)
        {
            case TypeKind.Enum:
            {
                bool wide = d.Bases.Any(b => b.Name is "long" or "ulong" or "Int64" or "UInt64");
                return new Kind(wide ? "I8" : "I4", wide ? 2 : 1, Through: wide ? "long" : "int", Enum: r.Name);
            }
            case TypeKind.Interface:
                return new Kind("Interface", 1, Reference: true, Iid: Iid(d));
            case TypeKind.Class when !d.IsDelegate:
                return new Kind(marshalAs == "IDispatch" ? "Dispatch" : "Unknown", 1, Reference: true);
        }
        return Unsupported(out why, $"'{r}' (a struct or a delegate)");
    }

    static Kind? Unsupported(out string why, string what)
    {
        why = what;
        return null;
    }

    static string? ReturnMarshal(MethodDecl m)
        => m.Attributes.FirstOrDefault(a => a.Target == "return" && a.Is("MarshalAs"))?.Argument;

    static bool PreserveSig(MemberDecl m) => m.Attributes.Any(a => a.Target.Length == 0 && a.Is("PreserveSig"));

    static int? DispId(MemberDecl m)
    {
        AttributeRef? a = m.Attributes.FirstOrDefault(x => x.Target.Length == 0 && x.Is("DispId"));
        if (a is null) return null;
        string? word = a.Arguments.FirstOrDefault()?.Words.LastOrDefault() ?? a.Argument;
        if (word is null) return null;
        bool negative = a.Arguments.Count > 0 && a.Arguments[0].Value == "-";
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            long hex = 0;
            foreach (char ch in word[2..])
            {
                int digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
                if (digit < 0) return null;
                hex = hex * 16 + digit;
            }
            return unchecked((int)hex);
        }
        if (int.TryParse(word, out int value)) return negative ? -value : value;
        return null;
    }

    /// <summary>A type as written, without its '?': what typeof and a cast to a non-null value take.</summary>
    static string Plain(TypeRef r)
    {
        StringBuilder s = new(r.Name);
        if (r.Args.Count > 0) s.Append('<').Append(string.Join(", ", r.Args.Select(Plain))).Append('>');
        for (int i = 0; i < r.PointerDepth; i++) s.Append('*');
        for (int i = 0; i < r.ArrayRank; i++) s.Append("[]");
        return s.ToString();
    }

    static bool IsVoid(TypeRef? r) => r is null || r.Name == "void";

    // ---- one interface's members, as slots -----------------------------------------------

    /// <summary>A vtable entry: a method, or a property's accessor.</summary>
    sealed class Slot
    {
        public required MemberDecl Member;
        public required string Name;            // the method's, or the property's
        public required TypeRef? Returns;       // null: void
        public required List<Param> Params;
        public string? ReturnMarshal;
        public bool PreserveSig;
        public int Accessor;                    // 0 a method, 1 get, 2 set
        public int? DispId;
    }

    static List<Slot>? Slots(Pass pass, TypeDecl i)
    {
        List<Slot> slots = new();
        foreach (MemberDecl m in i.Members)
        {
            if (m.Mods.HasFlag(Mods.Static)) continue;
            switch (m)
            {
                case MethodDecl md when md.Body is null || i.Kind == TypeKind.Interface:
                    if (md.TypeParams.Count > 0) { pass.Error(md, $"'{i.Name}.{md.Name}': a generic method cannot be a COM interface's"); return null; }
                    slots.Add(new Slot
                    {
                        Member = md, Name = md.Name, Returns = IsVoid(md.Returns) ? null : md.Returns, Params = md.Params,
                        ReturnMarshal = ReturnMarshal(md), PreserveSig = PreserveSig(md), DispId = DispId(md),
                    });
                    break;
                case PropertyDecl p:
                    if (p.Params.Count > 0) { pass.Error(p, $"'{i.Name}': an indexer cannot be a COM interface's member"); return null; }
                    slots.Add(new Slot { Member = p, Name = p.Name, Returns = p.Type, Params = new(), Accessor = 1, PreserveSig = PreserveSig(p), DispId = DispId(p) });
                    if (p.HasSetter)
                        slots.Add(new Slot { Member = p, Name = p.Name, Returns = null, Params = new() { new Param { Name = "value", Type = p.Type, Line = p.Line, Col = p.Col } }, Accessor = 2, PreserveSig = PreserveSig(p), DispId = DispId(p) });
                    break;
            }
        }
        return slots;
    }

    /// <summary>The interface and every [ComImport] interface it extends, each once, itself first.</summary>
    static List<TypeDecl> WithBases(Pass pass, TypeDecl i)
    {
        List<TypeDecl> all = new();
        void Walk(TypeDecl at)
        {
            if (all.Contains(at)) return;
            all.Add(at);
            foreach (TypeRef b in at.Bases)
            {
                TypeDecl? up = pass.Resolve(b, at);
                if (up is null || up.Kind != TypeKind.Interface)
                {
                    pass.Error(at, $"'{at.Name}' extends '{b}', which is not a COM interface this program declares");
                    continue;
                }
                if (!IsComImport(up)) pass.Error(at, $"'{at.Name}' extends '{up.Name}', which is not [ComImport]");
                Walk(up);
            }
        }
        Walk(i);
        return all;
    }

    // ---- the runtime-callable wrapper --------------------------------------------------------

    static string? Rcw(Pass pass, TypeDecl i)
    {
        int errors = pass.Errors.Count;
        StringBuilder s = new();
        string name = RcwName(i);
        string iid = Iid(i);
        s.Append("sealed class ").Append(name).Append(" : System.__ComObject, ").Append(i.Name).Append("\n{\n");
        s.Append("    ").Append(name).Append("(System.__ComObject from, nint pointer, string iid) : base(from, pointer, iid) { }\n");
        s.Append("    public static object? ").Append(WrapMethod).Append("(object? o)\n    {\n");
        s.Append("        System.__ComObject? c = o as System.__ComObject;\n");
        s.Append("        if (c == null) return null;\n");
        s.Append("        nint p = c.__Query(\"").Append(iid).Append("\");\n");
        s.Append("        if (p == 0) return null;\n");
        s.Append("        return new ").Append(name).Append("(c, p, \"").Append(iid).Append("\");\n    }\n");

        // EVERY MEMBER OF THE INTERFACE AND OF THE [ComImport] INTERFACES IT
        // EXTENDS, each implemented once and publicly: an interface that
        // repeats its base's members, as .NET asks of a [ComImport] one, has
        // them in its own vtable after its base's three or seven slots,
        // and is called through its own pointer; one that does not has them
        // called through the base's pointer, asked for by the base's IID.
        HashSet<string> done = new(StringComparer.Ordinal);
        foreach (TypeDecl owner in WithBases(pass, i))
        {
            List<Slot>? slots = Slots(pass, owner);
            if (slots is null) continue;
            int baseSlots = BaseSlots(owner);
            string ownerIid = Iid(owner);
            for (int k = 0; k < slots.Count; k++)
            {
                Slot slot = slots[k];
                if (slot.Member is PropertyDecl p)
                {
                    if (slot.Accessor != 1 || !done.Add("P:" + p.Name)) continue;
                    Slot? setter = slots.FirstOrDefault(x => x.Member == p && x.Accessor == 2);
                    int setterIndex = setter is null ? -1 : slots.IndexOf(setter);
                    s.Append("    public ").Append(p.Type).Append(' ').Append(p.Name).Append("\n    {\n");
                    s.Append("        get\n        {\n");
                    Body(pass, s, owner, slot, baseSlots < 0 ? -1 : baseSlots + k, ownerIid, "            ");
                    s.Append("        }\n");
                    if (setter is not null)
                    {
                        s.Append("        set\n        {\n");
                        Body(pass, s, owner, setter, baseSlots < 0 ? -1 : baseSlots + setterIndex, ownerIid, "            ");
                        s.Append("        }\n");
                    }
                    s.Append("    }\n");
                    continue;
                }
                MethodDecl md = (MethodDecl)slot.Member;
                if (!done.Add("M:" + md.Name + "/" + md.Params.Count)) continue;
                s.Append("    public ").Append(IsVoid(md.Returns) ? "void" : md.Returns!.ToString()).Append(' ')
                 .Append(md.Name).Append('(').Append(ParamList(md.Params)).Append(")\n    {\n");
                Body(pass, s, owner, slot, baseSlots < 0 ? -1 : baseSlots + k, ownerIid, "        ");
                s.Append("    }\n");
            }
        }
        s.Append("}\n");
        return pass.Errors.Count > errors ? null : s.ToString();
    }

    static string ParamList(List<Param> ps)
        => string.Join(", ", ps.Select(p => (p.IsOut ? "out " : p.IsReadOnlyRef ? "in " : p.IsRef ? "ref " : "") + p.Type + " " + p.Name));

    /// <summary>One member's body in the wrapper: through the vtable at `slot`, or through IDispatch when `slot` is -1.</summary>
    static void Body(Pass pass, StringBuilder s, TypeDecl owner, Slot slot, int vtableSlot, string iid, string pad)
    {
        if (vtableSlot < 0)
        {
            DispatchBody(pass, s, owner, slot, pad);
            return;
        }
        s.Append(pad).Append(Ns).Append(".ComFrame __f = new ").Append(Ns).Append(".ComFrame(this, \"").Append(iid).Append("\");\n");
        s.Append(pad).Append("try\n").Append(pad).Append("{\n");
        string inner = pad + "    ";
        List<(Param P, Kind K)> back = new();
        foreach (Param p in slot.Params)
        {
            Kind? k = KindOf(pass, p.Type, p.MarshalAs, owner, out string? why);
            if (k is null) { pass.Error(p, $"'{owner.Name}.{slot.Name}': the parameter '{p.Name}' is {why}"); return; }
            string value = k.Enum is not null ? "(" + k.Through + ")" + p.Name : k.Through is not null ? "(" + k.Through + ")" + p.Name : p.Name;
            string extra = k.IsInterface ? ", \"" + k.Iid + "\"" : "";
            if (p.IsOut)
            {
                s.Append(inner).Append("nint __p_").Append(p.Name).Append(" = __f.Out(\"").Append(k.Name).Append('"').Append(extra).Append(");\n");
                back.Add((p, k));
            }
            else if (p.IsRef && !p.IsReadOnlyRef)
            {
                s.Append(inner).Append("nint __p_").Append(p.Name).Append(" = __f.Ref").Append(k.Name).Append('(').Append(value).Append(extra).Append(");\n");
                back.Add((p, k));
            }
            else
            {
                s.Append(inner).Append("__f.In").Append(k.Name).Append('(').Append(value).Append(extra).Append(");\n");
            }
        }
        Kind? result = null;
        if (slot.Returns is not null)
        {
            result = KindOf(pass, slot.Returns, slot.ReturnMarshal, owner, out string? why);
            if (result is null) { pass.Error(slot.Member, $"'{owner.Name}.{slot.Name}': its result is {why}"); return; }
        }
        if (slot.PreserveSig)
        {
            string call = result is null ? "__f.Call(" + vtableSlot + ")"
                : result.Name switch
                {
                    "I8" or "U8" => "__f.CallLong(" + vtableSlot + ")",
                    "R8" => "__f.CallDouble(" + vtableSlot + ")",
                    "R4" => "__f.CallFloat(" + vtableSlot + ")",
                    "Ptr" => "__f.CallPtr(" + vtableSlot + ")",
                    "VBool" or "Bool" or "Bool1" => "__f.Call(" + vtableSlot + ") != 0",
                    "I1" or "U1" or "I2" or "U2" or "I4" or "U4" or "Char" => "__f.Call(" + vtableSlot + ")",
                    _ => "",
                };
            if (call.Length == 0) { pass.Error(slot.Member, $"'{owner.Name}.{slot.Name}': a [PreserveSig] member returns a number, a bool or a pointer"); return; }
            s.Append(inner).Append("__f.PreserveSig();\n");
            s.Append(inner).Append(result is null ? "" : "var __r = ").Append(call).Append(";\n");
            WriteBack(s, back, inner);
            if (result is not null) s.Append(inner).Append("return (").Append(Plain(slot.Returns!)).Append(")__r;\n");
        }
        else
        {
            if (result is not null) s.Append(inner).Append("nint __ret = __f.Out(\"").Append(result.Name).Append('"').Append(Extra(result)).Append(");\n");
            s.Append(inner).Append(Ns).Append(".ComMarshal.Check(__f.Call(").Append(vtableSlot).Append("));\n");
            WriteBack(s, back, inner);
            if (result is not null) s.Append(inner).Append("return ").Append(Get(result, slot.Returns!, "__ret")).Append(";\n");
        }
        s.Append(pad).Append("}\n").Append(pad).Append("finally { __f.Done(); }\n");
    }

    static void WriteBack(StringBuilder s, List<(Param P, Kind K)> back, string pad)
    {
        foreach ((Param p, Kind k) in back)
        {
            s.Append(pad).Append(p.Name).Append(" = ").Append(Get(k, p.Type, "__p_" + p.Name)).Append(";\n");
        }
    }

    /// <summary>The managed value a slot holds, taken (its native resources freed).</summary>
    static string Get(Kind k, TypeRef type, string slot)
    {
        string got = "__f.Get" + k.Name + "(" + slot + ")";
        if (k.Enum is not null || k.Through is not null) return "(" + Plain(type) + ")" + got;
        if (k.Name is "Interface" or "Unknown" or "Dispatch" or "Variant" or "SafeArray") return "(" + Plain(type) + ")" + got + "!";
        if (k.Reference) return got + "!";
        return got;
    }

    /// <summary>A dispinterface's member: IDispatch::Invoke by its [DispId], or by its name.</summary>
    static void DispatchBody(Pass pass, StringBuilder s, TypeDecl owner, Slot slot, string pad)
    {
        int flags = slot.Accessor switch { 1 => 2, 2 => 4, _ => 1 };
        s.Append(pad).Append("object?[] __a = new object?[] { ");
        s.Append(string.Join(", ", slot.Params.Select(p => p.IsOut ? "null" : p.Name)));
        s.Append(" };\n");
        int byRef = 0;
        for (int k = 0; k < slot.Params.Count && k < 31; k++) if (slot.Params[k].IsOut || slot.Params[k].IsRef) byRef |= 1 << k;
        s.Append(pad).Append("object? __r = ").Append(Ns).Append(".ComDispatch.Invoke(this, ").Append(slot.DispId?.ToString() ?? "int.MinValue")
         .Append(", \"").Append(slot.Name).Append("\", ").Append(flags).Append(", __a, ").Append(byRef).Append(");\n");
        for (int k = 0; k < slot.Params.Count; k++)
        {
            Param p = slot.Params[k];
            if (!p.IsOut && !p.IsRef) continue;
            s.Append(pad).Append(p.Name).Append(" = ").Append(Coerced(pass, p.Type, owner, "__a[" + k + "]")).Append(";\n");
        }
        if (slot.Returns is not null)
            s.Append(pad).Append("return ").Append(Coerced(pass, slot.Returns, owner, "__r")).Append(";\n");
    }

    /// <summary>
    /// An object made the type a member wants (ComMarshal.Coerce): a number
    /// of another width, a string from a number and the like, as
    /// VariantChangeType makes them; an enum through its underlying type.
    /// </summary>
    static string Coerced(Pass pass, TypeRef t, TypeDecl at, string value)
    {
        Pass trial = new(pass.Unit, new List<CompileError>());
        Kind? k = KindOf(trial, t, null, at, out _);
        if (k?.Enum is not null) return "(" + Plain(t) + ")(" + k.Through + ")" + Ns + ".ComMarshal.Coerce(" + value + ", typeof(" + k.Through + "))!";
        return "(" + Plain(t) + ")" + Ns + ".ComMarshal.Coerce(" + value + ", typeof(" + Plain(t) + "))!";
    }

    // ---- the COM-callable wrapper ------------------------------------------------------------

    /// <summary>`__Ccw_C`: each interface's vtable and its entries, for 32 bits and for 64.</summary>
    static string? Ccw(Pass pass, TypeDecl c, IReadOnlyCollection<string>? symbols)
    {
        int errors = pass.Errors.Count;
        List<TypeDecl> interfaces = Interfaces(pass, c);
        StringBuilder s = new();
        string name = CcwName(c);
        s.Append("static class ").Append(name).Append("\n{\n");
        s.Append("    static ").Append(Ns).Append(".ComInterfaceEntry[]? _entries;\n");
        s.Append("    public static ").Append(Ns).Append(".ComInterfaceEntry[] Entries\n    {\n        get\n        {\n");
        s.Append("            if (_entries == null) _entries = Make();\n            return _entries;\n        }\n    }\n");
        s.Append("    static ").Append(Ns).Append(".ComInterfaceEntry[] Make()\n    {\n");
        s.Append("        ").Append(Ns).Append(".ComInterfaceEntry[] e = new ").Append(Ns).Append(".ComInterfaceEntry[").Append(interfaces.Count).Append("];\n");
        for (int n = 0; n < interfaces.Count; n++)
        {
            TypeDecl i = interfaces[n];
            s.Append("        e[").Append(n).Append("] = new ").Append(Ns).Append(".ComInterfaceEntry(\"").Append(Iid(i)).Append("\", \"")
             .Append(FullName(i)).Append("\", ").Append(BaseSlots(i)).Append(", V").Append(n).Append("(), new string[] { ")
             .Append(string.Join(", ", Signatures(pass, i).Select(x => "\"" + x + "\""))).Append(" });\n");
        }
        s.Append("        return e;\n    }\n");

        StringBuilder wide = new(), narrow = new();
        for (int n = 0; n < interfaces.Count; n++)
        {
            TypeDecl i = interfaces[n];
            List<Slot>? slots = Slots(pass, i);
            if (slots is null) continue;
            int baseSlots = BaseSlots(i);
            int first = baseSlots < 0 ? 7 : baseSlots;
            int count = baseSlots < 0 ? 7 : baseSlots + slots.Count;
            s.Append("    static nint V").Append(n).Append("()\n    {\n");
            s.Append("        nint v = ").Append(Ns).Append(".ComCallable.NewVtable(").Append(count).Append(", ").Append(first >= 7 ? "true" : "false").Append(");\n");
            if (baseSlots >= 0)
            {
                for (int k = 0; k < slots.Count; k++)
                {
                    s.Append("        ").Append(Ns).Append(".ComCallable.SetSlot(v, ").Append(baseSlots + k).Append(", (nint)&I").Append(n).Append('_').Append(k).Append(");\n");
                    foreach (bool is64 in new[] { true, false })
                    {
                        StringBuilder into = is64 ? wide : narrow;
                        Thunk(pass, into, c, i, slots[k], "I" + n + "_" + k, is64);
                    }
                }
            }
            s.Append("        return v;\n    }\n");
        }
        s.Append("#if TARGET_64BIT\n").Append(wide).Append("#else\n").Append(narrow).Append("#endif\n");
        s.Append("}\n");
        return pass.Errors.Count > errors ? null : s.ToString();
    }

    /// <summary>
    /// Each vtable entry's parameters as the exporter's stub reads them from
    /// a remote call (ComExporter): a token a parameter, its direction (i
    /// in, r in and out, o out, v the [retval]) and its kind, an
    /// interface's IID after a colon; `!` first for a [PreserveSig] one,
    /// whose native result is the call's answer.
    /// </summary>
    static List<string> Signatures(Pass pass, TypeDecl i)
    {
        List<string> made = new();
        List<Slot>? slots = Slots(new Pass(pass.Unit, new List<CompileError>()), i);
        if (slots is null) return made;
        foreach (Slot slot in slots)
        {
            Pass trial = new(pass.Unit, new List<CompileError>());
            List<string> tokens = new();
            foreach (Param p in slot.Params)
            {
                Kind? k = KindOf(trial, p.Type, p.MarshalAs, i, out _);
                string dir = p.IsOut ? "o" : p.IsRef && !p.IsReadOnlyRef ? "r" : "i";
                tokens.Add(dir + (k?.Name ?? "?") + (k?.Iid is { } iid ? ":" + iid : ""));
            }
            if (slot.Returns is not null)
            {
                Kind? k = KindOf(trial, slot.Returns, slot.ReturnMarshal, i, out _);
                tokens.Add((slot.PreserveSig ? "p" : "v") + (k?.Name ?? "?") + (k?.Iid is { } iid ? ":" + iid : ""));
            }
            made.Add((slot.PreserveSig ? "!" : "") + string.Join(",", tokens));
        }
        return made;
    }

    /// <summary>
    /// One vtable entry: called by native code with stdcall's rule, the
    /// interface pointer first, every argument a stack word (two for a
    /// 64-bit number and four for a VARIANT or a GUID by value on i386; one
    /// on x86-64). The object is found from the pointer, the arguments
    /// unmarshalled, the member called through the interface, the results
    /// written where the caller's pointers say, and an exception turned
    /// into its HRESULT.
    /// </summary>
    static void Thunk(Pass pass, StringBuilder s, TypeDecl c, TypeDecl i, Slot slot, string name, bool is64)
    {
        List<string> words = new() { "nint __self" };
        List<string> before = new(), args = new(), after = new();
        int w = 0;
        string Word() => "__w" + (w++);
        foreach (Param p in slot.Params)
        {
            Kind? k = KindOf(pass, p.Type, p.MarshalAs, i, out string? why);
            if (k is null) { pass.Error(p, $"'{i.Name}.{slot.Name}': the parameter '{p.Name}' is {why}"); return; }
            string local = "__a_" + p.Name;
            if (p.IsOut || (p.IsRef && !p.IsReadOnlyRef))
            {
                string at = Word();
                words.Add("nint " + at);
                string read = Read(k, p.Type, at);
                before.Add((p.IsOut ? Plain(p.Type) + " " + local + ";" : Plain(p.Type) + " " + local + " = " + read + ";"));
                args.Add((p.IsOut ? "out " : "ref ") + local);
                after.Add(Ns + ".ComMarshal." + (p.IsOut ? "Write" : "Replace") + k.Name + "(" + at + ", " + Value(k, local) + Extra(k) + ");");
                continue;
            }
            int n = is64 ? 1 : k.Words32;
            List<string> mine = new();
            for (int x = 0; x < n; x++) { string one = Word(); words.Add("nint " + one); mine.Add(one); }
            string arg = Ns + ".ComMarshal.Arg" + k.Name + "(" + string.Join(", ", mine) + ")";
            if (k.Enum is not null || k.Through is not null) arg = "(" + Plain(p.Type) + ")" + arg;
            else if (k.Name is "Interface" or "Unknown" or "Dispatch" or "Variant" or "SafeArray") arg = "(" + Plain(p.Type) + ")" + arg + "!";
            else if (k.Reference) arg += "!";
            before.Add(Plain(p.Type) + " " + local + " = " + arg + ";");
            args.Add(local);
        }
        Kind? result = null;
        if (slot.Returns is not null)
        {
            result = KindOf(pass, slot.Returns, slot.ReturnMarshal, i, out string? why);
            if (result is null) { pass.Error(slot.Member, $"'{i.Name}.{slot.Name}': its result is {why}"); return; }
        }
        string? retWord = null;
        if (result is not null && !slot.PreserveSig) { retWord = Word(); words.Add("nint " + retWord); }

        string target = "((" + Qualified(i) + ")__o)";
        string call = slot.Accessor switch
        {
            1 => target + "." + slot.Name,
            2 => target + "." + slot.Name + " = " + args[0],
            _ => target + "." + slot.Name + "(" + string.Join(", ", args) + ")",
        };
        string nativeReturn = "int";
        if (slot.PreserveSig && result is not null)
            nativeReturn = result.Name switch { "I8" or "U8" => "long", "R8" => "double", "R4" => "float", "Ptr" => "nint", _ => "int" };

        s.Append("    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]\n");
        s.Append("    static ").Append(nativeReturn).Append(' ').Append(name).Append('(').Append(string.Join(", ", words)).Append(")\n    {\n");
        s.Append("        try\n        {\n");
        s.Append("            ").Append(Qualified(c)).Append(" __o = (").Append(Qualified(c)).Append(')').Append(Ns).Append(".ComCallable.Target(__self);\n");
        foreach (string b in before) s.Append("            ").Append(b).Append('\n');
        if (result is null) s.Append("            ").Append(call).Append(";\n");
        else s.Append("            ").Append(Plain(slot.Returns!)).Append(" __r = ").Append(call).Append(";\n");
        foreach (string a in after) s.Append("            ").Append(a).Append('\n');
        if (slot.PreserveSig)
        {
            if (result is null) s.Append("            return 0;\n");
            else if (result.Name is "VBool" or "Bool" or "Bool1") s.Append("            return __r ? 1 : 0;\n");
            else s.Append("            return (").Append(nativeReturn).Append(")__r;\n");
        }
        else
        {
            if (retWord is not null) s.Append("            ").Append(Ns).Append(".ComMarshal.Write").Append(result!.Name).Append('(').Append(retWord)
                .Append(", ").Append(Value(result, "__r")).Append(Extra(result)).Append(");\n");
            s.Append("            return 0;\n");
        }
        s.Append("        }\n        catch (System.Exception __e)\n        {\n");
        s.Append("            return (").Append(nativeReturn).Append(')').Append(Ns).Append(".ComMarshal.HResultOf(__e);\n");
        s.Append("        }\n    }\n");
    }

    static string Read(Kind k, TypeRef type, string at)
    {
        string got = Ns + ".ComMarshal.Read" + k.Name + "(" + at + ")";
        if (k.Enum is not null || k.Through is not null) return "(" + Plain(type) + ")" + got;
        if (k.Name is "Interface" or "Unknown" or "Dispatch" or "Variant" or "SafeArray") return "(" + Plain(type) + ")" + got + "!";
        if (k.Reference) return got + "!";
        return got;
    }

    static string Value(Kind k, string local) => k.Enum is not null || k.Through is not null ? "(" + k.Through + ")" + local : local;

    static string Extra(Kind k) => k.IsInterface ? ", \"" + k.Iid + "\"" : "";

    // ---- what a COM-callable class gains ---------------------------------------------------------

    /// <summary>
    /// The class made IComCallable: its interfaces' entries, and its
    /// IDispatch -- a number for each public method and property by name
    /// (and its interfaces' [DispId] members by theirs), and the call of
    /// each by number with VARIANT arguments already made objects.
    /// </summary>
    static void AddCallableMembers(Pass pass, TypeDecl c, bool inherits, IReadOnlyCollection<string>? symbols, List<CompileError> errors)
    {
        // Virtual where a subclass may be COM-callable too and override
        // them with its own interfaces and members; a sealed class's are
        // plain.
        string modifier = inherits ? "override" : c.Mods.HasFlag(Mods.Sealed) ? "" : "virtual";
        if (!inherits) c.WritableBases.Add(new TypeRef { Name = Ns + ".IComCallable", Line = c.Line, Col = c.Col });

        // The members, by name: the class's own and its bases' public ones,
        // then its COM interfaces' [DispId] ones under their numbers.
        List<(string Name, int Id, List<MemberDecl> Members, TypeDecl? Through)> table = new();
        int next = 0x10000;
        List<TypeDecl> chain = new[] { c }.Concat(Ancestors(pass, c)).ToList();
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
        foreach (TypeDecl i in Interfaces(pass, c))
        {
            foreach (MemberDecl m in i.Members)
            {
                if (m is not (MethodDecl or PropertyDecl) || DispId(m) is not int id) continue;
                if (m is MethodDecl md && md.TypeParams.Count > 0) continue;
                if (m is PropertyDecl ip && ip.Params.Count > 0) continue;
                string memberName = m is MethodDecl mm ? mm.Name : ((PropertyDecl)m).Name;
                if (!named.Add(memberName)) continue;
                table.Add((memberName, id, new List<MemberDecl> { m }, i));
            }
        }
        foreach (TypeDecl at in chain)
        {
            foreach (MemberDecl m in at.Members)
            {
                if (!m.Mods.HasFlag(Mods.Public) || m.Mods.HasFlag(Mods.Static)) continue;
                string? memberName = m switch
                {
                    MethodDecl md when !md.IsCtor && md.TypeParams.Count == 0 && !md.Name.StartsWith("__", StringComparison.Ordinal)
                        && !md.Name.StartsWith("op_", StringComparison.Ordinal) && md.Name != at.Name => md.Name,
                    PropertyDecl p when p.Params.Count == 0 => p.Name,
                    _ => null,
                };
                if (memberName is null) continue;
                int found = table.FindIndex(x => string.Equals(x.Name, memberName, StringComparison.OrdinalIgnoreCase) && x.Through is null);
                if (found >= 0) { table[found].Members.Add(m); continue; }
                if (!named.Add(memberName)) continue;
                table.Add((memberName, next++, new List<MemberDecl> { m }, null));
            }
        }

        StringBuilder s = new();
        s.Append("class __Members\n{\n");
        s.Append("    public ").Append(modifier).Append(' ').Append(Ns).Append(".ComInterfaceEntry[] __ComInterfaces() { return ").Append(CcwName(c)).Append(".Entries; }\n");
        s.Append("    public ").Append(modifier).Append(" int __ComDispId(string name)\n    {\n");
        s.Append("        switch (name.ToUpperInvariant())\n        {\n");
        foreach (var e in table) s.Append("            case \"").Append(e.Name.ToUpperInvariant()).Append("\": return ").Append(e.Id).Append(";\n");
        s.Append("        }\n");
        s.Append(inherits ? "        return base.__ComDispId(name);\n" : "        return -1;\n");
        s.Append("    }\n");
        s.Append("    public ").Append(modifier).Append(" int __ComInvoke(int dispid, int flags, object?[] args, out object? result)\n    {\n");
        s.Append("        result = null;\n        switch (dispid)\n        {\n");
        foreach (var e in table)
        {
            s.Append("            case ").Append(e.Id).Append(":\n            {\n");
            foreach (MemberDecl m in e.Members) DispatchCase(pass, s, c, m, e.Through);
            s.Append("                return unchecked((int)0x8002000E);\n            }\n");
        }
        s.Append("        }\n");
        s.Append(inherits ? "        return base.__ComInvoke(dispid, flags, args, out result);\n" : "        return unchecked((int)0x80020003);\n");
        s.Append("    }\n}\n");

        CompilationUnit parsed;
        try { parsed = Parser.ParseText(s.ToString(), "<com:" + c.Name + ">", symbols); }
        catch (CompileError e) { errors.Add(e); return; }
        foreach (MemberDecl m in parsed.Types[0].Members)
        {
            m.Scope = c.Scope;
            m.Namespace = c.Namespace;
            m.File = c.File;
            if (c.Elsewhere) m.OwnedImplementation = false;
            c.Members.Add(m);
        }
    }

    /// <summary>One member's call by IDispatch: chosen by the kind of call and the argument count.</summary>
    static void DispatchCase(Pass pass, StringBuilder s, TypeDecl c, MemberDecl m, TypeDecl? through)
    {
        const string pad = "                ";
        string target = through is null ? "this" : "((" + Qualified(through) + ")this)";
        string Coerce(TypeRef t, string value) => Coerced(pass, t, through ?? c, value);
        if (m is PropertyDecl p)
        {
            s.Append(pad).Append("if ((flags & 2) != 0 && args.Length == 0) { result = ").Append(target).Append('.').Append(p.Name).Append("; return 0; }\n");
            if (p.HasSetter)
                s.Append(pad).Append("if ((flags & 12) != 0 && args.Length == 1) { ").Append(target).Append('.').Append(p.Name).Append(" = ")
                 .Append(Coerce(p.Type, "args[0]")).Append("; return 0; }\n");
            return;
        }
        MethodDecl md = (MethodDecl)m;
        s.Append(pad).Append("if ((flags & 3) != 0 && args.Length == ").Append(md.Params.Count).Append(")\n").Append(pad).Append("{\n");
        List<string> call = new();
        for (int k = 0; k < md.Params.Count; k++)
        {
            Param prm = md.Params[k];
            string local = "__a" + k;
            if (prm.IsOut) s.Append(pad).Append("    ").Append(Plain(prm.Type)).Append(' ').Append(local).Append(";\n");
            else s.Append(pad).Append("    ").Append(Plain(prm.Type)).Append(' ').Append(local).Append(" = ").Append(Coerce(prm.Type, "args[" + k + "]")).Append(";\n");
            call.Add((prm.IsOut ? "out " : prm.IsRef && !prm.IsReadOnlyRef ? "ref " : "") + local);
        }
        string invocation = target + "." + md.Name + "(" + string.Join(", ", call) + ")";
        if (IsVoid(md.Returns)) s.Append(pad).Append("    ").Append(invocation).Append(";\n");
        else s.Append(pad).Append("    result = ").Append(invocation).Append(";\n");
        for (int k = 0; k < md.Params.Count; k++)
            if (md.Params[k].IsOut || (md.Params[k].IsRef && !md.Params[k].IsReadOnlyRef))
                s.Append(pad).Append("    args[").Append(k).Append("] = __a").Append(k).Append(";\n");
        s.Append(pad).Append("    return 0;\n").Append(pad).Append("}\n");
    }
}
