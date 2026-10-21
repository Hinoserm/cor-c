using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Lower;

/// <summary>
/// WHAT THIS UNIT TELLS THE LINK OF ITS STATICS AND CONSTRUCTORS, for the
/// link to lay statics down made (Lto.Preinit): each candidate static's
/// initialiser (Binder.StaticInitialisers made it a step, FieldDecl.Preinit),
/// and each constructor that does nothing but chain and store, as recipes;
/// and each step's test of its flag, which the link sets for a field it laid
/// down. A recipe is only what lowering would make of the same expression --
/// the descriptor a `new` stamps, the constructor it calls, the offsets it
/// stores at -- so what the link lays down is what running it would have.
/// </summary>
public sealed partial class Lowering
{
    private readonly List<Preinit.Constructor> _preinitConstructors = new();
    private readonly HashSet<string> _preinitConstructorsSeen = new(StringComparer.Ordinal);
    private readonly List<Preinit.Candidate> _preinitCandidates = new();
    // The candidates this unit told the link of: their storage is the link's (the statics' loop lays none down).
    private readonly HashSet<FieldSymbol> _preinitNoted = new(ReferenceEqualityComparer.Instance);

    /// <summary>The flag the link sets for a candidate it laid down (Preinit.Candidate.Flag).</summary>
    private static string PreinitFlag(FieldSymbol f) => "preinit$done$" + StaticSymbol(f);

    /// <summary>
    /// A STATIC'S STEP RUNS ONLY WHERE THE LINK MADE NOTHING: its flag read
    /// first, and the step left at once when it is set.
    /// </summary>
    private void PreinitGuard(MethodSymbol m, string field)
    {
        // Not a candidate the link was told of (NotePreinitCandidate): it runs.
        if (m.Owner.Fields.FirstOrDefault(f => f.Static && f.Name == field) is not FieldSymbol f) return;
        VReg flag = _e.Load(IrType.I32, new SymOperand(PreinitFlag(f)), 0, 1, signed: false);
        Corsac.Lang.Ir.Block run = _f.NewBlock("preinit_run");
        _e.Branch(flag, _returnBlock, run);
        _e.SetBlock(run);
    }

    /// <summary>
    /// A STATIC'S STEP, LOWERED HERE: its field told to the link, with what
    /// it makes -- read now, while the step's body and the binder's answers
    /// about it are still held (Lowering.ReleaseBody lets them go after).
    /// Its place among its type's members is its place among the type's
    /// initialisers, which run in the order written.
    /// </summary>
    private bool NotePreinitCandidate(MethodSymbol step, MethodDecl decl, string field)
    {
        TypeSymbol t = step.Owner;
        if (t.Decl is not TypeDecl declared || declared.Elsewhere || declared.External) return false;
        if (t.Fields.FirstOrDefault(f => f.Static && f.Name == field) is not FieldSymbol f || FieldDeclOf(f) is not FieldDecl { Preinit: true } written) return false;
        // ONLY A FIELD THAT HOLDS A REFERENCE: the link makes objects. A
        // number's or a struct's step runs as an initialiser always did, its
        // storage its unit's.
        if (HoldsNumber(f.Type) || IsStructValue(f.Type) || f.Type.IsPointer || f.Type.IsNullableValue) return false;
        int order = declared.Members.IndexOf(written);
        Expr? value = decl.Body is { Statements: [ExprStmt { Expr: AssignExpr { Value: { } assigned } }] } ? assigned : null;
        Preinit.Recipe recipe = value is null ? new Preinit.Opaque() : RecipeOf(value, t, null, 0);
        _preinitCandidates.Add(new Preinit.Candidate(TypeKey(t), order, StaticSymbol(f), PreinitFlag(f), Binder.DefinitionName(step),
            _t.WordSize, written.Mods.HasFlag(Mods.Readonly), recipe));
        _preinitNoted.Add(f);
        return true;
    }

    /// <summary>The unit's notes, or null when it has nothing to say.</summary>
    private Preinit.Notes? PreinitNotes()
    {
        if (_preinitCandidates.Count == 0 && _preinitConstructors.Count == 0) return null;
        return new Preinit.Notes(_t.WordSize, _t.ArrayHeaderBytes, _t.ArrayCountOffset, _preinitConstructors.ToList(), _preinitCandidates.ToList());
    }

