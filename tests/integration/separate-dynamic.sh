#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
corlink=${CORLINK:-$root/linker/bin/managed/Release/net10.0/corlink}
mkdir -p build
work=$(mktemp -d "$root/build/separate-dynamic.XXXXXX")
"$corc" compile --nostdlib --shared tests/integration/ir-lto/Compute.cor -o "$work/libCompute.so"
"$corc" compile --nostdlib --obj tests/integration/ir-lto/Caller.cor \
    --ref tests/integration/ir-lto/Compute.cor --link-shared "$work/libCompute.so" -o "$work/caller.o"
for mode in lto native; do
    if [ "$mode" = native ]; then set -- --no-lto; else set --; fi
    "$corlink" "$work/caller.o" --link-shared "$work/libCompute.so" --runpath "$work" "$@" -o "$work/$mode"
    status=0
    "$work/$mode" || status=$?
    test "$status" = 42
    readelf -d "$work/$mode" > "$work/$mode.dynamic"
    grep -q 'Shared library: \[libCompute.so\]' "$work/$mode.dynamic"
done
printf 'PASS separate dynamic linking with and without LTO: %s\n' "$work"
