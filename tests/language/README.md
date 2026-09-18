# Language tests

Portable compiler/runtime/library fixtures and the language test runner.
From the repository root, run `bash tests/language/run.sh` or supply a filename
filter, such as `bash tests/language/run.sh strings`.

Executable fixtures default to a ten-second timeout. A `// timeout: 300`
header gives an integration fixture its own positive timeout in seconds;
an explicit `TIMEOUT` environment setting overrides the header. The kernel
fixture uses a committed snapshot from `CORSAC_ROOT` (defaulting to the sibling
`corsac86-integration` checkout), requires zero compilation errors, and retains
diagnostics under `build/kernel-binds.*` without modifying that checkout.

The [language test guide](../../docs/LANGUAGE-TESTS.md) documents fixture headers,
runner options and coverage. Expected behavior follows standard C# and .NET;
missing compiler/library support is a defect, not a reason to weaken a test.
