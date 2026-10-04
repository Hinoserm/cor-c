# Regions

A region is a call whose objects die by the time it returns, and which gives
them back all at once when it does. The compiler and the link prove which
allocations are dead at which returns; the runtime makes those allocations in
a per-thread arena and sets the arena back when the call returns. Nothing in
a region is swept. A block is given back before its region ends only from
the region's top, and nothing on the heap ever points into one.

This document describes the system as it stands: the runtime arena, the hints
a unit compile writes, the two link engines that answer what outlives a call,
the judge that picks boundaries and sites from those answers, and how the
regenerated units apply the result. Source comments are the authority for
detail; each section names the files.

## Terms

- **Site**: an allocator call in a function, numbered by its ordinal among
  that function's allocator calls (`RegionPointsTo.MarkSites`). A site is
  *rewritable* when it calls a collecting allocator.
- **Boundary**: a function that opens a region (`Runtime.RegionEnter`) and
  closes it on every return (`Runtime.RegionLeave`).
- **Taken site**: a site made in the innermost open region
  (`Runtime.AllocRegion`) instead of on the heap.
- **Loop region**: a region opened at the top of every lap of a natural loop
  (`Runtime.RegionLoop`), whose laps each leave dead what they make in it.
- **Beside**: an allocation made in the innermost open region for the object
  that will own it, if that object is in that region or in a stack frame that
  returns no later than the region ends, and on the heap otherwise
  (`Runtime.AllocNear`).

## The runtime arena

`runtime/src/core/gc.cor`, section "regions"; the entry points are thin
wrappers in `runtime/src/core/runtime.cor` (`RegionEnter`, `RegionLeave`,
`RegionLoop`, `RegionCatch`, `AllocRegion`, `AllocNear`).

**Per thread, nothing reserved.** A thread that never opens a region has no
arena. The first region it opens maps one chunk, the size of what it first
needs rounded to a page. `Tls.RegionBase` is the first chunk, `Tls.RegionEnd`
the chunk being filled, `Tls.RegionAt` the bump pointer in it, and
`Tls.RegionOpen` the innermost open region's record.

**Blocks are heap-shaped.** A block in the arena has the heap's header, kind
and footer, so the collector can read it. The arena's live part is scanned
with the thread's stack (`ScanRegion`) for the heap objects its blocks hold,
and is never swept. The collector takes no pointer into the arena for a
block of its own.

**Chunks are named by their end.** A chunk's trailer, at its end, holds its
first byte, the chunks before and after it, and how far it was filled when
the arena moved on past it. The first chunk's trailer also holds the lowest
first byte and highest end of all chunks, which every membership question
(`ChunkOf`, `InOwnRegion`) checks before walking. Places in the arena are
ordered chunk by chunk, then by address.

**Growth.** When the innermost region's next block does not fit, the arena
moves on to the next chunk (`NextChunk`). That is the spare if it is big
enough, else a newly mapped chunk at least as large as the block and as all
the chunks before it together (`MapChunk`). So an arena of gigabytes is a
few dozen chunks, never more than twice what its regions hold. If the system
has no memory, the allocation goes to the heap and `Gc.RegionMisses` counts
it.

**Records and closing.** Each region begins with a record: a two-word leaf
block naming the region it is inside and the frame of the call that opened
it (`OpenRecord`), with the frame word's low bit set for a loop's region
(`LoopMark`; `OpenerFrame` reads the frame without it). `RegionLeave` sets the arena back to the record (`Cut`):
- everything past it is zeroed;
- every chunk past the record's chunk is unmapped, except the next one, which
  is kept as **the spare** so that a loop whose laps cross a chunk's end
  maps nothing per lap.

`Gc.RegionFreedBytes` and `Gc.RegionBlocks` count what regions gave back at
their ends and made. What was given back before the end, from the top (below),
is counted in `Gc.RegionTopFreedBytes` and `Gc.RegionTopFrees` instead, and
not again at the end. A test asking what regions gave back reads both.

**Sized regions.** Where the link proved the most bytes a boundary's region
holds in one call, or a loop's region in one lap, `RegionEnter` and
`RegionLoop` are handed that count (`Room`). The region is laid down where
all of it fits: in the chunk being filled if there is room, else in the spare
if it is big enough, else in a chunk mapped for exactly those bytes, rounded
to a page. Regions nobody sized grow by the doubling rule. `Gc.RegionsSized`
counts sized openings. The link and the runtime agree on block sizes through
`RegionLayout` (`linker/src/lto/RegionHints.cs`).

**Loops.** `RegionLoop(record, frame, bytes)` opens the loop's region on the
first lap. On each later lap it cuts back to just past the record, giving
back what the previous lap made, and any region still open inside it.

