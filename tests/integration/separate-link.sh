#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
# Linking is a command of the one toolchain executable.
corlink() { "${CORLINK:-$corc}" link "$@"; }
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/separate-link.XXXXXX")
"$corc" compile examples/hello/Program.cor --obj -o "$work/hello.o"
corlink "$work/hello.o" -o "$work/hello"
"$work/hello" > "$work/actual.txt"
printf 'Hello from CORSAC/C#.\n' | cmp - "$work/actual.txt"
printf 'PASS separate compiler/object/linker/executable: %s\n' "$work"
