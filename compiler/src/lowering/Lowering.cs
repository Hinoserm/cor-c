#nullable enable
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using AstBlock = Corsac.Lang.Block;

/// <summary>
/// The bound syntax tree becomes IR here, and nothing below this knows what
/// a class, a string or a lambda is.
///
/// Everything the language means is decided in this one place: how an
/// object is laid out, how a virtual call finds its target, what a `foreach`
/// over an array turns into, how an exception unwinds. The old code
/// generator decided all of it too, but decided it in terms of one
/// machine's instructions; this decides it in terms of loads, stores, calls
/// and branches, and a backend for any machine takes it from there.
///
/// One instance per compilation. The entry point is <see cref="Lower"/>.
/// </summary>
public sealed partial class Lowering
{
    private readonly BindResult _b;
    private readonly Target _t = Target.Current;
    private readonly Module _m;
    private readonly string _file;
    private readonly bool _library;

    /// <summary>
    /// Whether there is no operating system under the program.
    ///
    /// It changes exactly one thing in what is lowered: the entry stub does
    /// not record the stack it was entered on. On Linux the loader leaves
    /// argc and argv there and Os.Args reads them back; on bare metal
    /// whatever jumped to the image left nothing there to read, and the
    /// store would be a write into a static nobody will ever look at -- but
    /// it would also drag in the symbol and imply a contract that does not
    /// hold. Everything else the stub does -- the static initialisers, the
    /// call to Main, the exit through Runtime.Exit -- is the same, because
    /// the platform file is what makes it different.
    /// </summary>
    public static bool Freestanding { get; set; }

    /// <summary>
    /// A freestanding program that keeps a GS of its own: an operating
    /// system's kernel on a machine with several processors, where "this
    /// thread's block" has to mean the one of the processor asking. The
    /// block is then found the way it is on Linux, at gs:[0], and the
    /// program is answerable for GS naming a descriptor whose base is a
    /// block before its first managed instruction runs.
    /// </summary>
    public static bool TlsGs { get; set; }

    /// <summary>
    /// Sys.Syscall traps to 0x83 instead of 0x80: the program is one of
    /// CORSAC's ring-1 kernels, whose Linux file calls go through a gate of
    /// their own (--ring1-syscalls). The numbers and registers are Linux's.
    /// </summary>
    public static bool Ring1Syscalls { get; set; }

    /// <summary>
    /// The runtime and the class library are shared objects this program
    /// links rather than source compiled into it.
    ///
    /// One thing changes in what is lowered, and it is in the entry stub:
    /// the collector and the stack-trace walker live in the library and
    /// find their own image by the linker's `__data_start`, `_end` and
    /// `__corsac_frames`. Those symbols describe ONE image each, and the
    /// program's are not the library's, so the program hands its own over
    /// at start -- Runtime.RegisterRoots for the statics the collector must
    /// scan, Runtime.RegisterFrames for the table a trace reads.
    /// </summary>
    public static bool Dynamic { get; set; }

    /// <summary>
    /// This compilation is a shared object. It gets one function nobody
    /// wrote -- <see cref="SharedInitName"/> -- which the loader calls
    /// through DT_INIT before anything uses the library, and which tells the
    /// runtime where this image's statics and frame table are.
    /// </summary>
    public static bool SharedObject { get; set; }

    /// <summary>What DT_INIT points at in a COR-C# shared object.</summary>
    public const string SharedInitName = Corsac.Lang.Elf.Linker.SharedInitName;

    /// <summary>
    /// Some of the sources given are here for their declarations only (see
    /// TypeDecl.Elsewhere), so this compilation is ONE OF SEVERAL that
    /// share them out.
    ///
    /// It changes what a library roots. A whole library publishes every
    /// method of every type; a part of one publishes every method of the
    /// types written in ITS sources, and reaches a generic instantiation
    /// only if its own code asks for one -- because an instantiation
    /// belongs to whoever wanted it, not to the file the template was
    /// written in, and rooting them all would put `List&lt;FileStream&gt;`
    /// in the collections library and make it depend on the file system's.
    /// </summary>
    public static bool PartOfALibrary { get; set; }

    /// <summary>
    /// The name of the entry stub, and whether it has to clear .bss itself.
    /// Both change when an assembled object owns `_start`: the stub becomes
    /// something that object calls, and the .bss clear belongs to whatever
    /// runs before the stack is in .bss -- which is that object.
    /// </summary>
    public static string EntryName { get; set; } = "_start";
    public static bool EntryClearsBss { get; set; } = true;
    public static string? StartupObject { get; set; }

    public List<CompileError> Errors { get; } = new();

    /// <summary>Method name to the address-bearing symbol, for `corc syms` and the entry table.</summary>
    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

    private Lowering(BindResult bound, string file, bool library)
    {
        _b = bound;
        _file = file;
        _library = library;
        _m = new Module(file) { PreserveExports = library || PartOfALibrary };
    }

    /// <summary>
    /// Lowers a whole bound compilation to a module. Only what the entry (or,
    /// for a library, every method) can reach is emitted: a method nothing
    /// calls costs nothing, and it also cannot fail to compile, which
    /// matters for library source that uses intrinsics this target lacks.
    /// </summary>
    public static Module Lower(BindResult bound, CompilationUnit unit, string file,
                               bool library, List<CompileError> errors,
                               Dictionary<string, string>? entries = null)
    {
        Lowering l = new(bound, file, library);
        l.Run(unit);
        // The frees the lifetime passes may add (Escape), by the label they
        // call: declared is enough, the body may be another unit's.
        foreach ((string helper, int arity) in new[] { ("Free", 1), ("FreeField", 2), ("FreeReplaced", 2), ("KeepField", 2), ("CardMarkObject", 1) })
            if (l.RuntimeMethod(helper, arity) is MethodSymbol provided) l._m.RuntimeHelpers.Add(Label(provided));
        errors.AddRange(l.Errors);
        if (entries is not null)
        {
            foreach ((string k, string v) in l.Entries)
            {
                entries[k] = v;
            }
        }
        return l._m;
    }

    private void Error(Node at, string message)
        => Errors.Add(new CompileError(_in, at.Line, at.Col, message));

    /// <summary>The file the method being lowered came from, for diagnostics.</summary>
    private string _in = "";

    // ---- names ---------------------------------------------------------------

    /// <summary>
    /// The symbol a method is compiled under. The owner's key and the full
    /// parameter signature are part of it, so overloads and same-named nested
    /// types never collide, and it is identical to what the old backend used
    /// so `corc syms` and every library header keep working.
    /// </summary>
    public static string Label(MethodSymbol m)
        => (m.Params.Count == 0
         ? $"m_{Owner(m)}_{m.Name}_0"
         : $"m_{Owner(m)}_{m.Name}_{m.Params.Count}_"
         + string.Join("_", m.Params.Select(p => (p.ByRef ? "R$" : "") + Mangle(p.Type))))
         // A CONVERSION OPERATOR IS ALSO WHAT IT MAKES: JsonNode's explicit
         // operators to bool, int, long and double all take one JsonNode.
         + (m.Name is "op_Implicit" or "op_Explicit" ? "_to_" + Mangle(m.Returns) : "");

    private static string Owner(MethodSymbol m) => m.Owner.Key.Replace('.', '$');

