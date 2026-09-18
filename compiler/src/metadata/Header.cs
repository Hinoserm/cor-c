#nullable enable
using System.Text;

namespace Corsac.Lang;

/// <summary>
/// A library's interface, written out as source.
///
/// This is the header file, and it is GENERATED rather than maintained. A
/// header somebody keeps in step with the code by hand is a header that drifts
/// from it, and the drift is silent until a caller uses a signature that moved.
/// Produced from the parse tree, it cannot disagree with what was compiled.
///
/// It is also carried INSIDE the library image, so asking a library what it
/// offers means reading the library. A copy is written beside it for people,
/// but nothing in the toolchain consults that copy.
///
/// What it contains is every declaration and no bodies. A method becomes its
/// signature and a semicolon, which the parser already accepts -- see
/// FinishMethod, where "'{', '=>' or ';' after the signature" has always been
/// the rule. Nothing new was needed in the grammar to have headers, which is a
/// good sign the grammar was right.
/// </summary>
public static class Header
{
    /// <summary>Renders a whole compilation as declarations.</summary>
    ///
    /// GENERIC TEMPLATES ARE NOT IN HERE, and briefly were.
    ///
    /// A caller has to COMPILE a template with its own type arguments, so a
    /// signature tells it nothing usable -- and the first fix for that was to
    /// copy the template into this header as source, sliced out of the file the
    /// parser read. It worked, and it made a COMPILED library depend on the
    /// compiler's SOURCE LANGUAGE for ever after: the day the grammar moved,
    /// every binary library on the disc would stop being instantiable, with
    /// nothing to say so until somebody tried.
    ///
    /// They travel as a TREE now, in the image's GIR section, behind a version
    /// number that is refused rather than guessed at. See Lang/Gir.cs.
    /// <param name="all">
    /// Every type the compilation produced, INCLUDING the specialisations that
    /// are deliberately kept out of the header. They are not written, but they
    /// are what a mangled name in a signature has to be looked up in to be
    /// written back as the application it came from. Null means there were
    /// none to look up.
    /// </param>
    public static string Of(CompilationUnit unit, string from,
                            CompilationUnit? all = null,
                            BindResult? bound = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        StringBuilder s = new();

        s.Append("// The interface of ").Append(from).Append(", generated.\n")
         .Append("//\n")
         .Append("// Do not edit: this is written out by `corsac lib` from the source it\n")
         .Append("// compiled, and the copy that matters lives inside the library image.\n\n");

        // WHAT EACH SPECIALISATION WAS MADE FROM.
        //
        // A header names types, and by the time one is written the generics are
        // gone: `IEqualityComparer<string>` has become a type CALLED
        // `IEqualityComparer$string`, whose name means nothing to whoever reads
        // this header -- only the TEMPLATE travels, and the consumer makes its
        // own copies. So a specialisation is written back out as the
        // application it is, which the consumer can read and instantiate.
        //
        // Without this, a library could not have a specialised generic anywhere
        // in its public surface: `class StringComparer : IEqualityComparer<string>`
        // produced a header naming a type nobody could resolve, and every
        // program that linked the library failed to build.
        Dictionary<string, TypeDecl> made = new(StringComparer.Ordinal);

        foreach (TypeDecl t in (all ?? unit).Types.Where(t => t.Template != null))
        {
            made[t.Name] = t;
        }

        // The binder is the authority for constant values.  Re-evaluating a
        // declaration here used to support only a literal and unary minus, so
        // valid C# such as `const uint Mask = (uint)3 | High;` compiled into a
        // library but lost its value in the generated header.  A consumer then
        // saw a valueless const and rejected the library.  Match declarations
        // to their bound symbols once and publish the exact value the library
        // itself compiled.
        Dictionary<TypeDecl, TypeSymbol> symbols
            = new(ReferenceEqualityComparer.Instance);
        if (bound != null)
        {
            foreach (TypeSymbol symbol in bound.Types.Values)
            {
                if (symbol.Decl != null)
                {
                    symbols.TryAdd(symbol.Decl, symbol);
                }
            }
        }

        foreach (TypeDecl t in unit.Types)
        {
            symbols.TryGetValue(t, out TypeSymbol? owner);
            Write(s, t, made, owner, bound);
        }

        return s.ToString();
    }

