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

    /// <summary>A new unit on this thread: what the last one named is let go with it.</summary>
    public static void Forget() => _names = null;
}
