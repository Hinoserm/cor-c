#!/bin/sh
# A STRUCT OF ANOTHER UNIT IN REGISTERS AFTER THE LINK'S INLINING
# (UnitBackend, SlotScalars.ApplyAndClean): PageWord is a one-word struct in
# its own file, Readable in another reads one through PageWord.At and asks
# its Present. In Readable's unit the two are calls, the struct's slot their
# argument, so the unit's own passes leave the slot; the link inlines them,
# and every use is then a load or a store of the slot. Unmade there, the
# word was zeroed, stored, copied and read back through two frame slots (the
# kernel's page-table walk, Arch.Readable, 140 bytes for 99). The link's
# regenerated Readable holds no slot at all.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/link-struct-registers.XXXXXX")
here=tests/integration/link-struct-registers
modules=tests/integration/modules
files="$modules/Host.cor $modules/Kernel.cor $modules/Loader.cor $here/PageWord.cor $here/Reader.cor"
fail() { printf 'FAIL %s\n' "$1"; exit 1; }

runtime=$("$corc" library-sources --freestanding)
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 $files $runtime -o "$work/kernel.idx" 2> "$work/index.log" \
    || { cat "$work/index.log"; fail "the index"; }
: > "$work/units.tsv"
for source in $files $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$work/$name.o" "$work/$name.o.deps" "$role" >> "$work/units.tsv"
done
"$corc" compile-project --units "$work/units.tsv" --nostdlib --obj --freestanding --ring 0 \
    --decl-index "$work/kernel.idx" --assembly Kernel 2> "$work/units.log" > /dev/null || { cat "$work/units.log"; fail "the units"; }
# shellcheck disable=SC2046
"$corc" link $(cut -f2 "$work/units.tsv" | tr '\n' ' ') -o "$work/kernel" --exports \
    --dump-function 'm_Program_Readable_1_V$NUInt_r$V$Bool' > "$work/link.log" 2>&1 || { cat "$work/link.log"; fail "the link"; }
awk '/^== regenerated/ { p = 1 } p { print } p && /^$/ { exit }' "$work/link.log" > "$work/regenerated.txt"
grep -q "function m_Program_Readable" "$work/regenerated.txt" || { cat "$work/link.log"; fail "the link regenerated no Readable"; }
if grep -q "slot" "$work/regenerated.txt"; then
    cat "$work/regenerated.txt"
    fail "PageWord stayed in the frame after the link inlined its methods"
fi
echo "PASS link-struct-registers"
