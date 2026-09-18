using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Corsac.Lang.Metadata;

/// <summary>Indexes one source at a time, with ordinary method bodies omitted by the parser.</summary>
public static class SourceIndexBuilder
{
    public static string AssemblyIdentity(string identity)
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

    public static void Write(string output, IEnumerable<string> paths, string assembly,
        IReadOnlyCollection<string>? symbols = null, int memoryBytes = 1024 * 1024)
    {
        string identity = AssemblyIdentity(assembly);
        string[] files = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (files.Contains(System.IO.Path.GetFullPath(output), StringComparer.Ordinal))
            throw new ArgumentException("Declaration index output would overwrite a source file");
        IEnumerable<DeclarationRecord> Records()
        {
            List<(string Path, byte[] Hash)> snapshots = new();
            foreach (string path in files)
            {
                string text = File.ReadAllText(path);
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                snapshots.Add((path, hash));
                Lexer lexer = new(text, path, 1, 1, symbols);
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
                    string own = type.Name + (type.TypeParams.Count == 0 ? "" : "`" + type.TypeParams.Count);
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
                    yield return new SourceDeclaration
                    {
                        Key = key, Path = path, Text = syntax,
                        Namespace = type.Namespace, Outer = type.Outer ?? "", Scope = scope,
                        From = type.SourceFrom, To = type.SourceTo, Line = type.Line, Column = type.Col,
                        SourceHash = hash, DeclarationHash = SHA256.HashData(canonical.ToArray()),
                        ConditionalSymbols = (symbols ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    }.Encode();
                    yield return new DeclarationRecord("B:" + identity + "\n" + Binder.TypeKey(type), Encoding.UTF8.GetBytes(key));
                    if (type.Kind == TypeKind.Interface) yield return InterfaceFamilies.Record(key, type);
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
