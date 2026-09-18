# CORSAC/C# Implementation Status and TODO

## Status and scope

Unchecked items are outstanding, partial, or unverified. Checked items record
completion only for the stated scope and source snapshot. Standing rules belong
in [REQUIREMENTS.md](REQUIREMENTS.md).
Implemented but unverified work stays TODO. A passing historical milestone
never certifies later changes, full self-hosting, or real-486 performance.

This file tracks current and earlier non-superseded implementation work for the
CORSAC/C# repository. End-state acceptance criteria are defined in
[REQUIREMENTS.md](REQUIREMENTS.md). CORSAC/OS drivers, kernel, TTY, networking,
and other operating-system work remain in the CORSAC/OS repository and are not
cancelled by this extraction.
Evidence paths below are relative to the repository root. Local build logs
may not exist in a fresh clone; keep the corresponding fixtures and rerun the
required gates rather than assuming a missing log is proof of completion.

## Priority and purpose

The current sequence is build utility design/implementation, the repository's
corsac.build and source/project reorganization, then separate compilation and
self-hosting memory work. Keep pushing documented implementation checkpoints.

Separate compilation must make self-hosting practical on small 486-class
systems as well as use modern multicore hosts effectively. The implementation
tasks below track that work; moving files alone does not reduce the working set.

## Build utility and executable separation

- [ ] Replace Driver.DefaultLibraries' eager Linux library source loading with
  demand-loaded library resolution. Resolve names through each file's `using`
  scope and actual references, and load only needed declarations/bodies and
  their required dependencies. Preserve standard C# fully qualified references,
  enclosing namespaces, aliases, extension lookup and implicit runtime support:
  a using directive enables lookup, not unconditional inclusion or exclusion.
  Removing unused code after loading the entire library does not satisfy this.

- [x] Specify XML orchestration, nested scope, ordered/parallel execution,
  process/script/test behavior and MSBuild compatibility in
  [BUILD-SYSTEM.md](docs/BUILD-SYSTEM.md).
- [x] Implement the initial host runner: discovery, nested target scope, graph
  validation, ordered steps, subprocess limits, scripts, timeout/cleanup and
  JUnit reports. Thirteen focused runner checks passed at the first milestone.
  Full profile/artifact/resource semantics remain below as separate tasks.
- [x] Supply cor-c's corsac.build and component directory defaults.
- [x] Build compiler and linker as independent .NET-hosted executables and
  document ELF relocatable objects. Compiler -> `.o` -> corlink -> Linux program
  passed; this does not establish managed file-by-file compilation.
- [ ] Restore project paths, split source types, and verify the reorganized repo.
- [ ] Implement native MSBuild-compatible project evaluation and compilation;
  a host dotnet adapter alone does not satisfy this requirement.
- [ ] Implement verified bootstrap and atomic native toolchain activation.
- [ ] Implement source locks/fetching, artifact references, installation/image
  tasks, profiles, imports and whole-build memory/resource budgeting.
- [ ] Verify the build utility itself compiles and runs natively under COR-C#.

## Compatibility defects

- [ ] Audit and repair compatibility gaps as they are found, record them as
  open defects, and add standard-behavior regression coverage. A feature
  with a known compatibility failure must not be marked complete merely
  because a custom workflow passes.

## Project compilation and source organization

- [x] Audit documentation placement; move optimization plans, pass inventory,
  benchmark write-ups and the detailed language-test guide into flat docs/.
  Keep short local READMEs, update references, and index the documents.
- [ ] Support projects containing multiple source files, independently compiled
  units, and a separate final link step using the existing ELF linker.
- [ ] Compile and release bounded units rather than retaining every source body,
  syntax tree, bound graph, and intermediate representation at once.
- [ ] After the project path works, reorganize the compiler toward one top-level
  class/type per matching source file, following ordinary C# conventions.
- [ ] Preserve useful partial-class subdivisions. Physical file boundaries must
  not change partial-type semantics or force unrelated bodies into memory.
- [ ] Update all build/source inventories and preserve native self-hosting.
- [x] Repository ownership now separates `compiler/` from `linker/`; object
  format and linking sources and their focused tests live under `linker/`.
