#!/bin/sh
# A Windows DLL (docs/WIN32-TARGET.md): stdcall exports by the names C asks
# for, a kernel32 import through its thunk, and a .reloc that moves it.
# Structural only: nothing here runs Windows.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p build
work=$(mktemp -d "$root/build/windows-dll.XXXXXX")
mkdir "$work/a" "$work/b"
"$corc" compile --freestanding --dll tests/integration/windows-dll/Exports.cor -o "$work/a/exports.dll" > "$work/a.log" 2>&1
"$corc" compile --freestanding --dll --base 0x20000000 tests/integration/windows-dll/Exports.cor -o "$work/b/exports.dll" > "$work/b.log" 2>&1
python3 tests/integration/windows-dll/check-pe.py "$work/a/exports.dll" "$work/b/exports.dll" Add Calls _grDrawTriangle@12 glVertex3f
if "$corc" compile --freestanding --dll tests/integration/windows-dll/Twice.cor -o "$work/twice.dll" > "$work/twice.log" 2>&1; then
    echo 'FAIL two methods exported under one name' >&2
    exit 1
fi
grep -q "are both exported as 'Same'" "$work/twice.log"
if "$corc" compile --windows tests/integration/windows-dll/Exports.cor -o "$work/program.exe" > "$work/program.log" 2>&1; then
    echo 'FAIL a Windows program linked before its platform layer exists' >&2
    exit 1
fi
echo 'PASS windows-dll'
