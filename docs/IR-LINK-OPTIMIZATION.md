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
Linking and compiling are commands of one executable, so the link step
reaches the compiler backend directly; `--lto-backend` still names another.
The `corc link` convenience command supplies the same backend in-process.

## Container

The archive is a non-loadable ELF note. Integers are little-endian. Its 84-byte
header contains magic `CCIR`, version 3, record count, directory byte count,
total archive size, a 32-byte native-object digest and a 32-byte directory digest.
Native-object hashing uses canonical ELF serialization with the archive omitted.

Each directory record contains a length-prefixed UTF-8 key, a one-byte importable
flag, instruction count, direct-call count and length-prefixed call names,
then a reference count and length-prefixed code/data reference names, followed
by a 64-bit decoded-node accounting cost, body-relative offset, byte length
and a 32-byte SHA-256 payload digest. Backend request protocol version 4 carries
the same cost for each imported body, and the unit's lifetime facts (below).
Earlier versions are rejected.
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

Decoding has a 64 MiB accounting budget. The reader charges registers, blocks,
instructions, operands, reference arrays, strings and data bytes before their
allocation, with object/list overhead allowances. Unretained records are never
decoded. The backend reports the accounted total for each unit. This is distinct
from process RSS: native-object storage, optimizer scratch and runtime overhead
are not certified by the decoder's accounting.

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
once. The backend retains unit data and lightweight function headers, then loads
functions in worker batches bounded by a working allowance sized from the memory
the machine has available (`MachineMemory.WorkBudget`: a quarter of it, between
8 and 512 MiB, less the unit's own headers; `CORC_WORK_BUDGET` forces a size).
Each function loads only its selected direct-call imports, performs bounded
inlining and constant/copy/dead-code/branch cleanup, and releases analysis-only
imports. Batch costs reserve three times decoded-node costs plus 512 KiB per
function for transformation/code-generation work; this is an accounting policy,
not a measured RSS ceiling. A function exceeding the whole allowance is compiled
alone in a batch of its own: slower, never refused. The allowance decides how
many functions are in memory at once and nothing else, so a link on a small
machine produces the same bytes as on a large one (tests/integration/lifetimes.sh
links once normally and once with `CORC_WORK_BUDGET=1` and compares). Emission remains ordered and releases decoded function bodies
after their native bytes and compact metadata are emitted. Remaining calls bind to the
original providers; imported copies are never accidentally emitted as new owners.
Exports remain intact. Stack maps, frame tables and line tables are regenerated
from the new code rather than patched with guessed offsets.

The final link discards IR and pre-link optimization certificates. Logs report
imported body counts/bytes and regenerated unit counts. Those counts are not
performance claims; integration checks additionally inspect machine code and
run the resulting executable.

## Closed bare-metal retention

Flat and physical-load-address links are closed images. Their entry symbol and
every relocation in a non-IR native object are roots. Reachability follows both
calls and code/data addresses, including vtables and native interrupt vectors.
Private references resolve inside their original object; globals resolve to the
same certified owner used by native linking. Unreachable function/data records
are omitted before decoding and regenerating an affected unit.

This keeps ordinary objects open to external callers while allowing the final
boot image to discard unused exports. Debug/frame tables are regenerated from
retained functions and do not artificially root every otherwise dead function.
Native objects without IR remain intact. Hosted links currently preserve their
exports; broader reflection/export-root policy is separate work.

## Lifetime hints

A unit compiled on its own cannot tell what another unit's function does with
an object it is handed, or whether what it returns is a fresh object. The escape
pass therefore leaves `.corsac.life` beside `.corsac.ir` (written first: the
archive's native digest covers it). For every function, each parameter's summary
and the function's fresh return are stated as a condition on other units:
never, always, or the set of `(function, argument)` pairs that must keep nothing
and functions that must return fresh objects. A condition is bounded to 32
requirements; past that it is "never", a fixed bound that is the same on every
machine. The unit also lists the conditions under which an object it left to the
collector only because of such a call could have been placed in its frame or
freed, or a reassigned variable fed by another unit's function could have
given back its previous value (at most 4096 per unit). Calls to the backend's
intrinsics (`__x86.*`, `__exception`) and to field sites are never conditions. The unit's own decisions use the same analysis
read pessimistically, so hints change nothing a unit compiles to by itself.

Only an object that will carry hints leaves them, or anything that depends on
the link: the driver decides before optimising (`Module.LeavesLinkHints`) that
the output is an object or library unit, position-dependent, compiled with the
link-time optimizer on and against no shared library -- the objects whose link
runs this step. A shared library's position-independent objects, and a program
compiled and linked in one step, get neither hints nor field sites.

Magic `CLIF`, version 2, total length; a sorted name table; the runtime frees the
unit may call; function records (name index, global flag, parameter conditions,
fresh condition); pending conditions. A condition is a stays count (-1 for never)
with name/argument pairs, then a fresh count with names.

The link reads every unit's hints and solves them as one system of monotone
equations, to the least fixed point, the way the compiler solves a call cycle
inside a unit: parameters start out keeping nothing and turn to escaping only
when something they depend on does; functions start out not fresh. Both halves
are worklists over a reverse-dependency index, linear in the hints' size, in a
fixed order. A function nobody summarised keeps everything. A unit is
regenerated when one of its pending conditions now holds, and every regenerated
unit gets its facts: the solved answers for its own functions and every function
it or an imported body calls, and the frees it may call (which also join its
import candidates). In the backend each function is inlined with its imports but
not the allocators or the functions the whole program found fresh (inlined,
their results would be branches the rules cannot recognise), cleaned up, given the lifetime rules again with those facts
(`Escape.RunAtLink`), then inlined again so the allocators and the new frees fold
in as they do in a unit compile. The compile keeps a pending allocation a call to
the allocator (`Module.KeepCalls`) so the link can still recognise it.

Fields go the same way (the owned field rules, `Opt/EscapeFields.cs`). For each
parameter that may stay and each function that may return a fresh object, the
unit states what the function does to that object's reference fields: word
offsets dirty or freshly filled whatever other units do; offsets that are fresh
only if a condition holds (a child stored from another unit's function) or stay
clean only if one holds (a loaded child handed to one); and the other units'
functions the object is handed to at its base, argument -1 meaning the object a
fresh function returns, whose summaries merge in. Bounded to 64 entries, past
which the object is opaque. The link unions those to a least fixed point as it
does escapes, with the conditions answered from the solved escapes and
freshness, and hands the solved summaries to the backend with the other facts.

An object the unit already owns is stored in its slot in the archived IR, so
the link's second look cannot take it again to add field frees. For those the
unit frees each field it cannot yet call clean through a symbol of its own
(`__corsac_field$<unit>$<n>`) and lists the site with the object's field
summary. The link defines every such symbol, with LTO on or off, as an alias of
`Runtime.FreeField` where the solved summary leaves the field clean and of
`Runtime.KeepField`, which does nothing, everywhere else; no code is
regenerated for them. Version 2 of the section adds the field summaries after
each function's conditions and the field sites after the pending conditions;
conditions gain a third requirement list, argument field summaries that must not
be opaque, used by pending triggers.

The log reports `LTO lifetimes: units with hints=N, units gaining=M, field
sites=S freed=F` and the backend `lifetimes placed or freed=K`.

## Remaining limits

This first IR path is not whole-program LTO: private dependency closures,
cross-unit devirtualization, summary-driven effect propagation, native-section
dead stripping, fully streamed object I/O and project-wide concurrent backend
scheduling remain separate tasks. Function batches within one backend are now
accounting-budgeted. Dynamic/interposable links do not use this
closed static-link import policy. The current backend unit budget is a checked
conservative estimate, not measured live heap usage or 486 acceptance.
