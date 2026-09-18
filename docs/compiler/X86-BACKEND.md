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

**What the language sees.** An async method returns `Task`, `Task<T>` or
`void`. It runs synchronously until it awaits something not yet complete,
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

Memory management is invisible to the programmer, as in real C#, and it
is the compiler's job. A program never frees anything, never sizes a
heap, and never names an allocator: `new` is all there is. What makes
that painless, fast, resistant to fragmentation and infallible is that
the compiler and the collector are designed together. This is not a
C-alike with a library bolted on; the collector is part of the toolchain.

**The design: a precise, generational, moving collector.**

- **Precise.** The collector knows exactly which words are references.
  Every object already carries a type descriptor; the descriptor gains a
  reference map (which fields hold pointers), arrays of references are
  marked as such, and the backend emits a stack map at every call site
  and safepoint saying which stack slots and registers hold references.
  Nothing is retained by accident, so a program that drops its last
  reference gets the memory back -- infallible in the sense that leaks
  through false retention cannot happen.
- **Moving.** Because every reference is known, objects can be moved.
  The young generation is a copying nursery: allocation is a pointer
  bump into a thread-local region, inlined by the compiler as a few
  instructions with a call only on the slow path, and a nursery
  collection copies the survivors out. The old generation is compacted
  when it fragments. Fragmentation is therefore a condition the collector
  removes, not one the program lives with.
- **Generational.** Most objects die young and are never copied at all.
  The compiler emits a write barrier on every store of a reference into
  a heap object -- a card mark, a few instructions -- so the old
  generation's pointers into the nursery are known without scanning it.
- **Safepoints.** The compiler emits a poll at loop back-edges and every
  call is a safepoint, so a collection can begin at a point where the
  stack maps are exact. On a multiprocessor this is also how every thread
  is brought to a known state.

**The stack map table, as emitted today.** Its own section,
`.corsac.stackmaps`, read-only data bracketed by the linker-visible
symbols `__corsac_stackmaps` and `__corsac_stackmaps_end`. It is
allocated read-only data like any other, so it merges into `.rodata` in
the final image and the two symbols are how the runtime finds it; nothing
executes it and a program without a collector pays only its bytes.

    header, 16 bytes
      +0   magic 'CSM1' (0x314d5343)
      +4   version, 1
      +8   entry count
      +12  entry stride, 16

    entry, 16 bytes, one per call site, in code order per function
      +0   the RETURN address of the call -- an absolute relocation
           against the function plus the offset of the byte it returns to
      +4   callee-saved registers holding references at that point, one
           bit per hardware register number (EBX 3, ESI 6, EDI 7). No
           other bit can be set: a value live across a call is never left
           in a caller-saved register.
      +8   byte offset from the start of the table to this entry's slot
           bitmap, or zero when no frame slot holds a reference
      +12  bytes of frame below EBP, so a walker can sanity-check a
           bitmap against the frame it is reading

    bitmaps, after the entries
      +0   length in words
      +4.. the bits; bit i of word k stands for the slot at
           [EBP - 4*(32k + i + 1)]

Entries are not sorted: the linker decides the addresses, so ordering is
the runtime's to do once at startup if it wants a binary search rather
than a scan. A return address is looked up by equality, and a frame whose
return address is not in the table is a frame this compiler did not emit.

**The interim rule for what is a reference.** On x86 a reference and an
`int` are both `IrType.I32`, so the IR does not distinguish them. Until it
does, a stack map lists EVERY live 32-bit value at the call except the two
halves of a 64-bit integer, which the selector marks as it makes them, and
except a value the allocator re-makes from a constant. The table is
therefore a SUPERSET of the truth: safe for a non-moving collector to scan,
and not safe to move on. That is the reason the write barrier and the
safepoint poll are off. Narrowing it means tagging reference-typed IR
registers; the format above does not change when that lands.

