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
| [DECLARATION-INDEX.md](DECLARATION-INDEX.md) | Bounded declaration storage, source fragments, fingerprints and lookup |
| [X86-BACKEND.md](X86-BACKEND.md) | x86 code generation, ABI and runtime mechanisms |
| [X86-CPU-PROFILES.md](X86-CPU-PROFILES.md) | CPU/FPU selection, instruction coverage and remaining acceptance |
| [WIN32-TARGET.md](WIN32-TARGET.md) | One 32-bit Windows program for Windows 95 through 11: the image, the platform layer, Forms over Windows' controls |
| [DOTNET-LIBRARY.md](DOTNET-LIBRARY.md) | Standard-library surface and compatibility status |
| [SELFHOST.md](SELFHOST.md) | Native self-compilation workflow and historical findings |
| [LANGUAGE-TESTS.md](LANGUAGE-TESTS.md) | Test format, running tests and coverage guide |
| [BENCHMARKS.md](BENCHMARKS.md) | Workloads, profiling and historical measurements |
| [OPTIMIZATIONS.md](OPTIMIZATIONS.md) | Numbered optimization implementation/evidence ledger |
| [HEAPS.md](HEAPS.md) | One collector, a heap per kernel module: what is each heap's own, the current heap switched at a module's gates, the API and the stages |
| [REGIONS.md](REGIONS.md) | Region inference: the runtime arena, the link's engines and judge, and how units apply it |
| [PASS-INVENTORY.md](PASS-INVENTORY.md) | Existing passes and their integration status |
| [LARGE-BATCH.md](LARGE-BATCH.md) | Planned optimization batch and validation obligations |

The repository's [requirements](../REQUIREMENTS.md) and
[task checklist](../TODO.md) remain at the root. Short READMEs beside source,
tests and tools describe those directories and link here for detailed material.
Historical measurements retain their source snapshots and do not certify the
current checkout or imply their local build logs are distributed with the repo.
