#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
corlink=${CORLINK:-$root/linker/bin/managed/Release/net10.0/corlink}
export CORC="$corc"
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
"$corc" index --assembly SharedGenerics tests/integration/indexed/GenericFunctions.cor \
    tests/integration/indexed/GenericType.cor tests/integration/indexed/GenericProvider.cor -o "$work/shared.idx"
"$corc" compile --nostdlib --lib --decl-index "$work/shared.idx" --assembly SharedGenerics \
    tests/integration/indexed/GenericProvider.cor --obj -o "$work/provider.o" 2> "$work/provider.compile.log"
"$corc" compile --nostdlib --no-opt --decl-index "$work/shared.idx" --assembly SharedGenerics \
    tests/integration/indexed/GenericSharedCaller.cor --obj -o "$work/shared-caller.o" 2> "$work/shared-caller.compile.log"
"$corlink" "$work/shared-caller.o" "$work/provider.o" -o "$work/shared-generic" 2> "$work/shared-generic.link.log"
status=0
"$work/shared-generic" || status=$?
test "$status" = 42
for family in Partial Cycle; do
    "$corc" index --assembly "$family" "tests/integration/indexed/${family}B.cor" \
        "tests/integration/indexed/${family}A.cor" -o "$work/$family.idx"
    for part in A B; do
        "$corc" compile --nostdlib --lib --decl-index "$work/$family.idx" --assembly "$family" \
            "tests/integration/indexed/$family$part.cor" --obj -o "$work/$family$part.o" 2> "$work/$family$part.compile.log"
    done
    "$corc" compile --nostdlib --decl-index "$work/$family.idx" --assembly "$family" \
        "tests/integration/indexed/${family}Caller.cor" --obj -o "$work/${family}Caller.o" 2> "$work/${family}Caller.compile.log"
    "$corlink" "$work/${family}Caller.o" "$work/${family}B.o" "$work/${family}A.o" \
        -o "$work/$family" 2> "$work/$family.link.log"
    status=0
    "$work/$family" || status=$?
    test "$status" = 42
done
printf 'PASS indexed namespace/alias/qualified consumers and separate linking: %s\n' "$work"
