#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Corsac.Lang;

/// <summary>
/// GENERIC INTERMEDIATE REPRESENTATION: a generic template, in a library, in a
/// form a consumer can instantiate.
///
/// The problem it solves is stated in docs/image-format.md. A library's
/// header carries declarations with the bodies stripped, and a template with no
/// body cannot be instantiated -- so `List` was simply not a known type to
/// anything that linked the standard library as a binary.
///
/// The first answer was to put the template into the header AS SOURCE and let
/// the consumer re-parse it. That worked, and it was wrong for a reason worth
/// writing down: it couples a COMPILED library to the compiler's SOURCE
/// LANGUAGE. A library built today would have to keep parsing against next
/// year's front end for ever, and the day the grammar moved, every binary
/// library on the disc would stop being instantiable with no way to tell
/// without trying. This is a TREE behind a version number instead.
///
/// What a template has to carry is not only its shape. Layout and dispatch are
/// decided by the library when it compiles the canonical copy, and a consumer
/// building its own specialisation has to agree with those decisions exactly or
/// the two disagree about where a field is -- so field layout RULES, vtable
/// slots and the static-slot count travel with the tree.
///
/// The format is little-endian and self-describing, and it begins with a
/// version. An older reader refuses a newer major rather than guessing.
/// </summary>
public static class Gir
{
    /// <summary>Bumped when the shape of a record changes and old readers must refuse.</summary>
    // Bumped when constructor calls learned to carry argument names: an old
    // reader would take the name count for the argument count.
    //
    // 6: a record whose declaration has NO type parameters is a members-only
    // carrier -- the generic METHODS of a non-generic type, bodies intact,
    // to be merged into the consumer's view of that type rather than declared
    // as a type of its own. The bytes did not change; the meaning did, and an
    // old reader would declare the carrier as a duplicate type.
    //
    // 7: a declaration carries the names of its attributes. `[Flags]` decides
    // what an enum prints, so dropping it on the way through here would make a
    // template's enum print differently from the same enum compiled directly.
    public const ushort Major = 8;

    /// <summary>Bumped when something is APPENDED that an old reader can ignore.</summary>
    public const ushort Minor = 0;

    private static ReadOnlySpan<byte> Magic => "GIR0"u8;

    /// <summary>
    /// What a consumer needs to know about one template beyond its tree.
    ///
    /// These are the library's decisions, not the consumer's: the consumer is
    /// building a specialisation the library never saw, and it has to lay it out
    /// the way the library laid out the canonical copy or the shared code reads
    /// the wrong offsets.
    /// </summary>
    public sealed class Template
    {
        public required TypeDecl Decl { get; init; }

        /// <summary>The canonical instantiation's name, or null when it has none.</summary>
        public string? Canon { get; init; }

        /// <summary>Bytes of statics one instantiation needs.</summary>
        public int StaticBytes { get; init; }

        /// <summary>Instance size of the canonical copy, which every word-shaped one shares.</summary>
        public int CanonSize { get; init; }

        /// <summary>Field name to its offset in the canonical copy.</summary>
        public Dictionary<string, int> FieldOffsets { get; } = new(StringComparer.Ordinal);

        /// <summary>Member index to its vtable slot, for members that have one.</summary>
        public Dictionary<int, int> VtableSlots { get; } = new();
    }

    // ---- writing ----------------------------------------------------------

    public static byte[] Write(IReadOnlyList<Template> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        Writer w = new();

        w.Bytes(Magic);
        w.U16(Major);
        w.U16(Minor);
        w.I32(templates.Count);

        foreach (Template t in templates)
        {
            w.Str(t.Decl.Name);
            w.Str(t.Canon);
            w.I32(t.StaticBytes);
            w.I32(t.CanonSize);

            w.I32(t.FieldOffsets.Count);

            foreach ((string name, int at) in t.FieldOffsets)
            {
                w.Str(name);
                w.I32(at);
            }

            w.I32(t.VtableSlots.Count);

            foreach ((int member, int slot) in t.VtableSlots)
            {
                w.I32(member);
                w.I32(slot);
            }

            w.Decl(t.Decl);
        }
        return w.Done();
    }

    // ---- reading ----------------------------------------------------------

    public static List<Template> Read(ReadOnlySpan<byte> bytes, string from)
    {
        if (bytes.Length < 8 || !bytes[..4].SequenceEqual(Magic))
        {
            throw new AsmException(0, $"{from}: not a generic-template section");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);

        // RULE 1 OF THE COMPATIBILITY RULES, applied here as well as to the
        // image: a major this reader does not know is refused rather than
        // guessed at. A tree misread is a program that compiles and is wrong.
        if (major != Major)
        {
            throw new AsmException(0,
                $"{from}: generic templates are format {major}, and this compiler reads {Major}");
        }

        Reader r = new(bytes.ToArray(), 8, from);
        int count = r.I32();
        List<Template> found = new(count);

        for (int i = 0; i < count; i++)
        {
            r.Str();                                    // the name, which is in the decl too
            string? canon = r.StrOrNull();
            int statics = r.I32();
            int size = r.I32();

            Dictionary<string, int> offsets = new(StringComparer.Ordinal);
            int fields = r.I32();

            for (int f = 0; f < fields; f++)
            {
                string name = r.Str();

                offsets[name] = r.I32();
            }

            Dictionary<int, int> slots = new();
            int vtable = r.I32();

            for (int v = 0; v < vtable; v++)
            {
                int member = r.I32();

                slots[member] = r.I32();
            }

            Template t = new()
            {
                Decl = r.Decl(from),
                Canon = canon,
                StaticBytes = statics,
                CanonSize = size,
            };

            foreach ((string name, int at) in offsets)
            {
                t.FieldOffsets[name] = at;
            }

            foreach ((int member, int slot) in slots)
            {
                t.VtableSlots[member] = slot;
            }

            found.Add(t);
        }
        return found;
    }

