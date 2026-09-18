# Documentation

Designs, technical guides, optimization plans and performance evidence live
directly in this directory. Keep the structure flat; use descriptive filenames
and this index rather than creating a directory per topic.

| Document | Purpose |
| --- | --- |
| [REPOSITORY-LAYOUT.md](REPOSITORY-LAYOUT.md) | Source ownership, directory layout and migration |
| [BUILD-SYSTEM.md](BUILD-SYSTEM.md) | XML orchestration, nested targets, tasks, tests and bootstrap |
| [OBJECT-FORMAT.md](OBJECT-FORMAT.md) | Separate compiler/linker executables and intermediate objects |
| [SEPARATE-COMPILATION.md](SEPARATE-COMPILATION.md) | Managed metadata, compiler/linker/runtime ownership, LTO and bare-metal builds |
| [X86-BACKEND.md](X86-BACKEND.md) | x86 code generation, ABI and runtime mechanisms |
| [DOTNET-LIBRARY.md](DOTNET-LIBRARY.md) | Standard-library surface and compatibility status |
| [SELFHOST.md](SELFHOST.md) | Native self-compilation workflow and historical findings |
| [LANGUAGE-TESTS.md](LANGUAGE-TESTS.md) | Test format, running tests and coverage guide |
| [BENCHMARKS.md](BENCHMARKS.md) | Workloads, profiling and historical measurements |
| [OPTIMIZATIONS.md](OPTIMIZATIONS.md) | Numbered optimization implementation/evidence ledger |
| [PASS-INVENTORY.md](PASS-INVENTORY.md) | Existing passes and their integration status |
| [LARGE-BATCH.md](LARGE-BATCH.md) | Planned optimization batch and validation obligations |

The repository's [requirements](../REQUIREMENTS.md) and
[task checklist](../TODO.md) remain at the root. Short READMEs beside source,
tests and tools describe those directories and link here for detailed material.
Historical measurements retain their source snapshots and do not certify the
current checkout or imply their local build logs are distributed with the repo.
