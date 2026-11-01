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

    /// <summary>
    /// A type's header hashed (SourceDeclaration.HeaderHash): its tokens up
    /// to the brace its members start at -- or the `;` that ends a record
    /// with none -- outside any parentheses or brackets, and the usings its
    /// names resolve by. No position, so a line moved above it moves nothing.
    /// </summary>
    private static byte[] HeaderHash(List<Token> tokens, TypeDecl type, FileScope scope)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            int depth = 0;
            // From its first token, found by position: the tokens are in order.
            int first = 0, last = tokens.Count;
            while (first < last) { int mid = (first + last) / 2; if (tokens[mid].Pos < type.SourceFrom) first = mid + 1; else last = mid; }
            for (int at = first; at < tokens.Count; at++)
            {
                Token token = tokens[at];
                if (token.Kind == Tok.End) continue;
                if (token.Pos >= type.SourceTo) break;
                if (depth == 0 && token.Kind is Tok.LBrace or Tok.Semi) break;
                if (token.Kind is Tok.LParen or Tok.LBracket) depth++;
                else if (token.Kind is Tok.RParen or Tok.RBracket) depth--;
                writer.Write((byte)token.Kind); writer.Write(token.Text);
            }
            foreach (var import in scope.Imports) { writer.Write(import.In); writer.Write(import.Namespace); }
            foreach (var alias in scope.Aliases) { writer.Write(alias.In); writer.Write(alias.Alias); writer.Write(alias.Target); }
        }
        return SHA256.HashData(stream.ToArray());
    }

    /// <summary>How a member counts toward its type's DeclarationHash.</summary>
    private enum PrivateMember : byte { Shown, Left, Field, Constructor, Initialised }

    /// <summary>
    /// Whether a type's private members may be left out of what another
    /// unit is said to read of it (Hiding; ManagedLayouts, the same types):
    /// a class or struct whose bodies no other unit copies -- not generic,
    /// with no generic method, no primary constructor, not partial, not a
    /// record. Any other is hashed whole.
    /// </summary>
    public static bool HidesPrivate(TypeDecl type)
        => type.Kind is TypeKind.Class or TypeKind.Struct && !type.IsRecord
            && (type.Mods & Mods.Partial) == 0 && type.TypeParams.Count == 0
            && type.PrimaryParameters is not { Count: > 0 }
            && !type.Members.Any(member => member is MethodDecl { TypeParams.Count: > 0 });

    /// <summary>
    /// A private member, by what another unit reads of it:
    /// - a method, or a static without an initialiser: nothing;
    /// - an instance field: its place, modifiers and type;
    /// - a constructor: that the type has one of its own;
    /// - a static with an initialiser: that the type has some, which every
    ///   unit touching the type reads as whether it must be made before use
    ///   (Binder.StaticInitialisers, Lowering.TouchType) -- and, of a type
    ///   whose members are told apart, the initialiser's tokens in its shape
    ///   (ByMember), whether it is a table or code being what that reads.
    /// A const counts as written -- a public constant, a default argument or
    /// a buffer's length may be folded from it -- as does a member with
    /// attributes, an explicit interface's, an instance property (an
    /// auto-property is a field), a static constructor and a fixed buffer.
    /// </summary>
    private static PrivateMember Hiding(MemberDecl member)
    {
        if ((member.Mods & (Mods.Public | Mods.Protected | Mods.Internal | Mods.Const)) != 0
            || member.ExplicitInterface is not null || member.Attributes.Count > 0) return PrivateMember.Shown;
        bool shared = (member.Mods & Mods.Static) != 0;
        return member switch
        {
            FieldDecl { FixedSize: not null } => PrivateMember.Shown,
            FieldDecl field => !shared ? PrivateMember.Field : field.Init is null ? PrivateMember.Left : PrivateMember.Initialised,
            PropertyDecl property => !shared ? PrivateMember.Shown : property.Init is null ? PrivateMember.Left : PrivateMember.Initialised,
            MethodDecl { IsCtor: true } => shared ? PrivateMember.Shown : PrivateMember.Constructor,
            MethodDecl => PrivateMember.Left,
            _ => PrivateMember.Shown,
        };
    }

    /// <summary>
    /// WHETHER A TYPE'S MEMBERS ARE TOLD APART for the units that read it
    /// (SourceDeclaration.ByMember): a class or a struct, not generic, not
    /// partial, no record and no primary constructor, with no base, no
    /// interface and no attribute -- nothing a unit reads of it in bulk: a
    /// [Registry] class's schema is written into every unit that loads it --
    /// and the program's, not the class library's, whose members the compiler
    /// asks for by names of its own (String.Concat, Gc's fields). The kernel's
    /// Arch, Mem and Irq: a method more in Arch rebuilt 115 kernel units that
    /// never called it.
    ///
    /// WITH GENERIC METHODS TOO -- Arch has two. A unit that copied one's body
    /// depends on the whole file as it always did (Dependence.Source); one
    /// that did not reads the template bodies for what they name, which is in
    /// the shape (the template bodies' hash, SourceDeclaration.TemplateHash).
    /// Its private members are then hashed as written, under their names.
    /// </summary>
    private static bool ByMember(TypeDecl type, bool librarySide)
        => !librarySide && type.Kind is TypeKind.Class or TypeKind.Struct && !type.IsRecord && (type.Mods & Mods.Partial) == 0
           && type.TypeParams.Count == 0 && type.PrimaryParameters is not { Count: > 0 }
           && type.Bases.Count == 0 && type.Attributes.Count == 0 && type.EnumMembers.Count == 0;

    /// <summary>
    /// The name a member of a ByMember type is hashed under, or null where
    /// what it says is the whole type's (its shape): a constructor, an
    /// operator, a finaliser, a virtual, abstract or overriding member, an
    /// explicit interface's, an instance field (the layout), an instance
    /// property or an indexer.
    /// </summary>
    private static string? MemberName(MemberDecl member)
    {
        if (member.ExplicitInterface is not null || (member.Mods & (Mods.Virtual | Mods.Abstract | Mods.Override)) != 0) return null;
        bool shared = (member.Mods & (Mods.Static | Mods.Const)) != 0;
        return member switch
        {
            MethodDecl method => method.IsCtor || method.Name.StartsWith("op_", StringComparison.Ordinal) || method.Name.StartsWith('~')
                || method.Name == "Finalize" ? null : method.Name,
            // An initialised static is the shape's: what is made of the type's
            // statics -- a step the link may lay down, a call before first use
            // -- is read by every unit touching the type (Binder.StaticInitialisers).
            // Decided per declaration too: one whose later declarator has an
            // initialiser (`static int a, b = F();`) is the shape's whole,
            // whatever this says of its first (the builder's spanStatics).
            FieldDecl field => shared && field.FixedSize is null && (field.Init is null || (field.Mods & Mods.Const) != 0) ? field.Name : null,
            PropertyDecl property => shared && property.Params.Count == 0 && property.Init is null ? property.Name : null,
            _ => null,
        };
    }

    /// <summary>Every identifier-shaped word of a text: what an interpolated string's holes may name.</summary>
    private static IEnumerable<string> Words(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!(char.IsLetter(text[i]) || text[i] == '_') || i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) continue;
            int end = i;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            yield return text[i..end];
            i = end;
        }
    }

    /// <summary>A static with an initialiser, a constant's aside: startup work the type has (Binder.StaticInitialisers).</summary>
    private static bool Statics(MemberDecl member)
        => (member.Mods & Mods.Static) != 0 && (member.Mods & Mods.Const) == 0 && member is FieldDecl { Init: not null } or PropertyDecl { Init: not null };

    /// <summary>The names a member is looked up by (Monomorphiser.MemberNames): a property's accessors and backing field besides its own.</summary>
    private static IEnumerable<string> LookedUpBy(MemberDecl member, string name)
    {
        yield return name;
        if (member is PropertyDecl)
        {
            yield return "<" + name + ">";
            yield return NameTable.Accessor("get_", name);
            yield return NameTable.Accessor("set_", name);
        }
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

    /// <summary>
    /// Shown text hashed as DeclarationHash is -- its tokens' kinds and
    /// spellings, no position -- with the file's usings where a scope is
    /// given (the shape's: a using names its types), cut to `bytes`.
    /// </summary>
    private static byte[] Hashed(string shown, string path, FileScope? scope, int bytes)
    {
        using MemoryStream canonical = new();
        using (BinaryWriter writer = new(canonical, Encoding.UTF8, leaveOpen: true))
        {
            foreach (Token token in Lexer.Tokenize(shown, path)) { writer.Write((byte)token.Kind); writer.Write(token.Text); }
            if (scope is not null)
            {
                foreach (var import in scope.Imports) { writer.Write(import.In); writer.Write(import.Namespace); }
                foreach (var alias in scope.Aliases) { writer.Write(alias.In); writer.Write(alias.Alias); writer.Write(alias.Target); }
            }
        }
        return SHA256.HashData(canonical.ToArray())[..bytes];
    }

    public static void Write(string output, IEnumerable<string> paths, string assembly,
        IReadOnlyCollection<string>? symbols = null, int memoryBytes = 1024 * 1024,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? fileSymbols = null,
        Func<string, bool>? librarySource = null, string? on = null, Action? interruptHandler = null)
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
            bool handlerSaid = false;
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
                    // A DECLARATION THAT CAN MAKE AN INTERRUPT HANDLER
                    // (Binder.DeclaresInterruptHandler): said to the build,
                    // whose units keep their facts for one.
                    if (interruptHandler is not null && !handlerSaid && Binder.DeclaresInterruptHandler(type))
                    {
                        handlerSaid = true;
                        interruptHandler();
                    }
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
                    // most of a private member is the type's own business,
                    // and editing it leaves current the units compiled
                    // against the type (DeclarationHash). What still counts is
                    // what they read of it (Hiding): what lays the object out
                    // -- an instance field, by its place, modifiers and type
                    // -- whether it has a constructor of its own, and whether
                    // it has statics to make. A type whose bodies another
                    // unit may copy is hashed whole (HidesPrivate).
                    StringBuilder visible = new();
                    bool filtered = HidesPrivate(type) && !bodies.Any(b => b.Template);
                    var nested = unit.Types.Where(other => other != type && other.SourceFrom >= type.SourceFrom && other.SourceTo <= type.SourceTo).ToArray();
                    bool byMember = ByMember(type, librarySide);
                    var members = filtered || byMember
                        ? parser.MemberSpans.Where(m => m.From >= type.SourceFrom && m.To <= type.SourceTo
                            && !nested.Any(n => m.From >= n.SourceFrom && m.To <= n.SourceTo)).OrderBy(m => m.From).ToArray()
                        : Array.Empty<(int From, int To, MemberDecl Member)>();
                    // EACH SPAN'S DECLARATORS: `static int a = 1, b = F();` is one
                    // span, its b a member of its own with none (the parser's
                    // More), found by where it is written. A span is an
                    // initialised static where any of them is one, and where
                    // any is a static with an initialiser, private or not,
                    // the span is the shape's (Statics): what another unit
                    // reads of it is whether the type has startup work and
                    // where the run of statics the link may make ends.
                    List<MemberDecl>[] declarators = new List<MemberDecl>[members.Length];
                    PrivateMember[] spanHiding = new PrivateMember[members.Length];
                    bool[] spanStatics = new bool[members.Length];
                    for (int i = 0; i < members.Length; i++)
                    {
                        declarators[i] = new List<MemberDecl> { members[i].Member };
                        spanHiding[i] = Hiding(members[i].Member);
                    }
                    if (members.Length > 0)
                    {
                        HashSet<MemberDecl> spannedOnce = new(members.Select(member => member.Member), ReferenceEqualityComparer.Instance);
                        Dictionary<(int Line, int Col), int>? where = null;
                        foreach (MemberDecl extra in type.Members)
                        {
                            if (extra is not FieldDecl || spannedOnce.Contains(extra)) continue;
                            if (where is null)
                            {
                                where = new();
                                foreach (Token placed in tokens)
                                    if (placed.Pos >= type.SourceFrom && placed.Pos < type.SourceTo) where.TryAdd((placed.Line, placed.Col), placed.Pos);
                            }
                            if (!where.TryGetValue((extra.Line, extra.Col), out int pos)) continue;
                            int holder = Array.FindIndex(members, member => pos >= member.From && pos < member.To);
                            if (holder < 0) continue;
                            declarators[holder].Add(extra);
                            if (spanHiding[holder] == PrivateMember.Left && Hiding(extra) == PrivateMember.Initialised) spanHiding[holder] = PrivateMember.Initialised;
                        }
                    }
                    for (int i = 0; i < members.Length; i++) spanStatics[i] = declarators[i].Any(Statics);
                    int memberIndex = 0, braces = 0, brackets = 0;
                    // The last private static marked in the shape, and where it
                    // ended: one like it right after says nothing more.
                    int markedMember = -1, markEnd = -1;
                    string? lastMark = null;
                    // Each `[` open: a length or an index (true), or a collection expression's.
                    Stack<bool> squares = new();
                    PrivateMember hiding = PrivateMember.Shown;
                    bool markedCtor = false, markedStatics = false;
                    // AND EACH MEMBER APART, where they can be told apart
                    // (ByMember): what a token shows goes to its member's text
                    // or to the type's shape, a nested type's to neither -- it
                    // is a key of its own.
                    StringBuilder shapeText = new();
                    Dictionary<string, (MemberDecl Member, StringBuilder Text)> memberTexts = new(StringComparer.Ordinal);
                    StringBuilder? routed = null;
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
                        { memberIndex++; markedCtor = false; braces = 0; brackets = 0; squares.Clear(); }
                        PrivateMember ownHiding = memberIndex < members.Length && token.Pos >= members[memberIndex].From
                            ? spanHiding[memberIndex] : PrivateMember.Shown;
                        hiding = filtered ? ownHiding : PrivateMember.Shown;
                        routed = null;
                        if (byMember && !nested.Any(n => token.Pos >= n.SourceFrom && token.Pos < n.SourceTo))
                        {
                            routed = shapeText;
                            // A private member's tokens show nothing of their own
                            // (Hiding): no name is made for one.
                            if (memberIndex < members.Length && token.Pos >= members[memberIndex].From && hiding == PrivateMember.Shown
                                && !spanStatics[memberIndex] && MemberName(members[memberIndex].Member) is string own)
                            {
                                if (!memberTexts.TryGetValue(own, out var said)) memberTexts[own] = said = (members[memberIndex].Member, new StringBuilder());
                                routed = said.Text;
                            }
                        }
                        if (hiding == PrivateMember.Constructor && !markedCtor) { visible.Append("ctor ; "); if (byMember) shapeText.Append("ctor ; "); markedCtor = true; }
                        markedStatics |= hiding == PrivateMember.Initialised;
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
                                if (hiding == PrivateMember.Shown) { visible.Append(standIn); routed?.Append(standIn); }
                                else if (copies) visible.Append("MemberwiseClone ; ");
                                // Whether objects are copied by bytes is the type's.
                                if (copies && byMember) shapeText.Append("MemberwiseClone ; ");
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
                        if (hiding == PrivateMember.Shown)
                        {
                            visible.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                            // A private static's initialiser written out (a type
                            // whose private members are shown): to its shape as
                            // below, not as written.
                            if (byMember && ownHiding == PrivateMember.Initialised && ReferenceEquals(routed, shapeText)) InitialiserToShape(token, tokenIndex);
                            else routed?.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                        }
                        else if (hiding == PrivateMember.Field && !parser.FieldInitialisers.Any(i => token.Pos >= i.From && token.Pos < i.To))
                        {
                            if (parser.FieldNames.Contains(token.Pos))
                            {
                                visible.Append("_ ");
                                if (byMember) shapeText.Append("_ ");
                            }
                            else
                            {
                                visible.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                                if (byMember) shapeText.Append(text, token.Pos, ends[tokenIndex] - token.Pos).Append(' ');
                            }
                        }
                        else if (hiding == PrivateMember.Initialised && byMember) InitialiserToShape(token, tokenIndex);
                    }
                    // A PRIVATE STATIC'S INITIALISER, to the shape: what every
                    // unit touching the type reads of it (Binder.StaticInitialisers,
                    // Lowering.TouchType) and no more. Each initialised
                    // declarator's class in order (Binder.InitialiserClasses),
                    // once for a run of them alike: a static more or less among
                    // others of its class -- Ide's shutdown list removed --
                    // changes nothing another unit compiles. An array made,
                    // which may be a table laid down as data, by its tokens:
                    // a literal without
                    // its value -- a table's bytes are its own unit's -- but a
                    // length's with it, in `[ ]` outside any `{ }`:
                    // `new byte[60000]` is a zeroed table and `new byte[70000]`
                    // code (Binder.ZeroedArrayOf).
                    void InitialiserToShape(Token token, int tokenIndex)
                    {
                        if (Binder.InitialiserClasses(declarators[memberIndex]) is { } marks)
                        {
                            if (markedMember == memberIndex) return;
                            markedMember = memberIndex;
                            foreach (string mark in marks)
                            {
                                if (mark == lastMark && shapeText.Length == markEnd) continue;
                                shapeText.Append("static static ").Append(mark).Append(" ; ");
                                lastMark = mark;
                                markEnd = shapeText.Length;
                            }
                            return;
                        }
                        {
                            if (token.Kind == Tok.LBrace) braces++;
                            else if (token.Kind == Tok.RBrace && braces > 0) braces--;
                            // A `[` after a type, a name, `)` or `]` holds a length
                            // or an index; any other begins a collection
                            // expression, whose elements are a table's as in `{ }`.
                            else if (token.Kind == Tok.LBracket)
                            {
                                Tok before = tokenIndex > 0 ? tokens[tokenIndex - 1].Kind : Tok.End;
                                // `?` too: `new string?[8000]`'s length is one.
                                bool sized = before is Tok.Ident or Tok.RBracket or Tok.RParen or Tok.Gt or Tok.Question || before.ToString().StartsWith("Kw", StringComparison.Ordinal);
                                squares.Push(sized);
                                if (sized) brackets++; else braces++;
                            }
                            else if (token.Kind == Tok.RBracket && squares.Count > 0)
                            {
                                if (squares.Pop()) brackets--; else braces--;
                            }
                            bool length = brackets > 0 && braces == 0;
                            if (!length && token.Kind is Tok.Int or Tok.Real or Tok.Str or Tok.Char or Tok.Utf8Str or Tok.InterpStr) shapeText.Append(token.Kind.ToString());
                            else shapeText.Append(text, token.Pos, ends[tokenIndex] - token.Pos);
                            shapeText.Append(' ');
                        }
                    }
                    shaped.Flush();
                    // Once, wherever they were written.
                    if (markedStatics) visible.Append("static ;");
                    // EVERY NAME ANOTHER UNIT CAN ASK, ITS OWN HASH OR THE SHAPE'S.
                    // A member with no span of its own -- `const int A = 1, B = 2`'s
                    // B, an event's hoisted remove_ -- was written inside another's,
                    // and its names are that one's text's (or the shape's, where
                    // that one is the shape's). One found inside none: the type's
                    // members are not told apart.
                    Dictionary<string, HashSet<string>> alsoNamed = new(StringComparer.Ordinal);
                    if (byMember)
                    {
                        HashSet<MemberDecl> spanned = new(members.Select(member => member.Member), ReferenceEqualityComparer.Instance);
                        Dictionary<(int Line, int Col), int>? at = null;
                        foreach (MemberDecl unspanned in type.Members)
                        {
                            if (spanned.Contains(unspanned) || filtered && Hiding(unspanned) != PrivateMember.Shown) continue;
                            if (at is null)
                            {
                                at = new();
                                foreach (Token placed in tokens)
                                    if (placed.Pos >= type.SourceFrom && placed.Pos < type.SourceTo) at.TryAdd((placed.Line, placed.Col), placed.Pos);
                            }
                            int inside = at.TryGetValue((unspanned.Line, unspanned.Col), out int pos)
                                ? Array.FindIndex(members, member => pos >= member.From && pos < member.To) : -1;
                            if (inside < 0) { byMember = false; break; }
                            if ((!filtered || Hiding(members[inside].Member) == PrivateMember.Shown) && MemberName(members[inside].Member) is string holder
                                && memberTexts.ContainsKey(holder))
                            {
                                if (!alsoNamed.TryGetValue(holder, out HashSet<string>? names)) alsoNamed[holder] = names = new(StringComparer.Ordinal);
                                names.UnionWith(Monomorphiser.MemberNames(new List<MemberDecl> { unspanned }));
                                names.Add(unspanned.Name);
                            }
                        }
                    }
                    // WHETHER IT HAS STATICS TO MAKE, whoever's they are: every
                    // unit touching the type reads that (Lowering.TouchType).
                    // An initialised static's text is the shape's already
                    // (MemberName; a private one's above); this says so even of
                    // a static constructor's type.
                    if (byMember && type.Members.Any(member => (member.Mods & Mods.Static) != 0
                            && member is FieldDecl { Init: not null } or PropertyDecl { Init: not null } or MethodDecl { IsCtor: true }))
                        shapeText.Append("static ;");
                    // EACH MEMBER WITH THE CONSTANTS OF THE TYPE IT NAMES, and
                    // theirs: `C = P + 1`, `B(int k = P)` change with P, and a
                    // unit asking for C or B is not one asking for P -- the
                    // binder works its constants out quietly (Binder.Run). The
                    // shape too: `new byte[P]` is a table or code by P's value,
                    // `fixed byte b[P]` its layout, read quietly as well.
                    Dictionary<string, string> constantHolders = new(StringComparer.Ordinal);
                    foreach (var held in memberTexts)
                        if (held.Value.Member is FieldDecl { Mods: var constMods } && (constMods & Mods.Const) != 0)
                            foreach (string name in (alsoNamed.GetValueOrDefault(held.Key) ?? new HashSet<string>(StringComparer.Ordinal)).Append(held.Key))
                                constantHolders.TryAdd(name, held.Key);
                    string WithConstants(string text, string? own)
                    {
                        if (constantHolders.Count == 0) return text;
                        SortedSet<string> named = new(StringComparer.Ordinal);
                        Queue<string> reading = new();
                        reading.Enqueue(text);
                        while (reading.Count > 0)
                            foreach (Token word in Lexer.Tokenize(reading.Dequeue(), path))
                                // An interpolated string's holes are expressions in its
                                // text: every word of it a name it may hold.
                                foreach (string name in word.Kind == Tok.Ident ? new[] { word.Text }
                                    : word.Kind == Tok.InterpStr ? Words(word.Text) : Array.Empty<string>())
                                    if (constantHolders.TryGetValue(name, out string? holder) && holder != own && named.Add(holder))
                                        reading.Enqueue(memberTexts[holder].Text.ToString());
                        StringBuilder said = new(text);
                        foreach (string holder in named) said.Append("\n").Append(holder).Append(" : ").Append(memberTexts[holder].Text);
                        return said.ToString();
                    }
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
                        HeaderHash = HeaderHash(tokens, type, scope),
                        MemberNames = Monomorphiser.MemberNames(type.Members).Order(StringComparer.Ordinal).ToArray(),
                        ConditionalSymbols = (activeSymbols ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        // The shape, with what its template bodies name (a type with generic methods).
                        ShapeHash = byMember ? SHA256.HashData(Hashed(WithConstants(shapeText.ToString(), null), path, scope, 32).Concat(SHA256.HashData(shape.ToArray())).ToArray()) : Array.Empty<byte>(),
                        MemberHashes = byMember
                            ? memberTexts.SelectMany(member =>
                                {
                                    byte[] said = Hashed(WithConstants(member.Value.Text.ToString(), member.Key), path, null, 16);
                                    return LookedUpBy(member.Value.Member, member.Key)
                                        .Concat(alsoNamed.GetValueOrDefault(member.Key) ?? Enumerable.Empty<string>())
                                        .Select(name => (Name: name, Hash: said));
                                })
                                // A name under two texts takes both: none is left out.
                                .GroupBy(member => member.Name, StringComparer.Ordinal)
                                .Select(group => group.Count() == 1 ? group.First() : (Name: group.Key, Hash: SHA256.HashData(group
                                    .Select(member => member.Hash).OrderBy(Convert.ToHexString, StringComparer.Ordinal).SelectMany(bytes => bytes).ToArray())[..16]))
                                .OrderBy(member => member.Name, StringComparer.Ordinal).ToArray()
                            : Array.Empty<(string, byte[])>(),
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
