#nullable enable
using System.Collections;
using System.Reflection;
namespace Corsac.Lang;

// TEMPORARY: structural comparison of a member and its rewritten copy.
internal static class ZzVerify
{
    public static long Compared, Differed;
    public static readonly Dictionary<string, long> Diffs = new();
    public static readonly Dictionary<string, long> Shared = new();
    private static readonly object Gate = new();
    static ZzVerify() { AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (Gate) Report(); }; }

    public static void Check(MemberDecl old, MemberDecl copy)
    {
        lock (Gate)
        {
            Compared++;
            Dictionary<object, int> seen = new(ReferenceEqualityComparer.Instance);
            List<string> found = new();
            Same(old, copy, old.GetType().Name, found, seen, 0);
            foreach (var p in seen) if (p.Value > 1 && p.Key is Node) { string k = p.Key.GetType().Name; Shared[k] = Shared.GetValueOrDefault(k) + 1; }
            if (found.Count > 0)
            {
                Differed++;
                foreach (string f in found.Distinct()) Diffs[f] = Diffs.GetValueOrDefault(f) + 1;
                if (Differed <= 5) Console.Error.WriteLine("ZZ differs " + old.Name + ": " + string.Join(" | ", found.Take(6)));
            }
            if ((Compared & 0xfff) == 0) Report();
        }
    }

    public static void Report()
    {
        Console.Error.WriteLine($"ZZ compared={Compared} differed={Differed} diffs={string.Join(", ", Diffs.OrderByDescending(p => p.Value).Take(12).Select(p => p.Key + "=" + p.Value))} shared={string.Join(", ", Shared.OrderByDescending(p => p.Value).Take(8).Select(p => p.Key + "=" + p.Value))}");
    }

    private static bool Recurse(System.Type t)
        => (t.Namespace ?? "").StartsWith("Corsac.Lang") && !t.Name.EndsWith("Symbol") && !t.Name.EndsWith("Sym")
           && t != typeof(TypeDecl) && t != typeof(FileScope) && !t.IsEnum && !t.IsPrimitive;

    private static void Same(object? a, object? b, string path, List<string> found, Dictionary<object, int> seen, int depth)
    {
        if (found.Count > 20 || depth > 200) return;
        if (a is not null && !a.GetType().IsValueType) { seen[a] = seen.GetValueOrDefault(a) + 1; if (seen[a] > 1) return; }
        if (ReferenceEquals(a, b)) return;
        if (a is null || b is null) { found.Add(path + " null"); return; }
        System.Type t = a.GetType();
        if (t != b.GetType()) { found.Add(path + " type " + t.Name + "/" + b.GetType().Name); return; }
        if (a is string || t.IsPrimitive || t.IsEnum) { if (!a.Equals(b)) found.Add(path + " value"); return; }
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count) { found.Add(path + " count"); return; }
            for (int i = 0; i < la.Count; i++) Same(la[i], lb[i], path + "[]", found, seen, depth + 1);
            return;
        }
        if (a is IDictionary da && b is IDictionary db)
        {
            if (da.Count != db.Count) { found.Add(path + " dcount"); return; }
            var ea = da.Values.Cast<object?>().ToList(); var eb = db.Values.Cast<object?>().ToList();
            for (int i = 0; i < ea.Count; i++) Same(ea[i], eb[i], path + "{}", found, seen, depth + 1);
            return;
        }
        if (t.IsValueType && !Recurse(t) && !t.Name.StartsWith("ValueTuple")) { if (!a.Equals(b)) found.Add(path + " struct"); return; }
        if (!Recurse(t) && !t.Name.StartsWith("ValueTuple")) { found.Add(path + " ref " + t.Name); return; }
        for (System.Type? c = t; c is not null && c != typeof(object); c = c.BaseType)
        {
            foreach (FieldInfo f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Same(f.GetValue(a), f.GetValue(b), c.Name + "." + f.Name, found, seen, depth + 1);
            }
        }
    }
}
