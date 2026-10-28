#!/bin/sh
# --count-spills (SpillCount): a program built with it says, at its end, how
# many times its blocks read and wrote the register allocator's spill slots
# -- on standard error, its own output unchanged -- and one built without it
# says nothing. The counters are volatile statics nothing in the program's
# IR writes, which UnwrittenStatics must not take for zero.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/count-spills.XXXXXX")
fail() { printf 'FAIL %s\n' "$1"; exit 1; }
src=tests/benchmarks/arithmetic.cor
"$corc" compile "$src" -o "$work/plain" > "$work/plain.log" 2>&1 || { cat "$work/plain.log"; fail "plain compile"; }
"$corc" compile "$src" -o "$work/count" --count-spills > "$work/count.log" 2>&1 || { cat "$work/count.log"; fail "counting compile"; }
"$work/plain" > "$work/plain.out" 2> "$work/plain.err" || fail "plain run"
"$work/count" > "$work/count.out" 2> "$work/count.err" || fail "counting run"
sed 's/ticks=[0-9]*,//' "$work/plain.out" > "$work/plain.cmp"
sed 's/ticks=[0-9]*,//' "$work/count.out" > "$work/count.cmp"
cmp -s "$work/plain.cmp" "$work/count.cmp" || fail "counting changed the program's output"
if grep -q '^spills:' "$work/plain.err"; then fail "a program built without --count-spills says spill counts"; fi
grep -Eq '^spills: reads [1-9][0-9]*$' "$work/count.err" || { cat "$work/count.err"; fail "no spill reads said"; }
grep -Eq '^spills: writes [0-9]+$' "$work/count.err" || { cat "$work/count.err"; fail "no spill writes said"; }
echo "PASS count-spills"