**Where the barriers and polls go.** Two backend flags, both false:
`X86Backend.WriteBarriers` and `X86Backend.SafepointPolls`, beside
`X86Backend.StackMaps`, which is true. The barrier belongs in Select.cs,
in the Store cases that write a word through an object pointer -- a card
mark on the object's address, a few instructions, no call. The poll
belongs in Select.cs too, where a jump to an already-emitted block is
selected: that is a back-edge, and a poll there plus a map at every call
is what makes every thread reachable at a point where the maps are exact.

**What the compiler emits, then:** reference maps in descriptors, stack
maps, inlined allocation fast paths, write barriers, safepoint polls.
These are all backend and lowering work, designed in from now rather than
retrofitted; the IR carries the information (an allocation is `Alloc`,
a reference store is a store of a word-typed reference, both are
already distinguishable) and the backend's frame layout already knows
which slots hold what.

**The collector is the last resort, not the first.** The compiler sees
every allocation, and most objects have lifetimes it can prove. Memory is
managed in tiers, and a program pays only for the tiers it needs:

1. **Local.** An object whose reference never escapes the function that
   made it -- the scratch buffer a number is formatted into, an
   enumerator, a closure called and dropped -- is allocated on the stack
   or freed at scope exit. Escape analysis over the IR decides this, after
   inlining, when the whole lifetime is in view.
2. **Owned.** An object with one owner and a last use the compiler can
   find -- stored in a field the owner drops, passed down and never
   kept -- is freed by a free the compiler inserts at that last use.
3. **Shared.** Whatever remains, where lifetime depends on data, is the
   collector's.

The collector is linked only when tier 3 is non-empty after whole-program
analysis. A hello world, whose every allocation is tier 1, links no
collector and no heap beyond a bump region. The tiers are decided by the
optimiser, which is why the memory library, the inliner and escape
analysis are designed together rather than bolted on.

**The contract between the compiler and the memory library.** The
compiler names these by mangled name and arity; the library provides them.

- `Runtime.Alloc(long bytes) : long` -- a zeroed block, 8-aligned. What
  `new` lowers to. When the collector is linked this is the collector's
  allocator; the bootstrap fast path bumps within a thread-local arena.
  Fitting free-bin hints, exhausted arenas and collection pressure enter
  the locked slow path.
- `Runtime.AllocBump(long bytes) : long` -- a zeroed block from a bump
  region that is never collected. What a program links when it needs no
  collector: every object is tier 1 or dies with the process.
- `Runtime.Free(long at)` -- returns a block from `Alloc` to the heap now,
  coalescing with free neighbours and tolerating 0. Boundary tags locate
  neighbours directly; exact-bin unlink is O(1), and large-bin AVL index
  updates are O(log n). Emitted by the
  compiler for tier-1 objects whose size is not a constant (so they cannot
  be frame slots) and for tier-2 owned objects at their last use. Present
  and correct whether or not the collector is linked.
- `Module.NeedsHeap` -- decided by escape analysis after inlining, over
  every function reachable from the entry: true iff some allocation is
  tier 3. The driver links `gc.cor` only then, and the statistics output
  (`--stats`) says which.
- Small blocks (<= 256 bytes) come from size-class free lists so a hot
  allocation is O(1) and does not fragment the general region.
- Larger free blocks are indexed by size and address in AVL trees within
  power-of-two bins. Tree metadata occupies free payload, not additional
  allocated headers. Published maximum-size hints let the allocation fast
  path decide whether reuse is possible without walking a concurrent tree;
  all tree mutations and the actual reuse occur under the heap lock.

**How the tiers are decided, today.** `Lang/Opt/Escape.cs` runs after
inlining and propagation. An allocation with an immediate size whose
address never escapes (stored, returned, passed to a callee whose parameter
escapes, or passed to an indirect callee) becomes a frame slot; one whose
size is dynamic becomes `Alloc` paired with `Free` on every exit path.
Callees are summarised bottom-up over the call graph so an object handed
to `Runtime.Print` or a helper that only reads it does not escape. Tier 2
is the next step: an object that escapes only into a tier-1 owner, or
whose last use is findable, is freed there.

