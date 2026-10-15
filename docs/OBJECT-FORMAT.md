# Compiler/linker intermediate objects

The compiler/linker/runtime responsibility split, managed declaration design,
LTO contracts and bare-metal build flow are specified in
[SEPARATE-COMPILATION.md](SEPARATE-COMPILATION.md).
The indexed IR/native-backend protocol is documented in
[IR-LINK-OPTIMIZATION.md](IR-LINK-OPTIMIZATION.md), and shared-definition
certificates in [DEFINITION-OWNERSHIP.md](DEFINITION-OWNERSHIP.md).

## Executables and ownership

`corc` is the toolchain executable and linking is one of its commands.
`corc compile --obj ... -o unit.o` produces a relocatable object;
`corc link unit.o support.o -o program --entry _start` consumes objects. The
project build pipeline uses separate compilation and link nodes.

The linker owns its object model, ELF reader/writer and relocation engine in
a library project of its own, and must not reference the compiler. That is
what keeps linking free of the parser, binder and optimizer, and it is a
property of the project graph rather than of how many programs are installed.

## Existing binary contract

The intermediate file is a standard little-endian ELF32 ET_REL object with
EM_386 machine identity for the x86 backend, conventionally named `.o`. There
is no new opaque container or alternative object suffix. It contains code,
read-only data, initialized data, zero-filled data, symbol/string tables, and
relocation sections. The current implementation is in linker/src/elf.

Supported i386 relocations include absolute and PC-relative references and
the implemented GOT/PLT forms. The exact accepted encodings are defined by
ElfReader/ElfWriter and documented against the i386 ELF ABI. Unknown formats,
unsupported relocations, unresolved required symbols, conflicting definitions,
and invalid entry points fail with input-specific diagnostics. Internal
relocation forms which have no ELF encoding must be resolved or rejected by
the writer rather than serialized as invented standard relocation numbers.

Metadata travels in named non-loadable sections where supported. A binary
metadata format has one layout, version 0, and bounds checks; readers reject
any other version and truncated records. Until a release nothing reads an
older layout: objects, indexes and images from an older toolchain are rebuilt. Generic templates and declaration metadata
already have compiler-specific representations, but their presence in every
ordinary object and independent-unit compatibility are not yet guaranteed.

## Separate-compilation extension work

### Native ABI contract

.corsac.abi is a 28-byte non-loadable section: four magic bytes CABI followed
by six little-endian uint32 values: version 0, pointer size 4, baseline CPU 486,
calling convention 1 (i386 cdecl with x87 floating returns), and TLS/platform
model (0 hosted Linux GS, 1 bare-metal static block, 2 bare-metal GS), and required
metadata flags. Flag 1 requires `.corsac.layout`; all other bits are rejected.
Compiler-produced objects set flag 1. Images register an image-wide unit
metadata directory rather than a single unit's frame table. The linker
rejects incompatible models and unknown contracts before LTO or output creation.
Native assembly/C objects may omit this managed-compiler contract; omission is
not permission to invent managed type identity. Assembly/type metadata remains
the separately specified .corsac.unit/.corsac.types work.

### Managed layout assumptions

The non-loadable `.corsac.layout` section begins with uint32 magic CMLY,
int32 version 0 and int32 record count. Each record is an int32 UTF-8 name byte
length, name bytes and 32-byte SHA-256 compiler fingerprint. Names are nonempty,
NUL-free and at most 4096 bytes. Duplicates, bad versions/lengths, truncated data
and trailing bytes fail. The linker compares overlapping assumptions before LTO
mutates code and before ELF or flat layout. Missing sections remain permitted
for native assembly/C inputs.

The initial compiler producer records non-generic, non-specialized type layout
(instance size, base/depth, modifiers, instance fields and dispatch slots), plus
independent static-field and native-method ABI facts. Independent records allow
additional static helpers without falsely changing an object's instance layout.
Generic identities/specializations and definition ownership are still separate
work; this is a consistency guard, not a complete managed loader contract.

### What a linked image keeps of the contracts

`.corsac.abi`, `.corsac.cpu` and `.corsac.layout` are for the link alone
(`Linker.ConsumedContract`). Every link checks them before it lays anything
out, then passes over them: no executable, flat image, shared object or module
carries them, and the objects keep theirs, fit to be checked and linked again.
A module's link takes the kernel's contracts from the exports file the
kernel's link wrote (`corc link --exports`), which records them itself, never
from the kernel image. One copy per input in an image would be no contract,
and the managed layouts alone were most of a small program's file.

### LTO summary encoding

The non-loadable .corsac.lto section begins with the four bytes `CLTO`, followed
by little-endian uint32 version (0), total section length, constant-return count
and call-site count, then a 32-byte SHA-256 hash of canonical .text bytes. The
header is 52 bytes. Canonical code replaces every four-byte relocation field
with zeros: ELF REL serializes addends in those fields, so hashing their raw
pre-emission values would not survive an object round trip.

A constant-return record contains uint32 UTF-8 symbol byte length, symbol bytes,
int32 return value and a 32-byte canonical function-code hash. Its contract is
ordinary zero-argument i32 ABI, side-effect-free, finite and nonthrowing.
A call record contains uint32 relocation-field offset, uint32 UTF-8 symbol byte
length and symbol bytes. It certifies a direct zero-argument i32 call emitted
by the backend; instruction-looking data is not inferred as a call.

Records are sorted by symbol name and call offset respectively. Empty/duplicate
names, invalid UTF-8, duplicate offsets, trailing/truncated data, unsupported
versions, hash mismatches and relocation mismatches are rejected. Call records
must point at the rel32 field immediately following E8, with addend -4 and the
specified symbol. The first pass replaces E8+rel32 with B8+imm32 and removes the
relocation; original function bodies remain. Dynamic/PIC objects do not publish
these closed-world summaries. --no-lto disables transformations but retains
metadata validation.

Generic template serialization (GIR, version 0): throw records retain the
bare-rethrow flag so specialization preserves exception traces. Block records carry
a checked/unchecked/inherited arithmetic-context byte. Any other version is
rejected rather than interpreted with missing overflow semantics.

The following remain required before file-by-file managed compilation is
accepted. Existing ELF objects alone do not establish these properties:

- Indexed declarations, assembly/type identity and ABI/layout facts, available
  without parsing every source file or materializing all declarations.
- Generic template bodies available on demand, with deterministic ownership of
  instantiations and duplicate-safe linking.
- Stable cross-unit type descriptors, interface identities, static initialization
  and managed-runtime references. Current whole-program numbering must not leak
  into independent compilation as an unstable ABI.
- Dependency fingerprints which distinguish declaration/layout changes from
  implementation changes and drive correct incremental invalidation.
- Optional compact optimization summaries. LTO must not reconstruct the full
  program AST and IR merely to link independently compiled objects. The summary
  implements constant-return summaries and certified direct call sites;
  general IR importing and cross-unit inlining remain outstanding.

Source headers currently generated by the compiler are derived artifacts, not
handwritten source lists or an alternative C# syntax. The durable direction is
indexed metadata with source language semantics unchanged.

## Acceptance

First acceptance is native machine-object interoperability: compile --obj,
invoke `corc link`, run the linked executable, and exercise
cross-object calls and invalid inputs. GNU binutils interoperability remains
part of linker tests. Managed independent compilation has separate acceptance:
mutually referencing files, generics/partial types, incremental rebuilds,
native self-compilation and measured bounded memory.
