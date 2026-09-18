# Cross-object LTO fixtures

Distinct C# sources compiled to ELF objects and linked through corlink. The
constant-return case must change emitted code while preserving behavior. The
side-effect case must not fold. These static primitive cases use --ref source
declarations and disable precise stack maps; they are not acceptance of managed
type/layout linking or demand-loaded metadata.
