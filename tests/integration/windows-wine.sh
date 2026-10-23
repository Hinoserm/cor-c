#!/bin/sh
# A Windows DLL run: corc's DLL loaded by a 32-bit Windows program under Wine,
# each export called and its answer checked, then four threads in a DLL at
# once, and a read through null in the DLL caught as NullReferenceException
# (docs/WIN32-TARGET.md).
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
# Loaded for good: a FreeLibrary leaves it where it was.
i686-w64-mingw32-gcc -O1 tests/integration/windows-wine/pin.c -o "$work/pin.exe"
(cd "$work" && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine pin.exe exports.dll) > "$work/pin.log" 2>&1 || { cat "$work/pin.log"; echo 'FAIL windows-wine: the pin check did not finish'; exit 1; }
tr -d '\r' < "$work/pin.log" | grep -qx 'pinned 1' || { cat "$work/pin.log"; echo 'FAIL windows-wine: the DLL was unloaded'; exit 1; }

# Four Windows threads in the DLL at once, each given a block on its first
# call and letting it go as it ends, with collections while they all run.
"$corc" compile --dll tests/integration/windows-wine/Threads.cor -o "$work/threads.dll" > "$work/threads-dll.log" 2>&1
i686-w64-mingw32-gcc -O1 tests/integration/windows-wine/threads.c -o "$work/threads.exe"
(cd "$work" && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine threads.exe threads.dll) > "$work/threads.log" 2>&1 || { cat "$work/threads.log"; echo 'FAIL windows-wine: the threads did not finish'; exit 1; }
tr -d '\r' < "$work/threads.log" > "$work/threads-lines.log"
for line in 'thread 0 ok' 'thread 1 ok' 'thread 2 ok' 'thread 3 ok' 'collected yes' 'null caught 3'; do
    grep -qx "$line" "$work/threads-lines.log" || { cat "$work/threads.log"; echo "FAIL windows-wine: no '$line'"; exit 1; }
done
echo 'PASS windows-wine'
