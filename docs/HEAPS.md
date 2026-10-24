# Heaps: one collector, a heap per module

CORSAC's GUI and NT kernels are moving from ring 1 into ring 0 as kernel
modules (the OS repository's docs/software/RING0-MODULES.md, section 4.1).
Each module gets a heap of its own, and the requirement is: "extremely
performant and never wasteful". This is the runtime's half of that design:
what changes in `runtime/src/core/gc.cor`, the API the kernel's module
loader uses, and the order of the work.

Nothing here changes a program on Linux or Windows. Such a program has one
heap, the one it has today.

## The rules

1. **ONE COLLECTOR.** Objects are passed by reference between the modules
   and the kernel, which is the point of the move. One SATB mark therefore
   covers every heap, with the same two brief stops as today, and a pointer
   into any heap is found through the one chunk index.
2. **CHOOSING A HEAP COSTS NOTHING PER ALLOCATION.** `Runtime.AllocFast` is
   put in place at every `new`: it reads the thread's class cache through
   its thread block (`[tb + Tls.Classes]`), pops a free block and returns.
   Whatever picks the heap must be behind that pointer, not in front of it.
3. **NEVER WASTEFUL.** No heap reserves memory it does not use, and memory
   a heap no longer needs goes back to the kernel at once.
4. **SHARING IS SAFE.** An object made on the "wrong" heap is not an error,
   because one collector marks them all. Only the per-heap figures blur.

## The design

### What is a heap's own, and what is shared

A heap is a record in a mapped page, not a managed object: a heap cannot
be allocated on a heap. Each heap has its own:

- its chunk list (today `HeapChunks._chunks`), the chunk the bump pointer
  is in (`_current`, `_bump`, `_limit`), and its share of `_heapBytes`;
- the free-list bins of big blocks (`_bins`, `_smallFree`);
- the size-class pages with room (`_classPages`) and the empty-page pool
  (`_pool`, `_poolBytes`);
- its pacing (`_since`, `_threshold`) and figures (`_liveBytes`,
  `_liveBlocks`, `_allocated`).

Shared by every heap:

- the class sizes and the size-to-class table (constants once mapped);
- the mark and everything about a cycle (`_cycle`, `_snapshots`,
  `_concurrent`, the generations, the cards);
- the roots, and the chunk index that maps an address to its chunk, which
  the mark needs for a pointer into any heap;
- the lock the collector's phases take.

A chunk names its heap in its header (`HeapChunks.HeapOf`, the word at
+14W; the header grew to sixteen words so the first block stays
eight-aligned). A block freed or swept goes to its own chunk's heap's bins
and lists, whichever heap the code doing it works for (`Gc.Link`,
`ListPage`, `RetirePage`, `SweepNext`), so a free from another module's
thread can never put one heap's memory in another's bins.

A heap's record is one page of the operating system's: its bump chunk,
pool and figures, then its class pages' list heads, then its bins. The
allocator works for one heap at a time under the heap lock (`Gc._h`), set
from the locking thread's heap as the lock is taken (`GcLock.Enter`). A
program that makes no heap has heap 0 alone, and a free pays one compare
for the rest (`_heapCount`).

### The current heap

The heap allocations go to is the **current heap**, a word per thread.
`Tls.Classes` holds the running thread's class cache for that heap. The
inlined fast path is unchanged: it reads the cache it always read, and
which heap that cache belongs to is decided where the cache pointer is set.

- `Runtime.EnterHeap(heap)` makes `heap` current and returns the heap that
  was. `Runtime.LeaveHeap(previous)` makes `previous` current again. The
  same heap again costs a compare. The current heap is the thread block's
  word `Tls.Heap` (0 for heap 0).
- **Switching, as built:** the class cache is parked per heap. Each thread
  block has a shelf (`Tls.Shelf`, a page: one word per heap number) holding
  the class cache it had in each heap it left. A switch takes the cache in
  use out of the block in one atomic swap, shelves it in the old heap's
  place and puts the new heap's in, with no lock, flagged inside the
  allocator (`Tls.InAlloc`) so that an interrupt neither answers a round nor
  frees on top of it. A collection round takes a thread's cache by
  compare-and-swap after writing it aside, so the one that swaps the word out
  has it; a switch that finds the cache taken by a round, or a thread's
  first switch (which maps the shelf), goes the long way under the heap lock.
  Shelved caches stay the thread's: their pages stay owned, and are stamped,
  captured and unmarked with the cache in use, and given back when the thread
  ends or the heap is released. A hundred thousand enter-and-leave pairs
  take the slow path under a thousand times (test 2410), where giving the
  cache back at every switch took it twice a pair.
- **The thread's bump buffer is not switched.** It serves allocations above
  the size classes and stays with the thread whatever heap it is in, so up to
  a buffer of them land in the heap it was taken from. That is safe (rule 4);
  `Gc.ReleaseHeap` keeps any chunk a thread's buffer is in, as the first
  heap's.
- **A parked call that resumes on another processor** (a kernel whose thread
  block is the processor's): save the call's `Tls.Heap` word with it, and on
  the processor it resumes on call `Runtime.EnterHeap(saved)`, which is the
  switch above. Copy none of the allocator's words (`AllocPtr`, `AllocBase`,
  `AllocLimit`, `Classes`, `Shelf`): they are the block's, and its pages are
  owned by the block.
- The kernel calls them at a module's gates, around its trap entries (its
  `ModuleGates` registry), its registered kernel-to-module interfaces, and
  the nt/gui dispatchers. A thread a module lends runs in that module's
  heap for life.
