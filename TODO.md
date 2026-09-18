# CORSAC/C# Implementation Status and TODO

Status and scope
----------------
[TODO] Outstanding, partially implemented, or not fully verified.
[DONE] Implemented and verified only to the explicitly stated scope/snapshot.
[RULE] Continuing requirement; not a one-time item that can be finished.
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

## Build utility and executable separation

- [x] Specify XML orchestration, nested scope, ordered/parallel execution,
  process/script/test behavior and MSBuild compatibility in BUILD-SYSTEM.md.
- [ ] Implement manifest discovery, strict validation, nested target resolution,
  graph planning, task execution, cancellation, bounded parallelism and reports.
- [ ] Supply cor-c's corsac.build and component directory defaults.
- [ ] Build the compiler and linker as independent executables. Preserve and
  document ELF relocatable objects as their intermediate exchange format.
- [ ] Restore project paths, split source types, and verify the reorganized repo.
- [ ] Implement native MSBuild-compatible project evaluation and compilation;
  a host dotnet adapter alone does not satisfy this requirement.
- [ ] Implement verified bootstrap and atomic native toolchain activation.
- [ ] Implement source locks/fetching, artifact references, installation/image
  tasks, profiles, imports and whole-build memory/resource budgeting.
- [ ] Verify the build utility itself compiles and runs natively under COR-C#.

Real separate project compilation must
make self-hosting practical on small 486-class systems as well as exploit
modern multicore hosts. Splitting source files without reducing the resident
working set is not sufficient. These are requirements, not completion claims.

## Mandatory C# and .NET compatibility criterion
[RULE] Any departure from existing C# language standards or .NET library
       contracts is a product failure, not an acceptable COR-C# variation.
       This applies to syntax, language semantics, name/type resolution,
       runtime behavior, API names/signatures/overloads, and library behavior,
       including exceptions, threading, collections and object lifetimes.
[RULE] Missing standard syntax or library functionality is unfinished work.
       A custom helper, renamed API or project-specific alternative does not
       satisfy the corresponding standard requirement. For example, a static
       Thread.Start(Action) helper does not replace the standard Thread
       constructor and parameterless instance Start() method.
[RULE] Change our compiler/runtime/library to conform. Do not rewrite valid
       standard C# callers, weaken valid tests, change expected results, or
       remove intended optimizations merely to hide a compatibility failure.
[RULE] Establish expected behavior from the C# specification, official .NET
       documentation and/or the reference implementation. Project tests are
       evidence, not authority to redefine C# or .NET when they disagree.
[TODO] Audit and repair compatibility gaps as they are found, record them as
       open defects, and add standard-behavior regression coverage. A feature
       with a known compatibility failure must not be marked complete merely
       because a custom workflow passes.

1. Project compilation and source organization
---------------------------------------------
[TODO] Support projects containing multiple source files, independently compiled
  units, and a separate final link step using the existing ELF linker.
[TODO] Compile and release bounded units rather than retaining every source body,
  syntax tree, bound graph, and intermediate representation at once.
[TODO] After the project path works, reorganize the compiler toward one top-level
  class/type per matching source file, following ordinary C# conventions.
[TODO] Preserve useful partial-class subdivisions. Physical file boundaries must
  not change partial-type semantics or force unrelated bodies into memory.
[TODO] Update all build/source inventories and preserve native self-hosting.
[DONE] Repository ownership now separates `compiler/` from `linker/`; object
  format and linking sources and their focused tests live under `linker/`.
[DONE] Target-specific compiler support now lives below `compiler/src/arch/`,
  with focused target tests below `compiler/tests/arch/`. A repository-wide
  `architectures/` directory is not part of the layout.
[DONE] The first one-type-per-file source split is complete for the IR object
  model and the call-boundary optimizer helpers and passes. Larger frontend,
  IR, and backend groups remain to be split without changing behavior.