**Throws.** A throw out of a boundary runs no `RegionLeave`. The next region
opened, or the next region allocation, from a frame above a stale record
closes it first (`PopStale`). A catch in its own frame closes the regions
its throw left (`RegionCatch`, inserted into every landing pad by
`RegionPointsTo.CatchUp`).

**Beside an owner.** `AllocRegion(bytes, kind, owner, frame)` with an owner
puts the block in the innermost region when the owner lies in it
(`InInnermost`, which handles a region spanning chunks), or when the owner is
on this thread's stack in a frame that returns no later than the region ends
(`OnStackWithin`). The second is for a collection the link's lifetime pass
put in its function's frame: its storage grows in the region, not on the
heap. The rule:
- Every prologue is `push ebp; mov ebp, esp`, so the frame pointers form a
  chain of callers, each higher on the stack than its callee. The walk up the
  chain from the allocating frame must meet the region's opener frame
  exactly (at most 64 frames). That proves the opener is a live call on this
  stack, and that the stack between is its and its callees'.
- A function's region ends at its opener's return. An owner below the
  opener's frame pointer and at or above the stack pointer is in the opener's
  locals or a deeper call, so it is dead no later. A boundary is never
  inlined, so its frame is its own call's.
- A loop's region ends with each lap, while its frame lives on, so an owner
  in the loop function's own frame does not qualify. Only an owner below the
  frame pointer of the opener's callee on the chain qualifies: that call
  returns before its lap ends. With no callee (the allocation made in the
  opener's own frame), nothing qualifies.

Anything else goes on the heap: an owner on the heap, in an outer region, in
a frame above the opener, or in a loop function's own frame. Test 1315.

**Frees.** A free through a register does nothing to a region block:
`Runtime.Free`, `Gc.Free` and `FreeReplaced` check `InOwnRegion` first, and
`FreeManual` cannot find a region address among its own blocks. Such a
pointer may name memory a loop's earlier lap gave back and a later lap made
again.

A free of what a field of a live object held is different: the pointer is
the block itself. `Runtime.FreeHeld` (reached from `FreeField`,
`FreeOwnedFields` and `FreeOwnedReplaced`), given a region block:
- frees what it owns, if it is an object (`Gc.RegionObject`): its owned
  fields and its storage (`FreeOwnedFields`, `FreeStorageOf`). A block of no
  type, such as a captured variable's cell, owns nothing;
- then gives the block itself back (`Gc.RegionFree`).

The order matters: what the object owns was made after it, above it, and is
given back first.

**Given back from the top** (`Gc.RegionFree`). A block that ends exactly at
the innermost region's bump pointer, in the chunk being filled and past the
region's record, is given back at once. The pointer comes down over it and
its bytes are zeroed. Then the pointer continues down over every block below
it that was given back already. A block given back anywhere else in that
stretch is marked `KindFreed`, for the pointer to come down over later.
`ScanRegion` and `CopyRegion` skip such a block. Anywhere else, a block waits
for its region's end. The pointer moves before the bytes are cleared, so a
stopped thread's arena always reads whole. A List grown in a region and freed
with its arrays leaves nothing behind. Test 1303.

**Grown in place** (`Gc.RegionGrow`, through `Runtime.GrowInPlace`). An array,
or a string's characters, that is the innermost region's top block grows
where it is when the chunk has the room. The pointer moves past its new end,
then the block's footer, size and count follow it, and the new elements are
the zeroed bytes the arena keeps past its pointer. Otherwise nothing changes
and the caller makes a new array as before; on the heap it always does. Only
storage its collection's own field alone holds is grown so: List's array
(never once lent out), a StringBuilder's buffer, a Stack's array, a
MemoryStream's own array (never lent by GetBuffer), and the tables' lists of
vacant places. `Gc.RegionGrownInPlace` counts growths. Test 1304.

The checked field free (`VerifyingFieldFrees`) hands a region block to
`FreeHeld` as the unchecked path does.

**Concurrent marking.** The STAMP snapshot copies the arena as it copies the
stack (`CopyRegion`). It writes only words that could mark something: not
null, and not pointing into the arena itself. `RegionWords` bounds the copy.

Tests: 938, 940, 943, 973, 998 (throws, growth across chunks, stale regions,
regions opened where first needed); 1070, 1170 (loop laps); 1292 (sized
regions); 1291 (a region cell freed through its frame object); 1303 (given
back from the top); 1304 (grown in place).

## The unit's hints

`compiler/src/optimizations/RegionSummary.cs`, format in
`linker/src/lto/RegionHints.cs` (section `.corsac.regions`, version 8).

A unit compile states, function by function, the pointer constraints of the
IR the link will regenerate the unit from:
- **Nodes**: the parameters, the return, and every register that can hold an
  address and hands it somewhere. Registers that only copy another collapse
  into it (`Reduce`).
- **Constraints**:
  - `Site`, `Slot`, `Unknown`: a node holds a site's object, a frame slot, or
    the unknown object;
  - `Symbol`: a node holds the address of one of the function's `Symbols`,
    a constant or the unknown object as the link judges it (below);
  - `Copy` (with a byte shift, any offset, or an index scaled by 2^k and the
    address it moves);
  - `Load`, `Store`, `MemCopy`;
  - `Leak` (a throw).

  What the compile cannot follow is the unknown object: a call nobody can
  name, a system call's answer, a frame or label address, a constant address.
  A symbol's address is named instead (`Symbol`), for the link to judge.
- **Calls**: the callee, a virtual call's symbol (resolved by the link from
  the descriptors, `VirtualTargets`), or none. Each call lists its argument
  and result nodes. A call that keeps nothing it is handed is left out
  (`RegionPointsTo.Harmless`):
  - the runtime's frees and the collector's notes;
  - `Runtime.GrowInPlace`;
  - `Runtime.InvalidCastTo`, a failed cast's throw, which names the object's
    type and keeps nothing of it. Followed, it was a member of the runtime's
    cycle of exceptions, traces and symbol lookups. Unified there, every
    object any cast handed it went to the unknown object. Test 1299.
