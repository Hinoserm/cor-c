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

Immediate sequence: resume x86 instruction/profile work. The isolated CORSAC86
per-file process scheduling work is paused separately.

- [ ] Complete CPU/ISA profiles, assembler encodings and automatic profitable
  code generation for Pentium/MMX/K6/K6-2/K6-III/plus and 3DNow! families.
  - [x] Automatically pack proven frame-memory arithmetic, bitwise operations,
    word/dword shifts, low/high word products and signed word dot products.
  - [x] Select 3DNow rounded byte averages, rounded high-word products, exact
    word-to-float conversion and range-proven floating arithmetic/conversion.
  - [x] Preserve x87 state boundaries and prohibit implicit packed-register use
    in freestanding code without hosted state ownership.
  - [ ] Broaden patterns beyond current bounded frame regions, complete missing
    saturation/comparison/packing/reduction families, and validate source-level
    uptake. Do not substitute approximate reciprocal instructions for exact
    language division or change NaN/subnormal behavior.
- [ ] Add 386 profiles with optional 387; audit 486-only integer instructions
  and runtime atomics, and implement software floating point for no-coprocessor
  builds. Keep 486+x87 as the default.
- [ ] Support `--fpu=none` on every CPU, including software arithmetic/math
  fallbacks and an emitted-code/runtime audit for accidental FPU instructions.
- [ ] Implement Pentium FDIV detection and corrected division fallback, with
  forced-path regression coverage on non-affected Linux/QEMU hosts.
- [x] Preserve target requirements through owned objects, separate compilation and
  LTO; reject incompatible instruction/FPU selections.
  CPU contracts are required on newly compiled managed units; native project
  flags and cache identities retain CPU/FPU choices. Foreign objects without
  owned metadata cannot be certified by this contract.
- [ ] Run encoding/correctness tests on Linux/QEMU and host-supported benchmarks.
  CPU profiles and MMX/3DNow encodings are implemented; 627 encoding/profile
  checks and seven QEMU TCG execution checks pass, including x87, MMX/3DNow,
  debug registers, 386 bit operations and 486 atomics/cache instructions.
  Seven backend configurations execute generated programs; native packed
  benchmarks and real X25519/ChaCha workloads have run. Broader coverage and
  hardware-specific profitability remain unfinished; see docs/X86-OPTIMIZATION-RESULTS.md.
- [x] Match current .NET saturating float-to-integer conversions, including NaN,
  infinity, signed boundaries and the upper half of UInt64.
- [ ] Correct the existing Math.Clamp overloads' missing inverted-bound check;
  found during the saturation-pattern audit. Keep standard .NET behavior.
- [x] Validate LOCK operand restrictions with positive and rejection fixtures.
  A separate kernel task owns F00F
  mitigation; do not duplicate its implementation here.

The requested completion order is: (1) automatic per-file standard .csproj
pipeline, (2) demand-loaded default libraries, (3) complete lookup/generic
coverage, (4) broader LTO, (5) end-to-end memory and self-hosting acceptance.
  Do not treat a supporting test or narrow fixture as completion of a stage.

The current priority is again the compiler/linker interface, managed separate
compilation, bounded declaration loading and LTO. The isolated CORSAC86 build
migration has its own branch and acceptance record; do not modify the active
kernel developer's workspace. Self-hosting runs remain paused; keep pushing
documented implementation checkpoints.

Separate compilation must make self-hosting practical on small 486-class
systems as well as use modern multicore hosts effectively. The implementation
tasks below track that work; moving files alone does not reduce the working set.

## Build utility and executable separation

- [x] Accept bare `Name=Value` arguments beside nested target names, preserving
  spaces and additional equals signs. Strict manifests reject undeclared
  property names. Nineteen build-runner checks passed at 4d76278.
