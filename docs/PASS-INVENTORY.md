# Compiler optimization inventory

Source snapshot: 81505fb1, audited against Driver.Optimise,
Lang/Opt/Pass.cs and Lang/X86/X86Backend.cs. This distinguishes existing
machinery from work introduced by the performance program; it is not a
list of 100 newly completed optimizations.

## Default compilation path

Driver.Optimise constructs Pipeline.Default without adding further passes.
Per-function rounds (three by default): ConstantFold, Narrowing,
ConstantAndCopyPropagation, LocalCopies, WideProductSharing,
IntegerReassociate, IntegerValueReuse, CarryRecognition, DivRemReuse,
SignedPowerOfTwo, ByteSwapCalls, Peephole, ConstantFold,
DeadCodeElimination, LoopInvariant, BranchSimplify.

Module stage: DeadStatics, ConstantSpecialize, Inline (keeping the free
helper), ReadOnlyFold. The per-function rounds then run again.

Late stage: Inline (keeping the free helper), ScalarObjects, Escape, Inline,
followed by another set of
per-function rounds. After instruction selection the x86 backend runs
Allocator.Run and the machine-level Peephole.Run.

## Existing versus newly introduced

| Machinery | Behavior visible in source | Status for counting |
| --- | --- | --- |
| ConstantFold | Integer constants and branch/switch decisions | Pre-existing |
| Narrowing | Redundant width conversions; conservative single-definition reasoning | Pre-existing |
| ConstantAndCopyPropagation | Constant/copy forwarding with definition and lifetime guards | Pre-existing |
| IR Peephole | Integer identities, power-of-two arithmetic, comparison simplification | Pre-existing |
| DeadCodeElimination | Unused results and unreachable computation | Pre-existing |
| BranchSimplify | Branch threading, block cleanup, phi maintenance | Pre-existing |
| DeadStatics | Remove unread non-address-taken static storage | Pre-existing |
| Inline | Bounded and single-caller expansion, recursive/EH/address guards | Pre-existing; constant-branch heuristic extended, not credited alone |
| ReadOnlyFold | Known read-only data loads excluding relocations | Pre-existing |
| Escape | Non-escaping stack promotion; explicit ownership/free for eligible dynamic sizes | Pre-existing |
| Linear scan allocator | Register assignment, spill slots, rematerialization | Pre-existing |
| Machine peephole | Adjacent store/load forwarding, dead definitions, copies, zeroing and jumps | Pre-existing; private nonadjacent spill forwarding is candidate 011 |
| Wide integer selection | New known-upper-half and constant selection paths | Candidates 001–008; details in OPTIMIZATIONS.md |
| ConstantSpecialize | Bounded literal-call-context clones | Candidate 009 |
| LoopInvariant | Non-trapping integer motion with loop/preheader guards | Candidate 010 |
| ScalarObjects | Field-local values with dominance/lifetime/escape checks | Candidate 012 |

## Implemented but not in the default pipeline

SsaOptimise constructs SSA, runs Sccp, SSA copy propagation and peepholes,
Gvn, Dse, further cleanup, then OutOfSsa and BranchSimplify. Searches of
compiler source also find an explicit experimentalSsa branch in
Driver.Optimise. It is enabled only by --experimental-ssa, not by the default
pipeline. Therefore its tests passing does not mean normal compiled programs
receive those transformations. Enabling existing passes is not new-pass credit.

Gvn combines dominating pure-expression reuse with memory-load forwarding;
memory facts survive only selected single-predecessor edges and are cleared
at joins/calls/unknown effects. Dse removes covered stores within blocks with
conservative alias barriers. These are existing implementations, not new
optimizations to claim merely by connecting the pipeline.

Continued integration investigation: benchmark isolated SSA-enabled builds with
verified round trips and exception/indirect-entry/loop cases. Audit memory
ordering and device access before making memory-transforming passes default.
Do not assume the lack of default wiring proves these passes are safe or
that a modern-host improvement guarantees a real-486 improvement.

Remaining inventory depth: individual lowering idioms, x87 stack traffic,
bounds-check control flow, and allocator profitability need emitted-code
audits. The parent TODO inventory requirement remains open for those.

## Isolated SSA integration experiment

`--experimental-ssa` now runs the existing SSA wrapper once after the normal
pipeline, with verification before entry and after each inner stage. It is
off by default. `PERF_SSA=1` in tests/perf/build.sh selects it; use a separate
compiler artifacts/output directory while a milestone suite runs.

The first allocation benchmark compile found invalid IR already produced by
the normal pipeline, not evidence of an SSA rename failure: Escape.Own used
ZExt32 for an I64 allocator result stored in an I32 ownership slot. Corrected
to Trunc64 for a 32-bit machine word in 3e9b449f (retaining widening for the
opposite width conversion). The running batch-two suite uses the earlier
preserved executable, so it does not validate that correction.

The next verified attempt stops before SSA at Console.WriteLine, storing an
I32 value with store.u8 into Runtime._atExit. This width mismatch remains to
be traced through lowering and the ordinary passes. Do not disable the
verifier, silently exclude the failing function, or claim SSA performance
results: no experimental benchmark executable has passed compilation yet.
Evidence: build/perf-ssa-bench-build.log.

Resolved the second mismatch in 8e1da7df: the method-reference path for
Sys.AddressOf returned a word-sized address despite the API's long result.
It now widens just as the numeric intrinsic path does. All three experimental
benchmark builds now pass verification before SSA and after every SSA stage.
The earlier failure above describes the investigation sequence, not the
current build result.

First same-source comparison (build/perf-ssa-comparison.log), SSA off/on:
X25519 2,000 operations 1.179130525/1.078952409 s; fresh HMAC 10,000 operations
63.534339/69.232712 ms; invariant loop 4.769061/3.822719 ms. All checks pass;
fresh HMAC collections remain 51 and retained-buffer collections remain 73.
Small local-buffer timing falls, branch-object timing rises. These single
samples show mixed effects, not sufficient evidence to enable SSA by default.
Broader runtime/memory-ordering/exception validation and repeated timings are
still required. No new optimization count is awarded for existing SSA passes.
