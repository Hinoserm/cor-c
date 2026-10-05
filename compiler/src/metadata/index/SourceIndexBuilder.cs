using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Indexes one source at a time, with ordinary method bodies omitted by the parser.</summary>
public static class SourceIndexBuilder
{
    private sealed record Identity(string Written, string Canonical);
    private static Identity? _identity;

    /// <summary>
    /// The canonical spelling of an assembly's identity. Every index query
    /// begins with it, thousands per unit and nearly always for the one
    /// assembly being compiled, so the last answer is kept.
    /// </summary>
    public static string AssemblyIdentity(string identity)
    {
        Identity? last = _identity;
        if (last is not null && last.Written == identity) return last.Canonical;
        string canonical = Canonical(identity);
        _identity = new Identity(identity, canonical);
        return canonical;
    }

    private static string Canonical(string identity)
    {
        AssemblyName name = new(identity);
        if (string.IsNullOrWhiteSpace(name.Name)) throw new ArgumentException("Assembly identity needs a name");
        Version version = name.Version ?? new Version(0, 0, 0, 0);
        AssemblyName canonical = new()
        {
            Name = name.Name.ToLowerInvariant(),
            Version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)),
            CultureName = (name.CultureName ?? "").ToLowerInvariant(),
        };
        canonical.SetPublicKeyToken(name.GetPublicKeyToken() ?? Array.Empty<byte>());
        return canonical.FullName;
    }

    /// <summary>The key of a source file's record: the hash of the text the index was made from.</summary>
    public static string SourceKey(string path) => "F:" + System.IO.Path.GetFullPath(path);

    /// <summary>A text's hash as the index records it: of its UTF-8 bytes, as read (File.ReadAllText).</summary>
    public static byte[] TextHash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public static void Write(string output, IEnumerable<string> paths, string assembly,
        IReadOnlyCollection<string>? symbols = null, int memoryBytes = 1024 * 1024,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? fileSymbols = null,
        Func<string, bool>? librarySource = null, string? on = null)
    {
        string identity = AssemblyIdentity(assembly);
        // ON ANOTHER INDEX (a kernel module's on its kernel's): this one
        // holds the module's own declarations and a record naming the index
        // under it with that index's stamp, and a reader answers from both
        // (DeclarationIndex.Under). A type the index under it declares is
        // refused: two declarations of one name would be read as one partial
        // type, and a module redefining the kernel's would not be the kernel's.
        string? onPath = on is null ? null : System.IO.Path.GetFullPath(on);
        using DeclarationIndex? under = onPath is null ? null : new DeclarationIndex(onPath);
        byte[]? onStamp = onPath is null ? null : DeclarationStamp.Of(onPath);
        string[] files = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (files.Contains(System.IO.Path.GetFullPath(output), StringComparer.Ordinal))
            throw new ArgumentException("Declaration index output would overwrite a source file");
        IEnumerable<DeclarationRecord> Records()
        {
            if (onPath is not null)
            {
                byte[] path = Encoding.UTF8.GetBytes(onPath);
                byte[] payload = new byte[32 + path.Length];
                Array.Copy(onStamp!, payload, 32);
                Array.Copy(path, 0, payload, 32, path.Length);
                yield return new DeclarationRecord(DeclarationIndex.OnKey, payload);
            }
            List<(string Path, byte[] Hash)> snapshots = new();
            foreach (string path in files)
            {
                IReadOnlyCollection<string>? activeSymbols = fileSymbols?.GetValueOrDefault(path) ?? symbols;
                string text = File.ReadAllText(path);
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                snapshots.Add((path, hash));
                // WHICH TEXT OF THE FILE THIS INDEX WAS MADE FROM, by its full
                // path (DeclarationCatalog.SourceHash): a unit compiled against
                // the index is compiled from this text of itself, or refused --
                // a source edited after the index was written read its new
                // self against its neighbours' old declarations.
                yield return new DeclarationRecord(SourceKey(path), hash);
                Lexer lexer = new(text, path, 1, 1, activeSymbols);
                List<Token> tokens = new();
                List<int> ends = new();
                while (true)
                {
                    Token token = lexer.Next(); tokens.Add(token); ends.Add(lexer.Position);
                    if (token.Kind == Tok.End) break;
                }
                // Parser speculation may split a >> token; retain untouched
                // lexical spellings for the declaration-only source record.
                Parser parser = new(new List<Token>(tokens), path, declarationsOnly: true);
                CompilationUnit unit = parser.ParseUnit();
                string TypeName(TypeDecl type)
                {
                    // Its own arity, as .NET's metadata names it: Box`1+Nested`1
                    // for Box<T>.Nested<U>, whose parameters are T and U.
                    int arity = type.TypeParams.Count - type.OuterParams;
                    string own = type.Name + (arity == 0 ? "" : "`" + arity);
                    TypeDecl? parent = type.Outer is null ? null : unit.Types
                        .Where(candidate => candidate.SourceFrom < type.SourceFrom && candidate.SourceTo >= type.SourceTo)
                        .OrderBy(candidate => candidate.SourceTo - candidate.SourceFrom).FirstOrDefault();
                    return parent is null ? (type.Namespace.Length == 0 ? "" : type.Namespace + ".") + own
                        : TypeName(parent) + "+" + own;
                }
                foreach (TypeDecl type in unit.Types)
                {
                    StringBuilder declaration = new();
                    var bodies = parser.OmittedBodies.Where(b => b.From >= type.SourceFrom && b.To <= type.SourceTo).OrderBy(b => b.From).ToArray();
                    int bodyIndex = 0;
                    bool wroteBody = false;
                    for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
                    {
                        Token token = tokens[tokenIndex];
                        if (token.Pos < type.SourceFrom || token.Pos >= type.SourceTo || token.Kind == Tok.End) continue;
                        while (bodyIndex < bodies.Length && token.Pos >= bodies[bodyIndex].To)
                        { bodyIndex++; wroteBody = false; }
                        if (bodyIndex < bodies.Length && token.Pos >= bodies[bodyIndex].From)
                        {
                            if (!wroteBody) declaration.Append(bodies[bodyIndex].Block ? "{} " : "default ");
                            wroteBody = true;
                            continue;
                        }
                        declaration.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                    }
                    string syntax = declaration.ToString();
                    FileScope scope = type.Scope ?? new FileScope();
                    using MemoryStream canonical = new();
                    using (BinaryWriter writer = new(canonical, Encoding.UTF8, leaveOpen: true))
                    {
                        // Token spelling/kind, not line offsets or body text,
                        // determines declaration invalidation.
                        foreach (Token token in Lexer.Tokenize(syntax, path))
                        { writer.Write((byte)token.Kind); writer.Write(token.Text); }
                        foreach (var import in scope.Imports) { writer.Write(import.In); writer.Write(import.Namespace); }
                        foreach (var alias in scope.Aliases) { writer.Write(alias.In); writer.Write(alias.Alias); writer.Write(alias.Target); }
                    }
                    string key = "T:" + identity + "\n" + TypeName(type);
                    if (under is not null && under.Find(key).Any())
                        throw new InvalidDataException(path + ": " + TypeName(type) + " is declared by the index this one is made on ("
                            + onPath + "); a module may not declare a type of its kernel's");
                    yield return new SourceDeclaration
                    {
                        Key = key, Path = path, Text = syntax,
                        Namespace = type.Namespace, Outer = type.Outer ?? "", Scope = scope,
                        From = type.SourceFrom, To = type.SourceTo, Line = type.Line, Column = type.Col,
                        SourceHash = hash, DeclarationHash = SHA256.HashData(canonical.ToArray()),
                        ConditionalSymbols = (activeSymbols ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    }.Encode();
                    yield return new DeclarationRecord("B:" + identity + "\n" + Binder.TypeKey(type), Encoding.UTF8.GetBytes(key));
                    // AND BY ITS SIMPLE NAME, when its key is qualified -- by a
                    // namespace or an outer type -- for the bare mention that
                    // only the binder's Sole answers (IndexedDeclarations.Sole).
                    if (type.Outer is not null)
                        yield return new DeclarationRecord("S:" + identity + "\n" + type.Name, Encoding.UTF8.GetBytes(key));
                    foreach (string method in type.Members.OfType<MethodDecl>()
                        .Where(method => method.Mods.HasFlag(Mods.Static) && method.Params.FirstOrDefault()?.IsThis == true)
                        .Select(method => method.Name).Distinct(StringComparer.Ordinal))
                        yield return new DeclarationRecord("E:" + identity + "\n" + type.Namespace + "\n" + method,
                            Encoding.UTF8.GetBytes(key));
                    // AND EVERY GENERIC INSTANCE METHOD BY NAME AND ARITY, so a
                    // call to a generic virtual method can find every class
                    // that overrides or implements it wherever it is declared
                    // (IndexedDeclarations.RequireOverrides). Instance methods
                    // of any kind, not only those written `override`: a class
                    // implements an interface's generic method without saying
                    // so.
                    foreach (string method in type.Members.OfType<MethodDecl>()
                        .Where(method => method.TypeParams.Count > 0 && !method.Mods.HasFlag(Mods.Static))
                        .Select(method => (method.ExplicitInterface is null ? method.Name : method.Name[(method.Name.LastIndexOf('.') + 1)..])
                                        + "`" + method.TypeParams.Count)
                        .Distinct(StringComparer.Ordinal))
                        yield return new DeclarationRecord("G:" + identity + "\n" + method, Encoding.UTF8.GetBytes(key));
                    if (type.Kind == TypeKind.Interface) yield return InterfaceFamilies.Record(key, type, librarySource?.Invoke(path) ?? true);
                }
            }
            // Validate the complete generation before the atomic publication.
            // This rereads one source at a time; no project body graph is retained.
            foreach (var snapshot in snapshots)
                if (!snapshot.Hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(snapshot.Path)))))
                    throw new InvalidDataException("Source changed while indexing: " + snapshot.Path);
        }
        DeclarationIndexWriter.Write(output, Records(), memoryBytes);
    }
}
