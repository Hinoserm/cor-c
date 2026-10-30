# The link's memory, toward a 256 MB self-host

The compiler is meant to build itself on a 256 MB machine: a P55C-class
board, the real development environment. Compiling units one at a time
nearly fits already (the largest hold about 200-230 MB under a 256 MB
cgroup). The link does not, by a factor of five to ten, and it is now what
fails the 32-bit stage 1>2>3 build: the native link of the compiler peaks
at 3.5-3.9 GB against a 32-bit process's heap ceiling of about 3.7 GB, and
ran out of memory in stage 3 on 2026-10-29.

This note is what the link holds, measured, and the plan to make it hold
little enough.

## Measured (the compiler's own link, 2026-10-29)

Live heap after each phase, the .NET-hosted linker, after 0b3a85a5 and
8e26b8ed (one archive directory a unit, preinit lets go, lifetime sets in
one array, body hashes inline):

| phase                          | live (MB) | before 0b3a85a5 |
|--------------------------------|----------:|----------------:|
| read objects                   |       413 |             413 |
| archives and hints             |      1184 |            1464 |
| reachability                   |      1247 |            1527 |
| virtual targets, lifetime solve |     1386 |            1680 |
| catches and owned fields       |      1662 |            1956 |
| regions (start, full GC)       |      2026 |               - |
| units regenerated              |      2100 |            2394 |
| heap decision                  |      2103 |            2398 |
| layout and write               |       748 |             992 |

Under a 2.5 GB hard limit the .NET link runs out of memory as the regions
begin (VirtualTargets.HoldersOf, an ordinary allocation that tips it over),
with or without a cap on the region graphs' edges: the region engine's
graphs are not the problem. The base is.

What is live after archives and hints (dotnet-dump, `dumpheap -stat -live`,
before 0b3a85a5; 1.65 GB in all):

| what                                            | MB  |
|-------------------------------------------------|----:|
| byte[] (section contents, then archive hashes)  | 343 |
| strings (symbol and record names)               | 222 |
| relocations                                     | 148 |
| archive directories (two copies then)           | 190 |
| string[] (archive call and reference lists)     | 123 |
| SortedSet<string> + nodes (lifetime, hints)     | 150 |
| SortedSet<(string, int)> + nodes                |  67 |
| Symbol objects and their dictionaries           |  95 |

After 4dc36797 and 14eda0f5 (code and data left in their files until laid
out, step 1 below), the same relink, byte-identical:

| phase                          | live (MB) | before |
|--------------------------------|----------:|-------:|
| read objects                   |       238 |    413 |
| archives and hints             |      1009 |   1184 |
| catches and owned fields       |      1488 |   1663 |
| units regenerated              |      1927 |   2102 |
| heap decision                  |      1930 |   2105 |
| layout and write               |       573 |    748 |

Then (2026-10-30, the same relink, byte-identical each step): reachability's
retained sets hold the archive's own keys (cf10cf65), relocations are 16
bytes (a630c3b2, e4a7b37a), a regenerated unit's code and data stay in its
file (185360e7), the heap records are let go after the catches and read
again for the heap decision (a44b7544), the owned-field name sets are small
(d593999f):

| phase                          | live (MB) | 10-29 start |
|--------------------------------|----------:|------------:|
| read objects                   |       203 |         413 |
| archives and hints             |       937 |        1184 |
| catches and owned fields       |      1090 |        1662 |
| units regenerated              |      1160 |        2100 |
| heap decision                  |      1163 |        2103 |
| layout and write               |       510 |         748 |

The regions' peak RSS is 4.18 GB (4.85 at the start); the 32-bit native
link of stage 1>2>3 peaks at 3.3-3.4 GB per process.

The regions phase is now the link's peak (RSS 3.6-4.2 GB against 1.1-1.6
GB live), and on the compiler's own link it is spent for nothing: from
`--region-report "+memsteps"` on 10-30, the copies and sites (1 s, 1.50 GB
live), every call's targets (7.5 s, 1.60 GB), the reachable types (25 s),
the receiver types -- which give up past their budget of 20 million carried
after 89 s, every target kept -- and the escape graphs, which grow the heap
to 2.9 GB in some 40 s before the whole engine gives up past the link's
work budget (24 million locations carried) and answers nothing. Since
eac52673 the engine stops where the receiver types give up -- no other
program in any build log has outgrown their budget -- and the escape
graphs' 40 s and 1.3 GB are gone; the receiver types' own 64 s remain. A region
engine that fits this program, or knows early and cheaply that it will
not, is the largest saving left in the link: about 2.5 GB of peak and two
minutes. Lowering the budgets alone would give the same answer here sooner
but may take regions from smaller closed programs that get them now; a
budget by memory would make an image depend on the machine that linked it.

## The plan

The link holds the whole program -- every section's bytes, every symbol by
name, every relocation, every unit's analysis summaries -- from the first
phase to the last. A link that fits 256 MB holds what the phase at hand
needs and reads the rest from the objects as it goes. In order of size:

1. **Section contents stay in their files** until layout and write, as the
   IR archives and notes do (Section.FileBacked, ElfReader.LeftInFile).
   DONE for the compiler's own link (175 MB): Section.Bytes brings a section
   in on first use, so a reader that needs all of it still works, and the
   readers before layout read where it is (Section.Read, ReadAt, Length).
   Any new early reader of Bytes undoes it for its sections: look with a
   stack trace in Section.Load, as was done to find these.
2. **Names once, ids everywhere.** A symbol's name is held once, in one
   pool, and relocations, symbols, archive call lists and the analyses'
   tables refer to it by an integer. The strings (222 MB), string arrays
   (123 MB) and symbol dictionaries (95 MB) shrink to the names once and
   arrays of ints.
3. **Relocations as compact records**, read a section at a time where they
   are applied, not held as objects naming their symbols by string (148 MB).
4. **The whole-program analyses keep summaries by id**: lifetimes, owned
   fields, virtual targets, regions and the heap decision, each a table of
   ints a function, released when its phase is done. The region engine's
   setup alone adds 360 MB before any graph is solved.
5. **Units are made again one at a time** in a fixed budget, each written
   and let go before the next (the backends hold 4 at once today).

Each step is measured the same way: the same relink of the compiler
(`--link-only --timings` on a stage's objects), its live heap per phase,
and its output byte-identical to the stage's binary. Then the native
32-bit link's peak, and at the end the link under a 256 MB cgroup.

## Tools

- A stage's objects to relink: any `sc3m` run's `sourceN/compiler/obj`.
- `--timings`: live heap and RSS after each link phase.
- `--region-report "+memsteps,+memtop=N"`: the region engine's heap after
  each step, and the N component solves that grew it most.
- `dotnet-dump collect --type Heap` and `dotnet-dump analyze -c "dumpheap
  -stat -live"`: what is live, by type, in the .NET-hosted linker.
- The .NET-hosted compiler's 768 MB GCHeapHardLimit (corc.csproj) is a
  unit-compile cap. The link has never fitted it; that is this work, not a
  limit to raise.