2. Demand-loaded declarations, not a whole-project metadata graph
---------------------------------------------------------------
[TODO] Do not retain a full declaration footprint for every class in every file.
[TODO] Maintain a compact disk-backed index from qualified type names to the
  locations of their declaration records. Access to the index itself must be
  bounded; it must not require loading the full project index into RAM.
[TODO] Resolve a compilation unit's references through its declared lookup scope.
  Load only the declarations actually needed to resolve and compile those
  references, not all declarations from the project or an imported namespace.
[TODO] A using directive makes namespace members eligible for name lookup. It is
  not an instruction to load all classes in that namespace. In particular,
  using System must not load the entire System namespace.
[TODO] Preserve standard C# lookup for current namespaces, enclosing/nested types,
  fully qualified names, namespace/type aliases, global usings, using static,
  and extension-method candidates. Do not require artificial using directives
  for references that ordinary C# permits without them.
[TODO] Use indexed candidate lookup for operations such as extension-method and
  overload discovery instead of eagerly materializing every possible class.
[TODO] Resolve ambiguity and accessibility correctly; never choose a declaration
  merely because it happened to be loaded first.

3. Transitive dependencies and implementation bodies
---------------------------------------------------
[TODO] Load required dependency declarations on demand: base types, interfaces,
  member signatures, generic constraints, constants, and layout information.
[TODO] Keep ordinary method bodies separate from declaration records. Inspecting
  a signature must not bring the method's implementation into memory.
[TODO] Fetch generic bodies only when specialization requires them, and fetch
  cross-unit optimization bodies only when a selected optimization needs them.
[TODO] Handle cyclic references and partial types without eagerly loading all
  implementation bodies participating in the cycle.
[TODO] Preserve one consistent identity and layout for each type across units,
  including static storage, static initialization, descriptors, generic
  instantiations, inheritance, interface dispatch, and exception matching.

4. Memory ownership and internal parallelism
-------------------------------------------
[TODO] Share immutable loaded declaration records between workers rather than
  duplicating the project declaration graph per worker.
[TODO] Bound the metadata cache and release or evict unused records. Live work may
  pin required records; eviction must not invalidate references or identities.
[TODO] Use one compiler process with bounded internal workers, not a collection of
  independent compiler processes each retaining its own compiler working set.
[TODO] Bound concurrently resident unit bodies and intermediate representations.
  Worker scheduling must respect memory limits as well as available CPUs.
[TODO] Support single-worker, low-memory operation on a 486-class host. Parallelism
  must not be required for correctness or for making forward progress.
[TODO] Keep diagnostics and output deterministic across worker counts and cache
  load/eviction order. Release completed-unit and obsolete analysis state.

5. Linking, incremental work, and cross-unit optimization
--------------------------------------------------------
[TODO] Extend the existing object writer/linker; do not build a second linker.
[TODO] Link independently compiled units without duplicate runtime/type identities
  or silently accepting conflicting definitions.
[TODO] Cache unit artifacts with dependency-aware invalidation. Declaration and
  layout changes must invalidate affected consumers; unrelated implementation
  changes should not force recompiling the entire project.
[TODO] Preserve ordinary cross-file language behavior and diagnostics. An object
  link that succeeds despite incompatible unit assumptions is not acceptance.
[TODO] Implement compact summaries for whole-program decisions and selective body
  loading for cross-unit optimization, following the general ThinLTO approach.
[TODO] Do not reconstruct the complete project AST or IR during linking/LTO. Keep
  optimization working sets bounded, including when running without parallelism.

6. Evidence required for acceptance
----------------------------------
[TODO] Demonstrate a project with separately compiled, mutually referencing files,
  partial types, inheritance/interfaces, generics, and static initialization.
[TODO] Verify scoped lookup, qualified references, aliases, ambiguity/accessibility
  diagnostics, and transitive dependencies without eager namespace loading.
