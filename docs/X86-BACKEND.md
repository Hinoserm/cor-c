# The x86 backend

Status: design contract for the 486 code generator. Everything in the
`compiler/Lang/Ir`, `compiler/Lang/X86` and `compiler/Lang/Lower` directories
is written against this document, and where the code and this document
disagree, one of them is wrong and the disagreement is a bug.

## What it is for

COR-C# has to produce fast, correct 32-bit code for a 486 and later, first as
Linux ELF executables on the development host, then as bare images the kernel
loads, then as the kernel itself. One backend serves all three: the only thing
that changes between them is the system-call library the program links.

The old code generator went from the syntax tree straight to encoded
instruction words for a 64-bit machine with thirty general registers and no
calling convention anyone else used. None of that survives contact with a
486. The new backend is a conventional three-stage compiler:

1. **Lowering.** The bound syntax tree becomes a target-independent
   intermediate representation: functions of basic blocks of three-address
   instructions over unlimited virtual registers. Every language feature --
   virtual dispatch, interface dispatch, closures, exceptions, generics,
   strings, boxing, `foreach`, `switch` -- is decided here, once, in terms of
   loads, stores, calls and branches.
2. **Optimisation** over the IR: constant folding and propagation, copy
   propagation, dead code elimination, branch simplification. Cheap passes
   that pay for themselves on a 486, where an instruction not emitted is an
   instruction not executed.
3. **Code generation** for x86: instruction selection into a machine-level
   IR with virtual registers, a linear-scan register allocator over the six
   allocatable general registers, frame layout, and encoding into bytes.

Output is an ELF object, linked by our own linker into an ELF executable.

## Targets

The compiler is retargetable, and the seam is drawn in one place.

`Lang.Target` describes a machine to the front half of the compiler: word
size, the size and alignment of each primitive, the object and array header
layout, and whether `long` is native or a pair. The binder lays out fields
and frames with those numbers and nothing else; lowering uses them to
compute offsets; the IR itself carries only sizes, never register names.

`Lang.Backend` is the other half: it takes an IR module and produces an
object file. The x86 backend is the first. A CORSAC backend, an ARM
backend or anything else implements the same interface, and `corc
--target arm` selects it. Nothing in the parser, the binder, the
monomorphiser or lowering changes for a new target, and a new target's
work is instruction selection, register allocation for its register file,
its calling convention, and an object-file writer.

What a target may NOT change is the language: `long` is 64 bits everywhere,
`int` is 32, a reference is one word. A target that cannot do 64-bit
arithmetic natively gets helper calls, as the 486 does.

## The machine model (x86)

**Registers.** EAX, ECX, EDX, EBX, ESI and EDI are allocatable. EBP is the
frame pointer, always. ESP is the stack pointer. Every method has a frame
with EBP, even a leaf: a 486 does not gain enough from omitting it to be
worth losing gdb backtraces and a simple unwinder.

**Calling convention: cdecl,** exactly as GCC uses it on i386 System V.

- Arguments are pushed right to left. Each occupies a whole number of 4-byte
  stack slots: `bool`, `byte`, `short`, `char`, `int`, `uint`, pointers and
  references take one; `long`, `ulong` and `double` take two; `float` takes
  one, stored as a 32-bit IEEE value.
- `this` is the first argument, so it is pushed last and sits at `[EBP+8]`.
- The caller removes the arguments after the call.
- Integers, pointers and references return in EAX. `long` and `ulong` return
  in EDX:EAX, high half in EDX. `float` and `double` return in ST(0).
- EBX, ESI, EDI and EBP are preserved across a call. EAX, ECX and EDX are not.
- The stack is 4-byte aligned at every call; 16-byte alignment is not
  required and not maintained. Nothing on a 486 wants it.

Choosing cdecl rather than something of our own means COR-C# code and C code
compiled by GCC can call each other without thunks, which is what porting a
C library onto the kernel needs, and it means gdb understands every frame.

**Sizes.** A machine word is 4 bytes. `long` and `ulong` are 8 bytes and
live in a register pair, low half in the lower-numbered operand. `double` is
8 bytes, `float` 4. Reference, pointer and `Type` values are one word. A
`Nullable<T>` is a pointer to a heap cell holding T, as before.

**Floating point** is x87. The register allocator does not manage the x87
stack: a floating-point virtual register lives in a frame slot, and each
operation loads its operands, computes, and stores the result. That is what
GCC does at -O0 and it is correct on every 486DX. Keeping values on the x87
stack across instructions is an optimisation for later, and it is confined
to instruction selection when it comes.

`float` arithmetic is performed in double precision and rounded on store,
which is what C# permits and what x87 does naturally.

## Object layout

Offsets are in bytes. Everything is little-endian.

**Object header, 8 bytes.**

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | vtable pointer |
| 4 | 4 | synchronisation word |

Fields follow, each naturally aligned to its size (8-byte fields to 8). A
`struct` has no header: its fields start at offset 0, it has no vtable, and
it is heap-allocated like a class. That is how the binder already treats it,
and the backend does not change it.

**Type descriptor, 48 bytes, immediately before the vtable.** An object
reaches its descriptor by subtracting 48 from its vtable pointer.

| Offset | Field |
|---|---|
| 0 | address of the name string |
| 4 | instance size in bytes |
| 8 | depth in the class chain; -1 for an interface |
| 12 | address of the ancestor display |
| 16 | address of the sorted interface descriptor array |
| 20 | this descriptor's own address |
| 24 | flags: bit 0 array, bit 1 string |
| 28 | payload offset, for arrays and strings |
| 32 | address of the reference map, or zero: the collector's |
| 36 | GC flags: bit 0, this sequence's elements are references |
| 40, 44 | reserved, zero |

It was 32 bytes and the first eight fields are where they were: the two
the collector needs were APPENDED, so an image built before them and one
built after disagree about the size of the descriptor and about nothing
else. The reserved pair is there so the next one costs no layout change
either.

**Reference map.** One per class, out of line, shared by nothing, and
absent -- the slot is null -- when the type holds no references at all,
which is the common case for a struct of numbers:

| Offset | Field |
|---|---|
| 0 | how many words of the instance the map covers |
| 4.. | the bits, 32 per word, least significant first |

Bit *i* stands for the word at byte offset 4*i* FROM THE START OF THE
OBJECT, header included; the header bits are always clear. A map covers
the whole instance, base classes' fields and all, because the collector
holds an object and must not walk a class chain to find the pointers in
one.

A field is a reference when its type is a class, an interface, a string
or an array; when it is a nullable value type, whose cell is on the heap;
when it is a struct, which is a headerless heap block reached by pointer;
and when it is a captured local's cell, whatever the captured type is.
Two holes are known and deliberate. A struct's block carries no
descriptor, so the collector can trace the pointer but not yet what is
inside it -- inline struct layout or a per-field map fixes that. And a
field of `Prim.Any`, the canonical machine word a generic is compiled
over, is NOT marked: it is a reference in one instantiation and an
integer in the next, and a moving collector that guessed would relocate
an integer. Precision there waits for per-instantiation maps.

**Array descriptors** carry the element stride in the size field and say
whether the elements are references in GC flags bit 0. The element kind is
currently inferred from the element type's printed name, which is what the
one call site that builds an array descriptor passes; an unknown name is
assumed to be a reference, since missing a root is the one mistake a
moving collector cannot survive.

**Ancestor display.** An array of descriptor addresses, index 0 the root of
the chain, index `depth` the type itself. `x is C` for a class C of depth d
is: load the vtable, load the display, check the object's depth is at least
d, compare display[d] with C's descriptor. Two loads and a compare in the
common case, no walk.

**Interface array.** Sorted ascending by descriptor address, terminated by
zero. `x is I` is a scan; interfaces are few per class.

**Vtable.** Interface method slots come first, numbered program-wide by the
binder so that a slot means the same method on every class implementing the
interface. Class virtuals follow. ToString and Equals have fixed slots on
every class. Each entry is the address of the method.

**Arrays and strings, 16-byte header.**

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | vtable pointer (a shared sequence descriptor) |
| 4 | 4 | synchronisation word |
| 8 | 4 | element count |
| 12 | 4 | unused, keeps 8-byte elements aligned |
| 16 | | elements |

A string is a byte array with the string flag set: a count and that many
bytes. There is no null terminator; one is appended when a string is handed
to a C interface.

**Closures** are ordinary objects: header, then the captured values as
fields. A captured local is a heap cell shared by the closure and the
method.

**Nullable cells** are one heap allocation of the held type's size, no
header. Null is the null pointer.

## Statics

Every static field is an ELF symbol of its own in `.data` or `.bss`, named
by its owner and field, and reached by absolute address in an executable or
through the GOT in a shared object. Not a block at fixed offsets: a block
has to be placed by someone, and the old design's per-library static slots
were that someone. With symbols, the dynamic linker places them, which is
the whole reason to use its format.

## Shared libraries

A COR-C# program does not carry the runtime and the class library in it.
They are ELF shared objects in `build/lib`, the program names the ones it
uses, and on Linux the system's `/lib/ld-linux.so.2` links them at load
time. **On CORSAC the kernel's loader reads the same files.** The format
does not change between the two systems; only who interprets it does, and
what follows is written so the kernel side can be built against it.

