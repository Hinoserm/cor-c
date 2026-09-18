# Large optimization batch

User direction: build roughly forty substantive transformations as a larger
program of work, with benchmark checkpoints after cohesive integrated groups.
Do not wait so long that regressions become hard to attribute. No per-rule
performance claims or repeated full suites. Existing passes and variants
do not count as new delivery.
The main goal remains every TODO item, including at least 100 real compiler
optimizations; this document is the next batch, not a replacement goal.

References consulted:
- https://llvm.org/docs/Passes.html
- https://llvm.org/docs/InstCombineContributorGuide.html
- https://llvm.org/doxygen/structllvm_1_1KnownBits.html
- https://gcc.gnu.org/onlinedocs/gccint/Tree-SSA-passes.html

Adapt algorithms to local IR and managed-language contracts. Do not import
LLVM poison/no-overflow assumptions, C undefined behavior, post-486 ISA, or
unsafe floating-point reassociation.

## Inventory targets, not completed-count credit

These are forty investigation/implementation targets. Audit each against
existing code first; replace overlaps rather than claiming them again.
Known overlap: Sccp, Gvn, Dse, LICM, local scalar replacement and basic
inlining already exist. Merely enabling them earns no new-pass credit.

1. Per-bit known-zero/known-one analysis across mutable definitions.
2. Demanded-bit simplification of integer expression trees.
3. Carry-free disjoint-bit arithmetic synthesis.
4. Bit-extraction idiom recognition across masks and shifts.
5. Bit-field insertion simplification.
6. Shift-pair sign-extension recognition.
7. Boolean-mask selection simplification.
8. Signed/unsigned comparison reduction using proven sign information.
9. Branch-edge relational facts for dominated comparisons.
10. Redundant bounds checks using proven ranges.
11. Correlated switch-case propagation.
12. Predicate-driven jump threading.
13. Hoisting common non-trapping expressions from branch arms.
14. Merging identical control-flow tails.
15. Clustering dense/sparse switch ranges with a size cost model.
16. Branch layout guided by loop/error-path structure.
17. Induction-variable strength reduction.
18. Elimination of redundant derived induction variables.
19. Deletion of proven finite, unobservable loops.
20. Closed-form final induction values.
21. Counted zero/fill loop recognition.
22. Counted copy loop recognition with overlap proof.
23. Loop-invariant memory promotion with alias/effect proof.
24. Sinking loop stores when intermediate writes are unobservable.
25. Width-aware partial store-to-load forwarding beyond existing GVN.
26. Eliminating stores that write an unchanged loaded value.
27. Trimming partially overwritten stores beyond existing DSE.
28. Folding initialized stores into preceding fills.
29. Merging adjacent ordinary stores with alignment/effect guards.
30. Removing overwritten initialization ranges of private objects.
31. Promoting aggregate arguments across direct call boundaries.
32. Folding proven immutable initialized object fields.
33. Removing dead internal function arguments and updating all calls.
34. Removing unused internal return values.
35. Propagating constant returns without body duplication.
36. Folding equivalent internal function bodies.
37. Eliminating eligible self-tail recursion.
38. Shrink-wrapping callee-save register work.
39. Rematerializing cheap arithmetic rather than spilling its result.
40. Reusing stack object slots with non-overlapping lifetimes.

## Current implementation state

IntegerBitFacts.cs is the first foundation: bounded fixed-point, all-definition
bit facts for I32/I64; parameters remain unknown. It includes modular carry
propagation, bitwise operations, constant shifts, width conversions, unsigned
loads and bounded product bits. It does not move memory or transform code,
and is not yet connected to the default pipeline. Analysis cases are not
separate optimization-count entries. Dependent rewrites and the remainder
of the batch are outstanding; no claim of forty implemented transforms.

BitFactSimplify.cs now stages the first consumers: constant conclusions from
bit facts, sign-proven logical shifts/zero extension, disjoint-bit arithmetic,
known-bit mask absorption, and signed/unsigned range comparison folding.
Loads and possible faults are not replaced. Neither class is wired into the
production pipeline yet; the batch's proof tests/integration remain to do.
Build-only checkpoints are permitted while developing; integrated groups
receive benchmark checkpoints without resuming a per-rule full-suite loop.