- **Async and iterator bodies**: what the body holds across a suspension is
  stated as stores into its state machine (`this`, at any offset), not as a
  leak of everything it touched. Since version 8. Test 1298.
- **Sites**: whether a region may take it, its line, the descriptor it
  stamps and where the method table begins (what a virtual call on its
  object runs), and how the collector reads its words (`RegionWords`).
- **Symbols**: the names of the symbols whose addresses the code takes, in
  the order the `Symbol` constraints number them.
- **Number parameters** (`NumberParams`): int, char, double and 32-bit enum
  parameters. Nothing a caller hands one is an address. Test 1290.
- **Number loads.** A load, or a copy, marked `Instr.Number` is never an
  address: on a 32-bit target an int and an address are one word, and an
  unmarked word read out of an object carries whatever that object's words
  are said to hold into every number made of it. The hints state no
  constraint for a marked load or a marked atomic. Marked:
  - fields, elements and cells of number types (`NeverAddress`: not a long
    or a nint, which may hold one);
  - a string's or a sequence's count (`CountOf`);
  - the loads lowering makes of integers (`Lowering.Numbered`): a type's
    flags, depth and entry count read from its descriptor; a Nullable's
    has-value byte; a box's number; a float's bits; a string's characters;
    an iterator's state and an array view's cursor; an async or iterator
    machine's size and number parameters; a static initialiser's flag;
    errno;
  - the copies `Sys.Copy` and `Sys.CopyNoOverlap` make, which are declared
    over strings and byte arrays only.

  `FrameAddressFold` keeps the mark when it folds a load's address.
  Deliberately left unmarked: `Sys.Peek` and its narrower forms (the runtime
  builds addresses out of words and bytes), the atomic intrinsics (their
  operands are longs), and the thread block's words. Tests 1290, 1316.
- **Must-run calls and sites** on every way to a return (`MustCalls`,
  `MustSites`), for judging whether a loop's lap is worth a region.
- **Loops**: natural loops that may get a region, by header position. Not in
  async or iterator bodies, type initialisers, or functions with landing pads
  or label addresses. For each: the sites and calls in its body, those every
  lap runs, the nodes live at a lap's end, the nodes live into the header
  that the body never writes, and the frame slots kept from before it.
- **Repeats and bytes**, for sizing: each natural loop with its parent and
  trip count where known; per site and per call, the innermost loop it is
  in, `Unbounded` or `Throwing`; per site, its block's bytes when constant.
- **Flags**: exported, may be a boundary (not async, not an iterator, not a
  type initialiser, not `Main`), instance method, `Main`.

## The link

`linker/src/lto/IrLinkOptimizer.cs` reads every unit's hints, resolves
virtual calls, and calls `RegionSolver.Solve`. It runs only for a closed
image whose units all carry hints and whose runtime has the region entry
points. The answer, a `RegionFacts` per unit, goes to each regenerated unit
with its lifetime facts (`BackendProtocol`).

`--region-engine escape|andersen` picks the engine that answers what
outlives each call. **Escape is the default.** `corc project` passes the
switch, and `--region-report`, through to its link, whether that link runs
in the project's own process or a child.

### The escape engine

`linker/src/lto/RegionEscape.cs`.

**Functions from the bottom of the calls up.** Each function is solved by
itself. It is an inclusion solve (`Graph`) over the function's own nodes,
with locations that are an object and a byte offset, or any offset. Its
objects are:
- its sites and frame slots;
- the unknown object;
- **places**: what a parameter points to, and what is reached from that by
  up to two fields (`PlaceDepth`), with one deep place for everything below.