This is also what lets a COR-C# program link a C library, and a C program
link a COR-C# one.

The loader on the CORSAC side is built
(`os/kernel/arch/x86/elfload.cor`, 2026-09-17), and what it requires of a
file is the other half of this contract:

| | |
|---|---|
| class | 32-bit, little endian, `EM_386`; `ET_EXEC` or `ET_DYN` |
| interpreter | `PT_INTERP` of `/lib/ld-linux.so.2` means "the kernel binds this". Any other name is a file that must exist, is mapped beside the program and entered instead of it, with `AT_BASE`; the binding is then its business |
| segments | each `PT_LOAD` becomes a region over the file, so a segment's file offset and its address must agree modulo the page size |
| symbols | `DT_HASH` or `DT_GNU_HASH`; the search order is the executable, then the libraries in load order |
| relocations | `R_386_RELATIVE`, `R_386_32`, `R_386_PC32`, `R_386_GLOB_DAT`, `R_386_JMP_SLOT`, `R_386_COPY`. Anything else is refused by name |
| binding | eager, always: every relocation is applied before the first instruction, so no resolver runs in the program and `DT_BIND_NOW`/`DF_BIND_NOW` need not be asked for |
| text | a relocation into a read-only segment fails, because text is shared between processes and is never written |
| libraries | `DT_NEEDED` is searched as given if it has a slash, else along `DT_RUNPATH` (with `$ORIGIN`), then `/lib` and `/usr/lib` |
| initialisers | the kernel fills `DT_DEBUG` with an `r_debug` whose chain of `link_map` records names every image, the executable first; the runtime's startup walks it and runs each `DT_INIT_ARRAY`, since ring-3 code is not the kernel's to call |

`tests/vm/kernel-shared.sh` holds the whole of it to account with files
gcc builds: a program and its library, a library that needs a library, a
position-independent executable, a copy relocation, and both hash tables
on one boot.

**Generics across the boundary.** Monomorphisation makes a type per set of
type arguments, and the instantiation belongs to whoever asked for it: a
library carries the instantiations ITS OWN code needs, and anything else --
`List<TypeTheProgramDeclared>`, or `List<string>` where no library ever
wanted one -- is compiled into the program. An instantiation the linked
libraries do have is never compiled twice: see "What each image contains".

**Symbols** keep the mangled names the old backend used, which encode the
owner and the full parameter signature, so two libraries never collide and
`corc syms` is unchanged.

### The ELF contract

Everything a loader has to understand, and nothing else. A COR-C# image is
deliberately a small subset of what GNU ld emits.

**File.** 32-bit, little-endian, `EM_386`. A library is `ET_DYN` and is
linked to load at 0; an executable is `ET_EXEC` at `0x08048000` and is NOT
position-independent.

**Program headers**, in this order and no others:

| Header | Contents |
|---|---|
| `PT_LOAD` R+X | the ELF header, `.interp`, `.hash`, `.dynsym`, `.dynstr`, `.rel.dyn`, `.rel.plt`, `.plt`, `.text`, `.rodata` |
| `PT_LOAD` R+W | `.data`, `.got`, `.dynamic`, `.bss` (the zero tail is `p_memsz - p_filesz`) |
| `PT_GNU_STACK` R+W | no execute permission on the stack |
| `PT_INTERP` | executables only: `/lib/ld-linux.so.2` |
| `PT_DYNAMIC` | `.dynamic` |

Both `PT_LOAD`s are page-aligned and the read-only one is genuinely
read-only: **there is never a relocation in a text or rodata page**, so
the whole R+X mapping is shareable between every process running the
image. `DT_TEXTREL` is never emitted, and a build that would need one is
a bug in the code generator rather than something the loader should cope
with.

There is no `PT_TLS`, no `PT_GNU_RELRO`, no `PT_NOTE` and no
`PT_GNU_EH_FRAME`. A per-thread block exists but is ordinary memory the
runtime allocates; see "The thread block".

**Dynamic tags.** `DT_NEEDED` (one per library actually used),
`DT_SONAME` (libraries), `DT_RUNPATH` (executables, naming the directory
the libraries were found in), `DT_INIT`, `DT_HASH`, `DT_STRTAB`,
`DT_SYMTAB`, `DT_STRSZ`, `DT_SYMENT`, `DT_REL`, `DT_RELSZ`, `DT_RELENT`,
`DT_PLTGOT`, `DT_PLTRELSZ`, `DT_PLTREL` (always `DT_REL`), `DT_JMPREL`,
`DT_BIND_NOW`, `DT_FLAGS` (`DF_BIND_NOW | DF_SYMBOLIC`), `DT_FLAGS_1`
(`DF_1_NOW`), `DT_NULL`. The `.plt` group appears only when there is a
PLT entry, which an executable has and a library does not.

Not emitted, and a loader need not implement them: `DT_RELA` and anything
with an addend, `DT_INIT_ARRAY`/`DT_FINI_ARRAY`/`DT_FINI`, `DT_GNU_HASH`,
symbol versioning (`DT_VERSYM`, `DT_VERDEF`, `DT_VERNEED`), `DT_RPATH`,
`DT_TEXTREL`, `DT_DEBUG`.

**DT_HASH is the only hash table.** The SysV one: `nbucket`, `nchain`,
the buckets, the chains, with the ELF hash function. There is no GNU hash
section to fall back to.

**Relocations.** `REL` only -- eight bytes, `r_offset` and `r_info`, the
addend implicit in the word being relocated. Exactly four types appear:

| Type | Number | Where | What the loader does |
|---|---|---|---|
| `R_386_RELATIVE` | 8 | a library's `.data`, `.got` | add the load bias to the word |
| `R_386_32` | 1 | an executable's `.data` | add the symbol's address to the word |
| `R_386_GLOB_DAT` | 6 | `.got` | write the symbol's address into the slot |
| `R_386_JMP_SLOT` | 7 | `.got`, behind `DT_JMPREL` | the same, for a PLT entry |

**`R_386_COPY` never appears, and neither does `R_386_PC32`.** No copy
relocation, because a type descriptor's identity IS its address and a
duplicate in the executable's `.bss` would make `x is Stream` compare two
addresses of one type. Nothing PC-relative survives to the output: every
relative call is resolved when the image is linked.

How an executable reaches a library's DATA without a copy relocation is
the one place this differs from a C toolchain. The executable is not
position-independent, but it knows where its own `.got` is, so the
reference becomes one load from an absolute address -- `mov eax,
[0x804a7f8]` -- and the slot is an ordinary `R_386_GLOB_DAT`. One extra
load at the reference, and the address is the library's own.

**Binding is eager, and a library's own definitions win.** `DT_BIND_NOW`
and `DF_BIND_NOW` are set: the loader must fill every `.got` slot,
`R_386_JMP_SLOT` included, before the first instruction of the image
runs, and the lazy resolver stub is code we never emit. The three
reserved words at the start of `.got` are the ABI's -- word 0 holds the
address of `_DYNAMIC`, words 1 and 2 are zero and are never read, because
nothing binds lazily. `DF_SYMBOLIC` is set because it is true: a
reference inside a library to a name that library exports was bound to
its own definition when it was linked, so nothing can interpose on a
COR-C# library's internals.

A PLT entry is eight bytes: `jmp *disp32` through its `.got` slot, then
two `nop`s. Only an executable has a PLT; a position-independent object
reaches an imported function by loading the slot and calling through the
register, so a library's `.plt` is empty and its `.rel.plt` absent.

**Symbols.** `.dynsym` holds what the image exports and what it imports,
all `STB_GLOBAL`, typed `STT_FUNC` or `STT_OBJECT` where the definition
says so and `STT_NOTYPE` otherwise, with `st_shndx` `SHN_UNDEF` for an
import. A library exports every global it defines EXCEPT the six names
the linker itself provides -- `__text_start`, `_etext`, `__data_start`,
`_edata`, `__bss_start`, `_end` -- which describe one image each and must
never be resolved from another. An executable exports nothing.

**DT_INIT, and what a library does with it.** Every COR-C# shared object
has one, pointing at a generated function called `__corsac_init`. It
takes no arguments, returns nothing, and introduces the image to the
runtime -- `Runtime.BeginImage(__data_start, _end, __corsac_frames)`:

- the collector scans statics between `__data_start` and `_end`, and
  those two symbols name the RUNTIME's image when the runtime is the one
  asking. Without this a library's statics are never scanned and the
  objects they hold are collected while live.
- `__corsac_frames` is this image's table of what each address is, so a
  stack trace can name its functions.

`__corsac_init` is idempotent -- a word of `.bss` guards it -- so calling
it twice is free, which matters because on Linux the dynamic linker has
already run it and on CORSAC the program runs it itself.

**WHO RUNS THE INITIALISERS.** On Linux, `ld-linux.so.2` does, before the
program is entered. A loader that lives in a KERNEL cannot: calling
`__corsac_init` means calling into the program's own privilege level,
which is exactly what a kernel must not do. So the kernel hands the
process the standard SysV link map instead and the program runs them:

