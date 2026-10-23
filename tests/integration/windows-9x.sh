#!/bin/sh
# COR-C#'s Windows DLLs on Windows 9x itself (docs/WIN32-TARGET.md): a DLL
# whose exports run, four of the program's threads in a DLL at once with
# collections, a null read caught as NullReferenceException, and a DLL over
# the runtime DLL -- in a Windows 9x machine under 86Box, booted headless.
#
#   tests/integration/windows-9x.sh MACHINE
#
# MACHINE is a folder of one's own (never another's machine): c.img, a FAT
# disk whose partition starts at sector 63; 86box.cfg naming it; nvr; and an
# 86Box binary. The test's files go into C:\TEST and a batch file into the
# StartUp folder, the machine is booted for three minutes, and C:\TEST\OUT.TXT
# is read off the image once it has stopped. Skipped without a machine, or
# without mtools and i686-w64-mingw32-gcc.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
machine=${1:-}
if [ -z "$machine" ] || [ ! -f "$machine/c.img" ] || ! command -v mcopy > /dev/null || ! command -v i686-w64-mingw32-gcc > /dev/null; then
    echo 'SKIP windows-9x: needs a machine folder, mtools and i686-w64-mingw32-gcc'
    exit 0
fi
mkdir -p build
work=$(mktemp -d "$root/build/windows-9x.XXXXXX")
mkdir "$work/lib"
"$corc" compile --dll tests/integration/windows-dll/Exports.cor -o "$work/STATIC.DLL" > "$work/static.log" 2>&1
"$corc" compile --dll tests/integration/windows-wine/Threads.cor -o "$work/THREADS.DLL" > "$work/threads.log" 2>&1
"$corc" compile --windows --shared -o "$work/lib/corsacrt.dll" > "$work/runtime.log" 2>&1
cp "$work/lib/corsacrt.dll" "$work/CORSACRT.DLL"
"$corc" compile --dll --dynamic --libdir "$work/lib" tests/integration/windows-dll/Exports.cor -o "$work/DYN.DLL" > "$work/dyn.log" 2>&1
# Kernel32 alone and no C runtime, for Windows 95's msvcrt as much as any.
i686-w64-mingw32-gcc -O1 -nostdlib -nostartfiles -mwindows -Wl,-e,_start@0 \
    -Wl,--major-subsystem-version,4,--minor-subsystem-version,0,--major-os-version,4,--minor-os-version,0 \
    tests/integration/windows-9x/w98test.c -lkernel32 -o "$work/W98TEST.EXE"
image="$machine/c.img@@32256"
mmd -i "$image" ::/TEST 2>/dev/null || true
for f in STATIC.DLL THREADS.DLL DYN.DLL CORSACRT.DLL W98TEST.EXE; do mcopy -o -i "$image" "$work/$f" "::/TEST/$f"; done
mdel -i "$image" ::/TEST/OUT.TXT 2>/dev/null || true
printf 'C:\r\ncd \\TEST\r\nW98TEST.EXE\r\n' > "$work/RUNTEST.BAT"
mcopy -o -i "$image" "$work/RUNTEST.BAT" "::/WINDOWS/Start Menu/Programs/StartUp/RUNTEST.BAT"
python3 tests/integration/windows-9x/boot.py "$machine" 180 > "$work/boot.log" 2>&1
mtype -i "$image" ::/TEST/OUT.TXT | tr -d '\r' > "$work/out.txt" || true
cat "$work/out.txt"
for line in 'C:\TEST\STATIC.DLL: add sum 630, vertex*1000 7250' 'C:\TEST\DYN.DLL: add sum 630, vertex*1000 7250' 'done'; do
    grep -qxF "$line" "$work/out.txt" || { echo "FAIL windows-9x: no '$line'"; exit 1; }
done
grep -qE '^C:\\TEST\\THREADS.DLL: threads ok 4/4, collections [1-9][0-9]*, null caught 3$' "$work/out.txt" || { echo 'FAIL windows-9x: the threads'; exit 1; }
grep -q '(9x)' "$work/out.txt" || { echo 'FAIL windows-9x: the machine is not Windows 9x'; exit 1; }
echo 'PASS windows-9x'
