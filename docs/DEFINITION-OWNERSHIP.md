# Shared definition ownership

Separate compilation can instantiate the same closed generic type or method in
several units. Its linked address and static storage must still have one identity.
Ordinary source definitions remain strong: duplicate declarations are errors,
not an invitation to select an arbitrary implementation.

## Certification

The compiler explicitly marks generic specializations and structural sequence
descriptors as eligible. Before optimization, it computes a SHA-256 structural
identity over their IR or data. This includes parameter and register types,
control flow, instructions, field widths, relocation targets, and contents of
referenced private constants. Source locations and private symbol serial numbers
are excluded. Global references retain their names. Graph traversal is bounded.

After code generation, `.corsac.coalesce` records both that semantic identity
and a separate integrity hash of the emitted definition and referenced local
definitions. Optimization can therefore choose different machine instructions
in different units without requiring byte-identical implementations.

This is a compiler contract, not a proof that arbitrary machine code has the
claimed meaning. As with optimization summaries, the linker trusts the producer's
semantic claim but verifies its association with intact object contents.

## Object encoding

All integers are little-endian. The non-loadable note contains `COAL`, a 32-bit
version (1), a 32-bit record count, followed by records sorted by ordinal name.
Each record contains a 32-bit UTF-8 name length, the name, a 32-byte semantic hash,
and a 32-byte native integrity hash. Names are 1–4096 bytes without NUL. Unknown
versions, duplicate records, invalid encoding, truncated records and trailing
bytes are rejected.

## Final link

Every duplicate must have a matching semantic certificate. The input with the
ordinally first input name owns the global definition. Repeated input identities
are rejected. All duplicate and alias checks complete before mutation. LTO also
validates its summaries before applying ownership changes.

The initial implementation retains non-owner bytes under local aliases. Stack
maps and frame records continue describing those retained bytes; ordinary calls,
type identities and statics resolve to the chosen global. Removing redundant
bytes is separate dead-code/layout work, not claimed by this milestone.
Certificates are consumed by the final link because they describe pre-link bytes.
Coalescing remains required when optional LTO transformations are disabled.

## Remaining scope

Indexed partial declarations are merged in canonical source-path order for
layout, independently of which source file owns the bodies being compiled.
The first fragment owns the descriptor and static storage. Member ownership is
explicit: methods and property accessors remain in their original source unit.
Synthesized default constructors and static-initialization wrappers belong to
the type owner. An explicit static constructor becomes a hidden ordinary helper
in its original unit; the initialization wrapper calls it after field initializers.
This preserves one initialization protocol without importing that method body.

Async specialization identities, closure ownership, full partial-type acceptance, fully
lazy generic method loading, and general IR import/regeneration remain work in
progress. Uncertified duplicate helpers are rejected rather than silently merged.
No whole-project memory bound or complete generic/runtime acceptance is claimed.
