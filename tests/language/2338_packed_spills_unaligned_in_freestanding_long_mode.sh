#!/usr/bin/env bash
#
# The half of test 2338 its exit cannot show: how long mode moves a packed
# value's spill slot. Hosted, by MOVAPS and never MOVUPS; freestanding,
# the other way round (FamilyPasses.AlignedSpills) -- in the listing, in
# the object's bytes, and in the word of the object's contract that a
# unit made again at the link is compiled by.
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
    # THE OBJECT, as it is encoded -- and its contract's last word, which is
    # what a unit made again at the link is compiled by (UnitBackend reads
    # it in another process: the compile's own switches are not there).
    object="$work/rows-$way.o"
    if $corc compile "$here/2338_packed_spills_unaligned_in_freestanding_long_mode.cor" --obj --target x86-64 --cpu=athlon64 $flags -o "$object" > "$object.log" 2>&1
    then pass "$way: the object is made"
    else fail "$way: the object is made"; head -5 "$object.log"; continue
    fi
    objcopy --dump-section .corsac.cpu="$object.cpu" "$object" "$object.copy" 2> /dev/null
    word=$(tail -c 1 "$object.cpu" 2> /dev/null)
    wanted=1; [ $way = freestanding ] && wanted=0
    if [ "$word" = "$wanted" ]; then pass "$way: the object's contract says so ($wanted)"; else fail "$way: the object's contract says so (wanted $wanted, has '$word')"; fi
    at=$(nm "$object" | awk 'index($0, " m_Program_Rows_") { print $1; exit }')
    size=$(nm -S "$object" | awk 'index($0, " m_Program_Rows_") { print $2; exit }')
    encoded=$(objdump -d --no-show-raw-insn -M intel "$object" --start-address=0x$at --stop-address=$(printf '0x%x' $((0x$at + 0x$size))) 2>/dev/null)
    aligned=$(echo "$encoded" | grep -cE 'movaps +(XMMWORD PTR \[rbp-0x[0-9a-f]+\],xmm[0-9]+|xmm[0-9]+,XMMWORD PTR \[rbp-0x[0-9a-f]+\])')
    unaligned=$(echo "$encoded" | grep -cE 'movups +(XMMWORD PTR \[rbp-0x[0-9a-f]+\],xmm[0-9]+|xmm[0-9]+,XMMWORD PTR \[rbp-0x[0-9a-f]+\])')
    if [ $way = hosted ]; then
        if [ "$aligned" -ge 4 ] && [ "$unaligned" -eq 0 ]; then pass "$way: the encoded slot moves are MOVAPS"; else fail "$way: the encoded slot moves are MOVAPS ($aligned aligned, $unaligned not)"; fi
    else
        if [ "$unaligned" -ge 4 ] && [ "$aligned" -eq 0 ]; then pass "$way: the encoded slot moves are MOVUPS"; else fail "$way: the encoded slot moves are MOVUPS ($aligned aligned, $unaligned not)"; fi
    fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
