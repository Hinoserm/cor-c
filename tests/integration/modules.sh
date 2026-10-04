#!/bin/sh
# KERNEL MODULES (docs/software/DRIVERS.md in the OS repository): a
# freestanding kernel linked with --exports, a module compiled against its
# declaration index and linked against those exports, and the kernel loading
# the module the way its binder binds a library -- built in rings, stamped,
# with its aliases in .corsac.modinfo. Then a kernel whose declarations
# differ, whose stamp the module does not carry, refusing it.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/modules.XXXXXX")
here=tests/integration/modules

fail() { printf 'FAIL %s\n' "$1"; exit 1; }

# A kernel: Host.cor, Kernel.cor, Loader.cor and the freestanding runtime,
# indexed and compiled per unit for ring 0, as CORSAC builds its kernel.
kernel() {
    out=$1; shift
    mkdir -p "$out"
    runtime=$("$corc" library-sources --freestanding)
    # shellcheck disable=SC2086
    "$corc" index --assembly Kernel --ring 0 "$@" $runtime -o "$out/kernel.idx" 2> "$out/index.log"
    : > "$out/units.tsv"
    for source in "$@" $runtime; do
        role=lib; case $source in */Kernel.cor) role=entry ;; esac
        name=$(basename "$source" .cor)
        printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$out/$name.o" "$out/$name.o.deps" "$role" >> "$out/units.tsv"
    done
    "$corc" compile-project --units "$out/units.tsv" --nostdlib --obj --freestanding --ring 0 \
        --decl-index "$out/kernel.idx" --assembly Kernel 2> "$out/units.log" || { cat "$out/units.log"; fail "kernel units"; }
    objects=$(cut -f2 "$out/units.tsv" | tr '\n' ' ')
    # shellcheck disable=SC2086
    "$corc" link $objects -o "$out/kernel" --exports "$out/kernel.exports" --decl-index "$out/kernel.idx" 2> "$out/link.log" \
        || { cat "$out/link.log"; fail "kernel link"; }
}

# The stamp a linked file carries, in hex.
stamp() { readelf -x .corsac.stamp "$1" | awk '/^  0x/ { for (i = 2; i <= 5; i++) printf "%s", $i }'; }

kernel "$work/a" $here/Host.cor $here/Kernel.cor $here/Loader.cor
a=$work/a

# THE KERNEL AS A LIBRARY: stamped twice, in a note and in .rodata, and its
# exports every global but the per-image ones, at the kernel's addresses.
test -n "$(stamp "$a/kernel")" || fail "the kernel carries no stamp"
test "$(stamp "$a/kernel")" = "$(stamp "$a/kernel.exports")" || fail "the exports' stamp is not the kernel's"
readelf -s -W "$a/kernel" | awk '$8 == "__corsac_build_stamp" && $3 == 32 { found = 1 } END { exit !found }' \
    || fail "no __corsac_build_stamp in the kernel"
readelf -h "$a/kernel.exports" | grep -q 'DYN (Shared object file)' || fail "the exports are not a shared object"
readelf --dyn-syms -W "$a/kernel.exports" > "$work/exports.syms"
for name in 'm_Kernel$Drivers_Register_1_T$Kernel$002eDriver' 't_Kernel$002eDriver' 'm_Runtime_BeginImage_3_V$I64_V$I64_V$I64' __corsac_build_stamp; do
    awk -v n="$name" '$8 == n && $7 == "ABS" { found = 1 } END { exit !found }' "$work/exports.syms" || fail "the kernel does not export $name"
done
kernelAddress=$(readelf -s -W "$a/kernel" | awk '$8 == "m_Kernel$Log_Write_1_V$String" { print $2 }')
exportAddress=$(awk '$8 == "m_Kernel$Log_Write_1_V$String" { print $2 }' "$work/exports.syms")
test -n "$kernelAddress" && test "$kernelAddress" = "$exportAddress" || fail "an export is not where the kernel has it"
for name in _start __data_start _end __corsac_units; do
    if awk -v n="$name" '$8 == n { found = 1 } END { exit !found }' "$work/exports.syms"; then fail "the kernel exports its own $name"; fi
done
readelf -d "$a/kernel.exports" | grep -q 'Library soname: \[kernel\]' || fail "the exports do not name the kernel"
if readelf -s -W "$a/kernel" | grep -q 'Seat'; then fail "a [Ring1] class is in the ring-0 kernel"; fi

# A MODULE, compiled whole, and the same compiled to an object and linked.
"$corc" compile --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor -o "$work/sb16.ko" 2> "$work/sb16.log" || { cat "$work/sb16.log"; fail "module compile"; }
"$corc" compile --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj -o "$work/sb16.o" 2> "$work/sb16-object.log" || { cat "$work/sb16-object.log"; fail "module object"; }
mkdir -p "$work/linked"
"$corc" link "$work/sb16.o" --kernel "$a/kernel.exports" -o "$work/linked/sb16.ko" 2> "$work/sb16-link.log" \
    || { cat "$work/sb16-link.log"; fail "module link"; }

