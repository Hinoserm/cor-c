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
    /// </summary>
    public string File { get; set; } = "";
}
