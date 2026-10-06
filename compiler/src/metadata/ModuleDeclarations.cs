namespace Corsac.Lang;

/// <summary>
/// Turns `[ModuleAlias("pnp:CTL0031")]` and `[ModuleDependency("mpu401")]`
/// on a driver's class into the entries of its module's .corsac.modinfo
/// (ModuleInfo in the linker). docs/software/DRIVERS.md in the OS repository
/// is the design: a driver says what it answers to, one alias each --
///
///   pnp:CTL0031                ISA Plug and Play logical device
///   pci:v10ECd8139             PCI vendor and device
///   pci:c010180                PCI class, subclass, interface
///   eisa:ADP7771               EISA identifier
///   isa:sb16@220,240,260,280   legacy ISA: a probe at those ports
///   i8042:aux                  a child of the 8042
///   platform:fdc               a fixed device of the board
///   virtio:d00000001           a virtio device, by its type
///
/// -- and a bus that finds a device looks its alias up. The attribute is on
/// the class because the class is the driver: the module holding it is what
/// the alias loads, and two drivers in one module are both offered.
///
/// Read from the DECLARATIONS, as the registry's attributes are
/// (RegistryDeclarations): of this unit's own types, never one it was given
/// for its declarations, whose module is somebody else's.
/// </summary>
public static class ModuleDeclarations
{
    /// <summary>The buses an alias may name, each the prefix before its colon.</summary>
    static readonly HashSet<string> Buses = new(StringComparer.Ordinal) { "pnp", "pci", "eisa", "isa", "i8042", "virtio", "platform" };

    public static List<string> Collect(CompilationUnit unit, List<CompileError> errors)
    {
        List<string> entries = new();
        foreach (TypeDecl decl in unit.Types)
        {
            if (decl.Elsewhere || decl.SignatureOnly) continue;
            foreach (AttributeRef attribute in decl.AttributeParts)
            {
                bool alias = attribute.Is("ModuleAlias"), dependency = attribute.Is("ModuleDependency");
                if (!alias && !dependency) continue;
                string? value = attribute.Argument;
                if (string.IsNullOrEmpty(value) || attribute.Arguments.Count != 1)
                {
                    errors.Add(Error(decl, $"[{attribute.Name}] takes one string: {(alias ? "the alias, \"pnp:CTL0031\"" : "the module's name, \"mpu401\"")}"));
                    continue;
                }
                if (value.Any(c => c == '\0' || char.IsWhiteSpace(c)))
                {
                    errors.Add(Error(decl, $"[{attribute.Name}(\"{value}\")] may hold no space and no NUL"));
                    continue;
                }
                if (alias)
                {
                    int colon = value.IndexOf(':');
                    if (colon <= 0 || colon == value.Length - 1 || !Buses.Contains(value[..colon]))
                    {
                        errors.Add(Error(decl, $"'{value}' is not an alias: one is a bus ({string.Join(", ", Buses.Order(StringComparer.Ordinal))}), a colon and what that bus calls the device"));
                        continue;
                    }
                }
                string entry = (alias ? "alias=" : "depends=") + value;
                if (!entries.Contains(entry)) entries.Add(entry);
            }
        }
        return entries;
    }

    static CompileError Error(TypeDecl decl, string message) => new(decl.File, decl.Line, decl.Col, message);
}