- [x] Integrate a production CORSAC86 manifest on an isolated branch without
  changing the active kernel developer's workspace. The OS's C# configuration
  component replaces Python schema/profile/source and syscall generation.
  `build boot-test disk=output.bin arch=486 smp=0` produced an exact 32 MiB disk
  and passed login/shell/mount/reboot acceptance. This is host .NET build support,
  not native build-utility self-hosting or migration of every legacy Python test.

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
- [x] Verify the new logical-CPU default/global worker leases, native script
  budget propagation and timestamp-based Inputs/Outputs task skipping. Standard
  .csproj incrementality stays with MSBuild; missing/changed inputs, outputs and
  options must invalidate state. Tests and undeclared side effects always run.
  Seventeen runner checks passed. A default 32-worker host build rebuilt all
  fourteen libraries; the next unchanged build rebuilt zero, preserving the
  compiler/linker/build DLL and library output timestamps and sizes. Logs:
  build/logs/20260918-123831-662d268f3da84087a98a35a51cc09216/ and
  build/logs/20260918-123920-d04b01be073e444ca862d78bd425b22c/.
- [x] Build compiler and linker as independent .NET-hosted executables and
  document ELF relocatable objects. Compiler -> `.o` -> corlink -> Linux program
  passed; this does not establish managed file-by-file compilation.
- [ ] Restore project paths, split source types, and verify the reorganized repo.
- [ ] Implement native MSBuild-compatible project evaluation and compilation;
  a host dotnet adapter alone does not satisfy this requirement.
  The owned evaluator and `corc project` coordinator are now implemented for
  an initial SDK-style profile, without invoking MSBuild. The two-file fixture
  builds and runs, and an unchanged build reports zero rebuilt units. Full
  project-profile acceptance, native library packaging, target hooks, metadata
  transforms and all project-reference semantics are still outstanding.
  Normal Compile tasks route to COR-C#; MSBuild is restricted to bootstrapping
  the build-tool component. No later stage is marked complete by this work.
- [ ] Implement verified bootstrap and atomic native toolchain activation.
- [ ] Implement source locks/fetching, artifact references, installation/image
  tasks, profiles, imports and whole-build memory/resource budgeting.
- [ ] Verify the build utility itself compiles and runs natively under COR-C#.

## Compatibility defects

- [x] Verify the current compatibility repair batch: standard ThreadStart
  construction and instance Start/Join lifecycle; stream-based pipe fixtures;
  ArgumentList boundary preservation; zero-error kernel snapshot compilation;
  interface return/ref matching and inherited dispatch. All six original failing
  fixtures passed focused acceptance after implementing missing constructor
  method-group conversion and TextReader/TextWriter.Close. At 21a8209 the
  default ten groups passed (build/logs/20260918-173351-09b8f91cc97f456a8da6adb3526566b4).
  The full language suite then passed 274/274, including new lifecycle and
  exception-dispatch fixtures (build/compatibility-language-final.log). Later
  extension/streaming changes have their own acceptance below.

- [ ] Complete interface contract matching: reject incompatible return/ref
  signatures, compare generic method parameters by position rather than spelling,
  and preserve inherited interface mappings through overrides/reimplementation.
- [x] Distinguish stack-trace reset for `throw exception;` from preservation for
  bare `throw;` and exception-dispatch/task propagation. The current checkpoint
  retains rethrow identity through GIR v9, implements ExceptionDispatchInfo,
  and routes task propagation through it. exception_dispatch and
  514_stack_traces passed; GIR round-trip coverage passed. Broader runtime
  exception/async compatibility remains subject to the complete suite.

- [x] Verify the standard Path.Combine params overload and fixed-overload null
  validation. This repairs the first post-extraction native bootstrap failure
  (five/six path segments); the implementation avoids intermediate join strings.
  The path_combine_params language fixture passed in the 271-case milestone.
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
  Queuing paths instead of all source texts and releasing declaration-record
  pins once headers are parsed passed the metadata/default milestone, including
  loading 100 declaration families through a 4 KiB serialized-record cache.
  Imported syntax and the bound unit remain separate memory costs, not included
  in the declaration cache accounting.