- the executable's `DT_DEBUG` is filled in by the loader with the address
  of an `r_debug`, whose `r_map` is a chain of `link_map` records
  (`l_addr` the load bias, `l_name`, `l_ld` that image's dynamic section,
  `l_next`, `l_prev`), the executable first. It is the same structure gdb
  reads.
- the entry stub calls `Runtime.StartImages(_DYNAMIC)`, which finds
  `DT_DEBUG`, walks the chain and calls each image's `DT_INIT` and then
  its `DT_INIT_ARRAY` (which this compiler does not emit, but a C library
  linked in would). Addresses in a dynamic section are as the image was
  linked, so the bias is added.
- **it does nothing at all if any image has already introduced itself**,
  which is how it tells the two worlds apart without asking. Under
  ld-linux every initialiser has run before `_start`, and walking the
  chain there would run a foreign library's `_init` a second time, which
  is not something this runtime may do.

The executable's own registration is a direct call in `_start`, not a
`DT_INIT`, so a loader that ignores an executable's `DT_INIT` loses
nothing.

**What the loader must do, in order:** map the `PT_LOAD`s of the
executable and of everything `DT_NEEDED` names; apply `DT_REL` and
`DT_JMPREL` for each image; fill the executable's `DT_DEBUG` with an
`r_debug` (or call each library's `DT_INIT` itself, if it can); jump to
`e_entry`. There is nothing else: no lazy binding to arrange, no TLS
block to allocate, no `.eh_frame` to register, no atexit list to keep.

### The split

One shared object per .NET assembly: a type goes to the file named after
the assembly it lives in in .NET, so a program maps what it uses and
nothing else. `os/build-libs.sh` holds the list and builds them into
`build/lib`; on a disc they are installed in `/lib`.

The exception is the bottom of the stack, and it is an honest one. The
runtime is written in COR-C# and the core of the class library is written
against the runtime -- `lib/std.cor` allocates and throws, `lib/rt/gc.cor`
walks strings and arrays -- so they refer to each other and cannot be two
files. `libcorsacrt.so` is therefore the runtime, the collector, the
threads, the system-call layer and what .NET calls
`System.Private.CoreLib` in one.

| Shared object | Sources | Needs |
|---|---|---|
| `libcorsacrt.so` | `std.cor`, `rt/runtime.cor`, `rt/gc.cor`, `threading.cor`, `threading-linux.cor`, `sys/linux.cor`, `signals.cor` | -- |
| `libSystem.Runtime.InteropServices.so` | `interop.cor` | rt |
| `libSystem.Security.Cryptography.so` | `security.cor` | rt |
| `libSystem.Runtime.Extensions.so` | `time.cor`, `values.cor`, `environment.cor` | rt, Cryptography |
| `libSystem.Runtime.Numerics.so` | `numerics.cor` | rt |
| `libSystem.Collections.so` | `collections.cor` | rt |
| `libSystem.Text.RegularExpressions.so` | `regex.cor` | rt |
| `libSystem.IO.so` | `io.cor`, `io-streams.cor` | rt, InteropServices, Extensions |
| `libSystem.IO.Compression.so` | `compression.cor` | rt, Numerics, IO |
| `libSystem.Formats.Tar.so` | `tar.cor` | rt, Extensions, IO |
| `libSystem.Console.so` | `console.cor` | rt, IO |
| `libSystem.Net.so` | `net.cor` | rt, InteropServices, Extensions, IO |
| `libMono.Posix.so` | `unix.cor`, `power.cor` | rt, Extensions, IO, Console |
| `libSystem.Diagnostics.Process.so` | `process.cor` | rt, Extensions, IO, Console, Posix |

The order of that table is the build order and the dependency order: a
library may only name what is above it, and `os/build-libs.sh` checks
after every build that no undefined symbol points back down the stack.
Three edges in `lib/` had to be turned round to make it true, and each
was a library calling UPWARDS: the platform library's `Exit` called
`Environment.AtExit` which called `Console.Flush` (now Console leaves the
address with `Runtime.OnExit` and the platform library calls what it was
left, knowing nothing about Console); `Os.Exec` asked `Environment` for
the address of `envp` (now it reads its own entry stack, which is its
business); and `PosixSignalRegistration` ended the process through
`Environment.Exit` (now `Os.Exit`, which runs the same hook).

### What each image contains

The compiler is given the whole class library's sources for every build,
in the same order every time, and is told which of them THIS build owns:

- `corc compile --shared <own sources> --ref <everybody else's> ...`
  builds one library. What the `--ref` sources declare is bound but not
  emitted, and every reference to it is left for the loader.
- `corc compile prog.cor --dynamic` builds a program against the shared
  objects in `build/lib`. Here nothing is `--ref`: the program is
  compiled as if it were static, and then every definition that one of
  the libraries EXPORTS is dropped from it. What survives is the
  program's own code and the generic instantiations no library was asked
  for, which is exactly the rule generics need.

`DT_NEEDED` is as-needed: after the program is generated, a library is
named only if it supplies a symbol the program actually references. A
`true` that prints nothing needs the runtime and nothing else.

**THE SOURCE LIST IS PART OF THE ABI.** Interface method slots are
numbered program-wide, in the order the interfaces are declared, so every
image that meets another must have been compiled from the same sources in
the same order -- which is why a library build is given the whole list
and marks the rest `--ref` rather than being given only its own files.
`os/build-linux.sh` holds the one list and every build reads it from
there.

### Position-independent code

PIC is a backend mode, on for shared objects and off for executables.

**The GOT pointer is a virtual register.** The i386 ABI pins it in EBX,
which costs a register in every function that names anything at all; on a
machine with six of them that was enough that eight functions of the
runtime could not be allocated. Here it is materialised at entry -- `call
$+5; pop r; add r, _GLOBAL_OFFSET_TABLE_` -- into a register the
allocator chooses, and spilled like any other value under pressure.
`compiler/Lang/X86/Pic.cs` rewrites a selected function between selection
and register allocation, which is the only place that can both see the
selector's symbol references and still invent registers:

- a name this object defines and does not export is `lea r, [got +
  sym@GOTOFF]`, or, when it was already a memory operand, the same
  instruction with the GOT pointer as its base -- the same number of
  bytes as the absolute form it replaces;
- anything else is `mov r, [got + sym@GOT]`, because an exported name has
  no fixed offset from this object's GOT;
- the address of a block -- a landing pad -- is GOTOFF against the
  function's own symbol, so it is this object's copy;
- a call to a name this object defines is a direct relative call, which
  the linker binds to that definition; a call to an imported one is `mov
  r, [got + sym@GOT]; call r`, and needs no PLT because binding is eager;
- a `switch` jump table is reached from the GOT pointer, which comes
  along as a second operand of the jump so the allocator keeps it live.

A function that names nothing outside itself pays none of this.

**Anything holding an address moves out of `.rodata`.** Jump tables, the
frame table, the stack maps, vtables, type descriptors and a string
literal's header are relocations, and a relocation in a read-only page is
a page the loader must make writable and private to each process. In a
shared object that is every such item; in an executable it is only the
ones naming a library's symbol. The text segment itself is untouched
either way.

### What it is worth

`build/bin`, the 67 utilities of the userland, with the class library
compiled into each and then linked against `build/lib`:

| | static | shared |
|---|---|---|
| all of `build/bin` | 9,045,584 | 4,925,604 |
| `build/lib` | -- | 1,609,352 |
| **together** | **9,045,584** | **6,534,956** |
| `true` | 67,948 | 21,480 |
| `cat` | 91,584 | 43,036 |
| `ls` | 176,704 | 103,120 |
| `grep` | 179,648 | 86,660 |
| `sh` | 262,272 | 203,196 |
| `ssh` | 602,696 | 496,424 |

The disc is a third smaller, but the number that matters more is not in
the table: the shared text is mapped from one copy of the file, so
sixty-seven processes running at once share one collector and one
`String`, and a fix to either is one file to replace rather than
sixty-seven to rebuild. `true` names five libraries and maps neither the
regular expressions, the compression, the tar, the sockets nor the big
integers.

What a program still carries is its own code, its own type descriptors
and string literals, its frame table, and the instantiations no library
was asked for.

### What is not built yet

**Structural descriptors can be duplicated between two libraries that do
not depend on each other.** A boxed `int`, or the descriptor shared by
every `byte[]`, belongs to whichever image first needed it; a program, or
a library higher up, drops its own copy in favour of one a library it
links already has, but two siblings can each end up with one. `x is int`
on a box made by the other sibling would then answer false. Nothing in
the current split hits it, because the bottom library uses every
primitive; putting a canonical set in `libcorsacrt.so` is what closes it.

**An inlined call does not follow the library.** The optimiser inlines
across the boundary at the source level, so a small library function the
optimiser folded into a program's loop is in that program's code, and a
fix to it needs the program relinked. That is the trade for inlining at
all; it is confined to small leaf functions.

**Nothing is unloaded.** There is no `DT_FINI`, no `dlclose`, and the
collector has no notion of an image going away.

## Calls and dispatch

- **Static and non-virtual instance calls** are direct `call` to a label.
- **Virtual calls** load the vtable from the object, load the slot, and
  `call` through the register.
- **Interface calls** are the same: the slot number is program-wide, so no
  lookup is needed. This is why interface slots are numbered first.
- **Delegates and lambdas** are objects with an Invoke in a known slot.
- **Indirect calls through a function pointer** (`Sys.AddressOf`) are
  `call` through a register.

Every call site evaluates arguments into virtual registers, pushes them, and
adjusts ESP afterwards. The register allocator treats EAX, ECX and EDX as
clobbered at each call, so nothing live is left in them.

## Exceptions

A per-thread handler chain, as before, in a frame-local record:

| Offset | Field |
|---|---|
| 0 | previous handler |
| 4 | handler address |
| 8 | saved ESP |
| 12 | saved EBP |

`try` pushes a record on the stack and links it at the head of the chain,
whose head lives in the runtime's thread block. `throw` loads the head,
restores ESP and EBP from it, and jumps to the handler address with the
exception object in EAX. A catch clause is a type test on that object;
nothing matching re-throws to the next record. `finally` is emitted twice:
once on the normal path, once on the unwinding path, and a `return` inside
a `try` runs the open finally blocks innermost first before leaving.

This is setjmp/longjmp with the frame pointer as the anchor, which is why
every method keeps one. It costs a few stores per `try` and nothing per
call, which is the right trade for a 486.

**The thread block.** Everything the runtime keeps per thread is one
64-byte block (`Tls` in `lib/rt/runtime.cor`, mirrored by constants in the
lowering), reached by `Sys.ThreadBlock()`: on Linux `mov r, gs:[0]` with
GS set through `set_thread_area` at thread start; freestanding, a static
the entry stub fills, so bare metal needs no GS until the kernel wants
one. Offsets: 0 self, 4 the handler chain head, 8 the stack's top (what
the collector scans to), 12 the stack pointer where the thread stopped,
16/20 the allocation buffer's pointer and limit, 24 the thread id, 28 a
running/stopped state, 32/36 the buffer's base and saved limit, 40-63
scratch. The main thread's block is `__corsac_tls0` in `.bss`; a started
thread's is the bottom page of its own stack. There are no other
per-thread statics, which is what lets a second thread throw, catch and
allocate.

