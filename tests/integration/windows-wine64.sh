#!/bin/sh
# 64-BIT WINDOWS IMAGES (PE32+, docs/WIN32-TARGET.md) under Wine: the 32-bit
# tests' DLLs and host programs built for x64 -- exports called by a 64-bit C
# program, a DLL pinned once loaded, four Windows threads in a DLL with
# collections and a null read caught, the same DLLs over the 64-bit runtime
# DLL -- and a 64-bit program (windows-wine64/Program.cor) whose every line
# is an edge between this compiler's convention and Windows' x64 one. Before
# running anything, the images' headers as objdump reads them: PE32+, the
# default bases, .pdata.
#
#   tests/integration/windows-wine64.sh WINEPREFIX
#
# WINEPREFIX is a Wine prefix that runs 64-bit programs. Skipped without one,
# or without wine and x86_64-w64-mingw32-gcc.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
prefix=${1:-}
if [ -z "$prefix" ] || ! command -v wine > /dev/null || ! command -v x86_64-w64-mingw32-gcc > /dev/null; then
    echo 'SKIP windows-wine64: needs a Wine prefix argument, wine and x86_64-w64-mingw32-gcc'
    exit 0
fi
mkdir -p build
work=$(mktemp -d "$root/build/windows-wine64.XXXXXX")
x64="--target x86-64"
fail() { echo "FAIL windows-wine64: $1"; exit 1; }
run() { (cd "$1" && shift && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine "$@") 2>&1 | tr -d '\r'; }
want() { echo "$1" | grep -qx "$2" || { echo "$1"; fail "no '$2'"; }; }

# THE IMAGES' HEADERS: PE32+, the bases Microsoft's linker gives, an
# exception directory (.pdata) objdump lists, and the subsystem version 5.2
# (XP x64). The unwind data itself is walked below (walk.c).
"$corc" compile $x64 --dll tests/integration/windows-dll/Exports.cor -o "$work/exports.dll" > "$work/dll.log" 2>&1 || { cat "$work/dll.log"; fail "the DLL"; }
"$corc" compile $x64 --windows --subsystem console tests/integration/windows-wine64/Program.cor -o "$work/program.exe" > "$work/program.log" 2>&1 \
    || { cat "$work/program.log"; fail "the program"; }
x86_64-w64-mingw32-objdump -p "$work/exports.dll" > "$work/dll.headers"
x86_64-w64-mingw32-objdump -p "$work/program.exe" > "$work/program.headers"
grep -q 'Magic.*020b' "$work/dll.headers" || fail "the DLL is not PE32+"
grep -q 'ImageBase.*0000000180000000' "$work/dll.headers" || fail "the DLL is not based at 0x180000000"
grep -q 'ImageBase.*0000000140000000' "$work/program.headers" || fail "the program is not based at 0x140000000"
grep -q 'The Function Table' "$work/program.headers" || fail "the program has no .pdata"
grep -q 'MajorSubsystemVersion.*5$' "$work/program.headers" && grep -q 'MinorSubsystemVersion.*2$' "$work/program.headers" \
    || fail "the program's subsystem version is not 5.2"

# THE PROGRAM, every edge.
code=0
(cd "$work" && WINEPREFIX=$prefix WINEDEBUG=-all timeout 120 wine program.exe one two) > "$work/program.out" 2>&1 || code=$?
got=$(tr -d '\r' < "$work/program.out")
for line in 'hello 64' 'args one two' 'null caught' 'threads 3200006' 'collected yes' 'deep 1' 'sorted 1,2,3,5,7,8,9 True' 'printf 2.500' 'pow 1024'; do want "$got" "$line"; done
[ "$code" = 7 ] || fail "the program's exit code is $code, not 7"

# THE 32-BIT TESTS' DLLS AND HOSTS, built for x64.
for c in host pin threads keep two; do
    x86_64-w64-mingw32-gcc -O1 tests/integration/windows-wine/$c.c -o "$work/$c.exe"
