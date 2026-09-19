# x86 automatic instruction selection and measurements

## Current implementation

The optimizer selects these paths automatically when the CPU enables their
instructions and the implementation proves its input/storage requirements.
There is no enable switch per optimization. `--disable-mmx` and
`--disable-3dnow` exclude the corresponding instruction families.

| Source/IR pattern | Selected instruction families | Important restrictions |
| --- | --- | --- |
| Repeated fixed-size copies and zeroing | MOVQ, PXOR, EMMS/FEMMS | Proven frame storage; exact tails; bounded size and loop profitability |
| Adjacent wrapping integer operations | PADDB/W/D, PSUBB/W/D, PAND, POR, PXOR | Contiguous independent frame lanes; no escaping intermediate values |
| Word multiplication | PMULLW, PMULHW | Correct low-word truncation or signed high-word result |
| Signed pairwise word dot products | PMADDWD | Includes the wrapping `(-32768 * -32768) * 2` boundary |
| Equality and signed greater-than masks | PCMPEQB/W/D, PCMPGTB/W/D | All-bits masks only; matching extension for narrow equality; signed narrow greater-than inputs |
| Word/dword shifts | PSLLW/D, PSRLW/D, PSRAW/D | C# shift-count masking and source sign extension preserved |
| Rounded unsigned-byte averages | PAVGUSB | Proven unsigned byte inputs and exact round-up expression |
| Rounded signed high-word products | PMULHRW | Proven signed word inputs and exact rounding expression |
| Word-to-float conversions | PUNPCKLWD, PI2FD, PI2FW | All 16-bit inputs are exactly representable; plus extension gated |
| Floating arithmetic after word conversion | PFADD, PFSUBR, PFMUL, PF2ID | Proven finite ranges and matching signed-zero/rounding/conversion behavior |

Packed regions close before subsequent calls or x87 operations. Freestanding
code does not gain implicit packed-register use: an interrupted user context
may own those registers. The object/LTO contract preserves this distinction.

The bounded loop-profitability analysis requires a growing memory region to
execute on every loop backedge, not merely appear in a conditional loop arm.
It uses at most 32 KiB of dominance bits per function and conservatively avoids
growing bulk-memory code in larger functions. The 32-byte zero case can still
be selected outside loops because it replaces larger unrolled scalar stores.

## Floating-point correctness