**Allocation and collection across threads.** Each thread bumps in its
own 64 KiB buffer, taken under the one heap lock and carved without it.
A collection stops the world without signals: the collector raises a
flag and zeroes every other thread's allocation limit, so their next
allocation falls to the slow path and parks on a futex; threads in a
futex wait or a sleep count as stopped already; once every registered
thread has stopped, their buffers are retired, every thread's stack (from
where it stopped to its top) and the statics are the roots, and the world
is woken after the sweep. The known gap, closed by the safepoint polls
the design already calls for: a thread in a long computation that neither
allocates nor blocks delays everyone's collection until it does.

## Async

`async` and `await` mean exactly what they mean in C#, and the design is
chosen so a thread pool can be added under it without changing a line of
compiled code.

**What the language sees.** An async method returns `Task`, `Task<T>`,
`ValueTask`, `ValueTask<T>` or `void`. It runs synchronously until it awaits something not yet complete,
then returns its task to the caller. `return x;` in an `async Task<T>`
method is checked against `T`. An exception that escapes the body is
stored in the task and rethrown by whoever awaits it; one that escapes an
`async void` method is unhandled. `await e` follows the awaiter pattern:
`e.GetAwaiter()` gives an awaiter with `bool IsCompleted`, `GetResult()`
and `void OnCompleted(Action continuation)`, and the value of the await
is what `GetResult()` returns. Async lambdas are async methods.

**What the compiler makes.** Each async method becomes a state machine:
a class implementing `Action`, whose `Invoke` is the method body rewritten
as `MoveNext`. The method itself becomes the kickoff: it allocates the
state machine, copies the receiver and the arguments into it, creates the
task with the task type's parameterless constructor, calls `MoveNext`
once, and returns the task.

`MoveNext` begins by switching on the state field: state 0 is the start
of the body, and state *k* is the resumption after the *k*-th await. At an
await whose awaiter is not complete, it records *k*, stores every register
live across the await into a field of the state machine, calls
`OnCompleted` with the state machine itself as the continuation, and
returns. Resumption reloads those registers, re-links the exception
handlers that were open at the await into the new stack frame, and
continues with `GetResult()`. Frame memory that must survive an await --
address-taken locals, the exception held across a finally -- lives in the
state machine rather than on the stack. Handler records stay on the
stack, because they name the stack.

The body runs under a catch-all whose landing pad completes the task with
the exception; reaching the end or a `return` completes it with the
result. Because the state machine is an ordinary heap object reachable
from the task's continuation list, the collector sees it like any other
object, and nothing about a suspended method lives outside the heap.

**The library contract.** The compiler calls exactly these, found by
name, in `lib/threading.cor`:

- `Task` and `Task<T>`: public parameterless constructors that create an
  incomplete task; `GetAwaiter()`; `void SetResultCore()` on `Task`,
  `void SetResultCore(T value)` on `Task<T>`, and
  `void SetExceptionCore(Exception e)` on `Task`, all of which complete
  the task and schedule its continuations.
- `AsyncRuntime.Unhandled(Exception e)`: an exception escaped an
  `async void` method.
- `AsyncRuntime.RunMain(Task t)`: an `async Task Main` or `async
  Task<int> Main` returned `t`; run the scheduler until it completes,
  then rethrow its exception or return.

**Where it runs.** The scheduler (`lib/threading.cor`) makes no system
call: it asks its platform for the time (`Clock.Now`), a sleep until a
deadline (`Clock.SleepUntil`), and a wait and a wake on a word
(`Native.Wait/Wake`). On Linux `lib/threading-linux.cor` answers with
`clock_gettime`, `nanosleep`, futexes, and `clone` for `Thread` and the
pool; on bare metal `lib/sys/baremetal.cor` answers with the PIT --
polled with wraps counted while interrupts are off, or driven by the
kernel's tick through `Clock.StartClock`/`OnTick` once they are on -- and
no threads. `--freestanding` links the core and that platform, so the
bootloader and the kernel await exactly as a Linux program does
(`tests/vm/async.sh` boots the proof). One rule bare metal adds: an
async method allocates its state machine on entry, so the image's entry
is synchronous, sets the heap, and only then runs the async body on the
scheduler; and the scheduler's own tables are made on first use, not by
a static initialiser, for the same reason.

**The scheduler.** Continuations are not run inline by the completing
code; they are posted to the current scheduler, which keeps a run queue.
On Linux the first scheduler is a single-threaded event loop that also
owns timers (`Task.Delay`) and, later, readiness for file descriptors. A
thread pool replaces it by providing the same `Post` over several
threads; the task and awaiter types are written with atomic state
transitions from the start so that nothing changes when it does. On
CORSAC the scheduler is the kernel's, and the multiprocessor is the pool.

**ValueTask.** An `async ValueTask` or `async ValueTask<T>` method
allocates nothing until it really suspends, as .NET's
AsyncValueTaskMethodBuilder boxes only at the first await that does not
complete synchronously. Its kickoff keeps the state machine in its own
frame (512 bytes at most, zeroed, a Home word set) and answers through
the caller's result buffer, as any method returning the struct does.
MoveNext runs on it; at the first await whose awaiter is not complete it
calls the method's box, which copies the machine to the heap, makes the
task, puts it in both copies, and hands the heap copy to OnCompleted. A
body that completes without that leaves its result in the machine, and
the kickoff answers `new ValueTask<T>(result)` (or `default`); one that
suspended, or threw, is answered `new ValueTask<T>(task)`. A struct
result buffer in such a body is a frame slot, which is a machine field,
so awaiting a ValueTask -- or anything whose awaiter is a struct -- makes
nothing either. The awaiters of a completed ValueTask hold its result
and no task.

Because the machine moves, the transform keeps every register that
always points into it as an offset from it across a suspension. A
register that may point into it on one path and elsewhere on another, or
an address in it stored into it, cannot be moved; the transform then
clears the method's `smstack_` word and the kickoff makes the machine on
the heap from the start. A call handed an address in a machine (a result
buffer) has the machine's cards marked after it.

## Async safety

The compiler refuses, after binding the whole unit (Binder.AwaitChecks):

- **An `await` while a lock is held.** C#'s CS1996 for `lock`, and the
  same for CORSAC's paired locks: IrqSpinLock, KernelGate, Ring1Lock,
  IoOwnership (IFilesystemGuard), GcLock, Atom, Monitor and SpinLock, and
  any type marked `[NoAwaitWhileHeld]` or deriving from or implementing
  one. Enter, EnterInterruptible, EnterPair and TryEnter take one; Leave,
  Exit and LeavePair give it back. From a taking to its giving back, on
  every path -- branches, loops, switch, try/catch/finally, early
  returns, break and continue through finally blocks -- an await is an
  error. A TryEnter (and IoOwnership's Enter, and Enter on a type marked
  `[NoAwaitWhileHeld(EnterMayFail = true)]`) holds the lock only where it
  answered true, in an `if` on it or on the bool it was put in. A method
  of the program's own that returns holding a lock (`EnterSeat()`) or
  gives one back is summarised and counts as that at its calls. Not
  seen: a lock taken through a delegate or an override the call does not
  name, one handed between methods in a field, or a `goto` backwards over
  an Enter.

  For a hold that awaits, `AsyncLock` (`using (await gate.LockAsync())`)
  and `SemaphoreSlim.WaitAsync`, both in the core runtime and so in a
  freestanding kernel: a waiter is a task, holding no processor.

