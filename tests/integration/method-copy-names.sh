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
readelf -s -W "$work/2502.o" | awk '$8 ~ /^m_.*(Volatile_Write|Interlocked_Exchange|Holder_Set|Holder_Id)\$/ { print $8 }' | sort -u > "$work/copies"
# One copy each over Backing (written Backing? and Backing), and one of
# Volatile.Write over object (written object? and object).
for copy in 'Volatile_Write\$Backing' 'Interlocked_Exchange\$Backing' 'Holder_Set\$Backing' 'Volatile_Write\$object'; do
    count=$(grep -c "$copy" "$work/copies" || true)
    test "$count" = 1 || { cat "$work/copies"; fail "$copy has $count copies, not one"; }
done
# And Id's three (over List<Backing?>, string?[][] and (Backing?, int)[]),
# each once.
count=$(grep -c 'Holder_Id\$' "$work/copies" || true)
test "$count" = 3 || { cat "$work/copies"; fail "Id has $count copies, not three"; }
if grep -q '?' "$work/copies"; then cat "$work/copies"; fail "a copy's name holds a reference's ?"; fi

printf 'PASS method copy names: %s\n' "$work"
