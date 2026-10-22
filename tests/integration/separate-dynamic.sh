#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
# Linking is a command of the one toolchain executable.
corlink() { "${CORLINK:-$corc}" link "$@"; }
mkdir -p build
work=$(mktemp -d "$root/build/separate-dynamic.XXXXXX")
"$corc" compile --nostdlib --shared --obj tests/integration/ir-lto/Compute.cor -o "$work/compute.o"
"$corc" compile --nostdlib --shared --obj --no-shared-init tests/integration/ir-lto/SharedData.cor -o "$work/shared-data.o"
corlink "$work/compute.o" "$work/shared-data.o" --shared --no-undefined -o "$work/libCompute.so"
readelf -h "$work/compute.o" > "$work/object.header"
grep -q 'REL (Relocatable file)' "$work/object.header"
readelf -d "$work/libCompute.so" > "$work/library.dynamic"
grep -q 'Library soname: \[libCompute.so\]' "$work/library.dynamic"
test "$(readelf --dyn-syms -W "$work/libCompute.so" | awk '$8 == "__corsac_init" && $7 != "UND" { count++ } END { print count+0 }')" = 1
if grep -q TEXTREL "$work/library.dynamic"; then
    echo 'FAIL separate PIC library has text relocations' >&2
    exit 1
fi
if corlink "$work/compute.o" --shared --flat -o "$work/invalid.so" > "$work/invalid.log" 2>&1; then
    echo 'FAIL shared flat output accepted' >&2
    exit 1
fi
test ! -e "$work/invalid.so"
"$corc" compile --nostdlib --obj tests/integration/ir-lto/Caller.cor \
    --ref tests/integration/ir-lto/Compute.cor --link-shared "$work/libCompute.so" -o "$work/caller.o"
if corlink "$work/caller.o" --shared --no-undefined -o "$work/missing.so" > "$work/missing.log" 2>&1; then
    echo 'FAIL unresolved shared-library dependency accepted' >&2
    exit 1
fi
grep -q 'unresolved shared-library imports' "$work/missing.log"
test ! -e "$work/missing.so"
for mode in lto native; do
    if [ "$mode" = native ]; then set -- --no-lto; else set --; fi
    corlink "$work/caller.o" --link-shared "$work/libCompute.so" --runpath "$work" "$@" -o "$work/$mode"
    status=0
    "$work/$mode" || status=$?
    test "$status" = 42
    readelf -d "$work/$mode" > "$work/$mode.dynamic"
    grep -q 'Shared library: \[libCompute.so\]' "$work/$mode.dynamic"
done
"$corc" compile --nostdlib --shared tests/integration/ir-lto/SharedData.cor -o "$work/libData.so"
"$corc" compile --nostdlib --obj tests/integration/ir-lto/SharedDataCaller.cor \
    --ref tests/integration/ir-lto/SharedData.cor --link-shared "$work/libData.so" -o "$work/data.o"
corlink "$work/data.o" --link-shared "$work/libData.so" --runpath "$work" -o "$work/data"
status=0
"$work/data" || status=$?
test "$status" = 42
# AND COMPILED WHOLE AGAINST IT, linked by the compile: the image carries
# none of the notes its link reads -- a --shared compile kept its handlers'
# .corsac.irq, a --link-shared one its .corsac.lto.
"$corc" compile --nostdlib tests/integration/ir-lto/SharedDataCaller.cor \
    --ref tests/integration/ir-lto/SharedData.cor --link-shared "$work/libData.so" --runpath "$work" -o "$work/data-whole"
status=0
"$work/data-whole" || status=$?
test "$status" = 42
for image in libData.so data-whole; do
    if readelf -SW "$work/$image" | grep -q '\.corsac\.\(irq\|lto\|ir\|uses\)'; then
        echo "FAIL $image carries the notes its link read: $(readelf -SW "$work/$image" | grep -o '\.corsac\.[a-z.]*' | tr '\n' ' ')" >&2
        exit 1
    fi
done
# A LIBRARY OF ITS OWN OVER THE CLASS LIBRARY: `--shared` alone takes the
# class library's sources as --ref, so the object holds its own code and
# imports the rest. Owned, the whole class library was rooted in the one
# object, and the compile stopped on a copy's body wanted after binding.
"$corc" compile --shared tests/integration/ir-lto/Words.cor -o "$work/libWords.so"
readelf --dyn-syms -W "$work/libWords.so" > "$work/words.syms"
awk '$7 != "UND" && $8 ~ /^m_Words_Joined_/' "$work/words.syms" | grep -q .
awk '$7 == "UND" && $8 ~ /^m_String_Join_/' "$work/words.syms" | grep -q .
if awk '$7 != "UND" && $8 ~ /^m_(Runtime|String)_/' "$work/words.syms" | grep -q .; then
    echo 'FAIL a --shared library of its own compiled the class library into itself' >&2
    exit 1
fi
printf 'PASS separate dynamic linking with and without LTO: %s\n' "$work"
