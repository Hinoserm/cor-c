#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Elf;

/// <summary>
/// A KERNEL MODULE: a shared object that carries no runtime of its own and is
/// linked against the kernel's exports (KernelExports), as a program is
/// linked against libSystem.so (docs/software/DRIVERS.md in the OS
/// repository, "Modules").
///
///   0x0000  ELF header, program headers                \
///           .hash, .dynsym, .dynstr                    |  PT_LOAD R X
///           .rel.dyn                                   |
///           .text, .rodata                             /
///   next page
///           .data.rel.ro (descriptors, string objects)  \
///           .data.rel.ro.imports                        |  PT_LOAD RW
///           .data, .got, .dynamic, .bss                 /
///           .corsac.stamp     the kernel's build stamp
///           .corsac.modinfo   name, ring, aliases, dependencies
///
/// It is a shared object as any library is, so the kernel's binder
/// (elfload.cor) binds it with what it binds a program's libraries with, and
/// it asks nothing of that binder that a library does not:
///
///   - REL relocations only, of the kinds a library has -- R_386_RELATIVE for
///     its own addresses, R_386_32 for a kernel address held in its data,
///     R_386_GLOB_DAT for a GOT slot -- and never R_386_COPY, R_386_PC32,
///     a TLS relocation, or one in its text: a link that would need any of
///     them fails here rather than at insmod;
///   - SysV DT_HASH, and one DT_INIT: __corsac_init, which hands the module's
///     statics (__data_start to _end) to the collector as roots and its
///     frame and stack-map directory (__corsac_units) to the stack walker,
///     through the kernel's own Runtime.BeginImage -- or, when the module has
///     [ModuleInitializer] methods, a function this link writes that calls
///     __corsac_init and then each of them, so a driver registers itself
///     with its statics already roots;
///   - DT_NEEDED for another module it imports from, never for the kernel,
///     which is always there.
///
/// Every name it imports must be one the kernel or a module it depends on
/// exports. A name neither has would be "undefined symbol" at insmod, on the
/// machine; here it is the build failing.
/// </summary>
public static partial class Linker
{
    /// <summary>The function a shared object's link writes when it has initialisers: __corsac_init, then each of them (Initializers).</summary>
    public const string ModuleInitName = "__corsac_module_init";

    /// <summary>
    /// THE MODULE WAS BUILT FOR THIS KERNEL'S MACHINE: the same pointer size,
    /// calling convention and thread-block model as the kernel's units (a
    /// module compiled without --tls-gs reads its thread block from a word the
    /// kernel never sets), and no instruction the kernel's processor lacks.
    /// Asked before the contracts are taken out of the objects.
    /// </summary>
    public static void AgreeWithKernel(IEnumerable<(string Name, ObjectFile Object)> inputs, KernelExports kernel)
    {
        List<string> errors = new();
        foreach ((string name, ObjectFile obj) in inputs)
        {
            Section? contract = obj.Sections.FirstOrDefault(section => section.Name == TargetContract.SectionName);
            if (contract is null || kernel.Abi is null) continue;
            byte[] mine = contract.Content(), theirs = kernel.Abi;
            if (mine.Length != 28 || theirs.Length != 28) throw new ElfFormatException(name + ": unsupported native ABI contract");
            if (!mine.AsSpan(8, 12).SequenceEqual(theirs.AsSpan(8, 12)))
                errors.Add(name + ": not built for the kernel's machine (" + kernel.Name + ")");
            uint tls = BinaryPrimitives.ReadUInt32LittleEndian(mine.AsSpan(20)), kernelTls = BinaryPrimitives.ReadUInt32LittleEndian(theirs.AsSpan(20));
            if (tls != TargetContract.NoTlsClaim && kernelTls != TargetContract.NoTlsClaim && tls != kernelTls)
                errors.Add(name + ": finds its thread block other than " + kernel.Name + " does (compile it as the kernel was: --tls-gs or not)");
        }
        if (errors.Count != 0) throw new LinkException(errors);
        if (kernel.Cpu is not null)
        {
            ObjectFile carrier = new();
            Section cpu = new(X86CodeGenerationContract.SectionName, SectionKind.Note);
            cpu.Bytes.AddRange(kernel.Cpu);
            carrier.Sections.Add(cpu);
            if (X86CodeGenerationContract.Read(carrier) is { } target) X86CodeGenerationContract.ValidateTarget(inputs, target);
        }
    }