[TODO] Exercise cache eviction, incremental invalidation, and deterministic output
  with one worker and multiple workers.
[TODO] Build the compiler through the native project path, run the resulting
  compiler, and verify the existing self-host smoke and generation-parity gates.
[TODO] Measure peak resident memory, mapped heap, live managed data, loaded metadata,
  and phase/end-to-end time separately. Report actual completed-run peaks.
[TODO] Demonstrate bounded single-worker memory use as project size grows beyond
  the actively compiled dependency set, and verify a stated small-system memory
  budget. Do not claim 486 suitability from instruction compatibility alone.
[TODO] Merely passing a small fixture, surviving the 32-bit address-space limit, or
  moving the same whole-program memory footprint into the linker is not enough.

7. Earlier compiler requirements that remain in force
----------------------------------------------------
[RULE] Separate compilation is now first priority. It does not supersede
       all-phase internal parallelism, memory reduction, optimization quality,
       compatibility, self-hosting, or the earlier benchmark requirements.
[RULE] Default generated code must use only 486 plus x87 FPU instructions.
       Exploit that ISA without silently requiring Pentium, MMX, SSE, or later
       instructions. Audit emitted code, not merely command-line target names.
[RULE] Preserve ordinary C#/.NET language and API behavior. When implementation
       differs from the applicable standard, fix the implementation rather
       than weaken a valid test or require a nonstandard client workaround.
       Preserve applicable POSIX/Linux ABI behavior in runtime/platform code.
[RULE] Retain intended optimizations when repairing self-hosting. Improve the
       compiler so it can compile them; do not disable or remove them just to
       make a bootstrap pass.
[RULE] Allocation and GC remain top performance concerns. Avoid heap allocation
       for small buffers when a proven-safe alternative exists. Use static
       storage only where sharing is safe; otherwise use stack or reusable
       per-owner/thread storage. Preserve reentrancy, concurrency, identity,
       zero initialization, exception behavior, GC visibility and secret erasure.
[RULE] Evaluate size-oriented output before increasingly complex transformations.
       On the 486, account for instruction fetch, cache/code footprint, spills,
       memory traffic and call overhead. Balance inlining against added size;
       neither universally inline nor universally minimize size.
[RULE] Use LLVM/GCC as algorithmic references where useful, adapting legality
       conditions to this managed language and local IR. Observe applicable
       licenses. Do not import C undefined behavior, LLVM poison assumptions,
       unsafe floating-point reassociation, or instructions beyond the target.
[TODO] Deliver at least 100 distinct, substantive compiler optimizations with
       real workload relevance and correctness evidence. Existing passes,
       equivalent variants, runtime/library tuning and policy flags do not
       count as separate newly delivered compiler optimizations.
       Inventory: tests/perf/OPTIMIZATIONS.md. Candidate counts are not accepted
       completion counts; the per-entry evidence audit is still outstanding.
[TODO] Continue the requested substantial batch of roughly 40 transformations,
       with benchmark checkpoints after cohesive sub-batches. This is part of,
       not a replacement for, the 100-optimization requirement.
       Inventory and legality obligations: tests/perf/LARGE-BATCH.md.
[TODO] Enable accepted integrated optimizations by default, with explicit
       exclusions for diagnosis. Do not require per-optimization opt-in flags.
       The temporary --experimental-batch/--batch-without policy remains to be
       replaced; experimental --opt-size has not been accepted as the default.
[TODO] Reduce redundant instructions, safely reuse results, improve loops,
       bounds-check handling, register/spill traffic and x87 use, while keeping
       allocation/lifetime and arithmetic legality explicit.
[TODO] Complete remaining boxed-struct equality compatibility (reference fields,
       padding, floating NaN and signed-zero behavior), not just raw-byte equality.

8. Internal parallelism: current scope and remaining acceptance
--------------------------------------------------------------
[DONE] Existing whole-program command accepts multiple source files and source
       response lists. This is NOT bounded separate project compilation.
       Source: compiler/Driver.cs and compiler/Frontend.cs.
