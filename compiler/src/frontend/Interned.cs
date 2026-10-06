#nullable enable
namespace Corsac.Lang;

/// <summary>
/// ONE COPY OF A NAME PER THREAD'S UNIT. A type's name is spelled again at
/// every reference to it -- qualified, mangled for a specialisation, joined
/// to a member -- and each spelling was a string of its own: in a heap dump
/// of a large unit half of the 40 MB of strings were copies of another.
///
/// Per thread, because a project's units compile on a dozen at once and a
/// process-wide table is a lock every reference takes; bounded, because a
/// thread compiles unit after unit and what one unit named the next mostly
/// does not -- past its size the table starts again, and a name is only
/// ever a copy too many, never wrong.
/// </summary>
public static class Interned
{
    [ThreadStatic] private static Dictionary<string, string>? _names;
    private const int MostNames = 1 << 17;

    public static string Name(string value)
    {
        Dictionary<string, string> names = _names ??= new(StringComparer.Ordinal);
        if (names.TryGetValue(value, out string? had)) return had;
        if (names.Count >= MostNames) names.Clear();
        names[value] = value;
        return value;
    }

    /// <summary>
    /// A GENERIC TEMPLATE'S KEY, `name`count`, asked for at every reference
    /// to a generic type the monomorphiser follows: spelt each time only to
    /// be found in Name's table, it is kept here by its two parts instead,
    /// and spelt once.
    /// </summary>
    public static string WithArity(string name, int count)
    {
        Dictionary<(string, int), string> arities = _arities ??= new();
        if (arities.TryGetValue((name, count), out string? had)) return had;
        if (arities.Count >= MostNames) arities.Clear();
        string spelt = Name($"{name}`{count}");
        arities[(name, count)] = spelt;
        return spelt;
    }

    [ThreadStatic] private static Dictionary<(string, int), string>? _arities;

    /// <summary>
    /// `prefix + name` kept by its two parts: an accessor's name (`get_` and
    /// a property's) asked for at every simple name a body reads, spelt each
    /// time only to be looked for among the members.
    /// </summary>
    public static string Prefixed(string prefix, string name)
    {
        Dictionary<(string, string), string> joined = _prefixed ??= new();
        if (joined.TryGetValue((prefix, name), out string? had)) return had;
        if (joined.Count >= MostNames) joined.Clear();
        string spelt = Name(prefix + name);
        joined[(prefix, name)] = spelt;
        return spelt;
    }

    [ThreadStatic] private static Dictionary<(string, string), string>? _prefixed;

    /// <summary>
    /// A STRING BUILDER TO SPELL A NAME IN, one per thread and used again:
    /// a name made of parts -- a type with its arguments, a specialisation's
    /// mangling -- was a string for each part and each join, where one
    /// builder and one ToString make it a single string. Taken, not shared:
    /// a name spelt while another is (an argument's own arguments) is given
    /// a builder of its own, and Return keeps whichever comes back first.
    /// </summary>
    public static System.Text.StringBuilder Builder()
    {
        System.Text.StringBuilder? b = _builder;
        if (b is null) return new System.Text.StringBuilder(64);
        _builder = null;
        return b;
    }

    /// <summary>The builder's text, and the builder back for the next name.</summary>
    public static string Return(System.Text.StringBuilder b)
    {
        string text = b.ToString();
        // A BUILDER THAT GREW FOR ONE LONG NAME is let go rather than kept
        // at that size for the life of the thread.
        if (b.Capacity <= 1024)
        {
            b.Clear();
            _builder = b;
        }
        return text;
    }

    [ThreadStatic] private static System.Text.StringBuilder? _builder;

    /// <summary>A new unit on this thread: what the last one named is let go with it.</summary>
    public static void Forget()
    {
        _names = null;
        _arities = null;
        _prefixed = null;
    }
}