CallBoundarySimplify.cs stages three module transformations: dead arguments,
unused integer returns and constant-return propagation. It uses a closed
executable call graph, preserves address-visible/shared-library ABIs, and
keeps side effects and throwing calls. Return-type mutation is internal to
the IR so a proven unused return can become void consistently.

CommonTailMerge.cs stages exact duplicate-block and suffix sharing within a
function, introducing jumps rather than calls. It compares register identity,
operand types, memory widths, targets and source lines, excludes phi/EH/
indirect-entry functions, and bounds matching work. It does not claim machine
size profitability from the IR instruction count alone. These passes remain
outside the production pipeline until batch integration and validation.

## Validation and measurement gate

Preserve pre-batch compilers, sources and library sets. Compare matching
iteration counts, or report normalized time per operation. Record code size,
hot call sites, spill traffic, collections, compile cost and known answers.
Assess both default and size-oriented policies; do not select solely on
modern-host timing. Batch correctness covers ordinary C# integer overflow,
exceptions, mutation, aliasing, EH/re-entry, GC lifetime and 486 codegen.
Actual 486 SSH/shell latency remains a separate acceptance requirement.

### First integrated checkpoint

The staged bit-fact, edge-predicate, call-boundary and common-tail passes are
now connected only behind --experimental-batch, with per-pass IR verification.
Earlier staging descriptions above describe their development order; default
compilation still does not enable them. Grouped proof tests remain outstanding.

Preserved compiler: build/compiler-batch-checkpoint1-verified/bin/corc/release/corc.
Control and candidate use the same compiler, 486 target and
build/perf-libs-immutable-metadata libraries; only the experimental flag differs.
Three alternating quiet pairs are in build/perf-large-checkpoint1-results.log.
An earlier run overlapping compilation is retained separately as
build/perf-large-checkpoint1-concurrent-diagnostic.log, not gain evidence.

Fresh X25519, 2,000 operations: control 1.0187-1.0265 s, candidate
1.0231-1.0639 s. Every pair was slower (about 0.4-4%); investigate before
expanding the integrated group. SHA, HMAC and ChaCha timings were mixed,
not evidence of a dependable speedup. All crypto known answers passed and
reported zero collections. This is not broad correctness acceptance.

Code bytes, control -> candidate: crypto 64,099 -> 64,055;
ChaCha 22,333 -> 22,838; allocation 39,917 -> 40,221. Inspect inlining and
layout effects rather than accepting IR shrinkage as machine-size improvement.
All nine allocation cases also passed a single diagnostic run, recorded in
build/perf-large-checkpoint1-allocation-results.log; short single samples are
not accepted performance gains. No new batch transform is accepted by this
checkpoint alone, and production defaults remain unchanged.

### Pass isolation checkpoint

Compiler build/compiler-batch-ablation adds --batch-without <pass> for one
named experimental pass, rejecting non-batch use and unknown pass names.
The build passed without warnings; six ChaCha omission variants and three
X25519 omission variants compiled with the per-pass verifier enabled.

ChaCha code bytes with one pass omitted: bit-fact-simplify 22,401;
edge-predicates 22,838; common-tail-merge 22,794; constant-returns,
dead-returns and dead-arguments each 22,838. Thus bit-fact simplification
accounts for most of the reproducible code growth in this pipeline context.
Without it both 125-byte ReadLittleWord helpers remain; with it they vanish
as separate symbols and _start grows from 0x54d5 to 0x578a bytes. Trace the
inlining decision before changing policy; this is not proof of faulty bit facts.

Three quiet trials at 4,000 fresh X25519 operations are recorded in
build/perf-batch-ablation-x25519.log. Control took 2.0598/2.1468/2.0598 s;
full batch 2.0858/2.0976/2.0599 s. Unlike the earlier checkpoint, the paired
direction is mixed, so a reliable runtime regression is not established.
Omitting bit facts took 2.0797/2.0649/2.0564 s; omitting tail merge took
2.1123/2.0715/2.1268 s; omitting dead arguments took 2.1061/2.0964/2.0662 s.
Every result was valid with zero collections. Do not choose a default from
these small noisy differences. The deterministic size/inlining change is
the next investigation target; broad proof tests remain outstanding.

