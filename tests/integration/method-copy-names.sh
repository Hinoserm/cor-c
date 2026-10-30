#!/bin/sh
# ONE COPY OF A GENERIC METHOD PER TYPE, AS C# HAS IT: a reference type
# argument's `?` is an annotation of the use (Binder.CopyArgument), never part
# of the copy's name. Test 2502 calls Volatile.Write, Interlocked.Exchange and
# a generic method of its own with a Backing? argument, before and after a
# test proves it not null; its object must hold one copy of each, its name
# without a `?`.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/method-copy-names.XXXXXX")

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

"$corc" compile tests/language/2502_ref_argument_infers_its_declared_type.cor --obj -o "$work/2502.o" 2> "$work/compile.log" \
    || { cat "$work/compile.log"; fail "test 2502 did not compile"; }
readelf -s -W "$work/2502.o" | awk '$8 ~ /^m_.*(Volatile_Write|Interlocked_Exchange|Holder_Set)\$/ { print $8 }' | sort -u > "$work/copies"
for method in Volatile_Write Interlocked_Exchange Holder_Set; do
    count=$(grep -c "${method}\\\$Backing" "$work/copies" || true)
    test "$count" = 1 || { cat "$work/copies"; fail "$method has $count copies over Backing, not one"; }
done
if grep -q '?' "$work/copies"; then cat "$work/copies"; fail "a copy's name holds a reference's ?"; fi

printf 'PASS method copy names: %s\n' "$work"
