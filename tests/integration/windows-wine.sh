#!/bin/sh
# A Windows DLL run: corc's DLL loaded by a 32-bit Windows program under Wine,
# each export called and its answer checked, then four threads in a DLL at
# once, and a read through null in the DLL caught as NullReferenceException;
# then the same DLLs over the runtime DLL (--windows --shared, --dynamic),
# with statics only a DLL holds kept across collections, and two DLLs over
# one runtime in one process (docs/WIN32-TARGET.md).
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
# OVER THE RUNTIME DLL: corsacrt.dll built once, each DLL --dynamic against it.
mkdir "$work/dyn"
"$corc" compile --windows --shared -o "$work/dyn/corsacrt.dll" > "$work/runtime.log" 2>&1 || { cat "$work/runtime.log"; echo 'FAIL windows-wine: the runtime DLL'; exit 1; }
for unit in Exports:tests/integration/windows-dll/Exports.cor Threads:tests/integration/windows-wine/Threads.cor Keep:tests/integration/windows-wine/Keep.cor; do
    "$corc" compile --dll --dynamic --libdir "$work/dyn" "${unit#*:}" -o "$work/dyn/$(echo ${unit%%:*} | tr A-Z a-z).dll" > "$work/dyn-${unit%%:*}.log" 2>&1 \
        || { cat "$work/dyn-${unit%%:*}.log"; echo "FAIL windows-wine: ${unit%%:*} over the runtime DLL"; exit 1; }
done
for c in host threads keep two; do
    i686-w64-mingw32-gcc -O1 tests/integration/windows-wine/$c.c -o "$work/dyn/$c.exe"
done
run() { (cd "$work/dyn" && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine "$@") 2>&1 | tr -d '\r'; }
want() { echo "$1" | grep -qx "$2" || { echo "$1"; echo "FAIL windows-wine: over the runtime DLL, no '$2'"; exit 1; }; }
got=$(run host.exe exports.dll); for line in 'add sum 630' 'vertex 7.250' 'calls>0 1' 'free 1'; do want "$got" "$line"; done
got=$(run threads.exe threads.dll); for line in 'thread 0 ok' 'thread 1 ok' 'thread 2 ok' 'thread 3 ok' 'collected yes' 'null caught 3'; do want "$got" "$line"; done
got=$(run keep.exe keep.dll); for line in 'filled 5000' 'collected yes' 'check 0'; do want "$got" "$line"; done
got=$(run two.exe keep.dll exports.dll); want "$got" 'two: add sum 630, check 0'
echo 'PASS windows-wine'
