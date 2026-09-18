#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/Release/net10.0/corc}
corlink=${CORLINK:-$root/linker/bin/Release/net10.0/corlink}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/lto.XXXXXX")
"$corc" compile --nostdlib --lib --no-stackmaps tests/integration/lto/Value.cor --obj -o "$work/value.o"
"$corc" compile --nostdlib --no-stackmaps tests/integration/lto/Caller.cor --ref tests/integration/lto/Value.cor --obj -o "$work/caller.o"
"$corlink" "$work/caller.o" "$work/value.o" -o "$work/on" 2> "$work/on.link.log"
"$corlink" --no-lto "$work/caller.o" "$work/value.o" -o "$work/off" 2> "$work/off.link.log"
grep -Eq 'LTO calls folded=[1-9]' "$work/on.link.log"
grep -q 'LTO calls folded=0' "$work/off.link.log"
status=0
"$work/on" || status=$?
test "$status" = 42
status=0
"$work/off" || status=$?
test "$status" = 42
if cmp -s "$work/on" "$work/off"; then echo 'LTO did not change output' >&2; exit 1; fi
"$corc" compile --nostdlib --lib --no-stackmaps tests/integration/lto/Effect.cor --obj -o "$work/effect.o"
"$corlink" "$work/caller.o" "$work/effect.o" -o "$work/effect" 2> "$work/effect.link.log"
grep -q 'LTO calls folded=0' "$work/effect.link.log"
status=0
"$work/effect" > "$work/effect.txt" || status=$?
test "$status" = 42
printf 'effect\n' | cmp - "$work/effect.txt"
printf 'PASS cross-object LTO and side-effect guard: %s\n' "$work"
