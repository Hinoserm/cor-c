# x86 CPU and instruction support

## Target selection

The default remains `--cpu=486` with x87 floating point. `--cpu` selects allowed
instructions; `--tune` selects a tuning model without granting new instructions.
Tuning-specific cost models are not implemented yet.

| CPU | Default floating point | Default extensions |
| --- | --- | --- |
| 386 | None; optional `--fpu=387` | None |
| 486 | x87 | None |
| pentium | x87 | Pentium system instructions |
| pentium-mmx | x87 | MMX |
| k6 | x87 | MMX |
| k6-2, k6-3 | x87 | MMX and base 3DNow! |
| k6-2+, k6-3+ | x87 | MMX and extended 3DNow! |

The parser also accepts `i386`, `i486`, `586`, `i586`, `pentium_mmx`,
`k6-iii`, `k6-iii+`, and the `686`/`i686`/`pentium-pro` names. A parsed profile
does not imply complete backend support for that processor.

`--disable-mmx` also disables dependent 3DNow! instructions.
`--disable-3dnow` leaves MMX available. Explicit `--enable-mmx` and
`--enable-3dnow` override the base CPU extension selection.
`--fpu=none` disables x87/MMX/3DNow! and rejects an explicit conflicting
extension enablement. Native compilation with this option remains blocked
until software arithmetic, conversions, runtime code and ABI handling are ready.
Native 386 compilation also remains blocked pending the complete instruction
and runtime-atomic audit. These diagnostics must not be removed prematurely.

## Implemented instruction work

The assembler has base MMX and 3DNow! instruction families, plus the five
extended 3DNow! operations selected by K6 plus profiles. It supports x87
arithmetic, comparisons, constants, real/integer/BCD loads and stores, control
and environment operations, and debug/test-register moves. Test-register
access is restricted by CPU model.

Additional integer support includes bit scans, bit-test/change instructions,
double shifts, BSWAP, XADD, CMPXCHG, CMPXCHG8B, cache invalidation and INVLPG.
The assembler rejects 486-only instructions under a 386 profile and validates
LOCK against writable memory forms rather than emitting arbitrary prefixes.

The compiler's byte-swap lowering uses BSWAP on 486 and newer CPUs. Under a
386 profile it uses shifts, masks, OR and rotation, including in-place 64-bit
byte swaps. This is one completed lowering change, not complete 386 support.

## Verification

Automatic selection now includes bounded packed-memory operations, integer
arithmetic/bitwise groups, shifts, word multiplication and dot products, and
range-proven 3DNow conversions/arithmetic. See
[optimization results](X86-OPTIMIZATION-RESULTS.md) for precise coverage,
profitability restrictions and measured outcomes. This does not imply every
instruction is already selected automatically.

- 627 encoding/profile checks compare with GNU assembler, including 16-bit
  and 32-bit encodings, malformed operands and feature exclusions.
- Seven result checks execute COR-C#-assembled bytes under QEMU TCG with an
  Athlon model: x87 arithmetic, saturated MMX addition, packed shifts,
  EMMS/x87 transition, 3DNow! conversion/addition, lane swapping, and
  prefetch/FEMMS/x87 transition.
- The backend suite runs seven configurations: default 486, 386+387 selection,
  native MMX, explicitly excluded MMX, K6-2 TCG, K6-plus TCG, and kernel state
  ownership. It includes byte swaps, buffer boundaries, wrapping arithmetic,
  shift counts/sign extension, dot products, signed zero, and saturating casts.
  These native-host checks do not prove an entire executable is 386-safe.

Run the focused instruction and backend milestones from the repository root:

```sh
tools/build/bin/managed/Release/net10.0/build --file compiler/tests/arch/x86/isa/corsac.build
tools/build/bin/managed/Release/net10.0/build --file compiler/tests/arch/x86/corsac.build
```

QEMU execution establishes the tested instruction semantics, not K6 timing.
Native-host packed measurements are recorded separately; emulator timing is
not presented as real K6 performance.

## Projects and link-time optimization

Native `corc project` accepts the CPU/FPU flags. SDK-style projects can set
`CorCCpu`, `CorCTune`, and `CorCFpu` using ordinary MSBuild properties; command
line selections override them. The standard .NET toolchain ignores these
native-only properties. Runtime/source units use the same selection, which
also participates in incremental cache identities.

`.corsac.cpu` records the `CCPU2` code-generation contract, including explicit
extension exclusions and whether automatic packed operations are allowed by
the execution environment. Owned managed objects require this metadata through
their ABI contract. LTO restores each unit's selection and rejects changed
permissions. Rebuild older IR units without this contract before using LTO.
`corlink --cpu=...` checks declared input requirements against explicit output
permissions, before and after LTO. Foreign objects without metadata are not
certified by these checks.

Freestanding builds do not implicitly borrow MMX/x87 state. Explicit assembly
remains the responsibility of code that establishes and preserves that state.

## Remaining work

- Complete instruction/profile coverage and compiler-emitter auditing.
- Broaden profitable automatic selection and source-level recognition while
  preserving state transitions and language floating-point semantics.
- Implement software floating point and complete the 386 runtime audit.
- Add Pentium FDIV detection/correction and forced-path regression checks.
- Measure native-host-supported optimizations and retain K6 performance
  acceptance for actual hardware. F00F mitigation is owned by the OS task.
