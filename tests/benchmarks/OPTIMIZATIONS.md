# Compiler optimization ledger

Target: at least 100 distinct, substantive new optimizations.
First batch: 10 implemented candidates. Second batch: 2 implemented candidates.
Third batch: 3 implemented candidates; broad regression milestone completed.
Fourth batch: 2 implemented candidates; benchmarks and broad milestone passed.
Fifth batch: relocated immutable metadata/root-range separation; broad milestone passed.
Sixth batch: rotate recognition and small stack fills; broad milestone passed.
Seventh batch: integer value reuse, constant reassociation and owned-field promotion; in progress.
Eighth milestone: 264 language tests and 48 optimizer checks passed, including nested ownership.
Next batch: branchless unsigned carry-identity recognition; benchmark passes.
Paired-product sharing is a 486-targeted code-size/instruction-mix candidate;
modern-host elapsed time is flat to slightly slower, not a measured speedup.
There are 31 implemented candidates; eleven grouped milestones have passed
at their recorded source snapshots. The final per-entry acceptance count
still requires an evidence audit; this is not a claim of 31 completed entries.
Do not split equivalent
operator variants to inflate the count. All x86 changes target 486+x87.

| ID | Transformation | Implementation under compiler/Lang | Remaining legality coverage |
| --- | --- | --- | --- |
| 001 | Fold identity/absorbing halves of wide bitmasks | X86/Select.cs, BitOrAlu | All masks, overlap and register pressure; flags are not observable in IR |
| 002 | Specialize wide multiplication with a zero constant half | X86/Select.cs, SelectMul | Modulo-64 boundaries and source/destination aliasing |
| 003 | Eliminate overwritten copies in wide constant shifts | X86/Select.cs, SelectShift | Counts 0/31/32/63/64 and signed negatives |
| 004 | Omit multiply cross-products with proven zero upper inputs | X86/Select.cs, ZeroHigh/SelectMul | Single-definition facts; randomized wide products |
| 005 | Substitute proven zero upper halves at consumers | X86/Select.cs, PairRM | Mutable values remain unknown; loop/redefinition cases |
| 006 | Narrow division of proven nonnegative narrow I64 operands | X86/Select.cs, SelectDiv | Zero-divisor behavior and quotient/remainder boundaries |
| 007 | Narrow proven nonnegative wide comparisons | X86/Select.cs, IntCompare | Signed I64 ordering becomes unsigned I32 ordering; all predicates |
| 008 | Narrow right shifts of zero-upper-half I64 values | X86/Select.cs, SelectShift | Signed/unsigned agreement and masked count boundaries |
| 009 | Specialize constant-controlled call contexts before escape analysis | Opt/ConstantSpecialize.cs | EH/recursion/address guards, budgets, object identity and escape cases |
| 010 | Hoist invariant non-trapping integer work from natural loops | Opt/LoopInvariant.cs | Exclusive preheader, dominance, zero-trip/nested/EH cases |
| 011 | Forward nonadjacent private spill loads after register allocation | X86/MachineIr.cs, RegAlloc.cs, Peephole.cs | Build and benchmark checks pass; dedicated clobber/alias/barrier coverage pending; allocator provenance required |
| 012 | Scalar-replace directly accessed object fields across dominated control flow | Opt/ScalarObjects.cs | Build and benchmark checks pass; dedicated field overlap, escape, initialization and loop cases pending |
| 013 | Narrow products whose entire unsigned result fits 32 bits | X86/Select.cs, UnsignedBits/SelectMul | Builds and benchmark checks pass; crypto unchanged, no performance credit; boundary coverage pending |
| 014 | Remove globally unused virtual register moves before allocation | X86/RegAlloc.cs, PruneUnusedMoves | Build and benchmarks pass; dedicated machine-IR and milestone coverage pending |
| 015 | Forward current block-local copies with redefinition invalidation | Opt/LocalCopies.cs | Builds and benchmarks pass; mutable-source/branch/loop regression coverage pending |
| 016 | Replace common constant divides/remainders with exact reciprocal products | X86/Select.cs, SelectDiv | Signed/unsigned builds and benchmarks pass; dedicated extremes and wide-result coverage pending |
| 017 | Reuse a prior matching quotient to derive the remainder | Opt/DivRemReuse.cs | Build and benchmark pass; signed extremes, traps and redefinition cases pending |
| 018 | Exclude loader-relocated immutable data and metadata from dynamic GC roots | X86/X86Backend.cs, Elf/Linker.cs, Elf/Dynamic.cs | Shared-library rebuild and crypto benchmarks pass; ELF/shared-root/runtime milestone pending |
| 019 | Recognize complementary shifts as a 32-bit rotate, including masked I64 idioms | X86/Select.cs, MachineIr.cs, Encoder.cs | SHA benchmark passes; count, sharing, alias and branch-fact regressions pending |
| 020 | Expand small constant fills of proven aligned frame storage | X86/Select.cs, SelectSmallStackSet | Build and allocation benchmark pass; fill-byte/tail/alignment edge coverage pending |
| 021 | Reuse block-local integer expressions under mutable-register invalidation | Opt/IntegerValueReuse.cs | Build, arithmetic and SHA checks pass; reassignment/trap/float exclusions need targeted milestone coverage |
| 022 | Reassociate same-width modular integer constant chains | Opt/IntegerReassociate.cs | Candidate; build, benchmark and mutation/wraparound coverage pending |
| 023 | Promote child buffers through field-sensitive receiver retention proofs | Opt/OwnedFieldEscape.cs, Escape.cs | SHA benchmark eliminates timed GC; capture/return/overlap fixtures pass, broader milestone running |
| 024 | Replace the generate/propagate carry-bit identity with an unsigned comparison | Opt/CarryRecognition.cs | X25519 benchmark and disassembly pass; mutation/shared-value/word-boundary fixtures await next milestone |
| 025 | Share word products and the middle column between low/high wide products | Opt/WideProductSharing.cs | 81 exact product vectors and IR guards pass; fewer multiplies/smaller code, actual 486 timing remains unverified |
| 026 | Remove unused virtual-register arithmetic before allocation using flag liveness | X86/RegAlloc.cs, PruneUnusedValues | SHA/HMAC benchmark gains and x86 guard cases pass; broader language milestone pending |
| 027 | Replace signed power-of-two division/remainder with sign-correct bias/shift/mask arithmetic | Opt/SignedPowerOfTwo.cs | Narrow/wide benchmarks and extreme-value language checks pass; helper/exceptional-divisor unit cases staged |
| 028 | Lower the runtime byte-reversal primitive to native 486 word swaps | Opt/ByteSwapCalls.cs, X86/Select.cs, Encoder.cs | 6.5x focused benchmark improvement; constant, width, alias and flag checks pass; broader milestone pending |
| 029 | Prioritize repeated allocations within the fixed stack-promotion budget | Opt/Escape.cs, PromotionOrder | Fresh X25519 timed collections 47 to zero; lifetime guards unchanged, grouped milestone running |
| 030 | Remove integer masks that retain every bit the input can contain | X86/Select.cs, SelectAlu/UnsignedBits | Byte-packing guards and checksum benchmark pass; next grouped milestone pending |
| 031 | Narrow constant I64 left shifts whose complete result fits I32 | X86/Select.cs, SelectShift/DefinitionBits | Preserves wide overflow bits and masked counts; focused checks pass, next grouped milestone pending |
| 032 | Fold proven promoted-frame pointer chains into in-bounds stack memory operands | Opt/FrameAddressFold.cs | Candidate: X25519 about 10% faster and crypto text 4.6% smaller; bounds/mutation checks pass, retained-buffer timing regression and broad acceptance remain open |

