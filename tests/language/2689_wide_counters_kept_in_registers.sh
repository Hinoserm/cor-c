#!/usr/bin/env bash
#
# The half of test 2689 its output cannot show: the image, compiled for the
# Geode LX (the 486-class kernel's CPU). The route cache's 64-bit generation
# and the two counters of Twice are added to in registers -- no ADD or ADC
# of a frame slot -- and no constant with a zero low word is added to the
# low word at all, on either side of an addition or taken away (Halves).
# And the same source compiled for long mode, whose edges carry the
# allocator's loads as i386's do (X64Target, IPlacement.EdgeMoves), runs
# to .NET's answers.
#
#     tests/language/2689_wide_counters_kept_in_registers.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/wide-counters.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if $corc compile "$here/2689_wide_counters_kept_in_registers.cor" -o "$work/wide" --cpu=geode-lx --asm > "$work/wide.asm" 2> "$work/wide.log"
then pass "the program compiles"
else fail "the program compiles"; cat "$work/wide.log"; exit 1
fi

body() { awk "/^m_Tcp_$1.*:\$/,/^global/" "$work/wide.asm"; }

for f in RouteOf Twice; do
    if [ -n "$(body "$f")" ]; then pass "$f is in the image"; else fail "$f is in the image"; continue; fi
    if body "$f" | grep -qE '^\s+(add|adc|sub|sbb) dword \['; then
        fail "$f adds to no frame slot"; body "$f" | grep -E '^\s+(add|adc|sub|sbb) dword \['
    else pass "$f adds to no frame slot"; fi
done
for f in Twice Halves; do
    if body "$f" | grep -qE '^\s+(add|sub) e[a-z]{2}, 0$'; then fail "$f adds no zero low word"; else pass "$f adds no zero low word"; fi
done

expected="$(sed -n '/^\/\/ expect-output:/,/^\/\/$/p' "$here/2689_wide_counters_kept_in_registers.cor" | sed '1d;$d' | sed 's|^// ||')"
if $corc compile "$here/2689_wide_counters_kept_in_registers.cor" -o "$work/wide64" --target=x86-64 > "$work/wide64.log" 2>&1
then pass "the program compiles for long mode"
    if [ "$("$work/wide64")" = "$expected" ]; then pass "long mode answers as .NET"; else fail "long mode answers as .NET"; fi
else fail "the program compiles for long mode"; cat "$work/wide64.log"
fi

[ "$failures" -eq 0 ]