    /// <summary>
    /// A type as a reader of this header can understand it: a specialisation
    /// written back out as the template application it was made from, and
    /// anything else exactly as it stands.
    ///
    /// Recursive, because the arguments may be specialisations too --
    /// `List&lt;KeyValuePair&lt;string, int&gt;&gt;`.
    /// </summary>
    private static string Spell(TypeRef? r, IReadOnlyDictionary<string, TypeDecl> made)
    {
        if (r is null)
        {
            return "void";
        }

        if (!made.TryGetValue(r.Name, out TypeDecl? from) || from.Template is null)
        {
            // Its own arguments may still need spelling: an OPEN application
            // like `IEnumerable<T>` keeps them.
            if (r.Args.Count == 0)
            {
                return r.ToString();
            }

            return Suffix(r, r.Name + "<" + string.Join(", ", r.Args.Select(a => Spell(a, made))) + ">");
        }

        return Suffix(r, from.Template + "<"
                       + string.Join(", ", from.TemplateArgs.Select(a => Spell(a, made))) + ">");
    }

    /// The stars, question mark and brackets a reference carries, put back on.
    private static string Suffix(TypeRef r, string name)
    {
        StringBuilder s = new(name);

        for (int i = 0; i < r.PointerDepth; i++)
        {
            s.Append('*');
        }

        if (r.Nullable)
        {
            s.Append('?');
        }

        for (int i = 0; i < r.ArrayRank; i++)
        {
            s.Append("[]");
        }
        return s.ToString();
    }

