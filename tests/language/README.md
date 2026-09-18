# Language tests

Portable compiler/runtime/library fixtures and the language test runner.
From the repository root, run `bash tests/language/run.sh` or supply a filename
filter, such as `bash tests/language/run.sh strings`.

The [language test guide](../../docs/LANGUAGE-TESTS.md) documents fixture headers,
runner options and coverage. Expected behavior follows standard C# and .NET;
missing compiler/library support is a defect, not a reason to weaken a test.