- [x] Verify indexed extension-method discovery and namespace-scoped candidate
  lookup (CDIX v3), including rejection of candidates in unimported namespaces.
  Metadata checks and indexed, IR-LTO and managed-unit integrations passed at
  f185352 using an isolated host toolchain (build/milestone2-*.log).
- [x] Verify non-copying LTO archive views and their allocation bound. The linker
  suite passed 118/118, including a 4 MiB payload whose summary-open allocation
  stays below 1 MiB. A measured host link saved 8,760 KiB peak RSS with identical
  output; see docs/BENCHMARKS.md. Native ELF input objects are still resident;
  this is not complete streamed object I/O.
- [x] Verify the subsequent symbol-file split and standard protected
  Stream.Dispose(bool) override path. At 9d5179d all ten default groups passed;
  the disposal override, namespace fixture and four LINQ fixtures also passed.
  Evidence: build/logs/20260918-174943-54378661ac3147a2bb18222cbcb3fcf8/ and
  build/milestone3-language-focused.log. IR summary-open allocation measured
  5,728 bytes for a 4 MiB payload in the linker unit test.
- [x] Rebuild the shared runtime/standard libraries for GIR v9 and verify
  dynamic execution after the combined compatibility changes: 37/37 shared
  checks passed (build/milestone3-libraries.log and build/milestone3-shared.log).
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

- [x] Verify the managed-image/initializer milestone at 4705796: all ten default
  groups passed; shared-library acceptance passed 35 checks; the dynamic-runtime
  stack-trace fixture additionally passed nine checks. The full language run
  remained 266 passed / 6 known failures / 272 total, with no new failure names.
  The rebuilt ABI-v3 stage2/kernel passed actual ISA-486 login/shell/reboot
  acceptance from an isolated OS snapshot. Evidence:
  build/logs/20260918-164246-f81e24b123d046b18e01d614842a2bdf/,
  build/logs/20260918-164348-a011860f583c450787f831f7af127618/,
  build/logs/20260918-162924-fa1fc56192da468e87b97c4dd55ff38a/,
  build/shared.zf5qEQ/ and build/corsac-boot.Rfs3bV/.

- [x] Add a linker-created image directory for every unit's frame/stack-map
  tables, with reserved-name/bounds validation and ABI version 3 rejection of
  stale objects. Fix cross-unit line-table lookup and preserve the throwing
  source site when runtime helpers are inlined. Executable exact-line checks
  passed with and without LTO; caller object bytes matched at one/two workers.
- [x] Certify compiler-created tuple/array-adapter descriptors for shared
  ownership while retaining rejection of ordinary duplicate strong symbols.
  The first real managed-runtime boundary caught these missing certificates;
  the corrected multi-unit executable links and runs.
- [x] Implement static auto-property initialization using the same owned,
  source-scoped helper path as fields. The multi-unit runtime fixture verifies
  two partial files' aliases, static properties, instance initialization and
  exactly one static-constructor invocation. Evidence for these focused gates:
  build/logs/20260918-162754-47be25bf8c4e4740bd269e8a828af0e1/.
  These checks do not certify full interface/generic/async separate compilation.

- [x] Verify the new indexed IR archive and persistent backend milestone:
  payload/metadata integrity, complete function/data round trips, bounded
  imports, cross-object argument inlining and constant propagation, native
  execution, disabled/budget-limited modes, and stripped final IR notes.
  Private dependency-closure imports and memory-aware concurrent backends remain.
  All nine default groups passed at f6f9107, including executable IR inlining and
  smaller native text; logs: build/logs/20260918-144752-19120d49a7614350894efb744e8a63d8/.
