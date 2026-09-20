using Corsac.Lang.Metadata;

namespace Corsac.Lang;

/// <summary>
/// Turns `[Registry("CORSAC.Paint")]` and the nested static classes under it
/// into the schema the kernel reads.
///
/// docs/software/REGISTRY.md in the OS repository is the design. The rules
/// this enforces, in one place:
///
///   - the attribute's name is the source of truth: the mount path and the
///     filename are both derived from it, so they cannot disagree
///   - everything in the namespace is lowercase, folded once here
///   - nested static classes are path segments, the field name is the key
///   - the field type is the setting's type and the initialiser its default
///   - a choice is an enum, never a list of strings in an attribute
/// </summary>
public static class RegistryDeclarations
{
    const int MaxDomainSegments = 4;
    const int MaxSegmentChars = 32;

    /// <summary>
    /// Read from the DECLARATIONS and from nothing else: a class that only
    /// holds settings is never used as a type, so it is never materialised
    /// as a symbol, and looking for it among the symbols finds nothing.
    /// Working from the declarations also means this can run before binding,
    /// which is what turning a declared member into a property will need.
    /// </summary>
    public static List<RegistrySchema> Collect(CompilationUnit unit, List<CompileError> errors)
    {
        List<TypeDecl> declarations = unit.Types
            .Where(decl => decl.AttributeParts.Any(a => a.Is("Registry")))
            .ToList();

        if (declarations.Count == 0)
        {
            return new List<RegistrySchema>();
        }

        // EXACTLY ONE CLASS IN THE LINK NAMES THE DOMAIN. A bare [Registry]
        // inherits it, which is how a library joins in: a shared toolkit
        // reading window geometry cannot hardcode a name, because the domain
        // belongs to whichever program links it.
        List<(TypeDecl Decl, string Name)> named = new();

        foreach (TypeDecl decl in declarations)
        {
            foreach (AttributeRef attribute in decl.AttributeParts.Where(a => a.Is("Registry")))
            {
                if (attribute.Argument is string argument && argument.Length > 0)
                {
                    named.Add((decl, argument));
                }
            }
        }

        Dictionary<string, RegistrySchema> byDomain = new(StringComparer.Ordinal);
        List<string> order = new();

        foreach ((TypeDecl decl, string name) in named)
        {
            string? domain = Domain(name, decl, errors);

            if (domain is null || byDomain.ContainsKey(domain))
            {
                continue;
            }

            byDomain[domain] = new RegistrySchema { Domain = domain };
            order.Add(domain);
        }

        if (order.Count == 0)
        {
            foreach (TypeDecl decl in declarations)
            {
                errors.Add(Error(decl, "no [Registry(\"Maker.App\")] in this program names a domain, "
                                     + "so a bare [Registry] has nothing to inherit"));
            }
            return new List<RegistrySchema>();
        }

        // A bare [Registry] joins the one domain the link declares. More
        // than one, and it cannot know which, so it has to say.
        foreach (TypeDecl decl in declarations)
        {
            bool bare = decl.AttributeParts.Any(a => a.Is("Registry") && string.IsNullOrEmpty(a.Argument));

            if (bare && order.Count > 1)
            {
                errors.Add(Error(decl, "this program declares more than one registry domain, "
                                     + "so a bare [Registry] cannot tell which one it joins"));
                continue;
            }

            foreach (AttributeRef attribute in decl.AttributeParts.Where(a => a.Is("Registry")))
            {
                string? domain = string.IsNullOrEmpty(attribute.Argument)
                    ? order[0]
                    : Domain(attribute.Argument!, decl, errors);

                if (domain is not null && byDomain.TryGetValue(domain, out RegistrySchema? schema))
                {
                    Walk(decl, schema, unit, errors);
                }
            }
        }

        return order.Select(name => byDomain[name]).ToList();
    }

    /// <summary>
    /// The declared name, validated and folded. Letters, digits and hyphens
    /// per segment, at most four segments, a cap per segment.
    /// </summary>
    static string? Domain(string name, TypeDecl where, List<CompileError> errors)
    {
        string[] segments = name.Split('.');

        if (segments.Length > MaxDomainSegments)
        {
            errors.Add(Error(where, $"'{name}' has {segments.Length} segments; a registry domain has at most {MaxDomainSegments}"));
            return null;
        }

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                errors.Add(Error(where, $"'{name}' has an empty segment"));
                return null;
            }

            if (segment.Length > MaxSegmentChars)
            {
                errors.Add(Error(where, $"'{segment}' is longer than the {MaxSegmentChars} characters a domain segment may have"));
                return null;
            }

