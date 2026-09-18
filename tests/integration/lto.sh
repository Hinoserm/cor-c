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
objcopy --dump-section ".text=$work/on.text" "$work/on"
objcopy --dump-section ".text=$work/off.text" "$work/off"
test "$(wc -c < "$work/on.text")" = "$(wc -c < "$work/off.text")"
if cmp -s "$work/on.text" "$work/off.text"; then echo 'LTO did not change code' >&2; exit 1; fi
"$corc" compile --nostdlib --lib --no-stackmaps tests/integration/lto/Effect.cor --obj -o "$work/effect.o"
"$corc" compile --nostdlib --no-stackmaps tests/integration/lto/EffectCaller.cor --ref tests/integration/lto/Effect.cor --obj -o "$work/effect-caller.o"
"$corlink" "$work/effect-caller.o" "$work/effect.o" -o "$work/effect" 2> "$work/effect.link.log"
grep -q 'LTO calls folded=0' "$work/effect.link.log"
status=0
"$work/effect" > "$work/effect.txt" || status=$?
test "$status" = 43
# Static initialization includes exception caching and therefore needs the
# runtime. This checks retention through the separate linker, not independent
# managed-runtime/type-layout ownership (which is a later acceptance gate).
"$corc" compile tests/integration/lto/Initialize.cor tests/integration/lto/EffectCaller.cor --obj -o "$work/init.o"
"$corlink" "$work/init.o" -o "$work/init" 2> "$work/init.link.log"
status=0
"$work/init" || status=$?
test "$status" = 47
"$corc" compile --nostdlib --lib --no-stackmaps --freestanding tests/integration/lto/Value.cor --obj -o "$work/bare-value.o"
if "$corlink" "$work/caller.o" "$work/bare-value.o" -o "$work/mixed" 2> "$work/mixed.log"; then
    echo 'Hosted/bare-metal ABI mismatch was accepted' >&2; exit 1
fi
grep -q 'TLS/platform contract conflicts' "$work/mixed.log"
test ! -e "$work/mixed"
# Exercise actual separately generated bare-metal code and an assembly-owned
# entry point. These are layout tests, not executable OS boot acceptance.
"$corc" build --target x86-32 tests/integration/lto/Startup.asm --obj -o "$work/start.o"
"$corc" compile --nostdlib --no-stackmaps --freestanding --asm-entry managed_entry tests/integration/lto/Caller.cor --ref tests/integration/lto/Value.cor --obj -o "$work/bare-caller.o"
"$corlink" --flat --base 0x10000 "$work/start.o" "$work/bare-caller.o" "$work/bare-value.o" -o "$work/stage2.bin" 2> "$work/flat.log"
grep -q 'flat: entry=0x10000 base=0x10000' "$work/flat.log"
test "$(od -An -tx1 -N1 "$work/stage2.bin" | tr -d ' ')" = fa
"$corlink" --base 0xc0100000 --paddr 0x100000 "$work/start.o" "$work/bare-caller.o" "$work/bare-value.o" -o "$work/kernel" 2> "$work/kernel.log"
readelf -lW "$work/kernel" > "$work/kernel.headers"
grep -Eq 'LOAD[[:space:]]+0x[0-9a-f]+[[:space:]]+0xc0100000[[:space:]]+0x00100000' "$work/kernel.headers"
test -z "$(nm -u "$work/kernel")"
# The convenience flat path must retain --with objects, and must reject an
# incompatible contract just like the separate linker.
"$corc" compile --nostdlib --no-stackmaps --freestanding --flat tests/integration/lto/Caller.cor --ref tests/integration/lto/Value.cor --with "$work/bare-value.o" -o "$work/convenience.bin"
if "$corc" compile --nostdlib --no-stackmaps --freestanding --flat tests/integration/lto/Caller.cor --ref tests/integration/lto/Value.cor --with "$work/value.o" -o "$work/bad-flat.bin" 2> "$work/bad-flat.log"; then
    echo 'Convenience flat link accepted a hosted object' >&2; exit 1
fi
grep -q 'TLS/platform contract conflicts' "$work/bad-flat.log"
test ! -e "$work/bad-flat.bin"
printf 'PASS cross-object LTO, effects, contracts and bare-metal layout: %s\n' "$work"