done
got=$(run "$work" host.exe exports.dll); for line in 'add sum 630' 'vertex 7.250' 'calls>0 1' 'free 1'; do want "$got" "$line"; done
got=$(run "$work" pin.exe exports.dll); want "$got" 'pinned 1'
"$corc" compile $x64 --dll tests/integration/windows-wine/Threads.cor -o "$work/threads.dll" > "$work/threads-dll.log" 2>&1 || { cat "$work/threads-dll.log"; fail "threads.dll"; }
got=$(run "$work" threads.exe threads.dll); for line in 'thread 0 ok' 'thread 1 ok' 'thread 2 ok' 'thread 3 ok' 'collected yes' 'null caught 3'; do want "$got" "$line"; done

# A SLOT PAST SIXTY-FOUR AND THE LAST ERROR: the host takes seventy TLS slots
# first, so the DLL's is in the thread's expansion array (gs:[0x1780]); a call
# in leaves the thread's last error as it was (no TlsGetValue on the way in),
# and a thread of the host's, with no expansion array of its own yet, calls in.
x86_64-w64-mingw32-gcc -O1 tests/integration/windows-wine64/slots.c -o "$work/slots.exe"
got=$(run "$work" slots.exe exports.dll); for line in 'slots taken past 64 1' 'call 1 lasterror 4321' 'foreign 1 lasterror 777'; do want "$got" "$line"; done

# THE UNWIND DATA, WALKED: the host's callback, called from inside the DLL,
# walks the stack by every image's .pdata and .xdata back through the DLL's
# way-in stub and body to the host's function that called it.
"$corc" compile $x64 --dll tests/integration/windows-wine64/Walk.cor -o "$work/walk.dll" > "$work/walk-dll.log" 2>&1 || { cat "$work/walk-dll.log"; fail "walk.dll"; }
x86_64-w64-mingw32-gcc -O1 tests/integration/windows-wine64/walk.c -o "$work/walk.exe"
got=$(run "$work" walk.exe walk.dll); for line in 'through 46' 'walked dll frames>0 1 caller 1'; do want "$got" "$line"; done

# A 32-BIT DLL NAMES NO VECTORED HANDLER: those are a 64-bit image's
# (Win32.Attach, Release), and Windows 95, NT 4 and 2000 have none.
"$corc" compile --dll tests/integration/windows-dll/Exports.cor -o "$work/exports32.dll" > "$work/dll32.log" 2>&1 || { cat "$work/dll32.log"; fail "the 32-bit DLL"; }
x86_64-w64-mingw32-objdump -p "$work/exports32.dll" > "$work/dll32.headers"
grep -q 'Magic.*010b' "$work/dll32.headers" || fail "the 32-bit DLL is not PE32"
if grep -q 'VectoredExceptionHandler' "$work/dll32.headers"; then fail "the 32-bit DLL imports a vectored-handler function"; fi

# OVER THE 64-BIT RUNTIME DLL: corsacrt.dll built once, each DLL --dynamic against it.
mkdir "$work/dyn"
"$corc" compile $x64 --windows --shared -o "$work/dyn/corsacrt.dll" > "$work/runtime.log" 2>&1 || { cat "$work/runtime.log"; fail "the runtime DLL"; }
for unit in Exports:tests/integration/windows-dll/Exports.cor Threads:tests/integration/windows-wine/Threads.cor Keep:tests/integration/windows-wine/Keep.cor; do
    "$corc" compile $x64 --dll --dynamic --libdir "$work/dyn" "${unit#*:}" -o "$work/dyn/$(echo ${unit%%:*} | tr A-Z a-z).dll" > "$work/dyn-${unit%%:*}.log" 2>&1 \
        || { cat "$work/dyn-${unit%%:*}.log"; fail "${unit%%:*} over the runtime DLL"; }
done
for c in host threads keep two; do cp "$work/$c.exe" "$work/dyn/"; done
got=$(run "$work/dyn" host.exe exports.dll); for line in 'add sum 630' 'vertex 7.250' 'calls>0 1' 'free 1'; do want "$got" "$line"; done
got=$(run "$work/dyn" threads.exe threads.dll); for line in 'thread 0 ok' 'thread 1 ok' 'thread 2 ok' 'thread 3 ok' 'collected yes' 'null caught 3'; do want "$got" "$line"; done
got=$(run "$work/dyn" keep.exe keep.dll); for line in 'filled 5000' 'collected yes' 'check 0'; do want "$got" "$line"; done
got=$(run "$work/dyn" two.exe keep.dll exports.dll); want "$got" 'two: add sum 630, check 0'
echo 'PASS windows-wine64'