- [x] Target-specific compiler support now lives below `compiler/src/arch/`,
  with focused target tests below `compiler/tests/arch/`. A repository-wide
  `architectures/` directory is not part of the layout.
- [x] The first one-type-per-file source split is complete for the IR object
  model and the call-boundary optimizer helpers and passes. Larger frontend,
  IR, and backend groups remain to be split without changing behavior.
- [x] Split syntax nodes, bound symbols, IR values/functions/blocks, x86 machine
  IR and ELF records into individually named files. Host compiler and linker
  build with zero warnings/errors; optimizer, assembler, x86 and linker suites
  passed through the new runner after this milestone.

## Demand-loaded declarations, not a whole-project metadata graph

- [ ] Do not retain a full declaration footprint for every class in every file.
- [ ] Maintain a compact disk-backed index from qualified type names to the
  locations of their declaration records. Access to the index itself must be
  bounded; it must not require loading the full project index into RAM.
- [ ] Resolve a compilation unit's references through its declared lookup scope.
  Load only the declarations actually needed to resolve and compile those
  references, not all declarations from the project or an imported namespace.
- [ ] A using directive makes namespace members eligible for name lookup. It is
  not an instruction to load all classes in that namespace. In particular,
  using System must not load the entire System namespace.
- [ ] Preserve standard C# lookup for current namespaces, enclosing/nested types,
  fully qualified names, namespace/type aliases, global usings, using static,
  and extension-method candidates. Do not require artificial using directives
  for references that ordinary C# permits without them.
- [ ] Use indexed candidate lookup for operations such as extension-method and
  overload discovery instead of eagerly materializing every possible class.
- [ ] Resolve ambiguity and accessibility correctly; never choose a declaration
  merely because it happened to be loaded first.

## Transitive dependencies and implementation bodies

- [ ] Load required dependency declarations on demand: base types, interfaces,
  member signatures, generic constraints, constants, and layout information.
- [ ] Keep ordinary method bodies separate from declaration records. Inspecting
  a signature must not bring the method's implementation into memory.
- [ ] Fetch generic bodies only when specialization requires them, and fetch
  cross-unit optimization bodies only when a selected optimization needs them.
- [ ] Handle cyclic references and partial types without eagerly loading all
  implementation bodies participating in the cycle.
- [ ] Preserve one consistent identity and layout for each type across units,
  including static storage, static initialization, descriptors, generic
  instantiations, inheritance, interface dispatch, and exception matching.

## Memory ownership and internal parallelism

- [ ] Share immutable loaded declaration records between workers rather than
  duplicating the project declaration graph per worker.
- [ ] Bound the metadata cache and release or evict unused records. Live work may
  pin required records; eviction must not invalidate references or identities.
- [ ] Use one compiler process with bounded internal workers, not a collection of
  independent compiler processes each retaining its own compiler working set.
- [ ] Bound concurrently resident unit bodies and intermediate representations.
  Worker scheduling must respect memory limits as well as available CPUs.
- [ ] Support single-worker, low-memory operation on a 486-class host. Parallelism
  must not be required for correctness or for making forward progress.
- [ ] Keep diagnostics and output deterministic across worker counts and cache
  load/eviction order. Release completed-unit and obsolete analysis state.

## Linking, incremental work, and cross-unit optimization

- [ ] Extend the existing object writer/linker; do not build a second linker.
- [ ] Link independently compiled units without duplicate runtime/type identities
  or silently accepting conflicting definitions.
- [ ] Cache unit artifacts with dependency-aware invalidation. Declaration and
  layout changes must invalidate affected consumers; unrelated implementation
  changes should not force recompiling the entire project.
- [ ] Preserve ordinary cross-file language behavior and diagnostics. An object
  link that succeeds despite incompatible unit assumptions is not acceptance.
- [ ] Implement compact summaries for whole-program decisions and selective body
  loading for cross-unit optimization, following the general ThinLTO approach.
- [ ] Do not reconstruct the complete project AST or IR during linking/LTO. Keep
  optimization working sets bounded, including when running without parallelism.

