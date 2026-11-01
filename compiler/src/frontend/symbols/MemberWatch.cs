#nullable enable
namespace Corsac.Lang;

/// <summary>
/// WHAT A UNIT READ OF A TYPE IT DID NOT DECLARE, member by member, for its
/// receipt (Dependence.Members): the names it asked the type for, and
/// whether it ever read the type's members in bulk. One for each type read
/// from the index whose members can be told apart (SourceDeclaration.ByMember),
/// shared by every declaration of it the unit makes and copies.
///
/// SAFE BY DEFAULT. A name asked through the binder's lookups by name
/// (TypeSymbol.FindField, FindMethods, MayHave) is recorded; anything else
/// that reads the member lists -- TypeSymbol.Fields or Methods, TypeDecl.Members
/// -- reads them all, whatever it then does with them, and the unit depends
/// on the whole declaration as it always did. A path that reads more than it
/// needs costs a rebuild, never a stale object. What reads a type's members
/// for what is the shape's to say (its own declaring, its layout) does so
/// quietly (Quietly).
/// </summary>
public sealed class MemberWatch
{
    /// <summary>Whether the members were read in bulk: the whole declaration is the unit's dependence.</summary>
    public bool Whole { get; private set; }

    /// <summary>The names asked of it.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// WHOSE OWN WORK IS UNDER WAY, innermost last (Within): a type's
    /// declaring, its copying, its layout, the working out of its constants.
    /// What that work reads of the type itself is not the unit's (neither
    /// bulk reads nor names asked count); what another type's work started
    /// inside it reads is: T's constant naming S.X, whose value names T.P,
    /// asks P of T, and a scope counted per type -- quiet for T throughout --
    /// lost it. Null for a type with no watch, which ends the quiet the same.
    /// </summary>
    [ThreadStatic] private static List<MemberWatch?>? _within;

    /// <summary>Its own work innermost, or every type's shape read: what is read of it now is not the unit's.</summary>
    private bool Quiet => _shape > 0 || _within is { Count: > 0 } within && ReferenceEquals(within[within.Count - 1], this);

    /// <summary>Reads that are the shape's, of every type (a unit's layouts captured).</summary>
    [ThreadStatic] private static int _shape;

    /// <summary>--receipt-trace: say where each type is first read whole, and by what.</summary>
    public static bool Trace => Corsac.Switches.TraceReceipt;

    /// <summary>The type's name, for the trace.</summary>
    public string Key { get; }

    public MemberWatch(string key) => Key = key;

    /// <summary>A name asked of the type by a lookup by name.</summary>
    public void Asked(string name)
    {
        // Not while its own reads are under way, nor its shape's, as for a
        // read whole: working out its own constants asks each by its name.
        if (Quiet) return;
        // Traced, where each name is first asked, as for a read whole.
        if (Names.Add(name) && Trace) Say(name + " asked by ");
    }

    /// <summary>The member lists read: in bulk, unless quietly.</summary>
    public void ReadAll()
    {
        if (Quiet) return;
        // Traced, every place that reads one whole is said once, not only the first.
        if (Trace) Say("read whole by ");
        Whole = true;
    }

    /// <summary>--receipt-trace: what was read of the type, and the call sites that read it, once each.</summary>
    private void Say(string what)
    {
        // The stack's text, not its frames' methods, which a native
        // compiler's trimming cannot promise.
        string frames = string.Join(" < ", Environment.StackTrace.Split('\n').Skip(3).Take(8)
            .Select(line => line.Trim().StartsWith("at ", StringComparison.Ordinal) ? line.Trim()[3..].Split('(')[0] : line.Trim()));
        string said = "receipt: " + Key[(Key.IndexOf('\n') + 1)..] + " " + what + frames;
        lock (Said) if (Said.Add(said)) Console.Error.WriteLine(said);
    }

    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);

    /// <summary>Its own members read for its own declaring or copying, not by the unit: `using (watch.Quietly())`.</summary>
    public Scope Quietly() => Within(this);

    /// <summary>A type's own work begun, watched or not (null): `using (MemberWatch.Within(d.Watch))`.</summary>
    public static Scope Within(MemberWatch? owner)
    {
        (_within ??= new()).Add(owner);
        return new Scope();
    }

    /// <summary>Every type's members read for what is their shape's to say (ManagedLayouts.Capture).</summary>
    public static ShapeRead ShapesOnly() { _shape++; return new ShapeRead(); }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() => _within!.RemoveAt(_within.Count - 1);
    }

    public readonly struct ShapeRead : IDisposable
    {
        public void Dispose() => _shape--;
    }
}
