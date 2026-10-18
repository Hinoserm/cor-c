# x86 CPU and FPU profiles

The default is 486+x87. `--cpu` permits the selected processor's complete ISA;
`--tune` selects cost preferences without adding instructions. Model names cover
386, 486, Pentium, Pentium MMX, Pentium Pro, Pentium II, Pentium III, Pentium 4,
K6, K6-2, K6-III, K6-2+, K6-III+, Athlon, Athlon XP, Athlon 64 (32-bit code
with an x87; `k8` is the long-mode target) and Cyrix's 6x86MX (CMOV without
FCMOV/FCOMI). Aliases such as `k6-3+`, `k7` and
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

`--fpu=none` is accepted on all models. The 386 additionally supports an optional
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

- The 386 with a 387 (`--cpu=386 --fpu=387`) compiles natively. It has XCHG and
  no other instruction that reads and writes memory as one: an atomic add,
  compare-exchange, and, or or exclusive or is a call of the runtime's
  `AtomicAdd386`, `AtomicCas386` or `AtomicBits386` (lowering), the read and
  the write between `Platform.AtomicsEnter` and `AtomicsLeave` -- on bare metal
  with interrupts off where the code's privilege level is at most EFLAGS.IOPL
  (`Sys.ReadCodeSegment`), else, and in a process, under one lock taken by
  XCHG (`Runtime.Atomics386`). The store-sequence compare-exchange stub
  (`__corsac_refcas`) makes the same choice itself and takes the same lock.
  BSWAP has its 386 form; INVLPG and the control-register moves
  are the source's own, made after its own processor check (Probed). The
  emitted-code audit refuses any other 486 instruction under 386, and knows a
  CR4 move needs a Pentium; the assembler refuses CR4 before the Pentium and
  the test registers in long mode, and moves the debug registers to and from
  64-bit registers there.

This is not complete CPU support. Remaining gates include full baseline/x87
assembler coverage, software floating point, FDIV correction, automatic
MMX/3DNow! selection, serialized CPU/FPU contracts through objects/LTO,
execution tests and measured cost decisions. Native compilation rejects
`--fpu=none` (the 386's default) rather than emitting invalid code: it needs
software floating point.
QEMU TCG can test semantics; it is not a K6 timing model. Host benchmarks apply
only to extensions the host advertises. F00F kernel mitigation is separately owned.
