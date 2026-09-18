# Linker I/O

Platform-independent views and streaming adapters for object-file data.
`ByteListReadStream` reads an existing section without cloning its payload;
LTO uses it to inspect summaries while leaving unrequested function bodies alone.
The native object sections themselves are still resident in memory.