These are grouped transformations, not a claim that each row improves every
workload. Broad correctness and per-transformation emitted-code evidence
remain milestone gates. No relaxed FP or managed semantics are intended.

## Measured batch effects

- Alternating original/wide-selection X25519 measurements are in README.md.
- Context specialization removes local-buffer heap pressure: 97 collections
  become zero for 200,000 iterations; retained buffers still allocate.
- Latest combined benchmark (build/perf-narrow-compare): X25519 reuse
  1.175964570 s / 2,000, SHA-256 short 28.491176 ms / 10,000, local buffer
  0.491160 ms / 200,000, invariant loop 4.655110 ms / 10 million. All checks
  pass. These are host-Linux observations, not real-486 acceptance.
- Longer allocation comparisons did not confirm the earlier single-sample
  LICM regression: build/perf-licm-allocation-comparison.log. Register-pressure
  and code-size trade-offs still require wider coverage.

Not counted: inlining heuristic C001 (no demonstrated change to the original
benchmarks), existing passes, benchmark infrastructure, or SHA library tuning.

## Post-batch profiling and next selection

Reprofile of build/perf-narrow-compare/crypto, 10,000 X25519 reuse operations:
5.935414139 seconds, zero collections, every known-answer check valid.
`perf record -e cpu-clock:u -F 199 -g` collected 1,181 samples with none lost
(build/perf-narrow-compare/reprofile.data). Self time remains concentrated in
Multiply (37.17%), AddProduct19 (25.83%), CarryRound (21.68%) and Canonicalize
(9.48%). These percentages are sampled shares, not isolated speedup claims.
Multiply is now 8,165 bytes versus the original 12,793-byte symbol.

Next batch should target remaining arithmetic and spill traffic in those
routines, alongside allocation elimination. The post-allocation peephole
currently forwards only adjacent store/load pairs. Extending that across
instructions requires explicit alias, register-clobber and memory-effect
barriers. Do not treat arbitrary memory as a compiler spill slot: MMem currently
has addressing information but no spill provenance or volatile marker. Audit
that distinction before extending forwarding, especially for kernel MMIO.
Candidate 011 now marks allocator-owned spill references explicitly and uses
a bounded backward scan, stopping at calls, control flow, unknown memory,
overlaps or source-register clobbers. It is not enabled in the executable
used for the first-batch language suite. A separately built candidate succeeds
with zero build warnings/errors and valid crypto, allocation and loop checks.
Scalar replacement and redundant-load elimination from LLVM, and argument
specialization from GCC, are reference techniques rather than new completed
ledger entries; the reference links are in README.md.

## Milestone results

First batch executable (before candidate 011): 258/258 language tests,
40/40 optimizer tests, x86 suite ALL CHECKS PASSED. Logs:
build/perf-batch1-language.log, build/perf-batch1-opt-fixed.log,
build/perf-batch1-x86.log. This establishes broad regression coverage, not
every listed boundary or individual transformation's performance contribution.

Candidate 011: build/perf-spill-comparison.log contains three alternating
X25519 reuse comparisons (2,000 operations each). Before: 1.216230663,
1.189655792, 1.197979645 s; after: 1.190494019, 1.185511174, 1.197163642 s.
Every result is valid with zero collections. Differences are small: do not
claim a material speedup. Crypto code falls from 41,297 to 41,193 bytes;
Multiply falls from 8,165 to 8,105 bytes. Allocation and loop checks pass;
local buffer/object collections remain zero, retained buffers still collect.
The single loop timing is not sufficient to establish a regression or gain.

## Rejected experiment: wide constant shift/add chains

Experiment f3452762 lowered factors 3/5/9/17/19/33/65 to two-half shift/add
chains. It built cleanly and passed the crypto/loop benchmark checks, but
three alternating X25519 comparisons regressed (build/perf-shiftmul-comparison.log).
Before: 1.226209851, 1.170166375, 1.175722046 s; experiment: 1.351373541,
1.341127512, 1.317093816 s. Crypto code grew from 41,193 to 41,334 bytes.
The transformation is removed from the default path and receives no credit.
An advantage on an actual 486 is unproven; do not infer it from instruction
names or modern-host timing. The experiment remains in git history and its
isolated build artifacts for a future target-specific profitability analysis.

## Scalar object replacement

Candidate 012 removes small constant-sized allocations whose entire address
use is direct, fixed-offset 1/2/4/8-byte integer field loads/stores.
Each field becomes a local value initialized at the original allocation site.
Unknown uses, calls taking the object, address escape, identity comparisons,
overlap and differing access widths/types reject the candidate. The allocation
and each derived address definition must dominate their uses; the existing
escape-analysis liveness check rejects an earlier iteration's live reference.
Multiple CFG roots (including exception roots) are rejected. This is a first
stage, not the requested complete field-sensitive
owned-buffer lifetime analysis; SHA/HMAC child-array code remains unchanged.

Build has zero warnings/errors. All X25519, SHA, HMAC fresh/reuse, allocation
and loop benchmark checks pass (build/perf-scalar-results.log). Allocation
benchmark code falls 15,241 -> 15,161 bytes. Dedicated legality coverage and
the next batch's suite run remain pending; no full suite was run per change.

Three alternating 2,000,000-iteration allocation runs confirm the small-object
loop benefit: before 3.623068/3.573108/3.567348 ms, scalar replacement
0.878267/0.852447/0.854766 ms (build/perf-scalar-comparison.log). Checksums
match and collections stay zero on both sides: this eliminates residual
object storage/access work, not previously remaining GC activity. Retained
buffers still collect 732 times on both sides; no buffer speedup is claimed.
This is a host microbenchmark result, not end-to-end terminal/SSH acceptance.

The control-flow extension to candidate 012 (not another numbered optimization)
handles branch joins while retaining those lifetime checks. Three alternating
2,000,000-iteration branch-object samples: before 4.044127/3.913855/4.070347 ms,
after 1.663192/1.665132/1.633031 ms. All allocation checksums match, with
unchanged collection counts (build/perf-scalar-branch-comparison.log).
The new compiler builds with zero warnings/errors; SHA/HMAC code remains
unchanged. This does not complete cross-call child-buffer analysis.

Narrow-field extension (still candidate 012): byte/short stores use explicit
zero-extension to retain only their stored bits; each signed/unsigned load
uses its own extension. Different-width overlapping accesses still reject
scalar replacement. The benchmark covers byte, sbyte, ushort, short and an
untouched zero-initialized byte, with its reference checksum computed by
integer masks and arithmetic rather than narrow casts.
Two million iterations pass before/after, checksum 65,751,578,368, with zero
collections (build/perf-narrow-fields-comparison.log). First timing pair is
5.096597/1.963347 ms, not a confirmed speedup estimate. Allocation benchmark
code falls 22,413 -> 22,245 bytes; build has zero warnings/errors. Other
allocation cases pass, including retained buffers with 732 collections.

Second batch milestone completed: 259 language tests, 41 optimizer tests,
x86 ALL CHECKS PASSED (build/perf-batch2-*.log). Includes optimizer_objects
and direct scalar escape/overlap/width checks. Does not cover subsequent
ownership ABI fixes, AddressOf widening, experimental SSA, or candidate 013.