A load of a place finds the place one field further on, besides whatever was
written there.

**Summaries.** What a function writes into places and the unknown object,
what it hands back, and the objects it made that those reach, is its
summary. A caller applies it at each call: a place becomes what the caller
reaches by the same fields from the argument, and a made object becomes one
made at that call. Summaries are bounded (`MostFresh`, `MostPlaces`,
`MostCells`); past the bounds they are made coarse, never dropped.

**Cycles.** A cycle of calls is solved as one graph, a call between members
being edges into the callee's nodes. A member's summary for calls from
outside is what it does to its own places.

**What outlives a call.** A function's answer (`Escaping`) is the set of
origins reached from its parameters, its result, the unknown object, and
every place written. `Global` marks the sites the unknown object reaches,
and what functions called from where nobody follows hand back (roots: the
entry, code outside the IR, a taken address).

**Cost bounds.** Every bound only ever answers more outliving.
- A node past `MostHeld` (256) made objects, or `MostPlacesHeld` (512)
  places, is saturated: it holds the unknown object, and what it held goes
  to the unknown object's cell. That cell and the node fed by `Aliased`
  are never saturated.
- Copy cycles are collapsed online. Tarjan's components over the copy edges
  are found at each solve's start and whenever a quarter more copy edges
  have appeared. A worklist in wave order (sources first) carries each
  node's delta.
- A function or cycle that carries more than `_mostCarried` locations
  (150,000 plus 20 a node) falls back to unification. So does a cycle of more
  than 300 functions (`LargeCycle`), or one with more than 100,000 nodes
  (`LargeNodes`).

**Unification (`Unified`).** Steensgaard's analysis by field: every node
points to one class, and a class holds one class at each offset. It is
linear in what the cycle states, and coarser. Calls out of the cycle apply
summaries as the inclusion solve does.

A member's own summary, for calls from outside, is stated field by field
(`ByField`):
- each argument's class is its place;
- what each field holds is a place one field further down;
- a class holding objects made in the cycle is a made object;
- the unknown object's class is the unknown object.

So what one field of an argument holds leaks or not apart from what another
field holds. Past a walk of 4096 classes or 512 objects, the summary is the
coarse one (`Coarsest`): every argument and all it reaches one object, which
the unknown object holds as soon as any argument reaches it. Test 1305.

**One body, solved once.** The same body in several units (an iterator's
MoveNext, a lambda over shared code, a generic method specialised per unit,
a box stub, a key helper) is one function per unit. Every copy is a target
of every call that may run any of them: `ResolveOverride` for a local copy,
and every unit's definition for a global one, since only the final link
coalesces those. The engine solves each body once, as its first copy in
link order (`RegionEscape.Bodies`):
- two functions are one body when they have the same name and say the same
  to the engine (`SameBody`: parameters, nodes, slots, number parameters,
  constraints, calls' callees, results and arguments, sites' descriptors and
  words, loops, whether their symbols are constants), and each of their
  calls runs the same bodies. The partition is refined until no class
  splits;
- every call of a copy is a call of the body, and a body is rooted or asked
  about when any copy is;
- a copy's answers are its body's at the same ordinals: its sites are global
  when the body's are (`Mark`), outlive it when the body's outlive the body
  (`BitsOf` sets a body site's copies too), and its loops hold what the
  body's do;
- a virtual call in a body runs on an object what it runs in any copy, each
  resolved in its own unit (`RunsOn`).

What one copy's callers do with its objects, every copy's sites answer, as
one function called from all of them would. Symbols' names, sites' lines and
what only the judge reads are not compared. The report line counts the
functions solved as another copy.

**Wide calls and stand-ins.** A virtual call with more than 16 targets
(`WideTargets`; `+wide=N` changes it, `+wide=0` follows every call) is not
an edge of the order. Equals, GetHashCode, ToString and an iterator's
MoveNext would otherwise join thousands of functions into one cycle. Each
set of targets is assumed a stand-in:
- a shape of a few classes: the unknown object; what the targets make,
  split in two; and each of the first six arguments and all below it;
- one bit for each class that may hold another or be returned;
- one bit for the unknown call.

What the targets make is two classes (`HeldClass`, "Mu" in the report):
what the unknown object reaches in a target's own summary, and everything
else. Without the split, one target that throws what it made, or caches it
in a static, made every object any target makes the unknown object's: every
string a ToString hands back, every enumerator, every object stored into an
argument, and, past `WidenAfter`, every site beneath the targets. Widening
adds to the second class only. Each object is still in one class and each
cell joins two classes, so the shape is as sound as before. Test
`WideHeldApart` (engine tests).

The coarse summaries past a summary's bounds (`Fewer`, `Coarse`,
`Everything`) keep the same split between what the unknown object reaches
and the rest. `Everything` also keeps each parameter's own object and one
deep place below it, so one argument that leaks no longer leaks every
argument of every call.