    /// <summary>A constructor lowered here, told to the link when it only chains and stores.</summary>
    private void NotePreinitConstructor(MethodSymbol m, MethodDecl decl)
    {
        if (m.Static || !m.IsCtor || m.Owner.Kind != TypeKind.Class || !_preinitConstructorsSeen.Add(CallLabel(m))) return;
        Preinit.Act[]? acts = ConstructorActs(m, decl);
        if (acts is not null) _preinitConstructors.Add(new Preinit.Constructor(CallLabel(m), m.Params.Count, acts));
    }

    private Preinit.Act[]? ConstructorActs(MethodSymbol m, MethodDecl decl)
    {
        if (!MakesAsData(m.Owner)) return null;
        List<Preinit.Act> acts = new();
        // AS LOWERING RUNS IT (EmitMethod): the chained constructor, then the body.
        if (decl.Init is { } init)
        {
            TypeSymbol? owner = init.IsThis ? m.Owner : m.Owner.Base;
            MethodSymbol? chained = _b.Chained.TryGetValue(decl, out MethodSymbol? chosen)
                ? chosen : owner?.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == init.Args.Count);
            if (chained is null || init.ArgumentOrder.Count != 0) return null;
            Preinit.Recipe[] args = init.Args.Select(a => RecipeOf(a, m.Owner, m, 0)).ToArray();
            acts.Add(new Preinit.Chain(CallLabel(chained), args));
        }
        else if (NearestConstructor(m.Owner.Base) is MethodSymbol inherited)
        {
            acts.Add(new Preinit.Chain(CallLabel(inherited), Array.Empty<Preinit.Recipe>()));
        }
        if (decl.Body is not Block body || !StoresOnly(body, m, acts)) return null;
        return acts.ToArray();
    }

    /// <summary>A block of nothing but stores into this object's fields (an empty return at the end allowed).</summary>
    private bool StoresOnly(Block block, MethodSymbol m, List<Preinit.Act> acts)
    {
        for (int i = 0; i < block.Statements.Count; i++)
        {
            switch (block.Statements[i])
            {
                case Block inner:
                    if (!StoresOnly(inner, m, acts)) return false;
                    break;
                case ReturnStmt { Value: null } when i == block.Statements.Count - 1:
                    break;
                case ExprStmt { Expr: AssignExpr { Op: null, Target: { } target, Value: { } value } }
                    when _b.Resolved.TryGetValue(target, out Sym? sym) && sym is FieldSym { Field: { Static: false, Inline: false } field }
                         && (target is NameExpr || target is MemberExpr { Target: ThisExpr })
                         && !IsStructValue(field.Type):
                    acts.Add(new Preinit.Store(field.Offset, HoldsNumber(field.Type) ? Math.Max(1, field.Type.Size) : _t.WordSize, RecipeOf(value, m.Owner, m, 0)));
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// WHETHER AN OBJECT OF THE CLASS MAY BE LAID DOWN: made by `new` with
    /// nothing else to it -- no type initialiser of its own or its bases'
    /// (making one runs it, at that moment, in C#), no finaliser, no struct
    /// held out of line (InitStructFields makes those), not abstract.
    /// </summary>
    private bool MakesAsData(TypeSymbol sym)
    {
        if (sym.Kind != TypeKind.Class || sym.Decl is TypeDecl { Mods: var mods } && mods.HasFlag(Mods.Abstract)) return false;
        for (TypeSymbol? t = sym; t is not null; t = t.Base)
        {
            if (t.Decl is TypeDecl d && d.Members.OfType<MethodDecl>().Any(x => x.Name is "StaticInit$" or "Finalize" || x.Name == "~" + d.Name)) return false;
            if (t.Fields.Any(f => !f.Static && !f.Boxed && !f.Inline && IsStructValue(f.Type))) return false;
        }
        return true;
    }

    /// <summary>
    /// WHAT AN EXPRESSION MAKES, as a recipe: a constant, a string, an array,
    /// an object made by a constructor the link may run, a constructor's
    /// argument (within one, `of` its symbol), or another candidate static.
    /// Anything else is opaque, and the static it is part of is the type
    /// initialiser's as before.
    /// </summary>
    private Preinit.Recipe RecipeOf(Expr e, TypeSymbol within, MethodSymbol? of, int depth)
    {
        if (depth > 32) return new Preinit.Opaque();
        switch (e)
        {
            case SuppressExpr suppressed:
                return RecipeOf(suppressed.Operand, within, of, depth + 1);
            case LiteralExpr { Kind: Lit.Null }:
                return new Preinit.Null();
            case LiteralExpr { Kind: Lit.Str } text:
                return new Preinit.Address(PreinitString(text.Text), 0);
            case NameExpr or MemberExpr when _b.Resolved.TryGetValue(e, out Sym? sym):
                switch (sym)
                {
                    case ParamSym p when of is not null && !p.ByRef && p.Index >= 0 && p.Index < of.Params.Count:
                        return new Preinit.Argument(p.Index);
                    case FieldSym { Field: { Static: true } field } when FieldDeclOf(field) is FieldDecl { Preinit: true } declared && declared.Mods.HasFlag(Mods.Readonly):
                        return new Preinit.StaticOf(StaticSymbol(field));
                }
                break;
            case CallExpr { Target: MemberExpr { Name: "ToArray", Target: NewExpr { Utf8Bytes: { } bytes } }, Args.Count: 0 }:
                return new Preinit.Elements(SequenceDescriptor(ElementKey(Type.U8), 1, isString: false, elementType: Type.U8), _t.DescriptorBytes, 1,
                    bytes.Select(b => (Preinit.Recipe)new Preinit.Bits(b)).ToArray());
            case CallExpr call when _b.Calls.TryGetValue(call, out MethodSymbol? helper) && call.Args.Count == 0
                                    && helper.Decl is MethodDecl { Name: var name, Body.Statements: [ReturnStmt { Value: { } returned }] } && name.StartsWith("FieldInit$", StringComparison.Ordinal):
                // A field initialiser kept in its own unit's helper (InitializerMethods): what it returns.
                return RecipeOf(returned, within, of, depth + 1);
            case NewExpr made:
                return MadeRecipe(made, within, of, depth);
        }
        if (Fold.TryConst(e, out long value, NamedConstant) && HoldsNumber(_b.TypeOf(e)) && !_b.TypeOf(e).IsFloat)
            return new Preinit.Bits(value);
        return new Preinit.Opaque();
    }

    private Preinit.Recipe MadeRecipe(NewExpr made, TypeSymbol within, MethodSymbol? of, int depth)
    {
        Type type = _b.TypeOf(made);
        if (made.Utf8Bytes is not null || made.CanonSlot >= 0 || made.NeedSlot >= 0
            || made.Body.Inits.Count != 0 || made.Body.Adds.Count != 0 || made.Body.Indexes.Count != 0) return new Preinit.Opaque();
        if (type.Element is Type element)
        {
            if (InlineElement(element)) return new Preinit.Opaque();
            int stride = ElementStride(element);
            string vtable = SequenceDescriptor(ElementKey(element), stride, isString: false, elementType: element);
            if (made.Elements is { } written)
                return new Preinit.Elements(vtable, _t.DescriptorBytes, stride, written.Select(w => RecipeOf(w, within, of, depth + 1)).ToArray());
            if (made.ArraySize is { } size && Fold.TryConst(size, out long length, NamedConstant) && length >= 0 && length <= int.MaxValue)
                return new Preinit.Zeroed(vtable, _t.DescriptorBytes, stride, (int)length);
            return new Preinit.Opaque();
        }
        if (type.Symbol is not TypeSymbol sym || !MakesAsData(sym)) return new Preinit.Opaque();
        MethodSymbol? ctor = _b.NewConstructors.TryGetValue(made, out MethodSymbol? selected)
            ? selected : sym.Methods.FirstOrDefault(c => c.IsCtor && c.Params.Count == made.Args.Count);
        if (ctor is null && made.Args.Count == 0) ctor = ImplicitConstructor(sym);
        if (ctor is null && made.Args.Count != 0 || made.ArgumentOrder.Count != 0) return new Preinit.Opaque();
        Preinit.Recipe[] args = made.Args.Select(a => RecipeOf(a, within, of, depth + 1)).ToArray();
        return new Preinit.Make(ctor is null ? "" : CallLabel(ctor), ClassDescriptor(sym), _t.DescriptorBytes,
            Math.Max(_t.ObjectHeaderBytes, sym.InstanceSize), args);
    }

    /// <summary>
    /// A string a recipe holds, as an object another object of the link can
    /// name: by its text, exported and coalesced, as a library unit's
    /// literals always are (SharedLiterals).
    /// </summary>
    private string PreinitString(string text)
    {
        if (SharedLiterals) return InternString(text);
        string key = "\0preinit\0" + text;
        if (_strings.TryGetValue(key, out string? sym)) return sym;
        byte[] block = StringBlock(text);
        sym = "str_" + System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(block.AsSpan(_t.ArrayHeaderBytes)))[..32];
        _strings[key] = sym;
        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, Exported = true, Coalescible = true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(0, StringDescriptor(), _t.DescriptorBytes));
        return sym;
    }
}
