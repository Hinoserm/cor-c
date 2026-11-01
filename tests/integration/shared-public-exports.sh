#!/bin/sh
# A SHARED LIBRARY'S EXPORTS ARE ITS PUBLIC SURFACE (PublicApi), as a kernel's
# are: a public member of a public type in .dynsym, kept though nothing in
# the library calls it; an internal member, an internal type's public one and
# a private one not, but what its generic code names, which a program's copy
# of that code calls; a program importing one refused at its link, naming it.
# Its exports are found as a kernel's are: DT_GNU_HASH and DT_CORSAC_NAMES64.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
here=tests/integration/shared-public
mkdir -p build
work=$(mktemp -d "$root/build/shared-public-exports.XXXXXX")
fail() { printf 'FAIL %s\n' "$1"; exit 1; }
"$corc" compile --nostdlib --shared --obj $here/Library.cor -o "$work/library.o" 2> "$work/library.log" || { cat "$work/library.log"; fail "library object"; }
"$corc" link "$work/library.o" --shared --no-undefined -o "$work/libApi.so" 2> "$work/link.log" || { cat "$work/link.log"; fail "library link"; }
readelf --dyn-syms -W "$work/libApi.so" | awk '$7 != "UND" && $8 != "" { print $8 }' > "$work/exported"
grep -q '^m_Api_Answer_' "$work/exported" || fail "a public method is not exported"
grep -q '^m_Api_Never_' "$work/exported" || fail "a public method nothing calls is not exported"
if grep -q '^m_Api_Inner_' "$work/exported"; then fail "an internal method is exported"; fi
if grep -q '^m_Api_Seed_' "$work/exported"; then fail "a private method is exported"; fi
if grep -q '^m_Helper_Half_' "$work/exported"; then fail "an internal type's public method is exported"; fi
grep -q '^m_Api_Picked_' "$work/exported" || fail "a private method the library's generic code names is not exported for a program's copy"
grep -q '^s_Api__picks_' "$work/exported" || fail "a private static the library's generic code names is not exported for a program's copy"
# Pick's own copies, never Picked: a copy's name goes on past Pick otherwise than `ed`.
if grep -q '^m_Api_Pick[^e]' "$work/exported"; then fail "the library exported a copy of generic code it never made"; fi
readelf -d "$work/libApi.so" > "$work/dynamic"
grep -q 'GNU_HASH' "$work/dynamic" || fail "the library has no GNU hash"
grep -qi '0x60000c08' "$work/dynamic" || fail "the library has no names64 (DT_CORSAC_NAMES64)"
# Every export found as a loader finds it, through those tables.
python3 -I $here/lookup.py "$work/libApi.so" > "$work/lookup.out" 2>&1 || { cat "$work/lookup.out"; fail "an export is not found through the GNU hash and names64"; }
grep -q '^lookup ok [1-9]' "$work/lookup.out" || { cat "$work/lookup.out"; fail "the lookup found no export"; }
# A program calling the public surface: linked, and runs.
"$corc" compile --nostdlib --obj $here/Caller.cor --ref $here/Library.cor --link-shared "$work/libApi.so" -o "$work/caller.o" 2> "$work/caller.log" \
    || { cat "$work/caller.log"; fail "caller object"; }
"$corc" link "$work/caller.o" --link-shared "$work/libApi.so" --runpath "$work" -o "$work/caller" 2> "$work/caller-link.log" \
    || { cat "$work/caller-link.log"; fail "caller link"; }
status=0; "$work/caller" || status=$?
test "$status" = 42 || fail "the caller answered $status, not 42"
# Its copy of Pick its own, calling what the library exported for it.
readelf -sW "$work/caller" | awk '$7 != "UND" { print $8 }' | grep -q '^m_Api_Pick[^e]' || fail "the caller has no copy of Api.Pick of its own"
readelf --dyn-syms -W "$work/caller" | awk '$7 == "UND" { print $8 }' | grep -q '^m_Api_Picked_' || fail "the caller's copy of Pick does not call the library's Picked"
# A program reaching the library's internal type: refused, the name said --
# by its compile, which C# says may not name it (CS0122), or by its link,
# which finds it not exported.
if "$corc" compile --nostdlib --obj $here/Intruder.cor --ref $here/Library.cor --link-shared "$work/libApi.so" -o "$work/intruder.o" 2> "$work/intruder.log"; then
    if "$corc" link "$work/intruder.o" --link-shared "$work/libApi.so" --runpath "$work" -o "$work/intruder" 2> "$work/intruder-link.log"; then
        fail "a program importing an internal type's member linked"
    fi
    grep -q 'm_Helper_Half_' "$work/intruder-link.log" || { cat "$work/intruder-link.log"; fail "the link's refusal does not name the internal member"; }
else
    grep -q 'Helper' "$work/intruder.log" || { cat "$work/intruder.log"; fail "the compile's refusal does not name the internal type"; }
fi
echo "PASS a shared library exports its public surface alone, found by GNU hash and names64, and a program importing an internal member is refused: $work"