### Inlining threshold traced; blanket branch penalty rejected

--trace-opt now includes inliner profitability decisions for the selected
caller or callee. ReadLittleWord was 42 IR instructions before bit facts,
38 afterward: four redundant masks removed crossed the fixed limit of 40.
Logs: build/perf-chacha-trace-control.log and perf-chacha-trace-candidate.log.

A trial charged two extra units per conditional branch for ordinary small
body eligibility, preserving single-site, constant-branch and fresh-owner
opportunities. Compiler build/compiler-branch-cost built successfully.
Crypto code fell 64,055 -> 60,951 bytes; ChaCha 22,838 -> 22,725 bytes.
However, three quiet paired runs in build/perf-branch-cost-comparison.log
showed ChaCha 319.5/331.9/320.9 ms -> 328.6/353.5/328.0 ms per million
blocks. Fresh X25519 at 4,000 operations was also slightly slower in every
pair (2.0678/2.0718/2.1173 -> 2.0741/2.0751/2.1611 s). All answers valid,
zero collections. The blanket penalty is DISABLED in all pipeline defaults;
the configurable cost and decision diagnostics remain for controlled work.
This policy experiment is not an additional accepted optimization.

### Managed-semantics proof checkpoint

compiler/tests/opt/BitFactsTests.cs now checks 24,206 input/result cases
before and after bit simplification, also checking that inferred known bits
contain every observed result. Coverage includes I32/I64 arithmetic,
signed/unsigned comparisons, modular extremes, masks and shift counts at
and beyond each width. Both pre-pass and post-pass IR are verified.
An initial test fixture used an invalid I64 shift-count operand; the fixture
was corrected to the IR's required I32 count, with pre-pass verification
added. No product behavior was changed to accommodate the fixture.

Another 25,600 input/result cases exercise edge predicates across true/false
arms, operand reversal, signed/unsigned ordering, equality and operand
reassignment. The grouped --bit-facts-only run passed all three checks;
log: build/perf-bit-facts-proof.log. A separate barrier check confirms
parameter entry values remain unknown after constant reassignment and a
register-addressed load survives elimination of its constant consumer.

These are bounded differential checks, not exhaustive correctness proofs.
Loop/EH joins, interval predicates with differing constants, call-boundary
effects and common-tail transformations still need dedicated validation.
No production optimization changed in this checkpoint, so the preceding
runtime comparison remains the relevant benchmark; no new gain is claimed.

### Private frame write-back elimination

StoreBackElimination.cs implements the first target-26 scope: a same-block
load followed by an unchanged store to the exact same private frame region.
Only unescaped, in-bounds slots are eligible; register reassignment, overlap,
unknown writes, calls and synchronization invalidate remembered values.
The pass never deletes the original load. Pointer/static/device-memory
write-backs remain outside this implementation, preserving write faults and
observable I/O. It is experimental-only and supports --batch-without store-back.

Compiler build/compiler-store-back passed with no warnings. The focused
28-case width/barrier group passed (build/perf-store-back-proof.log).
The freshly compiled crypto ELF is byte-for-byte identical to the previous
full-batch candidate, at 64,055 code bytes: no crypto benefit from this pass.
Two 2,000-operation X25519 checks passed with zero collections, at 1.0895
and 1.0391 s (build/perf-store-back-crypto-results.log). Identical binaries
mean timing variation here is not evidence of an optimization effect.
Real workload profitability beyond crypto remains unproven; do not count
this as an accepted performance optimization yet.

### Non-SSA ordinary load reuse

Disassembly of field Multiply showed repeated limb-array pointer and length
loads between bounds checks. Existing GVN assumes SSA and cannot simply be
enabled on this mutable-register pipeline. LoadReuse.cs now tracks exact
ordinary reads along single-predecessor dominated paths and checks captured
address/result validity with Defs.CanForward. Joins, loop headers, writes,
calls and synchronization discard memory facts; first reads remain in place.
It is experimental-only and selectable with --batch-without load-reuse.
This adapts an existing optimization capability to the non-SSA pipeline;
do not count it as a wholly new algorithm merely because it is a new file.

