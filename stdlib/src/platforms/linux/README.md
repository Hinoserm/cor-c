# Linux standard-library adapters

Linux-specific implementation details behind ordinary `System` APIs, including
native errors, files, processes, consoles, and sockets when those pieces are
split from portable library code. Linux-only names must not escape as a second
public library surface.
