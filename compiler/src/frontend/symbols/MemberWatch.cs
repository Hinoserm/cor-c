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

    /// <summary>Its own reads under way that are not the unit's (its declaring, its copying).</summary>
    private int _quiet;

    /// <summary>Reads that are the shape's, of every type (a unit's layouts captured).</summary>
    [ThreadStatic] private static int _shape;

    /// <summary>--receipt-trace: say where each type is first read whole, and by what.</summary>
    public static bool Trace => Corsac.Switches.TraceReceipt;

    /// <summary>The type's name, for the trace.</summary>
    public string Key { get; }

    public MemberWatch(string key) => Key = key;

    /// <summary>A name asked of the type by a lookup by name.</summary>
    public void Asked(string name) => Names.Add(name);

    /// <summary>The member lists read: in bulk, unless quietly.</summary>
    public void ReadAll()
    {
        if (_quiet > 0 || _shape > 0) return;
        // Traced, every place that reads one whole is said once, not only the first.
        if (Trace)
        {
            System.Diagnostics.StackTrace stack = new(1, false);
            string frames = string.Join(" < ", stack.GetFrames().Take(6).Select(frame => frame.GetMethod() is { } method
                ? method.DeclaringType?.Name + "." + method.Name : "?"));
            lock (Said) if (Said.Add(frames)) Console.Error.WriteLine("receipt: " + Key[(Key.IndexOf('\n') + 1)..] + " read whole by " + frames);
        }
        Whole = true;
    }

    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);

    /// <summary>Its own members read for its own declaring or copying, not by the unit: `using (watch?.Quietly())`.</summary>
    public Quiet Quietly() { _quiet++; return new Quiet(this); }

    /// <summary>Every type's members read for what is their shape's to say (ManagedLayouts.Capture).</summary>
    public static ShapeRead ShapesOnly() { _shape++; return new ShapeRead(); }

    public readonly struct Quiet : IDisposable
    {
        private readonly MemberWatch _watch;
        internal Quiet(MemberWatch watch) => _watch = watch;
        public void Dispose() => _watch._quiet--;
    }

    public readonly struct ShapeRead : IDisposable
    {
        public void Dispose() => _shape--;
    }
}