- **An `await` or an allocation in an interrupt handler.** A method
  marked `[InterruptHandler]`, or implementing an interface method so
  marked or `IIrqHandler.OnIrq`, may not be async, await, or allocate:
  `new` of a class, an array or a delegate, joining strings, boxing, a
  lambda or method group made a delegate, or a call to an async method.
  The same is refused in every method it calls directly -- a static or
  non-virtual call -- whose body the unit compiles, transitively, and
  reported at the handler's call. Not proved: what a library compiled
  elsewhere does, what a virtual, interface or delegate call reaches, and
  what the runtime allocates by itself.

## Optimisation

The IR is the optimisation surface, and it is shaped for heavy work later:
a control-flow graph of three-address instructions over unlimited virtual
registers, with explicit loads and stores, calls that name their callee,
and no target names anywhere in it. SSA construction, inlining,
devirtualisation, loop optimisation, scalar replacement and instruction
scheduling all consume exactly this shape, and each arrives as a pass in
one list in the driver, between lowering and the backend. Lowering does
not pre-optimise beyond constant folding, deliberately: a lowering that
tries to be clever produces IR the passes cannot see through.

## Linux is a target, not a model

The compiler has a Linux target because the development host is Linux and
because running our programs there is the fastest way to prove the
backend. That target is one system-call library and the backend's
`Syscall` operation. Nothing else in the compiler or the operating system
takes Linux as a model: the CORSAC kernel, its loader, its process and
thread model, its allocator and its exception chain are designed for the
hardware they run on -- 486-class machines, including the asynchronous
multiprocessor designs this project is heading toward -- and are free to
be novel wherever novelty buys performance. ELF is the one Linux artifact
that leaks upward, and it is used as a container for interoperability, not
as a constraint on what goes in it.

## The IR

See `compiler/Lang/Ir/Ir.cs`. In one paragraph: a `Function` is a list of
`Block`s; a block is a list of `Instr` ending in one terminator. Operands
are virtual registers typed I32, I64, F32 or F64, or immediates, or symbols
(the address of a function, static, string literal or descriptor). I64
values are single virtual registers in the IR and become pairs at
instruction selection. There are no phi nodes: the IR is not SSA, a virtual
register may be assigned more than once, and the allocator computes live
ranges by a dataflow pass. This is a deliberate choice to keep lowering
simple; SSA can be introduced later without changing lowering, by a
renaming pass.

## Linking assembly, and where a kernel lives

`corc build --target x86-32 file.asm --obj -o file.o` assembles into an
ET_REL object (sections, `.global`/`.extern`, `R_386_32` and
`R_386_PC32`), and `corc compile ... --with file.o` links it in;
`--asm-entry sym` names the assembly's entry and tells the COR-C# stub it
is being called rather than calling. `--base` is the link address and
`--load` the physical one, so a kernel links at 0xC0100000 and is loaded
at 0x00100000 with `e_entry` physical: the loader copies by `p_paddr`
and jumps before any mapping exists. `Sys.AddressOf(Type.Method)` is a
method's address, and a method whose address is taken is neither
inlined away nor dropped. `@file` response files and `--cpu` are
accepted. This is what lets `os/kernel/arch/x86/entry.asm` be the
kernel's real entry, with its own stack and its interrupt stubs, and
`Arch.SwitchTo` an `iret` from a frame.

## The ELF output

The backend emits a relocatable ELF object and our own linker produces a
static, non-PIE executable at 0x08048000, the traditional i386 Linux load
address. Sections are the usual `.text`, `.rodata`, `.data` and `.bss`,
plus `.corsac.meta` for the type table so `typeof` and reflection keep
working and a future loader can read it. Symbols use the same mangled names
the old backend used, so `corc syms` is unchanged.

## Memory

Memory management is invisible to the programmer, as in real C#, and it is
the compiler's job. A program never frees anything, never sizes a heap and
never names an allocator: `new` is all there is. What makes that cheap on a
486 with 32 MB and still quick on a server with many gigabytes is that the
compiler and the memory library are designed together, and that the heap
is the LAST place an object goes, not the first.

**Where an object lives, in order of preference.**

0. **Static data.** What is known when the program is compiled is laid down
   in the image and never built: every string literal (with its hash already
   worked out, below), and every static array of literals (below). A
   deliberate departure from .NET, which builds such tables at run time.
1. **The frame.** An object whose reference never leaves the function that
   made it -- a scratch buffer, an enumerator, a small array, a closure
   called and dropped -- is a frame slot, up to a kilobyte.
2. **Owned.** An object with one owner and a last use the compiler can find
   is freed there, by a free the compiler inserts.
3. **The collector.** Whatever is left, where lifetime depends on data.

The collector is linked only when tier 3 is non-empty after whole-program
analysis (`Module.NeedsHeap`); a program without it gets a bump region.

**Tier 0: static arrays of constants.** A static field -- `readonly` or not --
whose initialiser is a one-dimensional array of a keyword element type
(`bool`, `char`, the integers, `float`, `double`, `string`) written with
literals only, each in range, is recognised from its declaration
(`Binder.StaticArrayOf`) and kept off the type's initialiser
(`FieldDecl.StaticData`). Lowering lays the array down in the data section
exactly as the heap would hold one -- the vtable of its sequence
descriptor, the count, the elements, a string element as its literal --
and the field's word as its address (`Lowering.StaticArrayData`). The table
exists before any code runs; a type whose only static state is such tables
needs no initialiser at all, so no check guards every touch of it. The
array stays writable, as a C# array is. For a `static readonly` field of a
type with no static constructor, the field's own word is read-only data, and
`ReadOnlyFold` turns every load of it into the table's address: the field
costs nothing to read. Anything else -- an enum element, a named constant,
an expression -- is initialised at run time as before, and checked there.


**How the tiers are decided, today.** `Lang/Opt/Escape.cs` runs after
inlining and propagation. An allocation with an immediate size whose
address never escapes (stored, returned, passed to a callee whose parameter
escapes, or passed to an indirect callee) becomes a frame slot; one whose
size is dynamic becomes `Alloc` paired with `Free` on every exit path.
Callees are summarised bottom-up over the call graph so an object handed
to `Runtime.Print` or a helper that only reads it does not escape. A cycle
of calls (a recursive family) is summarised as a whole, to the least fixed
point: every parameter on the cycle starts as staying put, and the members
are summarised again until nothing changes, so a value that only travels
round the cycle does not escape and one kept anywhere on it does.

Tier 2 has two rules today, both in the same pass:

- *Fresh returns.* A function whose every return hands over an object it
  made -- or one a fresh callee handed it, or null -- and which lets that
  object go no other way is summarised as returning fresh. At a call to it
  the result is an owned allocation of the caller: `Free` at its last use,
  and in a loop the previous result is given back *after* the call, since
  the call may read it (`x = Grow(x)`). Chains of helpers compose. A result
  assigned to a variable that has another definition too (the loop-carried
  `x` of that example) is not followed by this rule; the next one takes it.
- *Owned variables* (`EscapeVariables.cs`). A variable assigned again and
  again (`s = s + part`, `x = Grow(x)`) whose every value is null, static
  data or a fresh object, and nothing it holds escapes, owns what it holds:
  each assignment gives back the previous value (`Runtime.FreeReplaced`,
  which skips the same object assigned twice) provided nothing that could
  still hold it is live there, and every return gives back the last.
