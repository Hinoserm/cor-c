using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// THE ESCAPE ENGINE (RegionEscape), one behaviour a test: small functions
/// stated as their region constraints, solved, and its answers -- what
/// outlives each function (Escaping), what is global, what a loop holds, a
/// summary -- checked against what the program means. Every expectation is
/// argued from the constraints, in the comment beside it, never read off a
/// run. A function's nodes are its parameters, then its return, then its
/// own (RegionFunction).
/// </summary>
public static class RegionEscapeTests
{
    /// <summary>Each test by name, for Program to run one at a time.</summary>
    public static readonly (string Name, Action Run)[] All =
    {
        ("inclusion: stores, loads and returns through parameters", Inclusion),
        ("inclusion: what a loop's laps hold", LoopsHold),
        ("a cycle of calls solved together", Cycle),
        ("past the inclusion bound, unified", Overflow),
        ("a large cycle's summary, field by field", UnifiedByField),
        ("a large cycle keeps two fields at one offset apart", UnifiedFamilies),
        ("a place below a field names the field", TypedPlaceSteps),
        ("wide calls: stand-ins and rounds answer as following every call", WideCalls),
        ("wide calls: what one target leaks of what it makes leaves what another hands back", WideHeldApart),
        ("a constant's address holds nothing", Constants),
        ("a number parameter is handed no address", NumberParameters),
        ("a virtual call runs on each object only what it may", Guards),
        ("what one field of a place is written is read by no other field's load", TypedAliasing),
        ("a callee's write of a field into a parameter keeps its field at the caller", TypedAliasingThroughSummary),
        ("a wide call on a parameter is deferred for the targets that leak", WideDeferred),
        ("past its groups, a call keeps apart the targets that leak", GroupsFull),
        ("a call on a blob runs what its members' classes run", BlobDispatch),
        ("saturated nodes in a chain, each blob a member of the next", BlobChain),
        ("a wide call deferred through two levels of parameters", WideDeferredTwoLevels),
        ("a node past MostHeld objects holds a blob of the rest", Saturation),
        ("a summary past MostCells is everything", MostCells),
        ("a parameter passed nothing is the unknown object", Unpassed),
        ("a function called from where nobody follows", Rooted),
        ("copies of one body in several units are solved once, answered alike", Copies),
        ("region hints round trip at the current version", HintsRoundTrip),
        ("backend facts round trip: region sizes and owned-field flags", BackendRoundTrip),
        ("a virtual call runs nothing on a type the image never makes", MadeTypes),
        ("a virtual call runs only what its receiver's types run", ReceiverTypes),
        ("an iterator's yielded elements die in the loop that walks it", IteratorElements),
        ("a closure that leaks one capture keeps the other", ClosureCaptures),
        ("a method no blind call's slot holds is not called from where nobody follows", BlindSlots),
    };

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    // ---- building a program ------------------------------------------------

    private static int Leaf(int site) => -site - 1;

    private sealed class Prog
    {
        public readonly List<RegionFunction> Functions = new();
        // A virtual call's symbol, to its targets by name.
        public readonly Dictionary<string, string[]> Virtuals = new(StringComparer.Ordinal);

        public RegionFunction Add(string name, int parameters, int nodes, int sites = 0)
        {
            RegionSite[] made = new RegionSite[sites];
            for (int s = 0; s < sites; s++) made[s] = new RegionSite(true, 1, null, 0);
            RegionFunction f = new(name, true, true, false, parameters, nodes, 0, made);
            Functions.Add(f);
            return f;
        }

        public int Index(string name) => Functions.FindIndex(f => f.Name == name);

        public int[] SiteBase()
        {
            int[] bases = new int[Functions.Count];
            for (int f = 1; f < Functions.Count; f++) bases[f] = bases[f - 1] + Functions[f - 1].Sites.Length;
            return bases;
        }

        /// <summary>A site of a function, numbered among every function's.</summary>
        public int Site(string function, int ordinal) => SiteBase()[Index(function)] + ordinal;

        public RegionEscape Solve(int wide = 16, Func<int, int, int, int[]?>? targetsOn = null, string[]? rooted = null, Action<string>? progress = null, long? pool = null,
            Action<int[]?[][], string?[][], bool[]>? narrow = null)
        {
            int count = Functions.Count;
            int[]?[][] targets = new int[]?[count][];
            string?[][] keys = new string?[count][];
            for (int f = 0; f < count; f++)
            {
                RegionFunction function = Functions[f];
                targets[f] = new int[]?[function.Calls.Count];
                keys[f] = new string?[function.Calls.Count];
                for (int k = 0; k < function.Calls.Count; k++)
                {
                    string? callee = function.Calls[k].Callee;
                    if (callee is null) continue;
                    if (Virtuals.TryGetValue(callee, out string[]? overrides))
                    {
                        targets[f][k] = overrides.Select(Index).Order().ToArray();
                        keys[f][k] = callee;
                    }
                    else targets[f][k] = new[] { Index(callee) };
                }
            }
            int[] siteBase = SiteBase();
            int sites = count == 0 ? 0 : siteBase[^1] + Functions[^1].Sites.Length;
            bool[] wanted = Enumerable.Repeat(true, count).ToArray();
            bool[] roots = Functions.Select(f => rooted?.Contains(f.Name) == true).ToArray();
            // The link's narrowing of each virtual call to what its receiver's types run (RegionTypes), where a test asks.
            narrow?.Invoke(targets, keys, roots);
            RegionEscape escape = new(Functions, targets, keys, siteBase, sites, wanted, roots)
            {
                WideTargets = wide, TargetsOn = targetsOn, Progress = progress,
            };
            if (pool is { } work) escape.InclusionPerRound = work;
            escape.Run();
            return escape;
        }
    }

    private static RegionConstraint Site(int node, int ordinal) => new(RegionConstraintKind.Site, node, ordinal, 0);
    private static RegionConstraint Copy(int to, int from) => new(RegionConstraintKind.Copy, to, from, 0);
    private static RegionConstraint Load(int to, int address, int offset) => new(RegionConstraintKind.Load, to, address, offset);
    private static RegionConstraint Store(int address, int value, int offset) => new(RegionConstraintKind.Store, address, value, offset);
    private static RegionConstraint Leak(int node) => new(RegionConstraintKind.Leak, node, 0, 0);

    // ---- the tests ---------------------------------------------------------

    /// <summary>
    /// Make hands back what it makes; Hold(box, x) writes x into box at 8;
    /// Fetch(box) hands back what is at 8 of box. Keeps makes a box, holds
    /// Make's object in it and hands nothing back: both are dead by its
    /// return. Hands does the same and hands back what Fetch finds in the
    /// box: Make's object outlives it, the box does not. Gives hands back
    /// the box itself: the box outlives it, and Make's object with it, held
    /// at 8. Nothing reaches the unknown object, so nothing is global.
    /// </summary>
    private static void Inclusion()
    {
        Prog p = new();
        RegionFunction make = p.Add("Make", 0, 2, sites: 1);
        make.Constraints.AddRange(new[] { Site(1, 0), Copy(0, 1) });
        p.Add("Hold", 2, 3).Constraints.Add(Store(0, 1, 8));
        p.Add("Fetch", 1, 3).Constraints.AddRange(new[] { Load(2, 0, 8), Copy(1, 2) });
        RegionFunction keeps = p.Add("Keeps", 0, 3, sites: 1);
        keeps.Constraints.Add(Site(1, 0));
        keeps.Calls.Add(new("Make", 2, Array.Empty<int>()));
        keeps.Calls.Add(new("Hold", -1, new[] { 1, 2 }));
        RegionFunction hands = p.Add("Hands", 0, 4, sites: 1);
        hands.Constraints.AddRange(new[] { Site(1, 0), Copy(0, 3) });
        hands.Calls.Add(new("Make", 2, Array.Empty<int>()));
        hands.Calls.Add(new("Hold", -1, new[] { 1, 2 }));
        hands.Calls.Add(new("Fetch", 3, new[] { 1 }));
        RegionFunction gives = p.Add("Gives", 0, 3, sites: 1);
        gives.Constraints.AddRange(new[] { Site(1, 0), Copy(0, 1) });
        gives.Calls.Add(new("Make", 2, Array.Empty<int>()));
        gives.Calls.Add(new("Hold", -1, new[] { 1, 2 }));
        RegionEscape e = p.Solve();

        int made = p.Site("Make", 0);
        Check(!e.Escapes(p.Index("Keeps"), p.Site("Keeps", 0)) && !e.Escapes(p.Index("Keeps"), made), "Keeps: its box and Make's object are dead by its return");
        Check(e.Escapes(p.Index("Hands"), made), "Hands: Make's object, fetched from the box and handed back, outlives it");
        Check(!e.Escapes(p.Index("Hands"), p.Site("Hands", 0)), "Hands: the box itself is dead by its return");
        Check(e.Escapes(p.Index("Gives"), p.Site("Gives", 0)) && e.Escapes(p.Index("Gives"), made), "Gives: the box handed back and what it holds outlive it");
        Check(e.Escapes(p.Index("Make"), made), "Make: what it hands back outlives it");
        Check(!e.Global.Any(g => g), "nothing reaches the unknown object");
    }

