# CPU and instruction checks

Checks profile defaults/exclusions and compares 16/32-bit MMX, 3DNow!, x87,
386 bit operations, 486 atomics/cache and Pentium instruction bytes with GNU
assembler output. Equivalent ordering of independent size/LOCK prefixes is
normalized; opcodes, operands and immediate bytes must match. Rejects malformed
operands, unavailable ISA features and invalid LOCK operations.

A private boot sector assembled by COR-C# runs seven result checks under QEMU
TCG, covering x87, MMX saturation/shifts, 3DNow conversion/arithmetic, lane swaps,
prefetch and EMMS/FEMMS transitions back to x87. It uses the Athlon CPU model
for instruction semantics, not as a K6 timing model. KVM is explicitly disabled.
The execution timeout is 30 seconds; debug-exit identifies a failing case.
Source, image and logs remain in the reported temporary evidence directory.

Run from the repository root:

```sh
tools/build/bin/Release/net10.0/build --file compiler/tests/arch/x86/isa/corsac.build
```

Requires GNU `as`, `objcopy`, and `qemu-system-i386`. These checks do not establish
full CPU coverage, automatic optimizer selection, software floating-point
readiness, real-hardware timing, or an optimization-performance improvement.
