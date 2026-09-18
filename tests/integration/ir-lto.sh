#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/Release/net10.0/corc}
corlink=${CORLINK:-$root/linker/bin/Release/net10.0/corlink}
export CORC="$corc"
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/ir-lto.XXXXXX")
cp tests/integration/ir-lto/Compute.cor "$work/Compute.cor"
cp tests/integration/ir-lto/Caller.cor "$work/Caller.cor"
"$corc" compile --nostdlib --lib "$work/Compute.cor" --obj -o "$work/compute.o"
"$corc" compile --nostdlib "$work/Caller.cor" --ref "$work/Compute.cor" --obj -o "$work/caller.o"
# Linking must use the intermediate objects, not read source back in.
mv "$work/Compute.cor" "$work/Compute.source-not-available"
mv "$work/Caller.cor" "$work/Caller.source-not-available"
for mode in on off budget; do
    case "$mode" in
        on) set -- ;;
        off) set -- --no-lto ;;
        budget) set -- --lto-import-bytes 1 ;;
    esac
    "$corlink" "$@" "$work/caller.o" "$work/compute.o" -o "$work/$mode" 2> "$work/$mode.link.log"
    status=0
    "$work/$mode" || status=$?
    test "$status" = 42
    readelf -SW "$work/$mode" > "$work/$mode.sections"
    if grep -q '\.corsac\.ir' "$work/$mode.sections"; then echo 'Compiler IR leaked into final image' >&2; exit 1; fi
    objdump -d "$work/$mode" > "$work/$mode.disassembly"
done
grep -q 'IR units regenerated=1' "$work/on.link.log"
grep -q 'IR units regenerated=0' "$work/off.link.log"
grep -q 'IR units regenerated=0' "$work/budget.link.log"
grep -Eq 'call.*<m_Compute_Choose' "$work/off.disassembly"
grep -Eq 'call.*<m_Compute_Choose' "$work/budget.disassembly"
if grep -Eq 'call.*<m_Compute_Choose' "$work/on.disassembly"; then echo 'IR import did not inline the cross-unit call' >&2; exit 1; fi
objcopy --dump-section ".text=$work/on.text" "$work/on"
objcopy --dump-section ".text=$work/off.text" "$work/off"
test "$(wc -c < "$work/on.text")" -lt "$(wc -c < "$work/off.text")"
printf 'PASS IR imports, cross-unit inlining/constant propagation, native execution and budgets: %s\n' "$work"