            foreach (char c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    errors.Add(Error(where, $"'{segment}' has '{c}' in it; a domain segment is letters, digits and hyphens"));
                    return null;
                }
            }
        }

        return name.ToLowerInvariant();
    }

    /// <summary>
    /// The declaring class and every static class nested under it, each level
    /// a path segment. `Settings.Canvas.Width` becomes `canvas/width`: the
    /// class carrying the attribute names the domain and contributes no
    /// segment of its own.
    /// </summary>
    static void Walk(TypeDecl root, RegistrySchema schema, CompilationUnit unit, List<CompileError> errors)
    {
        string rootKey = Key(root);

        foreach (TypeDecl decl in unit.Types)
        {
            string key = Key(decl);
            string prefix;

            if (ReferenceEquals(decl, root))
            {
                prefix = "";
            }
            else if (key.StartsWith(rootKey + ".", StringComparison.Ordinal))
            {
                prefix = key.Substring(rootKey.Length + 1).Replace('.', '/').ToLowerInvariant() + "/";
            }
            else
            {
                continue;
            }

            foreach (MemberDecl member in decl.Members)
            {
                // A SETTING IS SOMETHING SOMEBODY WROTE. The binder adds
                // fields of its own to a static class -- a StaticReady$ flag,
                // a stashed exception, a property's backing field -- and none
                // of them is a setting; they are told apart by the characters
                // a name in source cannot contain.
                if (member is FieldDecl field && field.Mods.HasFlag(Mods.Static)
                    && !field.Name.Contains('$') && !field.Name.StartsWith('<'))
                {
                    Declare(field, prefix, schema, unit, errors);
                }
            }
        }
    }

    static string Key(TypeDecl decl)
        => decl.Outer is string outer && outer.Length > 0 ? outer + "." + decl.Name : decl.Name;

    static void Declare(FieldDecl field, string prefix, RegistrySchema schema,
                        CompilationUnit unit, List<CompileError> errors)
    {
        string key = prefix + field.Name.ToLowerInvariant();
        string written = field.Type.Name;
        RegistryEntry entry = new RegistryEntry { Key = key };

        entry.Label = Text(field, "Label") ?? Derived(field.Name);
        entry.Description = Text(field, "Description") ?? "";

        TypeDecl? choice = null;
        Dictionary<string, long>? choices = null;

        switch (written)
        {
            case "int" or "Int32" or "long" or "Int64" or "uint" or "UInt32" or "short" or "Int16":
                entry.Kind = RegistryValueKind.Int;
                break;
            case "bool" or "Boolean":
                entry.Kind = RegistryValueKind.Bool;
                break;
            case "string" or "String":
                entry.Kind = RegistryValueKind.String;
                break;
            case "byte" when field.Type.ArrayRank > 0:
            case "Byte" when field.Type.ArrayRank > 0:
                entry.Kind = RegistryValueKind.Binary;
                break;
            default:
                choice = Lookup(unit, written);

                if (choice is null)
                {
                    errors.Add(Error(field, $"a registry setting cannot be '{written}'; it is int, bool, "
                                          + "string, byte[] or an enum"));
                    return;
                }

                choices = Values(choice);
                entry.Kind = RegistryValueKind.Enum;
                entry.Enum = Intern(choice, choices, schema);
                break;
        }

        if (field.Type.ArrayRank > 0 && entry.Kind != RegistryValueKind.Binary)
        {
            errors.Add(Error(field, $"a registry setting cannot be an array of '{written}'; "
                                  + "byte[] is the only one the format has a record for"));
            return;
        }

        if (!Default(field, entry, choices, errors))
        {
            return;
        }

        // TWO USES OF ONE KEY MUST AGREE. They are separate declarations in
        // separate files as often as not -- that is what a bare [Registry]
        // on a toolkit is for -- and the one thing that cannot be allowed is
        // for them to disagree about what the setting IS.
        RegistryEntry? already = schema.Entries.FirstOrDefault(e => e.Key == key);

        if (already is null)
        {
            schema.Entries.Add(entry);
            return;
        }

        if (already.Kind != entry.Kind)
        {
            errors.Add(Error(field, $"'{key}' is declared as two different types"));
        }
        else if (already.Default != entry.Default || !Same(already.DefaultBytes, entry.DefaultBytes))
        {
            errors.Add(Error(field, $"'{key}' is declared with two different defaults"));
        }
    }

    static bool Same(byte[]? a, byte[]? b)
        => (a is null || a.Length == 0) && (b is null || b.Length == 0)
        || a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>
    /// The initialiser, read as a value. A setting with no initialiser has no
    /// default, which the doc does not allow: the whole design rests on the
    /// default living in the program.
    /// </summary>
    static bool Default(FieldDecl field, RegistryEntry entry, Dictionary<string, long>? choices, List<CompileError> errors)
    {
        if (field.DeclaredInit is not Expr init)
        {
            errors.Add(Error(field, $"'{field.Name}' has no default; a registry setting's default "
                                  + "is its initialiser and there is nowhere else for it to live"));
            return false;
        }

        if (entry.Kind is RegistryValueKind.String or RegistryValueKind.Binary)
        {
            if (init is LiteralExpr { Kind: Lit.Str } literal)
            {
                entry.DefaultBytes = System.Text.Encoding.UTF8.GetBytes(literal.Text);
                return true;
            }

            if (entry.Kind == RegistryValueKind.Binary)
            {
                entry.DefaultBytes = Array.Empty<byte>();
                return true;
            }

            errors.Add(Error(field, $"'{field.Name}' must have a literal default the compiler can write "
                                  + "into the schema"));
            return false;
        }

        if (Fold.TryConst(init, out long value, e => Named(e, choices)))
        {
            entry.Default = value;
            return true;
        }

        errors.Add(Error(field, $"'{field.Name}' must have a constant default the compiler can work out"));
        return false;
    }

    /// <summary>`Units.Banana` as the number it is, for an enum-typed setting.</summary>
    static long? Named(Expr e, Dictionary<string, long>? choices)
    {
        if (choices is null)
        {
            return null;
        }

        string? member = e switch
        {
            MemberExpr m => m.Name,
            NameExpr n => n.Name,
            _ => null,
        };

        return member is not null && choices.TryGetValue(member, out long value) ? value : null;
    }

    /// <summary>
    /// An enumeration, emitted once per schema and referenced by index: two
    /// settings sharing a type do not carry two copies of its members.
    /// </summary>
    static int Intern(TypeDecl choice, Dictionary<string, long> values, RegistrySchema schema)
    {
        int at = schema.Enums.FindIndex(e => e.Name == choice.Name);

        if (at >= 0)
        {
            return at;
        }

        RegistryEnum emitted = new RegistryEnum
        {
            Name = choice.Name,
            FlagSet = choice.Attributes.Contains("Flags") || choice.Attributes.Contains("FlagsAttribute"),
        };

        // Declaration order, which is the order a settings page shows them
        // in. TypeSymbol.EnumValues is a dictionary and has no order at all.
        {
            foreach (EnumMember member in choice.EnumMembers)
            {
                emitted.Members.Add(new RegistryEnumMember
                {
                    Name = member.Name,
                    Value = values.TryGetValue(member.Name, out long value) ? value : 0,
                    Label = Text(member.Attributes, "Label") ?? Derived(member.Name),
                    Description = Text(member.Attributes, "Description") ?? "",
                });
            }
        }

        schema.Enums.Add(emitted);
        return schema.Enums.Count - 1;
    }

    /// <summary>
    /// An enum member's value, worked out the way the binder works it out: a
    /// running counter that an explicit constant resets.
    /// </summary>
    static Dictionary<string, long> Values(TypeDecl choice)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        long next = 0;

        foreach (EnumMember member in choice.EnumMembers)
        {
            if (member.Value is Expr written && Fold.TryConst(written, out long folded))
            {
                next = folded;
            }

            values[member.Name] = next++;
        }
        return values;
    }

    static TypeDecl? Lookup(CompilationUnit unit, string name)
    {
        TypeDecl? found = null;

        foreach (TypeDecl decl in unit.Types)
        {
            if (decl.Kind != TypeKind.Enum)
            {
                continue;
            }

            // A nested enum is in the table under its full path; the field
            // wrote whatever was in scope where it was written.
            if (Key(decl) == name)
            {
                return decl;
            }

            if (decl.Name == name)
            {
                found = decl;
            }
        }
        return found;
    }

    static string? Text(FieldDecl field, string name) => Text(field.Attributes, name);

    static string? Text(List<AttributeRef> attributes, string name)
    {
        foreach (AttributeRef attribute in attributes)
        {
            if (attribute.Is(name) && attribute.Argument is string argument)
            {
                return argument;
            }
        }
        return null;
    }

    /// <summary>
    /// A label from the member name where none was given: "CanvasWidth"
    /// becomes "Canvas width", which is what a settings page would have
    /// written by hand anyway.
    /// </summary>
    static string Derived(string name)
    {
        System.Text.StringBuilder built = new();

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsAsciiLetterUpper(name[i]) && !char.IsAsciiLetterUpper(name[i - 1]))
            {
                built.Append(' ');
                built.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                built.Append(name[i]);
            }
        }
        return built.ToString();
    }

    static CompileError Error(Node where, string message)
        => new(where.File, where.Line, where.Col, message);
}
