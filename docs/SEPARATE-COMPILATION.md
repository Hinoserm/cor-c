# Separate compilation and link-time optimization

## Responsibilities

The compiler owns C# parsing, name lookup, binding, generics and native code
generation. It consumes indexed declarations, emits one bounded compilation
unit and publishes code, managed metadata and optimization summaries. A source
file is the normal scheduling unit, not an assembly or a new type-identity scope.
Partial types may require declaration merging across files before body work.

The linker owns symbol resolution, ABI/type consistency checks, static layout,
LTO planning and final ELF/shared/flat output. It does not parse C# or guess the
meaning of unresolved managed names. Optimization requiring code generation uses
a compiler backend service with a bounded import plan; the linker executable
must not acquire a dependency on the compiler frontend.

The managed loader resolves explicitly deferred assembly/type references,
registers canonical descriptors and generic dictionaries, and applies runtime
loading policy. The runtime performs dispatch, type checks, initialization and
reflection over those definitions. Runtime-created types require a separate
code-generation/interpreter facility; ELF linking alone does not implement it.

## Compilation sequence

1. Evaluate the standard project and its references for one configuration and
   target framework. Assign assembly identity and a stable declaration identity
   to every type/member; record project/compiler/runtime ABI fingerprints.
2. Index declarations without retaining ordinary method bodies. Namespace and
   using scopes drive candidate lookup, with normal qualified/alias/extension
   semantics. Shared partial declarations have one indexed identity.
3. Compile a unit by loading its bodies and only needed declaration records.
   Emit managed/native references and summaries. Release its transient trees,
   binding maps and IR after publication; workers share bounded declaration caches.
4. Thin-link summaries and symbol/type requirements. Produce per-unit import
   plans and deterministic ownership decisions for generic specializations.
5. Recompile only selected units requiring IR imports, with explicit body/byte
   budgets and a global memory/worker limit. Retain ordinary native code for
   units which need no backend work.
6. Resolve relocations and publish the final image and retained runtime metadata.

An ELF object can be linked today without this entire managed sequence. That
does not imply independent managed type-layout or generic compilation works.
The implementation checklist distinguishes the stages.

### Declaration indexing and cycles

Project evaluation publishes one immutable declaration-index generation before
body workers start. Index construction visits every participating source file,
but does not bind or retain its method bodies. A source scanner records body
spans and lexical scopes while parsing declarations. It merges partial-type
headers by canonical identity, checks conflicting declarations, and publishes
the generation only after that merge completes. A project source change during
construction invalidates the generation; workers never combine generations.

The on-disk index has sorted lookup tables for fully qualified types, namespace
members, nested types, member names/arities, and extension-method candidates.
Lookup reads bounded pages rather than deserializing every declaration. A hash
index alone cannot implement namespace/member candidate enumeration. Each hit
names the owning artifact and declaration offset/length; the body location is
a separate record. Cache keys include generation and full identity. An evicted
declaration can be reloaded without changing its identity or numbering.

Mutually referring classes therefore do not require a source-file build order:
both headers exist before either body is bound. Layout computation tracks
unseen/in-progress/complete states by identity. A reference to an in-progress
reference type is legal; recursive by-value layout or an inheritance cycle is
a language error. Layout and generic-constraint traversal load only required
declarations and keep a diagnostic dependency path. Partial method/type bodies
stay in their source units; merging headers does not merge every syntax tree.

A worker pins its required declaration records and body IR while compiling.
The scheduler accounts for pinned bytes, cache bytes and backend scratch space.
It reduces concurrency before exceeding the configured memory budget; if one
unit cannot fit, it reports that unit and measured requirement rather than
silently loading the whole project. One-worker execution uses the same index
and ownership rules. The initial implementation may still have a minimum unit
size; streaming an arbitrarily large method is a separate capability.

## Artifact division

The exchange container is ELF32 ET_REL on x86. Native code and relocations
remain standard ELF. Compiler sections are non-loadable unless the linker
explicitly materializes a runtime table. No source-language replacement or
hand-maintained header is introduced.

