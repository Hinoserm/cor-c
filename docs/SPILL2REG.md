# Spilling to vector registers (Spill2Reg): studied, not built

A spilled general register can be kept in an idle vector register instead
of a frame slot: `movd xmm7, eax` for the store, `movd eax, xmm7` for the
reload. It was studied for the register allocator (10-28) and **not built**,
because it loses or breaks even on every CPU the compiler targets today. This
note keeps the study so it can be picked up when a target where it wins is
added. Nothing in the compiler implements it.

## When it wins

It wins only where a move between a general register and a vector register
costs less than a store and a load through L1 -- or uses execution ports the
stack does not, on a machine where those are the bottleneck. Measured
latencies (Agner Fog, *Instruction tables*, 2022; clock cycles; "ops" are
the decoder's macro- or micro-operations):

| CPU | GPR to vector | vector to GPR | L1 store / load | verdict |
|---|---|---|---|---|
| 386, 486 | -- | -- | -- | no MMX or SSE |
| Pentium (P5) | -- | -- | 1 / 1 | no MMX |
| Pentium MMX (P55C) | MMX 1 | MMX 1, result needed a clock early | 1 / 1 | no gain; MMX and x87 switches cost 38-58 clocks |
| K6, K6-2, K6-III | MMX | MMX | load 2 | not measured in the tables; MMX/x87 switch (FEMMS) on every function using both |
| K7 Athlon | MMX 9 | MMX 7 | store 1 op, load 3 | loses |
| K8 Athlon 64 | MMX 9; XMM 3 (3 ops) | MMX 4; XMM 2 (3 ops) | store 1 op, load 3 | XMM: reload -1 clock, store 3 ops for 1 -- a wash |
| Geode LX | -- | -- | -- | no SSE; MMX not measured |
| Zen 1 | XMM 3 | XMM 3 | store 3, load 4 | small win possible |
| Skylake | XMM 1 (port 5) | XMM 1-2 (port 0) | store 2, load 2 (ports 2/3/4/7) | win where load/store ports are saturated (the LLVM RFC's measurements) |
| Zen 4 | XMM 4 | XMM 3 | 0-4 ("may mirror": store-to-load memory renaming) | loses: a stack spill and its reload are already nearly free |

The pattern: worth trying on Zen 1-3 and on Intel from Skylake to just before
memory renaming; not on anything this compiler targets now (P5 to K8,
Geode). Re-measure before turning it on for a new `--cpu`.

## What it needs here

- **Vector state the code owns.** XMM registers are usable only where the
  thread owns its SSE state (`MFunction.SseState`, Lowering.OwnsFpuState) and
  `--cpu` has SSE2 for MOVD to and from XMM -- in the kernel, only in code
  that owns that state the same way. MMX shares the x87 stack:
  any function that spills to MMX must leave with EMMS/FEMMS before any x87
  code runs, its callees included -- in practice, MMX is not worth it.
- **Free vector registers.** Only XMM registers no value of the function's
  own uses, across the whole span of the spilled value (the allocator's XMM
  class; on i386 with SSE, all eight when the function does no packed or
  floating-point work in SSE).
- **Calls.** The compiler's own convention saves no XMM register across a
  call on either target (the allocator's XMM class has no callee-saved
  registers), so a value live across a call cannot sit in one: only spill slots
  whose lives contain no call qualify -- the opposite of most i386 spills,
  which happen because of a call. Fewer candidates than on x86-64 SysV code
  where LLVM measured it.
- **The collector.** A reference kept in an XMM register at a safepoint is
  invisible to the stack maps; safepoints are calls, which the previous point
  already excludes.

## How it would fit the allocator

The allocator already has the shape it needs: a spilled value's slot is a
number the target turns into an operand (`ITarget.NewSlot`, `X86Target.Slot`,
`X64Target.Slot`), and every store and reload is placed by the allocator and
emitted by the target (`IPlacement.Before`, `After`, `MovesBefore`). A
Spill2Reg pass would run after the scan, as LLVM's does:

1. For each spill slot, the span from its first store to its last reload,
   and whether a call (any `AInstr.IsCall`, the store sequences' stubs
   included) or a packed/SSE instruction lies inside it.
2. Assign free XMM registers to qualifying slots, interval-colouring the
   spans (they are short and few).
3. The target emits `movd xmm, r32` / `movd r32, xmm` (`movq` for a 64-bit
   value on x86-64) for those slots instead of the frame operand. A folded
   memory operand on such a slot (`add eax, [slot]`) must be unfolded first:
   no ALU instruction takes an XMM source for a GPR operation.
4. Count it separately in `--count-spills` (a third total), or the counter
   reports a win the machine may not see.

Measure with the real clock on the target, not with `--count-spills`: the
counter would drop to zero for these slots whatever the moves cost.

## Prior art

- V. Porpodas, *[RFC] Spill2Reg: Selectively replace spills to stack with
  spills to vector registers*, llvm-dev, January 2022; LLVM PR #118832
  (2024-25). Wins on Skylake-SP, where the stack's store and load ports and
  the vector moves' ports differ.
- GCC `TARGET_SPILL_CLASS` / `X86_TUNE_GENERAL_REGS_SSE_SPILL`: enabled for
  some Intel tunings, then disabled in June 2016 ("the spill-to-vector regs
  infrastructure is not yet ready"; PRs 70902, 71453, 71555, 71596, 71657).
- A. Fog, *Instruction tables* (2022), the latencies above.
