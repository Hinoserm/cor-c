# Inline assembly

An `asm { ... }` block puts machine instructions in a COR-C# method. It is
one of the few departures from C# the language allows. The block is Intel
syntax, read by the compiler's own assembler
(`compiler/src/arch/x86/assembler`), and it names the method's locals,
parameters and constants in its instructions directly, as MSVC's `__asm`
does. There are no operand, output or clobber lists: the compiler reads
every instruction, so it works all of that out itself
(`Corsac.Asm.InlineAsm`).

```csharp
static ulong ReadTsc()
{
    uint lo, hi;
    asm
    {
        .runtime tsc
        rdtsc
        mov lo, eax
        mov hi, edx
    }
    return ((ulong)hi << 32) | lo;
}

static void Cpuid(uint leaf, uint subleaf, out uint a, out uint b, out uint c, out uint d)
{
    uint ra, rb, rc, rd;
    asm
    {
        .runtime cpuid
        mov eax, leaf
        mov ecx, subleaf
        cpuid
        mov ra, eax
        mov rb, ebx
        mov rc, ecx
        mov rd, edx
    }
    a = ra; b = rb; c = rc; d = rd;
}
```

## Where a block may stand

`asm {` begins a statement: after `;`, `{`, `}`, `)`, `:`, `else`, `do` or
another block, with white space or comments between `asm` and its brace.
Anywhere else `asm` is an ordinary identifier, so a property named `asm`
still compiles, and so does a type named `asm` after a base list's or a
constraint's colon (`class C : asm {`). The lexer reads the block's text raw, up to the first `}`
that is not in a comment or a quoted string, so an assembly comment's
apostrophe is not a C# character literal. A block has no braces of its own.

One statement to a line; labels first (`again:`), a `;` comment to the end of
the line. The only directive is `.runtime`.

## Names

A name an operand spells that is not a register, a size word (`byte` ...
`oword`, `ptr`) or a label of the block is looked up in the method:

- **a local or a parameter**: the instruction reads or writes it. It must be
  a whole operand (`mov eax, x`, `dword x`), never part of an address or an
  expression: load it into a register first.
- **a constant** (a `const` local, or a `const` field of the type or one
  around it, by its plain name): it is written into the block as its
  number, and may stand anywhere a number may, inside an address too.

What the block does with each local follows from its instructions:

- a local read before the block writes it, on some path, is an **input**: C#'s
  definite assignment holds for it as for any read;
- a local the block writes is an **output**, assigned as any assignment
  assigns it. One the block writes on some paths only is an input too: where
  the block does not write it, it keeps its value.

A name spelt as a register (`cx`, `si`) is the register. A local, a
parameter or a constant of that spelling cannot be named in a block, and
using the spelling while one is in scope is an error: rename the local.

A local's type must be one register's worth: an integer, `char`, `bool`, an
enum, a pointer, `nint`/`nuint`, `float` or `double`. **Its width is the
register's it meets**: `mov eax, x` with a `ushort x` is an error, not an
extension. An object reference cannot be named; the collector could not
follow it into a register. Pass its address as a `nuint`, and keep the
object alive while the block runs. A struct cannot be named either.

## Permission: `.runtime`

Inside a block **the baseline is the 386**, with an x87 where the target has
one, **not `--cpu`**. Anything newer needs the block's own `.runtime`, which
names what the code checked for: a CPUID feature bit (`mmx`, `mmxext`,
`3dnow`, `3dnowext`, `cmov`, `sse`, `sse2`, `tsc`, `msr`, `cx8`, `fxsr`,
`clflush`) or a single instruction (`cpuid`, `rdtsc`, `rdmsr`, `wrmsr`,
`cmpxchg8b`, `cr4`, `bswap`, `xadd`, `cmpxchg`, `invlpg`, `invd`, `wbinvd`).
It never names a processor: a TSC read admits RDTSC and not RDMSR with it,
and a late 486 has CPUID. `.asm` files take the same names. A 64-bit block
runs on a K8 or later and has all of it.

The compiler holds the block to that. It does not hold it to `--cpu`: the
emitted-code audit leaves the block to its own `.runtime`, as it leaves
`Sys.Cpuid` to the program's check. Nor does it hold the block to who owns
the x87 and SSE state: MMX or SSE in freestanding code, where a packed block
would save the thread's state first, is the block's to arrange, as it is an
`.asm` file's.

## The rules

Every path through the block is followed, and each rule holds on all of
them:

- **no jump out of the block**, and no `call`, `ret`, `iret`, `enter` or
  `leave`. Jumps go to the block's own labels. A block is no safepoint, and a
  call would be one;
- **ESP is left where the block found it**: `push` and `pop` pair up (on every
  path), and nothing else writes ESP. **EBP is never written**: the method's
  frame is reached through it -- `popa` only where `pusha`'s eight are on top
  of the stack, whole and in place, which puts EBP back. The stack is
  followed a byte at a time: a `pushad` may be taken back by sixteen 16-bit
  pops, but a `popad` two bytes off its frame is refused. A write through
  ESP (`mov dword [esp], 0x400`) makes nothing more known of what the block
  pushed. CS and SS are not loaded;