    /// <summary>An injective structural type encoding using '$' as the escape.</summary>
    public static string Mangle(Type t)
    {
        if (t.IsPointer)
            return "P$" + Mangle(t.Pointee!);
        if (t.IsArray)
            return $"A${t.ArrayRank}$" + Mangle(t.Element!);
        if (t.IsNullableValue)
            return "N$" + Mangle(t.Underlying);
        if (t.Symbol is null && t.ParamName is null)
            return "V$" + t.Prim;
        string key = t.Symbol?.Key ?? t.ParamName!;
        StringBuilder encoded = new(t.Symbol is null ? "G$" : "T$");
        foreach (char c in key)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                encoded.Append(c);
            else
                encoded.Append('$').Append(((int)c).ToString("x4"));
        }
        return encoded.ToString();
    }

    private static string TypeKey(TypeSymbol t)
    {
        StringBuilder sb = new();
        foreach (char c in t.Key)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                sb.Append(c);
            else
                sb.Append('$').Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    /// <summary>The symbol of a static field: its own ELF symbol, so a library's statics are the library's.</summary>
    private static string StaticSymbol(FieldSymbol f) => $"s_{TypeKey(f.Owner)}_{f.Name}";

    /// <summary>
    /// Whether a type was written in the class library rather than in the
    /// program. What a shared object may supply in this compilation's place:
    /// see Module.Provided, and tests/lang/502 for the case that makes the
    /// question worth asking.
    /// </summary>
    private static bool IsLibrary(TypeSymbol? t) => t?.Decl?.FromLibrary ?? false;

    /// <summary>
    /// What a method is CALLED in a stack trace: `Type.Method`, the way it was
    /// written, rather than the mangled label the linker knows it by. A
    /// constructor is spelled the way .NET spells one.
    /// </summary>
    /// <summary>
    /// A method as a .NET stack trace names it: `Program.Scale(Int32 by,
    /// String[] names)`, `Point..ctor(Double x)`, parameters by their
    /// runtime type's short name, `&amp;` after one passed by reference.
    /// </summary>
    private static string Display(MethodSymbol m)
        => (m.IsCtor ? $"{m.Owner.Name}..ctor" : $"{m.Owner.Name}.{m.Name}")
         + "(" + string.Join(", ", m.Params.Select(p => FrameTypeName(p.Type) + (p.ByRef ? "&" : "") + " " + p.Name)) + ")";

    /// <summary>A parameter's type as a .NET frame writes it: Int32, String, List`1, Int32[], Nullable`1.</summary>
    private static string FrameTypeName(Type t)
    {
        if (t.IsArray && t.Element is Type element) return FrameTypeName(element) + string.Concat(Enumerable.Repeat("[]", t.ArrayRank));
        if (t.IsNullableValue) return "Nullable`1";
        if (t.IsPointer) return FrameTypeName(t.Pointee ?? Type.Void) + "*";
        if (t.Symbol is TypeSymbol named)
        {
            string name = named.Decl is { Template: string template, TemplateArgs.Count: > 0 } made
                ? template + "`" + made.TemplateArgs.Count : named.Name;
            int dot = name.LastIndexOf('.');
            return dot < 0 ? name : name[(dot + 1)..];
        }
        if (t.ParamName is string generic) return generic;
        string full = RuntimeName(t);
        return full.StartsWith("System.", StringComparison.Ordinal) ? full["System.".Length..] : full;
    }

    // ---- the thread block ------------------------------------------------------
    //
    // Everything the runtime needs ONE OF PER THREAD lives in a block of words
    // this compiler and lib/rt/runtime.cor (class Tls) both know the shape of.
    // The handler chain head and the entry stack pointer used to be single
    // words of .bss, which is exactly one of each for the whole process: two
    // threads inside `try` interleaved one list, and the collector scanned one
    // thread's stack.
    //
    // On Linux the block is reached through GS -- its address is the base of a
    // GDT entry set with set_thread_area(2), and its first word is its own
    // address, glibc's convention -- so `mov eax, gs:[0]` answers it. With no
    // operating system there is one thread and no GDT worth the trouble, so
    // the address sits in a word of .bss instead and the entry stub fills it
    // in; a freestanding program needs no GS and no set-up call.
    //
    // The offsets are byte offsets into the block, scaled by the word size the
    // same way the handler record's are, so the layout is the same shape on a
    // 64-bit target. KEEP IN STEP WITH class Tls IN lib/rt/runtime.cor.

    /// <summary>The main thread's block: .bss, so it needs no allocator.</summary>
    public const string ThreadBlock0 = "__corsac_tls0";

    /// <summary>Where the block's address is when there is no GS to keep it in.</summary>
    public const string ThreadBlockSelf = "__corsac_tls_self";

    public const int TlsSelf = 0;
    public const int TlsHandler = 4;
    public const int TlsStackBase = 8;
    public const int TlsStackLimit = 12;
    public const int TlsAllocPtr = 16;
    public const int TlsAllocLimit = 20;
    public const int TlsThreadId = 24;
    public const int TlsState = 28;
    public const int TlsBytes = 140;

    /// <summary>The type the runtime library provides its hooks in.</summary>
    public const string RuntimeType = "Runtime";

    // ---- the module ------------------------------------------------------------

    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeSymbol, string> _descriptors = new();
    private readonly Dictionary<TypeSymbol, string> _interfaceDescriptors = new();
    private readonly Dictionary<string, string> _sequenceDescriptors = new(StringComparer.Ordinal);
    private readonly HashSet<FieldSymbol> _statics = new();
    private readonly HashSet<MethodSymbol> _required = new();
    private readonly Queue<MethodSymbol> _work = new();

    private void Run(CompilationUnit unit)
    {
        MethodSymbol? entry = null;

        foreach (TypeSymbol t in _b.Types.Values)
        {
            foreach (MethodSymbol m in t.Methods)
            {
                if (m.Name == "Main" && m.Static && Emits(m) && (StartupObject is null || t.Key == StartupObject))
                {
                    entry = m;
                }
            }
        }

        if (entry is not null && !_library)
        {
            EmitEntry(entry);
        }

        if (SharedObject)
        {
            EmitSharedInit();
        }

        // Roots: a library publishes everything; a program starts at Main.
        if (_library || PartOfALibrary || entry is null)
        {
            foreach (TypeSymbol t in _b.Types.Values)
            {
                // A PRELUDE TYPE THE LIBRARY ADDS TO. `Math` is the prelude's,
                // for its intrinsics, and Core.cor gives it Abs(long) and
                // Min(long, long) with bodies: those are the library's to
                // publish, or every other library calling them links against
                // a symbol nobody defines. Emits skips the intrinsics.
                if (t.Decl?.File == "<prelude>")
                {
                    // One part of a split library roots only its own file's.
                    foreach (MethodSymbol m in t.Methods)
                        if (_library || (PartOfALibrary && m.Decl?.OwnedImplementation == true)) Require(m);
                    continue;
                }
                if (t.Decl?.FromLibrary == true && !_library) continue;
                // Another shared object's; and, when this is one library of
                // several, an instantiation is reached rather than rooted.
                if ((t.Decl?.Elsewhere == true && !t.Methods.Any(method => method.Decl?.OwnedImplementation == true))
                    || (PartOfALibrary && t.Decl?.Specialised == true))
                {
                    continue;
                }
                foreach (MethodSymbol m in t.Methods.Where(m => _library || PartOfALibrary || m.Static))
                {
                    Require(m);
                }
                if ((_library || PartOfALibrary) && t.Decl?.Elsewhere != true)
                    foreach (FieldSymbol field in t.Fields.Where(field => field.Static)) _statics.Add(field);

                // A TYPE'S IDENTITY IS ITS DESCRIPTOR'S ADDRESS, and the
                // image that declares the type owns it -- whether or not
                // that image's own code ever makes one. Without this,
                // `throw new IOException(...)` in a library above found no
                // descriptor anywhere: the runtime declares IOException and
                // never throws one, and every library above declares it
                // `--ref` and may not emit a second.
                if ((_library || PartOfALibrary) && t.Decl is { Canon: null, Specialised: false } && t.Decl.TypeParams.Count == 0)
                {
                    if (t.Kind == TypeKind.Interface)
                    {
                        InterfaceDescriptor(t);
                    }
                    else if (t.Kind is TypeKind.Class or TypeKind.Struct)
                    {
                        ClassDescriptor(t);
                    }
                }
            }
        }

        while (_work.Count > 0)
        {
            SealFunctions();
            MethodSymbol m = _work.Dequeue();

            if (m.Decl is null
                && (m.Owner.Name.StartsWith("ArrayView$", StringComparison.Ordinal)
                    || m.Owner.Name.StartsWith("ArrayEnumerator$", StringComparison.Ordinal)))
            {
                EmitArrayViewMethod(m);
                continue;
            }

            if (m.Decl?.Body is null)
            {
                continue;       // abstract, external, or an intrinsic
            }

            EmitMethod(m, m.Decl);
        }

        SealFunctions();

        TypeSymbol? runtimeOwner = _b.Types.Values.FirstOrDefault(t => t.Name == RuntimeType);
        bool ownsRuntime = runtimeOwner is not null && runtimeOwner.Decl?.Elsewhere != true;
        if (ownsRuntime || (!_library && runtimeOwner is null))
        {
            // The main thread's block, and the word that finds it where there
            // is no GS. Ordinary library objects reference this storage;
            // only the entry/runtime-owning compilation defines it.
            // THE RUNTIME'S, not the program's: with a shared runtime there
            // is one thread block for the process and it is the library's,
            // and a program carrying a second one would have a handler chain
            // and an allocation buffer nothing else could see.
            _m.Data.Add(new DataItem(ThreadBlock0, new byte[TlsBytes / 4 * _t.WordSize]) { Zero = true, Align = _t.WordSize, FromLibrary = true });
            _m.Data.Add(new DataItem(ThreadBlockSelf, new byte[_t.WordSize]) { Zero = true, Align = _t.WordSize, FromLibrary = true });
        }

        foreach (FieldSymbol f in _statics)
        {
            // Another shared object's static, reached by its symbol.
            if (f.Owner.Decl?.Elsewhere == true)
            {
                continue;
            }
            int size = Math.Max(1, f.Type.Size);
            if (StaticArrayData(f) is string table)
            {
                // A `static readonly` table's field is never written again, so
                // its word is read-only data and every load of it folds to the
                // table's address (ReadOnlyFold). Unless the type has a static
                // constructor: C# lets that assign a readonly static even when
                // it has an initialiser, and the word must stay writable.
                bool fixedField = f.Owner.Decl is TypeDecl declaring
                    && declaring.Members.OfType<FieldDecl>().Any(d => d.Name == f.Name && d.Mods.HasFlag(Mods.Readonly))
                    && !declaring.Members.OfType<MethodDecl>().Any(m => (m.IsCtor && m.Mods.HasFlag(Mods.Static))
                                                                     || m.Name == "StaticConstructorBody$");
                DataItem holder = new(StaticSymbol(f), new byte[size])
                {
                    Align = AlignFor(size, _t.Align64), FromLibrary = IsLibrary(f.Owner), ReadOnly = fixedField,
                };
                holder.Relocs.Add(new DataReloc(0, table, 0));
                _m.Data.Add(holder);
                continue;
            }
            _m.Data.Add(new DataItem(StaticSymbol(f), new byte[size])
            {
                Zero = true, Align = AlignFor(size, _t.Align64), FromLibrary = IsLibrary(f.Owner),
                Coalescible = f.Owner.Decl?.Specialised == true,
            });
        }
    }

    /// <summary>
    /// A STATIC ARRAY OF CONSTANTS AS DATA (FieldDecl.StaticData): the array
    /// object laid down exactly as the heap would hold it -- the vtable of its
    /// sequence descriptor, the count, the elements -- in the writable data
    /// section, since a C# array may be written. Answers its symbol, or null
    /// when the field is not one. The collector never frees it: it is not in
    /// the heap, and a pointer outside the heap is no block (Gc.BlockForPointer);
    /// the data section is read as roots, so what a program stores into a
    /// string table later is kept.
    /// </summary>
    private string? StaticArrayData(FieldSymbol f)
    {
        if (f.Owner.Decl is not TypeDecl owner
            || owner.Members.OfType<FieldDecl>().FirstOrDefault(d => d.Name == f.Name && d.StaticData is not null) is not { StaticData: { } table }
            || f.Type.Element is not Type element)
        {
            return null;
        }
        int w = _t.WordSize;
        int stride = Math.Max(1, element.Size);
        int count = table.Count;
        int bytes = _t.ArrayHeaderBytes + stride * count;
        bytes = (bytes + w - 1) / w * w;
        byte[] block = new byte[Math.Max(bytes, _t.ArrayHeaderBytes + w)];
        WriteWord(block, _t.ArrayCountOffset, count);
        string sym = "sa_" + StaticSymbol(f);
        DataItem item = new(sym, block) { Align = _t.Align64, FromLibrary = IsLibrary(f.Owner), Exported = false };
        item.Relocs.Add(new DataReloc(0, SequenceDescriptor(ElementKey(element), stride, isString: false, elementType: element), _t.DescriptorBytes));
        for (int i = 0; i < count; i++)
        {
            int at = _t.ArrayHeaderBytes + i * stride;
            switch (table.Element)
            {
                case "string":
                    if (table.Strings[i] is string text)
                    {
                        item.Relocs.Add(new DataReloc(at, InternString(text), 0));
                    }
                    break;
                case "float":
                    BitConverter.TryWriteBytes(block.AsSpan(at, 4), (float)table.Reals[i]);
                    break;
                case "double":
                    BitConverter.TryWriteBytes(block.AsSpan(at, 8), table.Reals[i]);
                    break;
                default:
                    for (int b = 0; b < stride; b++)
                    {
                        block[at + b] = (byte)(table.Integers[i] >> (8 * b));
                    }
                    break;
            }
        }
        _m.Data.Add(item);
        return sym;
    }

    /// <summary>
    /// The largest power of two that divides <paramref name="size"/>, capped
    /// at <paramref name="max"/>. Section/symbol alignment must be a power of
    /// two (or one); <c>size</c> itself is not one for an aggregate whose
    /// members do not add up to a power of two (e.g. a 3-byte struct of three
    /// bytes), so it cannot be used as the alignment directly the way it can
    /// for a primitive.
    /// </summary>
    private static int AlignFor(int size, int max)
    {
        int a = 1;
        while (a * 2 <= size && a * 2 <= max)
        {
            a *= 2;
        }
        return a;
    }

    /// <summary>
    /// This thread's block. On Linux the self pointer at gs:[0], through a
    /// machine intrinsic so that no pass moves it across a change of GS; with
    /// no operating system, the one word of .bss that names the only block.
    /// </summary>
    private VReg ThreadBlockOf(Builder e)
    {
        if (Freestanding && !TlsGs)
        {
            return e.Load(IrTypes.Word, new SymOperand(ThreadBlockSelf));
        }
        return e.Call(MachineIntrinsics.ThreadBlock, IrTypes.Word)!;
    }

    /// <summary>This thread's block, in the builder the current method uses.</summary>
    private VReg ThreadBlockNow() => ThreadBlockOf(_e);

    private int _sealed;

    /// <summary>
    /// Every block ends in a terminator. The block a statement after a
    /// `throw` or a `return` would have gone into is dead but exists, and
    /// without the optimiser nothing removes it.
    /// </summary>
    private void SealFunctions()
    {
        for (; _sealed < _m.Functions.Count; _sealed++)
        {
            foreach (Block b in _m.Functions[_sealed].Blocks)
            {
                if (b.Terminator is null)
                {
                    b.Instrs.Add(new Instr { Op = Opcode.Unreachable });
                }
            }
        }
    }

    /// <summary>Whether a method lives in another image and is an import here.</summary>
    private static bool IsExternal(MethodSymbol m)
        => m.Owner.Decl?.External == true && !(m.Owner.Decl?.Specialised == true || m.Decl?.LocalCopy == true);

    private void Require(MethodSymbol call)
    {
        MethodSymbol m = Canonical(call);
        if (Emits(m) && _required.Add(m))
        {
            _work.Enqueue(m);
        }
    }

    /// <summary>
    /// Whether a method has a body of its own to lay down. A prelude
    /// declaration is an intrinsic, a template's method has no code until
    /// it is instantiated, and a word-shaped instantiation's method is the
    /// canonical copy's -- see Canonical.
    /// </summary>
    private static bool Emits(MethodSymbol m)
        // AN ARRAY VIEW'S METHODS HAVE NO DECLARATION, because the view has
        // none: it is written here, in EmitArrayViewMethod, and a vtable slot
        // pointing at it is a symbol something has to define.
        => m.Decl is null
        && (m.Owner.Name.StartsWith("ArrayView$", StringComparison.Ordinal)
            || m.Owner.Name.StartsWith("ArrayEnumerator$", StringComparison.Ordinal))
        || m.Decl?.Body != null && m.Decl.File != "<prelude>"
        && !IsExternal(m) && (m.Decl?.LocalCopy == true || (m.Decl?.OwnedImplementation ?? m.Owner.Decl?.Elsewhere != true)) && m.Owner.Decl?.Canon is null
        && m.Owner.Decl?.TypeParams.Count is null or 0
        && m.Decl?.TypeParams.Count is null or 0;

    /// <summary>
    /// The method actually compiled for a call. Every word-shaped
    /// instantiation of a template shares the canonical copy's code, so a
    /// call on List&lt;string&gt; lands in List$__canon's method, found by
    /// its position in the template.
    /// </summary>
    private MethodSymbol Canonical(MethodSymbol m)
    {
        if (m.Owner.Decl?.Canon is not string canon || m.Decl is null)
        {
            return m;
        }
        if (!_b.Types.TryGetValue(canon, out TypeSymbol? owner))
        {
            return m;
        }
        // A CONSTRUCTOR NOBODY WROTE has no place in the template: a type with
        // field initialisers and no constructor is given one (Binder's
        // Initialisers) on the copy itself when the copy was made before the
        // template had it. The canonical copy was given its own the same way,
        // and a copy's members are never emitted -- the call has to reach
        // that one, or it names a symbol nothing defines (Shelf<string>, made
        // in one round of specialisation, calling a constructor only the
        // canonical Shelf has).
        if (m.Decl.TemplateIndex < 0)
        {
            if (!m.IsCtor) return m;
            foreach (MethodSymbol c in owner.Methods)
                if (c.IsCtor && c.Static == m.Static && c.Decl is { TemplateIndex: < 0 } && c.Params.Count == m.Params.Count) return c;
            return m;
        }
        MethodSymbol? byName = null;

        foreach (MethodSymbol c in owner.Methods)
        {
            if (c.Decl?.TemplateIndex == m.Decl.TemplateIndex
                && (c.Name == m.Name || (c.IsCtor && m.IsCtor)))
            {
                return c;
            }

            if (c.Name == m.Name)
            {
                byName = c;
            }
        }

        // AND THE NAME ALONE IS ENOUGH FOR A SPECIALISED COPY.
        //
        // A copy's name already carries the template's position and its type
        // arguments, so two members of that name in one class are the same
        // copy. Its OWN position is not the template's -- a copy is added
        // wherever the class happened to end, and the canonical class ends
        // somewhere else than the instantiation does. Requiring the two
        // positions to agree left `List$Sym.OfType$LocalSym$29` calling a
        // symbol nothing defines, while the body sat in List$__canon under
        // exactly that name.
        //
        // The position is still what tells two ORDINARY overloads apart, which
        // is why it is tried first and this is the fallback.
        return byName ?? m;
    }

    /// <summary>The label a call to a method is aimed at: its canonical copy's.</summary>
    private string CallLabel(MethodSymbol m) => Label(Canonical(m));

    /// <summary>
    /// The array `static Main(string[] args)` is handed: every word on the
    /// entry stack after the program's own name, which is what C# gives Main
    /// and what .NET's Environment.GetCommandLineArgs gives less its first
    /// element. Built here, in the entry, because Main is an ordinary
    /// function with no source behind it, and because the library that knows
    /// how to read the stack -- Environment -- is not linked into a
    /// freestanding image, where a Main taking arguments simply gets none.
    /// </summary>
    private VReg EntryArgs(MethodSymbol entry)
    {
        Type element = entry.Params[0].Type.Element ?? Type.String;

        MethodSymbol? all = _b.Types.TryGetValue("Environment", out TypeSymbol? env)
            ? env.FindMethods("GetCommandLineArgs").FirstOrDefault(m => m.Static && m.Params.Count == 0)
            : null;

        if (all is null)
        {
            return AllocateArray(entry.Decl!, _e.Const(0, IrType.I32), element);
        }

        Require(all);
        VReg whole = _e.Call(CallLabel(all), IrTypes.Word)!;
        VReg count = _e.Load(IrType.I32, whole, _t.ArrayCountOffset);
        VReg rest = _f.NewReg(IrType.I32, "argn");
        _e.CopyTo(rest, R(_e.Binary(Opcode.Sub, count, 1)));

        // A program invoked with no name at all -- which nothing does, and
        // which a bad loader could -- would ask for an array of minus one.
        Block trim = _f.NewBlock("argtrim");
        Block build = _f.NewBlock("argbuild");
        _e.Branch(_e.Binary(Opcode.LtS, rest, 1), trim, build);
        _e.SetBlock(trim);
        _e.CopyTo(rest, Imm(0, IrType.I32));
        _e.Jump(build);
        _e.SetBlock(build);

        VReg made = AllocateArray(entry.Decl!, rest, element);
        VReg i = _f.NewReg(IrType.I32, "argi");
        _e.CopyTo(i, Imm(0, IrType.I32));

        Block test = _f.NewBlock("argtest");
        Block body = _f.NewBlock("argbody");
        Block done = _f.NewBlock("argdone");
        _e.Jump(test);
        _e.SetBlock(test);
        _e.Branch(_e.Binary(Opcode.LtS, R(i), R(rest), IrType.I32), body, done);
        _e.SetBlock(body);
        VReg from = _e.Binary(Opcode.Add, i, 1);
        VReg word = LoadElement(whole, from, element, entry.Decl!);
        StorePlace(ElementPlace(made, i, Type.ArrayOf(element), element, entry.Decl!), word);
        _e.CopyTo(i, R(_e.Binary(Opcode.Add, i, 1)));
        _e.Jump(test);
        _e.SetBlock(done);
        return made;
    }

    /// <summary>
    /// The program's entry: sets aside the initial stack pointer for the
    /// runtime, runs every static initialiser in declaration order, calls
    /// Main, and exits through the runtime with what Main returned. `_start`
    /// is what the ELF entry names and what ld-linux jumps to.
    /// </summary>
    private void EmitEntry(MethodSymbol entry)
    {
        // NO SOURCE FILE: the entry stub is the one function nobody wrote, so
        // a stack trace names it and stops rather than pointing at a line of
        // whichever file happened to be first on the command line.
        Function f = new(EntryName, IrType.Void);
        Block b0 = f.NewBlock("entry");
        Builder e = new(f, b0);

        // The stack as the loader left it -- argc at the top -- not as the
        // prologue left it. The frame pointer is the entry stack pointer less
        // one pushed word, whatever else the prologue saves after it; the
        // stack pointer here would already be below the callee-saved pushes.
        //
        // Freestanding, nobody left anything there: see Freestanding above.
        VReg? entrySp = null;
        VReg? loaderFini = null;

        if (!Freestanding)
        {
            // The loader's finaliser, in RDX/EDX at the first instruction
            // (the System V process entry): what C's startup registers so the
            // libraries' destructors run at exit. First, before anything can
            // use the register.
            loaderFini = e.Call(MachineIntrinsics.LoaderFini, IrTypes.Word)!;
            VReg fp = e.Reg(IrTypes.Word, "fp");
            e.Emit(Opcode.FramePointer, fp);
            entrySp = e.Binary(Opcode.Add, fp, Target.Current.WordSize);
        }
        else
        {
            // Nothing zeroed .bss: a flat image is whatever bytes the loader
            // copied and then whatever the memory after them held. A boot
            // sector has no room to clear it, so the image clears itself,
            // first thing, between the symbols the linker defines.
            //
            // Unless an assembly startup got there first: it has to clear .bss
            // before it puts the stack there, so by the time it calls this
            // stub the clear has happened and doing it again would wipe the
            // return address out from under the call.
            if (EntryClearsBss)
            {
                VReg bssStart = e.Address("__bss_start");
                VReg bssEnd = e.Address("_end");
                VReg bssBytes = e.Binary(Opcode.Sub, bssEnd, bssStart);
                e.Emit(Opcode.MemSet, null, new RegOperand(bssStart), new ImmOperand(0, IrType.I32), new RegOperand(bssBytes));
            }

            // With no operating system there is one thread, and its block is
            // the one in .bss: the word that finds it is filled in here, after
            // the clear that would otherwise undo it, so nothing on this
            // target needs a set-up call before a `try` or a `new`.
            VReg only = e.Address(ThreadBlock0);
            e.Store(new SymOperand(ThreadBlockSelf), new RegOperand(only));
            e.Store(only, only, TlsSelf);

            // AND WHERE ITS STACK BEGINS, which is where the collector's scan
            // of it ends. This was left out "because nobody left anything
            // there" -- true of argc and argv, and beside the point: with the
            // base at zero the scan ran from the stack pointer up to nothing,
            // so a freestanding program's stack was NEVER SCANNED and every
            // object held only by a local was the sweep's to take. A boot
            // loader lost a directory block it was in the middle of reading
            // the first time it had allocated enough to collect; a kernel ran
            // its whole start-up that way -- probing, mounting, loading the
            // first program -- until the first trap from a program set the
            // base for it, and survived or did not according to where its
            // start-up's few collections happened to fall. Whatever called
            // this stub, its frame is above this one and holds nothing of
            // ours: the frame pointer plus the pushed word is the top.
            VReg top = e.Reg(IrTypes.Word, "fp");
            e.Emit(Opcode.FramePointer, top);
            e.Store(only, e.Binary(Opcode.Add, top, Target.Current.WordSize), TlsStackBase / 4 * Target.Current.WordSize);
        }

        // Linux: the block goes behind a GDT entry and GS names it. This is
        // the first call the program makes, because every `try` and every
        // allocation after it reads the block.
        bool initializedThreadBlock = false;
        if (!Freestanding && RuntimeMethod("StartThreadBlock", 0) is MethodSymbol block)
        {
            Require(block);
            e.Call(CallLabel(block), IrType.Void);
            initializedThreadBlock = true;
        }

        // The stack as the loader left it is the top of this thread's stack,
        // which is where the collector's scan of it ends.
        if (entrySp is not null && initializedThreadBlock)
        {
            e.Store(ThreadBlockOf(e), entrySp, TlsStackBase / 4 * Target.Current.WordSize);
        }

        // The collector cannot scan a stack it does not know about; the main
        // thread's is registered the same way every other thread's is.
        if (RuntimeMethod("StartThread", 0) is MethodSymbol join)
        {
            Require(join);
            e.Call(CallLabel(join), IrType.Void);
        }

        // WITH A SHARED RUNTIME, THIS IMAGE INTRODUCES ITSELF. The library's
        // `__data_start`, `_end` and `__corsac_frames` are the library's own,
        // and nothing in it can discover a second image; the program's
        // statics would never be scanned and its frames would have no names.
        if (Dynamic || AnyNativeImports())
        {
            // The libraries first, if nobody else ran their initialisers --
            // which is the case under a loader that cannot call into the
            // program, and not the case under ld-linux. StartImages decides;
            // it is handed this program's own _DYNAMIC because the runtime's
            // copy of that symbol is the runtime's.
            if (RuntimeMethod("StartImages", 1) is MethodSymbol images)
            {
                Require(images);
                e.Call(CallLabel(images), IrType.Void,
                    new RegOperand(WordAddress(e, "_DYNAMIC")));
            }
            EmitBeginImage(e);
        }

        // NO STATIC INITIALISERS RUN HERE ANY MORE. They used to, in source
        // order, and in a freestanding image that is before there is a heap:
        // a `static byte[] table = new byte[24];` in any file the kernel
        // linked brought it down at entry. Each type's now runs the first time
        // anything touches it -- see Lowering.StaticInit -- which is both
        // C#'s rule and the only one a kernel can live with.

        Require(entry);

        // UNDER A C LIBRARY, C'S STARTUP RUNS FIRST. A dynamically linked C
        // program is entered through __libc_start_main, and so is this one
        // when a C library is loaded: it is what sets up the C library's
        // environment, program name and standard streams, and registers the
        // loader's finaliser so every library's destructors run at exit.
        // It calls back into __corsac_c_main, which runs Main exactly as
        // below. With no C library loaded StartC comes straight back, and the
        // program carries on here.
        if (!Freestanding && (Dynamic || AnyNativeImports()) && entrySp is not null && loaderFini is not null
            && RuntimeMethod("StartC", 3) is MethodSymbol startC)
        {
            Function cMain = new(CMainName, IrType.I32);
            cMain.Params.Add(cMain.NewReg(IrTypes.Word, "argc"));
            cMain.Params.Add(cMain.NewReg(IrTypes.Word, "argv"));
            cMain.Params.Add(cMain.NewReg(IrTypes.Word, "envp"));
            Builder ce = new(cMain, cMain.NewBlock("entry"));
            if (RuntimeMethod("ReturnFromC", 0) is MethodSymbol back)
            {
                Require(back);
                ce.Call(CallLabel(back), IrType.Void);
            }
            EmitRunMain(entry, cMain, ce);
            _m.Functions.Add(cMain);

            Require(startC);
            e.Call(CallLabel(startC), IrType.Void,
                new RegOperand(loaderFini), new RegOperand(entrySp), new RegOperand(e.Address(CMainName)));
        }

        EmitRunMain(entry, f, e);

        // The optimiser rewrites allocations after lowering -- an owned
        // object gains a Free, a program that needs no collector has its
        // Alloc retargeted to AllocBump -- and the worklist only lowers what
        // the program reaches. These are reached by the optimiser, so they
        // are rooted here; the inliner drops whichever end up unused.
        foreach (string helper in new[] { "AllocBump", "Free", "FreeBump" })
        {
            if (RuntimeMethod(helper, 1) is MethodSymbol rooted)
            {
                Require(rooted);
            }
        }
        // And the free of an owned object's field (Escape's owned fields),
        // and of an owned variable's previous value (owned variables).
        if (RuntimeMethod("FreeField", 2) is MethodSymbol fieldFree)
        {
            Require(fieldFree);
        }
        if (RuntimeMethod("FreeReplaced", 2) is MethodSymbol replacedFree)
        {
            Require(replacedFree);
        }

        _m.Functions.Add(f);
        _m.Entry = EntryName;
    }

    /// <summary>What C's startup calls as main when it runs first: see EmitEntry.</summary>
    private const string CMainName = "__corsac_c_main";

    /// <summary>
    /// The rest of the entry, from Main's own type on: into <paramref name="f"/>,
    /// which is the entry stub or the C library's main.
    /// </summary>
    private void EmitRunMain(MethodSymbol entry, Function f, Builder e)
    {
        // WHAT MAIN WAS DECLARED TO TAKE. `static int Main(string[] args)` is
        // one of the four shapes C# allows, and the stub called it with no
        // arguments at all: the program read whatever the register happened to
        // hold as an array. The words are on the entry stack, where the
        // library already knows how to find them; Main's own array is that one
        // WITHOUT the program's name, which is the difference between Main's
        // args and Environment.GetCommandLineArgs in .NET too.
        _f = f;
        _e = e;
        // A block made on demand for this function belongs to it alone: the
        // entry stub and the C library's main are both written from here,
        // and a bounds check in the second must not jump into the first.
        _boundsFail = null;

        // MAIN'S OWN TYPE IS TOUCHED BEFORE MAIN RUNS, which is what C# does
        // and what the three ordinary triggers cannot do: a static method of a
        // type reached from another method of that type is skipped, because
        // something must already have entered the type -- and Main is the one
        // entry nothing else made. Without this, os/bin/uniq's `static Stream
        // _out = Console.OpenStandardOutput();` was never run and the program
        // wrote through a null stream.
        TouchType(entry.Owner);

        Operand[] passed = entry.Params.Count == 1
            ? new Operand[] { new RegOperand(EntryArgs(entry)) }
            : Array.Empty<Operand>();

        Operand code;
        if (entry.Async)
        {
            // `async Task Main`: run the scheduler until the task is done,
            // then exit with what Task<int> holds, or zero.
            VReg task = e.Call(CallLabel(entry), IrTypes.Word, passed)!;
            _f = f;
            _e = e;
            if (AsyncRuntimeMethod(entry.Decl!, "RunMain", 1) is MethodSymbol runMain)
            {
                e.Call(CallLabel(runMain), IrType.Void, new RegOperand(task));
            }
            Type result = AsyncResultType(entry.Returns);
            if (!result.IsVoid && entry.Returns.Symbol?.FindMethods("get_Result").FirstOrDefault(g => g.Params.Count == 0) is MethodSymbol getResult)
            {
                VReg value = CallMethod(getResult, task, new List<Operand> { new RegOperand(task) })!;
                code = new RegOperand(Narrow(e, value, result, Type.I32));
            }
            else
            {
                code = new ImmOperand(0, IrType.I32);
            }
        }
        else if (entry.Returns.IsVoid)
        {
            e.Call(CallLabel(entry), IrType.Void, passed);
            code = new ImmOperand(0, IrType.I32);
        }
        else
        {
            VReg r = e.Call(CallLabel(entry), IrTypes.Of(entry.Returns), passed)!;
            code = new RegOperand(Narrow(e, r, entry.Returns, Type.I32));
        }

        MethodSymbol? exit = RuntimeMethod("Exit", 1);
        if (exit is not null)
        {
            Require(exit);
            e.Call(CallLabel(exit), IrType.Void, code);
        }
        else
        {
            e.Syscall(new ImmOperand(1, IrTypes.Word), new[] { code });
        }
        e.Unreachable();
    }

    /// <summary>
    /// WHAT THE LOADER CALLS WHEN IT BRINGS THIS LIBRARY IN. Every image
    /// carries its own statics and its own frame table, and the runtime can
    /// find neither from the other side of a shared-object boundary: the
    /// linker's `__data_start`, `_end` and `__corsac_frames` name one image
    /// each, and in the runtime they name the runtime's. So each library
    /// introduces itself, and the collector scans its statics and a stack
    /// trace can name its functions.
    ///
    /// DT_INIT and not an initialiser the first caller runs, because a
    /// library's statics have to be scannable before the first collection
    /// and not before the first call into that library -- the two are not
    /// the same moment, and the collection can be the earlier one.
    /// </summary>
    private void EmitSharedInit()
    {
        Function f = new(SharedInitName, IrType.Void);
        Block b0 = f.NewBlock("entry");
        Builder e = new(f, b0);
        _f = f;
        _e = e;

        // ONCE, however many times it is called. On Linux the dynamic linker
        // runs this before the program is entered; where the loader is a
        // kernel it cannot, and the program's startup walks the link map and
        // runs it instead. A word of .bss is cheaper than deciding which of
        // those happened.
        string done = SharedInitName + "_done";
        _m.Data.Add(new DataItem(done, new byte[_t.WordSize])
        {
            Zero = true, Align = _t.WordSize, Exported = false,
        });

        Block run = f.NewBlock("first");
        Block already = f.NewBlock("again");
        e.Branch(e.Load(IrTypes.Word, new SymOperand(done)), already, run);
        e.SetBlock(already);
        e.Ret();
        e.SetBlock(run);
        e.Store(new SymOperand(done), new ImmOperand(1, IrTypes.Word));

        EmitBeginImage(e);
        e.Ret();
        _m.Functions.Add(f);
    }

    /// <summary>
    /// `Runtime.BeginImage(__data_start, _end, __corsac_units)`: what every
    /// image says about itself, and the three symbols that mean something
    /// different in each one.
    /// </summary>
    private void EmitBeginImage(Builder e)
    {
        if (RuntimeMethod("BeginImage", 3) is not MethodSymbol begin)
        {
            return;
        }
        Require(begin);
        e.Call(CallLabel(begin), IrType.Void,
            new RegOperand(WordAddress(e, "__data_start")),
            new RegOperand(WordAddress(e, "_end")),
            new RegOperand(WordAddress(e, ManagedDirectory.Symbol)));
    }

    /// <summary>
    /// A symbol's address as the `long` the runtime's hooks take: widened
    /// from i386's 32-bit word, and already that in long mode, where
    /// widening would cut it to its low half.
    /// </summary>
    private static VReg WordAddress(Builder e, string symbol)
    {
        VReg address = e.Address(symbol);
        return address.Type == IrType.I32 ? e.Unary(Opcode.ZExt32, address) : address;
    }

    /// <summary>A hook the runtime library provides, found by name and arity.</summary>
    private MethodSymbol? RuntimeMethod(string name, int arity)
    {
        if (!_b.Types.TryGetValue(RuntimeType, out TypeSymbol? rt))
        {
            return null;
        }
        // By name and arity only, so the runtime is free to declare its
        // routines in plain COR-C#. Two overloads of one arity would be bound
        // to the first by luck; refuse that rather than guess.
        List<MethodSymbol> found = rt.Methods.Where(m => m.Name == name && m.Static && m.Params.Count == arity).ToList();
        if (found.Count > 1)
        {
            Errors.Add(new CompileError(_in, 0, 0,
                $"{RuntimeType}.{name} with {arity} parameter(s) is declared more than once; the compiler binds runtime routines by name and arity"));
        }
        return found.Count == 0 ? null : found[0];
    }

    private MethodSymbol? RequireRuntime(Node at, string name, int arity, string because)
    {
        MethodSymbol? m = RuntimeMethod(name, arity);
        if (m is null)
        {
            Error(at, $"{because} needs {RuntimeType}.{name} with {arity} parameter(s), which no compiled source provides; compile with the system library");
            return null;
        }
        Require(m);
        return m;
    }

    // ---- strings, descriptors and vtables ---------------------------------------

    /// <summary>
    /// A string literal as data: the array header and the UTF-16 code units,
    /// interned by content so two spellings of one text are one object.
    /// </summary>
    private string InternString(string text)
    {
        if (_strings.TryGetValue(text, out string? sym))
        {
            return sym;
        }

        byte[] block = new byte[_t.ArrayHeaderBytes + 2 * text.Length];
        WriteWord(block, _t.ArrayCountOffset, text.Length);
        // ITS HASH, WRITTEN IN NOW: a literal is read-only data, and the
        // runtime keeps a string's hash in these four bytes the first time it
        // is asked (Runtime.StringHashCode) -- which must find it already here.
        uint hash = 2166136261;
        foreach (char unit in text) hash = (hash ^ unit) * 16777619;
        if (hash == 0) hash = 1;
        for (int i = 0; i < 4; i++) block[_t.ArrayCountOffset + 4 + i] = (byte)(hash >> (8 * i));
        for (int i = 0; i < text.Length; i++)
        {
            block[_t.ArrayHeaderBytes + 2 * i] = (byte)text[i];
            block[_t.ArrayHeaderBytes + 2 * i + 1] = (byte)(text[i] >> 8);
        }

        // Named and registered BEFORE the descriptor is asked for, because the
        // descriptor's own name is a string and asking for it comes back here.
        sym = $"str_{_strings.Count}";
        _strings[text] = sym;
        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, Exported = false };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(0, StringDescriptor(), _t.DescriptorBytes));
        return sym;
    }

    private void WriteWord(byte[] into, int at, long value)
    {
        for (int i = 0; i < _t.WordSize; i++)
        {
            into[at + i] = (byte)(value >> (8 * i));
        }
    }

    private const int DescName = 0, DescSize = 1, DescDepth = 2, DescDisplay = 3,
                      DescInterfaces = 4, DescSelf = 5, DescFlags = 6, DescPayload = 7,
                      DescRefMap = 8, DescGcFlags = 9, DescElement = 10;

    /// <summary>
    /// The descriptor an array of these names as its element's (DescElement):
    /// a class's, an interface's, a string's, an inner array's; null for
    /// object, which every reference is, and for a value type.
    /// </summary>
    private string? ElementDescriptor(Type element)
    {
        if (element.IsArray && element.Element is Type inner)
        {
            return SequenceDescriptor(ElementKey(inner), Math.Max(1, inner.Size), isString: false, elementType: inner);
        }
        if (element.Prim == Prim.String)
        {
            return StringDescriptor();
        }
        if (element.IsPointer || element.IsNullableValue)
        {
            return null;
        }
        return element.Symbol switch
        {
            { Kind: TypeKind.Class } c => ClassDescriptor(c),
            { Kind: TypeKind.Interface } i => InterfaceDescriptor(i),
            _ => null,
        };
    }

    /// <summary>
    /// Bit 0 of the GC flags word: the elements of this sequence are
    /// references. Meaningful only on an array descriptor, where there is
    /// one map for every element rather than a map of the instance.
    /// </summary>
    private const int GcElementsAreReferences = 1;

    /// <summary>
    /// Whether a field of this type holds a pointer into the collected heap,
    /// so the collector must trace it and may move what it names.
    ///
    /// Nullable value types are cells on the heap and count. Structs are
    /// headerless heap blocks reached by pointer, so the word IS a heap
    /// pointer and must be traced; that the block carries no descriptor of
    /// its own is recorded in the doc as the next thing to fix.
    ///
    /// Prim.Any -- the canonical machine word a generic is compiled over --
    /// is deliberately NOT marked: it holds a reference in one instantiation
    /// and an integer in the next, and a moving collector that guessed would
    /// relocate an integer. Precision there waits for per-instantiation maps.
    /// </summary>
    private static bool HoldsReference(Corsac.Lang.Type ty)
        => ty.IsReference || ty.IsArray || ty.IsNullableValue
        || (!ty.IsPointer && ty.Symbol is { Kind: TypeKind.Struct });

    /// <summary>
    /// The reference map of one instance layout: a bitmap, one bit per word
    /// of the object from offset zero, so bit i covers the word at i *
    /// WordSize. The header words are covered and always clear.
    ///
    /// Emitted out of line and shared by nothing, because it is per type:
    ///
    ///   word 0    how many words the map covers
    ///   word 1..  the bits, least significant bit first, 32 per word
    ///
    /// A type with no reference fields gets no map at all and the descriptor
    /// slot stays null, which is the common case for a struct of numbers and
    /// saves the collector a load.
    /// </summary>
    private string? ReferenceMap(string key, IEnumerable<(int Offset, Corsac.Lang.Type Type)> fields, int instanceBytes)
    {
        int w = _t.WordSize;
        int words = (instanceBytes + w - 1) / w;
        uint[] bits = new uint[(words + 31) / 32];
        bool any = false;

        foreach ((int offset, Corsac.Lang.Type ty) in fields)
        {
            // EVERY FIELD A STORE TREATS AS A REFERENCE (MayHoldReference):
            // a class, an array, a string -- and an `object`, and a type
            // parameter's, which HoldsReference leaves out. Left off the map,
            // an object held only in `object gate = new()` was never marked,
            // and the next collection gave its memory to something else.
            bool traced = HoldsReference(ty)
                || ((ty.Prim is Prim.Any || ty.ParamName is not null) && !ty.IsPointer && LoadSize(ty) == w);
            if (!traced || offset % w != 0)
            {
                continue;
            }
            int bit = offset / w;
            if (bit >= words)
            {
                continue;
            }
            bits[bit / 32] |= 1u << (bit % 32);
            any = true;
        }

        if (!any)
        {
            return null;
        }

        byte[] block = new byte[(1 + bits.Length) * w];
        WriteWord(block, 0, words);
        for (int i = 0; i < bits.Length; i++)
        {
            WriteWord(block, (1 + i) * w, bits[i]);
        }
        _m.Data.Add(new DataItem("m_" + key, block) { ReadOnly = true, Exported = false, Align = w });
        return "m_" + key;
    }

    /// <summary>
    /// How an array's element type names its descriptor: WITHOUT a reference's
    /// `?`, at every depth. `string?[]` and `string[]` are one array at run
    /// time -- the checker converts between them with no copy -- so they
    /// must share a descriptor, or `o is string?[]` is false for the very
    /// array it names. A Nullable value's `?` stays: an `int?[]` holds cells,
    /// and is a different array.
    /// </summary>
    /// <summary>
    /// What an array's descriptor is keyed and named by: its element, spelt
    /// by the element type's KEY -- `Corsac.Lang.Ir.Operand`, not `Operand`.
    /// Spelt by simple name, two types called Operand in two namespaces
    /// shared one array descriptor, so `x is Operand[]` could not tell their
    /// arrays apart; once the descriptor named its element's, the two units
    /// that made it made it differently and the link refused the pair.
    /// </summary>
    private static string ElementKey(Type element) => Keyed(Unannotated(element));

    private static string Keyed(Type t)
    {
        if (t.IsArray && t.Element is Type inner)
        {
            return Keyed(inner) + string.Concat(Enumerable.Repeat("[]", t.ArrayRank)) + (t.Nullable ? "?" : "");
        }
        if (t.Symbol is not TypeSymbol named)
        {
            return t.ToString();
        }
        string s = named.Key;
        if (t.Args.Count > 0)
        {
            s += "<" + string.Join(", ", t.Args.Select(Keyed)) + ">";
        }
        s += new string('*', t.PointerDepth);
        return t.Nullable ? s + "?" : s;
    }

    /// <summary>
    /// An element key as .NET names the type: `System.Int32` for `int`, and
    /// `System.Int32[]` for `int[]`, which is what an array's GetType() says.
    /// A declared type's key is already its name.
    /// </summary>
    /// <summary>
    /// What .NET's Type.FullName answers for a declared type: its namespace
    /// and then its name, a nested type after its outer ones with `+` --
    /// `System.IO.IOException`, `Corsac.Lang.Parser+State`. The library's
    /// global types are System's, as they are to the checker
    /// (Binder.MoveToSystem): `System.NotSupportedException`. A
    /// specialisation, a tuple or a closure keeps the name it was made with.
    /// Type.Name is the part after the last `.` or `+` (String.TypeName).
    /// </summary>
    private static string FullTypeName(TypeSymbol t)
    {
        if (t.Decl is not TypeDecl d || d.Specialised || t.Structural)
        {
            return t.Name;
        }
        string space = d.Namespace.Length > 0 ? d.Namespace : d.FromLibrary ? "System" : "";
        // The outer types: the declaration's path less its namespace.
        string outer = d.Outer ?? "";
        if (space.Length > 0 && (outer == space || outer.StartsWith(space + ".", StringComparison.Ordinal)))
        {
            outer = outer.Length == space.Length ? "" : outer[(space.Length + 1)..];
        }
        else if (d.Namespace.Length > 0 && (outer == d.Namespace || outer.StartsWith(d.Namespace + ".", StringComparison.Ordinal)))
        {
            outer = outer.Length == d.Namespace.Length ? "" : outer[(d.Namespace.Length + 1)..];
        }
        string nested = outer.Length > 0 ? outer.Replace('.', '+') + "+" + t.Name : t.Name;
        return space.Length > 0 ? space + "." + nested : nested;
    }

    private static string DotNetName(string key)
    {
        if (key.EndsWith("[]", StringComparison.Ordinal)) return DotNetName(key[..^2]) + "[]";
        return key switch
        {
            "bool" => "System.Boolean", "byte" => "System.Byte", "sbyte" => "System.SByte",
            "short" => "System.Int16", "ushort" => "System.UInt16", "int" => "System.Int32",
            "uint" => "System.UInt32", "long" => "System.Int64", "ulong" => "System.UInt64",
            "nint" => "System.IntPtr", "nuint" => "System.UIntPtr", "float" => "System.Single",
            "double" => "System.Double", "char" => "System.Char", "string" => "System.String",
            "object" => "System.Object",
            _ => key,
        };
    }

    private static Type Unannotated(Type t)
    {
        if (t.IsNullableValue)
        {
            return t;
        }
        if (t.IsArray && t.Element is Type inner)
        {
            return Type.ArrayOf(Unannotated(inner));
        }
        return t.AsNonNullable();
    }

    /// <summary>
    /// The descriptor for arrays of one element type, and for strings. An
    /// array's vtable holds only object's own virtuals; what matters most is
    /// that every array of bytes shares one, so `GetType` and the flags agree.
    /// </summary>
    /// <summary>A string's descriptor: a sequence of two-byte UTF-16 code units.</summary>
    private string StringDescriptor() => SequenceDescriptor("char", 2, isString: true);

    private string SequenceDescriptor(string element, int stride, bool isString, bool? elementsAreReferences = null, Type? elementType = null)
    {
        string key = (isString ? "string" : element) + ":" + stride;
        bool elemRefs = elementsAreReferences ?? (!isString && ElementNameIsReference(element));
        if (_sequenceDescriptors.TryGetValue(key, out string? sym))
        {
            return sym;
        }

        sym = "q_" + (isString ? "string" : "array_" + Safe(element));
        int w = _t.WordSize;

        // AN ARRAY IS AN OBJECT, and answers object's virtuals: `o.ToString()`
        // on an array held as object, and the "Unable to cast" message a
        // failed cast builds from it, read these slots. With no vtable they
        // read whatever data followed the descriptor and jumped there. A
        // string's own methods are reached by name, never through here.
        int slots = isString ? 0 : Math.Max(Math.Max(_b.ToStringSlot, _b.CompareSlot), Math.Max(_b.EqualsSlot, _b.HashSlot)) + 1;
        byte[] d = new byte[_t.DescriptorBytes + slots * w];
        WriteWord(d, DescSize * w, stride);
        WriteWord(d, DescDepth * w, -1);
        WriteWord(d, DescFlags * w, isString ? 3 : 1);
        WriteWord(d, DescPayload * w, _t.ArrayHeaderBytes);
        WriteWord(d, DescGcFlags * w, elemRefs ? GcElementsAreReferences : 0);

        // Structural, and shared with the library when it has one: `x is
        // byte[]` compares descriptor addresses, so two copies of an array's
        // descriptor would be two types.
        DataItem item = new(sym, d) { ReadOnly = true, Align = _t.Align64, FromLibrary = true, Coalescible = true };
        _sequenceDescriptors[key] = sym;
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString(isString ? "System.String" : DotNetName(element) + "[]"), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));
        // ITS ELEMENT'S DESCRIPTOR, for covariance at run time: whether a
        // value may be stored in it (Runtime.ArrayStoreCheck) and whether it
        // is an array of a base type (Runtime.ArrayOf). None for an element
        // anything may be -- object -- and none for a value type, whose
        // arrays are never covariant.
        if (!isString && elementType is not null && ElementDescriptor(elementType.AsNonNullable()) is string elementDesc)
        {
            item.Relocs.Add(new DataReloc(DescElement * w, elementDesc, 0));
        }

        // AN EMPTY INTERFACE LIST, the terminator alone, so `is` and `as`
        // against an interface answer no for a sequence rather than read a
        // list that is not there. An array reaches its sequence interfaces
        // through a view made where it is converted (Binder.ArrayView); held
        // as object, it has no table to dispatch them through.
        DataItem faces = new("sf_" + sym, new byte[w]) { ReadOnly = true, Exported = false };
        _m.Data.Add(faces);
        item.Relocs.Add(new DataReloc(DescInterfaces * w, faces.Name, 0));
        if (slots > 0)
        {
            item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.ToStringSlot * w, ObjectToStringStub(), 0));
            item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.EqualsSlot * w, ObjectEqualsStub(), 0));
            item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.HashSlot * w, ObjectHashStub(), 0));
            item.Relocs.Add(new DataReloc(_t.DescriptorBytes + _b.CompareSlot * w, ObjectCompareStub(), 0));
        }
        return sym;
    }

    /// <summary>
    /// The descriptor of a primitive: int, bool, char and the rest.
    ///
    /// A primitive has no vtable and nothing to dispatch, but a descriptor
    /// is what a TYPE'S IDENTITY is here -- `typeof(int) == typeof(int)`
    /// compares two addresses -- so it needs one, and every unit must agree
    /// on it. Coalescible and marked as the library's for exactly that
    /// reason: two copies would be two types, and `typeof(int)` in a program
    /// would not equal `typeof(int)` in the library it links.
    ///
    /// The name is the one .NET answers, because that is what `Type.Name`
    /// means: `typeof(int).Name` is "Int32" and has been since 2002.
    /// </summary>
    private string PrimitiveDescriptor(Prim prim)
    {
        string named = PrimitiveName(prim);

        if (prim == Prim.String)
        {
            return StringDescriptor();
        }

        // `typeof(object)` is what `new object().GetType()` reads.
        if (prim == Prim.Any)
        {
            return ObjectDescriptor();
        }

        // A VALUE'S TYPE IS ITS BOX'S, which is what GetType() on one reads:
        // `((object)5).GetType() == typeof(int)` holds, as it does in .NET,
        // only if the two are one descriptor.
        Type primitive = new() { Prim = prim };
        if (Boxable(primitive))
        {
            return BoxDescriptor(primitive);
        }

        if (_primitiveDescriptors.TryGetValue(prim, out string? sym))
        {
            return sym;
        }

        sym = "v_" + Safe(named);
        _primitiveDescriptors[prim] = sym;

        byte[] d = new byte[_t.DescriptorBytes];
        int w = _t.WordSize;

        WriteWord(d, DescSize * w, PrimitiveBytes(prim));
        WriteWord(d, DescDepth * w, -1);

        DataItem item = new(sym, d) { ReadOnly = true, Align = _t.Align64, FromLibrary = true, Coalescible = true };

        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString("System." + named), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));
        return sym;
    }

    private readonly Dictionary<Prim, string> _primitiveDescriptors = new();

    private static string PrimitiveName(Prim prim) => prim switch
    {
        Prim.Bool => "Boolean",
        Prim.I8 => "SByte",
        Prim.I16 => "Int16",
        Prim.I32 => "Int32",
        Prim.I64 => "Int64",
        Prim.U8 => "Byte",
        Prim.U16 => "UInt16",
        Prim.U32 => "UInt32",
        Prim.U64 => "UInt64",
        Prim.NInt => "IntPtr",
        Prim.NUInt => "UIntPtr",
        Prim.F32 => "Single",
        Prim.F64 => "Double",
        Prim.Char => "Char",
        Prim.Void => "Void",
        Prim.Any => "Object",
        _ => "String",
    };

    private int PrimitiveBytes(Prim prim) => prim switch
    {
        Prim.Bool or Prim.I8 or Prim.U8 => 1,
        Prim.I16 or Prim.U16 or Prim.Char => 2,
        Prim.I32 or Prim.U32 or Prim.F32 => 4,
        Prim.I64 or Prim.U64 or Prim.F64 => 8,
        _ => _t.WordSize,
    };

    /// <summary>
    /// Whether an array of elements spelled this way holds references.
    ///
    /// INTERIM, and by name rather than by type: the one place that creates
    /// an array descriptor lives in Lowering.Expr and passes the element's
    /// printed name, not its Type. The names are the binder's own, so a
    /// class, a struct, a string, an array-of-array and a nullable cell are
    /// all recognisable; when that call site is taught to pass the Type this
    /// goes away and the explicit argument takes over.
    /// </summary>
    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "bool", "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong",
        "nint", "nuint", "float", "double", "char", "void",
    };

    private bool ElementNameIsReference(string element)
    {
        if (element.EndsWith("[]", StringComparison.Ordinal) || element.EndsWith("?", StringComparison.Ordinal))
        {
            return true;
        }
        if (element.EndsWith("*", StringComparison.Ordinal))
        {
            return false;       // a raw pointer is not the collector's
        }
        if (_b.Types.TryGetValue(element, out TypeSymbol? sym))
        {
            return sym.Kind is TypeKind.Class or TypeKind.Interface or TypeKind.Struct;
        }
        // Anything not a primitive and not a type this program declared --
        // a generic instantiation, whose printed name is not a dictionary
        // key -- is assumed to be a reference. Missing a root is the one
        // error a moving collector cannot survive; an extra one costs a
        // check that fails.
        return !Primitives.Contains(element);
    }

    private static string Safe(string s)
    {
        StringBuilder sb = new();
        foreach (char c in s)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>The symbol of a type's descriptor; the vtable follows it at DescriptorBytes.</summary>
    private string DescriptorOf(TypeSymbol t)
        => t.Kind == TypeKind.Interface ? InterfaceDescriptor(t) : ClassDescriptor(t);

    /// <summary>The address an object's first word holds: the vtable, just past the descriptor.</summary>
    private SymOperand VtableOf(TypeSymbol t) => new(ClassDescriptor(t), _t.DescriptorBytes);

    private string InterfaceDescriptor(TypeSymbol t)
    {
        if (_interfaceDescriptors.TryGetValue(t, out string? sym))
        {
            return sym;
        }

        sym = "i_" + TypeKey(t);
        _interfaceDescriptors[t] = sym;

        if (t.Decl?.Elsewhere == true)
        {
            // The shared object that holds the type holds its descriptor,
            // and the descriptor's ADDRESS is the type's identity: a second
            // copy here would be a second type.
            return sym;
        }

        byte[] d = new byte[_t.DescriptorBytes];
        int w = _t.WordSize;
        WriteWord(d, DescDepth * w, -1);

        DataItem item = new(sym, d) { ReadOnly = true, Align = _t.Align64, FromLibrary = IsLibrary(t), Coalescible = t.Decl?.Specialised == true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString(FullTypeName(t)), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));
        return sym;
    }

    /// <summary>
    /// The descriptor and vtable of a class, built once and on demand.
    ///
    /// Interface slots come first and are numbered program-wide by the
    /// binder, so a caller holding only the interface reaches the method by
    /// slot number and nothing else. Class virtuals follow. ToString and
    /// Equals have slots every class shares, filled with shared stubs when
    /// the class declares neither.
    /// </summary>
    private string ClassDescriptor(TypeSymbol t)
    {
        if (_descriptors.TryGetValue(t, out string? sym))
        {
            return sym;
        }

        sym = "t_" + TypeKey(t);
        _descriptors[t] = sym;

        if (t.Decl?.Elsewhere == true)
        {
            return sym;
        }

        int slots = t.Kind == TypeKind.Class ? Math.Max(Math.Max(_b.ToStringSlot, _b.CompareSlot), Math.Max(_b.EqualsSlot, _b.HashSlot)) + 1 : 0;
        for (TypeSymbol? s = t; s is not null; s = s.Base)
        {
            foreach (int interfaceSlot in s.InterfaceImplementations.Keys) slots = Math.Max(slots, interfaceSlot + 1);
            foreach (MethodSymbol m in s.Methods.Where(m => m.VtableSlot >= 0))
            {
                slots = Math.Max(slots, m.VtableSlot + 1);
            }
        }

        MethodSymbol?[] table = new MethodSymbol?[slots];
        List<TypeSymbol> chain = new();
        for (TypeSymbol? s = t; s is not null; s = s.Base)
        {
            chain.Insert(0, s);
        }
        foreach (TypeSymbol s in chain)
        {
            foreach (var implementation in s.InterfaceImplementations)
                table[implementation.Key] = implementation.Value.Abstract ? null : implementation.Value;
            foreach (MethodSymbol m in s.Methods.Where(m => m.VtableSlot >= 0))
            {
                table[m.VtableSlot] = m.Abstract ? null : m;
            }
        }

        int w = _t.WordSize;
        byte[] block = new byte[_t.DescriptorBytes + Math.Max(1, slots) * w];
        WriteWord(block, DescSize * w, Math.Max(t.InstanceSize, t.Kind == TypeKind.Class ? _t.ObjectHeaderBytes : 1));
        WriteWord(block, DescDepth * w, t.Depth);
        WriteWord(block, DescPayload * w, _t.ObjectHeaderBytes);

        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, FromLibrary = IsLibrary(t), Coalescible = t.Structural || t.Decl?.Specialised == true,
            Exported = t.Decl?.LocalOnly != true };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(DescName * w, InternString(FullTypeName(t)), 0));
        item.Relocs.Add(new DataReloc(DescSelf * w, sym, 0));

        // The reference map covers the whole instance, base fields included:
        // the collector holds an object, not a class chain, and must not walk
        // one to find the pointers in it.
        {
            List<(int, Corsac.Lang.Type)> fields = new();
            foreach (TypeSymbol s in chain)
            {
                foreach (FieldSymbol f in s.Fields)
                {
                    // A struct held in line holds no reference (FieldSymbol.Inline):
                    // nothing of its bytes is traced.
                    if (!f.Static && !f.Inline)
                    {
                        // A captured local's field holds the ADDRESS of the shared
                        // cell, a heap pointer whatever the captured type is;
                        // String stands in here for "one traced word".
                        fields.Add((f.Offset, f.Boxed ? Corsac.Lang.Type.String : f.Type));
                    }
                }
            }
            string? map = ReferenceMap(TypeKey(t), fields, Math.Max(t.InstanceSize, t.Kind == TypeKind.Class ? _t.ObjectHeaderBytes : 1));
            if (map is not null)
            {
                item.Relocs.Add(new DataReloc(DescRefMap * w, map, 0));
            }
        }

        // The display: every ancestor's descriptor, root first, self last.
        {
            byte[] display = new byte[chain.Count * w];
            DataItem disp = new("d_" + TypeKey(t), display) { ReadOnly = true, Exported = false };
            for (int i = 0; i < chain.Count; i++)
            {
                disp.Relocs.Add(new DataReloc(i * w, ClassDescriptor(chain[i]), 0));
            }
            _m.Data.Add(disp);
            item.Relocs.Add(new DataReloc(DescDisplay * w, disp.Name, 0));
        }

        // The interfaces: every one the class or an ancestor implements,
        // sorted by symbol name so lookups are deterministic, zero-terminated.
        {
            List<TypeSymbol> faces = new();
            foreach (TypeSymbol s in chain)
            {
                foreach (TypeSymbol i in s.Interfaces)
                {
                    AddInterfaceClosure(i, faces);
                }
            }
            // The doc promises this array sorted (ascending by descriptor
            // address at link time, which lowering cannot know); a symbol
            // is the closest stand-in available here, and it is what makes
            // the layout deterministic across a rebuild.
            // Named first, in closure order, and then sorted by the names: a
            // descriptor is MADE the first time it is named, so naming them
            // inside the comparison made them in whatever order the sort
            // happened to compare in.
            foreach (TypeSymbol face in faces) InterfaceDescriptor(face);
            faces.Sort((a, b) => string.CompareOrdinal(InterfaceDescriptor(a), InterfaceDescriptor(b)));
            byte[] arr = new byte[(faces.Count + 1) * w];
            DataItem ifc = new("f_" + TypeKey(t), arr) { ReadOnly = true, Exported = false };
            for (int i = 0; i < faces.Count; i++)
            {
                ifc.Relocs.Add(new DataReloc(i * w, InterfaceDescriptor(faces[i]), 0));
            }
            _m.Data.Add(ifc);
            item.Relocs.Add(new DataReloc(DescInterfaces * w, ifc.Name, 0));
        }

        for (int i = 0; i < slots; i++)
        {
            int at = _t.DescriptorBytes + i * w;
            string? target;

            if (table[i] is { } m)
            {
                Require(m);
                target = CallLabel(m);
            }
            else if (i == _b.EqualsSlot && t.Kind == TypeKind.Class)
            {
                target = IsTupleShape(t) ? TupleEquals(t) : EqualsGuard(t) ?? ObjectEqualsStub();
            }
            else if (i == _b.HashSlot && t.Kind == TypeKind.Class)
            {
                target = IsTupleShape(t) ? TupleHash(t) : ObjectHashStub();
            }
            else if (i == _b.CompareSlot && t.Kind == TypeKind.Class)
            {
                target = IsTupleShape(t) ? TupleCompare(t) : OwnCompare(t) ?? ObjectCompareStub();
            }
            else if (i == _b.ToStringSlot && t.Kind == TypeKind.Class)
            {
                target = IsTupleShape(t) ? TupleToString(t) : ObjectToStringStub();
            }
            else
            {
                target = null;      // abstract: calling it is a null call, which traps
            }

            if (target is not null)
            {
                item.Relocs.Add(new DataReloc(at, target, 0));
            }
        }

        return sym;
    }

    private static void AddInterfaceClosure(TypeSymbol face, List<TypeSymbol> into)
    {
        if (into.Contains(face))
        {
            return;
        }
        into.Add(face);
        foreach (TypeSymbol parent in face.Interfaces)
        {
            AddInterfaceClosure(parent, into);
        }
    }

    private bool _hasObjectEquals, _hasObjectToString;

    /// <summary>object.Equals: the same object or not. Shared by every class that declares none.</summary>
    private string ObjectEqualsStub()
    {
        const string name = "__object_equals";
        if (!_hasObjectEquals)
        {
            _hasObjectEquals = true;
            Function f = new(name, IrType.I32) { Coalescible = true };
            VReg a = f.NewReg(IrTypes.Word, "this");
            VReg b = f.NewReg(IrTypes.Word, "other");
            f.Params.Add(a);
            f.Params.Add(b);
            Builder e = new(f, f.NewBlock("entry"));
            e.Ret(new RegOperand(e.Binary(Opcode.Eq, a, b)));
            _m.Functions.Add(f);
        }
        return name;
    }

    /// <summary>object.ToString: the type's name, read out of the object's own descriptor.</summary>
    private string ObjectToStringStub()
    {
        const string name = "__object_tostring";
        if (!_hasObjectToString)
        {
            _hasObjectToString = true;
            Function f = new(name, IrTypes.Word) { Coalescible = true };
            VReg self = f.NewReg(IrTypes.Word, "this");
            f.Params.Add(self);
            Builder e = new(f, f.NewBlock("entry"));
            VReg vt = e.Load(IrTypes.Word, self, 0);
            VReg nm = e.Load(IrTypes.Word, vt, -_t.DescriptorBytes + DescName * _t.WordSize);
            e.Ret(new RegOperand(nm));
            _m.Functions.Add(f);
        }
        return name;
    }

    /// <summary>
    /// The generated bodies of an array-interface adapter: an object holding
    /// an array and answering the interface's Count and indexer from it.
    /// </summary>
    /// <summary>A node to hang a diagnostic on, for code nobody wrote.</summary>
    private static MethodDecl At(MethodSymbol m)
        => m.Decl ?? new MethodDecl { Name = m.Name, Line = 0, Col = 0 };

    private void EmitArrayViewMethod(MethodSymbol m)
    {
        TypeSymbol view = m.Owner;
        Type of = view.Fields[0].Type.Element ?? Type.I32;

        Function f = new(Label(m), IrTypes.Of(m.Returns)) { Coalescible = true };
        VReg self = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(self);
        foreach (ParamSymbol p in m.Params)
        {
            f.Params.Add(f.NewReg(IrTypes.Of(p.Type), p.Name));
        }
        Builder e = new(f, f.NewBlock("entry"));
        VReg items = e.Load(IrTypes.Word, self, view.Fields[0].Offset);

        Function savedFn = _f; Builder savedB = _e;
        Block? savedFail = _boundsFail;

        // A FRESH FUNCTION HAS NO FAILURE BLOCK YET. The one a bounds check
        // jumps to is cached per function, and reusing the last function's put
        // a branch to a block this one does not contain -- which the optimiser
        // meets as a successor it cannot find.
        _f = f; _e = e; _boundsFail = null;

        // ---- the enumerator's own two members ------------------------------
        //
        // `at` starts at -1 and MoveNext steps before it answers, which is
        // .NET's contract: Current is not readable until MoveNext has said
        // true once.
        if (m.Owner.Name.StartsWith("ArrayEnumerator$", StringComparison.Ordinal))
        {
            FieldSymbol cursor = view.Fields[1];

            if (m.Name == "Dispose")
            {
                e.Ret(null);
            }
            else if (m.Name == "Reset")
            {
                e.Store(new RegOperand(self), Imm(-1, IrType.I32), cursor.Offset, 4);
                e.Ret(null);
            }
            else if (m.ExplicitMember == "get_Current")
            {
                // IEnumerator's Current, an object: the element boxed.
                VReg value = LoadElement(items, e.Load(IrType.I32, self, cursor.Offset), of, At(m));
                e.Ret(new RegOperand(Boxable(of) ? BoxValue(At(m), value, of) : value));
            }
            else if (m.Name == "MoveNext")
            {
                VReg next = e.Binary(Opcode.Add, e.Load(IrType.I32, self, cursor.Offset), 1);

                e.Store(new RegOperand(self), new RegOperand(next), cursor.Offset, 4);
                e.Ret(new RegOperand(e.Binary(Opcode.LtS, next,
                                              e.Load(IrType.I32, items, _t.ArrayCountOffset))));
            }
            else
            {
                VReg value = LoadElement(items, e.Load(IrType.I32, self, cursor.Offset), of,
                                         At(m));

                e.Ret(new RegOperand(value));
            }

            _f = savedFn; _e = savedB; _boundsFail = savedFail;
            _m.Functions.Add(f);
            return;
        }

        // ---- the view's ----------------------------------------------------
        //
        // WALKING ONE IS INDEXING IT, through an enumerator written the same
        // way this view is. An array borrows nothing from a list: the standard
        // library's ListEnumerator belongs to a List<T> this program may never
        // have made.
        if ((m.ExplicitMember ?? m.Name) == "GetEnumerator" && m.Params.Count == 0)
        {
            // The non-generic GetEnumerator hands back the same walker, which
            // is an IEnumerator as well as the IEnumerator<T> it was made for.
            MethodSymbol typed = view.Methods.First(x => x.Name == "GetEnumerator" && x.Params.Count == 0);
            TypeSymbol walker = _b.Types[$"ArrayEnumerator${typed.Returns.Symbol!.Name}"];
            VReg made = Allocate(At(m), Math.Max(_t.ObjectHeaderBytes, walker.InstanceSize), described: true);

            e.Store(new RegOperand(made), VtableOf(walker), 0, _t.WordSize);
            e.Store(new RegOperand(made), new RegOperand(items), walker.Fields[0].Offset, _t.WordSize);
            e.Store(new RegOperand(made), Imm(-1, IrType.I32), walker.Fields[1].Offset, 4);
            _f = savedFn; _e = savedB; _boundsFail = savedFail;
            e.Ret(new RegOperand(made));
            _m.Functions.Add(f);
            return;
        }

        _f = savedFn; _e = savedB;

        if (m.Name == "get_Count" || m.Params.Count == 0)
        {
            e.Ret(new RegOperand(e.Load(IrType.I32, items, _t.ArrayCountOffset)));
        }
        else
        {
            VReg index = f.Params[1];
            Function saved = _f; Builder savedE = _e;
            _f = f; _e = e; _boundsFail = null;
            VReg value = LoadElement(items, index, of, At(m));
            _f = saved; _e = savedE;
            e.Ret(new RegOperand(value));
        }
        _boundsFail = savedFail;
        _m.Functions.Add(f);
    }
}
