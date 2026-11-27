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
# Its link: the slot of every stub the module calls bound by the loader
# (GLOB_DAT against the stub's name), and each stub call -- counted by its
# target, a slot, so no virtual call through a register is taken for one --
# a call through one of those slots.
readelf -rW "$work/sb16.ko" > "$work/ko.relocs"
awk '$5 ~ /^__corsac_(refstore|cardstore|refxchg|refcas)$/ { print $5 }' "$work/sb16.relocs" | sort -u > "$work/stubs.used"
awk '$3 == "R_X86_64_GLOB_DAT" && $5 ~ /^__corsac_(refstore|cardstore|refxchg|refcas)$/ { print $5, $1 }' "$work/ko.relocs" | sort -u > "$work/stubs.slots"
while read -r stub; do
    awk -v n="$stub" '$1 == n { found = 1 } END { exit !found }' "$work/stubs.slots" || fail "the module's link binds no slot for $stub"
done < "$work/stubs.used"
objdump -d "$work/sb16.ko" > "$work/ko.dis"
through=$(awk 'NR == FNR { slot[sprintf("%x", strtonum("0x" $2))] = 1; next }
    /\tff 15 / && /call/ { if (match($0, /# [0-9a-f]+/)) { at = substr($0, RSTART + 2, RLENGTH - 2); sub(/^0+/, "", at); if (at in slot) n++ } }
    END { print n + 0 }' "$work/stubs.slots" "$work/ko.dis")
test "$through" = "$calls" || fail "the module's link calls through a stub's slot $through times for $calls stub calls"

# RELOCATED TEXT (--relocated-text): the same module with no GOT. Every
# kernel name in its text is reached where it is -- a store stub's call
# `call rel32` straight into the kernel's range -- each an R_X86_64_PC32 the
# binder fixes (S + A - P), its place left zero and its addend in the entry;
# DT_TEXTREL said; no GOT slot and no PLT entry; addresses in data as before.
mkdir -p "$work/relocated"
# shellcheck disable=SC2086
"$corc" compile $flags --kernel "$work/kernel" --decl-index "$work/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj --relocated-text -o "$work/relocated/sb16.o" 2> "$work/relocated/sb16.log" || { tail -n 30 "$work/relocated/sb16.log"; fail "relocated module object"; }
"$corc" link "$work/relocated/sb16.o" --kernel "$work/kernel" --relocated-text -o "$work/relocated/sb16.ko" 2> "$work/relocated/link.log" \
    || { cat "$work/relocated/link.log"; fail "relocated module link"; }
readelf -dW "$work/relocated/sb16.ko" | grep -q TEXTREL || fail "the relocated module does not say it relocates its text"
readelf -rW "$work/relocated/sb16.ko" > "$work/relocated/ko.relocs"
kinds=$(awk '$3 ~ /^R_X86_64_/ { print $3 }' "$work/relocated/ko.relocs" | sort -u | tr '\n' ' ')
for kind in $kinds; do
    case $kind in
        R_X86_64_PC32|R_X86_64_64|R_X86_64_RELATIVE) ;;
        *) fail "the relocated module asks its binder for $kind" ;;
    esac
done
case " $kinds" in *" R_X86_64_PC32 "*) ;; *) fail "the relocated module reaches the kernel through no PC32 relocation" ;; esac
# Each PC32 in the text: its place zero; a store stub's a `call rel32`
# (E8) with the addend -4 -- straight into the kernel's range.
python3 -I - "$work/relocated/sb16.ko" "$work/relocated/ko.relocs" > "$work/relocated/check.out" <<'PY' || { cat "$work/relocated/check.out"; fail "the relocated module's PC32 places"; }
import sys
image = open(sys.argv[1], 'rb').read()
import struct
# The file offset of an address, through the PT_LOADs.
phoff, = struct.unpack_from('<Q', image, 0x20); phentsize, phnum = struct.unpack_from('<HH', image, 0x36)
loads = []
for i in range(phnum):
    kind, flags, offset, vaddr, paddr, filesz = struct.unpack_from('<IIQQQQ', image, phoff + i * phentsize)
    if kind == 1: loads.append((vaddr, offset, filesz))
def at(address):
    for vaddr, offset, filesz in loads:
        if vaddr <= address < vaddr + filesz: return offset + address - vaddr
    raise SystemExit('0x%x is in no loaded segment' % address)
stubs = {'__corsac_refstore', '__corsac_cardstore', '__corsac_refxchg', '__corsac_refcas'}
pc32 = calls = 0
for line in open(sys.argv[2]):
    f = line.split()
    if len(f) < 3 or f[2] != 'R_X86_64_PC32': continue
    pc32 += 1
    place = at(int(f[0], 16))
    if image[place:place + 4] != b'\0\0\0\0': raise SystemExit('PC32 place %s is not zero' % f[0])
    name = f[4] if len(f) > 4 else ''
    if name in stubs:
        addend = line.split(name)[1].strip()
        if image[place - 1] != 0xE8 or addend not in ('- 4', '-4'): raise SystemExit('stub %s at %s: not call rel32 with addend -4 (%r)' % (name, f[0], addend))
        calls += 1
print(pc32, calls)
PY
read -r pc32 direct < "$work/relocated/check.out"
test "$direct" -gt 0 || fail "the relocated module makes no direct call to a kernel store stub"
tab=$(printf '\t')
if objdump -d "$work/relocated/sb16.ko" | grep -Eq "${tab}(ff 15|ff 25) "; then fail "the relocated module still calls through a slot"; fi
echo "PASS modules-long-mode-sequences ($calls stub calls through the kernel's GOT slots; relocated text: $pc32 PC32, $direct direct stub calls)"