| Section | Purpose | Current status |
| --- | --- | --- |
| .text/.rodata/.data/.bss | Native fallback code and storage | Implemented |
| .symtab and relocation sections | Native definitions/references | Implemented |
| .corsac.lto | Versioned summary-only optimization records | First implementation in this milestone |
| .corsac.abi | Native calling convention and hosted/bare-metal TLS contract | Implemented |
| .corsac.unit | Target/assembly/ABI identity and compilation settings | Specified, pending |
| .corsac.types | Indexed managed declarations and layout requirements | Specified, pending |
| .corsac.generics | Indexed template and instantiation records | Specified, pending integration |
| .corsac.ir | Independently addressable optimized IR function bodies | Specified, pending |
| .corsac.roots | Explicit retention and deferred-resolution contracts | Specified, pending |

GIR is the existing generic syntax representation, not serialized optimization
IR. Its major version is 8. The generic index must identify GIR version per
template payload and reject incompatible input rather than reinterpret it.

All new indexed sections use little-endian fixed-width integers, section-relative
offsets, an explicit version, total byte length, record count and record width.
Offsets and lengths are range-checked before allocation/read, including integer
overflow. Strings are length-prefixed UTF-8 with no embedded NUL; indices are
zero-based with 0xffffffff denoting no reference. Unknown mandatory records,
flags and versions fail; optional extensions require explicit optional tagging.

## Managed identity and declaration tables

Assembly identity is the standard name/version/culture/public-key-token tuple.
Type identity consists of assembly identity, namespace, enclosing type chain,
name and generic arity. Constructed types additionally identify their generic
arguments. Runtime canonicalization includes the load context; a linker hash
alone does not make two assemblies in different contexts the same runtime type.
Use a 256-bit digest as an index key, retain the complete identity for collision
checking, and never assign semantic identities by input enumeration order.

The .corsac.unit record contains format/runtime ABI versions, assembly identity,
unit identity, target triple/profile, minimum CPU features, pointer width,
endianness, calling convention, exception/TLS/GC modes, declaration-index hash
and compiler-option fingerprint. Hosted and bare-metal profiles cannot be mixed
accidentally; neutral native assembly may omit managed records.

The initial implemented .corsac.abi section already enforces the native
hosted/bare-metal/TLS boundary. Its exact encoding is in OBJECT-FORMAT.md; it
does not substitute for the assembly and managed-layout identity records.

The .corsac.types index is sorted by identity key. Each index record gives the
identity-string offset, declaration offset/length and declaration fingerprint.
A declaration contains accessibility, kind, generic constraints, base/interface
identity keys, layout policy, size/alignment where fixed, field records, method
signatures, virtual/interface slots expressed by stable member identities,
initialization policy, and reflection visibility. References distinguish
declared-but-external from owned definitions. Duplicate owned definitions fail
unless a defined generic/COMDAT rule proves equivalence.

Layout dependency hashes include base layouts, packing, field types/order,
calling convention and GC reference maps. A missing mandatory layout cannot be
deferred merely because the symbol name is known. Runtime-dependent layouts
must use an explicit dictionary/descriptor access rather than a guessed offset.

Class identity already uses descriptor addresses and inheritance depth rather
than the old whole-program ancestor masks. Interface slots still need a common
reservation plan: the indexed path now reads compact family counts independently
of loaded declarations. Full managed acceptance additionally requires canonical
ownership and complete type contracts; a machine-object test alone is not proof.

## Generics, static initialization and open-world references

Generic templates have indexed payloads and constraints. Instantiation keys
include canonical type arguments and ABI options. Shared reference-type bodies
use explicit dictionaries for context-sensitive operations. Value-type bodies
specialize when size/layout requires it. The link planner assigns one owner per
required specialization and canonical descriptor; caches may reuse matching
artifacts without changing ownership.

Static constructors retain C# initialization semantics, including beforefieldinit
where applicable, exactly-once execution, synchronization and failure caching.
The linker may order initialization tables only within those rules; it cannot
eagerly run every constructor or deduplicate state by coincidental byte equality.

The initial thread block is owned by the program-entry unit or the unit defining
the runtime, not independently allocated by every object. Library exception/TLS
references resolve to that owner. Minimal hosted --nostdlib programs which do
not initialize the thread block must not read GS as if a runtime initialized it.