Candidate 013 builds cleanly and passes allocation/X25519 checks. The control
and candidate crypto binaries are identical (SHA256
332c6e50d5b513a3463c9e61827a35a4bb7829379e8993233ae09a6d1cc1b292), so
timing differences in build/perf-bounded-product-comparison.log are noise.
CarryRound's carry register is multiply defined; all assignments shown in
the optimized IR are logical right shifts by 51, but single-definition-only
facts cannot follow it. Extending range bounds requires accounting for every
definition, entry value and loop cycle rather than selecting a convenient
assignment. Candidate 013 has no demonstrated performance contribution yet.

All-definition extension: parameters retain unknown entry bounds; every
assignment contributes to the maximum, with depth/visit limits returning
unknown. This changes CarryRound, but its first form regresses the measured
workload: control 1.172565/1.172194/1.171126 s versus candidate
1.257959/1.189904/1.238089 s (2,000 X25519 operations, all valid, zero GC).
Disassembly shows a shorter final multiply but extra spills throughout the
function: CarryRound grows 369 -> 393 bytes. Extending zero-high facts to
other consumers reduces total crypto code to 54,605 bytes but leaves that
function at 393 bytes; its first timing is 1.249030 s, still not a win.
This is WIP with an unresolved allocator/profitability regression, not a
completed optimization. Next work must address spill lifetime placement or
withhold the regressing selection, not claim the smaller multiply alone wins.

Candidate 014 addresses the identified dead-high-half spill traffic. It removes
only MOV into an unread virtual register, with no memory source or lock; all
physical destinations and memory reads remain. Read sets include address
registers and operand roles. Repeating removes newly dead copy chains before
the allocator assigns registers or slots. Arithmetic/flags are untouched.

CarryRound now falls to 358 bytes (original control 369; regressing candidate
393). Total crypto is 54,201 bytes versus 54,689 in the bounded-product control.
Three alternating X25519 comparisons: control 1.193537/1.183622/1.187715 s,
combined candidate 1.194290/1.201366/1.195634 s. Correct results and zero GC;
no clear speedup is established, despite smaller code and removal of the
previous larger regression. Allocation, HMAC and loop checks pass; the single
loop timing is noisy and not a performance conclusion. Evidence:
build/perf-dead-moves-comparison.log, perf-dead-moves-build.log and disassembly.

Candidate 015 maintains one-hop copy facts per block, rewrites reads before
processing the instruction's definition, and invalidates every alias of a
redefined source. Facts do not cross blocks or phi edges. It also runs after
scalar replacement with DCE before escape analysis, exposing removable field
copies without assuming that referenced objects are local.

Build succeeds; allocation and X25519 checks pass. Crypto code falls from
54,201 to 53,973 bytes in the original benchmark set. First owned-buffer
benchmark (build/perf-owned-comparison.log) still collects 74 times locally
and 73 times when retained on both compilers. Optimized IR shows its shared
Sample helper still combines local and retained paths under a runtime kind
argument; no owned-buffer allocation win is established. Do not treat passing
checks or eliminated scalar copies alone as proof of the requested child-
buffer lifetime optimization. Cross-call SHA/HMAC field retention remains open.

Follow-up: the four-version specialization cap skipped the later benchmark
modes. Candidate 009 now allows up to 16 versions under a retained-code budget
of 2,048 instructions per original function (plus the existing module cap).
That exposed the literal paths, but the local buffer still collected until
candidate 015's copy facts crossed single-predecessor dominated edges.
Facts still reset at roots, joins and phi boundaries; redefinitions invalidate
captured sources. This carries field references across bounds-check branches.

With both refinements, owned-buffer-local drops from 74 collections to zero
for 200,000 iterations, checksum 25,493,856 unchanged. The retained case
still allocates (74 collections in this run; earlier 73 depends on heap phase).
First local timing 0.423978 ms versus the earlier 54–61 ms heap-allocating
samples; do not claim a stable timing ratio from this single new sample.
All allocation/X25519 checks pass (build/perf-dominated-copies-results.log).
Code growth is material: allocation 32,069 bytes, crypto 57,610 bytes. Budget
profitability and broader correctness remain open; this does not complete
cross-call SHA/HMAC analysis or increment the count for pass extensions.

Candidate 016 uses exact high-word unsigned multiplication and shifts for
divisors 3, 5, 10. Signed I32 is excluded; signed/unsigned I64 participates
only when the numerator is proven nonnegative and no larger than uint.MaxValue.
Remainders use n-q*d. No zero or negative divisor is rewritten; wider/unknown
numerators retain their existing path. Only 486 integer instructions are used.

The division benchmark checks a million pseudorandom inputs against mutable-
data divisors outside the timed section. Three alternating results: control
6.822569/6.912251/6.717948 ms, candidate 2.615679/2.662001/2.735832 ms, all
checksum 1,359,528,045,344,287 and valid. Code grows 10,129 -> 10,153 bytes.
X25519's known-answer check also passes. Evidence:
build/perf-constant-divide-comparison.log and corresponding build logs.
This is a host microbenchmark gain, not measured SSH or real-486 latency.
The running third-batch language suite predates this change.

Candidate 017 replaces a same-block remainder with n-q*d when the matching
division already computed q. Signedness and width must agree. Reassignments
of q, n or d invalidate the cached result; overwriting an input with the
quotient is not cached. No facts cross blocks, and the original division
remains before the replacement, retaining divide-by-zero/overflow behavior.

On top of candidate 016, three alternating million-iteration samples:
2.634170/2.692687/2.621169 ms before, 2.028209/1.991808/2.014098 ms after.
All checksums match the mutable-divisor reference; X25519 also passes.
Division code falls 10,153 -> 10,061 bytes. Evidence:
build/perf-divrem-comparison.log and perf-divrem-bench-build.log.
This does not yet fuse hardware quotient/remainder outputs or handle
remainder-before-quotient order; those are future extensions, not new claims.

Signed extension of candidate 016 (not another optimization count): I32
division by positive 3/5/10 uses signed high-product IMUL, arithmetic shift
and subtraction of the numerator's sign word. Remainder remains n-q*d.
The one-operand signed widening multiply is now represented in machine IR,
encoded as F7 /5, printed as imul, and declares EAX input plus EAX/EDX outputs
to the allocator. This is a 486 instruction, not an ISA upgrade.

Three alternating signed million-iteration samples: previous quotient-reuse
compiler 4.630368/3.753137/3.780242 ms; signed reciprocal candidate
2.928451/2.529559/2.540778 ms. Every result matches the runtime-divisor
reference, checksum -495,975,407,572. Build has zero warnings/errors.
Evidence: build/perf-signed-divide-comparison.log. Signed MinValue, neighboring
multiples and exception cases still need dedicated milestone coverage.

Third milestone: 43 optimizer checks and x86 ALL CHECKS PASSED. Language run
was 259 pass / 1 fail: GC churn reported no collections while its checksum
and heap cap passed. Exact-input optimized IR (build/gc-churn-audit.ir) shows
the 1,016-byte allocation promoted to a zeroed frame slot after its local
holder was scalarized. The fixture now uses a persistent read-back static
root to force actual heap allocations, with all expected outputs unchanged.
Its same-compiler focused rerun passes (build/perf-batch3-gc-churn-fixed.log).
This is 259 original passes plus one corrected-fixture pass, not a claim of
a second full 260-test run. Candidate 016/017 boundary regressions have been
added but are not covered by that older executable's milestone.