- [x] Verify actual CORSAC stage2/kernel split linking and a 486/ISA boot.
  The new `test/corsac-boot` target snapshots the OS's committed source, builds
  fresh static login/shell utilities, and retains exact symbols/provenance and
  serial/QEMU diagnostics. It does not modify another developer's checkout or
  saved image. This fixture is a boot gate, not full OS regression acceptance.
  Closed-image reachability fixes the oversized stage2 exposed by the initial
  image. The bounded backend passed actual boot acceptance in
  build/corsac-boot.fsaIh8. Stage2 has an explicit conventional-memory size guard.
- [ ] Verify typed interface dispatch, private stable closures/async state machines,
  and source-scoped partial initializer helpers, including new focused tests.
  Named delegate syntax was missing when the closure test used an ordinary C#
  declaration. Add parsing/indexing and executable single-cast/generic coverage;
  multicast operations and complete delegate reflection semantics remain separate.
- [x] Verify allocation-accounted IR decoding on the real kernel. Functions now
  load/optimize/emit in bounded worker batches rather than retaining the complete
  unit IR. The accepted kernel retained 604 functions and 1,998 data records:
  1,392,143 bytes resident decode accounting, peak batch allowance 57,409,789
  bytes at 32 workers. These are accounting figures, not measured total RSS.
  All nine default groups passed at 038fa5c, including byte-identical deferred
  codegen and budget rejection. Logs:
  build/logs/20260918-153841-15e9e283a5d244808a6c9cddef9ad56b/.

- [x] Verify the new disk-backed declaration-index storage milestone: bounded
  external sorting, exact/prefix lookup, partial fragments, deterministic output,
  corruption rejection and atomic publication. Source indexing and demand-loaded
  binder integration remain distinct work; storage alone does not complete them.
  Passed metadata suite at 4cc0529. Logs:
  build/logs/20260918-125148-75ea0294c16d4917a12fc849dfac1859/.
- [x] Verify source-index generation: declaration-only parsing, lexical scopes,
  partial/nested generic identities, body-independent declaration fingerprints
  and stale-source rejection. Integrate indexed lookup into the binder next.
  Source-generation checks passed at 348be5d; expanded key-checksum and bounded
  cache/pinning checks are added for the next metadata milestone.
- [ ] Verify initial binder demand loading and separate-object integration for
  namespace/alias/qualified references. Complete generic body import, partial
  ownership and indexed extension candidates before marking managed independent
  compilation complete. Default library loading is still eager.
  Initial namespace/alias/qualified integration and all eight test groups passed
  at e101c50; logs: build/logs/20260918-131514-7bedac7ab133481eaf5806dde23ea7d2/.
- [x] Verify compact project-wide interface reservations and managed layout/
  method ABI consistency contracts before extending generic and partial ownership.
  All eight groups passed at ab60933; logs:
  build/logs/20260918-133528-fce8e9d4d33c4dfe93bb8eab32ac8019/.
- [x] Verify indexed template-body imports, ordinary-body omission, namespaced
  specialization identities and consumer-owned generic method code. Multi-unit
  specialization ownership/deduplication and partial ownership still remain.
  Initial template integration passed all eight groups at 7551fcb.
- [x] Verify compiler-certified shared definitions across independent consumers,
  including different optimization choices, integrity rejection, stable ownership
  and stack-map preservation. The first implementation retains duplicate local
  bytes; physical removal and async/closure/partial ownership remain separate work.
  All eight groups passed at 6c6a9e6; logs:
  build/logs/20260918-142230-00afa03655fd49db97daffb50d82e3b0/.
- [x] Verify indexed partial-member ownership and cyclic references. Canonical
  fragment order owns the descriptor/static storage; each source unit owns its
  methods and accessors. Static-constructor helpers stay with their source unit
  while the shared initialization wrapper stays with the type owner. Runtime
  acceptance now includes per-fragment aliases, static auto-properties, two
  instance constructions and once-only static construction. This is scoped
  ownership/initialization acceptance, not every partial-type language feature.
  Initial metadata and executable partial/cycle fixtures passed at b6d107e.
  The broad language milestone was 265 passed / 6 failed / 271 total; failures
  remain the four moved OS-helper fixtures, kernel-script routing, and standard
  Thread(Action)/instance Start support. No new language failure appeared.
  Evidence: build/logs/20260918-143225-95b2895cfe584b6fb95e6c627726c9a0/.
