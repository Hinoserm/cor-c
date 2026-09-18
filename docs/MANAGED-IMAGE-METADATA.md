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

Stack lookup searches each unit and carries an absolute line-program address
from the matching table. It must never interpret another unit's line offset
relative to the runtime's own table. The directory makes stack maps discoverable
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
