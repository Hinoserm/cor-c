#!/bin/sh
# A MODULE'S PART OF ITS KERNEL'S PARTIAL TYPE (SourceIndexBuilder.KernelPart):
# the kernel declares Uptime partial and the module adds static members to it,
# as a class library module's io.cor adds DateTime.Now to the kernel's
# DateTime. The module's index is made on the kernel's, its part compiled and
# linked against the kernel's exports -- the kernel's public and private
# members of the type read by the module's part, as one part reads another's.
# And refused, each with why: a part of a type the kernel did not declare
# partial, and in a part an instance field, a virtual member, a static field,
# a static constructor or an instance auto-property.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/module-partial.XXXXXX")
here=tests/integration/module-partial
kernel_files="tests/integration/modules/Host.cor tests/integration/modules/Kernel.cor tests/integration/modules/Loader.cor $here/Uptime.cor"

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

# The kernel, as modules.sh builds it: indexed and compiled per unit for ring 0.
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
"$corc" compile-project --units "$k/units.tsv" --nostdlib --obj --freestanding --ring 0 \
    --decl-index "$k/kernel.idx" --assembly Kernel 2> "$k/units.log" || { cat "$k/units.log"; fail "kernel units"; }
# shellcheck disable=SC2086
"$corc" link $(cut -f2 "$k/units.tsv" | tr '\n' ' ') -o "$k/kernel" --exports 2> "$k/link.log" \
    || { cat "$k/link.log"; fail "kernel link"; }

# The module: its index on the kernel's, holding its part of Uptime.
m=$work/module
mkdir -p "$m"
module_files="$here/AUptimeMore.cor $here/Ticker.cor"
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 --on "$k/kernel.idx" $module_files -o "$m/module.idx" 2> "$m/index.log" \
    || { cat "$m/index.log"; fail "the module's index refused its part of the kernel's partial Uptime"; }
: > "$m/units.tsv"
for source in $module_files; do
    role=lib; case $source in */Ticker.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$m/$name.o" "$m/$name.o.deps" "$role" >> "$m/units.tsv"
done
"$corc" compile-project --units "$m/units.tsv" --obj --kernel "$k/kernel" --decl-index "$k/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/units.log" || { cat "$m/units.log"; fail "the module's units"; }
# shellcheck disable=SC2086
"$corc" link $(cut -f2 "$m/units.tsv" | tr '\n' ' ') --kernel "$k/kernel" -o "$m/ticker.ko" 2> "$m/link.log" \
    || { cat "$m/link.log"; fail "the module's link"; }
# The module's part is the module's code; the kernel's part stays the kernel's.
readelf -s -W "$m/ticker.ko" | awk '$8 ~ /^m_Kernel\$Uptime_Twice_/ && $7 != "UND" { found = 1 } END { exit !found }' \
    || fail "the module does not define its own part's Twice"
if readelf -s -W "$m/ticker.ko" | awk '$8 ~ /^m_Kernel\$Uptime_Ticks_/ && $7 != "UND" { found = 1 } END { exit !found }'; then
    fail "the module defines the kernel's Ticks again"
fi
# THE TYPE IS THE KERNEL'S though the module's part's file sorts first: its
# static, its descriptor and its start are defined by the kernel alone.
if readelf -s -W "$m/ticker.ko" | awk '$7 != "UND" && $8 ~ /Uptime/ && $8 ~ /^(s_|t_|m_Kernel\$Uptime_StaticInit)/ { found = 1 } END { exit !found }'; then
    readelf -s -W "$m/ticker.ko" | awk '$7 != "UND" && $8 ~ /Uptime/'
    fail "the module defines the kernel's Uptime statics, descriptor or start"
fi

# Refused: a kernel type not declared partial, and each shape a part may not have.
if "$corc" index --assembly Kernel --ring 0 --on "$k/kernel.idx" "$here/NotPartial.cor" -o "$m/not.idx" 2> "$m/not.log"; then
    fail "a part of the kernel's Gate, which is not partial, was indexed"
fi
grep -q "only as a part of a partial type" "$m/not.log" || { cat "$m/not.log"; fail "the refusal of Gate does not say why"; }
for shape in FIELD:"an instance field" VIRTUAL:"a virtual" INIT:"a static field" STATIC:"a static field" CCTOR:"a static constructor" AUTO:"an instance auto-property"; do
    symbol=${shape%%:*}; said=${shape#*:}
    if "$corc" index --assembly Kernel --ring 0 -D "$symbol" --on "$k/kernel.idx" "$here/Refused.cor" -o "$m/$symbol.idx" 2> "$m/$symbol.log"; then
        fail "a part declaring $said ($symbol) was indexed"
    fi
    grep -q "$said" "$m/$symbol.log" || { cat "$m/$symbol.log"; fail "the $symbol refusal does not say why"; }
done
# And the part with none of those is indexed.
"$corc" index --assembly Kernel --ring 0 --on "$k/kernel.idx" "$here/Refused.cor" -o "$m/plain.idx" 2> "$m/plain.log" \
    || { cat "$m/plain.log"; fail "a plain part was refused"; }

echo "PASS module-partial"