    // ---- the tags ---------------------------------------------------------
    //
    // NUMBERED AND NEVER RENUMBERED, which is the same rule the image's section
    // tags follow and for the same reason: a number that changes meaning between
    // two versions is a file that reads successfully as the wrong thing.

    // BOTH LISTS MUST COVER THE WHOLE AST, and nothing checks that they do
    // except a program failing to build. The writer's default throws "cannot
    // record a ... in a template", which sounds like a policy and is actually a
    // hole: any node missing from these enums is a node that cannot appear in
    // any generic body in any library. `default(V)!` in Dictionary -- one
    // SuppressExpr, three characters of source -- made lib/std.cor
    // uncompilable and took every disc build down with it, because the
    // serialiser had never learned the node.
    //
    // So when a node is added to Ast.cs, it gets a tag HERE, a writer case, and
    // a reader case, in the same commit. Numbers are append-only: they are an
    // on-disc format, and reusing or reordering them silently misreads every
    // library already built.
    private enum S : byte
    {
        Null = 0,
        Block = 1, Local = 2, ExprStmt = 3, If = 4, While = 5, Do = 6, For = 7,
        Foreach = 8, Return = 9, Break = 10, Continue = 11, Throw = 12,
        Switch = 13, Try = 14, GotoCase = 15, Deconstruct = 16, UsingDecl = 17,
    }

    private enum E : byte
    {
        Null = 0,
        Literal = 1, Name = 2, This = 3, Base = 4, Member = 5, Call = 6,
        Index = 7, SizeOf = 8, Default = 9, New = 10, Unary = 11, Binary = 12,
        Assign = 13, Conditional = 14, Cast = 15, RefArg = 16, Is = 17, As = 18,
        Await = 19, Lambda = 20, SwitchExpr = 21, Suppress = 22, TypeOf = 23,
        Throw = 24, Tuple = 25, Range = 26, FromEnd = 27, With = 28,
        Pattern = 29, Subject = 30,
    }

    private enum M : byte
    {
        Field = 1, Method = 2, Property = 3,
    }

    // ---- the writer -------------------------------------------------------

    private sealed class Writer
    {
        private readonly List<byte> _out = new();

        public byte[] Done() => _out.ToArray();