[DONE] Bounded backend worker pipeline with ordered output has native serial/
       four-worker PIC and non-PIC object parity over the backend fixture.
       Evidence: tests/perf/backend-workers.cor;
       build/perf-native-backend-workers-run.log.
[DONE] Read-only-data folding and scalar replacement have a native worker
       fixture covering pipeline worker propagation, 64 replacements, IR
       verification and serial/four-worker parity.
       Evidence: build/perf-native-module-pipeline-run.log.
[DONE] Native fixture checkpoint for constant-return and dead-return worker
       transformations passed serial/four-worker parity.
       Evidence: build/perf-native-return-workers-run.log. This predates the
       later dead-argument extension of tests/perf/module-workers.cor.
[DONE] Default-argument spelling no longer mutates shared parameter declarations;
       it uses binder-owned caches. Six relevant checks passed.
       Evidence: build/perf-default-isolation-proof.log.
[DONE] Inherited generic tuple-use annotations passed the focused regression.
       Evidence: build/perf-inherited-type-uses-proof.log. This does not complete
       isolation of synthesized symbols or all nested generic cases.
[TODO] Complete safe parallel binding. Body work is still serial; declaration
       container copying and stable closure names alone do not make it safe.
       Isolate synthesized tuple/array metadata and merge results deterministically.
[TODO] Complete eligible module-wide parallel work and dependency scheduling.
       Per-file parsing and per-function optimization already use tasks;
       inliner analyses run concurrently but actual inlining expansion remains
       serial. Lowering and other shared-state work still require coverage.
[TODO] Verify the later parallel dead-argument analysis/application changes in
       the expanded native module fixture and integrated pipeline.
[TODO] Complete native thread-pool sizing, memory-aware worker limits, fault
       handling, GC-under-worker-load and deterministic diagnostics acceptance.
       Use tasks/async for coordination, not as a substitute for CPU workers.
[TODO] Establish repeatable quiet end-to-end serial/parallel throughput and
       peak-memory results for the same current-source workload. No accepted
       whole-compiler speedup follows from worker count or a noisy timing pair.

9. Allocation, retention and low-memory acceptance
-------------------------------------------------
[DONE] Smaller-fragment TLS refill before heap growth passed ten GC checks at
       its checkpoint. Evidence: build/perf-fragment-refill-proof.log.
       This removed the observed 64 KiB minimum-buffer growth behavior; it
       does not prove a low-memory compiler or absence of other fragmentation.
[DONE] Free-bin tree regression was updated to remain below the dedicated-mapping
       threshold and require actual tree probes; focused check passed.
       Evidence: build/perf-fragment-tree-proof.log.
[DONE] Lazy List backing allocation passed the 10,000-empty-list memory and
       capacity regression. Evidence: build/perf-list-lazy-proof.log.
[TODO] Complete integrated collection reference-release acceptance.
       List, Queue, Stack, Dictionary and HashSet removal/clear paths were
       changed. An earlier six-test checkpoint passed
       (build/perf-collection-release-milestone.log), but the subsequent
       lazy-list collection run was 5/1 (build/perf-list-lazy-collections.log).
       The current retention fixture attempts to isolate stale conservative
       stack roots on a joined thread, but DOES NOT COMPILE:
       build/perf-collection-release-thread-scope.log reports missing standard
       Thread construction and parameterless instance Start(). Fix the missing
       API and validate the retention behavior; do not mark this requirement done.
[TODO] Verify release of consumed parser roots and obsolete binding tables
       through complete native self-compilation and project-unit lifetimes.
       ReleaseForRebind and parser-root clearing are implemented; partial-run
       live-heap reductions are not full-build acceptance.