for ko in "$work/sb16.ko" "$work/linked/sb16.ko"; do
    readelf -h "$ko" | grep -q 'DYN (Shared object file)' || fail "$ko is not a shared object"
    readelf -d "$ko" > "$ko.dynamic"
    if grep -q 'NEEDED\|TEXTREL' "$ko.dynamic"; then fail "$ko needs a library or relocates its text"; fi
    grep -q '(HASH)' "$ko.dynamic" || fail "$ko has no DT_HASH"
    # Only what a library has: no COPY, no PC32, no TLS -- and none in text.
    readelf -r -W "$ko" | awk '/^[0-9a-f]+ / { print $3 }' | sort -u > "$ko.kinds"
    if grep -v -x -e R_386_RELATIVE -e R_386_32 -e R_386_GLOB_DAT -e R_386_JMP_SLOT "$ko.kinds"; then fail "$ko has a relocation the binder does not apply"; fi
    writable=$(readelf -l -W "$ko" | awk '$1 == "LOAD" && $7 ~ /W/ { printf "%d %d\n", strtonum($3), strtonum($3) + strtonum($6) }')
    readelf -r -W "$ko" | awk -v range="$writable" 'BEGIN { split(range, r, " ") } /^[0-9a-f]+ / { at = strtonum("0x" $1); if (at < r[1] || at >= r[2]) bad = 1 } END { exit bad }' \
        || fail "$ko relocates outside its writable segment"
    # No runtime of its own: the kernel's collector, allocator and strings.
    readelf --dyn-syms -W "$ko" > "$ko.syms"
    if awk '$7 != "UND" && $8 ~ /^m_(Runtime|Gc|String)_/ { found = 1 } END { exit !found }' "$ko.syms"; then fail "$ko carries a runtime"; fi
    for name in 'm_Kernel$Drivers_Register_1_T$Kernel$002eDriver' 'm_Runtime_BeginImage_3_V$I64_V$I64_V$I64' 't_Kernel$002eDriver'; do
        awk -v n="$name" '$8 == n && $7 == "UND" { found = 1 } END { exit !found }' "$ko.syms" || fail "$ko does not import $name"
    done
    # DT_INIT is the link's: __corsac_init, then the [ModuleInitializer].
    init=$(awk '/(INIT)/ { print strtonum($3) }' "$ko.dynamic")
    stub=$(readelf --dyn-syms -W "$ko" | awk '$8 == "__corsac_module_init" { print strtonum("0x" $2); exit }')
    test -n "$init" && test "$init" = "$stub" || fail "$ko: DT_INIT is not its initialisers"
    test "$(stamp "$ko")" = "$(stamp "$a/kernel")" || fail "$ko does not carry the kernel's stamp"
    readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
    printf '%s\n' name=sb16 ring=0 alias=pnp:CTL0031 alias=isa:sb16@220,240,260,280 depends=mpu401 | cmp -s - "$ko.modinfo" \
        || { cat "$ko.modinfo"; fail "$ko: .corsac.modinfo"; }
    if readelf -s -W "$ko" | grep -q 'Sb16Mixer'; then fail "$ko holds its [Ring1] half"; fi
done

# THE KERNEL LOADS IT: stamp checked, mapped, bound against the exports,
# DT_INIT run -- the module's statics roots, its driver registered.
cp "$work/linked/sb16.ko" "$a/module.ko"
(cd "$a" && ./kernel) > "$work/run.out" 2>&1 || { cat "$work/run.out"; fail "the kernel did not load the module"; }
printf '%s\n' 'kernel 0' 'registered sb16' 'modinfo name=sb16' 'modinfo ring=0' 'modinfo alias=pnp:CTL0031' \
    'modinfo alias=isa:sb16@220,240,260,280' 'modinfo depends=mpu401' 'drivers 1' 'sb16 at 544, loaded 1 time' 'bind 1' \
    | cmp -s - "$work/run.out" || { cat "$work/run.out"; fail "the module ran other than it should"; }

# A KERNEL WHOSE DECLARATIONS DIFFER -- one more field in Driver -- has
# another stamp; the module built for the first is refused by its loader,
# by the link, and its index refused by the compile.
mkdir -p "$work/b"
sed '/^public abstract class Driver/{n;s/^{$/{\n    public int Generation;/}' $here/Kernel.cor > "$work/b/Kernel.cor"
grep -q 'public int Generation;' "$work/b/Kernel.cor" || fail "kernel b is not different"
kernel "$work/b" $here/Host.cor "$work/b/Kernel.cor" $here/Loader.cor
b=$work/b
test "$(stamp "$b/kernel")" != "$(stamp "$a/kernel")" || fail "two kernels with different declarations share a stamp"
cp "$work/linked/sb16.ko" "$b/module.ko"
status=0
(cd "$b" && ./kernel) > "$work/refused.out" 2>&1 || status=$?
test "$status" = 3 && grep -qx 'refused: built for another kernel' "$work/refused.out" \
    || { cat "$work/refused.out"; fail "a module of another kernel was loaded"; }
if "$corc" link "$work/sb16.o" --kernel "$b/kernel.exports" -o "$work/wrong.ko" 2> "$work/wrong-link.log"; then fail "a module of another kernel linked"; fi
grep -q 'compiled against the declarations of another kernel' "$work/wrong-link.log" || { cat "$work/wrong-link.log"; fail "the wrong link said otherwise"; }
if "$corc" compile --kernel "$b/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel $here/Sb16.cor -o "$work/wrong.ko" 2> "$work/wrong-compile.log"; then
    fail "a module compiled against another kernel's index"
fi
grep -q 'is not the index kernel was compiled against' "$work/wrong-compile.log" || { cat "$work/wrong-compile.log"; fail "the wrong compile said otherwise"; }
test ! -e "$work/wrong.ko"

printf 'PASS kernel modules: %s\n' "$work"
