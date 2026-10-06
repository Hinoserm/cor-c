#nullable enable
namespace Corsac.Lang;

/// <summary>Every node carries a position, because every diagnostic needs one.</summary>
public abstract class Node
{
    public int Line { get; init; }
    public int Col { get; init; }

    /// <summary>
    /// Which file this was written in.
    ///
    /// A program is compiled from several sources at once -- a heap, a task
    /// library, a driver, the program itself -- and they become ONE unit before
    /// anything is checked. Without this every diagnostic was stamped with the
    /// name of the first file on the command line, so an error in the last one
    /// was reported at a line number that belonged to a different file
    /// entirely, and pointed at code that was perfectly correct.
    ///
    /// A DECLARATION'S OWN FIELD (MemberDecl, TypeDecl), which is where the
    /// parser writes it and where a diagnostic finds it (Binder.Where: the
    /// node's, else its member's). Anything else -- an expression, a
    /// statement, a type as written -- is told its file only by a copy of a
    /// node that had none, and is asked only to be copied again, so it has
    /// no field for it: one on every node was eight bytes on every one of a
    /// large unit's million, six megabytes live at the end of binding to
    /// hold the empty string. One that is told a file all the same keeps it
    /// beside it, on the thread that binds it (Elsewhere).
    /// </summary>
    public virtual string File
    {
        get => _elsewhere is { } told && told.TryGetValue(this, out string? file) ? file : "";
        set
        {
            if (value.Length != 0)
            {
                Dictionary<Node, string> told = _elsewhere ??= new(ReferenceEqualityComparer.Instance);
                // BOUNDED, as Interned is: a thread compiles unit after unit,
                // and what this holds is a diagnostic's file name, never more.
                if (told.Count >= MostElsewhere) told.Clear();
                told[this] = value;
            }
            else
            {
                _elsewhere?.Remove(this);
            }
        }
    }

    /// <summary>The file of a node that is no declaration, the few told one (File).</summary>
    [ThreadStatic] private static Dictionary<Node, string>? _elsewhere;
    private const int MostElsewhere = 1 << 12;
}
