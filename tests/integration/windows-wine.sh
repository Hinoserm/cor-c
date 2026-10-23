#!/bin/sh
# A Windows DLL run: corc's DLL loaded by a 32-bit Windows program under Wine,
# each export called and its answer checked (docs/WIN32-TARGET.md).
#
#   tests/integration/windows-wine.sh WINEPREFIX
#
# WINEPREFIX is a Wine prefix that runs 32-bit programs. Skipped without one,
# or without wine and i686-w64-mingw32-gcc (which builds the program that
# loads the DLL).
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
prefix=${1:-}
if [ -z "$prefix" ] || ! command -v wine > /dev/null || ! command -v i686-w64-mingw32-gcc > /dev/null; then
    echo 'SKIP windows-wine: needs a Wine prefix argument, wine and i686-w64-mingw32-gcc'
    exit 0
fi
mkdir -p build
work=$(mktemp -d "$root/build/windows-wine.XXXXXX")
"$corc" compile --dll tests/integration/windows-dll/Exports.cor -o "$work/exports.dll" > "$work/dll.log" 2>&1
i686-w64-mingw32-gcc -O1 tests/integration/windows-wine/host.c -o "$work/host.exe"
(cd "$work" && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine host.exe exports.dll) > "$work/run.log" 2>&1 || { cat "$work/run.log"; echo 'FAIL windows-wine: the program did not finish'; exit 1; }
tr -d '\r' < "$work/run.log" > "$work/lines.log"
for line in 'add sum 630' 'vertex 7.250' 'calls>0 1' 'free 1'; do
    grep -qx "$line" "$work/lines.log" || { cat "$work/run.log"; echo "FAIL windows-wine: no '$line'"; exit 1; }
done
echo 'PASS windows-wine'
