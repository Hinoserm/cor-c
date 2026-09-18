# corc compiled by corc

Where the self-hosting work stands, how to pick it up, and what it found.

## Current benchmark workflow

The post-extraction attempt at source snapshot 3e37787 stopped during bootstrap
binding on missing Path.Combine(params string[]) support. Eight calls with five
or six path segments were rejected. That failed .NET-hosted bootstrap pass took
4.50 seconds with a 308800 KiB peak RSS; these are not native self-compilation
results. Logs remain under build/selfcompile.HzVP0J. The API repair and its
standard-behavior fixture are tracked in TODO.md.

Use `bash tests/benchmarks/selfcompile.sh` after building the .NET bootstrap, or
set `CORC` to a preserved bootstrap executable. `SELF_REVISION` selects a
committed source snapshot (default HEAD); `SELF_PROFILE` is `default`, `size`,
`batch` or `batch-size`. Every run creates a fresh `build/selfcompile.*`
directory, records input hashes, and retains all logs/artifacts on failure.

The bootstrap creates a native Linux i386 stage-one compiler. That compiler
must build and run a smoke program before its full self-compilation is timed.
Stage two must also build/run the smoke program with identical output bytes;
compiler-generation byte parity is reported separately. Bootstrap time is
preparation, not a native compiler benchmark. Native counters report GC work
and last-collection live storage, not total allocated bytes or pause time.

Current-source first attempt: `build/selfcompile.2dwsre`, source revision
9431b76b, 98 compiler files. Bootstrap stopped on an optional local-function
parameter in CarryRecognition.cs. No native self-compilation timing exists
for that attempt. The old selfhost branch's unique commit is patch-equivalent
to main (`git cherry main refs/heads/selfhost` reports `-`); do not restore the
old branch to work around current language/compiler gaps.

Follow-up snapshot e7916c98 (99 compiler files) gets past that parse error.
Local defaults now work through direct/captured calls and preserve named
argument evaluation order; tests/lang/optimizer_local_defaults.cor passes.
The next bootstrap stops during type checking, with its complete diagnostics
retained in build/selfcompile.0N55Ho/bootstrap.log. Remaining categories are
target-typed construction contexts, primitive CompareTo, comparer-taking
ToDictionary, integral Sum inference, lambda declaration spaces, heterogeneous
object switch arms and TextWriter.Write. No native timing is available yet.

## Historical handoff status

The following describes the earlier self-hosting development snapshot, not
the status of current main or a completed current-source benchmark.

`corc`, compiled by `corc`, checks, lowers, optimises and links a program over
the whole standard library and writes **byte for byte** the executable the
dotnet-hosted compiler writes. Given its OWN source it runs for about two and
a half minutes and then faults; the last fault found (a null array wrapped as
a list) is fixed and the run has not been repeated since. The next fault, if
there is one, is the next piece of work. The goal is two generations that
agree: the compiler built by dotnet-corc (stage 1) and the compiler built by
stage 1 (stage 2) should be the same file.

This branch is NOT on main. Before it lands: the language suite (249 pass; 191,
271 and 272 need disc images a fresh worktree does not have), then the
libraries, the userland and the kernel built with it, then the VM harnesses.
`lib/std.cor` changed in ways every program sees (below), so the harnesses are
not optional.

## Building and running it

    libs=$(sed -n 's/^libs="${CORC_LIBS:-\(.*\)}"/\1/p' tests/lang/run.sh)
    srcs=$(find compiler -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' \
               -not -path '*/Legacy/*' -not -path 'compiler/tests/*' | sort)
    dotnet compiler/bin/Release/net10.0/corc.dll compile -Wno-error $libs $srcs -o corc.self
    ./corc.self compile --nostdlib -Wno-error $libs program.cor -o program
    ./corc.self compile --nostdlib -Wno-error $libs $srcs -o corc.stage2    # the proof

Thirty seconds to build. `--nostdlib` plus the libraries by name, because only
the dotnet build carries the class library inside it. Delete the old
`corc.self` first: a failed compile leaves it there and it is easy to test the
wrong binary.

## Finding the next one

1. `gdb -q -batch -ex run -ex 'bt 16' --args ./corc.self ...`; the symbols name
   the method. A managed exception prints its own trace with line numbers.
2. REPRODUCE IT in ten or twenty lines of `.cor` before touching anything.
   Every fault so far was an ordinary miscompile or a place the library
   departs from .NET, never something peculiar to the compiler.
3. When both compilers run but disagree, diff their `--dump-ir`, then
   `--dump-opt`, then `--trace-opt <function>`, which prints one function after
   every optimiser pass: the first pass after which they differ is the one.
4. Fix it to match .NET, add a `tests/lang/6NN` test, commit it alone.

## What it found

Code generation: an array arm of a conditional typed as an interface was not
wrapped; two `Equals` shared one vtable slot; `object.Equals(a, b)` was
reference equality; `continue` inside a `switch` was `break`; a chained
constructor was chosen by arity; a base constructor was never called
implicitly and initialisers were repeated, and also put into `: this(...)`
constructors where they undid the chained one's work; `a?.B.C` was not
short-circuited; `<`, `>`, `<=`, `>=` were not lifted over nullable values; a
local function written after `return` had no closure; a null array converted
to an interface became a non-null wrapper; an enum argument bound to
`Append(int)` rather than `Append(object)`.

Library: `==` on strings faulted on null and made null equal to ""; tables
never reclaimed erased slots (an endless probe) and wrote duplicate keys past
one; every key that was not a string was compared by identity, in tables and
in `Contains`/`IndexOf`; the default comparer ordered tuples by address;
tables enumerated in hash order rather than insertion order.

The mechanism added for the key work: three slots every class shares, for
`Equals(object)`, `GetHashCode()` and `CompareTo`, with tuples given all three
field by field, a guard in front of a typed `Equals(T)`, and
`Sys.IsObject`/`KeyEquals`/`KeyHash`/`KeyCompare` for the library to reach them.

## Known and not fixed

Lifted ARITHMETIC on nullable values is refused by the checker; a generic type
spelt in full (`System.Collections.Generic.List<string>`) as a field type does
not specialise; `new object()` is refused; records get no synthesized value
equality; iterators are not lazy state machines.
