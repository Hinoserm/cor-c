# CORSAC/C# Requirements

CORSAC/C# is the proper product name. COR-C# is the accepted short name.

This document defines the required end state of the compiler, runtime, standard
library, linker, project system, and self-hosting toolchain. It does not track
implementation progress. Current status, completed checkpoints, and outstanding
work belong in [TODO.md](TODO.md).

## Standards compatibility

### C# language

- Any departure from the applicable C# language specification is a product
  failure, not an intentional COR-C# dialect feature.
- The compiler must preserve standard syntax, semantics, name lookup, overload
  resolution, conversions, generic behavior, accessibility, exceptions,
  threading semantics, and observable evaluation order.
- Missing standard language functionality is unfinished work. A custom syntax
  or project-specific workaround is forbidden.
- Valid standard C# programs must not be rewritten merely to accommodate a
  compiler limitation.

### .NET libraries

- Public APIs must use the standard .NET names, namespaces, signatures,
  overloads, exceptions, and observable behavior applicable to the supported
  profile.
- A custom helper does not replace a missing standard API. For example, a
  static `Thread.Start(Action)` helper does not replace the standard `Thread`
  constructor and parameterless instance `Start()` method.
- Collections, I/O, networking, cryptography, threading, reflection-like type
  behavior, and object lifetime must follow their applicable .NET contracts.
- Platform limitations must be reported through standard behavior, including
  the appropriate exception where the contract permits unsupported platforms.
  Silent success, fabricated results, or renamed substitute APIs are failures.
- Expected behavior must be established from the C# specification, official
  .NET documentation, and the reference implementation. Project tests are
  evidence and may not redefine a conflicting standard contract.

## Repository and source organization

- The public project repository is `Hinoserm/cor-c` and uses the MIT license.
- The repository must remain buildable without undeclared reads from the
  CORSAC/OS source tree.
- Top-level ownership is:
  - `compiler/`: the compiler executable, project tooling, frontend, lowering,
    IR, optimization, metadata, and target-specific compiler support. Target
    code belongs below `compiler/src/arch/`; focused target tests belong below
    `compiler/tests/arch/`.
  - `linker/`: object-format support, relocations, static/dynamic linking, and
    focused linker tests.
  - `runtime/`: managed execution support plus architecture and platform
    adapters.
  - `stdlib/`: public C#/.NET library APIs and portable implementations.
  - `tests/`: unit, language, integration, and benchmark coverage.
  - `examples/`: small supported programs.
  - `docs/`: design and development documentation.
- Every maintained directory must contain a README explaining its purpose and
  ownership boundary.
- Compiler implementation files belong under `compiler/src/`.
- A repository-wide `architectures/` directory is forbidden. Architecture code
  belongs under the subsystem that owns it, using the short `arch` name.
- Source should normally use one top-level type per correspondingly named file.
  Purposeful partial-class subdivisions may remain where they make a large
  compiler stage easier to maintain.
- Architecture, platform, ABI, and object-format concerns must remain separate.
  ELF is not inherently Linux-specific, and x86 code must not inherently assume
  Linux or CORSAC/OS.

## Project and separate compilation

- The installed `build` utility reads `corsac.build`, an XML orchestration
  manifest with nested targets, dependencies, ordered steps, executable/script
  tasks, test reporting, bootstrap, source acquisition, staging and image builds.
- Standard .csproj files remain authoritative for C# component builds under
  both Microsoft .NET and COR-C#. No duplicate source/dependency inventories or
  required .sln files are introduced. Unsupported active build features fail
  explicitly; native builds never silently switch to the host compiler.
- `build bootstrap` prepares and verifies a native toolchain; subsequent
  `build` uses that toolchain. CPU and memory budgets cover the whole build.
- [BUILD-SYSTEM.md](docs/development/BUILD-SYSTEM.md) defines orchestration
  semantics and the compatibility contract.

- Projects must support multiple source files compiled as bounded independent
  units and linked through the existing linker.
- The compiler must not retain every source body, syntax tree, bound graph, and
  intermediate representation for the entire project simultaneously.
- File boundaries must not change C# semantics for partial types, namespaces,
  accessibility, generics, static initialization, inheritance, or interfaces.