- [ ] Extend partial-type acceptance to generic constraints, attributes,
  inheritance/interface reimplementation and partial-method edge cases.
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

- [x] Define compiler/linker/runtime ownership, indexed declaration and generic
  records, selective LTO imports, and bootloader/kernel profiles in
  [SEPARATE-COMPILATION.md](docs/SEPARATE-COMPILATION.md).
- [x] Implement compiler-certified constant-return/call-site summaries and the
  first cross-object LTO pass. Separate caller/callee objects linked with LTO
  on/off both return 42; a state-changing callee remains a call and returns 43
  through its caller. General IR importing/inlining remains outstanding.
- [x] Expose flat output and virtual/physical base controls in corlink; unit
  checks cover flat entry, BSS alignment padding and physical kernel entry.
- [x] Verify native ABI contract rejection, static-initializer preservation,
  and the compiler/linker unit suites after the initial LTO milestone.
  At b6def5e, build-runner, optimizer, assembler, x86 backend and linker suites,
  separate-link smoke and extended LTO integration all passed. Logs:
  build/logs/20260918-122219-c31c0674fb0245f7860c57d18e45e842/.
  Repeated successfully at 73b07ef with object-local stack-map boundaries and
  normal stack maps enabled in separate-object tests. That run also covers
  the expanded build-runner tests. Logs:
  build/logs/20260918-123725-7c9cce0156064c8996cd3f1caf7ba0ae/.
- [x] Link compiler-produced bare-metal caller/callee objects with an assembly
  startup object as flat output and biased physical/virtual ELF. Verify the
  convenience flat path retains --with inputs and rejects mixed ABI contracts.
  This verifies image layout/linkage, not booting CORSAC on hardware or a VM.
- [x] Run and classify the post-extraction language suite at this milestone.
  At b6def5e: 262 passed, 9 failed, 271 total. Five failures require OS-project
  helpers/kernel sources; 607_namespaces uses a stale moved path; two LINQ
  fixtures expose ignored lambda-result unification failure; collection-release
  coverage needs the missing standard Thread(Action)/Start() instance API.
  Logs: build/logs/20260918-122403-d76a02cc819a41bf8ea70255984d19ff/.
- [x] Verify the lambda-result overload rejection and corrected namespace
  fixture path. Do not rewrite valid LINQ callers to avoid overload resolution.
  All four LINQ fixtures and 607_namespaces passed after the fix. The full
  271-case suite has not been rerun; the six other classified failures remain.
- [ ] Implement the standard Thread constructor and instance Start API needed
  by optimizer_collection_release; retain its original test expectations.
- [ ] Separate OS-project integration fixtures from compiler language acceptance
  without deleting their coverage or hiding failed external prerequisites.
- [ ] Extend the existing object writer/linker; do not build a second linker.
- [ ] Link independently compiled units without duplicate runtime/type identities
  or silently accepting conflicting definitions.
- [ ] Publish an image-wide directory or registration sequence for all retained
  per-object frame/stack-map tables. Object-local boundaries avoid duplicate
  symbols but do not alone implement runtime discovery across managed units.
- [ ] Cache unit artifacts with dependency-aware invalidation. Declaration and
  layout changes must invalidate affected consumers; unrelated implementation
  changes should not force recompiling the entire project.
- [ ] Preserve ordinary cross-file language behavior and diagnostics. An object
  link that succeeds despite incompatible unit assumptions is not acceptance.
- [ ] Implement compact summaries for whole-program decisions and selective body
  loading for cross-unit optimization, following the general ThinLTO approach.
- [ ] Implement bounded object/summary I/O: the current first LTO pass receives
  already materialized ObjectFile inputs. A bounded metadata design alone does
  not establish a low-memory linker implementation.
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
