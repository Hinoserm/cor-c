using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT A SYMBOL'S ADDRESS IS, for the region solvers: a constant or a
/// global. A unit's hints name the symbols its code takes the address of
/// (RegionConstraintKind.Symbol); the link, which holds every object, says
/// which are constants -- a function, or read-only data whose every
/// relocation names a constant in turn, a string literal, a type's
/// descriptor or vtable, a table of numbers -- and which are anything else:
/// a static's storage, data written at run time, a symbol it cannot find.
///
/// A CONSTANT IS NOTHING THE ESCAPE ENGINE NEEDS TO FOLLOW (RegionEscape:
/// Graph and Unified, whose calls are bound by their targets alone). It is no site's object,
/// it is never written (it lies in read-only memory), and what is read out
/// of it is a constant again, by the relocations' closure. So a node that
/// holds one holds nothing the solvers care about: storing it somewhere
/// makes nothing escape, loading from it finds nothing, and a call handed
/// it is handed nothing. Taken for the unknown object, as every symbol was,
/// `this.name = "x"` put the unknown object into a field of `this`, and
/// unification (RegionEscape.Unified) then joined that field, and with it
/// what `this` is, to the global class: a unified function's every object
/// that held a literal or a descriptor escaped. A global's address stays
/// the unknown object. The Andersen engine (RegionSolver) binds an instance
/// call by the objects its receiver holds -- a method called on a string
/// literal is run for that object -- so a constant is the unknown object
/// to it still.
/// </summary>
internal static class RegionConstants
{
    /// <summary>
    /// Each function's Symbol constraints made Unknown where the symbol is
    /// not a constant; those left name constants (RegionFunction.ConstantsKnown).
    /// </summary>
    public static int Resolve(IReadOnlyList<RegionHints> units, IReadOnlyList<ObjectFile> unitObjects, IEnumerable<ObjectFile> all)
    {
        Classifier classes = new(all);
        int constants = 0;
        for (int u = 0; u < units.Count; u++)
            foreach (RegionFunction function in units[u].Functions)
            {
                if (function.Symbols.Length == 0) continue;
                bool[] constant = new bool[function.Symbols.Length];
                for (int k = 0; k < constant.Length; k++) constant[k] = classes.IsConstant(unitObjects[u], function.Symbols[k]);
                List<RegionConstraint> list = function.Constraints;
                int w = 0;
                for (int r = 0; r < list.Count; r++)
                {
                    RegionConstraint c = list[r];
                    if (c.Kind == RegionConstraintKind.Symbol)
                    {
                        if (constant[c.B]) constants++;
                        else c = new RegionConstraint(RegionConstraintKind.Unknown, c.A, 0, 0);
                    }
                    list[w++] = c;
                }
                list.RemoveRange(w, list.Count - w);
                function.ConstantsKnown = true;
            }
        return constants;
    }

    private sealed class Classifier
    {
        // Every global definition by name: more than one (a coalesced library
        // item each unit has a copy of) is a constant only if all are.
        private readonly Dictionary<string, List<(ObjectFile Object, Symbol Symbol)>> _globals = new(StringComparer.Ordinal);
        // Per object, its own definitions by name (a local name is the object's).
        private readonly Dictionary<ObjectFile, Dictionary<string, Symbol>> _own = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Symbol, ObjectFile> _objectOf = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Symbol, bool> _known = new(ReferenceEqualityComparer.Instance);
        // Per section: its relocations in offset order, found once.
        private readonly Dictionary<Section, Relocation[]> _relocs = new(ReferenceEqualityComparer.Instance);

        public Classifier(IEnumerable<ObjectFile> all)
        {
            foreach (ObjectFile obj in all)
            {
                Dictionary<string, Symbol> own = new(StringComparer.Ordinal);
                foreach (Symbol s in obj.Symbols)
                {
                    if (!s.IsDefined) continue;
                    _objectOf[s] = obj;
                    // A local beside a global of the same name: the local is the object's own.
                    if (!s.Global || !own.ContainsKey(s.Name)) own[s.Name] = s;
                    if (s.Global)
                    {
                        if (!_globals.TryGetValue(s.Name, out var list)) _globals[s.Name] = list = new();
                        list.Add((obj, s));
                    }
                }
                _own[obj] = own;
            }
        }

        /// <summary>The definitions a name means in an object: its own, else every global one; none when nobody defines it.</summary>
        private IEnumerable<Symbol> Meant(ObjectFile obj, string name)
        {
            if (_own.TryGetValue(obj, out var own) && own.TryGetValue(name, out Symbol? mine)) return new[] { mine };
            return _globals.TryGetValue(name, out var list) ? list.Select(d => d.Symbol) : Array.Empty<Symbol>();
        }

        public bool IsConstant(ObjectFile obj, string name)
        {
            Symbol[] meant = Meant(obj, name).ToArray();
            return meant.Length > 0 && meant.All(Constant);
        }

        // A symbol in code, or in read-only data with nothing but constants
        // named from within it. Found over everything it reaches at once:
        // what one finds while another it reaches is still being judged is
        // only settled when every one of them is (a descriptor and its
        // vtable name each other).
        private bool Constant(Symbol root)
        {
            if (_known.TryGetValue(root, out bool known)) return known;
            List<Symbol> reached = new() { root };
            HashSet<Symbol> seen = new(ReferenceEqualityComparer.Instance) { root };
            Dictionary<Symbol, List<Symbol>> namedBy = new(ReferenceEqualityComparer.Instance);
            HashSet<Symbol> bad = new(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < reached.Count; k++)
            {
                Symbol s = reached[k];
                if (_known.TryGetValue(s, out bool settled)) { if (!settled) bad.Add(s); continue; }
                SectionKind kind = s.Section!.Kind;
                if (kind == SectionKind.Code) continue;
                if (kind != SectionKind.ReadOnlyData || s.Size <= 0) { bad.Add(s); continue; }
                ObjectFile obj = _objectOf[s];
                foreach (Relocation r in Within(s))
                {
                    Symbol[] targets = Meant(obj, r.Symbol).ToArray();
                    if (targets.Length == 0) { bad.Add(s); break; }
                    foreach (Symbol t in targets)
                    {
                        if (!namedBy.TryGetValue(t, out var by)) namedBy[t] = by = new();
                        by.Add(s);
                        if (seen.Add(t)) reached.Add(t);
                    }
                }
            }
            // What names something not constant is not constant either.
            Stack<Symbol> next = new(bad);
            while (next.TryPop(out Symbol? s))
                if (namedBy.TryGetValue(s, out var by))
                    foreach (Symbol b in by) if (bad.Add(b)) next.Push(b);
            foreach (Symbol s in reached) _known[s] = !bad.Contains(s);
            return _known[root];
        }

        // The relocations that lie within a symbol's bytes.
        private IEnumerable<Relocation> Within(Symbol s)
        {
            Section section = s.Section!;
            if (!_relocs.TryGetValue(section, out Relocation[]? all))
                _relocs[section] = all = section.Relocs.OrderBy(r => r.Offset).ToArray();
            long from = s.Offset, to = s.Offset + s.Size;
            int lo = 0, hi = all.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (all[mid].Offset < from) lo = mid + 1; else hi = mid; }
            for (int k = lo; k < all.Length && all[k].Offset < to; k++) yield return all[k];
        }
    }
}