        public void Bytes(ReadOnlySpan<byte> b) => _out.AddRange(b.ToArray());
        public void U8(byte b) => _out.Add(b);
        public void Bool(bool b) => _out.Add(b ? (byte)1 : (byte)0);

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];

            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            Bytes(b);
        }

        public void I32(int v)
        {
            Span<byte> b = stackalloc byte[4];

            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            Bytes(b);
        }

        public void I64(long v)
        {
            Span<byte> b = stackalloc byte[8];

            BinaryPrimitives.WriteInt64LittleEndian(b, v);
            Bytes(b);
        }

        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));

        /// <summary>A string, or null, as a length and its bytes. -1 is null.</summary>
        public void Str(string? s)
        {
            if (s is null)
            {
                I32(-1);
                return;
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(s);

            I32(utf8.Length);
            Bytes(utf8);
        }

        public void Type(TypeRef? t)
        {
            if (t is null)
            {
                Bool(false);
                return;
            }

            Bool(true);
            Str(t.Name);
            I32(t.ArrayRank);
            Bool(t.Nullable);
            I32(t.PointerDepth);
            I32(t.Args.Count);

            foreach (TypeRef a in t.Args)
            {
                Type(a);
            }
        }

        public void Param(Param p)
        {
            Str(p.Name);
            Type(p.Type);
            Bool(p.IsRef);
            Bool(p.IsOut);
            Bool(p.IsReadOnlyRef);
            Bool(p.IsParams);

            // `this` on a first parameter makes the method an extension, and a
            // template's are read back by a consumer that has to know it.
            Bool(p.IsThis);

            // AND WHAT AN ANSWER PROVES ABOUT IT: [NotNullWhen(false)] on
            // string.IsNullOrEmpty is what makes the check usable, and a
            // consumer reading the library back needs it as much as the
            // compile that wrote it did.
            Bool(p.NotNullWhen.HasValue);
            Bool(p.NotNullWhen ?? false);
            Expr(p.Default);
        }

        public void Decl(TypeDecl d)
        {
            U8((byte)d.Kind);
            Str(d.Name);
            I32((int)d.Mods);

            I32(d.TypeParams.Count);

            foreach (TypeParam p in d.TypeParams)
            {
                Str(p.Name);
                I32(p.Constraints.Count);

                // THE CONSTRAINTS TRAVEL WITH IT, so a consumer can check its
                // own type arguments rather than discovering the mistake in the
                // code generator, which is a message about registers.
                foreach (TypeRef c in p.Constraints)
                {
                    Type(c);
                }
            }

            I32(d.Bases.Count);

            foreach (TypeRef b in d.Bases)
            {
                Type(b);
            }

            I32(d.Attributes.Count);

            foreach (string a in d.Attributes)
            {
                Str(a);
            }

            I32(d.EnumMembers.Count);

            foreach (EnumMember e in d.EnumMembers)
            {
                Str(e.Name);
                Expr(e.Value);
            }

            I32(d.Members.Count);

            foreach (MemberDecl m in d.Members)
            {
                Member(m);
            }
        }

        private void Member(MemberDecl m)
        {
            switch (m)
            {
                case FieldDecl f:
                    U8((byte)M.Field);
                    Str(f.Name);
                    I32((int)f.Mods);
                    I32(f.TemplateIndex);
                    Type(f.Type);
                    Expr(f.Init);
                    break;

                case MethodDecl d:
                    U8((byte)M.Method);
                    Str(d.Name);
                    I32((int)d.Mods);
                    I32(d.TemplateIndex);
                    Type(d.Returns);
                    Bool(d.IsCtor);

                    // The parameter this method's result is null only for.
                    Str(d.NotNullIfNotNull ?? "");
                    I32(d.TypeParams.Count);

                    foreach (TypeParam p in d.TypeParams)
                    {
                        Str(p.Name);
                    }

                    I32(d.Params.Count);

                    foreach (Param p in d.Params)
                    {
                        Param(p);
                    }

                    Stmt(d.Body);

                    if (d.Init is null)
                    {
                        Bool(false);
                    }
                    else
                    {
                        Bool(true);
                        Bool(d.Init.IsThis);
                        I32(d.Init.Args.Count);

                        foreach (Expr a in d.Init.Args)
                        {
                            Expr(a);
                        }
                    }
                    break;

                case PropertyDecl p2:
                    U8((byte)M.Property);
                    Str(p2.Name);
                    I32((int)p2.Mods);
                    I32(p2.TemplateIndex);
                    Type(p2.Type);
                    Bool(p2.Auto);
                    Bool(p2.HasSetter);
                    Stmt(p2.Getter);
                    Stmt(p2.Setter);
                    Expr(p2.Init);
                    I32(p2.Params.Count);

                    foreach (Param ip in p2.Params)
                    {
                        Param(ip);
                    }
                    break;

                default:
                    throw new AsmException(0, $"cannot record a {m.GetType().Name} in a template");
            }
        }

        public void Stmt(Stmt? s)
        {
            switch (s)
            {
                case null:
                    U8((byte)S.Null);
                    break;

                case Block b:
                    U8((byte)S.Block);
                    U8(b.ArithmeticContext);
                    I32(b.Statements.Count);

                    foreach (Stmt one in b.Statements)
                    {
                        Stmt(one);
                    }
                    break;

                case LocalDecl d:
                    U8((byte)S.Local);
                    Type(d.Type);
                    Str(d.Name);
                    Expr(d.Init);
                    U8(d.LocalFunction ? (byte)1 : (byte)0);
                    I32(d.Also.Count);
                    foreach (LocalDecl also in d.Also) { Stmt(also); }
                    break;

                case ExprStmt e:
                    U8((byte)S.ExprStmt);
                    Expr(e.Expr);
                    break;

                case IfStmt i:
                    U8((byte)S.If);
                    Expr(i.Cond);
                    Stmt(i.Then);
                    Stmt(i.Else);
                    break;

                case WhileStmt w:
                    U8((byte)S.While);
                    Expr(w.Cond);
                    Stmt(w.Body);
                    break;

                case DoStmt d2:
                    U8((byte)S.Do);
                    Expr(d2.Cond);
                    Stmt(d2.Body);
                    break;

                case ForStmt f:
                    U8((byte)S.For);
                    Stmt(f.Init);
                    Expr(f.Cond);
                    I32(f.Step.Count);

                    foreach (Expr step in f.Step)
                    {
                        Expr(step);
                    }

                    Stmt(f.Body);
                    break;

                case ForeachStmt fe:
                    U8((byte)S.Foreach);
                    Type(fe.Type);
                    Str(fe.Name);
                    Bool(fe.Bindings != null);

                    if (fe.Bindings is { } bound)
                    {
                        Bindings(bound);
                    }

                    Expr(fe.Sequence);
                    Stmt(fe.Body);
                    break;

                case ReturnStmt r:
                    U8((byte)S.Return);
                    Expr(r.Value);
                    break;

                case BreakStmt:
                    U8((byte)S.Break);
                    break;

                case ContinueStmt:
                    U8((byte)S.Continue);
                    break;

                case GotoCaseStmt g:
                    U8((byte)S.GotoCase);
                    U8(g.IsDefault ? (byte)1 : (byte)0);
                    Expr(g.Value);
                    break;

                case ThrowStmt t:
                    U8((byte)S.Throw);
                    Expr(t.Value);
                    break;

                case SwitchStmt sw:
                    U8((byte)S.Switch);
                    Expr(sw.Subject);
                    I32(sw.Cases.Count);

                    foreach (SwitchCase c in sw.Cases)
                    {
                        Expr(c.Pattern);
                        I32(c.Body.Count);

                        foreach (Stmt one in c.Body)
                        {
                            Stmt(one);
                        }
                    }
                    break;

                case TryStmt tr:
                    U8((byte)S.Try);
                    Stmt(tr.Body);
                    I32(tr.Catches.Count);

                    foreach (CatchClause c in tr.Catches)
                    {
                        Type(c.Type);
                        Str(c.Name);
                        Stmt(c.Body);
                    }

                    Stmt(tr.Finally);
                    break;

                case DeconstructStmt de:
                    U8((byte)S.Deconstruct);
                    Bindings(de.Names);
                    Expr(de.Value);
                    break;

                case UsingDeclStmt u:
                    U8((byte)S.UsingDecl);
                    Stmt(u.Declaration);
                    break;

                default:
                    throw new AsmException(0, $"cannot record a {s.GetType().Name} in a template");
            }
        }

        /// <summary>The names a deconstruction binds, and the lists inside them.</summary>
        public void Bindings(List<Binding> names)
        {
            I32(names.Count);

            foreach (Binding b in names)
            {
                Type(b.Type);
                Str(b.Name);
                Expr(b.Target);
                Bool(b.Nested != null);

                if (b.Nested is { } inner)
                {
                    Bindings(inner);
                }
            }
        }

        /// <summary>One pair of initialiser braces, with everything inside it.</summary>
        public void Body(InitBody body)
        {
            I32(body.Inits.Count);

            foreach (InitAssign init in body.Inits)
            {
                Str(init.Name);
                Bool(init.Nested != null);

                if (init.Nested is InitBody nested)
                {
                    Body(nested);
                }
                else
                {
                    Expr(init.Value);
                }
            }

            I32(body.Adds.Count);

            foreach (InitAdd add in body.Adds)
            {
                I32(add.Args.Count);

                foreach (Expr a in add.Args)
                {
                    Expr(a);
                }
            }

            I32(body.Indexes.Count);

            foreach (InitIndex one in body.Indexes)
            {
                I32(one.Args.Count);

                foreach (Expr a in one.Args)
                {
                    Expr(a);
                }
                Expr(one.Value);
            }
        }

        public void Expr(Expr? e)
        {
            switch (e)
            {
                case null:
                    U8((byte)E.Null);
                    break;

                case LiteralExpr l:
                    U8((byte)E.Literal);
                    U8((byte)l.Kind);
                    Str(l.Text);
                    I64(l.IntValue);
                    F64(l.RealValue);
                    break;

                case NameExpr n:
                    U8((byte)E.Name);
                    Str(n.Name);
                    I32(n.TypeArgs.Count);

                    foreach (TypeRef a in n.TypeArgs)
                    {
                        Type(a);
                    }
                    break;

                case ThisExpr:
                    U8((byte)E.This);
                    break;

                case BaseExpr:
                    U8((byte)E.Base);
                    break;

                case MemberExpr m:
                    U8((byte)E.Member);
                    Expr(m.Target);
                    Str(m.Name);
                    Bool(m.NullConditional);
                    I32(m.TypeArgs.Count);

                    foreach (TypeRef a in m.TypeArgs)
                    {
                        Type(a);
                    }
                    break;

                case CallExpr c:
                    U8((byte)E.Call);
                    Expr(c.Target);

                    // THE NAMES GO WITH THE ARGUMENTS. A template's bodies are
                    // never checked here -- the binder only sees the
                    // specialisations -- so nothing has put `With(nullable:
                    // true)` into parameter order yet, and a consumer reading
                    // this tree back has to be able to.
                    //
                    // Written as a count that is either zero or the argument
                    // count, so a call with no names costs one byte more than
                    // it did and never a string.
                    I32(c.ArgNames.Count);

                    foreach (string? n in c.ArgNames)
                    {
                        Str(n ?? "");
                    }

                    I32(c.Args.Count);

                    foreach (Expr a in c.Args)
                    {
                        Expr(a);
                    }
                    break;

                case IndexExpr ix:
                    U8((byte)E.Index);
                    Expr(ix.Target);
                    I32(ix.Args.Count);

                    foreach (Expr a in ix.Args)
                    {
                        Expr(a);
                    }
                    break;

                case SizeOfExpr so:
                    U8((byte)E.SizeOf);
                    Type(so.Type);
                    break;

                case DefaultExpr df:
                    U8((byte)E.Default);
                    Type(df.Type);
                    break;

                case NewExpr nw:
                    U8((byte)E.New);
                    Type(nw.Type);
                    Expr(nw.ArraySize);
                    I32(nw.ArgNames.Count);

                    foreach (string? name in nw.ArgNames)
                    {
                        Str(name ?? "");
                    }
                    I32(nw.Args.Count);

                    foreach (Expr a in nw.Args)
                    {
                        Expr(a);
                    }

                    Body(nw.Body);
                    break;

                case UnaryExpr u:
                    U8((byte)E.Unary);
                    U8((byte)u.Op);
                    Expr(u.Operand);
                    break;

                case BinaryExpr b:
                    U8((byte)E.Binary);
                    U8((byte)b.Op);
                    Bool(b.PatternNullTest);
                    Expr(b.Left);
                    Expr(b.Right);
                    break;

                case AssignExpr a2:
                    U8((byte)E.Assign);
                    Bool(a2.Op.HasValue);
                    U8(a2.Op.HasValue ? (byte)a2.Op.Value : (byte)0);
                    Expr(a2.Target);
                    Expr(a2.Value);
                    break;

                case ConditionalExpr c2:
                    U8((byte)E.Conditional);
                    Expr(c2.Cond);
                    Expr(c2.Then);
                    Expr(c2.Else);
                    break;

                case CastExpr cast:
                    U8((byte)E.Cast);
                    Type(cast.Type);
                    Expr(cast.Operand);
                    break;

                case RefArgExpr ra:
                    U8((byte)E.RefArg);
                    Expr(ra.Target);
                    Bool(ra.IsOut);
                    Type(ra.Declare);
                    Str(ra.Name);
                    break;

                case IsExpr isx:
                    U8((byte)E.Is);
                    Expr(isx.Operand);
                    Type(isx.Type);
                    Str(isx.Binding);
                    break;

                case AsExpr asx:
                    U8((byte)E.As);
                    Expr(asx.Operand);
                    Type(asx.Type);
                    break;

                case AwaitExpr aw:
                    U8((byte)E.Await);
                    Expr(aw.Operand);
                    break;

                case LambdaExpr la:
                    U8((byte)E.Lambda);
                    Bool(la.Async);
                    I32(la.Params.Count);

                    foreach (Param p in la.Params)
                    {
                        Param(p);
                    }

                    Expr(la.Body);
                    Stmt(la.BlockBody);
                    break;

                case SwitchExpr se:
                    U8((byte)E.SwitchExpr);
                    Expr(se.Subject);
                    I32(se.Arms.Count);

                    foreach (SwitchArm arm in se.Arms)
                    {
                        Expr(arm.Value);
                        Type(arm.Type);
                        Str(arm.Binding);
                        Expr(arm.When);
                        Bool(arm.Discard);
                        Expr(arm.Result);
                    }
                    break;

                case SuppressExpr sup:
                    U8((byte)E.Suppress);
                    Expr(sup.Operand);
                    break;

                case TypeOfExpr ty:
                    U8((byte)E.TypeOf);
                    Type(ty.Type);
                    break;

                case ThrowExpr te:
                    U8((byte)E.Throw);
                    Expr(te.Value);
                    break;

                case TupleExpr tu:
                    U8((byte)E.Tuple);
                    I32(tu.Items.Count);

                    foreach (Expr item in tu.Items)
                    {
                        Expr(item);
                    }

                    // The names travel with the values: `(At: 1, Label: s)` is a
                    // different type from `(1, s)` and the reader must rebuild
                    // the one that was written.
                    I32(tu.Names.Count);

                    foreach (string name in tu.Names)
                    {
                        Str(name);
                    }
                    break;

                case RangeExpr ra:
                    U8((byte)E.Range);
                    Expr(ra.From);
                    Expr(ra.To);
                    break;

                case FromEndExpr fe:
                    U8((byte)E.FromEnd);
                    Expr(fe.Offset);
                    break;

                case WithExpr wi:
                    U8((byte)E.With);
                    Expr(wi.Source);
                    Body(wi.Body);
                    break;

                // A pattern's subject slot is assigned by the BINDER, per
                // compilation. Only the tree is recorded; whoever instantiates
                // the template binds it afresh and gets a slot of their own.
                case PatternExpr pat:
                    U8((byte)E.Pattern);
                    Expr(pat.Subject);
                    Expr(pat.Test);
                    break;

                case SubjectExpr:
                    U8((byte)E.Subject);
                    break;

                default:
                    throw new AsmException(0, $"cannot record a {e.GetType().Name} in a template");
            }
        }
    }

    // ---- the reader -------------------------------------------------------

    private sealed class Reader
    {
        private readonly byte[] _in;
        private readonly string _from;
        private int _at;

        public Reader(byte[] bytes, int at, string from)
        {
            _in = bytes;
            _at = at;
            _from = from;
        }

        /// <summary>
        /// Every read goes through here, so a truncated section is a message
        /// rather than an index out of range with a stack trace through the
        /// deserialiser -- which tells whoever is holding the library nothing.
        /// </summary>
        private void Need(int bytes)
        {
            if (_at + bytes > _in.Length)
            {
                throw new AsmException(0, $"{_from}: the generic templates are truncated");
            }
        }

        public byte U8()
        {
            Need(1);
            return _in[_at++];
        }

        public bool Bool() => U8() != 0;

        public int I32()
        {
            Need(4);

            int v = BinaryPrimitives.ReadInt32LittleEndian(_in.AsSpan(_at));

            _at += 4;
            return v;
        }

        public long I64()
        {
            Need(8);

            long v = BinaryPrimitives.ReadInt64LittleEndian(_in.AsSpan(_at));

            _at += 8;
            return v;
        }

        public double F64() => BitConverter.Int64BitsToDouble(I64());

        public string Str() => StrOrNull() ?? "";

        public string? StrOrNull()
        {
            int n = I32();

            if (n < 0)
            {
                return null;
            }

            Need(n);

            string s = Encoding.UTF8.GetString(_in, _at, n);

            _at += n;
            return s;
        }

        /// <summary>How many items a list claims, refused when it cannot be true.</summary>
        private int Count()
        {
            int n = I32();

            // A COUNT IS BOUNDED BY WHAT IS LEFT. Without this a corrupt length
            // asks for a list of two billion nodes and the compiler dies of
            // memory rather than saying which library is damaged.
            if (n < 0 || n > _in.Length - _at)
            {
                throw new AsmException(0, $"{_from}: the generic templates are damaged");
            }
            return n;
        }

        public TypeRef? TypeOrNull()
        {
            if (!Bool())
            {
                return null;
            }

            string name = Str();
            int rank = I32();
            bool nullable = Bool();
            int stars = I32();
            int args = Count();

            TypeRef t = new()
            {
                Name = name, ArrayRank = rank, Nullable = nullable, PointerDepth = stars,
                Line = 0, Col = 0,
            };

            for (int i = 0; i < args; i++)
            {
                if (TypeOrNull() is TypeRef a)
                {
                    t.Args.Add(a);
                }
            }
            return t;
        }

        public TypeRef Type() => TypeOrNull()
            ?? throw new AsmException(0, $"{_from}: a type is missing from a template");

        public Param Param()
        {
            string name = Str();
            TypeRef type = Type();
            bool byRef = Bool();
            bool isOut = Bool();
            bool readOnly = Bool();
            bool variadic = Bool();
            bool receiver = Bool();
            bool proves = Bool();
            bool provedWhen = Bool();

            return new Param
            {
                Name = name, Type = type, IsRef = byRef, IsOut = isOut,
                IsReadOnlyRef = readOnly, IsParams = variadic, IsThis = receiver,
                NotNullWhen = proves ? provedWhen : null,
                Default = Expr(),
            };
        }

        public TypeDecl Decl(string file)
        {
            TypeKind kind = (TypeKind)U8();
            string name = Str();
            Mods mods = (Mods)I32();

            TypeDecl d = new() { Kind = kind, Name = name, Mods = mods, File = file, Line = 0, Col = 0 };

            int typeParams = Count();

            for (int i = 0; i < typeParams; i++)
            {
                TypeParam p = new() { Name = Str() };
                int constraints = Count();

                for (int c = 0; c < constraints; c++)
                {
                    p.Constraints.Add(Type());
                }
                d.TypeParams.Add(p);
            }

            int bases = Count();

            for (int i = 0; i < bases; i++)
            {
                d.Bases.Add(Type());
            }

            int attributes = Count();

            for (int i = 0; i < attributes; i++)
            {
                d.Attributes.Add(Str());
            }

            int enums = Count();

            for (int i = 0; i < enums; i++)
            {
                d.EnumMembers.Add(new EnumMember { Name = Str(), Value = Expr() });
            }

            int members = Count();

            for (int i = 0; i < members; i++)
            {
                d.Members.Add(Member());
            }
            return d;
        }

        private MemberDecl Member()
        {
            M kind = (M)U8();

            switch (kind)
            {
                case M.Field:
                {
                    string name = Str();
                    Mods mods = (Mods)I32();
                    int index = I32();
                    TypeRef type = Type();

                    return new FieldDecl { Name = name, Mods = mods, Type = type, Init = Expr(), TemplateIndex = index };
                }

                case M.Method:
                {
                    string name = Str();
                    Mods mods = (Mods)I32();
                    int index = I32();
                    TypeRef? returns = TypeOrNull();
                    bool ctor = Bool();
                    string onlyFor = Str();

                    List<string> typeParams = new();
                    int tp = Count();

                    for (int i = 0; i < tp; i++)
                    {
                        typeParams.Add(Str());
                    }

                    List<Param> ps = new();
                    int np = Count();

                    for (int i = 0; i < np; i++)
                    {
                        ps.Add(Param());
                    }

                    Block? body = Stmt() as Block;
                    CtorInit? init = null;

                    if (Bool())
                    {
                        init = new CtorInit { IsThis = Bool() };

                        int args = Count();

                        for (int i = 0; i < args; i++)
                        {
                            init.Args.Add(Expr() ?? throw new AsmException(0, $"{_from}: a damaged constructor call"));
                        }
                    }

                    MethodDecl d = new()
                    {
                        Name = name, Mods = mods, Returns = returns, IsCtor = ctor,
                        Body = body, Init = init, TemplateIndex = index,
                        NotNullIfNotNull = onlyFor.Length == 0 ? null : onlyFor,
                    };

                    foreach (string one in typeParams)
                    {
                        d.TypeParams.Add(new TypeParam { Name = one });
                    }

                    d.Params.AddRange(ps);
                    return d;
                }

                case M.Property:
                {
                    string name = Str();
                    Mods mods = (Mods)I32();
                    int index = I32();
                    TypeRef type = Type();
                    bool auto = Bool();
                    bool hasSetter = Bool();
                    Block? getter = Stmt() as Block;
                    Block? setter = Stmt() as Block;
                    Expr? init = Expr();

                    PropertyDecl p = new()
                    {
                        Name = name, Mods = mods, Type = type, Auto = auto, HasSetter = hasSetter,
                        Getter = getter, Setter = setter, Init = init, TemplateIndex = index,
                    };

                    int np = Count();

                    for (int i = 0; i < np; i++)
                    {
                        p.Params.Add(Param());
                    }
                    return p;
                }

                default:
                    throw new AsmException(0, $"{_from}: unknown member kind {(byte)kind} in a template");
            }
        }

        public Stmt? Stmt()
        {
            S kind = (S)U8();

            switch (kind)
            {
                case S.Null:
                    return null;

                case S.Block:
                {
                    byte arithmetic = U8();
                    if (arithmetic > 2) throw new AsmException(0, "invalid block arithmetic context");
                    Block b = new() { ArithmeticContext = arithmetic };
                    int n = Count();

                    for (int i = 0; i < n; i++)
                    {
                        if (Stmt() is Stmt one)
                        {
                            b.Statements.Add(one);
                        }
                    }
                    return b;
                }

                case S.Local:
                {
                    TypeRef? type = TypeOrNull();
                    string name = Str();
                    Expr? init = Expr();
                    bool localFunction = U8() != 0;
                    int count = Count();
                    LocalDecl made = new()
                    {
                        Type = type, Name = name, Init = init, LocalFunction = localFunction,
                    };
                    for (int i = 0; i < count; i++)
                    {
                        made.Also.Add(Stmt() as LocalDecl
                            ?? throw new AsmException(0, $"{_from}: a damaged local declaration"));
                    }
                    return made;
                }

                case S.ExprStmt:
                    return new ExprStmt { Expr = Need() };

                case S.If:
                {
                    Expr cond = Need();
                    Stmt then = NeedStmt();

                    return new IfStmt { Cond = cond, Then = then, Else = Stmt() };
                }

                case S.While:
                {
                    Expr cond = Need();

                    return new WhileStmt { Cond = cond, Body = NeedStmt() };
                }

                case S.Do:
                {
                    Expr cond = Need();

                    return new DoStmt { Cond = cond, Body = NeedStmt() };
                }

                case S.For:
                {
                    Stmt? init = Stmt();
                    Expr? cond = Expr();
                    List<Expr> step = new();
                    int n = Count();

                    for (int i = 0; i < n; i++)
                    {
                        step.Add(Need());
                    }

                    ForStmt f = new() { Init = init, Cond = cond, Body = NeedStmt() };

                    f.Step.AddRange(step);
                    return f;
                }

                case S.Foreach:
                {
                    TypeRef? type = TypeOrNull();
                    string name = Str();
                    List<Binding>? bound = Bool() ? ReadBindings() : null;
                    Expr sequence = Need();

                    return new ForeachStmt
                    {
                        Type = type, Name = name, Bindings = bound,
                        Sequence = sequence, Body = NeedStmt(),
                    };
                }

                case S.Return:
                    return new ReturnStmt { Value = Expr() };

                case S.Break:
                    return new BreakStmt();

                case S.Continue:
                    return new ContinueStmt();

                case S.GotoCase:
                    return new GotoCaseStmt { IsDefault = U8() != 0, Value = Expr() };

                case S.Throw:
                    return new ThrowStmt { Value = Need() };

                case S.Switch:
                {
                    SwitchStmt sw = new() { Subject = Need() };
                    int cases = Count();

                    for (int i = 0; i < cases; i++)
                    {
                        SwitchCase c = new() { Pattern = Expr() };
                        int body = Count();

                        for (int j = 0; j < body; j++)
                        {
                            if (Stmt() is Stmt one)
                            {
                                c.Body.Add(one);
                            }
                        }
                        sw.Cases.Add(c);
                    }
                    return sw;
                }

                case S.Try:
                {
                    Block body = Stmt() as Block
                        ?? throw new AsmException(0, $"{_from}: a damaged try");
                    List<CatchClause> catches = new();
                    int n = Count();

                    for (int i = 0; i < n; i++)
                    {
                        TypeRef? type = TypeOrNull();
                        string? name = StrOrNull();
                        Block caught = Stmt() as Block
                            ?? throw new AsmException(0, $"{_from}: a damaged catch");

                        catches.Add(new CatchClause { Type = type, Name = name, Body = caught });
                    }

                    TryStmt tr = new() { Body = body, Finally = Stmt() as Block };

                    tr.Catches.AddRange(catches);
                    return tr;
                }

                case S.Deconstruct:
                {
                    List<Binding> names = ReadBindings();

                    DeconstructStmt de = new() { Value = Need() };

                    de.Names.AddRange(names);
                    return de;
                }

                case S.UsingDecl:
                    return new UsingDeclStmt
                    {
                        Declaration = Stmt() as LocalDecl
                            ?? throw new AsmException(0, $"{_from}: a damaged using declaration"),
                    };

                default:
                    throw new AsmException(0, $"{_from}: unknown statement {(byte)kind} in a template");
            }
        }

        private Stmt NeedStmt() => Stmt()
            ?? throw new AsmException(0, $"{_from}: a statement is missing from a template");

        private Expr Need() => Expr()
            ?? throw new AsmException(0, $"{_from}: an expression is missing from a template");

        /// <summary>The names a deconstruction binds, read back as they were written.</summary>
        private List<Binding> ReadBindings()
        {
            List<Binding> names = new();
            int n = Count();

            for (int i = 0; i < n; i++)
            {
                TypeRef? type = TypeOrNull();
                string name = Str();
                Expr? target = Expr();

                names.Add(new Binding
                {
                    Type = type, Name = name, Target = target,
                    Nested = Bool() ? ReadBindings() : null,
                });
            }
            return names;
        }

        /// <summary>One pair of initialiser braces, read back as it was written.</summary>
        private void ReadBody(InitBody body)
        {
            int inits = Count();

            for (int i = 0; i < inits; i++)
            {
                string name = Str();

                if (Bool())
                {
                    InitBody nested = new();

                    ReadBody(nested);
                    body.Inits.Add(new InitAssign { Name = name, Nested = nested });
                }
                else
                {
                    body.Inits.Add(new InitAssign { Name = name, Value = Need() });
                }
            }

            int adds = Count();

            for (int i = 0; i < adds; i++)
            {
                InitAdd add = new();
                int args = Count();

                for (int j = 0; j < args; j++)
                {
                    add.Args.Add(Need());
                }
                body.Adds.Add(add);
            }

            int indexes = Count();

            for (int i = 0; i < indexes; i++)
            {
                List<Expr> args = new();
                int n = Count();

                for (int j = 0; j < n; j++)
                {
                    args.Add(Need());
                }

                InitIndex one = new() { Value = Need() };

                one.Args.AddRange(args);
                body.Indexes.Add(one);
            }
        }

        public Expr? Expr()
        {
            E kind = (E)U8();

            switch (kind)
            {
                case E.Null:
                    return null;

                case E.Literal:
                {
                    Lit lit = (Lit)U8();
                    string text = Str();
                    long i = I64();

                    return new LiteralExpr { Kind = lit, Text = text, IntValue = i, RealValue = F64() };
                }

                case E.Name:
                {
                    NameExpr n = new() { Name = Str() };
                    int args = Count();

                    for (int i = 0; i < args; i++)
                    {
                        n.TypeArgs.Add(Type());
                    }
                    return n;
                }

                case E.This:
                    return new ThisExpr();

                case E.Base:
                    return new BaseExpr();

                case E.Member:
                {
                    Expr target = Need();
                    string name = Str();
                    bool conditional = Bool();
                    MemberExpr m = new() { Target = target, Name = name, NullConditional = conditional };
                    int args = Count();

                    for (int i = 0; i < args; i++)
                    {
                        m.TypeArgs.Add(Type());
                    }
                    return m;
                }

                case E.Call:
                {
                    CallExpr c = new() { Target = Need() };
                    int names = Count();

                    for (int i = 0; i < names; i++)
                    {
                        string n = Str();

                        c.ArgNames.Add(n.Length == 0 ? null : n);
                    }

                    int args = Count();

                    for (int i = 0; i < args; i++)
                    {
                        c.Args.Add(Need());
                    }
                    return c;
                }

                case E.Index:
                {
                    IndexExpr ix = new() { Target = Need() };
                    int args = Count();

                    for (int i = 0; i < args; i++)
                    {
                        ix.Args.Add(Need());
                    }
                    return ix;
                }

                case E.SizeOf:
                    return new SizeOfExpr { Type = Type() };

                case E.Default:
                    return new DefaultExpr { Type = Type() };

                case E.New:
                {
                    TypeRef type = Type();
                    Expr? size = Expr();
                    NewExpr nw = new() { Type = type, ArraySize = size };
                    int names = Count();

                    for (int i = 0; i < names; i++)
                    {
                        string name = Str();
                        nw.ArgNames.Add(name.Length == 0 ? null : name);
                    }
                    int args = Count();

                    for (int i = 0; i < args; i++)
                    {
                        nw.Args.Add(Need());
                    }

                    ReadBody(nw.Body);
                    return nw;
                }

                case E.Unary:
                {
                    UnOp op = (UnOp)U8();

                    return new UnaryExpr { Op = op, Operand = Need() };
                }

                case E.Binary:
                {
                    BinOp op = (BinOp)U8();
                    bool pattern = Bool();
                    Expr left = Need();

                    return new BinaryExpr
                    {
                        Op = op, Left = left, Right = Need(), PatternNullTest = pattern,
                    };
                }

                case E.Assign:
                {
                    bool compound = Bool();
                    BinOp op = (BinOp)U8();
                    Expr target = Need();

                    return new AssignExpr { Op = compound ? op : null, Target = target, Value = Need() };
                }

                case E.Conditional:
                {
                    Expr cond = Need();
                    Expr then = Need();

                    return new ConditionalExpr { Cond = cond, Then = then, Else = Need() };
                }

                case E.Cast:
                {
                    TypeRef type = Type();

                    return new CastExpr { Type = type, Operand = Need() };
                }

                case E.RefArg:
                {
                    Expr target = Need();
                    bool isOut = Bool();
                    TypeRef? declare = TypeOrNull();

                    return new RefArgExpr { Target = target, IsOut = isOut, Declare = declare, Name = StrOrNull() };
                }

                case E.Is:
                {
                    Expr operand = Need();
                    TypeRef type = Type();

                    return new IsExpr { Operand = operand, Type = type, Binding = StrOrNull() };
                }

                case E.As:
                {
                    Expr operand = Need();

                    return new AsExpr { Operand = operand, Type = Type() };
                }

                case E.Await:
                    return new AwaitExpr { Operand = Need() };

                case E.Lambda:
                {
                    bool async = Bool();
                    List<Param> ps = new();
                    int n = Count();

                    for (int i = 0; i < n; i++)
                    {
                        ps.Add(Param());
                    }

                    Expr? body = Expr();
                    LambdaExpr la = new() { Async = async, Body = body, BlockBody = Stmt() as Block };

                    la.Params.AddRange(ps);
                    return la;
                }

                case E.SwitchExpr:
                {
                    SwitchExpr se = new() { Subject = Need() };
                    int arms = Count();

                    for (int i = 0; i < arms; i++)
                    {
                        Expr? value = Expr();
                        TypeRef? type = TypeOrNull();
                        string? binding = StrOrNull();
                        Expr? when = Expr();
                        bool discard = Bool();

                        se.Arms.Add(new SwitchArm
                        {
                            Value = value, Type = type, Binding = binding,
                            When = when, Discard = discard, Result = Need(),
                        });
                    }
                    return se;
                }

                case E.Suppress:
                    return new SuppressExpr { Operand = Need() };

                case E.TypeOf:
                    return new TypeOfExpr { Type = Type() };

                case E.Throw:
                    return new ThrowExpr { Value = Need() };

                case E.Tuple:
                {
                    TupleExpr tu = new();
                    int items = Count();

                    for (int i = 0; i < items; i++)
                    {
                        tu.Items.Add(Need());
                    }

                    int names = Count();

                    for (int i = 0; i < names; i++)
                    {
                        tu.Names.Add(Str());
                    }
                    return tu;
                }

                case E.Range:
                {
                    Expr? from = Expr();

                    return new RangeExpr { From = from, To = Expr() };
                }

                case E.FromEnd:
                    return new FromEndExpr { Offset = Need() };

                case E.With:
                {
                    WithExpr wi = new() { Source = Need() };

                    ReadBody(wi.Body);
                    return wi;
                }

                case E.Pattern:
                {
                    Expr subject = Need();

                    return new PatternExpr { Subject = subject, Test = Need() };
                }

                case E.Subject:
                    return new SubjectExpr();

                default:
                    throw new AsmException(0, $"{_from}: unknown expression {(byte)kind} in a template");
            }
        }
    }
}
