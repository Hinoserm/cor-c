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

    /// <summary>How a member counts toward its type's DeclarationHash.</summary>
    private enum PrivateMember : byte { Shown, Left, Field, Constructor }

    /// <summary>
    /// A private member, by what another unit can see of it: none of a
    /// static, or of a method; an instance field's place, modifiers and type;
    /// that a constructor exists. A member with attributes, an explicit
    /// interface's, an instance property (an auto-property is a field), a
    /// static constructor (when statics are made) or a fixed buffer counts
    /// as written.
    /// </summary>
    private static PrivateMember Hiding(MemberDecl member)
    {
        if ((member.Mods & (Mods.Public | Mods.Protected | Mods.Internal)) != 0
            || member.ExplicitInterface is not null || member.Attributes.Count > 0) return PrivateMember.Shown;
        bool shared = (member.Mods & (Mods.Static | Mods.Const)) != 0;
        return member switch
        {
            FieldDecl { FixedSize: not null } => PrivateMember.Shown,
            FieldDecl => shared ? PrivateMember.Left : PrivateMember.Field,
            PropertyDecl => shared ? PrivateMember.Left : PrivateMember.Shown,
            MethodDecl { IsCtor: true } => shared ? PrivateMember.Shown : PrivateMember.Constructor,
            MethodDecl => PrivateMember.Left,
            _ => PrivateMember.Shown,
        };
    }

    /// <summary>The key of an assembly's `global using` directives on one side, the library's or the program's.</summary>
    public static string GlobalUsingKey(string identity, bool library) => "U:" + identity + "\n" + (library ? "library" : "program");

    /// <summary>The key of every record class's name (Parser.ProjectRecords): one key, every name.</summary>
    public static string RecordKey(string identity) => "R:" + identity + "\n";

    /// <summary>
    /// A text's hash as the index records it: of its UTF-8 bytes, as read
    /// (File.ReadAllText). FastHash, not SHA-256: it only tells a unit that a
    /// file is not the text the index was made from, and every unit compile
    /// checked each library file it took a body from with it -- megabytes of
    /// SHA-256 a unit, under a compiler compiled by itself a fifth of the time.
    /// Every place that makes or checks one calls this.
    /// </summary>
    public static byte[] TextHash(string text) => Corsac.Lang.Ir.FastHash.Of(Encoding.UTF8.GetBytes(text));

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
            // What the conflicted interface forms are read from, kept small:
            // each type's name, kind, parameters and bases (InterfaceForms).
            List<InterfaceForms.Declared> skeletons = new();
            foreach (string path in files)
            {
                IReadOnlyCollection<string>? activeSymbols = fileSymbols?.GetValueOrDefault(path) ?? symbols;
                string text = File.ReadAllText(path);
                byte[] hash = TextHash(text);
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
                bool librarySide = librarySource?.Invoke(path) ?? true;
                // THE FILE'S GLOBAL USINGS, which every file of its side reads
                // as its own (IndexedDeclarations.GlobalUsings, Frontend.
                // ApplyGlobalUsings): a unit compiled by itself parses only
                // its own files, and the directive may be in any other.
                foreach (string use in unit.GlobalUsings)
                    yield return new DeclarationRecord(GlobalUsingKey(identity, librarySide), Encoding.UTF8.GetBytes(use));
                foreach (TypeDecl type in unit.Types)
                {
                    skeletons.Add(InterfaceForms.Declared.Of(type, librarySide));
                    StringBuilder declaration = new();
                    var bodies = parser.OmittedBodies.Where(b => b.From >= type.SourceFrom && b.To <= type.SourceTo).OrderBy(b => b.From).ToArray();
                    int bodyIndex = 0;
                    bool wroteBody = false;
                    // AND ITS TEMPLATE BODIES' SHAPE, kept apart: the bodies a
                    // template is imported with (Parser.OmittedBodies' Template)
                    // are read for what they name before a unit is bound
                    // (IndexedDeclarations.AddHeaders), whether or not the unit
                    // copies one, and what they name is loaded into the unit.
                    // Their tokens, without a literal's value or any position:
                    // neither decides what a body names, so an edit to a number,
                    // or to a line above, leaves current a unit that never
                    // copied a body (UnitDependencies, Dependence.Template). An
                    // interpolated string keeps its text, whose holes are
                    // expressions.
                    using MemoryStream shape = new();
                    using BinaryWriter shaped = new(shape, Encoding.UTF8, leaveOpen: true);
                    // EACH TOKEN WHERE IT WAS WRITTEN: on its own line and at
                    // its own column, the text read from the type's line
                    // (IndexedDeclarations), so what a unit reading this header
                    // says of it points into the file. Joined by single spaces,
                    // every such error was at line 1 -- "Marked.cor(1,32)" for a
                    // base named on line 3. A body left out leaves its lines
                    // empty; a token its stand-in pushed right is moved over
                    // by one space, and stays on its line.
                    int line = type.Line, column = 1;
                    // WHAT ANOTHER UNIT MAY READ, hashed apart from the text:
                    // a private member is the type's own business, and adding
                    // or editing one leaves current the units compiled against
                    // the type (DeclarationHash). What still counts is what
                    // lays the object out -- an instance field, by its place,
                    // modifiers and type -- and whether it has a constructor
                    // of its own. A type whose bodies another unit may copy
                    // (a template: generic, or holding a generic method's),
                    // a partial one, a record, an interface or an enum is
                    // hashed whole.
                    StringBuilder visible = new();
                    bool filtered = type.Kind is TypeKind.Class or TypeKind.Struct && !type.IsRecord
                        && (type.Mods & Mods.Partial) == 0 && type.TypeParams.Count == 0
                        && type.PrimaryParameters is not { Count: > 0 } && !bodies.Any(b => b.Template);
                    var nested = unit.Types.Where(other => other != type && other.SourceFrom >= type.SourceFrom && other.SourceTo <= type.SourceTo).ToArray();
                    var members = filtered
                        ? parser.MemberSpans.Where(m => m.From >= type.SourceFrom && m.To <= type.SourceTo
                            && !nested.Any(n => m.From >= n.SourceFrom && m.To <= n.SourceTo)).OrderBy(m => m.From).ToArray()
                        : Array.Empty<(int From, int To, MemberDecl Member)>();
                    int memberIndex = 0;
                    PrivateMember hiding = PrivateMember.Shown;
                    bool markedCtor = false;
                    void Place(Token at)
                    {
                        if (at.Line > line) { declaration.Append('\n', at.Line - line); line = at.Line; column = 1; }
                        if (at.Line == line && at.Col > column) { declaration.Append(' ', at.Col - column); column = at.Col; }
                        else if (at.Line < line || at.Col < column) { declaration.Append(' '); column++; }
                    }
                    // A piece written, and where the next one starts: a token
                    // may hold a line break (a verbatim string).
                    void Write(string piece, int from, int count)
                    {
                        if (count == 0) return;
                        declaration.Append(piece, from, count);
                        int last = piece.LastIndexOf('\n', from + count - 1, count);
                        if (last < 0) { column += count; return; }
                        for (int i = from; i <= last; i++) if (piece[i] == '\n') line++;
                        column = from + count - last;
                    }
                    for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
                    {
                        Token token = tokens[tokenIndex];
                        if (token.Pos < type.SourceFrom || token.Pos >= type.SourceTo || token.Kind == Tok.End) continue;
                        while (bodyIndex < bodies.Length && token.Pos >= bodies[bodyIndex].To)
                        { bodyIndex++; wroteBody = false; }
                        while (memberIndex < members.Length && token.Pos >= members[memberIndex].To)
                        { memberIndex++; markedCtor = false; }
                        hiding = memberIndex < members.Length && token.Pos >= members[memberIndex].From
                            ? Hiding(members[memberIndex].Member) : PrivateMember.Shown;
                        if (hiding == PrivateMember.Constructor && !markedCtor) { visible.Append("ctor ; "); markedCtor = true; }
                        if (bodyIndex < bodies.Length && token.Pos >= bodies[bodyIndex].From)
                        {
                            // A BODY WHOSE TEXT SAYS IT NEVER RETURNS STAYS ONE
                            // THAT SAYS SO (Parser.NeverReturningBodies): a unit
                            // reading this reads the same of the method as the
                            // unit that compiles it, and code both compile -- a
                            // generic copy calling it -- comes out the same in
                            // both. An edit that lets it return changes the
                            // declaration, and what was compiled against it.
                            bool never = parser.NeverReturningBodies.Contains(bodies[bodyIndex].From);
                            // A BODY THAT NAMES MemberwiseClone KEEPS THE NAME
                            // (TypeDecl.MemberwiseCopied): a unit reading this
                            // reads the type as one whose objects may be
                            // copied by bytes, as the unit compiling it does.
                            bool copies = false;
                            for (int k = tokenIndex; k < tokens.Count && tokens[k].Pos < bodies[bodyIndex].To && !copies; k++)
                                copies = tokens[k].Kind == Tok.Ident && tokens[k].Text == "MemberwiseClone";
                            if (!wroteBody)
                            {
                                string standIn = bodies[bodyIndex].Block ? (never ? (copies ? "{ MemberwiseClone ; throw null ; } " : "{ throw null ; } ") : copies ? "{ MemberwiseClone ; } " : "{} ")
                                    : (never ? (copies ? "throw MemberwiseClone " : "throw null ") : copies ? "MemberwiseClone " : "default ");
                                // AND THE PRIMARY CONSTRUCTOR'S PARAMETERS IT
                                // NAMES (TypeDecl.PrimaryParameters): each is a
                                // field of the object where a body names it
                                // (Parser.AddPrimaryConstructor), and the unit
                                // reading this lays the object out from what the
                                // stand-ins name. Before a block's statements,
                                // whose last says whether it returns; after an
                                // expression, whose first does.
                                if (type.PrimaryParameters is { Count: > 0 } primary)
                                {
                                    StringBuilder? named = null;
                                    foreach (string name in primary)
                                        for (int k = tokenIndex; k < tokens.Count && tokens[k].Pos < bodies[bodyIndex].To; k++)
                                            if (tokens[k].Kind == Tok.Ident && tokens[k].Text == name
                                                && tokens[k - 1].Kind is not (Tok.Dot or Tok.QuestionDot or Tok.Arrow))
                                            { (named ??= new()).Append(name).Append(bodies[bodyIndex].Block ? " ; " : " "); break; }
                                    if (named is not null)
                                        standIn = bodies[bodyIndex].Block ? "{ " + named + standIn[1..].TrimStart() : standIn + named;
                                }
                                Place(token);
                                Write(standIn, 0, standIn.Length);
                                if (hiding == PrivateMember.Shown) visible.Append(standIn);
                                else if (copies) visible.Append("MemberwiseClone ; ");
                            }
                            wroteBody = true;
                            if (bodies[bodyIndex].Template)
                            {
                                shaped.Write((byte)token.Kind);
                                if (token.Kind is not (Tok.Int or Tok.Real or Tok.Str or Tok.Char or Tok.Utf8Str)) shaped.Write(token.Text);
                            }
                            continue;
                        }
                        Place(token);
                        Write(text, token.Pos, ends[tokenIndex] - token.Pos);
                        if (hiding == PrivateMember.Shown) visible.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                        else if (hiding == PrivateMember.Field && !parser.FieldInitialisers.Any(i => token.Pos >= i.From && token.Pos < i.To))
                            visible.Append(parser.FieldNames.Contains(token.Pos) ? "_" : text.Substring(token.Pos, ends[tokenIndex] - token.Pos)).Append(' ');
                    }
                    shaped.Flush();
                    string syntax = declaration.ToString();
                    FileScope scope = type.Scope ?? new FileScope();
                    using MemoryStream canonical = new();
                    using (BinaryWriter writer = new(canonical, Encoding.UTF8, leaveOpen: true))
                    {
                        // Token spelling/kind, not line offsets or body text,
                        // determines declaration invalidation.
                        foreach (Token token in Lexer.Tokenize(visible.ToString(), path))
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
                        TemplateHash = SHA256.HashData(shape.ToArray()),
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
                    // A RECORD CLASS BY NAME, which a record of another unit
                    // may derive from without arguments to say it is one
                    // (Frontend.RecordNames).
                    if (type.IsRecord && type.Kind == TypeKind.Class)
                        yield return new DeclarationRecord(RecordKey(identity), Encoding.UTF8.GetBytes(type.Name));
                }
            }
            // THE FORMS EVERY UNIT NUMBERS A BLOCK FOR (InterfaceForms), over
            // the declarations of the whole project at once.
            foreach (var (form, library) in InterfaceForms.Conflicts(skeletons).OrderBy(c => c.Key, StringComparer.Ordinal))
                yield return InterfaceFamilies.Form(assembly, form, library);
            // Validate the complete generation before the atomic publication.
            // This rereads one source at a time; no project body graph is retained.
            foreach (var snapshot in snapshots)
                if (!snapshot.Hash.SequenceEqual(TextHash(File.ReadAllText(snapshot.Path))))
                    throw new InvalidDataException("Source changed while indexing: " + snapshot.Path);
        }
        DeclarationIndexWriter.Write(output, Records(), memoryBytes);
    }
}
