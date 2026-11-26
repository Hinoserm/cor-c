#!/usr/bin/env bash
#
# The half of test 2338 its exit cannot show: how long mode moves a packed
# value's spill slot. Hosted, by MOVAPS and never MOVUPS; freestanding,
# the other way round (FamilyPasses.AlignedSpills).
#
#     tests/language/2338_packed_spills_unaligned_in_freestanding_long_mode.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/packed-spills.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

# Rows' lines in a listing: from its label to the blank line after it.
rows() { awk 'index($0, "m_Program_Rows_") == 1 && /:$/ { on = 1; next } on && /^$/ { exit } on { sub(/^ +/, ""); print }' "$1"; }

for way in hosted freestanding; do
    flags=""; [ $way = freestanding ] && flags="--freestanding --ring 0"
    listing="$work/rows-$way.s"
    if $corc compile "$here/2338_packed_spills_unaligned_in_freestanding_long_mode.cor" --asm --target x86-64 --cpu=athlon64 $flags > "$listing" 2> "$listing.err" && [ -s "$listing" ]
    then pass "$way: the listing is printed"
    else fail "$way: the listing is printed"; head -5 "$listing.err"; continue
    fi
    code=$(rows "$listing")
    aligned=$(echo "$code" | grep -cE '^movaps (\[rbp-[0-9]+\], xmm[0-9]+|xmm[0-9]+, \[rbp-[0-9]+\])$')
    unaligned=$(echo "$code" | grep -cE '^movups (\[rbp-[0-9]+\], xmm[0-9]+|xmm[0-9]+, \[rbp-[0-9]+\])$')
    if [ $((aligned + unaligned)) -ge 4 ]; then pass "$way: Rows spills its two values across the call"; else fail "$way: Rows spills its two values across the call ($aligned aligned, $unaligned not)"; echo "$code"; fi
    if [ $way = hosted ]; then
        if [ "$aligned" -ge 4 ] && [ "$unaligned" -eq 0 ]; then pass "$way: every slot move is MOVAPS"; else fail "$way: every slot move is MOVAPS ($aligned aligned, $unaligned not)"; echo "$code"; fi
    else
        if [ "$unaligned" -ge 4 ] && [ "$aligned" -eq 0 ]; then pass "$way: every slot move is MOVUPS"; else fail "$way: every slot move is MOVUPS ($aligned aligned, $unaligned not)"; echo "$code"; fi
    fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