[TODO] Finish heap-index/anchor allocation-failure coverage, concurrent allocator
       auditing, fragmentation/lock-contention reduction and bounded GC pauses.
       Existing chunk indexes, AVL free bins, page anchors and dedicated large
       mappings are foundations, not proof of a practical 486 memory footprint.
[TODO] Reduce genuinely live compiler data as well as reserved/fragmented memory.
       Use compact representations, avoid duplicate analysis and release
       temporary state. Do not merely raise heap limits or timeouts.
[TODO] Bound low-memory behavior of metadata lookup, generic specialization,
       lowering, optimization, object emission and linking, including cyclic
       project dependencies and single-worker execution.

10. Benchmarking and self-hosting
--------------------------------
[RULE] Profile crypto and inspect generated disassembly/IR before guessing at
       expensive transformations. Benchmark both crypto and general workloads.
[RULE] Preserve cryptographic known-answer checks, arithmetic/lifetime tests
       and security behavior while optimizing.
[RULE] Compare like-for-like workloads: distinguish fresh from reused crypto
       contexts, operation counts, target/host, flags, source revision and
       generated code size. Do not present different X25519 workloads as one
       continuous speedup/regression series.
[RULE] Use the compiler compiling itself as a primary real workload. Separate
       .NET bootstrap preparation from native COR-C# compilation measurements.
[TODO] Reduce SSH connection/crypto latency and terminal/shell latency with
       measurements; microbenchmark improvements alone do not complete this.
[TODO] Complete fresh native self-compilation, run the second-generation compiler,
       and satisfy smoke, output and compiler-generation parity gates.
       Harness: tests/perf/selfcompile.sh. Earlier native smoke successes and
       old suite milestones do not certify the current full self-build.
[TODO] Complete project-based self-build using separately compiled units, then
       demonstrate self-hosting inside CORSAC, not only as a Linux executable.
[TODO] Replace Python build/support scripts required by the self-hosting workflow
       with tooling that actually runs inside CORSAC, preserving functionality.
[TODO] Establish and verify an explicit small-system memory budget and actual
       486 performance. Modern-host speed, x86 instruction compatibility and
       surviving below 4 GiB do not constitute small-system acceptance.
[DONE] Standalone corc link command is implemented using the existing ELF linker
       and builds on the .NET host with zero warnings/errors.
       Evidence: compiler/ObjectLinkCommand.cs;
       build/perf-project-link-build.log.
[TODO] Validate standalone link execution, cross-unit references and diagnostics,
       then integrate it into the bounded native project build. The preceding
       DONE item is build-only, not end-to-end link/project acceptance.

11. Work and verification rules carried forward
-----------------------------------------------
[RULE] Audit source and exact generated artifacts before repeatedly testing a
       failure. If execution is needed to diagnose it, add enough instrumentation
       to capture the relevant state in a focused run.
[RULE] Run broad unit/language/compiler suites after substantial milestones,
       not after every individual optimization. Use focused regressions where
       they answer a specific question.
[RULE] Benchmark cohesive blocks often enough to identify regressions. Do not
       postpone measurement until an untraceably large batch has accumulated.
[RULE] Record immutable source/artifact identities, flags, phase boundaries,
       allocation/GC counters, correctness checks and final command status.
       A timeout is not a crash; a running process is not a completed result.
[RULE] Push coherent changes promptly, before tests when requested, then push
       corrections and report verification limits. Preserve unrelated changes.
[RULE] Keep the requirement checklist current and do not redo verified work
       without a reason. Never promote an old snapshot's passing result into
       a current all-tests-pass claim.
[RULE] Treat tests/language as the project's executable language specification,
       subject to the C#/.NET compatibility criterion in REQUIREMENTS.md;
       preserve standard behavior instead of adjusting valid expectations.
[RULE] Root TODO.txt and the optimization ledgers retain detailed outstanding
       tasks and historical evidence. This consolidated file does not erase
       earlier non-superseded requirements or declare them implicitly completed.
