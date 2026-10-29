using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>What of a declaration's text a unit depends on: the flag beside each key of its receipt.</summary>
public enum Dependence : byte
{
    /// <summary>Its declaration: signatures, constants and initialisers, with the bodies left out.</summary>
    Declaration = 0,

    /// <summary>
    /// Its declaration and its whole file: the unit copied one of its
    /// bodies, and the copy carries the file's line positions into the
    /// unit's code.
    /// </summary>
    Source = 1,

    /// <summary>
    /// Its declaration and the shape of its template bodies: a template the
    /// unit imported with its bodies and copied none of, whose bodies were
    /// still read for what they name (SourceDeclaration.TemplateHash).
    /// </summary>
    Template = 2,

    /// <summary>
    /// Its header alone: the unit named the type -- a parameter's, a field's,
    /// a cast's -- and never asked for one of its members, which the binder
    /// left undeclared to the end (TypeSymbol.MembersPending). A method more
    /// or less, or another signature, says nothing to it; its interface
    /// slots are the plan's (InterfaceFamilies.PlanSaid), its layout another
    /// key's. Recorded in the declaration's place, alone.
    /// </summary>
    Header = 3,
}

/// <summary>Consumed declaration families, not a project-wide declaration graph.</summary>
public static class UnitDependencies
{
    /// <summary>
    /// One key's records by their declarations (Declaration), by the text of
    /// the files they are in (Source), or by the shape of their template
    /// bodies (Template). They are kept apart so that a stale receipt can
    /// say which of them moved: a changed signature and a changed body
    /// behind a template rebuild the same units for different reasons, and
    /// the build log should not leave the difference to be worked out by hand.
    /// </summary>
    private static byte[] Fingerprint(IReadOnlyList<SourceDeclaration> records, Dependence part)
    {
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        foreach (SourceDeclaration record in records.OrderBy(record => record.Path, StringComparer.Ordinal).ThenBy(record => record.From))
        {
            writer.Write(record.Path);
            writer.Write(part switch
            {
                Dependence.Source => record.SourceHash, Dependence.Template => record.TemplateHash,
                Dependence.Header => record.HeaderHash, _ => record.DeclarationHash,
            });
        }
        return SHA256.HashData(buffer.ToArray());
    }