- **The current heap is the thread's, not the processor's.** Module code
  parks on a retained kernel stack and resumes on any processor, and
  `KeUserModeCallback` keeps frames across a trip to user mode. So the
  thread switch saves the word in the thread's record and restores it,
  through a hook the runtime provides.
- The slow path (`AllocMissed`, `AllocSlow`, large blocks) reads the
  current heap from the thread. Library code that allocates on a module's
  behalf (`String.Concat`, collections) lands in the module's heap with no
  change to that code.

### Never wasteful

- A heap starts with one chunk, mapped at its first allocation, and grows
  a chunk at a time from the kernel's page allocator (`Platform.Map`).
- A chunk a sweep leaves empty goes back at once, as it does today.
- Size classes are per heap, so one module's fragmentation never holds
  another's pages.
- A thread's class cache for a heap owns at most one page per class it
  has used. Caches are already taken back at a collection's handshake, so
  a heap nobody is allocating in keeps no cache pages.
- A large object (64 KB and above, to start) is a page run of its own,
  freed whole.
- `Gc.ReleaseHeap` gives back every chunk, page and large run of the heap,
  after the kernel's `Gc.CountHeld` and `Gc.Revoke` have dealt with the
  references into it that remain.

### The sweep

The mark is global. The sweep is per heap: each heap sweeps its own chunks
into its own bins and pages and keeps its own figures. A collection may
sweep one heap alone when only that heap is short.

## The API

```
long Gc.KernelHeap                         // heap 0: today's heap
long Gc.NewHeap()                          // 0 when there is no memory; its first chunk is mapped lazily
bool Gc.ReleaseHeap(long heap)             // false while a thread is in it; see below
void Gc.HeapFigures(long heap, out long live, out long held, out long free, out long chunks)
long Runtime.EnterHeap(long heap)          // returns the previous heap
void Runtime.LeaveHeap(long previous)
```

`Gc.ReleaseHeap` collects, then gives back every chunk and page of the
heap with nothing live in it. What still holds something live -- an object
of the module's that the kernel still refers to -- becomes heap 0's, so
nothing in use is ever unmapped. It answers false, and does nothing, while
a thread's `Tls.Heap` still names the heap: a kernel whose thread block is
a processor's must see that no thread it has switched out is still in it.
`live` in `HeapFigures` is what the last sweep found live.

Module load: `NewHeap`, kept in the module's record. Gates:
`EnterHeap`/`LeaveHeap`. Unload: `CountHeld`/`Revoke` as today, then
`ReleaseHeap`.

## The stages

Stages 1 to 3 have landed: the record, many heaps, and switching with a
class cache parked per heap (tests 2408, 2409 and 2410, with and without
`Corsac.GC.HeapCheck`). Open: stage 4; stage 5 is the kernel's.

1. **The heap record.** The heap's own state moves out of statics into a
   record; the program's or kernel's heap is instance 0. One heap
   everywhere and no change of behaviour. Gate: the full language suite, a
   collector stress run, the kernel-shape compile.
2. **Many heaps.** A list of heaps, the mark over all of them, a sweep per
   heap, figures per heap, `NewHeap`/`ReleaseHeap`. Tested from a hosted
   program that makes a second heap and allocates in it.
3. **Switching.** `EnterHeap`/`LeaveHeap`, a class cache per heap per
   thread, the thread-switch hook. Tested hosted, with threads that switch
   heaps.
4. **Large objects and short heaps.** Large objects as page runs of their
   own, and a collection that sweeps the one heap that is short.
5. **The kernel's side**, done in the OS repository: a heap per module from
   the loader, the gates switching, unload releasing.