Build/compiler-load-reuse passed after correcting an initial Jump opcode
spelling error. Eight focused mutation/width/signedness/effect cases passed
(build/perf-load-reuse-proof.log). Crypto code 64,055 -> 63,924 bytes;
Multiply 0x1694 -> 0x166d bytes. Three quiet pairs are preserved in
build/perf-load-reuse-comparison.log. X25519, 4,000 fresh operations:
2.0765/2.0647/2.0599 -> 2.0830/2.1305/2.0901 s (slower in all pairs).
SHA-short, 100,000: 58.88/58.88/60.72 -> 57.63/57.72/56.81 ms;
fresh HMAC timings mixed. Every known answer passed with zero collections.
Keep experimental; inspect live-range/spill effects before acceptance.
Cross-block joins, loops and exception-entry proof coverage remains open.

### Entry-block refinement and milestone 13

Static disassembly counts for Multiply showed 683 -> 678 EBP-relative
references after initial load reuse, with 13 calls and 62 multiply mnemonic
matches unchanged. These are static references, not dynamic spill counts;
the proposed increased-spill explanation is not supported by this check.

LoadReuse now allows a unique definition in the sole ordinary function entry
to cross blocks only if the entry has no predecessors. The existing Defs
landing-pad restriction remains unchanged globally. Added tests distinguish
ordinary entry from an entry with a backedge; all ten load-reuse cases pass.
Compiler build/compiler-entry-reuse builds successfully and generates
63,824 crypto code bytes, versus 64,055 before load reuse; Multiply is
0x1667 bytes. Three quiet 4,000-operation X25519 pairs in
build/perf-entry-reuse-comparison.log are mixed: 2.0555/2.1402/2.0984 s
control, 2.0775/2.1144/2.1106 s candidate. All valid, zero collections;
no clear throughput gain established.

Milestone 13 started with the preserved compiler-entry-reuse snapshot:
full language runner --experimental-batch, optimizer and x86 checks.
Logs: build/perf-batch13-language.log, perf-batch13-opt.log,
perf-batch13-x86.log. Results are pending, not an acceptance claim.
The language runner now forwards --experimental-batch instead of treating
it as a filename filter. No benchmark runs overlap these milestone builds.

Milestone 13 partial result: optimizer suite completed, 62 passed / 0 failed;
x86 backend checks completed with ALL CHECKS PASSED. The language suite is
still running; the integrated batch is not yet language-suite accepted.

### Exposing promoted frame addresses

FrameAddressFold.cs resolves unique, stable pointer definitions through
copies, 32-bit pointer width conversions and bounded constant offsets, then
rewrites in-bounds loads/stores to explicit frame-slot operands. MemSet can
use the explicit slot only at offset zero with an in-bounds constant count.
Reads/writes stay at their original execution points. Async, multiple CFG
roots, mutable pointers, out-of-bounds accesses and non-32-bit targets stay
conservative. This enables memory passes to see stack-promoted objects
instead of treating all their derived addresses as unknown pointers.

An initial build caught an attempted mutation of immutable Instr.Offset;
the pass now constructs a replacement instruction with the original width,
signedness, destination and source line. Preserved compiler-frame-address
build succeeds with zero warnings. All 14 offset/reassignment cases pass
(build/perf-frame-address-proof.log). Crypto code shrinks 63,824 -> 60,856
bytes (about 4.6%). Two-operation X25519/SHA/HMAC known-answer smoke checks
pass, log build/perf-frame-address-smoke.log. Their timings are NOT benchmark
evidence: milestone 13's older language snapshot is still running. Quiet
paired timing and broad acceptance of this newer pass remain outstanding.

### Milestone boxing fixes

The preserved language run exposed invalid IR BEFORE optimization in two
generated helpers: F64 operands sent to integer Eq, and boxed-struct pointer
arithmetic mixing I32 and I64. Lowering.Box.cs now emits floating equality
with the required both-NaN case, and uses the target pointer width for struct
byte traversal. The verifier remains strict. Source authority for floating
Equals: https://learn.microsoft.com/en-us/dotnet/api/system.double.equals.

