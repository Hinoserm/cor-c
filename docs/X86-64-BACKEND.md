# The x86-64 backend

`corc compile --target x86-64` (also `x86_64`, `amd64`, `x64`) builds a
long-mode Linux program: an ELF64 static executable for any x86-64 processor
from the first AMD64 (K8) on. Everything above the backend -- parser, binder,
lowering, the optimiser -- is shared with the i386 target; what differs is
here, in the linker, and in the word size the runtime is written against.

## The processor

The baseline is the K8: SSE2 for floating point, CMOV, the 64-bit
`CMPXCHG`/`XADD`/`XCHG`, `MFENCE`. Nothing from SSE3 on, no `CMPXCHG16B`, no
`POPCNT`/`LZCNT`, and no `LAHF`/`SAHF`, which the earliest K8 steppings did
not execute in 64-bit mode. `--cpu k8` is accepted and is the only choice;
the i386 profile flags (`--fpu`, MMX, 3DNow!) do not apply. Objects carry the
code-generation contract `k8`/`sse2`.

## Layout

Eight-byte words, native 64-bit integers. An object header is two words
(vtable, sync word, 16 bytes); an array header 24 bytes with the count at 16;
a type descriptor twelve words before its vtable. Lowering already scaled
every one of these by the word; the two places that had not -- tuple layout
and the main thread block's size -- now do.

## Calling convention

System V AMD64 for every function: integer arguments in RDI, RSI, RDX, RCX,
R8, R9, floating-point ones in XMM0..XMM7, the rest on the stack, eight bytes
each, pushed with a pad beneath an odd count so RSP is 16-aligned at every
call. Results in RAX or XMM0. RBX, RBP, R12..R15 are preserved; every other
general register and all sixteen XMM registers are not. The process entry is
entered with no return address, so its prologue realigns (`and rsp, -16`).

## Frame

```
[RBP+16..]  incoming stack arguments
[RBP+8]     return address
[RBP+0]     caller's RBP
[RBP-N..]   IR frame slots, then spill slots (eight bytes each)
below       the callee-saved registers this function uses
```

The saved registers are stored and restored with moves at fixed RBP offsets,
so the frame is one `sub rsp` whose size keeps RSP 16-aligned. A function
that makes a system call saves all five callee-saved registers: the collector
is entered through one, and a caller's reference may live only in one of
them (GcRoots.Enter in runtime/src/core/gc.cor).

## Selection, allocation, encoding

`compiler/src/arch/x64/backend`:

- **Select.cs** -- every value one register: I32 in the 32-bit forms (which
  zero the upper half), I64 and addresses in the 64-bit forms, F32/F64 in XMM.
  Named things are reached RIP-relative; a symbol's address is `lea`.
  Division uses the hardware and its trap, as on i386. Float-to-integer
  conversion follows .NET's saturating rule from the IEEE bits, exactly as
  the i386 backend does. The thread block is `fs:[0]`.
- **RegAlloc.cs** -- the i386 linear scan with two register classes. A value
  live across a call cannot sit in a caller-saved register; a float across a
  call is always spilled. Spill slots are eight bytes and reloads are whole
  words, so a sub-word integer definition is never folded into its slot (the
  slot's upper half would survive into the reload), and only constants a
  sign-extended imm32 can carry are folded as immediates.
- **Encoder.cs** -- REX, ModRM/SIB (with the SIB form for an absolute
  `[disp32]`, since mod 00 r/m 101 is RIP-relative in long mode), SSE2 with
  its mandatory prefix before REX, and branch relaxation to a fixed point.
- **Peephole.cs** -- jump threading, and dropping jumps the block order makes
  redundant.

## Objects and linking

A long-mode object says so in its ABI note (`.corsac.abi`: eight-byte
pointers, machine 0x8664, convention 2); the linker refuses to link it with
an i386 one. `--obj` writes ELF64 ET_REL with RELA relocations
(R_X86_64_64, _32, _PC32, _PLT32); the reader takes them back; GNU `ld -m
elf_x86_64` links them too. Executables are ELF64 ET_EXEC loaded at 0x400000,
the small code model: every symbol below 2 GiB.

## The runtime

The runtime keeps its own layouts -- the thread block, the collector's
block headers, free tree, chunk records, bitmaps, queues -- in machine words
through `WordSize.Bytes` (runtime/src/core/runtime.cor), chosen by the
target's conditional symbol. The driver defines .NET CoreLib's names:
`TARGET_64BIT` and `TARGET_AMD64` for x86-64, `TARGET_32BIT` and
`TARGET_X86` for i386. Fields that `Sys.Cas`, `Sys.AtomicSwap` and
`Sys.AtomicAdd` work on are `nint`, since those are word operations. In long
mode the collector's region filter is two-level over the 47-bit user space.

The Linux layer (runtime/src/platforms/linux) names both ABIs under
`#if TARGET_AMD64`: the x86-64 call numbers, `mmap` with a byte offset,
`lseek`, `wait4`, `statfs` without a size argument, the long-mode `stat`,
`statfs`, `timespec`, `timeval`, `msghdr`, `cmsghdr` and `sigaction`
layouts, `arch_prctl(ARCH_SET_FS)` for the thread block, and the `clone`
trampoline and signal restorer as x86-64 code.

## Testing

Everything runs natively on an x86-64 Linux host:

- `tests/language/run.sh --target=x86-64` runs the language suite in long
  mode.
- The linker tests (linker/tests) check ELF64 objects against binutils and ld
  and run a statically linked program under the kernel.

## Not built yet

Shared objects, position-independent code and dynamic linking, and
freestanding (bare-metal) images: the driver and the link command refuse them
for x86-64 with a message saying so. The IR archive that lets the linker
regenerate a unit (IrUnitCodec) is not attached to long-mode objects, so link
time optimisation regenerates nothing for them.