    /// <summary>
    /// Link <paramref name="objects"/> as a module of <paramref name="kernel"/>
    /// called <paramref name="fileName"/>, importing from the kernel and from
    /// <paramref name="dependencies"/> (other modules' files).
    /// </summary>
    public static byte[] LinkModule(IEnumerable<(string Name, ObjectFile Object)> objects, string fileName, KernelExports kernel, IReadOnlyList<string>? dependencies = null)
    {
        List<(string Name, ObjectFile Object)> inputs = objects.ToList();
        AgreeWithKernel(inputs, kernel);
        // EACH UNIT WAS COMPILED AGAINST THIS KERNEL: the compile stamped it
        // with the index it read (Driver.Compile), and an object built for
        // another kernel is refused here rather than stamped as this one's.
        List<string> others = new();
        foreach ((string what, ObjectFile obj) in inputs)
        {
            foreach (Section section in obj.Sections.Where(section => section.Name == KernelExports.StampSection))
                if (!section.Content().AsSpan().SequenceEqual(kernel.Stamp))
                    others.Add(what + ": compiled against the declarations of another kernel (build stamp "
                        + KernelExports.Text(section.Content()) + ", " + kernel.Name + "'s " + KernelExports.Text(kernel.Stamp) + ")");
            obj.Sections.RemoveAll(section => section.Name == KernelExports.StampSection);
        }
        if (others.Count != 0) throw new LinkException(others);
        List<string> said = ModuleInfo.Take(inputs, ModuleInfo.SectionName);
        // Consumed: what remains of them is the module's stamp and modinfo.
        foreach (var input in inputs)
            input.Object.Sections.RemoveAll(section => section.Name is TargetContract.SectionName or X86CodeGenerationContract.SectionName
                or ManagedLayoutContract.SectionName or UsesNotes.SectionName);

        // WHAT IT NEEDS OF ITS DEPENDENCIES: the ones that define a name it
        // does not, as a program's DT_NEEDED is worked out (Driver.Needed).
        HashSet<string> defined = new(inputs.SelectMany(input => input.Object.Symbols).Where(s => s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
        HashSet<string> undefined = new(inputs.SelectMany(input => input.Object.Symbols).Where(s => !s.IsDefined && !defined.Contains(s.Name)).Select(s => s.Name), StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> theirs = new(StringComparer.Ordinal);
        foreach (string dependency in dependencies ?? Array.Empty<string>())
        {
            byte[] bytes = File.ReadAllBytes(dependency);
            if (KernelExports.StampOf(bytes, dependency) is not { } stamp || !stamp.AsSpan().SequenceEqual(kernel.Stamp))
                throw new LinkException(new[] { dependency + ": not a module of this kernel (its build stamp is not " + KernelExports.Text(kernel.Stamp) + ")" });
            theirs[dependency] = new HashSet<string>(ElfReader.ExportsOf(bytes), StringComparer.Ordinal);
        }
        List<string> needed = theirs.Where(pair => pair.Value.Overlaps(undefined)).Select(pair => pair.Key).ToList();

        // THE MODULE'S OWN NAME AND RING FIRST, then what its source said,
        // then a dependency the link found that the source did not name.
        string name = ModuleInfo.NameOf(fileName);
        string[] rings = said.Where(entry => entry.StartsWith("ring=", StringComparison.Ordinal)).ToArray();
        if (rings.Length > 1)
            throw new LinkException(new[] { "one image of a module is for one ring, and its units were compiled for " + string.Join(", ", rings) });
        List<string> entries = new() { "name=" + name };
        entries.AddRange(rings);
        entries.AddRange(said.Where(entry => !entry.StartsWith("ring=", StringComparison.Ordinal) && !entry.StartsWith("name=", StringComparison.Ordinal)));
        foreach (string dependency in needed)
        {
            string depends = "depends=" + ModuleInfo.NameOf(SoName(dependency));
            if (!entries.Contains(depends)) entries.Add(depends);
        }
        ObjectFile own = KernelExports.StampObject(kernel.Stamp, loaded: false);
        own.Sections.Add(ModuleInfo.Note(ModuleInfo.SectionName, entries));

        Dyn dyn = new() { Shared = true, Soname = Path.GetFileName(fileName) };
        foreach (string dependency in needed) dyn.Needed.Add(SoName(dependency));
        inputs.Add(("the module's link", own));

        byte[] image = LinkDynamic(inputs, dyn, null, 0, kernel.LongMode);

        // WHAT THE KERNEL'S BINDER WILL BE ASKED TO DO, checked now.
        List<string> errors = new();
        if (dyn.TextRel) errors.Add("a module may not relocate its text, and this one would have to (DT_TEXTREL)");
        foreach (DynReloc r in dyn.Relocations)
            if (r.Kind is not (RelocKind.Relative or RelocKind.Abs32 or RelocKind.Abs64 or RelocKind.GlobData or RelocKind.JumpSlot))
                errors.Add($"'{r.Symbol}' needs a {r.Kind} relocation, which the kernel's binder does not apply");
        foreach (DynSymbol s in dyn.Symbols)
        {
            if (s.Name.Length == 0 || s.Definition is not null || kernel.Names.Contains(s.Name)) continue;
            if (theirs.Values.Any(exports => exports.Contains(s.Name))) continue;
            errors.Add("'" + s.Name + "' is imported, and neither " + kernel.Name + " nor a module this one depends on exports it");
        }
        if (errors.Count != 0) throw new LinkException(errors);
        return image;
    }

    /// <summary>
    /// [ModuleInitializer] METHODS, which C# runs when the assembly that
    /// holds them is loaded. Each unit names its own in a note
    /// (ModuleInfo.InitializerSection); a shared object with any gets a
    /// function of the link's making, which DT_INIT names instead of
    /// __corsac_init: a direct call to __corsac_init, so the image's statics
    /// are roots before an initialiser stores in one, then to each of them
    /// in link order, and a return. Every callee is this image's own, so each
    /// call is resolved here and none is left to the binder. In long mode the
    /// stack is kept sixteen-byte aligned across the calls, as the System V
    /// ABI has it. Null when no unit has one.
    /// </summary>
    private static ObjectFile? Initializers(List<Input> inputs, bool longMode)
    {
        List<string> initializers = ModuleInfo.Take(inputs.Select(input => (input.Name, input.Object)), ModuleInfo.InitializerSection);
        if (initializers.Count == 0) return null;
        HashSet<string> defined = new(inputs.SelectMany(input => input.Object.Symbols).Where(s => s.IsDefined).Select(s => s.Name), StringComparer.Ordinal);
        foreach (string initializer in initializers)
            if (!defined.Contains(initializer))
                throw new LinkException(new[] { "module initializer '" + initializer + "' is not defined by any object" });
        List<string> calls = new();
        if (defined.Contains(Elf.SharedInitName)) calls.Add(Elf.SharedInitName);
        calls.AddRange(initializers);
        ObjectFile stub = new();
        Section text = new(".text", SectionKind.Code) { Align = 16 };
        if (longMode) text.Bytes.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x08 });          // sub rsp, 8
        foreach (string call in calls)
        {
            text.Bytes.Add(0xE8);                                                            // call rel32
            text.Relocs.Add(new Relocation(text.Bytes.Count, call, -4, RelocKind.Rel32));
            text.Bytes.AddRange(new byte[4]);
        }
        if (longMode) text.Bytes.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x08 });          // add rsp, 8
        text.Bytes.Add(0xC3);                                                                // ret
        stub.Sections.Add(text);
        stub.Symbols.Add(new Symbol { Name = ModuleInitName, Section = text, Offset = 0, Size = text.Bytes.Count, IsFunction = true });
        return stub;
    }
}
