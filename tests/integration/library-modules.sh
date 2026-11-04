#!/bin/sh
# A KERNEL WHOSE CLASS LIBRARY IS SPLIT WITH ITS MODULES (--library-modules):
# the kernel keeps of the library what its own code and the runtime reach and
# exports all it keeps (PublicApi `?`); a public library member nothing in it
# reaches is not in it. A library module compiled from the library's source
# with the same switch, every library source named, owns the rest, member by
# member (Module.FixOwnLibrary):
# it defines what the kernel left out, takes what the kernel exports from the
# kernel, and links against it.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/library-modules.XXXXXX")
here=tests/integration/library-modules
kernel_files="tests/integration/modules/Host.cor tests/integration/modules/Kernel.cor tests/integration/modules/Loader.cor"
clamp='m_Math_Clamp_3_V$I32_V$I32_V$I32_r$V$I32'

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

k=$work/kernel
mkdir -p "$k"
runtime=$("$corc" library-sources --freestanding)
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 $kernel_files $runtime -o "$k/kernel.idx" 2> "$k/index.log" \
    || { cat "$k/index.log"; fail "the kernel's index"; }
: > "$k/units.tsv"
for source in $kernel_files $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$k/$name.o" "$k/$name.o.deps" "$role" >> "$k/units.tsv"
done
"$corc" compile-project --units "$k/units.tsv" --nostdlib --obj --freestanding --ring 0 --library-modules \
    --decl-index "$k/kernel.idx" --assembly Kernel 2> "$k/units.log" || { cat "$k/units.log"; fail "kernel units"; }
# shellcheck disable=SC2086
"$corc" link $(cut -f2 "$k/units.tsv" | tr '\n' ' ') -o "$k/kernel" --exports 2> "$k/link.log" \
    || { cat "$k/link.log"; fail "kernel link"; }
python3 -I "$root/tests/integration/modules/exports.py" "$k/kernel" | awk '{ print $1 }' > "$k/exports"

# The library the kernel reaches is in it and exported, and every static of
# the library; what it does not reach is neither. And the mark a library
# module asks for.
grep -q '^m_String_' "$k/exports" || fail "the kernel exports none of the library it keeps"
# A table of the date reader's nothing in the kernel touches: every static
# of the library stays the kernel's, reached or not.
grep -qxF 's_FreeDateReader_Kinds_t$A$1$V$I32' "$k/exports" || fail "the kernel does not export a library static it does not reach"
grep -qx '__corsac_library_modules' "$k/exports" || fail "the kernel does not say it splits its library"
if grep -qxF "$clamp" "$k/exports"; then fail "the kernel exports Math.Clamp, which nothing in it reaches"; fi
nm "$k/kernel" > "$k/symbols" || fail "nm could not read the kernel"
if awk '{ print $NF }' "$k/symbols" | grep -qxF "$clamp"; then fail "the kernel carries Math.Clamp, which nothing in it reaches"; fi

# The library module, as libcore is built: every source of the kernel's
# library named, so each member is the module's where the kernel exports
# none of it -- Core.cor's Math.Clamp, and what Core.cor's code reaches in
# the library's other files that the kernel left out (Task, Volatile).
m=$work/module
mkdir -p "$m"
# shellcheck disable=SC2086
"$corc" compile --kernel "$k/kernel" --decl-index "$k/kernel.idx" --assembly Kernel --ring 0 --library-modules \
    $here/Clamps.cor $runtime --obj -o "$m/clamps.o" 2> "$m/compile.log" \
    || { cat "$m/compile.log"; fail "the library module's compile"; }
nm "$m/clamps.o" | awk '$2 ~ /^[Tt]$/ { print $3 }' | grep -qxF "$clamp" || fail "the library module does not define Math.Clamp, which the kernel left out"
# No member of the library the kernel exports is defined again here: its
# methods, statics, descriptors and boxes (m_ s_ ts_ t_ b_). The image's own
# helpers and tables -- array helpers, layout names, type keys, variance
# tables -- every image makes for itself, as every module always has.
nm "$m/clamps.o" | awk '$2 ~ /^[TtDdBbRr]$/ && $3 ~ /^(m_|s_|ts_|t_|b_)/ { print $3 }' | sort > "$m/defined"
sort "$k/exports" | comm -12 - "$m/defined" > "$m/twice"
test ! -s "$m/twice" || { head "$m/twice"; fail "the library module defines what the kernel exports"; }
"$corc" link "$m/clamps.o" --kernel "$k/kernel" -o "$m/clamps.ko" 2> "$m/link.log" \
    || { cat "$m/link.log"; fail "the library module's link"; }

# A library module over a kernel that does not split its library is refused:
# that kernel keeps what it withholds, and owning by member would copy it.
w=$work/whole
mkdir -p "$w"
sed "s|$k/|$w/|g" "$k/units.tsv" > "$w/units.tsv"
"$corc" compile-project --units "$w/units.tsv" --nostdlib --obj --freestanding --ring 0 \
    --decl-index "$k/kernel.idx" --assembly Kernel 2> "$w/units.log" || { cat "$w/units.log"; fail "the whole kernel's units"; }
# shellcheck disable=SC2086
"$corc" link $(cut -f2 "$w/units.tsv" | tr '\n' ' ') -o "$w/kernel" --exports 2> "$w/link.log" \
    || { cat "$w/link.log"; fail "the whole kernel's link"; }
# shellcheck disable=SC2086
if "$corc" compile --kernel "$w/kernel" --decl-index "$k/kernel.idx" --assembly Kernel --ring 0 --library-modules \
    $here/Clamps.cor $runtime --obj -o "$w/clamps.o" 2> "$w/refused.log"; then
    fail "a library module was built over a kernel that does not split its library"
fi
grep -q "was not built with --library-modules" "$w/refused.log" || { cat "$w/refused.log"; fail "the refusal does not say why"; }

echo "PASS library-modules"