Compiler compiler-box-float-fix2 and compiler/tests/boxing-float-equality.cor
cover distinct NaN payloads, signed zeroes, finite values, infinities, null,
Single NaNs and mixed boxed types. The source-built executable passes
(build/perf-box-float-source.log); 505_boxing and 509_boxing_structs also pass
with this compiler (build/perf-boxing-rerun.log).

Initial dynamic runs used the preserved pre-fix shared libraries, which
export the older boxed-double helper and failed even self-equality. The
same instrumented source passes when built wholly from current sources.
Those old-library results are not a verdict on the newly generated helper;
shared libraries must be rebuilt before deployment. Raw boxed-struct byte
equality's broader semantic limitations are now explicit in TODO.txt.

### Completed milestone investigation and quiet frame-address benchmark

The preserved milestone 13 language process finished: 265 passed, 7 failed,
272 total. All failures were the two generated-boxing IR defects described
above. All seven affected tests pass targeted reruns with compiler-box-float-fix2:
505/509 in perf-boxing-rerun.log; 419/510/517/519/520 in
perf-boxing-affected-rerun.log. This resolves the reported defects but is not
a full green language run of the newer snapshot. Optimizer 62/0 and x86
checks passed on the earlier group, as previously recorded.

After all build/test processes finished, three paired frame-address runs
were recorded in build/perf-frame-address-comparison.log, identical benchmark
sources and preserved libraries. Fresh X25519, 4,000 operations:
control 2.1070/2.0747/2.0914 s; frame-address 1.9025/1.8610/1.8840 s.
This is roughly 9.7-10.3% less elapsed time in every pair, alongside the
4.6% crypto code reduction. At 100,000 hashes, SHA-short was
57.08/61.59/57.09 -> 55.08/55.28/55.23 ms. HMAC timings were mixed.
Every result was valid with zero collections. These are modern-host results,
not real-486 latency acceptance; all new passes remain experimental.

### Boxed floating hash contract completed