**Narrowed by the receiver.** A wide call is watched at its receiver, as a
narrower virtual call is (below). An object made at sites of known classes
gets the stand-in of only the overrides its classes run (`WideGroup`,
`NarrowedStandIn`); anything else gets the whole call's stand-in. Without
this, an iterator walked through `IEnumerator<T>.MoveNext` took on what any
of some two thousand MoveNexts does with `this`. A narrowed stand-in is
assumed and grown like any other, never the targets' summaries, which may
not be solved yet. A cycle solved by unification keeps the whole stand-in.

**Growing a stand-in.** A stand-in grows as soon as it is seen short
(`Cover`), not only between rounds:
- made, it covers at once every target already solved, so what applies it
  later in the same round has it right;
- a target published with a new shape grows every stand-in it is among
  (`_standInsOf`);
- at the end of a round each stand-in is checked against all its targets'
  summaries (`Check`), in passes while a pass grew some stand-in's sites.

After round 3 a stand-in that still grows takes every site its targets reach
at once (`WidenAfter`), and after round 8 every call is followed in order
instead (`MostRounds`). A stand-in keeps one holder for good. Its two made
classes are always built first, whether or not used:
- what its targets make, at index 1 (`StandInMade`);
- what the unknown object reaches of it, at index 2 (`StandInHeld`).

What an applier made of either is `Ref(holder, 1)` or `Ref(holder, 2)`,
whatever the stand-in grows to. Growth in either class's sites is growth
in sites alone. It is stamped as the holder's origins changing in place
(`Cover`, `OriginsChanged`), and `Check`'s passes and in-place origin updates
cover both.

**Incremental rounds.** Each solve is a tick, and each change is stamped
with the tick it was made at. A later round re-solves a component only if,
since its last solve (`Again`):
- a callee outside it, not called as a wide call, came out with a summary
  of another shape;
- a stand-in it applied, whole or narrowed, grew in shape (new bits);
- a holder it read sites through changed its origins in place. Sites are
  read when an object's sites are asked: a virtual call's targets on it, a
  guard, a word never read as a reference (`SitesOf` notes the holders).

A summary that changes only in its made objects' origins is updated in
place, under the same holder (`Publish`, `SameShapeAs`). A caller re-solved
would come out the same except for what it asked of those sites, so only
the components that asked are re-solved. Every answer is read from the
holders at the end (`Close`, `BitsOf`), so an answer not re-solved is still
the final one. Otherwise, holder numbers are never reused:
- a summary that comes out the same keeps its holder and object;
- one of another shape gets a new holder, so every reference still held
  elsewhere means what it meant;
- each component's global and rooted origins are its own, replaced when it
  is re-solved.

A component that went past its bound by inclusion is unified at once in
later rounds (`StraightToUnified`): what it reads only grows, so inclusion
would give up again after the same work. The round's line in the report
counts stand-ins made, grown in shape and in sites alone, and why each
component was re-solved.

`+widefirst` starts every stand-in with all its targets' sites. That means
fewer rounds, but coarser answers above wide calls.

