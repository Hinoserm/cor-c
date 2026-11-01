using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;

public sealed partial class Lowering
{
    /// <summary>
    /// The class library compiled into one of the several images it ships as
    /// (--shared, not freestanding, its sources the compile's own:
    /// libcorsacrt.so and the libSystem.*.so above it), every image of it a
    /// part of ONE assembly to C#. What its
    /// `internal` means is the assembly's, so another image of it naming an
    /// internal member of this one -- System.Runtime.Numerics formatting
    /// through Core's NumberFormat -- is C#, and this image exports it
    /// (RecordPublicApi). A program is another assembly, and names none of
    /// them (Binder: CS0122). Not a kernel's: the class library is a part of
    /// the kernel's one image there, and its internals the kernel's own.
    /// </summary>
    public static bool SplitLibrary { get; set; }

    /// <summary>
    /// The class library's sources this compile owns (full paths): named on
    /// its command line, not read for their declarations. Empty for a
    /// program's, a user's library's or a module's compile.
    /// </summary>
    public static IReadOnlySet<string> OwnLibrarySources { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// WHAT THIS UNIT OFFERS OTHER IMAGES (PublicApi, `.corsac.public`): of
    /// each method and static it defines, whether C# lets another assembly
    /// name it -- shown (Binder.ShownOutside) in a type public all the way
    /// out -- and so whether an image linked of it, a kernel (--exports) or a
    /// shared library (--shared) alike, exports it and keeps it out of line,
    /// or withholds it: `internal` is its assembly's alone, `private` its
    /// type's. The class library's no differently from a program's: what a
    /// program's or a module's compiled code calls of the runtime and the
    /// library is public, or it is no image's to call. A public type's
    /// startup work (StaticInit$ and its ready word) is offered with it: an
    /// image touching the type's statics runs it; a packed method's copy that
    /// runs inside its caller's state ($inside) with the method. A static is
    /// offered where the link keeps it (OptionalSymbols): the link may lay one
    /// down or fold it. Every generic instantiation is left as it was.
    ///
    /// AND WHAT C# LETS ANOTHER IMAGE NAME THOUGH NO OTHER ASSEMBLY CAN, all
    /// optional (OptionalSymbols):
    ///   the compiler's own contract with the library (CompilerAbi): what a
    ///     program's or a module's compiled code calls of it, though its
    ///     source never named it;
    ///   the class library's assembly-wide members, where it is split over
    ///     several images (SplitLibrary).
    /// </summary>
    private void RecordPublicApi()
    {
        foreach (TypeSymbol t in _b.Types.Values.ToList())
        {
            if (t.MembersPending || t.Decl is not TypeDecl d || d.LocalOnly || d.Specialised || d.Canon is not null || d.TypeParams.Count > 0)
                continue;
            // Its public surface (open), and where more of it is another
            // image's to call: the compiler's contract (abi), or the split
            // class library's assembly (wide), each to what the rest of its
            // assembly sees.
            bool abi = CompilerAbi.Is(d);
            bool open = Offered(d) || abi;
            bool wide = abi || SplitLibrary && Binder.ClassLibrarySide(d) && WithinAssembly(d);
            // A PARTIAL TYPE'S PRIVATE MEMBERS ARE EVERY PART'S, and the split
            // class library writes parts of one type into two images:
            // Utf8Formatter's TryFormat in libSystem.Runtime.Extensions calls
            // the private Text of its part in libcorsacrt. Only where a part
            // is another image's: a member read from a source this compile
            // does not own. Runtime's parts are all libcorsacrt's.
            bool parts = wide && !abi && (d.Mods & Mods.Partial) != 0 && SpansImages(d);
            // ITS MEMBERS READ IN ITS OWN SCOPE (MemberWatch): every type of the
            // unit is walked, and an imported one -- the kernel's Arch, in
            // every unit -- lists nothing here: none of its methods is emitted
            // (Emits; a generic method's copy is passed by, and the unit that
            // made it depends on the type's file), and its statics are laid
            // down by another unit (Elsewhere). Read whole for the unit, 139
            // kernel units were compiled again for an Arch method none of them
            // called.
            using MemberWatch.Scope own = MemberWatch.Within(d.Watch);
            foreach (MethodSymbol m in t.Methods)
            {
                // Defined here: a part of a type whose first part is another
                // unit's is this unit's code (Elsewhere, OwnedImplementation).
                if (!ReferenceEquals(m.Owner, t) || !_required.Contains(m) || !Emits(m) || m.Decl is not MethodDecl method || method.LocalCopy)
                    continue;
                bool startup = method.Name == "StaticInit$";
                bool offered = open && (startup || Binder.ShownOutside(d, method));
                // AN EXPLICIT IMPLEMENTATION OF AN INTERFACE'S MEMBER is private
                // to C#'s names and reached through the interface from anywhere:
                // another image's descriptor of the type -- a program's box of a
                // library struct -- names it in its table.
                // The symbol's: an explicit property's or indexer's accessor
                // carries it there, never in its made declaration.
                bool dispatched = (open || wide) && m.ExplicitInterface is not null;
                List<string> into = offered ? _m.PublicSymbols
                    : dispatched || wide && (startup || parts || Binder.SeenInAssembly(d, method)) ? _m.OptionalSymbols
                    : _m.WithheldSymbols;
                into.Add(CallLabel(m));
                if (CopiesInside(m)) into.Add((TakesContext(m) ? BodyLabel(m) : Label(m)) + "$inside");
            }
            foreach (FieldSymbol f in t.Fields)
            {
                // Laid down here, as the statics' rounds do (Run): another
                // unit's type's is that unit's. A [ThreadStatic] is reached
                // through its number (ThreadStaticIndex), never its own symbol.
                if (!f.Static || !_statics.Contains(f) || d.Elsewhere) continue;
                MemberDecl? field = FieldDeclOf(f);
                bool offered = f.Name == BindResult.ReadyField ? open || wide
                    : field is not null && (open && Binder.ShownOutside(d, field) || wide && (parts || Binder.SeenInAssembly(d, field)));
                (offered ? _m.OptionalSymbols : _m.WithheldSymbols).Add(f.ThreadStatic ? ThreadStaticIndex(f) : StaticSymbol(f));
            }
            // ITS DESCRIPTOR, where this unit made it: a module making one of
            // its objects, testing for the type or deriving from it binds to
            // that, and the kernel need never make one itself (Unused types,
            // a class only a module instantiates). By the name the unit gave
            // it, never one made here for the asking: a box for a struct or
            // an enum (BoxDescriptor), an interface's own, a class's. AN
            // INTERFACE'S NEVER WITHHELD: a public class may implement an
            // internal one, and a module's class deriving from it names the
            // interface's descriptor in its own (ClassDescriptor's interfaces).
            string? descriptor = t.Kind switch
            {
                TypeKind.Interface => _interfaceDescriptors.GetValueOrDefault(t),
                TypeKind.Struct or TypeKind.Enum => _boxes.GetValueOrDefault(BoxKey(Type.Plain(t, t.Kind == TypeKind.Enum ? t.EnumUnderlying : Prim.Void))),
                _ => _descriptors.GetValueOrDefault(t),
            };
            if (descriptor is not null && !d.Elsewhere)
                (open || wide ? _m.OptionalSymbols : t.Kind == TypeKind.Interface ? null : _m.WithheldSymbols)?.Add(descriptor);
        }
    }

    /// <summary>
    /// Generic code: a copy of a generic method (MemberDecl.LocalCopy), a
    /// member of a generic type's copy (Specialised), or of a lambda's closure
    /// made in either (TypeDecl.InGenericCode). Every image compiles its own
    /// of it for its own type arguments.
    /// </summary>
    private static bool GenericCode(MethodSymbol m)
        => m.Decl is MemberDecl { LocalCopy: true } || m.Owner.Decl is { Specialised: true } or { InGenericCode: true };

    /// <summary>
    /// WHAT THIS UNIT'S GENERIC CODE NAMES (ReachedSymbols, `^`), in an
    /// image that exports: every symbol a body lowered from generic code
    /// calls or takes, and every one a copy's own data names (its
    /// descriptor's table). Another image's copy of the same code for its own
    /// type arguments -- List&lt;T&gt; for a program's struct -- calls what
    /// this one calls, the library's code as it is, which C# lets it name
    /// whatever its accessibility; the image linked of this unit exports
    /// what of it a unit withholds (PublicApi). Exact for each body this unit
    /// lowered: a canonical copy's, every instantiation and generic method's
    /// copy it made; and what its generic code names as written, of every
    /// template, a copy of it made here or not (Binder.GenericNames). A
    /// copy's own definitions are its own to every image.
    /// </summary>
    private void RecordReached()
    {
        HashSet<string> copies = new(StringComparer.Ordinal), named = new(StringComparer.Ordinal);
        foreach (Function f in _m.Functions)
        {
            if (!f.GenericCode && !f.Coalescible) continue;
            copies.Add(f.Name);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Callee is { } callee) named.Add(callee);
                    foreach (Operand o in i.Operands)
                        if (o is SymOperand symbol) named.Add(symbol.Name);
                }
        }
        foreach (DataItem d in _m.Data)
        {
            if (!d.Coalescible) continue;
            copies.Add(d.Name);
            foreach (DataReloc r in d.Relocs) named.Add(r.Symbol);
        }
        // AND WHAT THE UNIT'S GENERIC CODE NAMES AS WRITTEN (Binder.
        // GenericNames): of a template no copy here was made of too. A
        // method by the label its callers call it by (and its copy run inside
        // a packed caller); a static by its symbol, with its type's startup
        // (its ready word and StaticInit$), which a copy touching the static
        // runs; a type by its descriptor, or its box's.
        void Startup(TypeSymbol owner)
        {
            if (owner.FindField(BindResult.ReadyField) is { } ready) named.Add(StaticSymbol(ready));
            foreach (MethodSymbol init in owner.FindMethods("StaticInit$")) named.Add(CallLabel(init));
        }
        foreach (MethodSymbol m in _b.GenericNamedMethods)
        {
            named.Add(CallLabel(m));
            if (CopiesInside(m)) named.Add((TakesContext(m) ? BodyLabel(m) : Label(m)) + "$inside");
            if (m.Static) Startup(m.Owner);
        }
        foreach (FieldSymbol f in _b.GenericNamedFields)
        {
            named.Add(f.ThreadStatic ? ThreadStaticIndex(f) : StaticSymbol(f));
            Startup(f.Owner);
        }
        foreach (TypeSymbol t in _b.GenericNamedTypes)
        {
            if (t.Kind == TypeKind.Interface) continue;
            named.Add(t.Kind is TypeKind.Struct or TypeKind.Enum
                ? "b_" + Safe(BoxKey(Type.Plain(t, t.Kind == TypeKind.Enum ? t.EnumUnderlying : Prim.Void)))
                : "t_" + TypeKey(t));
        }
        named.ExceptWith(copies);
        named.ExceptWith(_m.PublicSymbols);
        named.ExceptWith(_m.OptionalSymbols);
        _m.ReachedSymbols.AddRange(named.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// A type another assembly can name: public, and every type it is
    /// declared in public too. One written in a namespace is at the top: its
    /// Outer is the namespace (Binder.HiddenFromProgram).
    /// </summary>
    private bool Offered(TypeDecl d)
    {
        for (TypeDecl? at = d; at is not null; at = _b.Types.TryGetValue(at.Outer!, out TypeSymbol? holder) ? holder.Decl : null)
        {
            if ((at.Mods & Mods.Public) == 0) return false;
            if (at.Outer is null || at.Outer == at.Namespace) return true;
        }
        return false;
    }

    /// <summary>
    /// A partial type with a part in another image of the split class
    /// library: a part written in a source this compile does not own, by
    /// the full paths the parts were merged from (TypeDecl.PartSources) --
    /// a member's File is its file's bare name, the same for threading.cor
    /// under every platform.
    /// </summary>
    private static bool SpansImages(TypeDecl d)
        => d.PartSources is { } parts && parts.Any(path => !OwnLibrarySources.Contains(path));

    /// <summary>
    /// A type the rest of its assembly can name: any at the top, and one
    /// nested in it only where it and every type it is declared in say more
    /// than private -- a nested type that says nothing is private, as C# has it.
    /// </summary>
    private bool WithinAssembly(TypeDecl d)
    {
        for (TypeDecl? at = d; at is not null; at = _b.Types.TryGetValue(at.Outer!, out TypeSymbol? holder) ? holder.Decl : null)
        {
            if (at.Outer is null || at.Outer == at.Namespace) return true;
            if ((at.Mods & (Mods.Public | Mods.Internal | Mods.Protected)) == 0) return false;
        }
        return false;
    }
}
