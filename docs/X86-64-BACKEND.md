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
  the i386 backend does. The thread block is `gs:[0]`: FS is left to glibc, whose thread pointer C code needs.
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
(R_X86_64_64, _32, _PC32, _PLT32, _REX_GOTPCRELX); the reader takes them
back, and the GOTPCREL family besides; GNU `ld -m elf_x86_64` links them too.
Executables are ELF64 ET_EXEC loaded at 0x400000, the small code model:
every symbol of a program below 2 GiB.

## Separate compilation and link-time optimisation

As on i386: each source is its own unit, and a unit's object carries the IR
archive and the lifetime hints beside its code. At the link the lifetime
hints are solved over the whole program and every unit is regenerated from
its IR by the backend the unit's ABI note names -- this one for a long-mode
unit -- in memory-budgeted parallel batches. The link summary records
direct calls with a 32-bit result, and a call to a function that returns a
constant is patched to `mov eax, imm32`, the same five bytes on both
machines. `corc project --target x86-64` (or the project property
`CorCTarget`) builds a project this way for long mode.

## Shared objects and dynamic linking

`--shared`, `--pic`, `--link-shared` and `--dynamic` work as on i386, and the
class library builds as seventeen shared objects
(`TARGET=x86-64 tests/integration/build-libraries.sh`). A program names
`/lib64/ld-linux-x86-64.so.2` and runs under the system's loader.

Code is RIP-relative already, so a shared object's text needs no loader
relocation. What another image defines -- everything this object does not,
in a shared object; what a library supplies, in a program -- is reached
through its GOT slot, `mov r, [rip + sym@GOTPCREL]`; when the link finds
the symbol defined after all the load is relaxed to `lea r, [rip + sym]` and
no slot is made. Calls are `call rel32` (PLT32): direct to a definition, to
a PLT entry (`jmp [rip + slot]`) otherwise. Addresses held in data are
RELATIVE or R_X86_64_64 relocations in `.data.rel.ro`. Binding is eager and
symbolic, as on i386.

The image is ELF64 throughout: RELA relocations with the addend in the
entry, 24-byte symbols, 16-byte dynamic entries, eight-byte GOT slots; the
SysV hash table keeps its 4-byte words.

The frame table's base, the image directory's table addresses and the
stack maps' base are 32-bit fields. In long mode they hold the distance
from the field to what it names, so an image loaded anywhere in 64-bit space
describes itself with no loader relocation; the runtime reads them back with
`TableAddress`.

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
- The linker tests (linker/tests) check ELF64 objects against binutils and ld,
  run a statically linked program under the kernel, and a shared object with
  programs linked against it by this linker and by GNU ld under
  ld-linux-x86-64.
- `TARGET=x86-64 tests/integration/shared-libraries.sh` builds the class
  library as shared objects, checks each image against what the loader
  accepts, and runs the shared-library tests linked `--dynamic`.

## Bare metal

`--freestanding` builds for long mode as for i386: the runtime is
`runtime/src/arch/x86/baremetal.cor`, the thread block is a word of `.bss`
(or `gs:[0]` with `--tls-gs`, a kernel's per-processor block with its base
in `IA32_GS_BASE`), and `--flat` writes a flat image. A system call through
a gate other than Linux's (`--ring1-syscalls`, `int 0x83`) is `int N` with
`syscall`'s registers.

The machine intrinsics are the i386 backend's -- port I/O and its string
forms, `Lgdt`, `Lidt`, `Invlpg`, `ReadCr`/`WriteCr` (a word wide, so CR3 and
CR8 are whole), `LoadSegments` -- and what a long-mode kernel needs besides:
`ReadMsr`/`WriteMsr`, `Cpuid`, `ReadTsc`, `LoadTaskRegister`,
`LoadCodeSegment` (a far return, `retfq`) and `SwapGs`.

The linker's addresses are 64 bits, so a kernel may be linked in the top two
gigabytes (`--base 0xffffffff80100000 --load 0x100000`): the code the
compiler writes is RIP-relative, data holds eight-byte addresses, and the
assembler's absolute 32-bit fields in long mode are `R_X86_64_32S`.

The assembler writes 64-bit code (`.bits 64`, `corc asm --target x86-64`):
REX prefixes, r8-r15 in every width, RIP-relative `[rip + x]`, CR8, `movabs`,
`dq`, and long mode's own instructions. An object with 64-bit code in it is
a long-mode object; a boot stub that switches modes is one file.

`tests/freestanding/run.sh` runs bare-metal images as processes on both
targets: `host.cor` hands each a heap, a write and an exit, and everything
else is the bare-metal runtime. Privileged instructions are checked in the
disassembly, and the assembler's long mode against objdump.

## Calling C

See [NATIVE-CALLS.md](NATIVE-CALLS.md). The runtime's thread block is in GS
on x86-64, because FS is the C library's thread pointer.
