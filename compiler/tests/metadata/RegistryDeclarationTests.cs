using Corsac.Lang;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

/// <summary>
/// What `[Registry]` in source turns into: the schema the kernel reads, and
/// the properties a declared setting becomes. docs/software/REGISTRY.md in
/// the OS repository is the design.
/// </summary>
public static class RegistryDeclarationTests
{
    const string Paint = """
        enum Units
        {
            [Label("Banana")] [Description("Roughly 18cm.")] Banana = 0,
            [Label("Apple")] Apple = 5,
            Pear = 18,
        }

        [Registry("CORSAC.Paint")]
        static class Settings
        {
            static class Canvas
            {
                [Label("Canvas width")]
                [Description("How wide a new drawing starts.")]
                public static int Width = 800;
                public static Units Units = Units.Pear;
            }

            static class Window
            {
                public static string Title = "Paint";
                public static bool Maximised = false;
            }
        }
        """;

    public static void Run()
    {
        RegistrySchema schema = Only(Paint);

        Check(schema.Domain == "corsac.paint", "the attribute's name is folded to lowercase");
        Check(!schema.Closed, "a domain is open unless it says otherwise");
        Check(schema.Entries.Count == 4, "every declared field is a setting");

        // NESTED STATIC CLASSES ARE PATH SEGMENTS, and the class carrying the
        // attribute contributes none of its own.
        Check(Key(schema, "canvas/width") is not null, "a nested class is a path segment");
        Check(Key(schema, "window/title") is not null, "and so is the next one along");
        Check(Key(schema, "settings/canvas/width") is null, "the declaring class is not one");

        RegistryEntry width = Key(schema, "canvas/width")!;

        Check(width.Kind == RegistryValueKind.Int, "an int setting is an int");
        Check(width.Default == 800, "its default is its initialiser");
        Check(width.Label == "Canvas width", "a label is read from the attribute");
        Check(width.Description == "How wide a new drawing starts.", "so is a description");

        RegistryEntry title = Key(schema, "window/title")!;

        Check(title.Kind == RegistryValueKind.String, "a string setting is a string");
        Check(System.Text.Encoding.UTF8.GetString(title.DefaultBytes!) == "Paint", "its default is the literal");
        Check(title.Label == "Title", "a label with none written is derived from the name");

        RegistryEntry maximised = Key(schema, "window/maximised")!;

        Check(maximised.Kind == RegistryValueKind.Bool, "a bool setting is a bool");
        Check(maximised.Default == 0, "false is nought");

        // A CHOICE IS AN ENUMERATION, emitted once and referenced by index,
        // with its members in declaration order and their own labels.
        RegistryEntry units = Key(schema, "canvas/units")!;

        Check(units.Kind == RegistryValueKind.Enum, "an enum-typed setting is a choice");
        Check(units.Enum == 0, "which names its enumeration by index");
        Check(units.Default == 18, "and whose default is the member's own number");
        Check(schema.Enums.Count == 1, "the enumeration is emitted once");
        Check(schema.Enums[0].Name == "Units", "under the name it was written with");
        Check(schema.Enums[0].Members.Count == 3, "with all of its members");
        Check(schema.Enums[0].Members[1].Name == "Apple", "in declaration order");
        Check(schema.Enums[0].Members[1].Value == 5, "keeping the values written");
        Check(schema.Enums[0].Members[2].Value == 18, "and counting on from them");
        Check(schema.Enums[0].Members[0].Description == "Roughly 18cm.", "a member carries its description");
        Check(schema.Enums[0].Members[2].Label == "Pear", "and a label derived where none was written");

        // A DECLARED MEMBER IS NOT STORAGE. By the time this returns the
        // fields are gone and accessors stand where they were, which is what
        // makes `Settings.Canvas.Width` read the registry.
        TypeDecl canvas = Class(Paint, "Settings.Canvas");

        Check(canvas.Members.Count == 2, "the members are still there");
        Check(canvas.Members.All(m => m is PropertyDecl), "and every one of them is a property now");
        Check(canvas.Members.OfType<PropertyDecl>().All(p => p.Getter is not null && p.Setter is not null),
              "each with both accessors, because a setting is read and written");

        // CLOSED IS DECIDED WHERE THE DOMAIN IS NAMED.
        Check(Only(Paint.Replace("[Registry(\"CORSAC.Paint\")]",
                                 "[Registry(\"CORSAC.Paint\", Closed = true)]")).Closed,
              "a named argument closes the domain");

        Rejected("public static float Ratio = 1.5f;", "cannot be", "a type the format has no record for");
        Rejected("public static int Nothing;", "has no default", "a setting with no initialiser");
        Rejected("public const int Fixed = 3;", "a setting is written", "a const setting");
        Rejected("public static readonly int Kept = 3;", "a setting is written", "a readonly setting");
    }

    /// <summary>The one schema a source declares, with no errors reported.</summary>
    static RegistrySchema Only(string source)
    {
        List<CompileError> errors = new();
        List<RegistrySchema> schemas = RegistryDeclarations.Collect(Parser.ParseText(source, "Paint.cor"), errors);

        if (errors.Count > 0)
        {
            throw new Exception("registry declarations: unexpected " + errors[0]);
        }

        Check(schemas.Count == 1, "one domain is one schema");
        return schemas[0];
    }

    static TypeDecl Class(string source, string key)
    {
        CompilationUnit unit = Parser.ParseText(source, "Paint.cor");

        RegistryDeclarations.Collect(unit, new List<CompileError>());
        return unit.Types.First(t => (t.Outer is null ? t.Name : t.Outer + "." + t.Name) == key);
    }

    /// <summary>A declaration the compiler must refuse, and why it says so.</summary>
    static void Rejected(string member, string because, string what)
    {
        string source = Paint.Replace("public static string Title = \"Paint\";",
                                      "public static string Title = \"Paint\";\n        " + member);
        List<CompileError> errors = new();

        RegistryDeclarations.Collect(Parser.ParseText(source, "Paint.cor"), errors);

        if (!errors.Any(e => e.Message.Contains(because, StringComparison.Ordinal)))
        {
            throw new Exception($"registry declarations: {what} was accepted, or refused for the wrong reason");
        }
    }

    static RegistryEntry? Key(RegistrySchema schema, string key)
        => schema.Entries.FirstOrDefault(e => e.Key == key);

    static void Check(bool value, string what)
    {
        if (!value)
        {
            throw new Exception("registry declarations: " + what);
        }
    }
}