General 3DNow floating arithmetic cannot replace arbitrary C# arithmetic:
special values, underflow and approximate reciprocal refinement need separate
proofs. Current floating selection relies on bounded integer inputs and exact
conversion properties, rather than a fast-math exception. The processor's
documented semantics are the authority. See the
[AMD 3DNow manual](https://www.amd.com/content/dam/amd/en/documents/archived-tech-docs/programmer-references/21928.pdf)
and [AMD extension manual](https://www.amd.com/content/dam/amd/en/documents/archived-tech-docs/programmer-references/22466.pdf).

Boundary testing also exposed the old scalar FISTP overflow behavior. Scalar
conversion now maps NaN to zero and saturates signed/unsigned 32/64-bit results,
including the upper half of UInt64, matching
[current .NET conversion behavior](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/9.0/fp-to-integer).

## Native benchmark snapshot, 2026-09-18

Host: AMD Ryzen 9 9950X, Linux. Both builds select Pentium-MMX; the scalar
control explicitly disables MMX. These are measurements of generated native
Linux code, not emulator timing. They are not K6 hardware measurements.

Each case performs one warmup and five timed processes; the reported value is
the median. Process startup is included. Memory cases run 30 million operations
per process; arithmetic cases run 3 million. The host is not an isolated
benchmark machine. Code bytes include the complete generated benchmark text,
not only the loop body. Every run checks its result.

| 64-byte batch | Scalar ns/op | Packed ns/op | Scalar text bytes | Packed text bytes |
| --- | ---: | ---: | ---: | ---: |
| Copy | 3.526 | 1.170 | 294 | 366 |
| Zero | 1.644 | 0.997 | 288 | 333 |
| Byte add | 9.147 | 1.752 | 1317 | 402 |
| Word add | 4.777 | 1.786 | 837 | 402 |
| Dword add | 2.411 | 1.776 | 517 | 402 |
| Byte subtract | 9.691 | 1.732 | 1317 | 402 |
| Word multiply | 4.646 | 1.723 | 869 | 402 |

Full initial data: [packed measurements](../tests/benchmarks/results/x86-packed-20260918.csv).
Some small dword-shift candidates were slightly slower with EMMS included;
the selector now keeps groups below 64 bytes scalar on that transition path.
Do not interpret instruction availability as a profitability guarantee.

## Real crypto checks

The existing X25519 and ChaCha source workloads compile and pass their known
answer vectors in both variants. Initial five-pair X25519 measurements were
effectively unchanged: roughly 521 microseconds per reused-workspace operation.
ChaCha did not establish a reliable gain. Only setup/initialization code changed
substantially in these builds, so packed microbenchmark gains must not be
presented as an SSH throughput or latency improvement.

That result led to the tighter memory-region profitability policy above.
After that policy and immutable-array-length propagation, five new paired runs
measured median X25519 times of 488.411 microseconds scalar and 489.006
microseconds packed: still no demonstrated gain. ChaCha medians were 379.018
and 386.014 nanoseconds per block, respectively; run variation precludes a
speedup claim. All known-answer checks passed with no timed-region collections.
Packed X25519 emitted code fell from the earlier 67,131 bytes to 62,287 bytes;
the current scalar control is 62,431 bytes. Both current ChaCha variants emit
19,941 bytes. These builds precede the new in-place arithmetic extension.

## In-place arithmetic

Exact same-offset frame aliases now support packed arithmetic; shifted aliases
remain scalar to preserve dependencies between lanes. A 3-million-iteration,
five-sample XOR benchmark found 32-byte groups slower (1.337 to 2.114 ns),
64-byte groups approximately even (2.462 to 2.456 ns), and 128-byte groups
faster (4.597 to 3.469 ns). The selector therefore keeps in-place dword groups
below 64 bytes scalar on the EMMS path. The FEMMS path remains separately gated
and correctness-tested, without a native timing claim on this host.

Raw data: [expanded candidate measurements](../tests/benchmarks/results/x86-packed-20260918-expanded.csv)
and [post-threshold in-place measurements](../tests/benchmarks/results/x86-inplace-20260918-costed.csv).
The latter confirms the 32-byte candidate stays scalar with identical text size.
Absolute timing varied between runs on this non-isolated host; compare paired
controls within a run rather than treating separate snapshots as speedups.

## Reproduction and remaining acceptance

### Runtime byte comparison

The string/byte comparison runtime now skips aligned equal 32-bit words and
peels at most three leading bytes when both inputs have matching alignment.
Differently aligned inputs retain byte loads. A differing word falls back to
unsigned byte order, preserving endianness-independent results and exact
requested bounds. Runtime-sized x86 copies also transfer complete words before
their exact byte tail; no MMX/x87 state is borrowed by either change.

The statically linked `memory-compare` benchmark compares the real runtime with
a byte-at-a-time source reference, not an archived binary of the old runtime.
Five paired native runs of 200,000 comparisons gave these median nanoseconds:

| Equal span | Runtime | Byte reference |
| --- | ---: | ---: |
| 32 bytes, aligned | 7.621 | 22.234 |
| 128 bytes, aligned | 27.457 | 99.127 |
| 128 bytes, both offset by one byte | 26.622 | 98.915 |
| 512 bytes, aligned | 95.530 | 377.741 |

Late mismatches also passed and improved in this measurement. Full medians:
[runtime comparison results](../tests/benchmarks/results/runtime-compare-20260918.csv).
The host is not isolated and these are not legacy-CPU timing estimates.
Reproduce with `CPU=pentium-mmx PERF_STATIC=1 BENCHMARKS=memory-compare bash tests/benchmarks/build.sh`,
then run the produced binary with `200000` and `200000 reference` arguments.

Comparison-mask measurements use the same native warmup/five-sample harness.
For 64-byte batches, byte equality fell from 13.589 to 1.755 ns, signed word
greater-than from 8.635 to 1.728 ns, and signed dword greater-than from 3.439
to 1.916 ns. These are bounded frame-array microbenchmarks, not measurements
of an entire runtime or crypto workload. Raw data:
[equality](../tests/benchmarks/results/x86-equality-20260918.csv) and
[greater-than](../tests/benchmarks/results/x86-comparison-20260918.csv).

```sh
compiler/bin/managed/Release/net10.0/corc build --file compiler/tests/arch/x86/corsac.build benchmark
```

For actual OS crypto sources, the benchmark builder accepts `CPU`, `OUT`,
`PERF_DISABLE_MMX`, `BENCHMARKS`, and `CRYPTO_ROOT` (the read-only OS `os/lib`
directory). It records source hashes and disassembly. No OS checkout is edited.

Broader pattern coverage, source-level uptake, alias/rejection coverage,
real-application performance, complete per-instruction execution coverage,
and real legacy-hardware performance remain separate gates. Unsupported host
instructions are exercised under TCG for correctness, not assigned fabricated
native benchmark numbers. Privileged/system instructions are not speculative
substitutes for ordinary program operations.