    /// <summary>
    /// Writes a unit's receipt: every key it loaded, each with what of its
    /// text the unit depends on -- its declaration fingerprint always, and
    /// after it its whole file's where the unit copied one of its bodies
    /// (copied), or its template bodies' shape where it imported it as a
    /// template and copied none (templates) -- and every lookup the unit
    /// asked the index; and, for a kernel module's unit, the library
    /// functions and statics it took from what the kernel (or a module under
    /// it) exports rather than compiled (<paramref name="taken"/>), and those
    /// it compiled, nothing exporting them (<paramref name="kept"/>) -- no
    /// declaration says what is exported, and one taken that no longer is
    /// leaves the unit calling nothing, one kept that now is leaves two of it,
    /// a type with two descriptors.
    /// </summary>
    public static void Write(string path, DeclarationCatalog catalog, IEnumerable<string> keys, IReadOnlySet<string> templates,
        IReadOnlySet<string> copied, IEnumerable<string> queries, IEnumerable<string>? taken = null, IEnumerable<string>? kept = null,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? named = null)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream, Encoding.UTF8);
        string[] ordered = keys.Order(StringComparer.Ordinal).ToArray();
        writer.Write(0x50454443u); writer.Write(0); writer.Write(ordered.Length);
        foreach (string key in ordered)
        {
            using DeclarationLease lease = catalog.AcquireKey(key) ?? throw new InvalidDataException("Missing dependency " + key);
            Dependence dependence = copied.Contains(key) ? Dependence.Source
                : templates.Contains(key) ? Dependence.Template
                : named?.ContainsKey(key) == true ? Dependence.Header : Dependence.Declaration;
            byte[] encoded = Encoding.UTF8.GetBytes(key);
            if (encoded.Length > 4096) throw new InvalidDataException("Dependency key exceeds format limit");
            writer.Write(encoded.Length); writer.Write(encoded); writer.Write((byte)dependence);
            writer.Write(Fingerprint(lease.Records, dependence == Dependence.Header ? Dependence.Header : Dependence.Declaration));
            if (dependence is Dependence.Source or Dependence.Template) writer.Write(Fingerprint(lease.Records, dependence));
            // And the names it was told the type has no member of, which it
            // reads as long as they stay so.
            if (dependence == Dependence.Header)
            {
                string[] probed = named![key].Order(StringComparer.Ordinal).ToArray();
                writer.Write(probed.Length);
                foreach (string name in probed) writer.Write(name);
            }
        }
        string[] lookups = queries.Order(StringComparer.Ordinal).ToArray();
        writer.Write(lookups.Length);
        foreach (string key in lookups)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(key);
            if (encoded.Length > 4096) throw new InvalidDataException("Dependency lookup exceeds format limit");
            writer.Write(encoded.Length); writer.Write(encoded); writer.Write(catalog.QueryFingerprint(key));
        }
        foreach (IEnumerable<string>? names in new[] { taken, kept })
        {
            string[] list = (names ?? Array.Empty<string>()).Distinct().Order(StringComparer.Ordinal).ToArray();
            writer.Write(list.Length);
            foreach (string name in list)
            {
                byte[] encoded = Encoding.UTF8.GetBytes(name);
                if (encoded.Length > 4096) throw new InvalidDataException("Library name exceeds format limit");
                writer.Write(encoded.Length); writer.Write(encoded);
            }
        }
    }

    public static bool IsCurrent(string path, string index) => Stale(path, index) is null;

    /// <summary>
    /// Why the unit that wrote this receipt is not current against the index,
    /// or null when it is. The answer is the first difference found, in one
    /// sentence a build log can carry beside the unit it rebuilt
    /// (ProjectCompile): which declaration changed, whose file's text, or
    /// which lookup now answers differently.
    /// </summary>
    /// <param name="exports">
    /// A kernel module's: the names its kernel and the modules under it
    /// export now -- each the unit took has to be among them, and none it
    /// kept (null for any other unit, whose lists are empty).
    /// </param>
    public static string? Stale(string path, string index, IReadOnlySet<string>? exports = null)
    {
        if (!File.Exists(path)) return "no receipt";
        const string unreadable = "receipt unreadable";
        try
        {
            using DeclarationCatalog catalog = new(index);
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x50454443u || reader.ReadInt32() != 0) return unreadable;
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000) return unreadable;
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 4096 || length > stream.Length - stream.Position - 33) return unreadable;
                string key = Encoding.UTF8.GetString(reader.ReadBytes(length));
                Dependence dependence = (Dependence)reader.ReadByte(); if (!Enum.IsDefined(dependence)) return unreadable;
                byte[] declaration = reader.ReadBytes(32);
                byte[]? more = dependence is Dependence.Source or Dependence.Template ? reader.ReadBytes(32) : null;
                if (declaration.Length != 32 || more is { Length: not 32 }) return unreadable;
                using DeclarationLease? lease = catalog.AcquireKey(key);
                if (lease is null) return Named(key) + " is no longer declared";
                if (dependence == Dependence.Header)
                {
                    int probed = reader.ReadInt32();
                    if (probed < 0 || probed > 100000) return unreadable;
                    string? appeared = null;
                    for (int k = 0; k < probed; k++)
                    {
                        string name = reader.ReadString();
                        if (appeared is null && lease.Records.Any(record => Holds(record.MemberNames, name))) appeared = name;
                    }
                    if (!declaration.SequenceEqual(Fingerprint(lease.Records, Dependence.Header))) return "header of " + Named(key) + " changed";
                    if (appeared is not null) return Named(key) + " now has a member named " + appeared;
                    continue;
                }
                if (!declaration.SequenceEqual(Fingerprint(lease.Records, Dependence.Declaration))) return "declaration of " + Named(key) + " changed";
                if (more is not null && !more.SequenceEqual(Fingerprint(lease.Records, dependence)))
                    return dependence == Dependence.Source ? TextChanged(key, lease.Records) : "what " + Named(key) + "'s template bodies name changed";
            }
            count = reader.ReadInt32();
            if (count < 0 || count > 100000) return unreadable;
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 4096 || length > stream.Length - stream.Position - 32) return unreadable;
                string key = Encoding.UTF8.GetString(reader.ReadBytes(length));
                if (!reader.ReadBytes(32).SequenceEqual(catalog.QueryFingerprint(key)))
                {
                    string name = Named(key);
                    return "lookup " + key[0] + (name.Length == 0 ? "" : ":" + name) + " answers differently";
                }
            }
            for (int list = 0; list < 2; list++)
            {
                count = reader.ReadInt32();
                if (count < 0 || count > 1000000) return unreadable;
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32();
                    if (length < 1 || length > 4096 || length > stream.Length - stream.Position) return unreadable;
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(length));
                    if (exports is null) continue;
                    if (list == 0 && !exports.Contains(name)) return "took " + name + " from the kernel, which no longer exports it";
                    if (list == 1 && exports.Contains(name)) return "compiled its own " + name + ", which the kernel now exports";
                }
            }
            return stream.Position == stream.Length ? null : unreadable;
        }
        // What could not be read says what it was: a receipt cut short, or
        // an index that is not there.
        catch (IOException failure) { return unreadable + " (" + failure.Message + ")"; }
        catch (InvalidDataException failure) { return unreadable + " (" + failure.Message + ")"; }
    }

    /// <summary>
    /// A key or lookup as it is read in a log: the name after the assembly's
    /// identity, which every one of them begins with, its own parts (an
    /// extension lookup's namespace and method) joined by dots.
    /// </summary>
    private static string Named(string key) => key[(key.IndexOf('\n') + 1)..].Replace('\n', '.');

    /// <summary>
    /// The text fingerprint covers every file the key is declared in, and
    /// does not say which of them moved; one file is named as the one that
    /// changed, several as the ones one of which did.
    /// </summary>
    /// <summary>Whether a sorted list of member names (SourceDeclaration.MemberNames) holds one.</summary>
    private static bool Holds(IReadOnlyList<string> sorted, string name)
    {
        int low = 0, high = sorted.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2, order = string.CompareOrdinal(sorted[mid], name);
            if (order == 0) return true;
            if (order < 0) low = mid + 1; else high = mid - 1;
        }
        return false;
    }

    private static string TextChanged(string key, IReadOnlyList<SourceDeclaration> records)
    {
        string[] files = records.Select(record => Path.GetFileName(record.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return files.Length == 1
            ? "text of " + Named(key) + "'s file changed (" + files[0] + ")"
            : "text of one of " + Named(key) + "'s files changed (" + string.Join(", ", files) + ")";
    }
}
