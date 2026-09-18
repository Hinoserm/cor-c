# Indexed IR and selective link-time optimization

## Compiler and linker boundary

An optimized, non-PIC compilation to an object now carries `.corsac.ir` alongside
ordinary native code. Native code remains usable without IR optimization.
The linker reads costs and direct-call summaries, selects external bodies under
a per-unit budget, and asks the compiler's IR-only backend to regenerate that
unit. It does not parse C#, bind declarations, or reference the compiler project.

The `corc backend` worker uses a bounded binary request protocol on standard
input/output. One persistent process handles selected units sequentially; code
generation and function cleanup inside that worker use the machine's available
logical processors (up to the current compiler worker limit). An installed
`corlink` finds `corc` beside itself, from `CORC`, or through `--lto-backend`.
The `corc link` convenience command supplies the same backend in-process.

## Container

The archive is a non-loadable ELF note. Integers are little-endian. Its 84-byte
header contains magic `CCIR`, version 1, record count, directory byte count,
total archive size, a 32-byte native-object digest and a 32-byte directory digest.
Native-object hashing uses canonical ELF serialization with the archive omitted.

Each directory record contains a length-prefixed UTF-8 key, a one-byte importable
flag, instruction count, direct-call count and length-prefixed call names, followed
by body-relative offset, byte length and a 32-byte SHA-256 payload digest.
Names have a 16 KiB ceiling. Records cover consecutive, nonoverlapping payload
ranges exactly. Invalid versions, flags, lengths, hashes, duplicate keys and
unclaimed bytes are errors. Payload integrity is checked when that body is read.

`M:unit` stores unit settings. `F:<symbol>` names a complete post-async function
body. `D:<symbol>` names a data item, including its relocations. Function order
is retained, including a flat image's first entry function. This initial native
container reader still materializes the ELF and archive bytes; it is not a
claim of a fully streaming native linker.

## Compiler payloads

Version-1 function payloads retain return/parameter/register types, export and
specialization provenance, source diagnostics, frame slots, basic blocks,
landing-pad markers and every instruction field. References use checked table
indices. The reader runs the structural IR verifier before optimization.
Data records retain flags, alignment, bytes and relocations; zero-filled data
stores only its size. Allocation limits are checked before expanding zero data.

The unit record retains entry identity, heap policy, export policy, stack-map
configuration and native imports. Target and managed-layout contracts remain in
their existing native notes and are preserved when code is regenerated.

## Import policy and transformations

The initial planner imports at most 32 bodies and 1 MiB of payload per consumer.
`--lto-import-bytes` changes the byte budget; zero disables IR imports while
retaining native summary optimizations. `--no-lto` disables both kinds of
optimization. Definition coalescing remains a required correctness operation.

Eligible bodies are exported, at most 160 IR instructions, safe for the existing
inliner, and independent of private symbols in their original object. Exception
regions and frame-sensitive operations remain uninlined. Calls to other globals
retain their normal binding. Functions needing private constants/closure code
are conservatively kept as native calls until dependency-closure imports exist.

Selected payloads are loaded one consumer at a time, not all project bodies at
once. The backend decodes one unit under an explicit allocation budget, imports
selected functions, performs bounded inlining and constant/copy/dead-code/branch
cleanup, then removes the analysis-only imports. Remaining calls bind to the
original providers; imported copies are never accidentally emitted as new owners.
Exports remain intact. Stack maps, frame tables and line tables are regenerated
from the new code rather than patched with guessed offsets.

The final link discards IR and pre-link optimization certificates. Logs report
imported body counts/bytes and regenerated unit counts. Those counts are not
performance claims; integration checks additionally inspect machine code and
run the resulting executable.

## Remaining limits

This first IR path is not whole-program LTO: private dependency closures,
cross-unit devirtualization, summary-driven effect propagation, native-section
dead stripping, fully streamed object I/O and memory-aware concurrent backend
scheduling remain separate tasks. Dynamic/interposable links do not use this
closed static-link import policy. The current backend unit budget is a checked
conservative estimate, not measured live heap usage or 486 acceptance.