Candidate 018 separates source-immutable address-bearing data, frame tables,
stack maps and jump tables into .data.rel.ro before mutable .data/.bss.
Dynamic loading can still relocate the section; __data_start.._end retains
mutable statics but no longer includes those immutable tables. Static links
merge the relocated-constant inputs into ordinary .rodata after relocation.
This does not add PT_GNU_RELRO protection; the section remains loader-writable.
It is root-range reduction based on compiler immutability, not a new precise
heap tracer or a claim that arbitrary writable native data can be skipped.

Same executable, three alternating rebuilt-library comparisons, 100,000 SHA
operations: scanned words 31,969,986 -> 664,416, with 227 collections and
3,948,339 interior steps unchanged. Times 229.148760/225.858458/234.557558 ms
-> 119.424019/120.491539/120.330171 ms. HMAC fresh times
571.978355/547.932507/585.120460 ms -> 401.555326/392.943437/378.777263 ms;
HMAC collection counts vary with conservative retention. All known answers
pass. Evidence: build/perf-immutable-metadata-comparison.log, section dumps,
and perf-libs-immutable-metadata-build.log (all 14 libraries rebuilt).
An ELF root-boundary regression is added; the broader fifth milestone is
running and no completion claim is made yet.

ELF milestone follow-up: installed Fedora's glibc-devel.i686 and libgcc.i686
(and their package-manager dependencies) to remove the gcc -m32 skip. The C
fixture called Answer(true), whose callback adds one to 42, but mistakenly
expected 42; corrected its exit/output checks to 43. An isolated build of
the ELF suite now reports 113 passed, zero failed, including the C/shared-
library/C callback path. Logs: build/elf-multilib-install.log,
build/perf-batch5-elf-multilib-build.log, build/perf-batch5-elf-multilib.log.
The fifth language suite is still running; this is not its final verdict.

Candidate 019 matches adjacent complementary constant shifts joined by OR,
either unsigned I32 or the six-instruction masked-I64 SHA pattern. Counts
must be 1..31 and sum to 32; intermediate definitions must be single-use,
single-definition and must not overwrite the original source. It emits ROR
and removes the matched computations as a unit. Final-destination constant
facts are invalidated, including when the result overwrites a prior local.
ROR is a 486 instruction; it is not treated as defining ZF/SF in flag cleanup.

Same updated libraries and source, three alternating 100,000 SHA-short runs:
control 120.706494/118.979756/120.814097 ms, rotate candidate
99.247996/98.520697/102.725258 ms. All known answers valid; 227 collections
on both sides. Compress shrinks 1,728 -> 1,489 bytes and contains ten ROR
instructions. Logs: build/perf-rotates-comparison.log, build/perf-rotates/
crypto.asm and crypto-symbols.txt. No generic timing or real-486 claim follows
from this one workload, and the running fifth suite predates the candidate.

Fifth milestone finished: 261 language tests passed, 44 optimizer checks,
x86 ALL CHECKS PASSED, and the isolated multilib-enabled ELF suite passed
113 checks. See build/perf-batch5-*.log. Rotates and small stack fills are
not covered by that executable.

Candidate 020 emits direct 4/2/1-byte stores for constant fills of at most
32 bytes in a proven frame slot aligned to at least four bytes. Zero-length
fills need no stores. Arbitrary pointers, unknown counts/fill bytes and other
alignments keep the existing REP STOSB path; no wider MMIO access is inferred.
The low fill byte is replicated; tail stores never cross the requested end.

Small-clear benchmark uses dynamic array indices to require fresh zeroes on
every iteration. Three alternating million-iteration results:
2.434177/2.427177/2.438237 ms before, 1.696812/1.689992/1.658962 ms after.
All checksums match (127,493,856), zero collections. Evidence:
build/perf-small-stack-fill-comparison.log and build logs. Code-size tradeoffs
remain: direct stores increase some binaries; this is not a blanket fill rule.

Larger-frame experiment: added REP STOSD machine support and tried word bulk
stores with an exact byte tail beyond 32 bytes. The medium-buffer benchmark
passes but regresses on the host: control 9.642183/9.635004/9.707924 ms,
word-fill 10.578171/12.078370/10.659598 ms per million iterations; zero GC
and identical checksum 127,493,856. The larger-fill selection is disabled;
the validated small-fill path remains. REP STOSD encoding support is retained
for future target-specific work, with no optimization-count credit. A real
486 advantage cannot be inferred from host timing alone. Evidence:
build/perf-frame-word-fill-comparison.log and preserved experimental compiler.

Candidate 021 implements integer value reuse in the ordinary non-SSA pipeline.
Keys include opcode, result/operand widths and current operands; commutative
operations are canonicalized. Every definition invalidates cached results and
expressions using that register. Self-overwriting expressions are not cached.
Facts propagate only over a unique dominating predecessor; roots, joins,
loop headers and phi nodes reset them. Floating point, memory, calls,
division/remainder and atomics are excluded. This does not enable the existing
SSA GVN wrapper; the new pass handles mutable IR directly.

The rotate matcher also accepts the four-instruction shared-zero-high-input
form that expression reuse exposes, preserving the previously measured rotate
optimization rather than recomputing its shifts.

Three alternating million-iteration arithmetic runs: baseline
1.212473/1.239133/1.324955 ms, candidate 0.745814/0.727174/0.761264 ms.
Checksum 1,107,208,546,864 matches the independently structured reference in
every run. SHA-short 100,000-operation known-answer checks pass, 227 GC cycles.
Evidence: build/perf-integer-values-comparison.log and build logs. Broader
non-SSA invalidation and alias-sensitive cases remain the next milestone's work.

The unique-predecessor extension of 021 improves the branch benchmark from
1.271133/1.280784/1.263934 ms to 1.023369/1.018069/1.040460 ms per million
iterations; all checksums 1,107,209,046,864 are valid. Evidence:
build/perf-dominated-values-comparison.log. This is not another numbered pass.

Sixth milestone: 262 language tests, 44 optimizer checks and x86 suite pass.
It covers rotates and small fills, but predates integer value reuse.
Evidence: build/perf-batch6-*.log.

Candidate 022 combines same-width constant chains for modular addition,
subtraction, multiplication and bitwise AND/OR/XOR. Subtraction is normalized
to a wrapped additive offset. Facts stay within a block and invalidate on
source/result writes; phis clear facts. Floating point, checked arithmetic,
memory, mixed widths and other operators are excluded. Dead intermediate
operations are removed by the existing DCE pass rather than by assumption.

Three alternating ten-million-iteration constant-chain runs: control
10.300416/9.414461/9.477265 ms, candidate 7.266068/7.297289/7.249933 ms.
Checksum 4,894,525,408,384 agrees with the independently simplified reference.
Total executable code is 9,857 -> 9,853 bytes. SHA-short 100,000 hashes still
passes its known answer with 227 collections (96.993545 ms; single sample,
not a crypto speedup claim). Evidence: build/perf-constant-chains-*.log.
Mutation and wraparound regression fixtures are staged for the next milestone.