- **a register is set before it is read**: a block's registers hold nothing
  of the method's but what its own instructions put there, the head's
  bindings among them. Reading one the block has not set on every path is an
  error (`rep stosd` with EDI never set), except a register made from itself
  without its value (`xor eax, eax`, `sbb eax, eax`, `pxor mm0, mm0`), the part
  of a register a scalar or a half is written into (`cvtsi2sd xmm0, eax`,
  `movss xmm0, xmm1`), a push's register (saved to be put back), and what a
  trap or CPUID may read (`int`'s and `syscall`'s arguments, CPUID's subleaf
  in ECX). A register is followed by its parts -- bits 0-7, 8-15 (AH's),
  16-31 and 32-63 -- named or not: a byte's or a word's write sets that much
  and a byte's or a word's read needs that much set (`mov al, x` then `cbw`,
  `fnstsw ax` then `sahf`, `lahf` then `sahf`, `mov al, 0` then
  `repne scasb`, `mov al, x` then `mul bl`), and a 32-bit read of a register
  the block set only a byte of is refused, as is `mov al, 1` then `sahf`
  (AH) or a byte's `div` (which divides AX). A 32-bit write sets the whole
  register, in long mode too. Where one path sets a register whole and
  another only a byte of it, after them it is set a byte;
- **the x87 stack is as deep at the end as at the start**. `fincstp`,
  `fdecstp` and `ffree` are refused, since they move the top without its tags
  and the depth could not be followed, and so are `frstor` and `fldenv`,
  which load a state of unknown depth;
- **`emms` (or `femms`) after MMX**, before the end, and no x87 instruction
  between MMX and its `emms`. No MMX instruction runs with values on the x87
  stack: the MMX registers are the x87's;
- **the direction flag is clear at the end** (`cld` after `std`). `popf`
  puts DF back as its own `pushf` found it; a `popf` of anything else, or of
  flags written through ESP since, may set it;
- **the code does not depend on where it lands**: the block is assembled at
  two origins, and an absolute address of one of its labels, which moves
  with the method, is refused.

A local is never the operand of an instruction whose memory is larger than
any local (`fxsave`, `lgdt`, `fnsave`, `fnstenv`): give it an address in a
register. A block is at most 16000 bytes of text, the IR carrying it in a
name; a longer one belongs in an `.asm` file.

Every register the block writes, named (`mov ebx, 1`) or not (`cpuid`
writes EAX to EDX), is clobbered: the register allocator keeps nothing there
across the block, and a callee-saved one is saved by the prologue as for any
use. The block reads and writes memory as it likes, so the optimiser treats
it as it treats a call to an unknown function. A block that names no local
as memory, pushes nothing and names neither ESP nor EBP leaves its method
free to go without a frame pointer. A block with x87, MMX or SSE code counts
as float code in the facts the link reads about where float code runs
(X87Notes), and inside a packed block one that uses SSE has FXSAVE save
the thread's state.

## How a block meets its method

A block is optimised, not turned into a chain of moves:

- **the head's `mov reg, local`s are bindings, not instructions**: the local
  is put in the register before the block, a move the register allocator
  coalesces, so it is often already there and costs nothing;
- **the tail's `mov local, reg`s are bindings too**: the local is taken from
  the register after the block, so `mov a, eax` at the end of a block is the
  store of EAX to wherever `a` goes;
- **a local named anywhere else is a memory operand**: its frame slot, filled
  before the block where the block reads it and read back after it where the
  block writes it.

The head is the run of `mov reg, local` from the block's first statement,
each into a general register other than ESP and EBP, as wide as the local,
until anything else or a label. The tail is the run of `mov local, reg` back
from its last, each from a register written whole (a 32- or 64-bit one) into
a local not taken yet, with no label at or after it, so that every path to
the end runs them.

`Sys.Cpuid`'s hand-written selection is exactly what the CPUID example above
compiles to: the leaf straight into EAX, the subleaf into ECX, CPUID, and the
four results stored from the registers.

## In the compiler

| Where | What |
| --- | --- |
| `frontend/Lexer.cs` (`AsmBlock`) | the block's raw text, one token |
| `frontend/Parser.cs`, `syntax/AsmStmt.cs` | the statement and its names |
| `frontend/Binder.InlineAsm.cs` | names, shapes, constants, roles, definite assignment, and the block assembled once for its errors |
| `lowering/Lowering.InlineAsm.cs` | `__x86.i.asm:<spec>`, the inputs as operands, an `__x86.i.asmout` per output right after |
| `arch/x86/assembler/InlineAsm.cs` | reading, the instructions' facts, the rules, the bindings, the text assembled |
| `arch/x86/backend/Select.InlineAsm.cs`, `arch/x64/backend/Select.InlineAsm.cs` | the plan carried out: slots, bindings, `MOp.InlineAsm` |

The block's spec travels in the IR in the call's name, so a unit regenerated
at the link selects it as it did. The preinitialiser refuses a block: what it
does is the machine's alone.
