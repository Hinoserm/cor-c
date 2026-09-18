# Declaration index

The compiler-owned declaration index is separate from ELF native objects. It is
an immutable project-generation artifact used before body compilation. The first
implementation supplies storage, lookup and source declaration generation.
Demand-loaded binder integration is a subsequent stage, not implied by a
successful storage or source-index test.

## Storage version 1

All integers are little-endian. The 32-byte header contains uint32 magic CDIX,
uint32 version 1, int64 record count, int64 offset-directory position and int64
total file length. The directory consists of one int64 offset per record and
ends exactly at EOF. Readers binary-search directory entries on disk rather
than reading the directory into an in-memory collection.

Each record contains int32 UTF-8 key length, int32 payload length, a 32-byte
SHA-256 digest of key bytes followed by payload bytes, then the key and payload.
Keys use strict UTF-8 without NUL, up to 4096 bytes. Payloads are at most 1 MiB.
Record boundaries must exactly match adjacent offsets. Unknown versions, bad
lengths and invalid directory ranges fail; payload reads verify their digest.

Records sort by ordinal key, then payload bytes. Equal keys retain every
fragment: partial declarations must be merged semantically, not silently dropped
by the storage layer. Exact and prefix enumeration yield records individually.
The reader serializes seek operations, permitting multiple body workers to use
one open index without sharing mutable stream position unsafely.

## Memory and publication

The writer sorts bounded chunks, spilling runs to a uniquely owned temporary
directory beside the destination. Four-way, leveled merges keep live merge
heads bounded and avoid repeatedly merging the entire accumulated index. The
offset directory is streamed through a temporary file. The sort chunk budget
defaults to 1 MiB; a single record exceeding it fails explicitly. Merge-head,
serialization and stream buffers are additional bounded memory, not included
in that chunk budget. Input producers must also bound their own working sets.

Only a complete flushed index replaces the previous generation. A failure leaves
the previous index intact and removes only the current invocation's temporary
files. A caller must retain a generation while workers use it; publication does
not make old file contents mutable or permit mixing declaration generations.

The metadata unit suite exercises multi-level spills, lookup, concurrent readers,
partial fragments, byte-identical output across input order/chunk budgets,
malformed headers/payloads, empty indexes and failed-publication preservation.

## Source declaration records

`corc index --assembly <identity> <sources...> -o declarations.idx` builds an
index, including response-file sources and explicit -D symbols. It processes
one file at a time. The declaration parser skips balanced method/accessor blocks
and expression bodies without constructing implementation syntax trees. Body
syntax/semantics are still checked when the implementation unit is compiled;
declaration indexing is not a substitute for compilation diagnostics.

The source lexer resolves conditional directives, including file-local defines.
Stored declaration spellings contain only active tokens, with original literal
spellings preserved and implementation bodies replaced by declaration markers.
They are generated metadata, never handwritten replacement headers or executable
method implementations. Every fragment retains its own namespace, using imports
and aliases; partial fragments must not borrow another file's scope.

Type keys use normalized assembly identity and namespace/nested name chains,
with generic arity at each nesting level. Equal partial keys are retained. Source
location records use UTF-16 character offsets, matching the parser's string
indices. Source fingerprints cover complete file text; declaration fingerprints
cover declaration tokens and lexical scope but exclude omitted bodies and source
positions. A body-only change therefore invalidates implementation users without
changing an otherwise identical declaration fingerprint.

The CDEC payload starts with uint32 magic CDEC and version 1. Four int32-length
strict UTF-8 strings follow: source path, declaration spelling, namespace and
outer type path. Next are four int32 fields (source start, end, line, column),
32-byte source and declaration SHA-256 fingerprints, an int32 import count with
pairs of scope/namespace strings, and an int32 alias count with triples of
scope/alias/target strings. Lengths and source spans are validated; trailing data
is rejected. The payload key is supplied by its containing index record.

Implementation fetching verifies the source fingerprint before returning the
recorded type fragment. A stale source is an error, not permission to combine a
new body with old declarations. Generation publication verifies source hashes
again after indexing, one file at a time, before replacing the old index.
Source parsing still tokenizes one complete file; this milestone does not claim
streaming arbitrarily large files or complete managed separate compilation.
