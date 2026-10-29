# x86 CPU and instruction support

## Target selection

The default remains `--cpu=486` with x87 floating point. `--cpu` selects the
allowed instructions and the processor the code is optimized for; there is no
separate tuning switch. Newer processors are served by run-time detection and
patching in the runtime and libraries.

A profile is a set of instruction features (`X86Feature`), not a place on
one line of generations: the K6-2 has 3DNow! and no CMOV, the Pentium Pro
CMOV and no MMX. Each model is its predecessor plus what it added:

| CPU | Default floating point | Features |
| --- | --- | --- |
| 386 | None; optional `--fpu=387` | None |
| 486 | x87 | 486 (BSWAP, XADD, CMPXCHG, INVLPG) |
| pentium | x87 | + Pentium (CPUID, RDTSC, RDMSR/WRMSR, CMPXCHG8B) |
| pentium-mmx | x87 | + MMX |
| pentium-pro, 686 | x87 | pentium + CMOV, FCMOV/FCOMI/FUCOMI |
| pentium2 | x87 | + MMX |
| pentium3 | x87 | + MMX extensions, SSE |
| pentium4 | x87 | + SSE2 |
| k6 | x87 | pentium + MMX |
| k6-2, k6-3 | x87 | + 3DNow! |
| k6-2+, k6-3+ | x87 | + the five 3DNow! extensions |
| athlon | x87 | k6-2+ + MMX extensions, CMOV, FCMOV/FCOMI/FUCOMI |
| athlon-xp | x87 | + SSE |
| athlon64 | x87 | + SSE2 |
| 6x86mx | x87 | pentium-mmx + CMOV, without FCMOV/FCOMI |
| geode-lx | x87 | k6-2+ + MMX extensions, CMOV, FCMOV/FCOMI/FUCOMI: an athlon's set without SSE |

The MMX extensions are AMD's name for the integer half of SSE (PAVGB, PMINUB,
PMAXSW, PSHUFW, PMOVMSKB, MOVNTQ, PREFETCHh, SFENCE), which the Athlon has
without SSE itself. `athlon64` is a K8 running 32-bit code with an x87; the
name `k8` belongs to the long-mode target, whose floating point is SSE2.

The parser also accepts `i386`, `i486`, `586`, `i586`, `pentium_mmx`, `i686`,
`pentium-ii`, `pentium-iii`, `k6-iii`, `k6-iii+`, `k7`, `athlon-tbird`,
`athlon-4`, `athlon-mp`, `m2`, `mii` and `cyrix-mii` for Cyrix's 6x86MX,
whose CMOVcc comes without the x87's FCMOVcc and FCOMI, and `geodelx` for
AMD's Geode LX (AMD 33234: FCMOVcc and FCOMI in Table 8-29; no SSE registers,
no PAE, no long NOP; CPUID family 5, model 10). A parsed profile does not imply complete backend
support for that processor. The code generator selects FUCOMIP for float
compares where the profile has FCMOV/FCOMI, CMOVcc for a small choice where
it has CMOV (IfConvert), and CVTTSS2SI (SSE) or CVTTSD2SI (SSE2) for an
in-range float or double cast to `int`.

SSE's state is the operating system's to enable (CR4.OSFXSR) and save, so only
hosted code uses it, and that code runs only under an operating system that
has enabled it, as Linux has; freestanding code keeps the x87 under every
profile, and the emitted-code audit refuses an SSE instruction there. A cast to
`long`, and `uint`'s 64-bit store, stay on the x87: 32-bit SSE has no 64-bit
integer result.

`--enable-` and `--disable-` take `mmx`, `3dnow`, `cmov`, `sse` and `sse2`.
A disable takes what depends on it too: no MMX means no 3DNow!, 3DNow!
extensions or MMX extensions; no 3DNow! no 3DNow! extensions; no CMOV no
FCMOV/FCOMI; no SSE no SSE2. An enable brings what it needs: 3DNow! brings
MMX, SSE2 brings SSE, and SSE its MMX extensions. A disable wins over an
enable that would bring back what it took. An explicit enable extends
permission beyond the model and is the caller's promise that the processor
has the extension; the 3DNow! extensions have no switch and come only with a
model. `--fpu=none` disables MMX, 3DNow!, the MMX extensions, FCMOV/FCOMI and
SSE, keeps integer CMOV, and rejects an explicit conflicting enablement.
Native compilation with this option remains blocked until software
arithmetic, conversions, runtime code and ABI handling are ready. Native 386
compilation also remains blocked pending the complete instruction and
runtime-atomic audit. These diagnostics must not be removed prematurely.

## Implemented instruction work

The assembler has base MMX and 3DNow! instruction families, plus the five
extended 3DNow! operations selected by K6 plus profiles and the Athlon. It
supports x87 arithmetic, comparisons, constants, real/integer/BCD loads and
stores, control and environment operations, and debug/test-register moves.
Test-register access is restricted by CPU model.

The sixth-generation additions are CMOVcc (every condition spelling, 16-, 32-
and 64-bit), FCMOVcc, FCOMI/FCOMIP/FUCOMI/FUCOMIP, PREFETCHNTA/T0/T1/T2 and
SFENCE, each refused unless the selected profile has it. `.runtime mmx`,
`.runtime cmov` and `.runtime sse` (or a list of them, ended by
`.runtime none`) allow those families in a routine reached only after the
program has checked the processor itself.

Additional integer support includes bit scans, bit-test/change instructions,
double shifts, BSWAP, XADD, CMPXCHG, CMPXCHG8B, cache invalidation and INVLPG.
The assembler rejects 486-only instructions under a 386 profile and validates
LOCK against writable memory forms rather than emitting arbitrary prefixes.