Stack-map and frame-table boundaries are object-local. Their internal offsets
remain relative to their owning table after linking. The final managed-image
plan must enumerate every retained table for runtime registration; a future
precise collector cannot use only the entry unit's table. That directory and
cross-unit frame registration remain part of managed acceptance, not a reason
to disable stack-map emission for ordinary machine-object linking.

References have three explicit resolution policies: static-required,
managed-load-deferred, and runtime-generic. A plain undefined native relocation
is static-required. Deferred references carry assembly/type/member identity and
loader requirements, not an unresolved machine address assumed safe to call.
Closed bare-metal builds reject deferred references and unavailable services.

Reflection roots, native exports, interrupt/vector entries, address-taken code,
runtime registration and loader-visible methods are explicit roots. Unknown
runtime-loaded subclasses prohibit closed-world devirtualization. A sealed or
otherwise proven exact receiver can still be optimized. Unloading must not leave
cached type descriptors or code pointers into an unloaded context.

## LTO contract and first optimization

Normal native linking remains possible without optimization metadata. When LTO
is enabled, known summaries are validated before mutation. Missing summaries
mean conservative native linking; malformed summaries are errors. --no-lto
disables transformations for diagnosis. A report states what changed and why
other candidates were ineligible. LTO is enabled by default for eligible static
links; dynamic/interposable definitions do not receive closed-world treatment.

The first pass is cross-object constant-return call replacement. A compiler
summary proves that an ordinary zero-argument i32 function consists only of
constant copies and one return, with no allocation, memory access, call,
exception path, loop, synchronization or runtime initialization effect. The
summary identifies the symbol, return value and SHA-256 of its native bytes.
The linker validates its unique code definition, bounds and digest, resolves
local/global scope, and replaces an eligible direct E8 rel32 call with B8 imm32.
Both instructions occupy five bytes. It removes that call's relocation; all
other addresses, stack-map positions and line-table offsets remain unchanged.
Argument evaluation and caller stack cleanup are untouched. The original
function definition remains for address-taking and other callers.

This is genuine summary-driven interprocedural optimization, but it is not
general IR import, cross-unit inlining or a complete ThinLTO implementation.
No floating-point, instance, indirect, PLT, dynamic/interposable or nonstandard
ABI call is eligible. Debug/no-inline policy and future stack-observable modes
must be represented in summaries before enabling those modes.

General LTO later adds call graph and effects summaries, function body offsets,
instruction/size costs, constant arguments/results, type-devirtualization
conditions and import budgets. The thin link reads summaries only. Selected
function bodies load individually through .corsac.ir; no whole-project AST is
reconstructed. ABI-visible signatures cannot change without rewriting every
caller and proving there is no unknown external caller.

### Backend request and publication boundary

General IR LTO is a staged extension, not a linker dependency on the frontend.
The build utility supplies a compatible compiler-backend provider to corlink.
The linker first produces an immutable, versioned link plan containing:

- Target/runtime ABI and compiler-IR version fingerprints.
- Input artifact content hashes and symbol-resolution decisions.
- Canonical type/dispatch assignments and generic-specialization owners.
- Retention roots and explicit closed-world assumptions.
- For each output unit, selected imported function identities, artifact hashes,
  IR byte ranges and required declaration/layout fingerprints.
- Per-unit import/code-growth limits and the global resident-memory limit.

The backend consumes the plan through a dedicated compiler mode, not by
reparsing project sources. It loads only selected IR and its declaration
dependencies, optimizes, emits a replacement ET_REL object and reports actual
imports, effects, dependencies and peak working set. One long-lived provider
can process bounded units with internal workers; corlink does not spawn a full
frontend process per source file. The current summary-only pass requires no
backend provider and remains usable by the independent linker alone.

Plans use artifact-relative identities and content hashes, never live process
pointers. A replacement object must match its original unit identity and ABI,
preserve exported signatures, and satisfy the plan's type/generic ownership.
It is published to a temporary content-addressed artifact, validated, then
atomically committed. The final image is published only after every required
replacement succeeds. A missing optional IR body leaves native fallback code;
a malformed present body, stale required fingerprint or failed requested
backend run is an error, not silent success with partially optimized output.

