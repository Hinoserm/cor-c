# Tests

- `language/`: executable CORSAC/C# language and library specifications.
- `integration/`: shared-library and multi-artifact workflows.
- `benchmarks/`: compiler/runtime performance workloads and optimization ledgers.

Focused component tests live beside their owners: `compiler/tests/` and
`linker/tests/`. Architecture-specific compiler tests are under
`compiler/tests/arch/`.

Run host unit programs with `bash tests/run-unit.sh`.