    private static void Write(StringBuilder s, TypeDecl t,
                              IReadOnlyDictionary<string, TypeDecl> made,
                              TypeSymbol? owner, BindResult? bound)
    {
        // `[Flags]` MEANS SOMETHING, so a header that drops it changes what
        // the consuming module prints for an enum. The rest are still
        // metadata nothing here reads.
        if (t.Attributes.Contains("Flags") || t.Attributes.Contains("FlagsAttribute"))
        {
            s.Append("[Flags]\n");
        }

        s.Append(Modifiers(t.Mods)).Append(Keyword(t.Kind)).Append(' ').Append(t.Name);
        TypeParams(s, t.TypeParams);

        if (t.Bases.Count > 0)
        {
            s.Append(" : ").Append(string.Join(", ", t.Bases.Select(b => Spell(b, made))));
        }

        s.Append("\n{\n");

        if (t.Kind == TypeKind.Enum)
        {
            foreach (EnumMember m in t.EnumMembers)
            {
                s.Append("    ").Append(m.Name);

                if (owner != null
                    && owner.EnumValues.TryGetValue(m.Name, out long value))
                {
                    s.Append(" = ").Append(value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (m.Value != null && Constant(m.Value) is string v)
                {
                    s.Append(" = ").Append(v);
                }
                s.Append(",\n");
            }
        }

        foreach (MemberDecl m in t.Members)
        {
            Member(s, m, made, t.Kind == TypeKind.Interface, owner, bound);
        }

        s.Append("}\n\n");
    }

    private static void Member(StringBuilder s, MemberDecl m, IReadOnlyDictionary<string, TypeDecl> made,
                               bool onInterface, TypeSymbol? owner,
                               BindResult? bound)
    {
        switch (m)
        {
            case MethodDecl d:
                s.Append("    ").Append(Modifiers(d.Mods));

                // A constructor has no return type and keeps its name.
                if (!d.IsCtor)
                {
                    s.Append(Spell(d.Returns, made)).Append(' ');
                }

                s.Append(d.Name);
                TypeParams(s, d.TypeParams);
                s.Append('(');
                s.Append(string.Join(", ", d.Params.Select(p => Parameter(p, made))));
                s.Append(");\n");
                break;

            case PropertyDecl d:
                s.Append("    ").Append(Modifiers(d.Mods)).Append(Spell(d.Type, made)).Append(' ').Append(d.Name);

                // A property whose getter or setter has a BODY is represented
                // by real `get_Name` / `set_Name` methods in the library.  A
                // header cannot preserve that body, but it must preserve the
                // fact that this is an accessor rather than an auto-property:
                // `{ get; }` is parsed as storage and a consumer then loads an
                // invented static field instead of importing get_Name.  That
                // made StringComparer.OrdinalIgnoreCase load address 56 as an
                // object and branch through a zero vtable slot.
                //
                // The typed dummy getter is never emitted for an external
                // declaration; it exists solely to make the reader synthesise
                // the accessor signature.  Interface properties stay bodyless
                // because they are genuinely abstract declarations.
                if (!onInterface && !d.Auto)
                {
                    s.Append(" { get { return default(")
                     .Append(Spell(d.Type, made)).Append(")!; }");
                    if (d.HasSetter || d.Setter != null)
                    {
                        s.Append(" set { }");
                    }
                    s.Append(" }\n");
                }
                else
                {
                    s.Append(d.HasSetter || d.Setter != null ? " { get; set; }\n" : " { get; }\n");
                }
                break;

            case FieldDecl d:
                s.Append("    ").Append(Modifiers(d.Mods)).Append(Spell(d.Type, made)).Append(' ').Append(d.Name);

                // A CONST KEEPS ITS VALUE, because a const is not storage: every
                // use of one is the number written out where the name was, so a
                // caller that cannot see the number cannot use the name. A field
                // is storage and its initialiser is the library's business.
                if ((d.Mods & Mods.Const) != 0
                    && owner != null && bound != null
                    && bound.Constants.TryGetValue((owner, d.Name),
                                                   out long value))
                {
                    s.Append(" = ").Append(value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                else if ((d.Mods & Mods.Const) != 0
                    && owner != null && bound != null
                    && bound.TextConstants.TryGetValue((owner, d.Name),
                                                       out string? text))
                {
                    s.Append(" = ").Append(Quoted(text));
                }
                else if ((d.Mods & Mods.Const) != 0
                    && Constant(d.Init) is string v)
                {
                    s.Append(" = ").Append(v);
                }

                s.Append(";\n");
                break;
        }
    }

    /// <summary>A parameter exactly as a consumer must call it.</summary>
    ///
    /// <c>ref</c> and <c>out</c> are ABI, not decoration: the caller passes an
    /// address while an ordinary parameter passes a value. Dropping the word
    /// from a compiled library's generated header made a valid call fail to
    /// bind and, if accepted, would have made the callee treat a value as an
    /// address.
    private static string Parameter(Param p, IReadOnlyDictionary<string, TypeDecl> made)
    {
        // `this` is just as much part of a callable signature as `ref` and
        // `out`: dropping it turns an extension method in a compiled library
        // into an ordinary static method for every consuming compilation.
        string by = p.IsThis ? "this " : p.IsParams ? "params " : p.IsOut ? "out " : p.IsRef ? "ref " : "";

        return by + Spell(p.Type, made) + " " + p.Name;
    }

    private static void TypeParams(StringBuilder s, List<TypeParam> ps)
    {
        if (ps.Count > 0)
        {
            s.Append('<').Append(string.Join(", ", ps.Select(p => p.Name))).Append('>');
        }
    }

    /// <summary>
    /// A constant's value as source, or null when it is not one this can write.
    ///
    /// Whole numbers and their negations, which is what a const in this language
    /// is allowed to be. Anything else returns null and the const is left out of
    /// the header rather than written wrongly -- a caller then fails to compile
    /// against a name it cannot see, which is a better failure than compiling
    /// against a number that is not the one the library uses.
    /// </summary>
    private static string? Constant(Expr? e)
    {
        if (e is LiteralExpr { Kind: Lit.Str } text)
        {
            return Quoted(text.Text);
        }
        return e != null && Fold.TryConst(e, out long value)
            ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static string Quoted(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Keyword(TypeKind k) => k switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct    => "struct",
        TypeKind.Enum      => "enum",
        _                  => "class",
    };

    private static string Modifiers(Mods m)
    {
        StringBuilder s = new();

        if ((m & Mods.Public) != 0)    { s.Append("public "); }
        if ((m & Mods.Private) != 0)   { s.Append("private "); }
        if ((m & Mods.Protected) != 0) { s.Append("protected "); }
        if ((m & Mods.Internal) != 0)  { s.Append("internal "); }
        if ((m & Mods.Static) != 0)    { s.Append("static "); }
        if ((m & Mods.Abstract) != 0)  { s.Append("abstract "); }
        if ((m & Mods.Virtual) != 0)   { s.Append("virtual "); }
        if ((m & Mods.Override) != 0)  { s.Append("override "); }
        if ((m & Mods.Sealed) != 0)    { s.Append("sealed "); }
        if ((m & Mods.Readonly) != 0)  { s.Append("readonly "); }
        if ((m & Mods.Volatile) != 0)  { s.Append("volatile "); }
        if ((m & Mods.Const) != 0)     { s.Append("const "); }
        if ((m & Mods.Partial) != 0)   { s.Append("partial "); }

        return s.ToString();
    }
}
