# The x86-64 backend

`corc compile --target x86-64` (also `x86_64`, `amd64`, `x64`) builds a
long-mode Linux program: an ELF64 static executable for any x86-64 processor
from the first AMD64 (K8) on. Everything above the backend -- parser, binder,
lowering, the optimiser -- is shared with the i386 target; what differs is
here, in the linker, and in the word size the runtime is written against.

## The processor

`--cpu` names the processor, as for i386 (X86Cpu): a model that runs x86-64
code, its instructions what the code may use and its tuning what CpuModel
asks. The default is `x86-64`, the psABI's baseline: the K8's set without
3DNow! -- SSE2 for floating point, CMOV, the 64-bit `CMPXCHG`/`XADD`/`XCHG`,
`MFENCE`, nothing from SSE3 on, no `CMPXCHG16B`, no `POPCNT`/`LZCNT`, and no
`LAHF`/`SAHF` in 64-bit mode. The models: the K8 by revision -- `athlon64`
(`k8`, `opteron`, `athlon-fx`: revisions C and D, the FX-51, no SSE3) and
`athlon64-sse3` (`k8-sse3`, `opteron-sse3`: revision E on, the dual-core
Opteron 280 and 290, SSE3) -- `amdfam10`; `nocona`, `core2`, `nehalem`,
`sandybridge`, `haswell`, `skylake`, `skylake-avx512`; and the levels
`x86-64-v2`, `-v3`, `-v4`. The `--enable-`/`--disable-` switches of the
long-mode families (`sse3` .. `avx512`, `popcnt`, `lzcnt`, `cx16`, `sahf`,
`movbe`, `bmi`, `bmi2`) apply; `--fpu` does not, and a switch that takes away
what long mode stands on (CMOV, MMX, SSE, SSE2) is refused. Objects carry the
contract `CCPU2` with the model, `sse2` and the set; a link of long-mode units
compiled for different models names its own with `--cpu`.

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

ONE MACHINE IR FOR THE FAMILY. The two backends share the classes in
`compiler/src/arch/x86/backend` -- `MReg`, `MMem`, `MImm`, `MInstr`, `MBlock`,
`MFunction`, `Frame`, the opcode set `MOp` and what each operand does to its
register (`Roles.Of`) -- which know the mode they are in from `X86Mode`
(`Long`, `Word`, `Gprs`): the backend that generates says it, once a
process, as `Target.Current` is said. Registers are numbered alike in both
modes: the general ones below sixteen (eight of them on i386), the XMM ones
from sixteen (`MReg.XmmBase`), virtual ones from thirty-two.

Passes with a single implementation, in `x86/backend`, used by both modes:
`AddressFold`, `IfConvert`, `XmmLocalSplit`, `Layout`, `LoopRotate`,
`FrameCompact`, `SpillCount`, `FrameSlotLiveness` (the adapter of
`arch/shared/frames`), the inline assembly record `InlineAsmCode`, and
`Peephole` (with `Peephole.UnusedSaves`, the callee-saved registers nothing
names any more) -- whose long-mode part
(`Peephole.Long.cs`: the zero-extensions `mov r32, r32`, jump threading, the
jump to the next block) is what i386 has no need of. Long mode's calling
conventions and the registers its instructions touch unnamed are
`LongAbi` and `Implicit` (`x86/backend/Implicit.cs`).

ONE ENCODER (`x86/backend/Encoder.cs`, `Encoder.Long.cs`). An instruction is
written as i386 writes it, and long mode's REX prefix, segment prefix and
RIP-relative displacements are put in when it is whole; `Encoder.Long.cs`
has long mode's forms where they are not i386's with a prefix (64-bit
immediates, MOVSXD, the stack operations, calls, the frame's prologue and
epilogue with callee-saved registers in slots, jump tables), with the SIB
form for an absolute `[disp32]` (mod 00 r/m 101 is RIP-relative there) and
SSE's mandatory prefix before REX. What long mode has no form of is refused
with the function's name, never written as i386 would write it: an opcode
outside the family's shared forms, and the immediate of a 64-bit operation
that is not a sign-extended 32 bits or is a symbol's address. Branches are
relaxed to a fixed point by whole passes; loop heads are padded by `--cpu`
unless `--opt-size`.