## Evidence required for acceptance

- [ ] Demonstrate a project with separately compiled, mutually referencing files,
  partial types, inheritance/interfaces, generics, and static initialization.
- [ ] Verify scoped lookup, qualified references, aliases, ambiguity/accessibility
  diagnostics, and transitive dependencies without eager namespace loading.
- [ ] Exercise cache eviction, incremental invalidation, and deterministic output
  with one worker and multiple workers.
- [ ] Build the compiler through the native project path, run the resulting
  compiler, and verify the existing self-host smoke and generation-parity gates.
- [ ] Measure peak resident memory, mapped heap, live managed data, loaded metadata,
  and phase/end-to-end time separately. Report actual completed-run peaks.
- [ ] Demonstrate bounded single-worker memory use as project size grows beyond
  the actively compiled dependency set, and verify a stated small-system memory
  budget. Do not claim 486 suitability from instruction compatibility alone.
- [ ] Merely passing a small fixture, surviving the 32-bit address-space limit, or
  moving the same whole-program memory footprint into the linker is not enough.

## Optimization delivery

- [ ] Deliver at least 100 distinct, substantive compiler optimizations with
  real workload relevance and correctness evidence. Existing passes,
  equivalent variants, runtime/library tuning and policy flags do not
  count as separate newly delivered compiler optimizations.
  Inventory: docs/OPTIMIZATIONS.md. Candidate counts are not accepted
  completion counts; the per-entry evidence audit is still outstanding.
- [ ] Continue the requested substantial batch of roughly 40 transformations,
  with benchmark checkpoints after cohesive sub-batches. This is part of,
  not a replacement for, the 100-optimization requirement.
  Inventory and legality obligations: docs/LARGE-BATCH.md.
- [ ] Enable accepted integrated optimizations by default, with explicit
  exclusions for diagnosis. Do not require per-optimization opt-in flags.
  The temporary --experimental-batch/--batch-without policy remains to be
  replaced; experimental --opt-size has not been accepted as the default.
- [ ] Reduce redundant instructions, safely reuse results, improve loops,
  bounds-check handling, register/spill traffic and x87 use, while keeping
  allocation/lifetime and arithmetic legality explicit.
- [ ] Complete remaining boxed-struct equality compatibility (reference fields,
  padding, floating NaN and signed-zero behavior), not just raw-byte equality.

## Internal parallelism: current scope and remaining acceptance

- [x] Existing whole-program command accepts multiple source files and source
  response lists. This is NOT bounded separate project compilation.
  Source: compiler/src/driver/Driver.cs and compiler/src/driver/Frontend.cs.
- [x] Bounded backend worker pipeline with ordered output has native serial/
  four-worker PIC and non-PIC object parity over the backend fixture.
  Evidence: tests/benchmarks/backend-workers.cor;
  build/perf-native-backend-workers-run.log.
- [x] Read-only-data folding and scalar replacement have a native worker
  fixture covering pipeline worker propagation, 64 replacements, IR
  verification and serial/four-worker parity.
  Evidence: build/perf-native-module-pipeline-run.log.
- [x] Native fixture checkpoint for constant-return and dead-return worker
  transformations passed serial/four-worker parity.
  Evidence: build/perf-native-return-workers-run.log. This predates the
  later dead-argument extension of tests/benchmarks/module-workers.cor.
- [x] Default-argument spelling no longer mutates shared parameter declarations;
  it uses binder-owned caches. Six relevant checks passed.
  Evidence: build/perf-default-isolation-proof.log.
- [x] Inherited generic tuple-use annotations passed the focused regression.
  Evidence: build/perf-inherited-type-uses-proof.log. This does not complete
  isolation of synthesized symbols or all nested generic cases.
- [ ] Complete safe parallel binding. Body work is still serial; declaration
  container copying and stable closure names alone do not make it safe.
  Isolate synthesized tuple/array metadata and merge results deterministically.
- [ ] Complete eligible module-wide parallel work and dependency scheduling.
  Per-file parsing and per-function optimization already use tasks;
  inliner analyses run concurrently but actual inlining expansion remains
  serial. Lowering and other shared-state work still require coverage.
