#!/bin/sh
# WHAT A MODULE ASSUMES OF ITS KERNEL IS A NAME THE KERNEL SAYS
# (Lto.KernelAssumptions). Addr is a struct of the kernel's with a static of
# its own, an operator and two getters; Card, a module, adds to one and
# splits it into two words in a method inlined into its caller.
#
#   * The kernel's link says the types it initialised (`y_init$...`) and the
#     small bodies it offers (`y_body$<hash>$...`), the bodies beside it.
#   * The module's code does not test Addr's state word, calls none of the
#     three members and allocates nothing for the struct it passes by value
#     (Inline carries Instr.PassedByValue) -- and its link leaves the names
#     of what it relied on among its imports, so a kernel that does not say
#     them refuses it.
#   * A kernel whose operator has another body says another name: the
#     module built against the first imports a name the second has not.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/module-assumptions.XXXXXX")
here=tests/integration/module-assumptions
modules=tests/integration/modules
fail() { printf 'FAIL %s\n' "$1"; exit 1; }

runtime=$("$corc" library-sources --freestanding)
kernel() {
    out=$1; addr=$2
    mkdir -p "$out"
    files="$modules/Host.cor $modules/Kernel.cor $modules/Loader.cor $addr"
    # shellcheck disable=SC2086
    "$corc" index --assembly Kernel --ring 0 $files $runtime -o "$out/kernel.idx" 2> "$out/index.log" || { cat "$out/index.log"; fail "the index"; }
    : > "$out/units.tsv"
    for source in $files $runtime; do
        role=lib; case $source in */Kernel.cor) role=entry ;; esac
        name=$(basename "$source" .cor)
        printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$out/$name.o" "$out/$name.o.deps" "$role" >> "$out/units.tsv"
    done
    "$corc" compile-project --units "$out/units.tsv" --nostdlib --obj --freestanding --ring 0 \
        --decl-index "$out/kernel.idx" --assembly Kernel 2> "$out/units.log" > /dev/null || { cat "$out/units.log"; fail "the kernel's units"; }
    # shellcheck disable=SC2046
    "$corc" link $(cut -f2 "$out/units.tsv" | tr '\n' ' ') -o "$out/kernel" --exports > "$out/link.log" 2>&1 || { cat "$out/link.log"; fail "the kernel's link"; }
}
# The names a linked image leaves undefined (a module's imports), and those a kernel exports.
imports() { readelf --dyn-syms -W "$1" | awk '$7 == "UND" { print $8 }'; }
says() { python3 "$modules/exports.py" "$1" | cut -d" " -f1; }

a=$work/a
kernel "$a" "$here/Addr.cor"
grep -q "says [1-9][0-9]* types initialised by the link and [1-9][0-9]* bodies" "$a/link.log" || { cat "$a/link.log"; fail "the kernel's link said nothing a module may assume"; }
[ -s "$a/kernel.bodies" ] || fail "no bodies beside the kernel"

"$corc" compile --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    "$here/Card.cor" --obj -o "$work/card.o" 2> "$work/card.log" || { cat "$work/card.log"; fail "the module's unit"; }
"$corc" link "$work/card.o" --kernel "$a/kernel" -o "$work/card.ko" > "$work/card-link.log" 2>&1 || { cat "$work/card-link.log"; fail "the module's link"; }
imports "$work/card.ko" > "$work/imports.txt"

ready='s_Kernel$002eAddr_StaticReady$_t$V$NInt'
grep -qxF "y_init\$$ready" "$work/imports.txt" || { cat "$work/imports.txt"; fail "the module does not say it relied on Addr made by the link"; }
if grep -qxF "$ready" "$work/imports.txt"; then fail "the module still tests Addr's state word"; fi
for member in get_Low32 get_High32 op_Addition; do
    grep -q "^y_body\\\$[0-9a-f]\{16\}\\\$m_Kernel\\\$Addr_$member" "$work/imports.txt" || { cat "$work/imports.txt"; fail "the module does not name the body of Addr.$member it took"; }
    if grep -q "^m_Kernel\\\$Addr_$member" "$work/imports.txt"; then fail "the module still calls Addr.$member"; fi
done
# Recycle: nothing called, nothing allocated -- Arm inlined, its struct in registers.
objdump -d "$work/card.ko" | awk '/^[0-9a-f]+ <m_Card_Recycle[^>]*>:$/ { p = 1; next } p && /^$/ { exit } p { print }' > "$work/recycle.txt"
[ -s "$work/recycle.txt" ] || fail "no Card.Recycle in the module"
if grep -q "call" "$work/recycle.txt"; then cat "$work/recycle.txt"; fail "Card.Recycle calls something (the struct passed by value allocated, or a member not inlined)"; fi

# Every name it assumed is one kernel a says.
says "$a/kernel" > "$work/a.exports"
grep "^y_" "$work/imports.txt" | while read -r name; do
    grep -qxF "$name" "$work/a.exports" || fail "kernel a does not say $name"
done

# KERNEL B: the same but for the operator's body. Its name for it is another,
# and the module built against a imports one b does not say.
sed 's|a._bits + offset|a._bits + offset + 1|' "$here/Addr.cor" > "$work/Addr.cor"
b=$work/b
kernel "$b" "$work/Addr.cor"
says "$b/kernel" > "$work/b.exports"
plus=$(grep '^y_body.*Addr_op_Addition' "$work/imports.txt")
if grep -qxF "$plus" "$work/b.exports"; then fail "kernel b, whose operator differs, says the same name for its body"; fi
grep -q 'Addr_op_Addition' "$work/b.exports" || fail "kernel b does not export the operator at all"
low=$(grep '^y_body.*Addr_get_Low32' "$work/imports.txt")
grep -qxF "$low" "$work/b.exports" || fail "kernel b, whose getter is the same, says another name for it"
# And the module's units, linked against b, take b's body under b's name.
"$corc" link "$work/card.o" --kernel "$b/kernel" -o "$work/card-b.ko" > "$work/card-b-link.log" 2>&1 || { cat "$work/card-b-link.log"; fail "the module's link against kernel b"; }
imports "$work/card-b.ko" | grep '^y_body.*Addr_op_Addition' > "$work/plus-b.txt" || fail "linked against b, the module names no body of the operator"
if grep -qxF "$plus" "$work/plus-b.txt"; then fail "linked against b, the module still names a's body"; fi
echo "PASS module-assumptions"