**Receivers and guards.** A virtual call made on an object whose site is
known runs only the override that site's descriptor holds at the slot
(`TargetsOn`, from the site's stamp). On a place, whose object nobody here
knows, each override's summary is applied to the place *guarded* by it: the
objects there that run that override. A caller reaching the place by the
same path keeps, at the guard, only the objects of sites whose descriptor
runs it, and any of no known descriptor. `+classoff` turns this off.

**Boxes and strings.** A box's table and a string's name none of their system
interfaces (IComparable, IComparable<T>, IEquatable<T>, IFormattable), so
that every unit's copy of a box is one table. A call through an interface
therefore counts every box and the string among the objects it can run on
(`VirtualTargets.MayAnswer`), both in the link's targets and in its
`IsA`, which answers "not known" rather than "no". Test 1302.

**Number parameters.** A call hands nothing to a parameter of a number type
(`IsNumber`), in both engines. Test 1290.

**Constants.** Before solving, the link judges every `Symbol` constraint
(`linker/src/lto/RegionConstants.cs`, over every object's symbols and
relocations): a function, or read-only data whose every relocation names a
constant in turn -- a string literal, a type's descriptor or vtable, a table
of numbers -- is a constant; a static's storage, writable data, or a symbol
nobody defines becomes `Unknown` as before. `--region-report` prints how many
symbol addresses were constants. Test 1299.

A constant's address holds nothing either solve need follow: it is no site's
object, it is never written, and what is read from it is a constant again.
- In the inclusion graph it is **one object of its own** (`Graph.Constant`),
  made of no site and with no origins. A load from it gives it again, a store
  into it goes nowhere, and a summary keeps it as a made object of no
  origins. It is an object, not nothing, because of receivers: a virtual
  call watched at its receiver applies its targets to each location the
  receiver holds, so a receiver that held nothing would be a call that never
  runs, and a method called on a literal or a descriptor would hand its other
  arguments nowhere. A receiver that may be the constant runs any of the
  call's targets, and a guard lets it through, as any object of no known
  site. A stand-in's shape counts it among the made objects, with no sites.
- In unification (`Unified`), which has no receivers, a constant binds to
  nothing, so storing a literal into a field no longer joins that field to
  the global class.
- The Andersen engine still takes a constant for the unknown object: it binds
  an instance call by the objects its receiver holds, and a method called on a
  literal runs for that object.

**Unpassed parameters.** A parameter a call passes nothing for is the unknown
object, in both solves, for a call into a cycle as for a summary applied.

### The Andersen engine

`linker/src/lto/RegionSolver.cs`, `--region-engine andersen`. It is one
inclusion solve over the whole image, field-sensitive, with object contexts:
- an instance method is a copy of its nodes for each object it is called on,
  and what it makes is made once per such object, two contexts deep;
- an instance or virtual call is bound by the objects its receiver holds.

Sets are sparse bitmaps, and copy cycles are collapsed. Past fixed budgets
(`NodeBudget`, `HeldBudget`, `LocationBudget`, `HeapBudget`) it tries a
coarser context depth, and finally gives up: nothing is made in a region.

The flat whole-program compile uses the same design within one module
(`compiler/src/optimizations/RegionPointsTo.cs`), whatever `--region-engine`
says.

### The judge

`RegionSolver.Judge` turns either engine's answers into boundaries, sites
and loops.

1. **Nearest.** For each site's objects, it walks up from each copy that
   makes them, through the callers, to the nearest function whose return
   they are proved not to outlive and that may be a boundary. Excluded are
   `Main`, what the entry calls itself, functions before the thread block
   exists, and the entry.
2. **Evaluate.** `NearestAbove` finds, for every copy, the first boundary on
   each way up. A site is taken only when no boundary that can be innermost
   above any copy making it outlives any of its objects, because
   `AllocRegion` takes whichever region is open innermost. A function on a
   cycle may be a boundary: each activation opens its own region.
3. **Loss and Gain.** A boundary is dropped when the sites it alone refuses,
   which another boundary above would have taken (Loss), outnumber the sites
   taken only for it (Gain). At most four rounds.
4. **Loops** (`SelectLoops`, `TakenWithLoops`). A loop gets a region where
   nothing made beneath it, in its body or in what its calls reach, is live
   at a lap's end, except what a lap carries only through what the loop
   writes. It must also be worth a call at every lap (`AlwaysMakes`).
   - **Fix A**: a site the boundaries take that is live at a lap's end, but
     made with no boundary between the loop and its maker, no longer refuses
     the loop. It is sent to the heap instead, provided fewer sites go to the
     heap than every lap makes for the loop's region *that no boundary
     takes*. A site a boundary takes already is only given back sooner by the
     loop, while one sent to the heap costs an allocation every lap. A loop
     that gains nothing else gets no region whenever it sends anything to
     the heap. Test 1290.
   - A site made beneath a boundary inside the lap still refuses the loop.
   - A loop with nothing taken beneath it is dropped.
5. **Opening.** A boundary with nothing taken innermost beneath it is not
   opened.
6. **Sizes.** Where it can be proved, the most bytes a boundary's region
   holds in one call, or a loop's region in one lap, is computed from site
   bytes, loop trip counts and callees' own bytes (`Sizes`). Nothing is
   proved through unbounded loops, calls nobody can name, or recursion. On a
   32-bit target a proof past a gigabyte leaves the region unsized: the size
   is handed over as an immediate of the target's word.

Code nobody follows may make and keep an object the unknown object reaches;
such a site is never taken (`_unseenKept`).

Tests: 970 to 973 (boundaries across units, a static cache, virtual makers,
throws); 1110 (sites inlined across units); 1191, 1192 (list elements and
array views); 1200, 1201 (a front end's tree, freed or kept); 1243, 1244
(struct array element words); 1260 to 1266 (a buffer grown below `this`,
aliasing through parameters, statics in a virtual cycle, recursive returns,
laps kept in a list, number parameters, a size check's exception); 1264,
1170 (loops).

## Applying the facts

`compiler/src/optimizations/RegionPointsTo.cs` and
`compiler/src/driver/UnitBackend.cs`.

1. The regenerated unit's sites and loops are marked on the IR before any
   late pass runs (`MarkSites`, `MarkLoops`). The ordinals are the same ones
   the hints numbered, and the marks ride on every copy the inliner makes.
2. A boundary is never inlined. It opens its region where it first needs it
   (`OpenAt`), not always on entry, with its proved bytes, and closes it at
   every return. Loop regions are opened at their headers (`OpenLoops`).
3. The link's lifetime passes run first, so an object they place in the
   frame or free where it dies was never the region's. They run again after
   the regions are placed (`Escape.RunAtLink`), and put their own frees just
   before each return, after the boundary's `RegionLeave`. `LeaveLast` then
   moves each leave back to just before its return, past those frees. This
   matters for storage made beside a frame owner: it is the region's, and
   freed after the leave, its frees read memory the cut had zeroed or
   unmapped. A leave stays where it was if anything after it allocates,
   since what that made would be cut with the region.
4. What is left of each marked site becomes `AllocRegion`
   (`MakeSitesInRegion`), and every landing pad gets `RegionCatch`
   (`CatchUp`).

**The marks survive the archive.** The link rebuilds a unit's functions from
its archived IR more than once: its late passes, each lifetime run, and the
copy a refused run is taken back from (`UnitBackend`). Since IR function
record version 6 (`IrFunctionCodec`), the record carries every mark these
analyses rely on:
- an instruction's `Number` and `RegionSite`;
- a parameter's `Number`;
- a block's `RegionLoop` and `RegionLoopBytes`.

Before that, a function taken back after a refused run read its numbers as
addresses again and lost the regions it had just been given. An archive
written by an older corc is refused ("Unsupported IR function version"), so
units are rebuilt.

**Owned storage beside its owner.** A collection's storage (a List's array,
a Dictionary's keys, values and tables) is an owned field that the
collection frees itself as it replaces it (the self-replacing free,
`compiler/src/optimizations/EscapeSelfFrees.cs`). The link marks such fields
`SelfFreed`.

Where the image opens any region, each regenerated function makes an
allocation beside its owner (`MakeStorageBeside`) when all of these hold:
- the value, through copies, is stored only into self-freed owned fields;
- those fields all belong to one object;
- that object is held in a register written once, or a parameter never
  written;
- the register is defined before the allocation on every path to it.

The library frees the storage it outgrows or drops as an owned field's old
value, `Runtime.FreeOwnedReplaced(v, 0)`. It does so in
`OutgrownStorage.Release` (List, Queue, Stack, the tables, FloatBig's
limbs), `StringBuilder.Reserve` and `FreeStorage`, and MemoryStream's
buffer frees. Test 1312. On the heap that is `Runtime.Free`; in a region it gives the
array back at once from the top (`FreeHeld`, `Gc.RegionFree`).

The compiler knows two spellings of the self-replacing free
(`EscapeSelfFrees.StorageFreed`):
- `Runtime.Free(v)`;
- `FreeOwnedReplaced(v, 0)`, the second operand the constant zero.

The compiler's own `FreeOwnedReplaced(old, new)` never matches: the old
value it frees is a load it inserted, which names no field.

Before the library makes a new array, it tries `Runtime.GrowInPlace` (above).
The lifetime and region passes know that call by name as one that keeps
nothing (`Escape.IsCollectorNote`, `RegionPointsTo.Harmless`), so the inliner
keeps it a call until they have run: the unit's inliners pin it with the
other helpers (`Inline`'s pinned helpers), and the link's per-function
inliners keep it (`UnitBackend`, `Keep`), there being no inliner after the
link's lifetime and region passes. Inlined,
its body handed the storage to `Gc.RegionGrow`, whose stores into the
thread's block leaked it. Elements a collection owns are made beside it the
same way (`OwnedElements.ElementSites`). Tests 1270, 1271, 942, 1303, 1304.

**Frame closures.** A captured variable's cell is owned through a field of
its call's frame object and freed with it. A region takes the cell's site
like any other. The cell is a block of no type, which `FreeHeld` leaves to
its region. Test 1291.

## Types asked at run time

Two answers the link's targets and the regions rely on are also given at run
time, by `Runtime.DescribedAs` (`runtime/src/core/runtime.cor`), when only run
time knows the type asked about.

**A box's or a string's interfaces.** An interface's own descriptor says
which boxed face it is (`Lowering.BoxedFaceFlags`), in its flag word:
- `IComparable` (64);
- `IFormattable` (128);
- `IComparable<X>` or `IEquatable<X>` of a number, a bool, a char or a
  string X (256).

Word 10, unused by an interface, names X's own descriptor. For IFormattable
it names bool's box instead, the one primitive that is not formattable.
`DescribedAs` answers by these after the interface list, as
`BoxedFaces.Implements` does: in a shared copy's test, in
`ArrayStoreCheck` and in `ArrayOf`. Both are read from the interface alone,
so every unit's copy of its descriptor is the same bytes. Test 1300.

**A shared generic copy's own types.** In the one copy every reference
instantiation shares, a test, `as`, cast, switch arm, typeof or array of a
type constructed over its T reads that type for the object at hand
(`Monomorphiser.CanonTested`, `Lowering.CanonTest`). It takes the
descriptor the instantiation's type context holds. Where the context holds
none, the `__canon` form answers as before. Tests 1300, 1301.

## Diagnostics

`--region-report NAMES` (also `corc project --region-report`) logs the
solve and judge. Names without a `+` select the functions whose boundaries,
loops and refusals are reported. Switches:

| Switch | Effect |
| --- | --- |
| `+sites` | Every site's verdict: taken, global, unseen, refused-by a boundary, loop-refused, or no-boundary, with its line and stamp. |
| `+standins` | Each round's largest stand-ins and what they hold; how many have the unknown object holding an argument, and which targets' summaries make it so, and how each was found (inclusion, unified, past its bound, coarse). |
| `+why=NAME` | For the functions whose names contain NAME, which of their sites the unknown object reaches, and by what. |
| `+cycles` | The largest cycle with wide calls of more than 256, 64, 16 and 4 targets left out, and the widest slots. |
| `+wide=N` | Virtual calls of more than N targets are stand-ins (default 16; 0 follows every call). |
| `+widefirst` | Stand-ins start with every site their targets reach. |
| `+loopold` | Weigh a loop's region as before the loop rule's change: what it sends to the heap against every site its laps make that it takes, a boundary's included (an A/B of the rule). |
| `+classoff` | No receiver classes or guards: a virtual call runs every override. |
| `+noroots` | Leave out what functions called from where nobody follows hand back (a diagnostic only; unsound for an image). |
| `+norefoff` | Ignore the collector's knowledge of words that hold no reference. |

The runtime's counts appear with `--gc-stats` (or the program's own
`AppContext.SetSwitch("Corsac.GC.Stats", true)`): bytes given back by
regions, and region allocations the system had no memory for; and what the
arenas took of the system against what they held, so that a program's
regions can be held to taking what they need and no more:
- `gc: arena peak mapped bytes`, `gc: arena peak used bytes` and their ratio
  (`gc: arena mapped per used at peak percent`): the most bytes mapped for
  arenas at once, and filled at once, summed over threads. Mapping is
  counted where a chunk is mapped or handed back; filling is measured where
  a region ends or a lap is cut, where the arena moves on to another chunk,
  and before a block is given back from the top -- the moments the fill
  stops rising -- and only while the stats are asked for;
- `gc: arena mapped now bytes`, `gc: arena bytes asked of the system`,
  `gc: arena chunks mapped`, `gc: arena chunks unmapped`, `gc: arena spares
  reused` (moved on into the spare a region's end kept);
- `gc: region blocks`, `gc: sized regions`, `gc: sized room in the chunk` and
  `gc: sized room in another chunk` (a sized region's or lap's bytes found
  where the arena was, or in the spare or a chunk mapped for exactly them);
- `gc: grown in place`, `gc: region top frees`, `gc: region top freed bytes`.

Nothing is counted on a bump allocation. Test 1313.

**Measuring.** Three small scripts kept outside the repository, in the
integration's working area, compare runs:
- **A site differ:** reads two links' `+sites` output and prints:
  - the totals per verdict;
  - how many sites changed verdict, by pair (taken to global and so on);
  - the functions and the types with the most sites one link takes and the
    other does not, both ways.
- **A stand-in summariser:** reads a link's `+standins` output and prints,
  round by round:
  - the wide calls assumed, how many grew, the components solved, the
    locations carried and the time;
  - the stand-ins whose shape lets the unknown object hold something, by
    their first target, with the targets that make them so.
- **A `--gc-stats` lister:** puts several programs' `gc:` lines side by side.

`gc: given back by regions MB` counts everything regions gave back, at their
ends and from their tops; `gc: region top freed bytes` gives the second
alone. `+loopold` is the A/B for the loop rule: link twice and compare the
loops selected and the sites taken.

**Engine tests.** `linker/tests` with `--escape` runs only the region hint
tests and the escape engine's own tests (`RegionEscapeTests.cs`), which need
none of the tools. Each states a small program as its region constraints,
solves it with `RegionEscape` directly, and checks the answers (`Escaping`,
`Global`, `LoopHeld`, a summary through `SummaryOf`) against what the
program means, argued beside each test. They cover:
- stores, loads and returns;
- loops, cycles, unification and summaries by field;
- wide calls, constants and number parameters;
- receivers and guards, saturation and coarse summaries;
- unpassed parameters;
- the hints and a backend request, round trip.

Run without `--escape`, the whole suite includes them.