ONE SELECTOR (`X86.Selector`, `x86/backend/Select.cs` and its partials). The
walk over a function's blocks and the selection of nearly every opcode are
one method each for both modes: copies, add, subtract and the bit
operations, multiplication (constants as shifts and LEAs where `--cpu`'s
latencies say so), division by a register, shifts, compares and what makes
a 0 or 1 of one, branches and the fused compare-and-branch, the switch's
table, the extensions and truncations, loads and stores of every size
(device loads marked), the atomic operations, the string instructions'
copies and fills, and the floats -- long mode's are i386's XMM class
(`Select.Sse.cs`) with every float in it: arithmetic with a constant read
from the pool by the instruction (`addsd xmm, [rip + constant]`; the 64-bit
backend lays the pool down as i386's does), negation by XORPS, compares,
conversions between the widths and from a signed integer, bit casts.

What the modes differ in is asked in a few places and nowhere else:
`Pairs` (an I64 is two registers on i386, one in long mode), `WordOf` (the
bytes of that register), `R`, `RM` with a width and `Address` (long mode's
operands: a constant by the shortest move, a 64-bit one through a
register, a symbol RIP-relative or through its GOT slot). An instruction's
width left unsaid is the mode's word (`MInstr.Width`), so a method that
serves both names every width it emits.

What is long mode's own, in `x86/backend/Select.Long.cs` (its methods of
i386's names carry an `L`): the System V call, parameters and return
(`SelectCallL`, `LoadParamsL`, `SelectRetL`), unwinding and SYSCALL, the
makers of its operands (`RL`, `RML`, `AddressL`, `LoadConstant`,
`GotAddress`), division by a constant at either width
(`SelectDivByConstant`), an unsigned integer to a float and a float to an
integer (64-bit conversions among them), a small copy or clear written as
moves, the per-CPU words, and its machine intrinsics. `SelectInstrL` holds
only their cases and hands every other opcode to the family's
`SelectInstr`, which refuses in long mode an opcode whose case is still
i386's alone (`BothModes`).

Every value is one register: an I32 in the 32-bit forms (which clear the
register's upper half), an I64 and an address in the 64-bit forms, a float
in XMM. Named things are reached RIP-relative; a symbol's address is `lea`.
Division uses the hardware and its trap, as on i386. A float's conversion to
an integer follows .NET's saturating rule from the IEEE bits, as i386's
does.

What is still x86-64's own, in `compiler/src/arch/x64/backend`:

- **RegAlloc.cs, X64Target.cs** -- the function lowered into the one
  allocator's form (`Corsac.Lang.RegAlloc`), with two register classes. A value
  live across a call cannot sit in a caller-saved register; a float across a
  call is always spilled. Spill slots are eight bytes and reloads are whole
  words, so a sub-word integer definition is never folded into its slot (the
  slot's upper half would survive into the reload), and only constants a
  sign-extended imm32 can carry are folded as immediates.
- **FramePointerFree.cs** -- a leaf that gives RBP a value addresses its
  frame off RSP.

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

The frame table's base and the image directory's table addresses are
32-bit fields. In long mode they hold the distance from the field to what it
names, so an image loaded anywhere in 64-bit space describes itself with no
loader relocation; the runtime reads them back with `TableAddress`.

## The runtime

The runtime keeps its own layouts -- the thread block, the collector's
block headers, free index, chunk records, bitmaps, queues -- in machine words
through `WordSize.Bytes` (runtime/src/core/runtime.cor), chosen by the
target's conditional symbol. The driver defines .NET CoreLib's names:
`TARGET_64BIT` and `TARGET_AMD64` for x86-64, `TARGET_32BIT` and
`TARGET_X86` for i386. Fields that `Sys.Cas`, `Sys.AtomicSwap` and
`Sys.AtomicAdd` work on are `nint`, since those are word operations; an
`int` takes `Sys.Cas32`, `Sys.AtomicSwap32` and `Sys.AtomicAdd32`, which are
four bytes on either target (Interlocked's int forms). In long mode the
collector's region filter is two-level over the 47-bit user space.

A long-mode object carries its stack maps (`.corsac.stackmaps`) as an i386
one does: the same table (`StackMapTable` in the linker's object model), in
this target's layout (`StackMapLayout.X64`: eight-byte words; RBX, R12, R13,
R14 and R15, in the order the prologue saves them; version 1, whose header
says so and holds the base as a distance from its own word). At each call
the map names the spill slots and callee-saved registers holding live
references (the shared allocator's `IPlacement.LiveAcross`) and the IR
frame slot words live or exposed there (`Frames.FrameSlotLiveness`, shared
with i386, with the words a call could find unwritten zeroed after the
prologue). The collector reads a stack by them on both targets
(`Gc.ScanOwnStack`): one reader, a word `WordSize.Bytes` wide and the
registers by the bit the table packs each at. A function that gives RBP a
value (a leaf) has no map; a frame no map describes is read whole.
The heap's table of the chunk at every 4 KB (`HeapChunks.SlotTable`), which
on i386 covers all four gigabytes, covers a 64 GiB window in long mode, placed
by the first chunk the heap maps (`HeapChunks.SlotLow`); a free or a lookup
outside it finds the chunk by the index instead. The marker uses the table on
i386 only.

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
a gate other than Linux's (`Sys.GuiCall`, `int 0x81`) is `int N` with
`syscall`'s registers.

The machine intrinsics are the i386 backend's -- port I/O and its string
forms, `Lgdt`, `Lidt`, `Invlpg`, `ReadCr`/`WriteCr` (a word wide, so CR3 and
CR8 are whole), `LoadSegments` -- and what a long-mode kernel needs besides:
`ReadMsr`/`WriteMsr`, `Cpuid`, `ReadTsc`, `LoadTaskRegister`,
`LoadCodeSegment` (a far return, `retfq`) and `SwapGs`.

The linker's addresses are 64 bits, so a kernel may be linked in the top two
gigabytes (`--base 0xffffffff80100000 --load 0x100000`, or the same `--base`
for a `--flat` image): the code the compiler writes is RIP-relative, data
holds eight-byte addresses, and the assembler's absolute 32-bit fields in
long mode are `R_X86_64_32S`. An i386 image is refused a base, or an end,
above four gigabytes.

The assembler writes 64-bit code (`.bits 64`, `corc asm --target x86-64`):
REX prefixes, r8-r15 in every width, RIP-relative `[rip + x]`, CR8, `movabs`,
`dq`, and long mode's own instructions. An object with 64-bit code in it is
a long-mode object; a boot stub that switches modes is one file. Its 32-bit
part runs on the same long-mode processor, so no instruction in it is
refused for the processor (`--cpu` names one, or the baseline, as for
`compile`, and the object's contract says which), and its
fields are relocated with their whole 64-bit addend, so `label - KERNEL`
reaches a higher-half label's loaded address before paging is on.
`tests/freestanding/15_long_mode_switch` is such a stub: CPUID, PAE,
`EFER.LME`, paging and a far jump into the 64-bit part.

`tests/freestanding/run.sh` runs bare-metal images as processes on both
targets: `host.cor` hands each a heap, a write and an exit, and everything
else is the bare-metal runtime. Privileged instructions are checked in the
disassembly, and the assembler's long mode against objdump.

## Calling C

See [NATIVE-CALLS.md](NATIVE-CALLS.md). The runtime's thread block is in GS
on x86-64, because FS is the C library's thread pointer.
