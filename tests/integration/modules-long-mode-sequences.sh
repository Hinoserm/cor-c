#!/bin/sh
# A LONG-MODE KERNEL'S MODULE IN ITS STORE SCHEME (--store-sequences): the
# kernel lays the four store stubs out once, between __corsac_store_sequences
# and its end, and exports them; its module (--kernel, so --shared) makes
# none and calls the kernel's through their GOT slots -- call [rip +
# stub@GOTPCREL], R_X86_64_GOTPCRELX -- which its link binds as GLOB_DAT
# against the kernel's export table. So every reference store of the module
# runs inside the kernel's range, which the kernel's handshake stops no
# thread inside (X64Backend.EmitStoreSequences, Selector.SequenceStub).
# tests/integration/modules.sh's kernel and Sb16 module, built for x86-64.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/modules-long-mode-sequences.XXXXXX")
here=tests/integration/modules
flags="--target x86-64 --store-sequences"

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

runtime=$("$corc" library-sources --freestanding)
# shellcheck disable=SC2086
"$corc" index --target x86-64 --assembly Kernel --ring 0 $here/Host.cor $here/Kernel.cor $here/Loader.cor $runtime \
    -o "$work/kernel.idx" 2> "$work/index.log" || { cat "$work/index.log"; fail "kernel index"; }
: > "$work/units.tsv"
for source in $here/Host.cor $here/Kernel.cor $here/Loader.cor $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$work/$name.o" "$work/$name.o.deps" "$role" >> "$work/units.tsv"
done
# shellcheck disable=SC2086
"$corc" compile-project $flags --units "$work/units.tsv" --nostdlib --obj --freestanding --ring 0 \
    --decl-index "$work/kernel.idx" --assembly Kernel 2> "$work/units.log" || { tail -n 30 "$work/units.log"; fail "kernel units"; }
objects=$(cut -f2 "$work/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects -o "$work/kernel" --exports 2> "$work/link.log" || { cat "$work/link.log"; fail "kernel link"; }

# The kernel: its range and its four stubs, exported.
python3 -I "$root/$here/exports.py" "$work/kernel" > "$work/exports.syms"
for name in __corsac_store_sequences __corsac_store_sequences_end __corsac_refstore __corsac_cardstore __corsac_refxchg __corsac_refcas; do
    awk -v n="$name" '$1 == n { found = 1 } END { exit !found }' "$work/exports.syms" || fail "the kernel does not export $name"
done

# shellcheck disable=SC2086
"$corc" compile $flags --kernel "$work/kernel" --decl-index "$work/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj -o "$work/sb16.o" 2> "$work/sb16.log" || { tail -n 30 "$work/sb16.log"; fail "module object"; }
"$corc" link "$work/sb16.o" --kernel "$work/kernel" -o "$work/sb16.ko" 2> "$work/sb16-link.log" \
    || { cat "$work/sb16-link.log"; fail "module link"; }

# The module: no stub and no range of its own; its stores' calls GOTPCRELX
# against the kernel's stubs, and nothing else reaches them.
readelf -sW "$work/sb16.o" | awk '$7 != "UND" && $8 ~ /^__corsac_(refstore|cardstore|refxchg|refcas|store_sequences)/ { bad = 1 } END { exit bad }' \
    || fail "the module defines a store stub of its own"
readelf -rW "$work/sb16.o" > "$work/sb16.relocs"
calls=$(awk '$5 ~ /^__corsac_(refstore|cardstore|refxchg|refcas)$/ && $3 == "R_X86_64_GOTPCRELX" { n++ } END { print n + 0 }' "$work/sb16.relocs")
other=$(awk '$5 ~ /^__corsac_(refstore|cardstore|refxchg|refcas)$/ && $3 != "R_X86_64_GOTPCRELX" { n++ } END { print n + 0 }' "$work/sb16.relocs")
test "$calls" -gt 0 || fail "the module makes no reference store through the kernel's stubs"
test "$other" = 0 || fail "the module reaches a stub other than through its GOT slot ($other)"
# Its link: each stub's slot bound by the loader, and every call through one.
readelf -rW "$work/sb16.ko" > "$work/ko.relocs"
grep -q 'R_X86_64_GLOB_DAT .* __corsac_refstore' "$work/ko.relocs" || fail "the module's link binds no slot for __corsac_refstore"
through=$(objdump -d --no-show-raw-insn "$work/sb16.ko" | grep -c 'call   \*0x' || true)
test "$through" -ge "$calls" || fail "the module's link has $through calls through a slot for $calls stub calls"
echo "PASS modules-long-mode-sequences ($calls stub calls through the kernel's GOT slots)"
