#!/usr/bin/env bash
# A LINK WHOSE INPUTS SAY NOTHING NEW WRITES NOTHING (LinkReceipt): a module
# linked again against a kernel linked again, whose exports the module
# imports are the same, is current and its image untouched; a module whose
# own object changed links again; every input and the output said.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/link-receipts.XXXXXX")
fail() { printf 'FAIL %s\n' "$1"; exit 1; }
cp tests/integration/modules/Host.cor tests/integration/modules/Kernel.cor tests/integration/modules/Loader.cor tests/integration/modules/Sb16.cor "$work/"
k=$work/k
mkdir -p "$k"
runtime=$("$corc" library-sources --freestanding)
kernel() {
    # shellcheck disable=SC2086
    "$corc" index --assembly Kernel --ring 0 "$work/Host.cor" "$work/Kernel.cor" "$work/Loader.cor" $runtime -o "$k/kernel.idx" 2> "$k/index.log"
    : > "$k/units.tsv"
    for source in "$work/Host.cor" "$work/Kernel.cor" "$work/Loader.cor" $runtime; do
        role=lib; case $source in */Kernel.cor) role=entry ;; esac
        printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$k/$(basename "$source" .cor).o" "$k/$(basename "$source" .cor).o.deps" "$role" >> "$k/units.tsv"
    done
    "$corc" compile-project --units "$k/units.tsv" --nostdlib --obj --freestanding --ring 0 --decl-index "$k/kernel.idx" --assembly Kernel \
        2> "$k/units.log" || { cat "$k/units.log"; fail "kernel units"; }
    # shellcheck disable=SC2086
    "$corc" link $(cut -f2 "$k/units.tsv" | tr '\n' ' ') -o "$k/kernel" --exports 2> "$1" || { cat "$1"; fail "kernel link"; }
}
module() {
    "$corc" compile --kernel "$k/kernel" --decl-index "$k/kernel.idx" --assembly Kernel --ring 0 "$work/Sb16.cor" --obj -o "$work/sb16.o" \
        2> "$work/sb16.log" || { cat "$work/sb16.log"; fail "module object"; }
    "$corc" link "$work/sb16.o" --kernel "$k/kernel" -o "$work/sb16.ko" 2> "$1" || { cat "$1"; fail "module link"; }
}
kernel "$work/kernel-1.log"
grep -q '^link input: .*Kernel.o (' "$work/kernel-1.log" || fail "the kernel's link said no input"
grep -q '^link output: .*/kernel (' "$work/kernel-1.log" || fail "the kernel's link said no output"
module "$work/module-1.log"
grep -q '^link output: .*sb16.ko (' "$work/module-1.log" || fail "the module's first link wrote nothing"
before=$(stat -c '%Y.%s' "$work/sb16.ko")
# The same link again: current.
"$corc" link "$work/sb16.o" --kernel "$k/kernel" -o "$work/sb16.ko" 2> "$work/module-2.log"
grep -q '^link current: .*sb16.ko' "$work/module-2.log" || { cat "$work/module-2.log"; fail "the same module link was not current"; }
# THE KERNEL LINKED AGAIN, a body changed and no export: the module, its object
# unchanged, is current against it and its image untouched.
sleep 1
sed -i 's/public static int Twice(int value) => value \* 2 + Log.Lines;/public static int Twice(int value) => value * 2 + Log.Lines + 0 * value;/' "$work/Kernel.cor"
kernel "$work/kernel-2.log"
grep -q '^link output: .*/kernel (' "$work/kernel-2.log" || fail "the kernel with a body changed was not linked again"
"$corc" link "$work/sb16.o" --kernel "$k/kernel" -o "$work/sb16.ko" 2> "$work/module-3.log"
grep -q '^link current: .*sb16.ko' "$work/module-3.log" || { cat "$work/module-3.log"; fail "the module was linked again for a kernel body"; }
test "$(stat -c '%Y.%s' "$work/sb16.ko")" = "$before" || fail "the current module's image was written"
# ITS OWN OBJECT CHANGED: linked again.
sed -i 's/"sb16 at "/"sb16 found at "/' "$work/Sb16.cor"
module "$work/module-4.log"
grep -q '^link output: .*sb16.ko (' "$work/module-4.log" || { cat "$work/module-4.log"; fail "the module with its own source changed was not linked again"; }
echo "PASS a module is linked again for its own objects and the names it imports, not for every kernel link: $work"
