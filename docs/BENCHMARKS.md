# Compiler performance baselines

This guide contains workload instructions and historical observations carried
forward from the OS repository. Recorded timings, candidate counts, source
paths and local artifact names refer to their stated checkpoints; they are not
fresh measurements of this checkout. Executable fixtures live in
`tests/benchmarks/`. The SSH-specific crypto benchmarks still require their
source ownership and standalone build paths to be repaired after extraction.

`allocation-workers.cor` isolates shared-heap contention: each worker leaves
an unrelated small size-class block free, then allocates, checks, writes and
frees varying 1 KiB buffers. Arguments are iterations per worker and worker
count (defaults 200000 and 4). It uses the actual raw runtime allocator so
escape elimination cannot remove the workload. Timing includes thread start
and join; correctness checks and timed collection counts are reported.
Compile it with a preserved compiler and matching runtime sources when
comparing allocator changes. This is an allocator microbenchmark, not evidence
of an end-to-end compiler speedup.

The build entry point is `bash tests/benchmarks/build.sh`. Set `CORC` to a preserved compiler,
`OUT` to a separate output directory, and `PERF_LIBDIR` to matching shared
libraries. The script explicitly sets `CORC_LIB`: relocated compilers must
not accidentally discover the partial source/header directory in build/lib.

Run with `LD_LIBRARY_PATH` pointing to the same shared-library directory:

For the experimental size-oriented build, set `PERF_SIZE=1` and a separate
`OUT` when invoking the build script. The underlying compiler switch is
`--opt-size`. It restricts ordinary inlining while retaining a separate,
bounded opportunity to expose owned allocations. It does not change the
486 target or replace correctness checks. It is not yet the default.

```
LD_LIBRARY_PATH="$PWD/build/lib" build/perf/allocation 200000
LD_LIBRARY_PATH="$PWD/build/lib" build/perf/crypto 2000 reuse
LD_LIBRARY_PATH="$PWD/build/lib" build/perf/crypto 2000 fresh
```

Crypto uses the actual SSH X25519 implementation and checks every result
against the first RFC 7748 function vector. Reuse and fresh modes separate
arithmetic costs from workspace allocation. These are initial workloads,
not the complete crypto/general benchmark suite requested by the user.

Crypto output includes `ticks-per-operation` alongside the original elapsed
ticks, iteration count and frequency. The per-operation field is integer
division (rounded down by less than one tick); with the current 1 GHz
Stopwatch frequency it is nanoseconds per operation. Always compare matching
modes and normalize iteration counts. The original X25519 baseline used
2,000 operations, while several later runs use 4,000; total elapsed seconds
alone are not comparable. Keep configuration labels with every comparison.

