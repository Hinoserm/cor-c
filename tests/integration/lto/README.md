# Cross-object LTO fixtures

Distinct C# sources compiled to ELF objects and linked through corlink. The
constant-return case must change emitted code while preserving behavior. The
side-effect case must not fold. These static primitive cases use --ref source
declarations and disable precise stack maps; they are not acceptance of managed
type/layout linking or demand-loaded metadata.

The static-initializer fixture is compiled with the real runtime into one
object, then passed through corlink. It verifies initialization survives LTO;
it does not claim cross-unit managed initialization/layout acceptance.