- Diagnostics and output must be deterministic across worker counts, unit
  scheduling, cache state, and metadata eviction order.
- Completed-unit and obsolete-analysis memory must be released promptly.
- The implementation must support both low-memory single-worker operation and
  bounded parallel compilation on multicore hosts.

## Demand-loaded declarations

- The compiler must not load or retain a full declaration graph for every type
  in every project file or referenced library.
- A compact disk-backed index must map qualified names to declaration records
  without requiring the complete index or complete declarations in memory.
- A `using` directive makes names eligible for lookup; it must not load every
  type in the namespace. In particular, `using System` must not materialize the
  complete `System` namespace.
- Standard lookup must work for the current namespace, enclosing and nested
  types, fully qualified names, aliases, global usings, `using static`, and
  extension-method candidates.
- Indexed candidate lookup must support ambiguity, overload, accessibility,
  and extension-method resolution without depending on load order.
- Transitive declarations must load on demand, including base types,
  interfaces, signatures, generic constraints, constants, and layouts.
- Ordinary method bodies must remain separate from declaration records.
- Generic bodies may load when specialization requires them. Cross-unit bodies
  may load when a selected optimization requires them. Neither case permits
  eager loading of every implementation body.
- Cyclic references and partial types must not force unrelated implementation
  bodies into memory.

## Identity and ABI consistency

- Every type must have one consistent identity and layout across compilation
  units, including descriptors, static storage, static initialization,
  inheritance, interface dispatch, generic instantiations, exception matching,
  and object layout.
- ABI facts shared by the compiler, runtime, libraries, and consumers must be
  versioned and validated across artifacts.
- Separately compiled objects must not silently carry incompatible assumptions.
- Duplicate definitions, duplicate generic bodies, and duplicate type
  descriptors must be diagnosed or coalesced only under a defined rule.

## Linking, incremental builds, and cross-unit optimization

- The existing object writer and linker must be extended; the project must not
  grow a second incompatible linker.
- The linker must accept independently compiled units, resolve cross-unit
  references, diagnose conflicts, and preserve one runtime/type identity.
- Project artifacts must record dependencies strongly enough to invalidate
  consumers when declarations, layouts, ABI, or relevant options change.
- An unrelated implementation-only change should not force recompilation of
  the entire project.
- Cross-unit optimization must use compact summaries and selective body loading,
  following the scalable principles of ThinLTO.
- Link-time optimization must not reconstruct the complete project AST or IR in
  memory.
- Optimization and linking working sets must remain bounded even with one worker.

## Compiler pipeline and parallelism

- Internal parallelism must cover eligible parsing, binding, optimization,
  lowering, backend, and project work—not only instruction encoding.
- Parallel work must use one compiler process and a bounded worker pool rather
  than multiple independent compiler processes with duplicated working sets.
- Shared state must be immutable or synchronized. Worker-owned results must be
  merged deterministically in source/module order.
- Module-wide transformations must have explicit analysis and mutation barriers.
- Async coordination must not be mistaken for CPU parallelism.
- Worker count must respect both available processors and a configured or
  derived memory budget.
- Failures, diagnostics, generated bytes, symbols, relocations, and stack maps
  must be stable across serial and parallel compilation.

## Runtime architecture

- `runtime/src/core` contains platform-independent allocation, garbage
  collection, exception, type, synchronization, and runtime-helper machinery.
- `runtime/src/arch/<architecture>` contains CPU mechanisms independent of an
  operating system, such as stack/register operations and machine stubs.
- `runtime/src/platforms/<platform>` contains host services such as virtual
  memory, native threads, blocking/waking, clocks, process exit, and raw system
  interfaces.
- OS-and-architecture-specific conventions belong below the combined platform
  boundary rather than leaking into generic architecture code.
- CORSAC/OS kernel, bootloader, scheduler, driver, and hardware product code
  remain in the CORSAC/OS repository. Only reusable runtime mechanisms and the
  CORSAC user-process adapter belong here.
- Runtime initialization must avoid circular dependence on high-level standard
  library facilities.

## Standard-library architecture

