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
"$corc" compile --nostdlib --lib tests/integration/indexed/DifferentValue.cor --obj -o "$work/different.o"
if "$corlink" "$work/Caller.o" "$work/different.o" -o "$work/incompatible" 2> "$work/incompatible.log"; then
    echo 'Incompatible managed return ABI was accepted' >&2; exit 1
fi
grep -q 'managed layout.*conflicts' "$work/incompatible.log"
test ! -e "$work/incompatible"
"$corc" index --assembly Generics tests/integration/indexed/GenericFunctions.cor tests/integration/indexed/GenericType.cor -o "$work/generics.idx"
for source in GenericCaller GenericTypeCaller; do
    "$corc" compile --nostdlib --decl-index "$work/generics.idx" --assembly Generics \
        "tests/integration/indexed/$source.cor" --obj -o "$work/$source.o" 2> "$work/$source.compile.log"
    "$corlink" "$work/$source.o" -o "$work/$source" 2> "$work/$source.link.log"
    status=0
    "$work/$source" || status=$?
    test "$status" = 42
done
printf 'PASS indexed namespace/alias/qualified consumers and separate linking: %s\n' "$work"
