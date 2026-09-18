# x86 CPU and FPU profiles

The default is 486+x87. `--cpu` permits the selected processor's complete ISA;
`--tune` selects cost preferences without adding instructions. Model names cover
386, 486, Pentium, Pentium MMX, Pentium Pro, K6, K6-2, K6-III, K6-2+ and K6-III+.
Aliases such as `k6-3+` are accepted. Both `--cpu=name` and `--cpu name` forms
are accepted by the compiler and assembler.

MMX and 3DNow! are enabled by CPU capability, with explicit disable switches.
Disabling MMX disables dependent 3DNow! operations. Explicit enabling extends
instruction permission beyond a model's baseline and is the caller's promise
that the actual processor provides that extension.

`--fpu=none` is required on all models. The 386 additionally supports an optional
387. No-FPU disables shared-state MMX/3DNow! operations; explicitly enabling them
at the same time is an error. Software arithmetic, conversions and math-library
support are required before no-FPU native compilation can be accepted.

## Current implementation checkpoint

- CPU/FPU profile parsing and defaults are implemented.
- The x86 assembler now lives under `compiler/src/arch/x86/assembler`.
- MMX and 3DNow! instruction tables, qword/MMX operands, immediate packed shifts,
  Pentium system instructions, CMPXCHG8B and AMD prefetch encoding are implemented.
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