- *Owned fields* (`EscapeFields.cs`). A reference field of an owned object
  is freed with it (`Runtime.FreeField`, just before the object's own free)
  when every piece of code that touches the field -- the owner's function,
  each callee the object reaches (a per-parameter field summary), and the
  fresh function that filled it -- stores only fresh objects there, null,
  or what it just loaded from the same field; lets nothing loaded from it
  escape; and uses the object only at constant offsets. A `List`'s array
  dies with the `List`. What a field held before it was overwritten (the
  arrays a `List` grew out of) is still the collector's.

Every one of these lifetime proofs asks liveness whether anything still
holds an object where it dies. A landing pad has no predecessors in the
control-flow graph -- an exception reaches its catch from anywhere in the
try -- so a register a handler reads before writing is taken to be live at
every point of the function, and an object a handler may read is never
freed early.

`corc --stats` prints the counts: objects in frames, objects freed by the
compiler and how many of those were fresh returns, fresh functions, and
fields freed with their owner, and reassigned variables owned.


**The collector.** `runtime/src/core/gc.cor`. A mark-sweep collector over a
free-list heap, NON-MOVING, and concurrent: nobody is ever stopped. A
collection with one thread runs start to end on it; one with several runs
while every thread keeps working, and asks each, in handshake rounds at its
next safepoint, for the part only it can do -- record where the objects in
its allocation buffer start, mark from its own stack and registers.
Between, a snapshot-at-the-beginning barrier the compiler emits before every
store of a reference (`Runtime.Marking` tested, `Runtime.WriteBarrier`
called while it is set) reports what a store overwrites, so everything
reachable when the mark began is marked whatever is unlinked meanwhile,
and what is allocated meanwhile is kept by the sweep. The sweep then runs a
chunk at a time with the world running; a chunk found wholly dead goes back
to the operating system.

- *Precise where it can be, conservative where it must be.* A class
  instance, a box, a closure and an array are allocated through
  `Runtime.AllocObject`, which tags the block (`Gc.KindObject`); the
  collector reads such a block's references off its descriptor -- the
  reference map a class descriptor carries, or, for an array whose
  descriptor says its elements are references, the elements -- and looks
  at nothing else in it (`Gc.ScanObject`). The map names every field a
  store treats as a reference: a class, an interface, an array, a string, a
  struct's block, and a field typed `object` or by a type parameter. Strings and arrays of bytes,
  characters and floating point come from `Runtime.AllocLeaf` and are never
  read at all. Everything else the compiler allocates -- a struct's block, a
  nullable or captured-variable cell, a coroutine's frame -- is scanned word
  by word, as are the stacks, the registers and the statics. A block whose
  descriptor cannot be read (no vtable yet, a word that is not one) falls
  back to word-by-word, which is never wrong.
- *Paced by the machine.* The next collection comes when between one and
  three times the live size has been allocated since the last
  (`Gc.NextThreshold`): at least the live size, the old pace and all a
  small board can spare; up to three times it where half of what the
  operating system reports free (or half of what a heap cap leaves) allows.
  On 32-bit, never more than half of what the address space leaves: the
  heap is kept under 3.25 GB under a 64-bit kernel, whose 32-bit processes
  have all four gigabytes, and under 2.5 GB under a 32-bit kernel's three
  (`Gc.HeapCeiling`, set as the heap starts from `Platform.AddressSpace`),
  because a process's code, stacks and tables share that space. Under a hard limit --
  the ceiling, or a cap -- the floor of one live size gives way: near the
  limit collections come sooner, rather than the heap outgrowing it. And
  the ceiling is where growth waits for a collection: a chunk that would
  pass it, with a pace's worth allocated since the last cycle, is refused
  until a major collection has run (`Gc.GrowthWaits`); straight after one,
  growth is allowed, so the ceiling slows a heap and never stops one. A
  chunk the system will not give is answered with a collection, never a
  failure while one could help.
- *A mark queue sized to the heap.* The grey queue starts at 256 KB; a
  cycle that overflows it (and finishes by rescanning the heap, which needs
  no memory) asks the next cycle for four times it, as far as a
  thirty-second of the heap (`Gc.FitMarkQueue`). A small heap never grows
  it; a compiler's does, once.
- *Its own memory is the system's.* The queue, the barrier ring, the chunk
  index and the free-memory probe are mappings of their own, never heap
  blocks, and nothing on the collection path allocates -- nor maps: a
  chunk's start table is mapped with the chunk, because the collection
  that would have to map it is the one the address space ran out for.

**Memory is a pace, never an answer.** The toolchain builds the system on a
machine of 128 MB as on one of many gigabytes, and builds the SAME system:
memory decides how work is batched and how much runs at once, never what is
decided. The collector's pacing changes when it runs, not what a program
computes. The build admits units by what the process can hold
(`ProjectCompile`, `Lto.MachineMemory`), which reads `GC.GetGCMemoryInfo()`
-- whose total is the machine's memory, a heap cap, or the 32-bit heap
ceiling, whichever is least -- so a 32-bit compiler on a large server runs
fewer units at once instead of running out of address space. Every limit
that shapes output is a fixed constant.

**Strings.** A string cannot change, so everything about it that costs
anything is done once. A literal is static data (tier 0), and its header
carries its hash, worked out by the compiler. Any other string works its
hash out the first time it is asked and keeps it in the four header bytes
after the count, which the header pads anyway (`Runtime.StringHashCode`;
0 means not yet, and a hash that comes out 0 is kept as 1). The hash is
32-bit FNV-1a over the code units -- one 32-bit multiply per unit -- and
every whole-string hash (`string.GetHashCode`, `StringComparer.Ordinal`,
the tables' key hash) is that one. No output depends on a hash's value: the
compiler hosted by .NET, whose string hashes are randomised, and the
compiler compiled by itself write the same bytes.

**The contract between the compiler and the memory library.** Named by
mangled name and arity; the library provides them.

- `Runtime.Alloc(long) : long` -- a zeroed, 8-aligned block, scanned word by
  word. `Runtime.AllocLeaf` -- the same, never scanned.
  `Runtime.AllocObject` -- the same, scanned by the descriptor its first
  word will name; called exactly where the compiler stores a vtable next.
  All three bump in a thread's own buffer and enter the locked slow path
  only to refill it or to collect.
- `Runtime.AllocManual(long)` and `Runtime.AllocManualObject(long)`,
  `Runtime.FreeManual(long)`, `Runtime.ManualObject(long)` -- malloc and free
  for a program that needs no collector: escape analysis retargets every
  allocation here, and the collector's free and liveness test, when nothing
  reachable is left for a collector; the barriers, card marks and safepoint
  calls are dropped, no stack maps are written, and the collector goes
  unlinked.
- `Runtime.Free`, `Runtime.FreeReplaced`, `Runtime.FreeField` -- the frees
  the compiler inserts for tiers 1 and 2; a pointer outside the heap, or 0,
  is ignored.
- `Runtime.Marking` and `Runtime.WriteBarrier(slot, value)` -- the
  concurrent mark's barrier (above). `Runtime.WriteBarrierValues(old, value)`
  is its form for a store the optimiser has replaced with registers.
- `Runtime.Cards`, `Runtime.CardMark(slot)` and
  `Runtime.CardMarkObject(payload)` -- the generational card marks (below):
  the table, or 0 where there are no generations; the mark after a store of
  a reference; every card of a coroutine's machine after a suspension.
- Small blocks (to 256 bytes) come from exact-size free lists; larger free
  blocks from lists in eight bins to each power of two, with a bitmap of
  the non-empty bins (a two-level segregated fit): a fit looks at the first
  few blocks of its own bin, then takes the head of the next non-empty one,
  so linking, unlinking and fitting cost the same whatever the heap holds.


**The stack map table, as emitted today.** Its own section,
`.corsac.stackmaps`, read-only data bracketed by the object-local symbols
`__corsac_stackmaps` and `__corsac_stackmaps_end`. Each object owns one complete
table, including its own relative bitmap offsets; call sites whose frames hold
the same slots share one bitmap, so an offset may be named by many entries (a
reader follows it, never assumes the pool runs in entry order). The boundaries are not global
definitions: independently compiled objects must not collide or bind to each
other's table. The allocated bytes merge into `.rodata` in the final image.
The current conservative collector does not consume these tables. A future
image-wide precise collector must enumerate every table through a linker-built
directory or explicit registration; choosing one object's table is incorrect.
Nothing executes this data, and a program without a collector pays only its bytes.

The layout is version 6, written by `StackMapTable` in the linker's object
model (linker/src/model/StackMapTable.cs, which has it in full), shared by
the code generator and by the link when it cuts duplicate bodies, and read
by the collector (`Gc.MapSite`). All little-endian, offsets from the table:

    header, 36 bytes
      +0   magic 'CSM1' (0x314d5343)        +4   version, 6
      +8   function count                   +12  call-site count
      +16  the BASE: the address of the first function in this object
           that has a call site -- the table's one relocation
      +20  checkpoint count                 +24  the span: code bytes from
                                                 the base the table covers
      +28  the stream's offset              +32  the bitmap pool's offset

    checkpoints, 12 bytes, one every 32nd call site, ascending
      +0   the anchor that site's delta counts from (a code offset)
      +4   the site's offset in the table   +8   its function record's

    the stream, all ULEB128, per function in address order
      start - anchor (the anchor: the previous function's last return
        address, 0 at first; the start becomes the anchor)
      frame size << 3 | saved registers (bit 0 EBX, 1 ESI, 2 EDI)
      the IR frame slots' bitmap reference
      per call site: return address - anchor (never 0; it becomes the
        anchor), then bitmap reference << 4 | live registers | 8 for a
        call with no map
      0, the end of the function's sites

    the pool: per bitmap, a byte count and the bytes; bit b of byte k is
      the word at [EBP - 4*(8k + b + 1)]

A bitmap reference is odd for a bitmap held inline (ref >> 1, bit b the
word at [EBP - 4*(b + 1)], up to sixteen words) and even for one in the
pool (ref >> 1 its offset there); pooled bitmaps are stored once and
placed most used first, so the common references are the short ones.
Version 5 spent sixteen bytes a function and eight a call site; this is
about two bytes a site and five a function.

A return address is looked up by a binary search of the checkpoints for
the last anchor below it, then a forward read of at most thirty-two sites
until it is reached or passed; a frame whose return address is not in the
table is a frame this compiler did not emit, and is read whole. The
runtime keeps one index record per table, sorted once.

**Array covariance.** A Dog[] may be held as an Animal[], as C# allows, and a
store into an array whose element type is written as `object`, an interface
or a class that is not sealed is checked: nothing when the value is null or
the array is exactly the type written (one load and a compare), and
otherwise `Runtime.ArrayStoreCheck`, which throws ArrayTypeMismatchException
for a Cat. An array descriptor names its element's descriptor (word 10) for
that and for `is Animal[]` (`Runtime.ArrayOf`). A departure from .NET: a
store into an array whose element is a type parameter is not checked. In
shared generic code that element is a word of any type, List&lt;T&gt; stores into
its T[] at every Add, and the check there would cost every list on a slow
processor; a covariant array reaches a generic method's store only through a
cast .NET itself would have to check at the store.

**The interim rule for what is a reference.** On x86 a reference and an
`int` are both `IrType.I32`, so the IR does not distinguish them. Until it
does, a stack map lists EVERY live 32-bit value at the call except the two
halves of a 64-bit integer, which the selector marks as it makes them, and
except a value the allocator re-makes from a constant. The table is
therefore a SUPERSET of the truth: safe for a non-moving collector to scan,
and not safe to move on -- which is why the collector does not move
anything and reads the stacks word by word rather than through these
tables. Narrowing it means tagging reference-typed IR registers; the format
above does not change when that lands. (The backend's own `WriteBarriers`
and `SafepointPolls` flags stay off: the barrier the collector uses is
emitted by lowering, and threads reach their safepoints at allocation and
at blocking calls.)


**Generations.** Most of what a program allocates is dead by the next
collection, and a collection that marks the whole live heap, builds every
chunk's start table and sweeps every chunk pays for the whole heap to learn
that. So the collector is generational -- still non-moving, since the stacks
are read word by word and nothing may move -- by STICKY MARKS: a block that
survives a collection keeps its mark and scanned flags and is OLD; one
allocated since carries neither and is YOUNG. A chunk allocated in since the
last collection is young (`HeapChunks.IsYoung`); the others hold only old
blocks.

- *A minor collection* marks from the ordinary roots, from the cards, and
  from the blocks the stacks held at the last collection, and follows no
  pointer into an old chunk. It builds start tables for, and sweeps, only
  the young chunks; an old chunk's live counts are carried from its last
  sweep and its free lists are left alone. Survivors are stamped marked and
  become old where they stand.
- *The cards* are a byte for each kilobyte of the address space
  (`Runtime.Cards`, four megabytes reserved, a page committed for every four
  megabytes of heap). The compiler sets one after every store of a
  reference into memory -- after, so no collection can clear it between the
  mark and the store: `Runtime.CardMark(slot)` stays a note to the collector
  through the lifetime passes and is written out by the last pass
  (`CardMarks`) as a load of the table, a test, a shift and a byte store.
  A coroutine saves its frame into its state machine word by word at each
  suspension and marks the machine's cards there once
  (`Runtime.CardMarkObject`); `Interlocked`'s reference exchanges mark
  theirs. The stores that fill a block just made -- an array initialiser's
  elements, a box, a cell, a struct's own block, a state machine's fields
  -- take no barrier (nothing in a new block is overwritten) but do take
  the card: a collection can fall between the allocation and them, find
  the block and make it old. Array copies and list shifts are element
  stores and need nothing more. An object the optimiser keeps in registers
  (`ScalarObjects`) has no field in the heap, and its card marks go with it.
- *Taken at the snapshot.* When a cycle's start tables are built, with the
  heap lock held, every set card is cleared (`Gc.TakeCards`), and a store
  made after that -- above all of an object allocated after it, which the
  cycle keeps but cannot mark -- leaves its card set for the next cycle. A
  minor cycle moves the cards it clears to a second table and reads those
  kilobytes while marking (`ScanTakenCards`), once the start tables are
  whole. In an OLD chunk every block is old and the kilobyte is read word by
  word. In a YOUNG chunk a card is taken only over an old object: every
  store that fills a new object sets a card, so nearly every kilobyte
  allocated since the last cycle has one, and none needs reading. The
  snapshot walk flags every card an old non-leaf block lies in, set or not
  (a store may land between the walk and the taking), and only a set card
  so flagged -- or one over a thread's buffer, which the walk steps over --
  is kept. It is read block by block (`ScanCardBlocks`): only the old
  blocks, and of each only the words its descriptor calls references that
  lie in the kilobyte; a leaf, or an array of integers, not at all. The young
  blocks there are never roots: read as roots, every young object a dead one
  pointed at would be kept, and made old. Nothing is queued but the young
  objects found, so a heap of written old objects does not overflow the
  mark queue.
- *And again after the first round.* A store whose barrier found marking
  off just before the snapshot reports nothing, and its card may be set
  just after the cards were taken. By the end of a concurrent cycle's first
  handshake round every thread has passed a safepoint, and no safepoint
  falls between a barrier's test and the card mark after its store; so a
  minor cycle then reads every card set since as well (`Gc.PeekCards`),
  and leaves them set for the next cycle, which must read what was stored
  after the snapshot too.
- *The blocks the stacks held.* The compiler initialises a fresh object
  without a barrier, and a collection can fall between its allocation and
  those stores; it is on a stack then. So each cycle records every block a
  stack or a register reached (`Gc.Hold`, 32,768 entries) and the next
  minor one re-scans them (`RescanHeld`). A full record makes the next cycle
  major.
- *A major collection* clears every mark as it builds the start tables and
  then runs as a whole-heap one; its survivors keep their marks. One runs
  when the live heap has grown by the pacing's measure since the last major
  (`Gc.NextIsMinor`), on `GC.Collect`, when memory ran out, and after
  anything that makes the old generation's marks doubtful: an abandoned
  cycle, an overflowed record.
- *Young objects in few chunks.* A minor cycle walks each young chunk
  whole, so what it costs is how many chunks the cycle's allocation
  touched. So after each collection a thread's buffer is refilled from the
  holes of one chunk at a time, in address order, chunks at least an
  eighth free taken in turn (`Gc.NextHole`), and only holes of four cards or
  more, so that young objects do not share cards with old blocks and make
  the next minor cycle read those. A chunk whose largest hole at its last
  sweep was smaller is passed by unwalked (`HeapChunks.LargestHole`). Only
  when no chunk is left does a
  request take an exact-size hole from the free index, wherever it lies,
  or a fresh buffer. A buffer with room always answers first: the free
  index is never consulted on the fast path. A minor sweep leaves on its list a hole nothing beside it
  died to widen (`Gc.CloseDeadRun`).
- *Paced apart.* Between minor collections a program allocates a quarter of
  what the pacing would allow between whole-heap ones, within [the minimum
  threshold, 64 MB] (`Gc.Nursery`): a small board collects its nursery often
  and cheaply, a server lets it grow.
- *Where it runs.* 32-bit, on a platform whose mappings commit only the
  pages touched (`Platform.MapsCommitLazily`): Linux. Elsewhere -- the bare
  machine, long mode -- `Runtime.Cards` stays 0, every card mark is a load
  and a not-taken branch, and every collection is major.
- *Checked on request.* `CORSAC_GC_VERIFY=1` makes every minor cycle, once
  marked, walk every old block for a pointer to an unmarked young one and
  report it with its card (`Gc.VerifyMinor`). It reads each block by the
  marker's own rules (`Gc.MarkerReads`) -- an object by its reference map
  -- so a number in an int field is never reported as a pointer.

## The runtime

Everything the compiler emits a call to lives in COR-C# source under
`lib/` and is linked by default; `corc compile program.cor` is a complete
command. The default set, in link order, is `lib/std.cor`,
`lib/rt/runtime.cor`, `lib/rt/gc.cor` (only when `Module.NeedsHeap`),
`lib/threading.cor` and `lib/sys/linux.cor`; `--lib-root` points the driver
at another tree and `--no-default-libs` turns the set off.

- `lib/rt/runtime.cor`: the contract methods -- `Alloc`, `AllocManual`,
  `Free`, `Print*`, `Exit`, `Unhandled`, the throw helpers
  (`IndexOutOfRange`, `DivideByZero`, `Overflow`, `NullReference`,
  `InvalidCast`), 64-bit division and remainder, the byte and string
  routines the prelude's `Sys.*` intrinsics fall back to (`CompareBytes`,
  `StringFormat`, `Checksum`, ...).
- `runtime/src/core/gc.cor`: the collector (Memory, above): concurrent,
  non-moving, precise for objects and conservative for roots, paced by the
  machine. It takes memory from the platform in chunks and gives it back:
  after a collection a chunk with nothing live in it is released whole
  through `Platform.Unmap` (`munmap` on Linux; in the kernel, the pages
  unmapped and their frames freed), so a program's memory follows its live
  set down as well as up. Its own failure path allocates nothing and says
  what it asked for, how big the heap was, and why the platform refused.
- `lib/threading.cor`: `Task`, `Task<T>`, the awaiters, the scheduler and
  its timers, cancellation, `Thread` and the pool. The single-threaded
  scheduler is the default so ordering is deterministic;
  `Scheduler.UseThreads(n)` turns on worker threads made with `clone(2)`
  and futex-based locks. Per-thread runtime state (the handler chain,
  the entry stack) is what the pool needs from the runtime next.
- `lib/sys/baremetal.cor`: the freestanding platform. A bump region the
  image's own start-up names (`SetHeap`) until an operating system in
  the image installs `MapHook`/`UnmapHook` -- the kernel points them at
  its page mapper -- after which every chunk is real memory mapped and
  unmapped page by page (X86-KERNEL.md "Memory").
- `lib/sys/linux.cor`: the only file that knows it is on Linux. It makes
  `int 0x80` calls through `Sys.Syscall` and provides the POSIX surface --
  files, directories, pipes, fork and exec, sockets, time, memory mapping,
  the environment -- as negative-errno results. The bare-metal variant
  replaces it with serial-port and firmware calls; nothing above it
  changes. The CORSAC variant is the kernel's system-call table.

All of these are destined for `libcorsacrt.so`; see "Shared libraries".

## Stack traces

An unhandled exception prints what .NET prints -- the type, the message,
then a line a frame:

```
Unhandled exception. InvalidOperationException: deep
   at Program.Third in program.cor:line 38
   at Program.Second in program.cor:line 44
   at Program.First in program.cor:line 50
   at _start
```

`Exception.StackTrace` and `Environment.StackTrace` give the same text.
This matters more here than on a machine with a debugger, because this
one has no debugger: a kernel that faults can name the function it
faulted in.

Two things make it work. Every prologue is `push ebp; mov ebp, esp`, so
the saved frame pointers are a linked list of callers with each return
address beside them. And the code generator writes a table saying what
each stretch of the image is, under the symbol `__corsac_frames` in
`.rodata` -- mapped where the program can read it, needing no section a
linker script has to learn about, and carried by a flat freestanding
image exactly as by an ELF.

The table is compact because it is in every image: a fixed twenty bytes
a function came to sixty kilobytes on the kernel. The addresses ascend,
so each entry says only how far its function starts past the end of the
one before, and the whole table needs ONE relocation -- the first
function's address. A name is a delta from the name before, a file is
said only when it changes, and a line program by its length (or, when
other functions carry the same one, by where the shared copy lies).
Nothing can binary-search it and nothing needs to: the only reader is a
fault, and a fault can afford a walk. The layout is `FrameTableFormat`'s, in the
linker's object model (linker/src/model/FrameTableFormat.cs).

All little-endian, all offsets from the symbol:

| part | contents |
| --- | --- |
| header | `u32` magic `'CFR5'`, `u32` count, `u32` base (relocated), `u32` strings, `u32` own line programs, `u32` shared line programs |
| an entry | a lead byte (bits 0-2 start less the previous entry's end, 7 meaning an sleb of it follows; bit 3 the file changed; bits 4-5 line program none/own/shared; bits 6-7 the size's low bits), then uleb size >> 2, sleb name less the previous name, uleb file if it changed, and for an own program uleb its length and sleb its first line less the previous own program's, for a shared one uleb where it lies among the shared programs |
| own line programs | in entry order: uleb first offset, then ops |
| shared line programs | most used first: uleb length, sleb first line, uleb first offset, then ops |
| the strings | each ending in a zero byte |

A line program's ops each move the code offset forward by A and the line by
L, never 0, in the style of DWARF's special opcodes: a byte under 160 is
A = b / 2 + 1 and L = 1 or 2; a byte from 160 to 254 and the one after it
cover A up to 506 and L from -24 to 24; 255 is a uleb A and an sleb L.
Nearly four ops in ten take one byte and all but a few of the rest two. A pair that does not change the line is
dropped, and a program more than one function carries is stored once.

At the link every table becomes `'CFR6'`: the word at +12 is the address
of the image's one name pool, `__corsac_frame_pool`, and a name or file is
the offset of a name there. The pool (`'CFP2'`, its token count, the
names' offset, a word per token for where it starts and one for where the
last ends, the tokens' bytes, then the names) spells each name as a
ULEB128 count of tokens and their indices: a token is a run of separators
(`. $ _ ( ) , [ ] < > `` ` `` / ` and space) and the run after it, and
generic names repeat in tokens far more than whole, so most of a name is
one-byte indices of words stored once. After the names the pool holds
the line programs the image's tables share -- the same generic body in
forty units is one program -- and each `'CFR6'` table's +20 is where they
start in the pool.

`Sys.FrameTable()` is the address of the table and `Sys.FramePointer()`
the current frame; `Runtime.Trace(frame)` walks the one and looks each
return address up in the other. The frame is PASSED IN rather than read
inside the walk, which is not fussiness: whether a helper is inlined into
its caller changes how many frames lie between it and the one that
matters, and a trace that is right only while the optimiser leaves it
alone is not right. For the same reason the compiler reads the throwing
function's own frame pointer and hands it to `Runtime.Capture` at every
`throw` -- but never at a bare `throw;`, because a rethrow keeps the
trace the first throw recorded, which is what C# promises and the whole
reason the two are spelled differently.

An inlined frame is not a frame, here or in .NET. A trace over a small
method that the optimiser folded into its caller names the caller, and a
test that asserts otherwise is really asserting that nothing was
inlined.

`Runtime.SayTrace(fd, frame)` is the same walk with NO HEAP AT ALL,
written straight to a descriptor. `Trace` answers a string, which is
exactly what the failure path of an allocation may not ask for -- and the
report that matters most is the one printed when there is no memory left
to print it with. Every name is already in the image, one
zero-terminated run each, so each is written where it lies with
`Platform.WriteBytes` and nothing is built; line numbers are left off,
because a line means decoding a second program for every frame and what
that report is for is which functions were on the stack. The heap's own
out-of-memory stop (`Gc.OutOfMemory`) prints its message and then this.


## Enum names

An enum is a four-byte number with a symbol beside it, and by the time a
program runs the names have gone -- so `Colour.Green.ToString()` had
nothing to answer with. .NET reads them out of the type's metadata;
there is none here, so the code generator writes them down.

One table per enum a program ever renders, and none for the rest: the
names in a `string[]` under `en_<type>` and their values in an `int[]`
under `ev_<type>`, both in `.rodata`, both local to the module, both
**in value order** -- which is the order .NET reports an enum's members
in, and what lets a set of bits be taken apart largest member first. The
names are the same interned literals the program's own strings are, so a
table costs a word and four bytes a member and nothing for the text.

They are ordinary managed arrays because everything that reads them is
written in the language: `Runtime.EnumName(names, values, flags, value)`
walks them the way any other code walks a `string[]`.

ONE MECHANISM FOR ALL OF THEM. `e.ToString()`, `"" + e`, `$"{e}"`,
`string.Format` and a boxed enum's ToString all end in that call --
`e.ToString()` is rewritten to `"" + e` in the binder and the empty
string folded away in the lowering. Before, `ToString` expanded to a
switch at each call site and said "Green" while every other spelling of
the same thing said 5.

`[Flags]` is the one attribute the compiler reads. The parser keeps the
NAME of every attribute it steps over (`Parser.SkipAttributes`), the
binder records it on the enum, and a header and a `.gir` carry it --
because an enum marked with it is a set of bits, and a value no single
member has is written as the members it is made of, in value order, as
`Read, Run`. A value with bits no member accounts for is the number.

System.Enum's statics read the same table, and there is no `Enum` class
anywhere: the binder recognises the calls and the code generator emits
them, in both the spellings .NET has -- `Enum.GetName<Colour>(c)` and
the older `Enum.GetName(typeof(Colour), c)`. `GetValues` and `GetNames`
become the array C# would have written, because .NET's answer is a fresh
array the caller may write to; `GetName`, `IsDefined`, `Parse` and
`TryParse` are one call each into `Runtime`. `HasFlag` is neither: it is
`(value & flag) == flag` in two instructions.

## What is not being done

- Optimisation beyond the cheap passes listed. No inlining, no loop
  optimisation, no scheduling. The design leaves room for them.
- x87 register-stack allocation.
- Anything Pentium or later. `cpuid`, `cmov`, `rdtsc` and MMX are refused.

## Core rule: programs are regular .NET C# programs

This is the contract every library under `lib/` and every program under
`os/` is written against, and it outranks convenience everywhere:

**Programs are ordinary .NET C# programs. Our platform changes to mirror
C# and .NET 1:1.** The goal is that nearly any existing C# source compiles
to native code through COR-C# and runs, unchanged, on Linux or on
CORSAC-OS.

What that means in practice:

- Library namespaces, class names, method names, overloads and behaviour
  are .NET's: `System.IO.Stream`/`FileStream`/`StreamReader`,
  `System.Console`, `System.Environment`, `System.Net.Sockets.Socket`/
  `TcpClient`/`NetworkStream`, `System.Net.IPAddress`/`Dns`,
  `System.Security.Cryptography.RandomNumberGenerator`,
  `System.Runtime.InteropServices.PosixSignalRegistration`,
  `System.Diagnostics.Process`, and so on. When in doubt, the .NET
  reference documentation is the specification.
- We never invent our own API where .NET has one. A class that is not a
  .NET class is rewritten to the .NET one, not wrapped by it.
- Programs make no operating-system calls of their own. The `Os` class in
  `lib/sys/linux.cor` (and its CORSAC twin) is the platform layer the
  library is built on, not something a program touches.
- The compiler is never changed to suit a library or a program; the
  library or program is rewritten to what C# would be.
- The only classes of our own are for the things .NET has no API for at
  all: raw terminal mode (`Terminal`), I/O ports, the boot environment,
  the kernel itself.
