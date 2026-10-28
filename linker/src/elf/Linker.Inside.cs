#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

public static partial class Linker
{
    /// <summary>
    /// The suffix of a method's copy that runs inside its caller's kept
    /// state: no save or give-back of its own (the compiler's
    /// Lowering.CopiesInside and InsideLabel).
    /// </summary>
    public const string InsideSuffix = "$inside";

    /// <summary>
    /// A CALL INTO ANOTHER IMAGE MADE INSIDE A KEPT STATE names the callee's
    /// $inside copy where the images the unit was compiled against exported
    /// one (Lowering.InsideLabel): the kernel's, a module this one is built
    /// on. Where nothing this link has defines that copy -- no input, nothing
    /// `elsewhere` says is exported -- because a later link of theirs pruned
    /// it, the call is to the method itself, which keeps the state of its
    /// own: right inside a kept state as anywhere, only dearer. Done before
    /// anything is resolved, on the symbols and on the relocations naming
    /// them -- the objects themselves, so linked again they call the method.
    /// Nothing to do, and nothing read, where no unit names a copy it lacks.
    /// </summary>
    internal static void InsideOrItself(IEnumerable<ObjectFile> objects, Func<string, bool>? elsewhere = null)
    {
        List<ObjectFile> all = objects.ToList();
        HashSet<string> defined = new(StringComparer.Ordinal);
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (ObjectFile o in all)
            foreach (Symbol s in o.Symbols)
            {
                if (s.IsDefined) defined.Add(s.Name);
                else if (s.Name.EndsWith(InsideSuffix, StringComparison.Ordinal)) named.Add(s.Name);
            }
        named.RemoveWhere(name => defined.Contains(name) || elsewhere?.Invoke(name) == true);
        if (named.Count == 0) return;
        foreach (ObjectFile o in all)
        {
            HashSet<string> has = new(o.Symbols.Select(s => s.Name), StringComparer.Ordinal);
            for (int i = o.Symbols.Count - 1; i >= 0; i--)
            {
                Symbol s = o.Symbols[i];
                if (s.IsDefined || !named.Contains(s.Name)) continue;
                string itself = s.Name[..^InsideSuffix.Length];
                // One undefined name a method: the object may name it already.
                if (has.Contains(itself)) o.Symbols.RemoveAt(i);
                else
                {
                    o.Symbols[i] = new Symbol { Name = itself, Offset = s.Offset, Size = s.Size, IsFunction = s.IsFunction, Global = s.Global };
                    has.Add(itself);
                }
            }
            foreach (Section section in o.Sections)
                for (int r = 0; r < section.Relocs.Count; r++)
                    if (named.Contains(section.Relocs[r].Symbol))
                        section.Relocs[r] = section.Relocs[r] with { Symbol = section.Relocs[r].Symbol[..^InsideSuffix.Length] };
        }
    }

    /// <summary>What the shared objects at `paths` export, read once, the first time a name is asked about.</summary>
    private static Func<string, bool> ExportedBy(IEnumerable<string> paths)
    {
        HashSet<string>? exported = null;
        return name =>
        {
            if (exported is null)
            {
                exported = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in paths)
                {
                    try { exported.UnionWith(ElfReader.ExportsOf(File.ReadAllBytes(path))); }
                    catch (Exception e) when (e is IOException or ElfFormatException)
                    {
                        throw new LinkException(new[] { path + ": its exports cannot be read: " + e.Message });
                    }
                }
            }
            return exported.Contains(name);
        };
    }
}
