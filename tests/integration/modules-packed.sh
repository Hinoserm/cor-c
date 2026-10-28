#!/bin/sh
# PACKED CODE ACROSS IMAGES: a module whose loop calls the kernel's packed
# routine keeps the processor's state once around the loop, and each call is
# to the routine's copy that runs inside its caller's state ($inside), which
# the kernel exports beside it -- not to the routine itself, which would save
# and give back the state on every call (Lowering.InsideElsewhere,
# Binder.KeepsStateElsewhere). Then a kernel of the same declarations whose
# routine has no copy: the module's link calls the routine itself instead
# (Linker.InsideOrItself), and runs the same. And within the kernel, a unit calling another unit's
# packed routine in a loop: compiled apart, it learns of the copy from the
# project build (ProjectCompile's InsideRounds), and keeps the state there.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/modules-packed.XXXXXX")
here=tests/integration/modules
cpu="--cpu=athlon64 --disable-3dnow"

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

# The kernel, as modules.sh builds one, with the packed routine in it.
a=$work/a
mkdir -p "$a"
runtime=$("$corc" library-sources --freestanding)
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 $here/Host.cor $here/Kernel.cor $here/Loader.cor $here/Pixels.cor $here/Shade.cor $runtime -o "$a/kernel.idx" 2> "$a/index.log"
: > "$a/units.tsv"
for source in $here/Host.cor $here/Kernel.cor $here/Loader.cor $here/Pixels.cor $here/Shade.cor $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$a/$name.o" "$a/$name.o.deps" "$role" >> "$a/units.tsv"
done
# shellcheck disable=SC2086
"$corc" compile-project --units "$a/units.tsv" --nostdlib --obj --freestanding --ring 0 $cpu --packed-report \
    --decl-index "$a/kernel.idx" --assembly Kernel 2> "$a/units.log" || { cat "$a/units.log"; fail "kernel units"; }

# BETWEEN THE KERNEL'S UNITS: Shade's loop learnt of Pixels' copy in a
# round of its own, keeps the state, and calls the copy.
grep -q "^inside facts round 1: " "$a/units.log" || { grep "inside facts\|^unit " "$a/units.log"; fail "no round gave Shade its neighbour's copy"; }
grep -qF "packed: 'Shade.Run(Byte[] bytes, Int32 rounds)' keeps the processor's state (calls 'Pixels.Brighten' in a loop)" "$a/units.log" \
    || { grep "packed:" "$a/units.log"; fail "Shade's loop does not keep the state for its neighbour's packed routine"; }
readelf -s -W "$a/Shade.o" | awk '$7 == "UND" { print $8 }' | grep -q 'Pixels_Brighten.*\$inside$' || fail "Shade does not call the copy"
# And built again at once: every unit current, no round.
# shellcheck disable=SC2086
"$corc" compile-project --units "$a/units.tsv" --nostdlib --obj --freestanding --ring 0 $cpu --packed-report \
    --decl-index "$a/kernel.idx" --assembly Kernel 2> "$a/again.log" || { cat "$a/again.log"; fail "kernel units again"; }
