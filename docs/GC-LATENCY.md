# The collector's latency: no long holds, background collection, no whole-heap passes

The plan, its parts, and where each stands. The runtime's side is in
`runtime/src/core/gc.cor`; the kernel's side in CORSAC86. HEAPS.md is the
heap's layout; this file is about how long anything holds the processor.

## Why

On a K6-III+ at 550 MHz with one processor and 512 MB, a game's sound broke
up. The kernel's latency tracer put every stall of 110-280 ms in the
collector, with the preemption count raised by the heap lock (GcLock) and no
kernel lock in the chain:

- `StampPage <- StampPages <- StampBuffer <- CollectConcurrently <- CollectLocked <- AllocSlow`
- `TakeCards <- CollectConcurrently <- CollectLocked <- AllocSlow`

Both are step 1 of `CollectConcurrently`: open stamping, stack copies,
CaptureBuffers, BuildBlockAnchors over the whole heap, TakeCards, and the
collector's own StampBuffer, all run whole under the heap lock. The heap
was 40-50 MB live of 140-160 MB taken. One collection held the lock for up
to 744 ms.

The requirement: there is never one heap lock held across real work.
Collection must be parallel, threaded and preemptible, and sound must never
drop.

## Principles

1. The measure is the longest stretch nothing can preempt, not the number of
   locks. A global word held for microseconds is fine. Ten fine locks around
   a whole-heap pass are not.
2. Fast paths stay per thread, with no atomics where none are needed. A
   `lock cmpxchg` is 20-30 cycles on a P5 or K6. The thread buffers and class
   caches already are.
3. Collection work is done by threads whose priority is below everything
   interactive, not by whichever thread happened to allocate.
4. No pass whose cost grows with the heap runs as one step.

## Part 1 (runtime): no stretch over about 1 ms

The stop runs in steps of `Gc.StopBudget` blocks: the walk, then the cards,
then the collector's own pages. It lets go of the heap lock and calls
YieldHook(0) between steps.

- **While a stop is open, a slow path refills no buffer and no page.** Each
  request is answered with one block. A block of a chunk the walk has not
  reached has that chunk walked first (EnsureWalked). The walk is per chunk,
  only of chunks not yet walked, and counted (`Gc.StopWalksOnDemand`,
  `Gc.LongestWalkOnDemand`).
- **Every block handed out mid-stop is listed and allocated black.** Once
  every thread has answered STAMP, the list is marked, and what each block
  holds is read as roots (ScanStopBlocks). Its cards are set again then. The
  block is old once the cycle ends, and what it holds that the marker could
  not mark (a newcomer) stays young. The stop's own card taking may already
  have cleared the card the block's fill set, while the block was young.
- A blocked thread's pages are stamped in steps (GcThreads.Work).
- Counters: `Gc.StopSteps`, `Gc.StopBlocks`, `Gc.LongestStopStep` (µs).
- To come: `HeapChunks.MixedChunkMost`, a platform setting that bounds how
  large a mixed chunk is made, and with it the longest walk on demand.
- Still to do: step the sweep and CollectAlone where the kernel's tracer
  shows they run long, and the rest of the work under the lock (MarkWalks,
  PeekCards, TakeStaticCards, the collector's own buffer).

## Part 2 (shared): collect in the background

Runtime:

- A pacer decides when a cycle is due, from allocation since the last cycle,
  the heap's growth and free memory, as today's threshold does.
- An entry for the platform's collector threads (`Gc.CollectStep(budget)`,
  name to be fixed). It starts the due cycle, or does one bounded step of the
  current one (a stop budget, a marking budget, a sweep chunk), and returns.
  It is safe to call from several threads at once: steps are claimed, and
  marking takes from the shared grey queue as it does now.
- An allocator helps only when the heap is about to run out, past the
  threshold by a margin. Otherwise it takes memory past the threshold and
  lets the collector threads catch up. An allocator with its preempt count
  raised never collects (YieldHook(1) answers 1).

Kernel:

- Collector threads, one per scheduling processor minus one, and one on a
  uniprocessor that runs only when nothing else wants the processor. They
  are registered kernel threads that answer rounds at their safepoints, with
  a priority below every interactive thread and below the sound mixer.
- Woken by the pacer through Platform.Wake on a "cycle due" word.
- A collector thread is preemptible everywhere it does not hold the heap
  lock, and it never holds the lock longer than a budget.

## Part 3 (runtime): no whole-heap work per cycle

- Class-page object starts are arithmetic: a page's blocks sit on its
  class's grid from its first block, so they are computed, never stamped.
  Only the mixed heap needs a start record.
- The mixed heap's start bitmap is kept as blocks are handed out and freed,
  one bit at each, so BuildBlockAnchors as a whole-heap walk disappears. The
  snapshot then only freezes the bitmap, copy-on-write per chunk or by an
  epoch per chunk.
- Cards are taken a range of the table per budget, each range claimed by
  CAS.
- Sweeping is per chunk, claimed by CAS, and lazy: an allocator that needs a
  page may sweep that one page itself. The collector threads sweep the rest.

## Part 4 (kernel): allocate less

The kernel's allocation during play fell 22-fold, with no collection during
play. The per-call accounting that measures it reads `Gc.AllocatedByThread`,
which walks the thread's own class lists a few links at a time
(OwnListBytes) so that a uniprocessor is never held inside the walk.

## Part 5 (runtime, after 1-3): scale on many processors

- Slow paths take a per-size-class lock (a class's partial pages) and a
  per-chunk lock (carving), not the heap lock.
- The heap lock remains only for the cycle's phase changes, held for
  microseconds, or becomes a CAS on a control word.
- Marking stays parallel on the grey queue. Sweeping, card taking and
  snapshot freezing are claimed per chunk or range by CAS, so N threads share
  them.

## How it is judged

- The kernel's latency tracer on the real machine during play: no wanted
  switch delayed over 10 ms, and the mixer never late.
- `Gc.LongestStopStep` under 1 ms on a K6-III at 550 MHz.
- The kernel's allocation rate during play, from its per-call accounting.
- No loss of throughput: the collector tests' times on 486, P5 and K6, A/B
  against the build before.
- Correctness: the checks of marking (`Gc.VerifyMarks`) find nothing
  (`Gc.VerifyReports` is 0) in the stepped stop's stress test (2435), which
  answers tens of thousands of blocks one by one a run.
