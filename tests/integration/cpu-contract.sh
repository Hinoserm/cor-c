#!/bin/sh
# UNITS MADE FOR DIFFERENT PROCESSORS, LINKED WITH NONE NAMED, ARE REFUSED:
# a unit compiled for the 486 in an image of 386 units ran CMPXCHG where the
# 386 has none, and the link said nothing. Named (--cpu), the image is for
# that processor, and each unit's instructions are checked against it.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/cpu-contract.XXXXXX")
cp tests/integration/ir-lto/Compute.cor "$work/Compute.cor"
cp tests/integration/ir-lto/Caller.cor "$work/Caller.cor"
"$corc" compile --cpu=486 --nostdlib --lib "$work/Compute.cor" --obj -o "$work/compute.o"
"$corc" compile --cpu=386 --fpu=387 --nostdlib "$work/Caller.cor" --ref "$work/Compute.cor" --obj -o "$work/caller.o"
if "$corc" link "$work/caller.o" "$work/compute.o" -o "$work/none" 2> "$work/none.log"; then
    echo 'units for the 386 and the 486 linked with no processor named' >&2; exit 1
fi
grep -q 'were compiled for different processors' "$work/none.log"
"$corc" link --cpu=486 "$work/caller.o" "$work/compute.o" -o "$work/named" 2> "$work/named.log"
status=0
"$work/named" || status=$?
test "$status" = 42
if "$corc" link --cpu=386 --fpu=387 "$work/caller.o" "$work/compute.o" -o "$work/short" 2> "$work/short.log"; then
    echo 'a 486 unit linked into an image for the 386' >&2; exit 1
fi
grep -q 'exceed selected CPU/FPU target' "$work/short.log"
echo "cpu-contract: PASS"