if grep -q "^inside facts round\|rebuilt:" "$a/again.log"; then grep "inside facts\|rebuilt:" "$a/again.log"; fail "a build with nothing changed compiled again"; fi
objects=$(cut -f2 "$a/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects -o "$a/kernel" --exports "$a/kernel.exports" --decl-index "$a/kernel.idx" $cpu 2> "$a/link.log" \
    || { cat "$a/link.log"; fail "kernel link"; }

# THE KERNEL EXPORTS THE ROUTINE AND ITS COPY that runs inside.
readelf --dyn-syms -W "$a/kernel.exports" | awk '$8 != "" { print $8 }' > "$work/exported"
routine=$(grep -E '^m_Kernel\$Pixels_Brighten_' "$work/exported" | grep -v '\$inside$' | head -1)
test -n "$routine" || fail "the kernel does not export Pixels.Brighten"
grep -qxF "$routine\$inside" "$work/exported" || fail "the kernel does not export Pixels.Brighten's \$inside copy"

# THE MODULE KEEPS THE STATE ROUND ITS LOOP, and calls the copy.
# shellcheck disable=SC2086
"$corc" compile --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 $cpu --packed-report \
    $here/Blit.cor --obj -o "$work/blit.o" 2> "$work/blit.log" || { cat "$work/blit.log"; fail "module object"; }
grep -qF "packed: 'Blitter.Bind(Int32 port)' keeps the processor's state (calls 'Pixels.Brighten' in a loop)" "$work/blit.log" \
    || { cat "$work/blit.log"; fail "the module's loop does not keep the state for the kernel's packed routine"; }
# shellcheck disable=SC2086
"$corc" link "$work/blit.o" --kernel "$a/kernel.exports" $cpu -o "$work/blit.ko" 2> "$work/blit-link.log" \
    || { cat "$work/blit-link.log"; fail "module link"; }
readelf --dyn-syms -W "$work/blit.ko" | awk '$7 == "UND" && $8 != "" { print $8 }' | sort -u > "$work/imports"
grep -qxF "$routine\$inside" "$work/imports" || { cat "$work/imports"; fail "the module does not call the routine's \$inside copy"; }
if grep -qxF "$routine" "$work/imports"; then fail "the module calls the routine itself inside its kept state"; fi
cp "$work/blit.ko" "$a/module.ko"
(cd "$a" && ./kernel) > "$work/run.out" 2>&1 || { cat "$work/run.out"; fail "the kernel did not load the module"; }
grep -qx 'blit 11732' "$work/run.out" && grep -qx 'shade 12080' "$work/run.out" && grep -qx 'bind 11732' "$work/run.out" \
    || { cat "$work/run.out"; fail "the module's blit came out wrong"; }

# A KERNEL WITHOUT THE COPY: the same declarations, so the same stamp, its
# Pixels.Brighten plain code that keeps no state. The module object compiled
# against the first kernel names the copy; its link here calls the routine
# itself, and it runs the same.
b=$work/plain
mkdir -p "$b"
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 $here/Host.cor $here/Kernel.cor $here/Loader.cor $here/PixelsPlain.cor $here/Shade.cor $runtime -o "$b/kernel.idx" 2> "$b/index.log"
: > "$b/units.tsv"
for source in $here/Host.cor $here/Kernel.cor $here/Loader.cor $here/PixelsPlain.cor $here/Shade.cor $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$b/$name.o" "$b/$name.o.deps" "$role" >> "$b/units.tsv"
done
# shellcheck disable=SC2086
"$corc" compile-project --units "$b/units.tsv" --nostdlib --obj --freestanding --ring 0 $cpu \
    --decl-index "$b/kernel.idx" --assembly Kernel 2> "$b/units.log" || { cat "$b/units.log"; fail "plain kernel units"; }
plainObjects=$(cut -f2 "$b/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $plainObjects -o "$b/kernel" --exports "$b/kernel.exports" --decl-index "$b/kernel.idx" $cpu 2> "$b/link.log" \
    || { cat "$b/link.log"; fail "plain kernel link"; }
test "$(readelf -x .corsac.stamp "$b/kernel.exports" | tail -n +3)" = "$(readelf -x .corsac.stamp "$a/kernel.exports" | tail -n +3)" \
    || fail "the plain kernel's declarations are not the first's"
if readelf --dyn-syms -W "$b/kernel.exports" | awk '$8 != "" { print $8 }' | grep -qxF "$routine\$inside"; then
    fail "the plain kernel has a copy of a routine that keeps no state"
fi
# shellcheck disable=SC2086
"$corc" link "$work/blit.o" --kernel "$b/kernel.exports" $cpu -o "$b/blit.ko" 2> "$b/blit-link.log" \
    || { cat "$b/blit-link.log"; fail "the module did not link against the kernel without the copy"; }
readelf --dyn-syms -W "$b/blit.ko" | awk '$7 == "UND" && $8 != "" { print $8 }' | sort -u > "$b/imports"
grep -qxF "$routine" "$b/imports" || { cat "$b/imports"; fail "the module does not call the routine itself"; }
if grep -qxF "$routine\$inside" "$b/imports"; then fail "the module still names the copy the kernel does not have"; fi
cp "$b/blit.ko" "$b/module.ko"
(cd "$b" && ./kernel) > "$b/run.out" 2>&1 || { cat "$b/run.out"; fail "the plain kernel did not load the module"; }
cmp -s "$work/run.out" "$b/run.out" || { cat "$b/run.out"; fail "the module ran otherwise on the routine itself"; }

echo "PASS modules-packed"
