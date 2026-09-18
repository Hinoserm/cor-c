# Declaration index

The compiler-owned declaration index is separate from ELF native objects. It is
an immutable project-generation artifact used before body compilation. The first
implementation supplies storage and lookup; source records and binder integration
are subsequent stages, not implied by a successful storage test.

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
