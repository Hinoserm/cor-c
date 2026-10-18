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
# A PROGRAM WITH NO RUNTIME ends itself through Linux's exit(2), whose number
# is the target's: 60 through x86-64's syscall, where 1 -- i386's exit -- is
# write(2), which came back to the trap after it.
printf 'static class Program { static int Main() { return 3; } }\n' > "$work/bare.cor"
for target in x86 x86-64; do
    "$corc" compile --target "$target" --nostdlib "$work/bare.cor" -o "$work/bare-$target"
    status=0
    "$work/bare-$target" || status=$?
    if [ "$status" != 3 ]; then
        printf 'FAIL a --nostdlib %s program exited %s, not 3\n' "$target" "$status" >&2
        exit 1
    fi
done
printf 'PASS separate compiler/object/linker/executable: %s\n' "$work"
