#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/Release/net10.0/corc}
corlink=${CORLINK:-$root/linker/bin/Release/net10.0/corlink}
mkdir -p build
work=$(mktemp -d "$root/build/indexed.XXXXXX")
"$corc" index --assembly Indexed tests/integration/indexed/Value.cor tests/integration/indexed/Unused.cor -o "$work/declarations.idx"
"$corc" compile --nostdlib --lib tests/integration/indexed/Value.cor --obj -o "$work/value.o"
for source in Caller AliasCaller QualifiedCaller; do
    "$corc" compile --nostdlib --decl-index "$work/declarations.idx" --assembly Indexed \
        "tests/integration/indexed/$source.cor" --obj -o "$work/$source.o" 2> "$work/$source.compile.log"
    grep -q 'indexed declaration payloads loaded=1' "$work/$source.compile.log"
    "$corlink" "$work/$source.o" "$work/value.o" -o "$work/$source" 2> "$work/$source.link.log"
    status=0
    "$work/$source" || status=$?
    test "$status" = 42
done
printf 'PASS indexed namespace/alias/qualified consumers and separate linking: %s\n' "$work"
