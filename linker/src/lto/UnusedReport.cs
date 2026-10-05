#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// THE PROGRAM'S DEAD CODE, said by the link (`--unused-report FILE`): every
/// function of the program's own sources that nothing reaches from the
/// entry, and every static field nothing reads -- never touched, or only
/// ever stored into. Judged on what each definition names as written
/// (UsesNotes), so a function the optimiser inlined into every caller still
/// counts as called. The roots are the entry and every symbol an object
/// without notes names: native objects (an assembly entry, its vectors)
/// and any unit compiled without the flag, all of whose own definitions are
/// then taken as used and none judged. The class library's code is never
/// reported: what a program leaves of it is the library's business.
/// </summary>
public static class UnusedReport
{
    sealed class Node
    {
        public required ObjectFile Owner;
        public required UsesNotes.Definition Definition;
        public bool Reached;
        public bool Written;
    }

    public static void Write(IReadOnlyList<(string Name, ObjectFile Object)> inputs, string entry, string path)
    {
        Dictionary<string, List<Node>> globals = new(StringComparer.Ordinal);
        Dictionary<(ObjectFile, string), Node> locals = new();
        List<string> unnoted = new();
        HashSet<ObjectFile> unnotedObjects = new();
        List<Node> all = new();
        foreach (var (name, obj) in inputs)
        {
            List<UsesNotes.Definition>? notes = UsesNotes.Read(obj);
            if (notes is null) { unnoted.Add(name); unnotedObjects.Add(obj); continue; }
            foreach (UsesNotes.Definition definition in notes)
            {
                Node node = new() { Owner = obj, Definition = definition };
                all.Add(node);
                if (definition.Exported)
                {
                    if (!globals.TryGetValue(definition.Name, out List<Node>? copies)) globals[definition.Name] = copies = new();
                    copies.Add(node);
                }
                else locals[(obj, definition.Name)] = node;
            }
        }

        Stack<Node> pending = new();
        IEnumerable<Node> Resolve(ObjectFile? from, string symbol)
        {
            if (from is not null && locals.TryGetValue((from, symbol), out Node? local)) return new[] { local };
            // Every copy of a definition several units made (a generic
            // instantiation, coalesced at the link): each is the one called.
            return globals.TryGetValue(symbol, out List<Node>? copies) ? copies : Enumerable.Empty<Node>();
        }
        void Reach(ObjectFile? from, string symbol)
        {
            foreach (Node node in Resolve(from, symbol))
                if (!node.Reached) { node.Reached = true; pending.Push(node); }
        }

        Reach(null, entry);
        foreach (ObjectFile obj in unnotedObjects)
            foreach (Section section in obj.Sections) foreach (Relocation relocation in section.Relocs) Reach(null, relocation.Symbol);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            foreach (string read in node.Definition.Reads) Reach(node.Owner, read);
            foreach (string written in node.Definition.Writes)
                foreach (Node target in Resolve(node.Owner, written)) target.Written = true;
        }

        // ONE LINE A DECLARATION: a generic method's every copy, or a static
        // two units each laid down, is one thing in the source, used if any
        // copy is.
        List<(string File, int Line, string Text)> lines = new();
        int functions = 0, statics = 0;
        foreach (var group in all.Where(node => !node.Definition.FromLibrary && node.Definition.Display is not null)
                     // By kind, place in the source and member: a generic
                     // method's copies print their own types and are still
                     // one method, while a getter and setter on one line, or
                     // `static int a, b;`, are two.
                     .GroupBy(node => (node.Definition.Kind, node.Definition.File, node.Definition.Line, Member(node.Definition.Display!))))
        {
            if (group.Any(node => node.Reached)) continue;
            string file = Shown(group.Key.File);
            string display = group.First().Definition.Display!;
            if (group.Key.Kind == UsesNotes.Kind.Function)
            {
                functions++;
                lines.Add((file, group.Key.Line, "function " + display + " is never called"));
            }
            else
            {
                statics++;
                lines.Add((file, group.Key.Line, "static " + display
                    + (group.Any(node => node.Written) ? " is written but never read" : " is never used")));
            }
        }

        StringBuilder text = new();
        text.Append("unused-code report for ").Append(entry).Append(": ").Append(functions).Append(" functions and ")
            .Append(statics).Append(" statics in the program's sources\n");
        if (unnoted.Count > 0)
            text.Append("not judged, compiled without --unused-report (all they name counts as used): ")
                .Append(string.Join(", ", unnoted.Select(Path.GetFileName))).Append('\n');
        foreach (var line in lines.OrderBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Line))
            text.Append(line.File).Append(':').Append(line.Line).Append(": ").Append(line.Text).Append('\n');
        if (path == "-") Console.Error.Write(text.ToString());
        else File.WriteAllText(path, text.ToString());
    }

    /// <summary>A source's path as the report prints it: from the current directory when it is beneath it.</summary>
    static string Shown(string? file)
    {
        if (file is null) return "(no source)";
        if (!Path.IsPathRooted(file)) return file;
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
    }

    /// <summary>
    /// The member's own name in a display: `List$int.Add(Int32 item)` is
    /// `Add`, and so is a generic method's copy `Add$Int32`.
    /// </summary>
    static string Member(string display)
    {
        int open = display.IndexOf('(');
        string named = open < 0 ? display : display[..open];
        named = named[(named.LastIndexOf('.') + 1)..];
        int copy = named.IndexOf('$');
        return copy < 0 ? named : named[..copy];
    }
}