Thin-link choices are sorted by stable identity, with explicit cost limits and
stable tie-breaking. Input path order and worker completion order cannot choose
different generic owners or optimization candidates. Bare-metal entry placement
is an explicit layout constraint and is not overridden by sorting candidates.
Incremental cache keys include the plan, compiler/backend version, optimization
settings and fingerprints of every imported body. An exported declaration or
layout change invalidates consumers; a body-only change invalidates that unit
and its LTO importers, not unrelated declaration users.

The declarations and general IR sections above are architectural contracts,
not a claim of an implemented binary ABI. Their first implementation must freeze
record tags/widths and add reader/writer round-trip and corruption tests before
publishing artifacts. Only the `.corsac.abi` and `.corsac.lto` version-1 wire
encodings in OBJECT-FORMAT.md are currently accepted by the new linker path.

## Bare-metal bootloader and kernel

Bare-metal compilation selects the existing no-OS runtime and x86 ISA explicitly.
The default remains i486 plus x87; no implicit CPU upgrade is allowed. Kernel
and bootloader profiles specify TLS policy, interrupt ABI, runtime services,
physical/virtual addresses, stack requirements and enabled allocation/exception
facilities. They cannot inherit Linux syscalls or a managed loader implicitly.

corc --freestanding --obj emits normal relocatable objects. Handwritten startup
assembly may own _start; compiler-generated managed entry then uses --asm-entry.
The external linker accepts --flat --base for stage-two flat images, and
--base/--paddr for ELF kernels with separate virtual and physical load addresses.
The flat entry must be the first byte; input order and the entry stub placement
are explicit. BSS has memory size but no file payload, and the loader/startup
stub receives the zero-fill range. Alignment and address overflow are checked.

The build graph keeps three distinct artifacts: the existing real-mode stage-one
binary; stage-two objects plus their startup object linked as flat output; and
kernel objects plus startup/vector objects linked as ELF. Neither a flat binary
nor the stage-one sector is an ET_REL input. Image packaging consumes the final
artifacts and does not compile or relink them implicitly. Startup owns the CPU
mode transition, stack setup, BSS clearing and any required paging/GS setup.
The managed entry name must match the assembly call, and startup objects must
appear before managed code when a flat image requires entry at byte zero.

The current ELF `--paddr` contract emits a physical entry address and biased
segment load addresses. A higher-half kernel's entry stub must establish the
mapping before using linked high addresses. Merely assigning `--paddr` does
not make an ordinary compiled function safe to execute with paging disabled.
Layout checks cannot replace testing that actual startup sequence.

Stage-one 16-bit boot sectors remain assembled with the existing x86-16 path;
ELF32 LTO does not silently consume real-mode code. Mixed code-generation modes
require explicit adapters rather than treating every x86 input identically.

MMIO/port I/O, volatile/atomic accesses, interrupt masking, fences, fault handlers,
entry stubs and linker-owned boundaries are barriers or roots as appropriate.
No LTO pass may infer pure behavior from a missing summary. It must not merge
address-significant descriptors, discard vector tables, change an interrupt
calling convention or remove required memory ordering.

## Acceptance and migration

First tests cover ELF metadata round trips, bad versions/lengths/hashes,
duplicate definitions, local symbol shadowing, missing summaries, non-call
relocations, side effects, static initialization, default/off LTO parity,
observable cross-object code changes, flat layout/BSS and physical ELF loading.
Compiler-to-linker integration tests compile distinct sources to distinct
objects and invoke corlink as a separate process. Baseline --ref source mode is
explicitly a transition, not bounded declaration loading.

Managed acceptance additionally requires partial types, generics, inheritance,
interface dispatch, initialization, exceptions, reflection roots, rejected
deferred references in bare-metal builds, dependency invalidation and stable
results across input order and worker counts. Memory measurements cover the
index, active bodies, backend imports and final linker, not just parser memory.

The bootloader/kernel acceptance uses the actual OS source and its configured
image build after compiler/linker tests pass. A synthetic flat image test is
necessary but not evidence that the entire OS has booted.

## Reference semantics

- [Assembly loading scopes](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext)
- [Managed dependency loading](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/overview)
- [ThinLTO summary/import architecture](https://clang.llvm.org/docs/ThinLTO.html)