The compiler's byte-swap lowering uses BSWAP on 486 and newer CPUs. Under a
386 profile it uses shifts, masks, OR and rotation, including in-place 64-bit
byte swaps. This is one completed lowering change, not complete 386 support.

Floating-point comparisons use FUCOMIP under every profile with FCOMI (the
Pentium Pro and later, the Athlon), which leaves the result in EFLAGS and
keeps EAX free; every other profile keeps FUCOMPP, FNSTSW AX and SAHF.

## Emitted-code audit

Every function the x86 backend encodes is first checked, instruction by
instruction, against one requirement table (`compiler/src/arch/x86/backend/Isa.cs`):
BSWAP, XADD, CMPXCHG and INVLPG need a 486; CPUID, RDTSC, RDMSR and WRMSR a
Pentium; the MMX and 3DNow! families their extensions. An instruction the
`--cpu` profile lacks stops the compile with an error naming the function, the
instruction and what it needs, whichever pass chose it and whether the unit is
compiled whole or regenerated at the link. Machine intrinsics a kernel names
after its own processor check (`Sys.Cpuid`, `Sys.ReadTsc`, `Sys.ReadMsr`,
`Sys.WriteMsr`, `Sys.Invlpg`) are let through, as code after `.runtime mmx` is
in the assembler. MMX-register instructions are refused in code selected
without the hosted ABI's x87-state ownership, whatever the processor. The
CMPXCHG in the store sequences the backend writes as bytes
(`--store-sequences`) is checked against the same table.

## Verification

Automatic selection now includes bounded packed-memory operations, integer
arithmetic/bitwise groups, shifts, word multiplication and dot products, and
range-proven 3DNow conversions/arithmetic. See
[optimization results](X86-OPTIMIZATION-RESULTS.md) for precise coverage,
profitability restrictions and measured outcomes. This does not imply every
instruction is already selected automatically.

- 878 encoding/profile checks compare with GNU assembler, including 16-bit
  and 32-bit encodings, malformed operands and feature exclusions.
- Seven result checks execute COR-C#-assembled bytes under QEMU TCG with an
  Athlon model: x87 arithmetic, saturated MMX addition, packed shifts,
  EMMS/x87 transition, 3DNow! conversion/addition, lane swapping, and
  prefetch/FEMMS/x87 transition.
- The backend suite runs ten configurations: default 486, 386+387 selection,
  native MMX, explicitly excluded MMX, native Pentium Pro, K6-2 TCG, K6-plus
  TCG, and kernel state ownership, plain Pentium TCG, and Pentium-MMX TCG.
  The latter two use `pentium` and `pentium,+mmx` respectively, since QEMU has
  no separate P55C model. They execute generated code, including packed/x87
  state transitions where MMX is enabled. It includes byte swaps, buffer
  boundaries, wrapping arithmetic, shift counts/sign extension, dot products,
  signed zero, and saturating casts. These native-host checks do not prove an
  entire executable is 386-safe.
- `sh tests/integration/x86-packed-source.sh` compiles ordinary array source,
  verifies packed instruction selection/exclusion, and executes nine CPU
  profiles with a statically compiled runtime, including all five K6 profiles.
  It also checks runtime byte comparison across every source/destination byte
  alignment, tails, equality and unsigned differing-byte order. Static linking avoids an
  unrelated host glibc loader that requires CMOV on this development machine.

Run the focused instruction and backend milestones from the repository root:

```sh
compiler/bin/managed/Release/net10.0/corc build --file compiler/tests/arch/x86/isa/corsac.build
compiler/bin/managed/Release/net10.0/corc build --file compiler/tests/arch/x86/corsac.build
```

QEMU execution establishes the tested instruction semantics, not K6 timing.
Native-host packed measurements are recorded separately; emulator timing is
not presented as real K6 performance.

## Projects and link-time optimization

Native `corc project` accepts the CPU/FPU flags. SDK-style projects can set
`CorCCpu` and `CorCFpu` using ordinary MSBuild properties; command
line selections override them. The standard .NET toolchain ignores these
native-only properties. Runtime/source units use the same selection, which
also participates in incremental cache identities.

`.corsac.cpu` records the `CCPU1` code-generation contract: the CPU and FPU
names, the feature set as four hex digits, and whether automatic packed
operations are allowed by the execution environment. A contract is valid only
when its features are what `--cpu`, `--fpu` and the switches give: the
dependencies hold, and an extension no switch grants stays with its model.
Owned managed objects require this metadata through their ABI contract. LTO
restores each unit's selection and rejects changed permissions.
`corc link --cpu=...` checks declared input requirements against explicit output
permissions, before and after LTO: each unit's features must be a subset of
the target's, code for an x87 needs one, and a long-mode unit a long-mode
target. A K6 link refuses an Athlon unit and a Pentium Pro one (CMOV); an
Athlon link takes any K6 unit. Foreign objects without metadata are not
certified by these checks.

Freestanding builds do not implicitly borrow MMX/x87 state. Explicit assembly
remains the responsibility of code that establishes and preserves that state.

## Remaining work

- Complete instruction/profile coverage. Each instruction family the backend
  newly selects (CMOV, FCOMI/FUCOMI, SSE) gets its row in the emitted-code
  audit's table in the change that selects it.
- Broaden profitable automatic selection and source-level recognition while
  preserving state transitions and language floating-point semantics.
- Implement software floating point and complete the 386 runtime audit.
- Add Pentium FDIV detection/correction and forced-path regression checks.
- Measure native-host-supported optimizations and retain K6 performance
  acceptance for actual hardware. F00F mitigation is owned by the OS task.