**The precise collector's tables.** The reference map of each class lives
behind its descriptor and the array descriptor flags whether elements are
references; every call site has a stack map naming the frame slots and
callee-saved registers that hold references, in `.corsac.meta`. The exact
encodings are in the sections that define them. Write barriers and
safepoint polls are a backend option, off until the collector reads the
tables.

**The bootstrap.** Until the precise collector lands, the memory library
on Linux is a free-list heap with a conservative mark-sweep collector
adapted from the kernel's, scanning the stack, the registers and the
statics between the linker's `__data_start` and `_end`. It exists so
the toolchain can be brought up and tested without waiting for stack
maps; it remains distinct from the planned precise collector. Its current
allocation arenas and free-block indexes do not make marking precise.
During stop-the-world marking, per-page anchors bound interior-pointer
lookup to a nearby block boundary. They are rebuilt after allocation-buffer
retirement and disabled before sweep changes boundaries. Missing metadata
falls back to the full block walk; no possible root is discarded for lack
of an index. Anchor mappings are released with their chunks and accounted
separately from the managed heap cap. Chunk growth itself is page-rounded
and falls back from the preferred quantum to the request-sized mapping.

**The seam that survives.** Whatever the collector, the program's side
of it is one thing: `new`. On CORSAC the same collector is free to use
whatever the kernel and the hardware offer -- per-process regions the
kernel reclaims, kernel-assisted marking, an asynchronous multiprocessor
doing the copying on another core -- and the compiler's contribution is
the same maps and barriers.

## The runtime

Everything the compiler emits a call to lives in COR-C# source under
`lib/` and is linked by default; `corc compile program.cor` is a complete
command. The default set, in link order, is `lib/std.cor`,
`lib/rt/runtime.cor`, `lib/rt/gc.cor` (only when `Module.NeedsHeap`),
`lib/threading.cor` and `lib/sys/linux.cor`; `CORC_LIB` points the driver
at another tree and `--no-default-libs` turns the set off.

- `lib/rt/runtime.cor`: the contract methods -- `Alloc`, `AllocBump`,
  `Free`, `Print*`, `Exit`, `Unhandled`, the throw helpers
  (`IndexOutOfRange`, `DivideByZero`, `Overflow`, `NullReference`,
  `InvalidCast`), 64-bit division and remainder, the byte and string
  routines the prelude's `Sys.*` intrinsics fall back to (`CompareBytes`,
  `StringFormat`, `Checksum`, ...).
- `lib/rt/gc.cor`: the collector. Today the conservative bootstrap; the
  precise one replaces it behind the same `Alloc`. It takes memory from
  the platform in chunks and, since 2026-09-17, gives it back: after a
  collection a chunk with nothing live in it is released whole through
  `Platform.Unmap` (`munmap` on Linux; in the kernel, the pages unmapped
  and their frames freed), so a program's memory follows its live set
  down as well as up. Its own failure path allocates nothing and says
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
a function came to sixty kilobytes on the kernel, and these entries
average nine. The addresses ascend, so each is the distance from the one
before it, and the whole table needs ONE relocation -- the first
function's address. Nothing can binary-search it and nothing needs to:
the only reader is a fault, and a fault can afford a walk.

All little-endian, all offsets from the symbol:

| part | contents |
| --- | --- |
| header | `u32` magic `'CFRM'`, `u32` count, `u32` base (relocated), `u32` strings, `u32` lines |
| an entry | uleb start delta, uleb size, uleb name, uleb file, uleb line program plus one (zero meaning none) |
| a line program | uleb count, then that many (uleb offset delta, sleb line delta) |
| the strings | each ending in a zero byte |

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
