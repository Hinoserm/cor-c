# Tests

- `language/`: executable CORSAC/C# language and library specifications.
- `integration/`: shared-library and multi-artifact workflows.
- `benchmarks/`: compiler/runtime performance workloads and optimization ledgers.

Focused component tests live beside their owners: `compiler/tests/` and
`linker/tests/`. Architecture-specific compiler tests are under
`compiler/tests/arch/`.

Run host unit programs with `bash tests/run-unit.sh`.

## Checking the compiler's frees

The escape pass frees objects itself (docs/OPTIMIZATIONS.md, rows 033-040). A
wrong free is quiet by default: the allocator ignores a second free and hands
the bytes straight out again. `CORSAC_VERIFY_FREES=1` in a hosted program's
environment turns on the collector's checked mode (`Gc.VerifyFree`): a freed
block is filled with a pattern no pointer has and never given out again, so a
read after the free finds the pattern (a pointer read through it faults), and a
second free is reported and ends the process with 97. Run the language suite
under it to check every compiler-inserted free:

    CORSAC_VERIFY_FREES=1 bash tests/language/run.sh

Tests that print the collector's own figures (collections run, memory reused)
or exercise the allocator's free API directly -- 100, 110, 111, 112, 670,
671, escape_fresh_return and the optimizer_gc_* allocator tests -- differ
under it by design; everything else must pass. `tests/integration/checked-frees.sh`
checks the mode itself.

