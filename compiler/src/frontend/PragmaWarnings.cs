#nullable enable
namespace Corsac.Lang;

/// <summary>
/// One `#pragma warning disable` or `#pragma warning restore`, recorded where
/// the lexer reads it: which file it is in, which line the directive itself
/// sits on, which code it names (null for the bare form, C#'s "every code"),
/// and whether it turns that code off or back on.
/// </summary>
public readonly record struct PragmaWarning(string File, int Line, string? Code, bool Disabled);

/// <summary>
/// Whether a warning is suppressed at the place it would be reported,
/// answered by replaying a file's own directives rather than keeping a
/// running "currently disabled" set.
///
/// A RUNNING SET WOULD HAVE TO BE BUILT IN SOURCE ORDER AND CONSULTED IN
/// SOURCE ORDER TOO, which is fine for the lexer's own `#warning` (it asks
/// once, moving forward, and has nothing later to replay) but wrong for the
/// binder: a method the parser read early can be checked after one read
/// late, so "what was disabled when we got here" has no fixed meaning
/// except "replay every directive at or before this line". That is what
/// this does, each time it is asked.
/// </summary>
public static class PragmaWarnings
{
    public static bool IsSuppressed(IEnumerable<PragmaWarning> pragmas, string file, string code, int line)
    {
        bool blanket = false;
        Dictionary<string, bool>? overrides = null;

        foreach (PragmaWarning p in pragmas)
        {
            if (p.Line > line || !string.Equals(p.File, file, StringComparison.Ordinal))
            {
                continue;
            }

            if (p.Code is null)
            {
                // A BLANKET DISABLE OR RESTORE REPLACES EVERYTHING NAMED
                // BEFORE IT. A specific `restore CS1234` under an earlier
                // blanket `disable` no longer means anything once a later
                // blanket directive is read -- the same as C#, where a
                // second bare `disable` leaves no earlier per-code
                // `restore` in force.
                blanket = p.Disabled;
                overrides?.Clear();
            }
            else
            {
                (overrides ??= new Dictionary<string, bool>(StringComparer.Ordinal))[p.Code] = p.Disabled;
            }
        }

        return overrides != null && overrides.TryGetValue(code, out bool state) ? state : blanket;
    }
}
