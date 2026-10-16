# x86 CPU and FPU profiles

The default is 486+x87. `--cpu` permits the selected processor's complete ISA;
`--tune` selects cost preferences without adding instructions. Model names cover
386, 486, Pentium, Pentium MMX, Pentium Pro, Pentium II, Pentium III, Pentium 4,
K6, K6-2, K6-III, K6-2+, K6-III+, Athlon, Athlon XP and Athlon 64 (32-bit code
with an x87; `k8` is the long-mode target). Aliases such as `k6-3+`, `k7` and
`athlon-4` are accepted. Both `--cpu=name` and `--cpu name` forms are accepted
by the compiler and assembler.

A model is a set of features (`X86Feature`): 486, Pentium, MMX, 3DNow!, the
3DNow! extensions, CMOV, FCMOV/FCOMI, the MMX extensions, SSE and SSE2. Sets,
because the processors are not one line: the K6-2 has 3DNow! and no CMOV, the
Pentium Pro CMOV and no MMX. MMX, 3DNow!, CMOV, SSE and SSE2 have
`--enable-`/`--disable-` switches. Disabling one disables what depends on it
(3DNow! and the MMX extensions on MMX, SSE2 on SSE, FCMOV/FCOMI on CMOV).
Explicit enabling extends instruction permission beyond a model's baseline and
is the caller's promise that the actual processor provides that extension.

`--fpu=none` is required on all models. The 386 additionally supports an optional
387. No-FPU disables everything that shares the x87's registers or state (MMX,
3DNow!, the MMX extensions, FCMOV/FCOMI, SSE); explicitly enabling one of them
at the same time is an error. Software arithmetic, conversions and math-library
support are required before no-FPU native compilation can be accepted.

## Current implementation checkpoint

- CPU/FPU profile parsing and defaults are implemented, through the Pentium 4
  and the Athlon 64. A link checks each unit's features are a subset of the
  target's.
- The x86 assembler now lives under `compiler/src/arch/x86/assembler`.
- MMX and 3DNow! instruction tables, qword/MMX operands, immediate packed shifts,
  Pentium system instructions, CMPXCHG8B and AMD prefetch encoding are implemented.
- CMOVcc, FCMOVcc, FCOMI/FUCOMI and their popping forms, the SSE prefetches and
  SFENCE are encoded and gated by the profile's CMOV, FCMOV and MMX-extension
  features.
- Same-width floating moves can use integer registers without x87 conversion;
  signaling-NaN payloads are copied as bits. Memory displacement cloning retains
  index/scale/relocation metadata for both floating and 64-bit integer accesses.
- Instruction tests compare emitted bytes against GNU assembler in both modes.

This is not complete CPU support. Remaining gates include full baseline/x87
assembler coverage, 386-only code generation, software floating point, FDIV
correction, automatic MMX/3DNow! selection, serialized CPU/FPU contracts through
objects/LTO, execution tests and measured cost decisions. Native compilation
rejects unfinished no-FPU/386 selections rather than emitting invalid code.
QEMU TCG can test semantics; it is not a K6 timing model. Host benchmarks apply
only to extensions the host advertises. F00F kernel mitigation is separately owned.
