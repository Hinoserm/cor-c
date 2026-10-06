# Managed image metadata

Every compiler object owns a local `__corsac_frames` table and, when enabled,
a local `__corsac_stackmaps` table. Final static, flat and shared links construct
one image-local `__corsac_units` directory covering all contributing units.
Native ABI contract version 3 requires the new image-registration convention;
older contracts are rejected, not translated.

The little-endian directory has a 16-byte header: `CMDR` magic, version 1,
record count, and record stride 16. Each record holds four uint32 fields:
frame-table address, frame-table byte size, stack-map address and stack-map byte
size. An absent table has a zero address and size. Link-time validation rejects
out-of-section table bounds and collisions with reserved directory/alias names.
The directory lives in relocated read-only data, not mutable GC static roots.
Its symbols are image-local for dynamic binding. Linking does not mutate the
input object symbol tables to manufacture aliases.

The entry stub and shared-library initializer pass the directory address to
`Runtime.BeginImage`. Registration consumes one registry slot per image, not
per separately compiled file. Explicit individual frame-table registration is
also supported as a distinct metadata kind. Repeated registration is idempotent.
`Sys.FrameDirectory()` supplies the current image directory; `Sys.FrameTable()`
still supplies the current compilation unit's table for low-level inspection.

The tables' own layouts live in the linker's object model, shared by the code
generator and the link: `FrameTableFormat` (`'CFR5'` per object, `'CFR6'` once
the link has moved its names and shared line programs into the image's
`__corsac_frame_pool`, `'CFP2'`) and `StackMapTable` (`'CSM1'` version 6,
varint call sites with checkpoints for a logarithmic lookup). The runtime's
readers (`Runtime.LookupIn` and `Runtime.LineIn`, `Gc.MapSite`) are the third
copy and change with them; they accept only these magics.

Frame entries are a lead byte (start padding, file-changed flag, line-program
kind, low size bits) and a few varint deltas. A line program is its first code
offset and then one op per line change, coded like DWARF's special opcodes: a
byte for small forward steps of one or two lines, two bytes for nearly every
other step, an escape for the rest; pairs that do not change the line are
dropped. A function's own program takes its first line from the entry as a
delta from the previous one's. A program that several functions carry -- a
generic body compiled into many units, a one-line accessor -- is stored once,
most used first, and referred to by offset: within a table in `'CFR5'`, across
the whole image in the pool after the link. Only the matched entry's program
is decoded, only up to the address, and only when the caller wants a line;
nothing is allocated. Modelled on the compiler's own native image, the line
programs shrink from about 506 KB to about 249 KB and the tables from about
787 KB to about 425 KB, with about 18 KB of shared programs added to the pool.

Stack lookup searches each unit and decodes the line from the matching table's
own programs or its shared ones (the table's for `'CFR5'`, its pool's for
`'CFR6'`). It must never interpret another unit's line offset relative to the
runtime's own table. The directory makes stack maps discoverable
but does not by itself implement a precise collector or reflection metadata.

The compiler passes `Runtime.Capture` the throwing function's frame pointer and
an immutable source-site string. Walking that frame alone would start at its
caller and omit the throw site. The explicit source site preserves the leaf
through inlining without requiring an extra machine frame. Caller return
addresses are looked up one byte before the return location, so a call at a
line/function boundary belongs to the instruction that actually called.

Acceptance includes flat/static/shared linker structure, malformed metadata,
stale ABI rejection, repeated-link input preservation, and executable exception
traces crossing an indexed unit boundary. The latter checks exact source lines
with and without LTO and compares object bytes across worker counts. Its runtime
declaration list is deliberately explicit; eager standard-library declaration
loading remains a separate open task.

The accepted milestone also runs the standard stack-trace language fixture with
the runtime in a shared library. Its ELF checks reject text/copy relocations and
verify loader metadata. An isolated real-OS build with ABI-v3 objects boots on
an ISA 486 and reaches login, shell execution and reboot. These acceptance gates
do not claim native self-hosting or complete runtime type loading.
