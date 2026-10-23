#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
# Linking is a command of the one toolchain executable.
corlink() { "${CORLINK:-$corc}" link "$@"; }
cpu=${CPU:-486}
export CORC="$corc"
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/ir-lto.XXXXXX")
cp tests/integration/ir-lto/Compute.cor "$work/Compute.cor"
cp tests/integration/ir-lto/Caller.cor "$work/Caller.cor"
"$corc" compile --cpu="$cpu" --nostdlib --lib "$work/Compute.cor" --obj -o "$work/compute.o"
"$corc" compile --cpu="$cpu" --nostdlib "$work/Caller.cor" --ref "$work/Compute.cor" --obj -o "$work/caller.o"
# Linking must use the intermediate objects, not read source back in.
mv "$work/Compute.cor" "$work/Compute.source-not-available"
mv "$work/Caller.cor" "$work/Caller.source-not-available"
for mode in on off budget; do
    case "$mode" in
        on) set -- ;;
        off) set -- --no-lto ;;
        budget) set -- --lto-import-bytes 1 ;;
    esac
    corlink --cpu="$cpu" "$@" "$work/caller.o" "$work/compute.o" -o "$work/$mode" 2> "$work/$mode.link.log"
    status=0
    "$work/$mode" || status=$?
    test "$status" = 42
    readelf -SW "$work/$mode" > "$work/$mode.sections"
    if grep -q '\.corsac\.ir' "$work/$mode.sections"; then echo 'Compiler IR leaked into final image' >&2; exit 1; fi
    if grep -Eq '\.corsac\.(abi|cpu|layout)[[:space:]]' "$work/$mode.sections"; then
        echo 'Consumed object contracts leaked into final image' >&2; exit 1
    fi
    objdump -d "$work/$mode" > "$work/$mode.disassembly"
done
grep -q 'IR units regenerated=1' "$work/on.link.log"
grep -q 'IR units regenerated=0' "$work/off.link.log"
grep -q 'IR units regenerated=0' "$work/budget.link.log"
grep -Eq 'call.*<m_Compute_Choose' "$work/off.disassembly"
grep -Eq 'call.*<m_Compute_Choose' "$work/budget.disassembly"
if grep -Eq 'call.*<m_Compute_Choose' "$work/on.disassembly"; then echo 'IR import did not inline the cross-unit call' >&2; exit 1; fi
# Smaller code with the import: the functions' own bytes, not the section's,
# which aligning each function pads to the same length when the gain is small.
code() { readelf -sW "$1" | awk '$4 == "FUNC" { total += $3 } END { print total }'; }
test "$(code "$work/on")" -lt "$(code "$work/off")"
objcopy --remove-section .corsac.cpu "$work/caller.o" "$work/missing-cpu.o"
if corlink "$work/missing-cpu.o" "$work/compute.o" -o "$work/invalid" 2> "$work/missing-cpu.log"; then
    echo 'Linker accepted a removed required CPU contract' >&2; exit 1
fi
grep -q 'CPU/FPU code-generation contract is missing' "$work/missing-cpu.log"
# A FORWARDER TWO UNITS AWAY: the program calls Helper, whose body calls
# Wrap's one-line wrapper of an intrinsic. The link imports Helper and, with
# it, the wrapper, and the image calls neither (the GUI kernel's 3D paths
# called BitConverter.SingleToInt32Bits this way).
cp tests/integration/ir-lto/Wrap.cor tests/integration/ir-lto/Helper.cor tests/integration/ir-lto/Chain.cor "$work/"
"$corc" compile --cpu="$cpu" --nostdlib --lib "$work/Wrap.cor" --obj -o "$work/wrap.o"
"$corc" compile --cpu="$cpu" --nostdlib --lib "$work/Helper.cor" --ref "$work/Wrap.cor" --obj -o "$work/helper.o"
"$corc" compile --cpu="$cpu" --nostdlib "$work/Chain.cor" --ref "$work/Helper.cor" --ref "$work/Wrap.cor" --obj -o "$work/chain.o"
corlink --cpu="$cpu" "$work/chain.o" "$work/helper.o" "$work/wrap.o" -o "$work/chain" 2> "$work/chain.link.log"
status=0
"$work/chain" || status=$?
test "$status" = 42
objdump -d "$work/chain" > "$work/chain.disassembly"
if grep -Eq 'call.*<m_Wrap_Bits' "$work/chain.disassembly"; then echo 'A forwarder reached through an imported body stayed a call' >&2; exit 1; fi
printf 'PASS IR imports, cross-unit inlining/constant propagation, forwarders through imports, native execution and budgets: %s\n' "$work"