- Portable public implementations are organized by their standard namespaces
  under `stdlib/src/System/`.
- Internal platform adapters translate host services and errors into standard
  .NET behavior without changing the public API.
- Common POSIX implementation may be shared only where behavior is genuinely
  common; Linux and CORSAC/OS differences must remain explicit.
- Runtime mechanisms and public library APIs are separate layers. There must
  not be competing public implementations of the same contract.

## Memory and garbage collection

- Native self-compilation must fit practical 486-class memory budgets.
  Merely surviving below the 32-bit address-space ceiling is insufficient.
- Compiler memory must scale with the active unit and its actual dependency set,
  not with all classes in all project files and libraries.
- Metadata caches must be bounded and evictable while preserving pinned identity
  and layout records required by live work.
- Concurrency must not multiply the full compiler working set per worker.
- Avoid heap allocation for small temporary buffers where stack or reusable
  per-owner/per-thread storage is safe.
- Static storage may be used only when sharing, lifetime, reentrancy, and secret
  erasure remain correct.
- Allocation and GC changes must preserve object identity, zero initialization,
  concurrency, precise exception behavior, and collector visibility.
- Measurements must distinguish mapped heap, resident memory, live managed data,
  metadata, fragmentation, allocation rate, collection work, and pause time.

## Optimization requirements

- The default target is 486 plus x87. Generated code must not silently use
  Pentium, MMX, SSE, or later instructions.
- Size is a first-class performance concern on the 486. Decisions must account
  for instruction fetch, code footprint, calls, spills, and memory traffic.
- Intended optimizations must not be removed merely to make self-hosting pass;
  the compiler must be repaired so it can compile them.
- Accepted integrated optimizations must be enabled by default, with explicit
  exclusions available for diagnosis. Individual opt-in switches are not the
  final production policy.
- The project must deliver at least 100 distinct, substantive optimizations with
  correctness evidence and real workload relevance. Equivalent operator
  variants, existing passes, policy knobs, and runtime/library tuning do not
  count as separate compiler optimizations.
- LLVM and GCC may inform algorithms, but legality must be adapted to managed
  semantics and the local IR. C undefined behavior, LLVM poison assumptions,
  unsafe floating-point reassociation, and later-ISA assumptions may not leak in.
- Applicable source licenses and notices must be honored.

## Benchmarking and generated-code quality

- Cryptographic code and general workloads must both be benchmarked.
- Crypto measurements must retain known-answer, boundary, lifetime, and secret
  handling checks.
- Profile and inspect generated IR/disassembly before selecting expensive
  transformations.
- Comparisons must record revision, target, options, operation count, input
  lifecycle, code size, allocations, collections, and timing methodology.
- Different workloads or fresh/reused context models must not be presented as
  one continuous performance series.
- SSH, terminal, shell, compiler throughput, and memory latency goals require
  end-to-end measurements, not only isolated microbenchmarks.
- Real-486 performance claims require measurements on the target class of
  hardware or a clearly identified equivalent validation method.

## Self-hosting

- The native CORSAC/C# compiler must compile the same fixed source snapshot into
  a second generation.
- Each generation must build and run a smoke program, and compiler-generation
  parity must be checked under the defined deterministic build conditions.
- Bootstrap preparation time must be reported separately from native compiler
  time.
- Self-hosting must ultimately work inside CORSAC/OS, not only as a Linux i386
  executable.
- Build/support scripts required for in-system self-hosting must be replaced by
  tooling that runs under CORSAC/OS; Python must not remain a required in-system
  dependency.

## Verification and release quality

- Tests must exercise externally expected behavior. Valid tests may not be
  weakened to conceal implementation defects.
- Broad suites should run at meaningful milestones; focused checks should cover
  individual diagnoses and repairs.
- Regressions must record enough instrumentation to diagnose one run without
  repeated blind execution.
- A timeout is not a crash, a running process is not a completed result, and a
  historical green snapshot does not certify later source.
- Completion claims require current evidence matching the scope claimed.
- The public repository must not contain credentials, private machine images,
  local build artifacts, or unrelated CORSAC/OS source history.
- Public documentation must distinguish working, experimental, incomplete, and
  unverified features accurately.