Runtime follow-up (not credited toward the compiler's 100 transformations):
the interior-pointer walk now stops when the next block's payload starts
after the candidate address. Positive sizes make block addresses monotonic;
the previous block's inclusive one-past-end match is still checked before
advancing. Header/footer validation and the exact-address fast path remain.
No added allocation, global cache, or changed root semantics is required.

The preceding one-entry interior-block cache experiment showed no clear
gain and was removed. Its comparison remains in
build/perf-interior-cache-comparison.log; no performance credit is claimed.

Bounded-walk comparison, identical compiler and static sources except GC:
100,000 SHA-short operations take 97.371908/95.268267/94.190206 ms before,
92.099282/92.426998/92.741624 ms after. Each side has 227 collections and
193,618 candidate words; block-walk steps fall 3,641,106 -> 1,939,261 (47%).
All known answers pass. Evidence: build/perf-interior-bounded-comparison.log.
The focused 62_gc_interior language test passes using the matching local
runtime sources: build/perf-interior-bounded-regression-local-libs.log.
An initial isolated-compiler run omitted CORC_LIB and selected mismatched
automatic libraries, causing duplicate declarations at compile time; the
explicit local-library rerun corrects that invocation, not the language test.

Candidate 023 proves reference-field retention through fixed-offset receiver
aliases and direct callees, then includes references reloaded from that field
in ordinary escape/liveness analysis. Unknown calls, recursion, partial field
overlap, byte copies, variable/far/wrapping receiver offsets and captures are
rejected. Only already-promoted owners created before the child in the same
basic block qualify initially, proving owner renewal each time the child
allocation executes. Child allocation must dominate approved field stores.
Frame limits, zeroing, identity checks and existing liveness guards remain.
Multi-level HMAC graphs and more general owner/control-flow proofs remain open.

Identical shared libraries, three alternating SHA-short runs (100,000 hashes):
control 96.701534/97.250434/96.493180 ms, candidate
66.707868/66.685143/70.038626 ms. Collections drop 227 -> 0; all known answers
pass. Optimized IR contains 40-byte parent and 80/48/272-byte child frame
slots, replacing the SHA block/state/work allocations. Evidence:
build/perf-owned-fields-comparison.log, build/perf-owned-fields/optimized.ir.
Fresh HMAC currently drops only 385 -> 348 collections with nearly unchanged
timing; reuse remains zero GC. See build/perf-owned-fields-all-hashes.log.

Seventh optimizer/x86 checkpoint: 47 checks and x86 ALL CHECKS PASSED.
The existing compare-of-compare fixture expected two identical instructions
after the complete pipeline; CSE now correctly merges them. Its original
peephole assertions are retained before CSE, with an additional full-pipeline
assertion that both stores consume the shared comparison. Evidence:
build/perf-batch7-opt.log, perf-batch7-opt-fixed.log, perf-batch7-x86.log.
The subsequently tightened far-offset guard and additional nested-call
fixtures need their follow-up check; the full language milestone is running.
Follow-up guard checkpoint: 47 optimizer checks pass, including nested safe
reads, nested captures, returned children and far/wrapping field derivations.
Evidence: build/perf-batch7-fields-guard.log. No full language verdict yet.

Seventh full language milestone completed: 262 passed, 0 failed, alongside
47 optimizer checks and x86 ALL CHECKS PASSED. It predates the later
cross-block renewal, indirect-ancestor guard and static delegate-conversion
changes. Those have 48 optimizer checks and the separate static method-group
language regression, not a second full-suite claim.

The 023 extension now permits an earlier owner block when it dominates the
child and every cycle back to the child passes through the owner block.
An owner outside an inner child-allocation loop remains ineligible. A child
promoted through an ancestor cannot itself anchor another generation of
promotion until indirect ancestor aliases are modeled. A regression returns
a grandchild through its ancestor and proves that grandchild stays on heap.

Cross-block owner benchmark: identical source and libraries, comparison
compiler from f4991995 with only OwnerRenews restricted to the earlier
same-block policy, built in build/owner-path-control.hzuaiARa. The initial
delegate-only fixture allowed inlining on both sides and was non-diagnostic.
The corrected fixture preserves the Read method entry with Sys.AddressOf;
both sides retain the direct call. Optimized IR shows the control's 80-byte
Runtime.Alloc in the loop replaced by an 80-byte frame slot in the candidate.
Three 200,000-iteration runs: 18.717486/18.609784/19.349087 ms control,
0.633182/0.638152/0.577301 ms candidate; 97 -> 0 collections and matching
checksum 50,887,776. Evidence: build/perf-owner-paths-preserved-comparison.log,
perf-owner-paths-control.ir and perf-owner-paths-candidate.ir. This extends
023; it is not another numbered optimization or a whole-application gain.

Static method-group delegate assignment/return conversion was a compatibility
gap exposed by the new fixture. It now reuses contextual delegate lowering;
field/local assignments, reassignment, return and overload selection pass in
optimizer_static_method_groups. It is not optimization-count credit. Instance
receiver evaluation/capture semantics remain a separately tracked requirement.
Fresh HMAC GC counts vary with conservative roots between runs; the earlier
385 -> 348 sample is not a consistent reduction. The later paired run is
385 -> 386 with similar timing. No HMAC allocation improvement is claimed.

Further 023 extension: owner records retain base-reference aliases, approved
stores into ancestors, and the exact reference-field paths that reload them.
Queries follow those paths through local loads and direct callees. Every
ancestor path must be safe; returned/captured intermediate owners and leaf
references, unknown consumers, overlap and unproved offsets are rejected.
Paths deeper than four fields remain conservative. Interior references may
be promoted as children but cannot anchor descendants without offset proof.

The graph proof alone did not materially change HMAC: its constructor
allocations were exposed only by the final inliner, after escape analysis.
An additional bounded Inline pass after per-function cleanup but before
ScalarObjects/Escape exposes those allocations while lifetime proofs can
still act. This scheduling change is not another numbered transformation.

Fresh HMAC benchmark, three alternating 100,000-operation runs with identical
shared libraries: 287.662895/293.217215/298.228776 ms before scheduling,
238.407799/234.660232/226.632699 ms after. Collections fall 384/385/385 -> 0
and every known answer passes. Optimized IR confirms stack-backed HMAC and
nested Hash contexts and their private arrays. Total crypto code grows
60,975 -> 61,419 bytes; the size tradeoff is recorded, not hidden.
Evidence: build/perf-owned-graphs-scheduled-comparison.log and
build/perf-owned-graphs-scheduled/optimized.ir.

Runtime regression optimizer_owned_graphs preserves real call boundaries,
returns a leaf and intermediate owner, retains a leaf globally, carries a
previous iteration's leaf, then forces collection after overwriting old
frames. All results pass. Optimizer checks: 48/48. X25519 fresh/reuse, SHA and
HMAC reuse benchmark vectors also pass. Full eighth language milestone is
running (includes both new language fixtures); no full verdict yet.
Logs: build/perf-owned-graphs-runtime-check.log, perf-batch8-opt.log,
build/perf-owned-graphs-scheduled/crypto-checks.log.

Updated X25519 profile (473 samples, none lost): Multiply 35.10%,
AddProduct19 24.31%, CarryRound 23.68%. Evidence:
build/perf-owned-graphs-scheduled/x25519-profile-report.txt.

Candidate 024 recognizes the full high-bit generate/propagate carry identity
only when its sum is the matching modular addition. Intermediate definitions
must be single-use, same-block and safely reread under mutable-register IR.
It handles 32/64-bit words, preserving the sum, loads, stores and trap order.
The existing 486 backend emits CMP/SBB/SETB/MOVZX for the I64 comparison;
inspection confirms no data-dependent branch is introduced in AddProduct19.

Three alternating 4,000-operation X25519 runs: control
2.379181915/2.467266309/2.460128128 seconds, candidate
2.096381537/2.127457745/2.093630837 seconds. All vectors pass and neither
side collects. Multiply shrinks 8,070 -> 6,218 bytes; AddProduct19 400 -> 330;
total crypto code 61,419 -> 59,499 bytes. Fresh HMAC 100,000 also remains valid
with zero collections. Evidence: build/perf-carry-recognition-comparison.log,
perf-carry-recognition-hmac.log and the two crypto-symbols.txt/crypto.asm sets.

A follow-up direct ADD/ADC carry-flag fusion was rejected and removed.
Although smaller, it spills the carry result in AddProduct19 and runs slightly
slower: 2.129086000/2.114099759/2.106472338 seconds before,
2.139092042/2.144355886/2.234844485 after. Correct results do not justify a
performance regression. No optimization-count credit. Evidence:
build/perf-carry-flags-comparison.log and build/perf-carry-flags/crypto.asm.

Eighth milestone finished with 264 language tests and 48 optimizer checks
passing (build/perf-batch8-language.log and perf-batch8-opt.log). It predates
024. The kept carry candidate additionally compiles and passes 324 native
carry checks against independent split-word references. Its IR mutation,
wrong-sum and shared-intermediate fixtures compile and await the next batch's
unit execution; the broad suite was not rerun for this individual candidate.
Evidence: build/perf-carry-boundaries.log, perf-carry-boundaries-build.log,
perf-carry-kept-build.log and perf-carry-kept-opt-build.log.

Candidate 025 exposes the unsigned 32-bit partial products of a low I64
multiply only when all four corresponding high-half products are nearby and
the input values are stable. Both results then share the middle column;
all arithmetic remains modular and non-trapping. Current-value reuse (021)
now forwards newly reused values immediately within the block, retaining
canonical-source redefinition guards. That extension is not another count.

The first low-product expansion duplicated middle-column arithmetic and
regressed the host workload. The revised shared-column form reduces
AddProduct19's MUL/IMUL count 13 -> 10 and its code 330 -> 309 bytes;
Multiply falls 6,218 -> 5,780 bytes, total crypto 59,499 -> 59,039 bytes.
Three 4,000-operation X25519 comparisons: control
2.139928136/2.101238853/2.078579997 seconds, candidate
2.143178766/2.163653509/2.099434050 seconds. All vectors pass, no collections.
These times are flat to slightly slower; no measured host speedup is claimed.
Evidence: build/perf-wide-products-column-comparison.log and both generated
crypto-symbols.txt/crypto.asm sets. Earlier expansion evidence remains in
build/perf-wide-products-comparison.log.

Target distinction: Intel's i486 Programmer's Reference Manual, pages
26-160 and 26-219, documents operand-dependent early-out multiplication and
13-42-clock ranges for the relevant dword forms. Removing three multiplies
and reducing hot code is therefore a plausible 486 tradeoff, not proof of
target elapsed-time improvement. Actual 486 timing remains an acceptance
gap; modern-host timing alone cannot settle this target-specific tradeoff.
Primary source: https://mark-ogden.uk/files/intel/publications/240486-001%20i486%20Microprocessor%20Programmers%20Reference_Manual-1990%23Oct89.pdf
Local reference: build/reference-i486-programmer.pdf and extracted text.

81 independently generated exact 128-bit low/high product vectors pass,
including word boundaries, signed representations of unsigned maxima and
squares. Optimizer milestone: 51/51, including carry guards, missing/mutated
partial-product cases and immediate-CSE alias invalidation. The ninth full
language milestone is running; there is no final verdict yet. Evidence:
build/perf-wide-product-boundaries.log, perf-batch9-opt.log.

Candidate 026 extends preallocation cleanup beyond MOV-only dead copies.
Integer register arithmetic can be removed when its output is unused and
its flags cannot be observed before a full flag overwrite. Two-address
self-reads do not keep otherwise dead chains alive; operations retained for
their flags still retain their input chains. Unknown instructions and block
boundaries remain conservative; partial writers and zero-count shifts do
not kill flag liveness. Memory operands, locks, physical destinations,
division, implicit wide multiplies and x87 operations are not eliminated.

SHA Compress shrinks 1,486 -> 1,324 bytes and total crypto code
59,039 -> 58,311 bytes. Three alternating 100,000-operation comparisons:
SHA control 68.419736/69.475602/66.726469 ms, candidate
62.748113/58.687051/56.477679 ms; fresh HMAC control
235.754684/235.518294/228.710380 ms, candidate
208.559126/206.658015/206.557274 ms. All known answers pass and all runs
have zero collections. X25519 timing is approximately unchanged rather
than a claimed gain; its main arithmetic routines have unchanged sizes.
Evidence: build/perf-dead-arithmetic-hash-comparison.log,
perf-dead-arithmetic-comparison.log and generated crypto symbols/disassembly.

The x86 suite passes with new preallocation checks for dead outputs,
live flags and data, carry-in, zero shifts, rotate-preserved flags, memory,
division, physical destinations and block boundaries. Evidence:
build/perf-dead-arithmetic-x86.log. Candidate 026 is newer than the still
running ninth language executable and awaits the next broad batch.

Ninth milestone completed: 266 language tests, 51 optimizer checks and x86
ALL CHECKS PASSED. Logs: build/perf-batch9-*.log. Its executable predates
026 and 027; later focused checks do not constitute another full run.

Candidate 027 handles positive and negative power-of-two magnitudes >=2,
including the signed minimum divisor. A sign-derived bias makes arithmetic
right shift truncate toward zero. Standalone remainder uses biased masking
and removes the bias, preserving the dividend's sign. Zero and +/-1 remain
unchanged. Division/remainder pairing can still use the existing quotient
reuse pass before this lowering.

The first wide benchmark did not improve because lowering uses compiler-owned
Runtime.DivS/RemS calls on non-native-I64 targets. Exact helper names now use
the same transformation for eligible constant divisors; unrelated calls are
not treated as arithmetic. The trace identifying this gap is
build/perf-signed-powers/wide-trace.log.

Final three 200,000-iteration comparisons: I32 control
0.815836/0.769404/0.769854 ms, candidate 0.539110/0.534710/0.571451 ms;
I64 control 228.045677/230.302934/228.479832 ms, candidate
0.994139/0.954168/0.953748 ms. Checksums match the independently executed
mutable-divisor reference in every run. This is a constant-power workload,
not a claim about arbitrary division or whole-program latency.
Evidence: build/perf-signed-powers-runtime-comparison.log.

The 176 quotient/remainder boundary-pair language checks pass, including
positive/negative powers and both signed minima. Known helper, unknown-call,
zero, +/-1 and non-power unit cases are staged for the next batch.
Evidence: build/perf-signed-powers-runtime-boundaries.log. C#'s unchecked
MinValue/-1 choice is implementation-defined, while checked overflow and
the zero-remainder corner have explicit requirements; these remain separate
from the deliberately restricted optimization:
https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions

Division correctness follow-up (not optimization credit): checked signed
MinValue/-1 now calls the managed overflow path before machine/helper division.
Signed remainder by -1 returns zero before IDIV can fault. Checked multiplication
also guards its reverse-division overflow test before issuing that division.
Both widths pass the new exception fixture, including divide-by-zero behavior.
Unchecked quotient behavior is deliberately unchanged.

The tenth optimizer run initially had three failures. One was a real missed
fold: expanding a wholly constant division introduced a propagation chain
instead of producing the existing expected constant. Such raw IR operations
now remain with ConstantFold. Two peephole tests expected signed division to
stay untouched after the whole pipeline; their original peephole expectations
are retained before the signed-bias pass, with separate full-pipeline checks.
The corrected milestone is 52/52. Logs: build/perf-batch10-opt.log and
perf-batch10-opt-fixed.log. The full language run is active, not yet passed.
Runtime exception evidence: build/perf-checked-divide-regression.log.

Tenth milestone finished: 268 language tests and 52 optimizer checks passed.
Evidence: build/perf-batch10-language.log and perf-batch10-opt-fixed.log.
This covers 026/027 and exception repairs but predates byte-swap support.

Candidate 028 replaces the exact compiler-runtime ByteSwap(I64) helper with
a pure byte-reversal IR operation before inlining its eight-byte loop. I32
selection uses one BSWAP; I64 snapshots both input halves, byte-swaps each
and exchanges them. The encoder accepts only 32-bit registers; no memory
form or post-486 instruction is used. BSWAP preserves flags. Constant folding
handles both widths; the operation is eligible for ordinary integer reuse
and safe invariant motion. Hand-written endian expressions are not yet
recognized by this helper-specific transformation.

Three alternating million-operation byte-swap runs: control
9.564287/10.140198/9.652710 ms, candidate
1.479568/1.467958/1.462348 ms. Every checksum matches the independent byte-loop
reference (-9,198,659,104,269,987,821). Evidence:
build/perf-byte-swap-comparison.log and build/perf-byte-swap/native.asm.
The actual program contains two BSWAP instructions for the I64 reversal.

The x86 suite passes with I32 vectors, I64 in-place/asymmetric/minimum-value
vectors and a flag-preservation allocation check. The language fixture checks
constants, edge words and round trips. Evidence: build/perf-byte-swap-x86.log
and perf-byte-swap-regression.log. That language executable predates the
later CSE/LICM allowlist addition, which awaits broader batch coverage.
The Intel i486 manual confirms BSWAP availability and 32-bit semantics
(build/reference-i486-programmer.txt, instruction section 26-35).

Follow-up to 028: Peephole cancels pairs of identical integer ByteSwap,
Not or modular Neg operations, restricted to I32/I64 and the same block.
The inner result must have a unique definition, and Defs.CanForward must
prove the original input can be reread without crossing a redefinition.
Floating-point negation is excluded. This is not additional optimization
count credit. Pattern references consulted (implementation is local):
https://gnu.googlesource.com/gcc/+/refs/heads/master/gcc/match.pd
https://www.llvm.org/doxygen/InstCombineCalls_8cpp_source.html

Focused unary legality checks and optimizer_byte_swap pass with the isolated
build/compiler-unary compiler. The new roundtrip mode in byte-swap.cor checks
against the original unswapped values. Three alternating ten-million runs:
control 15.063392/14.528877/14.457845 ms; candidate
14.417869/15.858662/14.786721 ms. All checksums are
8566033409381728000. There is no demonstrated host timing improvement.
Disassembly has two BSWAPs instead of six: four redundant instructions were
removed from the roundtrip path, while the ordinary reversal path retains
two. Total code shrinks 10973 to 10965 bytes. Evidence:
build/perf-unary-comparison.log and build/perf-unary-language.log.
Broader coverage remains part of the next grouped milestone.

Current-source profile refresh (compiler-unary, immutable-metadata libraries):
4,000 X25519 calls take 2.155756638 seconds fresh (47 collections) and
2.042332884 seconds reused (zero collections). SHA-short and fresh HMAC
still have zero timed collections. These single observations locate work,
not a controlled claim of improvement. An 8,000-call fresh profile has
824 samples, none lost: Multiply 32.52%, CarryRound 26.46%.
Evidence: build/perf-current-audit-results.log and
build/perf-current-audit/profile.txt. Fresh workspace allocation remains a
separate target; instruction-level work still dominates CPU samples.

Rejected consumer-range expansion: applying the existing unsigned bit-bound
analysis to single-definition Add/Mul/Copy consumers eliminated a zero-upper
temporary in CarryRound (343 to 342 bytes), but total crypto code grew
58315 to 58319 bytes and all alternating X25519 runs regressed. Control:
2.029804089/2.006927529/2.032393593 seconds; candidate:
2.098330838/2.086950522/2.050525041. All vectors valid, zero collections.
The selector change was removed; no optimization credit. Evidence:
build/perf-bounded-consumers-comparison.log. Preserve the new carry and
mutable-source boundary fixture for future changes to this analysis.

Candidate 029 addresses the full-frame-budget case: classify natural loops
using dominating backedges and prioritize their blocks by nesting depth,
with stable ties. Only the order in which allocations compete for slots
changes; instructions do not move. EH/indirect-entry functions retain the
original ordering. The 4 KiB frame budget, per-object cap, owner renewal,
capture and loop liveness proofs remain unchanged. This does not predict
actual trip counts; a rarely executed loop can still be prioritized.

A bounded fresh-owner inlining hint exposes allocations hidden inside
initialization calls. It follows at most eight same-block copy/width wrappers
back to a unique allocator call in the first argument. Eligible bodies are
at most 320 instructions and must themselves allocate. Existing recursive,
address-taken, EH and growth exclusions remain. This profitability hint is
not permission to stack-allocate: normal ownership analysis decides that.
Count it as enabling work, not a separate optimization candidate.

The original X25519 parent workspace was already on the stack while its
out-of-line constructor allocated children. Inlining alone reduced timed
collections from 47 to 37 per 4,000 operations, without a reliable elapsed
gain. Its IR consumed exactly 4,096 promoted-object bytes, leaving later
loop allocations on the heap. Budget prioritization eliminates the timed
collections while retaining the same cap. Three alternating fresh runs:
control 2.089008212/2.088190086/2.085915265 seconds, candidate
2.029780587/2.037134442/2.029673786 seconds (about 2.4-2.8% faster).
All RFC vectors match. Reused X25519, SHA-short, fresh/reused HMAC also pass
with zero timed collections. Crypto code grows 58,315 to 63,163 bytes;
record that size cost explicitly. These are Linux-host results, not 486
hardware timing. Zero collections alone does not prove zero allocated bytes.

Evidence: build/perf-fresh-owner-comparison.log,
build/perf-hot-owners-comparison.log and
build/perf-current-audit/{optimized,fresh-owner,hot-owners}.ir.
The fresh-owner lifetime fixture passes. The budget test initially used
I64 addresses where x86 IR requires I32; it was corrected with the ordinary
pointer truncation, preserving all assertions. Eleventh grouped optimizer,
x86 and full language checks are being recorded in build/perf-batch11-*.

Eleventh milestone partial results: 55 optimizer checks pass after the
fixture pointer-width correction, and the x86 suite reports ALL CHECKS
PASSED. Logs: build/perf-batch11-opt-fixed.log and perf-batch11-x86.log.
The full language run is still active; no complete milestone verdict yet.

Fresh-owner context analysis is now skipped when normal small/single-caller
or constant-branch rules already decide eligibility, or the hard growth
budget will reject the call. The rebuilt crypto ELF is byte-for-byte identical
to the hot-owner version (cmp exit 0), so this avoids compiler work without
changing generated runtime code. Candidate build: build/compiler-owner-cost;
output: build/perf-current-audit/crypto-owner-cost. Its recorded compile time
is 5.07 seconds, but no controlled baseline timing establishes a speedup.

Candidate 019 follow-up: complementary shifts are accepted in either
instruction order for I32 and the masked/shared-mask I64 forms. Arithmetic
right shifts qualify only for the proven-zero-upper I64 forms, never signed
I32. Existing count, type, single-definition/use and source-alias guards
still apply. This does not add another candidate number.

New ChaCha20 profiling exposed the missed left-first idiom: zero native
rotates before, 64 ROR instructions after. Code shrinks 23,108 to 22,468 bytes.
Three alternating million-block fresh comparisons (seconds):
0.380773477/0.386634159/0.378213079 before,
0.342587161/0.345765279/0.341349914 after (about 9.7-10.6% faster).
Reuse before: 0.396682743/0.399145628/0.401184157; after:
0.390546260/0.365641682/0.360480824 (more variable, all faster).
Every RFC block matches; both modes have zero timed collections.
Evidence: build/perf-left-rotate-comparison.log and
build/perf-current-audit/chacha{,-left-rotate}.asm.
optimizer_left_rotates passes edge values, counts 1/7/31, dirty upper words,
signed-I32 exclusion and shared shift intermediates. Evidence:
build/perf-left-rotate-regression.log. This extension postdates the compiler
used for the still-running eleventh language milestone.

Eleventh milestone is now complete: 270 language tests, 55 optimizer checks,
and x86 ALL CHECKS PASSED. Compiler snapshot: build/compiler-hot-owners.
The later left-first rotate and byte-packing work is not covered by that
snapshot's verdict. Logs: build/perf-batch11-language.log,
perf-batch11-opt-fixed.log and perf-batch11-x86.log.

Candidates 030/031 use bounded, all-definition unsigned range analysis.
An AND is a copy only when its constant mask retains every possible set bit.
No loads, bounds checks or exception paths move. A constant I64 left shift
uses one low-word SHL plus a zero upper word only when the entire result
fits 32 bits; counts are masked to six bits, and high-producing cases retain
the ordinary wide path. The same shift proof reaches bitwise consumers.
UnsignedBits keeps its recursion/work limits and includes every assignment;
signed extensions and unknown values do not gain nonnegative assumptions.

Each ChaCha ReadLittleWord helper shrinks 172 to 125 bytes: four redundant
byte masks and the high-word shift/combine work disappear, while MOVZX byte
loads and bounds checks remain. Total cipher code shrinks 22,468 to 22,345.
Whole-cipher timings are mixed (one of three pairs regressed), so no reliable
ChaCha throughput gain is claimed for this follow-up.

The focused ten-million-word packing workload checks against independently
accumulated unsigned PRNG values, checksum 21473717823570240. Alternating
control 142.522824/142.873076/148.381951 ms; candidate
139.860753/133.522169/136.277421 ms. All valid, about 1.9-8.2% faster;
code 9,909 to 9,865 bytes. This includes byte stores and generation, not just
the reader. Evidence: build/perf-packing-focused-comparison.log,
perf-byte-packing-comparison.log and perf-byte-packing-regression.log.
The regression covers signed exclusions, narrowed masks, source snapshots,
and counts 8/16/24/32/63/64. Broad validation remains the next milestone.

Size-first investigation requested by the user: --opt-size uses SmallBody=8,
ConstantBranchBody=80 and ordinary GrowthLimit=1200, versus 40/160/4000.
Fresh-owner bodies remain capped at 320, with a separate 4000-instruction
growth allowance. All ordinary legality/exceptions/recursion/address guards
remain. This is policy tuning, not multiple new optimization-count entries.
PERF_SIZE=1 selects it for tests/perf/build.sh; default behavior is unchanged.

The first compact policy halved crypto code but restored 47 collections per
4,000 fresh X25519 operations: the tight caller cap blocked constructor
exposure. The separate fresh-owner allowance repairs that regression.
Revised, same-source 486 builds (code bytes, not total ELF bytes):
crypto 63123 -> 35817 (43.3% smaller), ChaCha 22333 -> 13660 (38.8% smaller),
allocation workload 39917 -> 9712 (75.7% smaller). Fresh X25519 again has zero
timed collections. SHA/HMAC, ChaCha vectors and all nine allocation workloads
pass; local cases stay at zero collections, retained cases at 73/74.
Fresh-owner cap unit checks and owned-graph runtime checks also pass.

Host timing remains a tradeoff, not the size-selection gate: fresh X25519
takes about 3.11 seconds per 4,000 in the revised size profile, versus
2.10-2.11 seconds in the default profile. SHA is 61.34 ms/100,000 and fresh
HMAC 204.04 ms/100,000, both zero collections. These diagnostic runs do not
establish confidence intervals or actual 486 performance.

Multiply's code shrinks 5780 -> 4875 bytes. Its disassembly has 611 lines
mentioning EBP instead of 685, but 67 call sites instead of 13. These static
counts exclude callees and do not measure total dynamic bus traffic. Keep
both artifacts for 486 comparison; do not silently trade allocation traffic
for smaller callers or use a modern-host slowdown alone to reject compact
code. No real-486 result or default-policy change is claimed yet.

Evidence: build/perf-size-policy-{compile,comparison,allocation,guards}.log,
build/perf-size-policy-lifetimes.log, build/perf-size-owned-results.log,
build/perf-size-owned-other.log, and build/perf-size-{default,owned}/crypto.asm.
The revised profile compiler is build/compiler-size-owned. The immediately
preceding bounded-sum/positive-shift prototype also exists in both comparison
builds; its dedicated boundary/profitability audit remains pending and it is
not yet added to the accepted-candidate count.

Twelfth grouped milestone started: tests/lang/run.sh --opt-size with the
preserved build/compiler-size-owned/bin/corc/release/corc executable and
CORC_LIB pointing explicitly to lib. Compiler DLL SHA256:
3e585468f51be199e8b382876cbc3b6bc9ef240d149352056cae0cc4fc94b9e6.
The runner now forwards --opt-size as an argument without changing expected
outputs, warning policy or the default mode. Logs are
build/perf-batch12-size-language.log, perf-batch12-opt.log and
perf-batch12-x86.log. No full size-mode verdict until the run terminates.

The twelfth milestone has now terminated successfully: 272 language tests,
55 optimizer checks and x86 ALL CHECKS PASSED. Its snapshot predates the
argument-word inlining policy and IntegerBitFacts foundation. Do not apply
that verdict to those later changes.

Call-aware compact-policy checkpoint: charging a bounded allowance for wide
arguments reduces Multiply's direct call sites from 67 to 28. The two product
helpers it retains contain no further calls. Code grows 35817 -> 37027 bytes,
still 41% below the default comparison. Two paired fresh-X25519 runs at
4,000 operations: 3.177139407/3.150360048 seconds before,
2.412463215/2.366684465 seconds after, zero collections and valid vectors.
Argument-width/growth-limit unit checks pass. Evidence:
build/perf-size-call-cost-comparison.log, perf-call-cost-guards.log and
build/perf-size-call-cost/crypto.asm. A further allowance-four experiment
builds at 41327 code bytes but has not been benchmark-accepted; it remains
experimental as attention moves to the user-requested large batch.