The equality fix requires matching hash semantics: the old helper returned
raw payload bits, distinguishing equal NaN payloads and Single signed zeroes.
Lowering.Keys.cs now canonicalizes NaNs/zeroes when generating boxed float
hashes and combines both halves for ordinary doubles. This satisfies equal
objects -> equal hashes; no cross-runtime numeric hash-value guarantee is
claimed (https://learn.microsoft.com/en-us/dotnet/api/system.object.gethashcode).

Preserved compiler-box-hash-fix builds without warnings. The fully source-built
regression passes direct equality/hash and Dictionary<object,int> lookup for
equivalent floating keys: build/perf-box-hash-proof.log. Now that milestone
13 is terminal, the fixture has moved from compiler/tests into the normal
language suite as tests/lang/optimizer_boxed_float_equality.cor. This is a
correctness fix, not another counted optimization or crypto speedup.

### Wider frame-folding checkpoint and allocation telemetry

Same compiler-box-hash-fix, same preserved libraries; control uses
--experimental-batch --batch-without frame-address-fold, candidate enables
the full group. Code bytes: ChaCha 22,740 -> 21,568; allocation 40,245 ->
37,921. Three paired million-iteration runs in
build/perf-frame-other-comparison.log pass all answers. ChaCha timings are
mixed: 461.1/443.5/449.5 -> 409.5/479.4/409.8 ms. Retained buffers are
slower in every pair: 106.2/99.7/99.6 -> 115.6/109.5/113.8 ms, with the
same 366 collections. Retained owners have 367 collections on both sides.
Do not promote the pass on the X25519 result alone.

allocation.cor now reports mark calls, interior searches, probe/sweep/rescan
work and last-collection live bytes/blocks, all sampled outside timing.
A matching pair of instrumented builds/runs is preserved in
build/perf-frame-allocation-telemetry.log. Retained-buffer control/candidate:
3,262,524/3,263,622 mark calls, identical 8,788,250 interior search steps,
identical 1,109,712 sweep visits, last live blocks 291/292. This small
difference does not support a large GC-work explanation. Instrumentation
changes program layout, so its timings are not compared against the earlier
uninstrumented binaries. Layout and generated loop code remain to inspect.
Frame folding is recorded as candidate 032, not a fully accepted optimization.

The earlier size-only milestone has finished: 272 language tests, 55 optimizer
checks and x86 checks passed with its preserved compiler-size-owned snapshot.
It does not validate this new batch or later call-aware policy changes.

### Native selfcompile GC chunk lookup

The native compiler from snapshot 4d2b9eb5 builds/runs the smoke program.
Its full selfcompile is retained under build/selfcompile.tA0sum. A single
10-second, 99 Hz cpu-clock profile captured 990 samples without losses:
93.64% self time in HeapChunks.Of, 5.86% in Gc.ScanRange. The stack was GC
root scanning triggered by TypeSymbol.FindMethods during binding. These are
sample-window percentages, not whole-compilation percentages or a speedup.
Artifacts: native-profile.data and native-profile-report.txt in that directory.
The profiled run is diagnostic, not a quiet benchmark baseline.

HeapChunks now maintains a sorted raw address index (four bytes per chunk,
initial one-page metadata mapping) and binary-searches it after the existing
memo/envelope tests. Updates accompany chunk insertion/removal; used-extent
checks still reject headers and unused tails. Metadata mapping failure drops
the incomplete index and uses the authoritative list. Metadata never allocates
managed objects or recursively invokes collection. This is a runtime algorithm
improvement, not another counted compiler transformation.

Focused index growth/boundary/removal/reuse regression and the existing churn,
live-list, shared-root and interior-pointer cases pass: 5/0, recorded in
build/perf-gc-index-milestone.log. Allocation-failure fallback coverage, threaded
collection coverage and a fresh native selfcompile performance comparison
remain outstanding. Do not infer an end-to-end speedup from the profile alone.

The old-collector diagnostic selfcompile was deliberately terminated with
SIGTERM after 799.27 seconds (harness status 143, peak RSS 1,631,580 KiB).
It neither completed nor hit its timeout. Its profile justified moving to the
indexed collector; retain its files, but do not treat that elapsed time as a
completed baseline. The replacement snapshot is build/selfcompile.C2MjYc.

### Bounded backend workers: initial integration

X86Backend accepts Workers=1..64. Worker tasks select instructions, apply
PIC/import rewriting and allocate registers for disjoint functions. Windows
hold at most four functions per requested worker; diagnostics and machine IR
are merged and released in module order, with serial encoding and linking.
The driver's --jobs control defaults to one during integration. This is a
concurrency control, not a separate flag for an optimization.

The .NET-hosted compiler builds cleanly and --jobs 1/4 produce byte-identical
source-linked crypto executables (101,931 code bytes, 202,468 file bytes).
The four-worker output passes 10 fresh X25519 known-answer iterations with
zero timed collections. Evidence: build/perf-backend-workers-build.log and
build/perf-backend-workers-parity.log. This proves neither native parallel
execution nor a throughput gain. Native pool startup, CPU-aware defaults,
threaded GC, failure paths and serial/parallel selfcompile parity are pending.

### Inliner analysis allocation checkpoint

A second 10-second profile, of the indexed native compiler's optimization
phase, captured 988 samples without losses. It shows GC/allocation work under
Inline.Run, including repeated definition/CFG tables. Files are retained as
build/selfcompile.C2MjYc/optimise-profile.data and optimise-profile-report.txt.
The earlier binding sample and this optimization sample are different phases;
their percentages must not be treated as a paired end-to-end speed comparison.

Fresh-owner queries now share Defs until Expand invalidates the caller, and
constant-branch queries allocate their dependency set only when a constant
argument exists. With identical current libraries, pre/post compiler builds
produce byte-identical crypto executables (101,499 code bytes, 199,844 total).
Known-answer validation passes. Ten selected optimizer checks, including
fresh-owner eligibility/growth guards, pass. Logs: perf-inliner-cache-build.log,
perf-inliner-cache-parity.log and perf-inliner-cache-proof.log under build/.
Native compiler throughput and current-source selfcompile remain unverified.