`build/perf/chacha 1000000 fresh` exercises the actual ChaCha20 block API;
`build/perf/chacha 1000000 reuse` uses its reusable context. Set the same
LD_LIBRARY_PATH as above. Every output block is checked against RFC 8439
section 2.3.2 (https://www.rfc-editor.org/rfc/rfc8439#section-2.3.2).
Inputs deliberately repeat for known-answer profiling, not production
encryption. Counters report bytes, elapsed ticks and timed collections;
validation cost is included. This is a block workload, not full SSH packet
encryption/Poly1305 or varying-message-size coverage. A 100,000-block sample
was too short for useful attribution; one million at 999 Hz gave 360 samples
with no loss. Preserve profile duration and rate when comparing results.

For profiling, run `perf record -e cpu-clock:u -F 199 -g -- COMMAND`, then
`perf report --stdio --no-children`. Disassembly and symbol sizes are emitted
by the build script. Keep compiler, sources, libraries, machine, input and
profile configuration identical when comparing transformations. Repeat
measurements before claiming a speedup; one sample is not a confidence bound.

## Initial observations, before optimizer changes

Preserved compiler: build/compiler-baseline.AwyuG4I5/corc.
Compiler DLL SHA256:
ba245627038d60ab05efe46ba18f40a72e2628a60c283c9856c2174a63eec40e.
Profile artifacts are in that directory's perf/ subdirectory.

- X25519 reuse: 2,000 valid operations, 1.713139104 seconds, zero collections.
  Of 341 samples, 43.70% are in Field25519.Multiply, 27.27% in AddProduct19,
  and 15.84% in CarryRound. No lost samples.
- X25519 fresh: 2,000 valid operations, 1.732990311 seconds, 23 collections.
  Of 344 samples, the same three routines account for 41.28%, 30.81% and
  16.57%. This single pair does not establish a statistically reliable delta.
- SSH's Field25519.Multiply symbol is 12,793 bytes. Disassembly shows
  repeated limb-array loads/checks and extensive stack spill traffic.
- The allocation workload verifies checksums and zero initialization. The
  shared buffer helper currently allocates in both local and retained modes;
  the local object workload already avoids collections. Existing optimization
  successes are not counted as newly delivered transformations.

## Optimization ledger policy

Target: at least 100 new substantive optimizations. There are 31 implemented
candidates; final per-entry acceptance is not yet audited. Eleven grouped
milestones have passed at the snapshots listed in the ledger.
The numbered implementation and validation ledger is [OPTIMIZATIONS.md](OPTIMIZATIONS.md).
Each entry must identify a distinct transformation, legality conditions,
implementation, correctness coverage, generated-code change and measured
impact. Baseline construction and application-only tuning do not increment
the compiler optimization count. Allocation/GC improvements have priority,
but sampled arithmetic bottlenecks must not be ignored.

### Candidate C001: bounded constant-branch inlining

Implemented in Inline.cs with existing recursion, exception, address-taken
and growth guards preserved. Dedicated IR tests cover constant versus runtime
arguments, growth-limit rejection and interpreted results. Optimizer suite:
40 passed, 0 failed (build/perf-constant-branch-opt-tests.log).

Not credited toward the 100: both current benchmark executables are byte-for-
byte identical to baseline. Allocation counts are unchanged. Single-run
timing differences are noise and must not be reported as an improvement.
Further work must target transformations that change profiled emitted code.

## Reference techniques being evaluated

- LLVM's pass catalog: https://llvm.org/docs/Passes.html
- GCC optimization options: https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html

Relevant directions are scalar replacement and interprocedural argument
specialization for small managed allocations, known-bit propagation for
wide integer arithmetic, loop-invariant motion, value numbering, and
redundant-check elimination. Adaptation must preserve managed bounds/null
exceptions, object identity, GC roots, checked arithmetic, aliasing and
486/x87 legality. Studying or enabling an existing pass does not itself
count as a newly delivered optimization.

### HMAC allocation baseline

The crypto executable also accepts `hmac-fresh` and `hmac-reuse`, checking
RFC 4231 case 1 after every operation. Both use the actual streaming HMAC
implementation. Fresh mode includes construction and Close; reused mode
constructs before timing, then performs Start/Update/Finish each iteration
and closes after timing. Output and input buffers are shared across samples.

Initial 10,000-operation observations with the spill candidate compiler:
fresh 63.130996 ms, 51 collections; reuse 34.913409 ms, zero collections.
Both valid (build/perf-hmac-baseline.log). These are single observations,
not a stable speedup estimate. This extends the crypto workload coverage;
it is not an additional compiler optimization.

Allocation-analysis lead: Hash owns three arrays and Hmac owns pads, digest
and Hash. Escape.Analyse currently treats a derived pointer stored as a
value as escaping, without proving a local owner's lifetime. Investigate
whole-object-graph escape analysis or scalar replacement, preserving aliasing,
identity, loop lifetime, GC-root visibility and Close's secret erasure.

Optimized-IR confirmation: build/perf-hmac/optimized.ir was generated with
the preserved spill compiler using --dump-opt and the same dynamic libraries.
In the SHA workload's constructed context, the parent is already stackobj
(40-byte zeroed frame object). Three calls to Runtime.Alloc remain, requesting
80, 48 and 528 bytes (array headers plus block/state/work payloads), followed
by stores of those references at parent offsets 8, 24 and 28. Initialize is
then called on the stack parent. Thus larger constructor inlining is not the
missing step in this case: it already exposed the allocation graph.

Do not simply exempt stores into promoted parents from Escape.Analyse.
A parent can remain local while a method returns or retains one of its fields.
The current parameter summary tracks derived addresses, not references loaded
from fields. A correct extension needs field-sensitive retention summaries,
tracking of child references reloaded from owners, and combined loop-lifetime
proof. Keep children on the heap whenever those proofs are unavailable.

### SHA word-storage reduction

SHA-256 schedule entries and public round constants now use uint rather than
long storage. The schedule is still private to each context and erased on
Close; constants remain shared and public. Every schedule write was already
bounded to 32 bits. Arithmetic remains long with the same explicit masks.
This removes 256 payload bytes per context and another 256 bytes from the
one-time constant array. It is library tuning, not a compiler optimization.

Same compiler/libraries, three alternating 10,000-operation comparisons
(build/sha-word-comparison.log): SHA short collections 35 -> 22, times
27.943810/30.502361/27.569254 ms -> 22.091840/21.433283/21.401137 ms.
Fresh HMAC collections 51 -> 38, times 64.284427/63.163621/62.767253 ms ->
56.187698/54.897954/54.624988 ms. All known-answer checks pass. Preserved
old source: build/crypto_base-before-word-storage.cor. Wider message-length
and context-interleaving coverage remains part of crypto acceptance.

### Post-storage SHA profile: collector priority

500,000 SHA-short operations after word-storage reduction: 1.146137217 s,
1,137 collections, all known answers valid. `perf record -e cpu-clock:u
-F 199 -g` captured 225 samples with none lost (build/sha-word-profile.data).
Self samples: Gc.ScanRange 36.89%, HeapChunks.Of 12.89%, Hash.Compress 30.67%,
Gc.Take 6.22%, Hash.Finish 5.33%, Hash.EraseState 3.56%. Sampling proportions
are approximate; they identify collector scanning as a priority, not a
measured gain for any proposed rewrite.

Code audit: ScanRange calls MarkAt for every aligned source word. MarkAt
increments MarkCalls then calls BlockForPointer, which calls HeapChunks.Of;
the heap floor/ceiling rejection happens inside that final helper. Bounds
are maintained in HeapChunks.Take, and collection synchronizes the current
chunk's end before scanning. Potential optimization: reject non-heap words
at the scan loop using stable bounds, preserving every plausible pointer
(including unaligned interiors and one-past payload pointers), MarkCalls
semantics, and all full block/header/footer checks for candidates.
Prove bounds stability under GC locking/stop-the-world before hoisting.
Do not edit lib/rt/gc.cor while the active language milestone reads it.

Isolated filter prototype: build/gc-scan-filter.cor copies the runtime and
hoists HeapChunks' envelope into ScanRange, leaving full validation for every
in-envelope candidate. MarkCalls still increments for every scanned word.
Take changes the envelope while holding the heap lock; Release deliberately
does not shrink it, so holes remain false-positive candidates rather than
lost roots. The main runtime source is unchanged.

Explicit static-source control/prototype builds using the same compiler and
all library sources, 100,000 SHA-short operations: control
115.725937/118.342949/120.869346 ms, prototype
116.053841/116.531841/116.144652 ms. All known answers valid, 227 collections
on both sides (build/sha-gc-filter-comparison.log). The effect is too small
to justify a runtime change yet. Do not compare these timings directly with
the earlier dynamically linked baseline: runtime artifacts and inlining/link
context differ. Investigate that build-context gap before crediting the filter.

Matched dynamic-runtime refresh: os/build-libs.sh rebuilt all 14 libraries
into build/perf-libs-current with an explicit compiler path; the old build/lib
set is preserved. LD_DEBUG confirms loading the new set, and the same
sha-word-after executable passes SHA/HMAC known answers with both sets.
Three 100,000-operation SHA timings: old 254.336802/241.864595/238.286622 ms,
new 233.535782/229.118349/234.535081 ms, all 227 collections. HMAC timings
overlap and collection counts vary slightly, so no HMAC gain is established.
Logs: build/perf-runtime-refresh-comparison.log, perf-current-loader.log,
perf-libs-current-build.log. GC ScanRange shrinks 1,243 -> 1,118 bytes, but
refreshing runtime artifacts alone does not explain the full static/dynamic gap.
Crypto reporting now includes MarkCalls and WalkSteps deltas so subsequent
measurements can separate words scanned and interior-pointer searches from
the cost per operation. No scanning behavior is changed by this reporting.

## Batched instruction-selection measurements

Mask-half simplification, wide constant multiplication and direct wide-shift
selection change actual emitted code. Three alternating 2,000-operation
X25519 reuse runs (build/perf-wide-comparison.log) measured:

| Baseline seconds | Candidate seconds |
| --- | --- |
| 1.674460116 | 1.347046913 |
| 1.699573994 | 1.337589900 |
| 1.716592857 | 1.344880722 |

All vector checks passed, with zero collections. This is host-Linux evidence,
not yet real-486 latency evidence. Broad compiler correctness validation is
deferred to a substantial milestone, per the user's requested workflow.

SHA-256 short-message allocation baseline: 10,000 hashes of abc, all correct,
45,071,413 ns and 59 collections (build/perf-sha-before.log). Each context
currently allocates and wipes a separate public round-constant table.

### Library allocation improvement L001: shared SHA-256 constants

The round table is now private static readonly storage. Closing a context
still wipes message-dependent state but does not modify public constants.
This is a library change, not one of the 100 compiler transformations.

Both binaries below use the preserved baseline compiler and identical shared
libraries, isolating the storage change. Three alternating 10,000-hash runs:

| Before ns / collections | Shared constants ns / collections |
| --- | --- |
| 45,396,897 / 59 | 31,555,830 / 35 |
| 46,179,748 / 59 | 31,632,872 / 35 |
| 46,393,852 / 59 | 31,385,087 / 35 |

All abc digests matched the known answer. Evidence:
build/perf-sha-shared-comparison.log. Broader interleaved-context and crypto
regressions remain part of the next substantial validation milestone.

### Candidate C002: constant-context specialization for escape analysis

ConstantSpecialize creates bounded internal versions of constant-controlled
calls, folds those branches, and retains the original ABI/body for other
callers. Recursive, address-taken, async and exception-sensitive bodies are
excluded using the inliner's existing guards. Budgets: 512 IR instructions
per original, four versions per original, 64 versions per module.

The allocation benchmark now distinguishes local and retaining paths:
200,000 local buffer iterations measured 499,589 ns with zero collections,
versus approximately 60 ms and 97 collections before specialization. The
retained case still measured 56,146,298 ns and 73 collections. Every checksum
and zero-initialization check passed. Code grew from 8,385 to 15,153 bytes;
that trade-off needs wider-workload validation and profitability refinement.
Artifacts: build/perf-specialize/. X25519 vectors also passed at 1.229 s for
2,000 reusable-workspace operations. Milestone-wide suites remain pending.