- [ ] Verify the later parallel dead-argument analysis/application changes in
  the expanded native module fixture and integrated pipeline.
- [ ] Complete native thread-pool sizing, memory-aware worker limits, fault
  handling, GC-under-worker-load and deterministic diagnostics acceptance.
  Use tasks/async for coordination, not as a substitute for CPU workers.
- [ ] Establish repeatable quiet end-to-end serial/parallel throughput and
  peak-memory results for the same current-source workload. No accepted
  whole-compiler speedup follows from worker count or a noisy timing pair.

## Allocation, retention and low-memory acceptance

- [x] Smaller-fragment TLS refill before heap growth passed ten GC checks at
  its checkpoint. Evidence: build/perf-fragment-refill-proof.log.
  This removed the observed 64 KiB minimum-buffer growth behavior; it
  does not prove a low-memory compiler or absence of other fragmentation.
- [x] Free-bin tree regression was updated to remain below the dedicated-mapping
  threshold and require actual tree probes; focused check passed.
  Evidence: build/perf-fragment-tree-proof.log.
- [x] Lazy List backing allocation passed the 10,000-empty-list memory and
  capacity regression. Evidence: build/perf-list-lazy-proof.log.
- [ ] Complete integrated collection reference-release acceptance.
  List, Queue, Stack, Dictionary and HashSet removal/clear paths were
  changed. An earlier six-test checkpoint passed
  (build/perf-collection-release-milestone.log), but the subsequent
  lazy-list collection run was 5/1 (build/perf-list-lazy-collections.log).
  The current retention fixture attempts to isolate stale conservative
  stack roots on a joined thread, but DOES NOT COMPILE:
  build/perf-collection-release-thread-scope.log reports missing standard
  Thread construction and parameterless instance Start(). Fix the missing
  API and validate the retention behavior; do not mark this requirement done.
- [ ] Verify release of consumed parser roots and obsolete binding tables
  through complete native self-compilation and project-unit lifetimes.
  ReleaseForRebind and parser-root clearing are implemented; partial-run
  live-heap reductions are not full-build acceptance.
- [ ] Finish heap-index/anchor allocation-failure coverage, concurrent allocator
  auditing, fragmentation/lock-contention reduction and bounded GC pauses.
  Existing chunk indexes, AVL free bins, page anchors and dedicated large
  mappings are foundations, not proof of a practical 486 memory footprint.
- [ ] Reduce genuinely live compiler data as well as reserved/fragmented memory.
  Use compact representations, avoid duplicate analysis and release
  temporary state. Do not merely raise heap limits or timeouts.
- [ ] Bound low-memory behavior of metadata lookup, generic specialization,
  lowering, optimization, object emission and linking, including cyclic
  project dependencies and single-worker execution.

## Benchmarking and self-hosting

- [ ] Reduce SSH connection/crypto latency and terminal/shell latency with
  measurements; microbenchmark improvements alone do not complete this.
- [ ] Complete fresh native self-compilation, run the second-generation compiler,
  and satisfy smoke, output and compiler-generation parity gates.
  Harness: tests/benchmarks/selfcompile.sh. Earlier native smoke successes and
  old suite milestones do not certify the current full self-build.
- [ ] Complete project-based self-build using separately compiled units, then
  demonstrate self-hosting inside CORSAC, not only as a Linux executable.
- [ ] Replace Python build/support scripts required by the self-hosting workflow
  with tooling that actually runs inside CORSAC, preserving functionality.
- [ ] Establish and verify an explicit small-system memory budget and actual
  486 performance. Modern-host speed, x86 instruction compatibility and
  surviving below 4 GiB do not constitute small-system acceptance.
- [x] Standalone corc link command is implemented using the existing ELF linker
  and builds on the .NET host with zero warnings/errors.
  Evidence: linker/src/driver/ObjectLinkCommand.cs;
  build/perf-project-link-build.log.
- [ ] Validate standalone link execution, cross-unit references and diagnostics,
  then integrate it into the bounded native project build. The preceding
  DONE item is build-only, not end-to-end link/project acceptance.