    /// <summary>
    /// Looper's one loop has node 1 (site 0's object) live where a lap ends
    /// and node 2 (site 1's) live into its header and never written in its
    /// body: what is live at a lap's end is site 0, what the loop keeps
    /// site 1, each by its own leaf origin.
    /// </summary>
    private static void LoopsHold()
    {
        Prog p = new();
        RegionFunction looper = p.Add("Looper", 0, 3, sites: 2);
        looper.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1) });
        looper.Loops.Add(new RegionLoopShape(1, new[] { 0, 1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), new[] { 1 }, new[] { 2 }, Array.Empty<int>()));
        RegionEscape e = p.Solve();
        var held = e.LoopHeld[p.Index("Looper")] ?? throw new Exception("Looper: no loop answered");
        Check(held.Length == 1, "Looper: one loop answered");
        Check(held[0].LapLive.SequenceEqual(new[] { Leaf(p.Site("Looper", 0)) }), "Looper: site 0 is live where a lap ends");
        Check(held[0].Kept.SequenceEqual(new[] { Leaf(p.Site("Looper", 1)) }), "Looper: site 1 is what the loop keeps");
    }

    /// <summary>
    /// Even and Odd call each other, and Odd throws its argument (Leak):
    /// anything handed to either reaches the unknown object. Ping and Pong
    /// call each other and do nothing else. A cycle is solved as one, two
    /// functions at most here.
    /// </summary>
    private static void Cycle()
    {
        Prog p = new();
        p.Add("Even", 1, 2).Calls.Add(new("Odd", -1, new[] { 0 }));
        RegionFunction odd = p.Add("Odd", 1, 2);
        odd.Constraints.Add(Leak(0));
        odd.Calls.Add(new("Even", -1, new[] { 0 }));
        p.Add("Ping", 1, 2).Calls.Add(new("Pong", -1, new[] { 0 }));
        p.Add("Pong", 1, 2).Calls.Add(new("Ping", -1, new[] { 0 }));
        RegionFunction leaky = p.Add("UseLeaky", 0, 2, sites: 1);
        leaky.Constraints.Add(Site(1, 0));
        leaky.Calls.Add(new("Even", -1, new[] { 1 }));
        RegionFunction quiet = p.Add("UseQuiet", 0, 2, sites: 1);
        quiet.Constraints.Add(Site(1, 0));
        quiet.Calls.Add(new("Ping", -1, new[] { 1 }));
        RegionEscape e = p.Solve();
        Check(e.LargestCycle == 2, "the largest cycle is Even and Odd (or Ping and Pong): two");
        Check(e.Global[p.Site("UseLeaky", 0)], "UseLeaky: handed to Even, its object is thrown by Odd");
        Check(!e.Global[p.Site("UseQuiet", 0)] && !e.Escapes(p.Index("UseQuiet"), p.Site("UseQuiet", 0)), "UseQuiet: Ping and Pong keep nothing");
    }

    /// <summary>
    /// Big puts 200 sites into node 1 and copies node 1 into 1000 nodes:
    /// carrying them is 200 locations into each of 1001 nodes, past the
    /// bound of 150000 + 20 per node (1003 nodes: 170060), so inclusion
    /// gives up and the function is unified instead (Fallbacks). Unified,
    /// a copy is one class: the 200 sites are one class with node 2, which
    /// Big hands back -- they outlive it. Site 200 is in node 1002 alone,
    /// joined with nothing: dead by its return.
    /// </summary>
    private static void Overflow()
    {
        Prog p = new();
        RegionFunction big = p.Add("Big", 0, 1003, sites: 201);
        for (int s = 0; s < 200; s++) big.Constraints.Add(Site(1, s));
        for (int k = 2; k <= 1001; k++) big.Constraints.Add(Copy(k, 1));
        big.Constraints.Add(Copy(0, 2));
        big.Constraints.Add(Site(1002, 200));
        // With no pool past its own bound, unified; with the link's pool, it
        // finishes by inclusion. Either way the same answers.
        foreach (bool pooled in new[] { false, true })
        {
            string how = pooled ? "with the pool" : "with no pool";
            RegionEscape e = p.Solve(pool: pooled ? null : 0);
            if (pooled) Check(e.Fallbacks == 0 && e.PoolComponents == 1 && e.PoolDrawn > 0, how + ": Big went past its own bound and finished by inclusion on the pool's work");
            else Check(e.Fallbacks == 1, how + ": Big went past its bound by inclusion and was unified");
            Check(e.Escapes(p.Index("Big"), p.Site("Big", 0)) && e.Escapes(p.Index("Big"), p.Site("Big", 199)), how + ": Big: the copied sites are handed back");
            Check(!e.Escapes(p.Index("Big"), p.Site("Big", 200)), how + ": Big: site 200, alone in its node, is dead by its return");
            Check(!e.Global.Any(g => g), how + ": nothing reaches the unknown object");
        }
    }

    /// <summary>
    /// A ring of 301 functions, each handing (holder, leak, mine) to the
    /// next: past 300 members, unified. The last writes leak into holder at
    /// 8 and throws it, writes mine into holder at 16, and goes round again.
    /// Caller makes all three and calls the ring: leak reaches the unknown
    /// object, holder and mine reach nothing that outlives Caller -- only
    /// field 8 of the holder leaks. The ring's summary says as much: the
    /// unknown object holds parameter 1, and neither parameter 0 nor 2, nor
    /// what is at 16 of parameter 0.
    /// </summary>
    private static void UnifiedByField()
    {
        Prog p = new();
        const int ring = 301;
        for (int i = 0; i < ring - 1; i++) p.Add("R" + i, 3, 4).Calls.Add(new("R" + (i + 1), -1, new[] { 0, 1, 2 }));
        RegionFunction last = p.Add("R" + (ring - 1), 3, 4);
        last.Constraints.AddRange(new[] { Store(0, 1, 8), Leak(1), Store(0, 2, 16) });
        last.Calls.Add(new("R0", -1, new[] { 0, 1, 2 }));
        RegionFunction caller = p.Add("Caller", 0, 4, sites: 3);
        caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), Site(3, 2) });
        caller.Calls.Add(new("R0", -1, new[] { 1, 2, 3 }));
        RegionEscape e = p.Solve();
        Check(e.LargestCycle == ring, "the ring is one cycle of 301");
        Check(e.Global[p.Site("Caller", 1)], "the leaked object is global");
        Check(!e.Global[p.Site("Caller", 0)] && !e.Global[p.Site("Caller", 2)], "the holder and the local are not global");
        Check(!e.Escapes(p.Index("Caller"), p.Site("Caller", 0)) && !e.Escapes(p.Index("Caller"), p.Site("Caller", 2)), "the holder and the local are dead by Caller's return");
        var (objects, cells, _, _) = e.SummaryOf(p.Index("R0")) ?? throw new Exception("R0 has no summary");
        bool Leaked(string place) => cells.Any(c => c.From == 0 && objects[c.To] == place);
        Check(Leaked("Place 1 []"), "R0's summary: the unknown object holds parameter 1");
        Check(!Leaked("Place 0 []") && !Leaked("Place 2 []") && !Leaked("Place 0 [16]"), "R0's summary: not parameter 0, parameter 2, or what is at 16 of parameter 0");
    }

    /// <summary>
    /// A ring of 301 functions, unified, each handing back what the next
    /// does. The last makes a holder, writes A into it at 8 as the field
    /// A::f and B as the field B::g -- two types' fields at one offset of
    /// one class, as unification merges unrelated objects -- loads A::f and
    /// throws it, and hands the holder back. A is global, B is not: B::g is
    /// a field class of its own. Loaded naming no field, the offset is one
    /// field again, and both are global. R0's summary keeps the split: the
    /// holder's cells at 8 name A::f and B::g.
    /// </summary>
    private static void UnifiedFamilies()
    {
        foreach (int loadFamily in new[] { 0, -1 })
        {
            Prog p = new();
            const int ring = 301;
            for (int i = 0; i < ring - 1; i++)
            {
                // Nodes: 0 the parameter, 1 the return, 2 what the next hands back.
                RegionFunction r = p.Add("R" + i, 1, 3);
                r.Calls.Add(new("R" + (i + 1), 2, new[] { 0 }));
                r.Constraints.Add(Copy(1, 2));
            }
            // Nodes: 0 the parameter, 1 the return, 2 the holder, 3 A, 4 B, 5 what is loaded.
            RegionFunction last = p.Add("R" + (ring - 1), 1, 6, sites: 3);
            last.Families = new[] { "A::f", "B::g" };
            last.Constraints.AddRange(new[]
            {
                Site(2, 0), Site(3, 1), Site(4, 2),
                new RegionConstraint(RegionConstraintKind.Store, 2, 3, 8, 0),
                new RegionConstraint(RegionConstraintKind.Store, 2, 4, 8, 1),
                new RegionConstraint(RegionConstraintKind.Load, 5, 2, 8, loadFamily),
                Leak(5),
                Copy(1, 2),
            });
            last.Calls.Add(new("R0", -1, new[] { 0 }));
            RegionFunction caller = p.Add("Caller", 0, 3);
            caller.Calls.Add(new("R0", 2, new[] { 1 }));
            RegionEscape e = p.Solve();
            string how = loadFamily >= 0 ? "loaded as A::f" : "loaded naming no field";
            int a = p.Site("R" + (ring - 1), 1), b = p.Site("R" + (ring - 1), 2);
            Check(e.LargestCycle == ring, how + ": the ring is one cycle of 301");
            Check(e.Global[a], how + ": A is thrown: global");
            Check(e.Global[b] == (loadFamily < 0), how + (loadFamily >= 0 ? ": B, in B::g, is not global" : ": the offset is one field: B is global too"));
            if (loadFamily >= 0)
            {
                var (_, cells, _, _) = e.SummaryOf(p.Index("R0")) ?? throw new Exception("R0 has no summary");
                string?[] names = e.CellFamiliesOf(p.Index("R0")) ?? throw new Exception("R0 has no summary");
                bool Named(string field) => Enumerable.Range(0, cells.Length).Any(k => cells[k].Offset == 8 && names[k] == field);
                Check(Named("B::g"), how + ": R0's summary: the holder's cell at 8 names B::g");
            }
        }
    }

    /// <summary>
    /// Read(p) loads p's field A::f at 8 and throws it: its summary throws
    /// the place below that field, Place 0 [8:A::f]. Caller makes an object
    /// and writes B into it at 8 as the field B::g, and hands it to Read: the
    /// place binds to what the object's A::f holds -- nothing -- so B is not
    /// global. Read loading at 8 naming no field throws whatever is there: B
    /// is global. Caller writing B as A::f itself: B is global. Inclusion
    /// keeps an object's words by field (Graph.Loaded, Stored), and a summary
    /// place's step names the field it was loaded as (TypedStep).
    /// </summary>
    private static void TypedPlaceSteps()
    {
        foreach (var (readAs, writeAs, global) in new[] { (0, 1, false), (-1, 1, true), (0, 0, true) })
        {
            Prog p = new();
            // Nodes: 0 p, 1 the return, 2 what is loaded.
            RegionFunction read = p.Add("Read", 1, 3);
            read.Families = new[] { "A::f", "B::g" };
            read.Constraints.Add(new RegionConstraint(RegionConstraintKind.Load, 2, 0, 8, readAs));
            read.Constraints.Add(Leak(2));
            // Nodes: 0 the return, 1 the object, 2 B.
            RegionFunction caller = p.Add("Caller", 0, 3, sites: 2);
            caller.Families = new[] { "A::f", "B::g" };
            caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), new RegionConstraint(RegionConstraintKind.Store, 1, 2, 8, writeAs) });
            caller.Calls.Add(new("Read", -1, new[] { 1 }));
            RegionEscape e = p.Solve();
            string how = $"read as {(readAs < 0 ? "no field" : read.Families[readAs])}, written as {caller.Families[writeAs]}";
            Check(e.Global[p.Site("Caller", 1)] == global, how + (global ? ": B is global" : ": B is not global"));
            Check(!e.Global[p.Site("Caller", 0)], how + ": the object itself is not global");
        }
    }

    /// <summary>
    /// T0 throws its argument; T1 to T17 do nothing. C1(p) makes a virtual
    /// call of T0 to T16 (17 targets, past WideTargets 16: a wide call, a
    /// stand-in) with p; D hands its object to C1: T0 may run, so D's object
    /// is global. C2 hands its object to a call of T1 to T17: none keeps it.
    ///
    /// Callers first, C1 is solved before T0 and applies a stand-in that
    /// does nothing; T0's summary grows it as T0 is published, and the next
    /// round solves again only what applied it before, and what that
    /// changed. Targets first, the stand-in is made covering them all, and
    /// a second round finds nothing to solve again. Either way the answers
    /// must be those of following every call in order (WideTargets 0),
    /// every one of them.
    /// </summary>
    private static void WideCalls()
    {
        Prog Build(bool callersFirst)
        {
            Prog p = new();
            void Targets()
            {
                p.Add("T0", 1, 2).Constraints.Add(Leak(0));
                for (int t = 1; t <= 17; t++) p.Add("T" + t, 1, 2);
            }
            if (!callersFirst) Targets();
            p.Virtuals["__virtual:t_A+48"] = Enumerable.Range(0, 17).Select(t => "T" + t).ToArray();
            p.Virtuals["__virtual:t_B+48"] = Enumerable.Range(1, 17).Select(t => "T" + t).ToArray();
            p.Add("C1", 1, 2).Calls.Add(new("__virtual:t_A+48", -1, new[] { 0 }));
            RegionFunction c2 = p.Add("C2", 0, 2, sites: 1);
            c2.Constraints.Add(Site(1, 0));
            c2.Calls.Add(new("__virtual:t_B+48", -1, new[] { 1 }));
            RegionFunction d = p.Add("D", 0, 2, sites: 1);
            d.Constraints.Add(Site(1, 0));
            d.Calls.Add(new("C1", -1, new[] { 1 }));
            if (callersFirst) Targets();
            return p;
        }
        foreach (bool callersFirst in new[] { true, false })
        {
            string how = callersFirst ? "callers first" : "targets first";
            Prog rounds = Build(callersFirst), whole = Build(callersFirst);
            List<string> said = new();
            RegionEscape byRounds = rounds.Solve(wide: 16, progress: said.Add);
            RegionEscape inOrder = whole.Solve(wide: 0);
            if (callersFirst)
                Check(said.Any(line => line.Contains("round 2:", StringComparison.Ordinal) && !line.Contains("nothing to solve again", StringComparison.Ordinal)),
                    how + ": the stand-in grew after C1 applied it, and a second round solved C1 again");
            else
                Check(said.Any(line => line.Contains("round 2: nothing to solve again", StringComparison.Ordinal)),
                    how + ": the stand-in was made covering its targets, and nothing was solved again");
            Check(byRounds.Global[rounds.Site("D", 0)], how + ": D's object, handed to a call that may run T0, is global");
            Check(!byRounds.Global[rounds.Site("C2", 0)], how + ": C2's object, handed to targets that keep nothing, is not");
            Check(byRounds.Global.SequenceEqual(inOrder.Global), how + ": what is global is the same by rounds as following every call");
            int sites = byRounds.Global.Length;
            for (int f = 0; f < rounds.Functions.Count; f++)
                for (int site = 0; site < sites; site++)
                    Check(byRounds.Escapes(f, site) == inOrder.Escapes(f, site), $"{how}: {rounds.Functions[f].Name}: site {site} outlives it the same by rounds as in order");
        }
    }

    /// <summary>
    /// T0 makes an object and leaks it (as a throw does); T1 to T16 each
    /// make one and hand it back. C makes a call of all seventeen (a wide
    /// call, a stand-in) and drops what comes back; E calls C. T0's object
    /// is global, and nothing else is: what T1 to T16 make is handed back
    /// to C and dropped there. One class for everything the targets make
    /// said the unknown object held all of it, and every target's object
    /// was global for T0's sake; held apart (RegionEscape.HeldClass), the
    /// answers are those of following every call, which keep each
    /// target's objects its own.
    /// </summary>
    private static void WideHeldApart()
    {
        Prog Build()
        {
            Prog p = new();
            RegionFunction t0 = p.Add("T0", 1, 3, sites: 1);
            t0.Constraints.Add(Site(2, 0));
            t0.Constraints.Add(Leak(2));
            for (int t = 1; t <= 16; t++) p.Add("T" + t, 1, 2, sites: 1).Constraints.Add(Site(1, 0));
            p.Virtuals["__virtual:t_A+48"] = Enumerable.Range(0, 17).Select(t => "T" + t).ToArray();
            p.Add("C", 1, 3).Calls.Add(new("__virtual:t_A+48", 2, new[] { 0 }));
            RegionFunction e = p.Add("E", 0, 2, sites: 1);
            e.Constraints.Add(Site(1, 0));
            e.Calls.Add(new("C", -1, new[] { 1 }));
            return p;
        }
        Prog rounds = Build(), whole = Build();
        RegionEscape byRounds = rounds.Solve(wide: 16);
        RegionEscape inOrder = whole.Solve(wide: 0);
        Check(byRounds.Global[rounds.Site("T0", 0)], "T0's object, leaked, is global");
        for (int t = 1; t <= 16; t++)
            Check(!byRounds.Global[rounds.Site("T" + t, 0)], $"T{t}'s object, handed back and dropped, is not global for T0's leak");
        Check(!byRounds.Global[rounds.Site("E", 0)], "E's object, handed to targets that keep nothing of it, is not");
        Check(byRounds.Global.SequenceEqual(inOrder.Global), "what is global is the same by rounds as following every call");
    }

    /// <summary>
    /// Make is the same body in two units (0 and 1): it makes an object and
    /// hands it back. A third Make (2) hands back nothing: another body of
    /// the same name. Use calls copy 1 and throws what it hands back; Keep
    /// calls copy 0 and keeps what it hands back to itself. Copy 1 is
    /// solved as copy 0 (one body), so its site is global as copy 0's is:
    /// what any copy's caller does, every copy's sites answer. Make 2's
    /// site is no copy's and nobody throws it: not global. Both copies
    /// answer alike what outlives them (their object, handed back), and the
    /// report says one function was solved as another copy.
    /// </summary>
    private static void Copies()
    {
        RegionSite[] One() => new[] { new RegionSite(true, 1, null, 0) };
        RegionFunction Make()
        {
            // Nodes: 0 the return, 1 the object.
            RegionFunction f = new("Make", false, true, false, 0, 2, 0, One());
            f.Constraints.Add(Site(1, 0));
            f.Constraints.Add(Copy(0, 1));
            return f;
        }
        RegionFunction other = new("Make", false, true, false, 0, 2, 0, One());
        other.Constraints.Add(Site(1, 0));
        RegionFunction thrower = new("Throw", true, true, false, 1, 2, 0, Array.Empty<RegionSite>());
        thrower.Constraints.Add(Leak(0));
        // Nodes: 0 the return, 1 what Make handed back.
        RegionFunction use = new("Use", true, true, false, 0, 2, 0, Array.Empty<RegionSite>());
        use.Calls.Add(new("Make", 1, Array.Empty<int>()));
        use.Calls.Add(new("Throw", -1, new[] { 1 }));
        RegionFunction keep = new("Keep", true, true, false, 0, 2, 0, Array.Empty<RegionSite>());
        keep.Calls.Add(new("Make", 1, Array.Empty<int>()));
        RegionFunction[] functions = { Make(), Make(), other, thrower, use, keep };
        int[]?[][] targets =
        {
            Array.Empty<int[]?>(), Array.Empty<int[]?>(), Array.Empty<int[]?>(), Array.Empty<int[]?>(),
            new int[]?[] { new[] { 1 }, new[] { 3 } },
            new int[]?[] { new[] { 0 } },
        };
        string?[][] keys = functions.Select(f => new string?[f.Calls.Count]).ToArray();
        int[] siteBase = { 0, 1, 2, 3, 3, 3 };
        List<string> said = new();
        RegionEscape escape = new(functions, targets, keys, siteBase, 3, Enumerable.Repeat(true, 6).ToArray(), new[] { false, false, false, false, true, true })
        {
            WideTargets = 16, Progress = said.Add,
        };
        escape.Run();
        Check(escape.Global[1], "copy 1's object, thrown by Use, is global");
        Check(escape.Global[0], "copy 0's object is global as copy 1's is: one body");
        Check(!escape.Global[2], "the other Make's object is nobody's to throw");
        Check(escape.Escapes(0, 0) && escape.Escapes(1, 1), "each copy's object outlives it, handed back");
        Check(said.Any(line => line.Contains("1 solved as another copy", StringComparison.Ordinal)), "one function was solved as another copy of its body");
    }

    /// <summary>
    /// Known writes its object into a constant's address, which the link
    /// found a constant (ConstantsKnown): a constant holds nothing anyone
    /// follows, so the object is dead by its return. Unknown does the same
    /// where the symbol was not judged: the unknown object, and the object
    /// written into it is global.
    /// </summary>
    private static void Constants()
    {
        Prog p = new();
        foreach (var (name, known) in new[] { ("Known", true), ("Unknown", false) })
        {
            RegionFunction f = new(name, true, true, false, 0, 3, 0, new[] { new RegionSite(true, 1, null, 0) }) { Symbols = new[] { "s_Table" } };
            f.ConstantsKnown = known;
            f.Constraints.AddRange(new[] { new RegionConstraint(RegionConstraintKind.Symbol, 1, 0, 0), Site(2, 0), Store(1, 2, 8) });
            p.Functions.Add(f);
        }
        RegionEscape e = p.Solve();
        Check(!e.Global[p.Site("Known", 0)] && !e.Escapes(p.Index("Known"), p.Site("Known", 0)), "Known: written into a constant, it is dead by its return");
        Check(e.Global[p.Site("Unknown", 0)], "Unknown: written into an unjudged symbol's address, it is global");
    }

    /// <summary>
    /// Number and Pointer each throw their parameter; Number's is a number
    /// (NumberParams). What a call hands a number parameter is no address,
    /// so CallNumber's object is handed nothing and stays; CallPointer's is
    /// thrown, and global.
    /// </summary>
    private static void NumberParameters()
    {
        Prog p = new();
        RegionFunction number = new("Number", true, true, false, 1, 2, 0, Array.Empty<RegionSite>()) { NumberParams = new[] { 0 } };
        number.Constraints.Add(Leak(0));
        p.Functions.Add(number);
        p.Add("Pointer", 1, 2).Constraints.Add(Leak(0));
        foreach (var (caller, callee) in new[] { ("CallNumber", "Number"), ("CallPointer", "Pointer") })
        {
            RegionFunction c = p.Add(caller, 0, 2, sites: 1);
            c.Constraints.Add(Site(1, 0));
            c.Calls.Add(new(callee, -1, new[] { 1 }));
        }
        RegionEscape e = p.Solve();
        Check(!e.Global[p.Site("CallNumber", 0)], "an object handed where a number is taken is not handed over");
        Check(e.Global[p.Site("CallPointer", 0)], "an object handed to a parameter that is thrown is global");
    }

    /// <summary>
    /// F(p, t) makes O (site 0) and S (site 1). It writes S into p's field
    /// Instr::Value at 16, and O into t's field TypeSymbol::Cache at 24, so
    /// O is reached from a place, and a caller may reach it by another way:
    /// what F writes into places may be in it (Aliased). It then reads O's
    /// field TypeSymbol::Name, also at 16, and throws what it read. Typed,
    /// that load is no field S was written into -- one offset of one object
    /// is one field -- so S is not global. Untyped, the same load reads
    /// everything written into places, S among it: global. And a load of
    /// Instr::Value from O reads it.
    /// </summary>
    private static void TypedAliasing()
    {
        RegionEscape Solve(int loadFamily)
        {
            Prog p = new();
            RegionFunction f = p.Add("F", 2, 6, sites: 2);
            f.Families = new[] { "Instr::Value", "TypeSymbol::Cache", "TypeSymbol::Name" };
            f.Constraints.AddRange(new[]
            {
                Site(3, 0), Site(5, 1),
                new RegionConstraint(RegionConstraintKind.Store, 0, 5, 16, 0),
                new RegionConstraint(RegionConstraintKind.Store, 1, 3, 24, 1),
                new RegionConstraint(RegionConstraintKind.Load, 4, 3, 16, loadFamily),
                Leak(4),
            });
            return p.Solve();
        }
        Prog shape = new();
        shape.Add("F", 2, 6, sites: 2);
        int s = shape.Site("F", 1);
        Check(!Solve(2).Global[s], "a load of TypeSymbol::Name reads nothing written into Instr::Value: S is not global");
        Check(Solve(-1).Global[s], "an untyped load reads what is written into places: S is global");
        Check(Solve(0).Global[s], "a load of Instr::Value reads what was written into it: S is global");
    }

    /// <summary>
    /// TypedAliasing with the write in a callee: G(p, v) writes v into p's
    /// field Instr::Value at 16; F hands G its parameter and S, and reads
    /// O's field TypeSymbol::Name at 16 as before. G's summary says the word
    /// it wrote was Instr::Value (Summary.Fields), and F, applying it, writes
    /// S as that field: S is not global. G's write untyped, it is.
    /// </summary>
    private static void TypedAliasingThroughSummary()
    {
        RegionEscape Solve(int storeFamily)
        {
            Prog p = new();
            RegionFunction g = p.Add("G", 2, 3);
            g.Families = new[] { "Instr::Value" };
            g.Constraints.Add(new RegionConstraint(RegionConstraintKind.Store, 0, 1, 16, storeFamily));
            RegionFunction f = p.Add("F", 2, 6, sites: 2);
            f.Families = new[] { "TypeSymbol::Cache", "TypeSymbol::Name" };
            f.Constraints.AddRange(new[]
            {
                Site(3, 0), Site(5, 1),
                new RegionConstraint(RegionConstraintKind.Store, 1, 3, 24, 0),
                new RegionConstraint(RegionConstraintKind.Load, 4, 3, 16, 1),
                Leak(4),
            });
            f.Calls.Add(new("G", -1, new[] { 0, 5 }));
            return p.Solve();
        }
        Prog shape = new();
        shape.Add("G", 2, 3);
        shape.Add("F", 2, 6, sites: 2);
        int s = shape.Site("F", 1);
        Check(!Solve(0).Global[s], "G writes S as Instr::Value: F's load of TypeSymbol::Name does not read it");
        Check(Solve(-1).Global[s], "G's write untyped: F's load reads it, and S is global");
    }

    /// <summary>
    /// A virtual call of LeakThis (throws `this`) or KeepThis (does
    /// nothing). Caller's sites 0 and 2 are of a class that runs KeepThis,
    /// site 1 of one that runs LeakThis (TargetsOn). Pass(p) makes the call
    /// on its parameter, so its summary throws p only as far as p is an
    /// object that runs LeakThis (a guarded place); Caller hands Pass sites 0
    /// and 1, and makes the call itself on site 2. Only site 1 can run
    /// LeakThis: only it is global. Asked with no knowledge of what runs
    /// where, any of them may run LeakThis, and all three are global.
    /// </summary>
    private static void Guards()
    {
        Prog Build()
        {
            Prog p = new();
            p.Add("LeakThis", 1, 2).Constraints.Add(Leak(0));
            p.Add("KeepThis", 1, 2);
            p.Virtuals["__virtual:t_X+48"] = new[] { "LeakThis", "KeepThis" };
            p.Add("Pass", 1, 2).Calls.Add(new("__virtual:t_X+48", -1, new[] { 0 }));
            RegionFunction caller = p.Add("Caller", 0, 4, sites: 3);
            caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), Site(3, 2) });
            caller.Calls.Add(new("Pass", -1, new[] { 1 }));
            caller.Calls.Add(new("Pass", -1, new[] { 2 }));
            caller.Calls.Add(new("__virtual:t_X+48", -1, new[] { 3 }));
            return p;
        }
        Prog known = Build();
        int leakThis = known.Index("LeakThis"), keepThis = known.Index("KeepThis");
        int keeper0 = known.Site("Caller", 0), leaker = known.Site("Caller", 1), keeper2 = known.Site("Caller", 2);
        int[]? Runs(int f, int k, int site) => site == leaker ? new[] { leakThis } : site == keeper0 || site == keeper2 ? new[] { keepThis } : null;
        RegionEscape e = known.Solve(targetsOn: Runs);
        Check(!e.Global[keeper0], "site 0, handed to Pass, runs KeepThis: not global");
        Check(e.Global[leaker], "site 1, handed to Pass, runs LeakThis: global");
        Check(!e.Global[keeper2], "site 2, called on directly, runs KeepThis: not global");
        Prog blind = Build();
        RegionEscape any = blind.Solve(targetsOn: null);
        Check(any.Global[keeper0] && any.Global[leaker] && any.Global[keeper2], "with no knowledge of what runs where, every receiver may run LeakThis");
    }

    /// <summary>
    /// Guards' program with a wide call: LeakThis (throws `this`) and
    /// sixteen KeepThis that do nothing, seventeen targets, past WideTargets
    /// (16), so a stand-in. Pass(p) makes the call on its parameter. A
    /// stand-in of all seventeen has the unknown object hold the receiver,
    /// and every object handed to Pass was global. Deferred, LeakThis alone
    /// is applied to p guarded by it, and the rest to p as it is: the
    /// caller's site of a class that runs LeakThis is global, the one of a
    /// class that runs KeepThis1 is not, and nor is one called on directly.
    /// With no knowledge of what runs where, every receiver may run
    /// LeakThis, and all are global.
    /// </summary>
    private static void WideDeferred()
    {
        Prog Build()
        {
            Prog p = new();
            p.Add("LeakThis", 1, 2).Constraints.Add(Leak(0));
            for (int t = 1; t <= 16; t++) p.Add("KeepThis" + t, 1, 2);
            p.Virtuals["__virtual:t_X+48"] = new[] { "LeakThis" }.Concat(Enumerable.Range(1, 16).Select(t => "KeepThis" + t)).ToArray();
            p.Add("Pass", 1, 2).Calls.Add(new("__virtual:t_X+48", -1, new[] { 0 }));
            RegionFunction caller = p.Add("Caller", 0, 4, sites: 3);
            caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), Site(3, 2) });
            caller.Calls.Add(new("Pass", -1, new[] { 1 }));
            caller.Calls.Add(new("Pass", -1, new[] { 2 }));
            caller.Calls.Add(new("__virtual:t_X+48", -1, new[] { 3 }));
            return p;
        }
        Prog known = Build();
        int leakThis = known.Index("LeakThis"), keepThis = known.Index("KeepThis1");
        int keeper0 = known.Site("Caller", 0), leaker = known.Site("Caller", 1), keeper2 = known.Site("Caller", 2);
        int[]? Runs(int f, int k, int site) => site == leaker ? new[] { leakThis } : site == keeper0 || site == keeper2 ? new[] { keepThis } : null;
        RegionEscape e = known.Solve(targetsOn: Runs);
        Check(!e.Global[keeper0], "site 0, handed to Pass, runs KeepThis1: not global");
        Check(e.Global[leaker], "site 1, handed to Pass, runs LeakThis: global");
        Check(!e.Global[keeper2], "site 2, called on directly, runs KeepThis1: not global");
        Prog blind = Build();
        RegionEscape any = blind.Solve(targetsOn: null);
        Check(any.Global[keeper0] && any.Global[leaker] && any.Global[keeper2], "with no knowledge of what runs where, every receiver may run LeakThis");
    }

    /// <summary>
    /// A call of twelve targets (past MostGuarded, under WideTargets: one of
    /// its own) on what a list of many classes holds: LeakThis throws its
    /// receiver, Keep1 to Keep11 keep nothing. Many makes eleven objects into
    /// one node -- as elements loaded out of a list are -- and calls on it:
    /// sites 0 to 9 each of a class that runs one KeepN of its own, site 10
    /// of one that runs LeakThis. Ten sets of targets past the eighth group
    /// (MostGroups) went to the whole call, LeakThis among it, and sites 8
    /// and 9 were thrown with site 10. Now, with the groups full, the call
    /// keeps LeakThis apart: only site 10 is global. A wide call (WideTargets
    /// 4 here) answers the same.
    /// </summary>
    private static void GroupsFull()
    {
        foreach (int wide in new[] { 16, 4 })
        {
            Prog p = new();
            p.Add("LeakThis", 1, 2).Constraints.Add(Leak(0));
            for (int t = 1; t <= 11; t++) p.Add("Keep" + t, 1, 2);
            p.Virtuals["__virtual:t_L+48"] = new[] { "LeakThis" }.Concat(Enumerable.Range(1, 11).Select(t => "Keep" + t)).ToArray();
            RegionFunction many = p.Add("Many", 0, 2, sites: 11);
            for (int s = 0; s < 11; s++) many.Constraints.Add(Site(1, s));
            many.Calls.Add(new("__virtual:t_L+48", -1, new[] { 1 }));
            int leakThis = p.Index("LeakThis");
            int[] site = Enumerable.Range(0, 11).Select(s => p.Site("Many", s)).ToArray();
            int[]? Runs(int f, int k, int at)
            {
                int s = Array.IndexOf(site, at);
                return s < 0 ? null : s == 10 ? new[] { leakThis } : new[] { p.Index("Keep" + (s + 1)) };
            }
            RegionEscape e = p.Solve(wide: wide, targetsOn: Runs);
            string how = wide == 16 ? "a call of its own" : "a wide call";
            for (int s = 0; s < 10; s++) Check(!e.Global[site[s]], $"{how}: site {s} runs Keep{s + 1} only: not global");
            Check(e.Global[site[10]], how + ": site 10 runs LeakThis: global");
        }
    }

    /// <summary>
    /// A call on a saturated node's blob runs what its members' classes run,
    /// not every target. Full's node 1 holds 257 objects of classes that run
    /// Keep1 (sites 0 to 255) and Keep2 (256), and then, loaded out of a box
    /// (site 258), site 257: past MostHeld, so it joins the node's blob, and
    /// possibly after the call on node 1 was dispatched on the blob (then it
    /// is dispatched again, Redispatch). Site 257 runs Keep3: nothing is
    /// global. Where it runs LeakThis instead, it is thrown -- the blob is --
    /// and nothing else is. A blob with a member of no known class still
    /// takes every target: with no knowledge of classes, site 257 is global.
    /// The same for a wide call (WideTargets 4).
    /// </summary>
    private static void BlobDispatch()
    {
        foreach (int wide in new[] { 16, 4 })
            foreach (string member in new[] { "Keep3", "LeakThis", "unknown" })
            {
                Prog p = new();
                p.Add("LeakThis", 1, 2).Constraints.Add(Leak(0));
                for (int t = 1; t <= 11; t++) p.Add("Keep" + t, 1, 2);
                p.Virtuals["__virtual:t_B+48"] = new[] { "LeakThis" }.Concat(Enumerable.Range(1, 11).Select(t => "Keep" + t)).ToArray();
                // Nodes: 0 the return, 1 the receiver, 2 the box, 3 site 257's object.
                RegionFunction full = p.Add("Full", 0, 4, sites: 259);
                for (int s = 0; s <= 256; s++) full.Constraints.Add(Site(1, s));
                full.Constraints.Add(Site(2, 258));
                full.Constraints.Add(Site(3, 257));
                full.Constraints.Add(Store(2, 3, 8));
                full.Constraints.Add(Load(1, 2, 8));
                full.Calls.Add(new("__virtual:t_B+48", -1, new[] { 1 }));
                int first = p.Site("Full", 0);
                int[]? Runs(int f, int k, int at)
                {
                    int s = at - first;
                    if (s < 256) return new[] { p.Index("Keep1") };
                    if (s == 256) return new[] { p.Index("Keep2") };
                    if (s == 257) return member == "unknown" ? null : new[] { p.Index(member) };
                    return null;
                }
                RegionEscape e = p.Solve(wide: wide, targetsOn: Runs);
                string how = (wide == 16 ? "a call of its own" : "a wide call") + ", site 257 running " + member;
                for (int s = 0; s <= 256; s++) Check(!e.Global[first + s], $"{how}: site {s} runs a keeper: not global");
                Check(e.Global[first + 257] == (member != "Keep3"), how + (member == "Keep3" ? ": the blob runs Keep3 only: not global" : ": the blob may run LeakThis: global"));
            }
    }

    /// <summary>
    /// Fifty nodes in a chain of copies, each past MostHeld: node 1 holds 258
    /// sites, each next node one site of its own and all the last one holds,
    /// so every node saturates and the blob of each joins the blob of the
    /// next. Each join is linked from the solve's loop, not inside the Add
    /// that made it (Link): linked in place, a chain of joins as deep as the
    /// compiler's own link ran the stack out. Thrown at its end, every site
    /// along it is global, through the blobs; kept, none is.
    /// </summary>
    private static void BlobChain()
    {
        const int Nodes = 50;
        foreach (bool thrown in new[] { false, true })
        {
            Prog p = new();
            // Nodes: 0 the return, 1 to Nodes the chain.
            RegionFunction chain = p.Add("Chain", 0, Nodes + 1, sites: 258 + Nodes - 1);
            for (int s = 0; s < 258; s++) chain.Constraints.Add(Site(1, s));
            for (int n = 2; n <= Nodes; n++)
            {
                chain.Constraints.Add(Site(n, 258 + n - 2));
                chain.Constraints.Add(Copy(n, n - 1));
            }
            if (thrown) chain.Constraints.Add(Leak(Nodes));
            RegionEscape e = p.Solve();
            int sites = 258 + Nodes - 1;
            for (int s = 0; s < sites; s++)
                Check(e.Global[p.Site("Chain", s)] == thrown, $"{(thrown ? "thrown" : "kept")}: site {s} {(thrown ? "is" : "is not")} global");
        }
    }

    /// <summary>
    /// WideDeferred's call one level further down: Outer(p) hands its
    /// parameter to Pass(q), which makes the wide call on q. Pass's summary
    /// throws q guarded by LeakThis; Outer's throws p guarded so in turn,
    /// the guard carried up (Filtered); Caller hands Outer an object of a
    /// class that runs KeepThis1 and one of a class that runs LeakThis, and
    /// only the second is global.
    /// </summary>
    private static void WideDeferredTwoLevels()
    {
        Prog p = new();
        p.Add("LeakThis", 1, 2).Constraints.Add(Leak(0));
        for (int t = 1; t <= 16; t++) p.Add("KeepThis" + t, 1, 2);
        p.Virtuals["__virtual:t_X+48"] = new[] { "LeakThis" }.Concat(Enumerable.Range(1, 16).Select(t => "KeepThis" + t)).ToArray();
        p.Add("Pass", 1, 2).Calls.Add(new("__virtual:t_X+48", -1, new[] { 0 }));
        p.Add("Outer", 1, 2).Calls.Add(new("Pass", -1, new[] { 0 }));
        RegionFunction caller = p.Add("Caller", 0, 3, sites: 2);
        caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1) });
        caller.Calls.Add(new("Outer", -1, new[] { 1 }));
        caller.Calls.Add(new("Outer", -1, new[] { 2 }));
        int leakThis = p.Index("LeakThis"), keepThis = p.Index("KeepThis1");
        int keeper = p.Site("Caller", 0), leaker = p.Site("Caller", 1);
        int[]? Runs(int f, int k, int site) => site == leaker ? new[] { leakThis } : site == keeper ? new[] { keepThis } : null;
        RegionEscape e = p.Solve(targetsOn: Runs);
        Check(!e.Global[keeper], "an object that runs KeepThis1, handed down two calls: not global");
        Check(e.Global[leaker], "an object that runs LeakThis, handed down two calls: global");
    }

    /// <summary>
    /// A node may hold MostHeld (256) made objects; the 257th, and all it is
    /// given after, join the node's blob, which it holds for them. Fill257's
    /// node holds 257 sites and goes nowhere: none is global, as Fill256's
    /// 256 are not. Leak257's node holds 257 and is thrown: the 256 it holds
    /// and, through its blob, the 257th are all global. Hand257 hands its
    /// node back: every one outlives it, the blob's member too.
    /// </summary>
    private static void Saturation()
    {
        Prog p = new();
        RegionFunction over = p.Add("Fill257", 0, 2, sites: 257);
        for (int s = 0; s < 257; s++) over.Constraints.Add(Site(1, s));
        RegionFunction under = p.Add("Fill256", 0, 2, sites: 256);
        for (int s = 0; s < 256; s++) under.Constraints.Add(Site(1, s));
        RegionFunction leak = p.Add("Leak257", 0, 2, sites: 257);
        for (int s = 0; s < 257; s++) leak.Constraints.Add(Site(1, s));
        leak.Constraints.Add(Leak(1));
        RegionFunction hand = p.Add("Hand257", 0, 2, sites: 257);
        for (int s = 0; s < 257; s++) hand.Constraints.Add(Site(1, s));
        hand.Constraints.Add(Copy(0, 1));
        RegionEscape e = p.Solve();
        for (int s = 0; s < 257; s++) Check(!e.Global[p.Site("Fill257", s)], $"Fill257: site {s} stays, the 257th in the node's blob");
        for (int s = 0; s < 256; s++) Check(!e.Global[p.Site("Fill256", s)], $"Fill256: site {s} stays");
        for (int s = 0; s < 257; s++) Check(e.Global[p.Site("Leak257", s)], $"Leak257: site {s} is thrown, the 257th through the blob");
        for (int s = 0; s < 257; s++) Check(e.Escapes(p.Index("Hand257"), p.Site("Hand257", s)) && !e.Global[p.Site("Hand257", s)], $"Hand257: site {s} is handed back, not global");
    }

    /// <summary>
    /// Many writes each of its 20 parameters into each other one, parameter
    /// i at offset 8i: 380 cells, past MostCells (128). Made coarse, each
    /// parameter is still its own place and the cells still 380 pairs: past
    /// the bound again, so the summary is everything -- the unknown object,
    /// one deep place past every parameter, holding itself anywhere, and
    /// nothing made or handed back.
    /// </summary>
    private static void MostCells()
    {
        Prog p = new();
        RegionFunction many = p.Add("Many", 20, 21);
        for (int i = 0; i < 20; i++)
            for (int j = 0; j < 20; j++)
                if (i != j) many.Constraints.Add(Store(j, i, 8 * i));
        RegionEscape e = p.Solve();
        var (objects, cells, result, coarse) = e.SummaryOf(p.Index("Many")) ?? throw new Exception("Many has no summary");
        Check(objects.SequenceEqual(new[] { "Unknown -1 []", "Deep -1 []" }), "Many: the unknown object and one deep place past every parameter, as " + string.Join("; ", objects));
        Check(cells.SequenceEqual(new[] { (1, RegionEscape.Any, 1, RegionEscape.Any) }), "Many: the deep place holds itself, anywhere");
        Check(result.Length == 0 && coarse, "Many: hands nothing back, and was made coarse");
    }

    /// <summary>
    /// Put(x, box) writes x into box at 8. One calls Put with x alone: the
    /// box is whatever its word holds -- the unknown object -- so x is
    /// written where nobody follows, and global. Two hands a box of its
    /// own: x is in Two's box, and neither is global.
    /// </summary>
    private static void Unpassed()
    {
        Prog p = new();
        p.Add("Put", 2, 3).Constraints.Add(Store(1, 0, 8));
        RegionFunction one = p.Add("One", 0, 2, sites: 1);
        one.Constraints.Add(Site(1, 0));
        one.Calls.Add(new("Put", -1, new[] { 1 }));
        RegionFunction two = p.Add("Two", 0, 3, sites: 2);
        two.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1) });
        two.Calls.Add(new("Put", -1, new[] { 1, 2 }));
        RegionEscape e = p.Solve();
        Check(e.Global[p.Site("One", 0)], "One: written into a box nobody passed, it is global");
        Check(!e.Global[p.Site("Two", 0)] && !e.Global[p.Site("Two", 1)], "Two: written into its own box, neither is global");
    }

    /// <summary>
    /// Root and Plain each hand back what they make. Root is called from
    /// where nobody follows (rooted): what it hands back is everyone's, and
    /// global. Plain's is only its callers', and not.
    /// </summary>
    private static void Rooted()
    {
        Prog p = new();
        foreach (string name in new[] { "Root", "Plain" })
        {
            RegionFunction f = p.Add(name, 0, 2, sites: 1);
            f.Constraints.AddRange(new[] { Site(1, 0), Copy(0, 1) });
        }
        RegionEscape e = p.Solve(rooted: new[] { "Root" });
        Check(e.Global[p.Site("Root", 0)], "Root: what a function called from anywhere hands back is global");
        Check(!e.Global[p.Site("Plain", 0)], "Plain: what it hands back is not global");
    }

    /// <summary>
    /// Every field the hints carry, written, read back and written again:
    /// the same bytes, at the format's version 11, and each field as it was.
    /// </summary>
    private static void HintsRoundTrip()
    {
        RegionHints hints = new() { WordSize = 8 };
        hints.AddressTaken.Add("Hook");
        hints.MethodsTaken.Add("Work");
        hints.CallsThroughMethods = true;
        RegionFunction f = new("Work", true, true, true, 2, 6, 1,
            new[] { new RegionSite(true, 7, "t_T", 48, RegionWords.Described), new RegionSite(false, 9, null, 0, RegionWords.Leaf) })
        {
            Main = false, NumberParams = new[] { 1 }, Symbols = new[] { "s_Table" }, Families = new[] { "Node::Next", "Sym::Name" },
            CallsThroughMethod = true, BlindSlots = new[] { RegionConstraint.Any, 4L, 12L },
        };
        f.Constraints.AddRange(new[]
        {
            Site(3, 0), Site(4, 1), new RegionConstraint(RegionConstraintKind.Slot, 5, 0, 0), new RegionConstraint(RegionConstraintKind.Unknown, 5, 0, 0),
            Copy(2, 3), Load(4, 0, 16), Store(0, 3, 8), new RegionConstraint(RegionConstraintKind.MemCopy, 3, 4, 24), Leak(4),
            new RegionConstraint(RegionConstraintKind.Load, 4, 0, 24, 1), new RegionConstraint(RegionConstraintKind.Store, 0, 3, 32, 0),
            new RegionConstraint(RegionConstraintKind.Symbol, 5, 0, 0), new RegionConstraint(RegionConstraintKind.Copy, 2, 4, RegionConstraint.IndexScaled(3)),
        });
        f.Calls.Add(new("Hook", 4, new[] { 3, -1 }));
        f.Calls.Add(new(null, -1, new[] { 4 }));
        f.MustCalls = new[] { 0 };
        f.MustSites = new[] { 0, 1 };
        f.Loops.Add(new RegionLoopShape(2, new[] { 1 }, new[] { 1 }, new[] { 1 }, Array.Empty<int>(), new[] { 4 }, new[] { 3 }, new[] { 0 }));
        f.Repeats = new[] { new RegionRepeat(2, -1, 10), new RegionRepeat(4, 0, 0) };
        f.SiteBytes = new[] { 32L, 0L };
        f.SiteLoops = new[] { -1, 1 };
        f.CallLoops = new[] { RegionFunction.Throwing, 0 };
        hints.Functions.Add(f);
        RegionFunction main = new("Main", true, false, false, 0, 1, 0, Array.Empty<RegionSite>()) { Main = true };
        main.Calls.Add(new("Work", -1, Array.Empty<int>()));
        hints.Functions.Add(main);

        byte[] bytes = hints.Write();
        Check(BitConverter.ToInt32(bytes, 4) == 11, "the hints are written at version 11");
        RegionHints again = RegionHints.Read(bytes);
        Check(again.Write().AsSpan().SequenceEqual(bytes), "read back, the hints write the same bytes");
        RegionFunction w = again.Functions.Single(x => x.Name == "Work");
        Check(again.WordSize == 8 && again.AddressTaken.SetEquals(new[] { "Hook" }), "the word size and the addresses taken");
        Check(again.MethodsTaken.SetEquals(new[] { "Work" }) && again.CallsThroughMethods, "the methods only descriptors name, and that the unit calls one blind");
        hints.MethodsTaken.Clear(); hints.CallsThroughMethods = false;
        RegionHints none = RegionHints.Read(hints.Write());
        Check(none.MethodsTaken.Count == 0 && !none.CallsThroughMethods, "no methods taken and no blind call, read back so");
        Check(w.Instance && !w.Main && w.Parameters == 2 && w.Nodes == 6 && w.Slots == 1, "the function's shape");
        Check(w.NumberParams.SequenceEqual(new[] { 1 }) && w.Symbols.SequenceEqual(new[] { "s_Table" }), "its number parameters and symbols");
        Check(w.Families.SequenceEqual(new[] { "Node::Next", "Sym::Name" }), "the fields its loads and stores name");
        Check(w.CallsThroughMethod && w.BlindSlots.SequenceEqual(new[] { RegionConstraint.Any, 4L, 12L }), "that it calls a method read out of a descriptor, and the slots it reads");
        Check(w.Sites[0] == new RegionSite(true, 7, "t_T", 48, RegionWords.Described) && w.Sites[1] == new RegionSite(false, 9, null, 0, RegionWords.Leaf), "its sites");
        Check(w.Constraints.SequenceEqual(f.Constraints), "its constraints, every kind");
        Check(w.Calls.Count == 2 && w.Calls[0].Callee == "Hook" && w.Calls[0].Arguments.SequenceEqual(new[] { 3, -1 }) && w.Calls[1].Callee is null, "its calls");
        Check(w.MustCalls.SequenceEqual(new[] { 0 }) && w.MustSites.SequenceEqual(new[] { 0, 1 }), "what runs on every way to a return");
        Check(w.Loops.Single() is { Header: 2 } loop && loop.Live.SequenceEqual(new[] { 4 }) && loop.Invariant.SequenceEqual(new[] { 3 }) && loop.KeptSlots.SequenceEqual(new[] { 0 }), "its loop");
        Check(w.Repeats.SequenceEqual(f.Repeats) && w.SiteBytes.SequenceEqual(f.SiteBytes) && w.SiteLoops.SequenceEqual(f.SiteLoops) && w.CallLoops.SequenceEqual(f.CallLoops), "how often each site and call runs");
        Check(again.Functions.Single(x => x.Name == "Main").Main, "Main reads back as Main");
    }

    /// <summary>
    /// The facts a backend is handed, with an owned field mapped and freed
    /// by its collection, its borrowers and elements, storage made beside,
    /// and regions with their sizes: read back, the same in every part.
    /// </summary>
    private static void BackendRoundTrip()
    {
        OwnedFieldFacts owned = new() { Beside = true };
        owned.Fields["T::list"] = 8; owned.Fields["T::name"] = 16;
        owned.Mapped.Add("T::list");
        owned.SelfFreed.Add("T::list");
        owned.Borrowers.Add("m_T_get_List_0");
        owned.Elements["T::list"] = "list";
        owned.ElementCallees[("m_T_Fill_1", 1)] = "T::list";
        owned.ElementKept.Add("T::name");
        RegionFacts regions = new();
        regions.Boundaries.Add("Work"); regions.Boundaries.Add("Other");
        regions.Sites.Add(("Work", 0)); regions.Sites.Add(("Make", 2));
        regions.Loops.Add(("Lines", 3));
        regions.BoundaryBytes["Work"] = 96;
        regions.LoopBytes[("Lines", 3)] = 40;
        LifetimeFacts facts = new() { OwnedFields = owned, Regions = regions };

        using MemoryStream wire = new();
        using (BinaryWriter writer = new(wire, BackendProtocol.Utf8, leaveOpen: true))
            BackendProtocol.WriteRequest(writer, new("in.o", "out.o", Array.Empty<IrImport>(), null, facts));
        wire.Position = 0;
        using BinaryReader reader = new(wire, BackendProtocol.Utf8);
        BackendRequest request = BackendProtocol.ReadRequest(reader)!;
        OwnedFieldFacts o = request.Facts?.OwnedFields ?? throw new Exception("the owned fields do not cross");
        Check(o.Fields.Count == 2 && o.Fields["T::list"] == 8 && o.Fields["T::name"] == 16, "each owned field and its offset");
        Check(o.Mapped.SetEquals(new[] { "T::list" }) && o.SelfFreed.SetEquals(new[] { "T::list" }), "the mapped and self-freed flags, on that field alone");
        Check(o.Beside, "storage made beside");
        Check(o.Borrowers.SetEquals(new[] { "m_T_get_List_0" }) && o.Elements.Count == 1 && o.Elements["T::list"] == "list", "the borrowers and the elements' fields");
        Check(o.ElementCallees.Count == 1 && o.ElementCallees[("m_T_Fill_1", 1)] == "T::list" && o.ElementKept.SetEquals(new[] { "T::name" }), "the element callees and the fields kept");
        RegionFacts r = request.Facts?.Regions ?? throw new Exception("the regions do not cross");
        Check(r.Boundaries.SetEquals(regions.Boundaries) && r.Sites.SetEquals(regions.Sites) && r.Loops.SetEquals(regions.Loops), "the boundaries, sites and loops");
        Check(r.BoundaryBytes.Count == 1 && r.BoundaryBytes["Work"] == 96, "a boundary's size, and none for the unsized");
        Check(r.LoopBytes.Count == 1 && r.LoopBytes[("Lines", 3)] == 40, "a loop's size");
    }

    /// <summary>
    /// ONLY THE TYPES AN IMAGE MAKES (VirtualTargets.Made), over objects
    /// laid out as Lowering lays them (word 4, twelve words before the
    /// method table at 48): Base, and Made, Never and Static deriving from
    /// it, each overriding slot 0 with a Run of its own. Main's code stamps
    /// a Made (a relocation to it at 48); a static object in data is a
    /// Static, stamped in data; nothing names Never but its own self word
    /// and its display, and nothing names Base but the displays and its
    /// self word. A call on a Base runs Made's Run and Static's, and neither
    /// Base's nor Never's: no object of theirs exists. Without the set, all
    /// four. And a unit whose IR still makes a Never -- a function naming
    /// it, which a late pass took out of the object -- makes it again.
    /// </summary>
    private static void MadeTypes()
    {
        const int w = 4, table = 48;
        List<(string Name, ObjectFile Object)> Image(bool archived)
        {
            ObjectFile o = new();
            Section text = new(".text", SectionKind.Code);
            Section data = new(".data.rel.ro", SectionKind.ReadOnlyData);
            o.Sections.Add(text); o.Sections.Add(data);
            string[] types = { "Base", "Made", "Never", "Static" };
            foreach (string type in types)
            {
                text.Bytes.Add(0xc3);
                o.Symbols.Add(new Symbol { Name = "m_" + type + "_Run", Section = text, Offset = text.Bytes.Count - 1, Size = 1, IsFunction = true });
            }
            text.Bytes.AddRange(new byte[] { 0xb8, 0, 0, 0, 0, 0xc3 });
            o.Symbols.Add(new Symbol { Name = "main", Section = text, Offset = text.Bytes.Count - 6, Size = 6, IsFunction = true });
            // `new Made()`: the stamp's address in main's code.
            text.Relocs.Add(new Relocation(text.Bytes.Count - 5, "t_Made", table, RelocKind.Abs32));
            foreach (string type in types)
            {
                // Its display: Base, then itself.
                string[] chain = type == "Base" ? new[] { "t_Base" } : new[] { "t_Base", "t_" + type };
                int displayAt = data.Bytes.Count;
                for (int i = 0; i < chain.Length; i++) { data.Relocs.Add(new Relocation(displayAt + i * w, chain[i], 0, RelocKind.Abs32)); data.Bytes.AddRange(new byte[w]); }
                o.Symbols.Add(new Symbol { Name = "d_" + type, Section = data, Offset = displayAt, Size = chain.Length * w, Global = false });
                int at = data.Bytes.Count;
                data.Bytes.AddRange(new byte[table + w]);
                data.Relocs.Add(new Relocation(at + 3 * w, "d_" + type, 0, RelocKind.Abs32));
                data.Relocs.Add(new Relocation(at + 5 * w, "t_" + type, 0, RelocKind.Abs32));
                data.Relocs.Add(new Relocation(at + table, "m_" + type + "_Run", 0, RelocKind.Abs32));
                o.Symbols.Add(new Symbol { Name = "t_" + type, Section = data, Offset = at, Size = table + w });
            }
            // A static Static laid down whole: its stamp a data relocation.
            int stat = data.Bytes.Count;
            data.Bytes.AddRange(new byte[2 * w]);
            data.Relocs.Add(new Relocation(stat, "t_Static", table, RelocKind.Abs32));
            o.Symbols.Add(new Symbol { Name = "s_static", Section = data, Offset = stat, Size = 2 * w, Global = false });
            if (archived)
                IrArchive.Attach(o, new[] { new IrArchiveRecord("F:main", false, 2, Array.Empty<string>(), new byte[] { 1 }, new[] { "t_Made", "t_Never" }) });
            return new() { ("app", o) };
        }
        const string call = VirtualTargets.Prefix + "t_Base+0";

        var inputs = Image(archived: false);
        VirtualTargets.Made made = VirtualTargets.MadeIn(inputs, Array.Empty<IrArchive>());
        Check(made.Descriptors.SetEquals(new[] { "t_Made", "t_Static" }), "made: what code and data stamp, not what a self word or a display names: " + string.Join(",", made.Descriptors));
        string[] all = VirtualTargets.Resolve(inputs, new[] { call })[call];
        Check(all.SequenceEqual(new[] { "m_Base_Run", "m_Made_Run", "m_Never_Run", "m_Static_Run" }), "every type's override without the set: " + string.Join(",", all));
        string[] runs = VirtualTargets.Resolve(inputs, new[] { call }, made)[call];
        Check(runs.SequenceEqual(new[] { "m_Made_Run", "m_Static_Run" }), "only the made types' overrides: " + string.Join(",", runs));
        Check(made.Calls == 1 && made.Narrowed == 1 && made.Removed == 2 && made.Targets == 4, "the report's counts");
        Check(made.Dropped.SetEquals(new[] { "t_Base", "t_Never" }), "the unmade types the call reached");

        var again = Image(archived: true);
        VirtualTargets.Made remade = VirtualTargets.MadeIn(again, again.Select(input => IrArchive.Read(input.Object)!));
        string[] still = VirtualTargets.Resolve(again, new[] { call }, remade)[call];
        Check(still.SequenceEqual(new[] { "m_Made_Run", "m_Never_Run", "m_Static_Run" }), "a type the unit's IR makes is made: " + string.Join(",", still));
    }

    /// <summary>
    /// RECEIVER TYPES (RegionTypes). Main makes an A, a B and a holder H,
    /// stores the A into H at 8, and hands the A to Use, the B to Other and
    /// H to Leaky and ReadBack. Use and Other each make the call of slot 0
    /// whose overrides are RunA and RunB: Use's receiver only ever holds
    /// the A, so it runs RunA alone; Other's only the B, RunB alone. Leaky
    /// lets H go where nobody follows, where anything may be written into
    /// it: what ReadBack loads out of H and calls the slot on may be
    /// anything, and runs both. Without Leaky, ReadBack's receiver holds
    /// only what Main stored, the A.
    /// </summary>
    private static void ReceiverTypes()
    {
        foreach (bool leaks in new[] { true, false })
        {
            Prog p = new();
            RegionFunction main = new("Main", true, true, false, 0, 4, 0,
                new[] { new RegionSite(true, 1, "t_A", 48), new RegionSite(true, 2, "t_B", 48), new RegionSite(true, 3, "t_H", 48) });
            p.Functions.Add(main);
            main.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), Site(3, 2), Store(3, 1, 8) });
            main.Calls.Add(new RegionCall("Use", -1, new[] { 1 }));
            main.Calls.Add(new RegionCall("Other", -1, new[] { 2 }));
            main.Calls.Add(new RegionCall("Leaky", -1, new[] { 3 }));
            main.Calls.Add(new RegionCall("ReadBack", -1, new[] { 3 }));
            const string slot = VirtualTargets.Prefix + "t_Base+0";
            p.Add("Use", 1, 2).Calls.Add(new RegionCall(slot, -1, new[] { 0 }));
            p.Add("Other", 1, 2).Calls.Add(new RegionCall(slot, -1, new[] { 0 }));
            RegionFunction leaky = p.Add("Leaky", 1, 2);
            if (leaks) leaky.Constraints.Add(Leak(0));
            RegionFunction readBack = p.Add("ReadBack", 1, 3);
            readBack.Constraints.Add(Load(2, 0, 8));
            readBack.Calls.Add(new RegionCall(slot, -1, new[] { 2 }));
            p.Add("RunA", 1, 2); p.Add("RunB", 1, 2);
            int runA = p.Index("RunA"), runB = p.Index("RunB");

            int count = p.Functions.Count;
            int[]?[][] targets = new int[]?[count][];
            string?[][] keys = new string?[count][];
            for (int f = 0; f < count; f++)
            {
                targets[f] = new int[]?[p.Functions[f].Calls.Count];
                keys[f] = new string?[p.Functions[f].Calls.Count];
                for (int k = 0; k < p.Functions[f].Calls.Count; k++)
                {
                    string callee = p.Functions[f].Calls[k].Callee!;
                    if (callee == slot) { targets[f][k] = new[] { runA, runB }; keys[f][k] = slot + "@0"; }
                    else targets[f][k] = new[] { p.Index(callee) };
                }
            }
            bool[] rooted = p.Functions.Select(f => f.Name == "Main").ToArray();
            RegionTypes types = new(p.Functions, targets, keys, rooted,
                (f, k, table, at) => table == "t_A" ? new[] { runA } : table == "t_B" ? new[] { runB } : Array.Empty<int>());
            Check(types.Prune(), "it solves within its budget");
            int[] TargetsOf(string name) => targets[p.Index(name)][0]!;
            Check(TargetsOf("Use").SequenceEqual(new[] { runA }), "a receiver holding only an A runs RunA alone");
            Check(TargetsOf("Other").SequenceEqual(new[] { runB }), "a receiver holding only a B runs RunB alone");
            Check(TargetsOf("ReadBack").SequenceEqual(leaks ? new[] { runA, runB } : new[] { runA }),
                leaks ? "what is loaded out of an object that went where nobody follows may be anything" : "what is loaded out of the holder is what was stored");
            Check(types.Calls == 3 && types.Narrowed == (leaks ? 2 : 3) && types.Unknown == (leaks ? 1 : 0), "the report's counts");
        }
    }

    // A Prog's virtual calls narrowed by RegionTypes, each receiver's
    // descriptor running what `runs` says it does at the call's slot.
    private static Action<int[]?[][], string?[][], bool[]> Narrowed(Prog p, Func<string, string, string?> runs)
        => (targets, keys, roots) =>
        {
            RegionTypes types = new(p.Functions, targets, keys, roots, (f, k, table, at) =>
                runs(p.Functions[f].Calls[k].Callee!, table) is { } method ? new[] { p.Index(method) } : Array.Empty<int>());
            Check(types.Prune(), "the receiver types solve within their budget");
        };

    /// <summary>
    /// AN ITERATOR OVER AN ITERATOR, as LINQ makes them. Walk(list) makes an
    /// inner machine over the list and a Wrap machine over the inner one
    /// (its source at 8), and walks Wrap: MoveNext, then Current, on it.
    /// Wrap's MoveNext calls MoveNext and Current on its source -- a place,
    /// what its `this` holds at 8 -- and stores what Current hands back as
    /// its own current (16). The inner MoveNext makes an element a lap,
    /// stores it as its current, and keeps the list it read in a saved
    /// field of its own (2048, named its own). Both slots have every
    /// machine's override as targets, nineteen MoveNexts: one, Leaky's,
    /// lets its current go where nobody follows, and sixteen do nothing.
    /// A stand-in of all of them has the unknown object hold what is below
    /// every receiver, and every element any walk made was global.
    ///
    /// Narrowed by the receivers' types (RegionTypes), Walk's calls run
    /// Wrap's MoveNext and Current, and Wrap's run the inner machine's
    /// alone: no Leaky machine is ever its source. The elements are stored
    /// into Walk's own machines and handed back to no one: dead by Walk's
    /// return, and nothing global. Nor is the list, read and kept apart.
    /// </summary>
    private static void IteratorElements()
    {
        Prog p = new();
        const string moveNext = VirtualTargets.Prefix + "i_IEnumerator+0", current = VirtualTargets.Prefix + "i_IEnumerator+4";
        RegionFunction inner = new("InnerMoveNext", true, true, true, 1, 4, 0, new[] { new RegionSite(true, 1, "t_E", 48) }) { Families = new[] { "Inner::current", "$saved:InnerMoveNext" } };
        // Its element, made and stored as its current; its source read and kept in a saved field.
        inner.Constraints.AddRange(new[] { Site(2, 0), new RegionConstraint(RegionConstraintKind.Store, 0, 2, 16, 0), Load(3, 0, 8),
            new RegionConstraint(RegionConstraintKind.Store, 0, 3, 2048, 1) });
        p.Functions.Add(inner);
        RegionFunction innerCurrent = p.Add("InnerCurrent", 1, 2);
        innerCurrent.Constraints.Add(Load(1, 0, 16));
        RegionFunction leaky = p.Add("LeakyMoveNext", 1, 3);
        leaky.Constraints.AddRange(new[] { Load(2, 0, 16), Leak(2) });
        p.Add("LeakyCurrent", 1, 2).Constraints.Add(Load(1, 0, 16));
        for (int t = 1; t <= 16; t++) p.Add("Quiet" + t + "MoveNext", 1, 2);
        RegionFunction wrap = p.Add("WrapMoveNext", 1, 4);
        wrap.Constraints.AddRange(new[] { Load(2, 0, 8), Store(0, 3, 16) });
        wrap.Calls.Add(new(moveNext, -1, new[] { 2 }));
        wrap.Calls.Add(new(current, 3, new[] { 2 }));
        p.Add("WrapCurrent", 1, 2).Constraints.Add(Load(1, 0, 16));
        RegionFunction walk = new("Walk", true, true, false, 1, 5, 0,
            new[] { new RegionSite(true, 2, "t_Inner", 48), new RegionSite(true, 3, "t_Wrap", 48) });
        walk.Constraints.AddRange(new[] { Site(2, 0), Store(2, 0, 8), Site(3, 1), Store(3, 2, 8) });
        walk.Calls.Add(new(moveNext, -1, new[] { 3 }));
        walk.Calls.Add(new(current, 4, new[] { 3 }));
        p.Functions.Add(walk);
        // The list, an object of its own class: what the inner machine keeps
        // of it is no receiver of either slot.
        RegionFunction main = new("Main", true, true, false, 0, 2, 0, new[] { new RegionSite(true, 4, "t_List", 48) });
        main.Constraints.Add(Site(1, 0));
        main.Calls.Add(new("Walk", -1, new[] { 1 }));
        p.Functions.Add(main);
        p.Virtuals[moveNext] = new[] { "InnerMoveNext", "LeakyMoveNext", "WrapMoveNext" }.Concat(Enumerable.Range(1, 16).Select(t => "Quiet" + t + "MoveNext")).ToArray();
        p.Virtuals[current] = new[] { "InnerCurrent", "LeakyCurrent", "WrapCurrent" };
        string? Runs(string callee, string table) => (callee == moveNext, table) switch
        {
            (true, "t_Inner") => "InnerMoveNext", (true, "t_Wrap") => "WrapMoveNext", (true, "t_Leaky") => "LeakyMoveNext",
            (false, "t_Inner") => "InnerCurrent", (false, "t_Wrap") => "WrapCurrent", (false, "t_Leaky") => "LeakyCurrent",
            _ => null,
        };
        RegionEscape e = p.Solve(rooted: new[] { "Main" }, narrow: Narrowed(p, Runs));
        int element = p.Site("InnerMoveNext", 0);
        Check(!e.Global[element], "an element the walk made is not global");
        Check(!e.Escapes(p.Index("Walk"), element), "an element the walk made is dead by Walk's return");
        Check(!e.Global[p.Site("Walk", 0)] && !e.Global[p.Site("Walk", 1)], "nor are the machines");
        Check(!e.Global[p.Site("Main", 0)], "nor is the list the inner machine read and kept");
    }

    /// <summary>
    /// A CLOSURE OF TWO CAPTURES. Caller makes A and B and a closure C
    /// holding A at 8 and B at 16, and hands C to Apply, which calls Invoke
    /// on it -- a place, Apply's parameter. Invoke's slot has seventeen
    /// targets, every lambda's: C's own lets what it captured at 8 go where
    /// nobody follows and reads what it captured at 16; sixteen others do
    /// nothing. A stand-in of all of them has the unknown object hold all
    /// below the receiver: B too. Narrowed by the receiver's type, Apply
    /// runs C's Invoke alone, and only A goes where nobody follows.
    /// </summary>
    private static void ClosureCaptures()
    {
        Prog p = new();
        const string invoke = VirtualTargets.Prefix + "t_Func+0";
        RegionFunction body = p.Add("CInvoke", 1, 4);
        body.Constraints.AddRange(new[] { Load(2, 0, 8), Leak(2), Load(3, 0, 16) });
        for (int t = 1; t <= 16; t++) p.Add("Lambda" + t + "Invoke", 1, 2);
        p.Add("Apply", 1, 2).Calls.Add(new(invoke, -1, new[] { 0 }));
        RegionFunction caller = new("Caller", true, true, false, 0, 4, 0,
            new[] { new RegionSite(true, 1, "t_C", 48), new RegionSite(true, 2, "t_A", 48), new RegionSite(true, 3, "t_B", 48) });
        caller.Constraints.AddRange(new[] { Site(1, 0), Site(2, 1), Site(3, 2), Store(1, 2, 8), Store(1, 3, 16) });
        caller.Calls.Add(new("Apply", -1, new[] { 1 }));
        p.Functions.Add(caller);
        p.Virtuals[invoke] = new[] { "CInvoke" }.Concat(Enumerable.Range(1, 16).Select(t => "Lambda" + t + "Invoke")).ToArray();
        RegionEscape e = p.Solve(rooted: new[] { "Caller" }, narrow: Narrowed(p, (callee, table) => table == "t_C" ? "CInvoke" : null));
        Check(e.Global[p.Site("Caller", 1)], "the capture Invoke lets go is global");
        Check(!e.Global[p.Site("Caller", 2)], "the capture it only reads is not");
        Check(!e.Global[p.Site("Caller", 0)], "nor is the closure");
    }

    /// <summary>
    /// ONLY THE METHODS A BLIND CALL'S SLOT HOLDS ARE CALLED FROM WHERE
    /// NOBODY FOLLOWS (RegionSolver.Addressed). Walk makes a machine and
    /// calls its MoveNext (slot 0, resolved), which makes an element and
    /// stores it into the machine: dead by Walk's return, made in its
    /// region. Blind, elsewhere, calls a method it read out of a descriptor
    /// at slot 4. MoveNext is held at slot 0 alone: it is not called blind,
    /// not rooted, and its element is taken. Read at slot 0, or with the
    /// link not saying where methods are held, MoveNext is called with
    /// anything, and what it stores into its machine is everyone's.
    /// </summary>
    private static void BlindSlots()
    {
        RegionFacts?[]? Solve(long slot, bool known)
        {
            RegionHints unit = new();
            RegionFunction start = new("_start", true, false, false, 0, 1, 0, Array.Empty<RegionSite>());
            start.Calls.Add(new("Main", -1, Array.Empty<int>()));
            unit.Functions.Add(start);
            RegionFunction main = new("Main", true, false, false, 0, 1, 0, Array.Empty<RegionSite>()) { Main = true };
            main.Calls.Add(new("Walk", -1, Array.Empty<int>()));
            main.Calls.Add(new("Blind", -1, Array.Empty<int>()));
            unit.Functions.Add(main);
            RegionFunction walk = new("Walk", true, true, false, 0, 2, 0, new[] { new RegionSite(true, 1, "t_It", 48) });
            walk.Constraints.Add(Site(1, 0));
            walk.Calls.Add(new(VirtualTargets.Prefix + "t_It+0", -1, new[] { 1 }));
            walk.SiteBytes = new[] { 32L }; walk.SiteLoops = new[] { -1 }; walk.CallLoops = new[] { -1 };
            unit.Functions.Add(walk);
            RegionFunction moveNext = new("MoveNext", true, true, true, 1, 3, 0, new[] { new RegionSite(true, 1, null, 0) });
            moveNext.Constraints.AddRange(new[] { Site(2, 0), Store(0, 2, 8) });
            moveNext.SiteBytes = new[] { 32L }; moveNext.SiteLoops = new[] { -1 };
            unit.Functions.Add(moveNext);
            RegionFunction blind = new("Blind", true, false, false, 0, 1, 0, Array.Empty<RegionSite>()) { CallsThroughMethod = true, BlindSlots = new[] { slot } };
            blind.Calls.Add(new(null, -1, Array.Empty<int>()));
            unit.Functions.Add(blind);
            unit.MethodsTaken.Add("MoveNext");
            unit.CallsThroughMethods = true;
            RegionHints read = RegionHints.Read(unit.Write());
            Func<string, IReadOnlyCollection<long>?>? slotsOf = null;
            if (known) slotsOf = method => method == "MoveNext" ? new long[] { 0 } : null;
            Dictionary<string, string[]> virtuals = new(StringComparer.Ordinal) { [VirtualTargets.Prefix + "t_It+0"] = new[] { "MoveNext" } };
            return RegionSolver.Solve(new[] { read }, virtuals, (table, at) => table == "t_It" && at == 48 ? "MoveNext" : null,
                "_start", new HashSet<string>(StringComparer.Ordinal), null, slotsOf: slotsOf);
        }
        Check(Solve(4, known: true) is [{ } apart] && apart.Sites.Contains(("MoveNext", 0)), "read at slot 4 alone, MoveNext is not called blind, and its element is made in Walk's region");
        Check(Solve(0, known: true) is not [{ } at0] || !at0.Sites.Contains(("MoveNext", 0)), "read at slot 0, MoveNext is called blind, and its element is everyone's");
        Check(Solve(4, known: false) is not [{ } blindAll] || !blindAll.Sites.Contains(("MoveNext", 0)), "not knowing where methods are held, every method is called blind");
    }
}
