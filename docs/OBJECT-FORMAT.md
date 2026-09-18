# Compiler/linker intermediate objects

The compiler/linker/runtime responsibility split, managed declaration design,
LTO contracts and bare-metal build flow are specified in
[SEPARATE-COMPILATION.md](SEPARATE-COMPILATION.md).

## Executables and ownership

`corc` is the compiler executable. `corlink` is the independent linker
executable. `corc compile --obj ... -o unit.o` produces a relocatable object;
`corlink unit.o support.o -o program --entry _start` consumes objects without
loading the parser, binder or optimizer. Existing compile-and-link and
`corc link` commands remain convenience frontends to the same linking engine.
The project build pipeline will use separate compilation and link nodes.

The linker owns its object model, ELF reader/writer and relocation engine.
The compiler references that project for object emission and convenience linking;
the linker must not reference the compiler. This prevents a project cycle and
makes independent linker builds possible.

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
metadata format must have a version and bounds checks; readers reject unknown
versions and truncated records. Generic templates and declaration metadata
already have compiler-specific representations, but their presence in every
ordinary object and independent-unit compatibility are not yet guaranteed.

## Separate-compilation extension work

### Native ABI contract, version 1

.corsac.abi is a 24-byte non-loadable section: four magic bytes CABI followed
by five little-endian uint32 values: version 1, pointer size 4, baseline CPU 486,
calling convention 1 (i386 cdecl with x87 floating returns), and TLS/platform
model (0 hosted Linux GS, 1 bare-metal static block, 2 bare-metal GS). corlink
rejects incompatible models and unknown contracts before LTO or output creation.
Native assembly/C objects may omit this managed-compiler contract; omission is
not permission to invent managed type identity. Assembly/type metadata remains
the separately specified .corsac.unit/.corsac.types work.

### Managed layout assumptions, version 1

The non-loadable `.corsac.layout` section begins with uint32 magic CMLY,
int32 version 1 and int32 record count. Each record is an int32 UTF-8 name byte
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

### LTO summary encoding, version 1

The non-loadable .corsac.lto section begins with the four bytes `CLTO`, followed
by little-endian uint32 version (1), total section length, constant-return count
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

Generic template serialization is now GIR major version 8: Block records carry
a checked/unchecked/inherited arithmetic-context byte. Older versions are
rejected rather than interpreted with missing overflow semantics. This is
compiler metadata versioning; ordinary ELF machine-object encoding is unchanged.

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
  program AST and IR merely to link independently compiled objects. Version 1
  now implements constant-return summaries and certified direct call sites;
  general IR importing and cross-unit inlining remain outstanding.

Source headers currently generated by the compiler are derived artifacts, not
handwritten source lists or an alternative C# syntax. The durable direction is
indexed metadata with source language semantics unchanged.

## Acceptance

First acceptance is native machine-object interoperability: compile --obj,
invoke corlink as a separate process, run the linked executable, and exercise
cross-object calls and invalid inputs. GNU binutils interoperability remains
part of linker tests. Managed independent compilation has separate acceptance:
mutually referencing files, generics/partial types, incremental rebuilds,
native self-compilation and measured bounded memory.
