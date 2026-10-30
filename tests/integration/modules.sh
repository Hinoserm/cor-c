#!/bin/sh
# KERNEL MODULES (docs/software/DRIVERS.md in the OS repository): a
# freestanding kernel linked with --exports, its export table a section of
# its own file no segment loads; a module compiled against its declaration
# index and linked against that table, and the kernel loading the module the
# way its binder binds a library, the table read from the kernel's file --
# built in rings, with its aliases in .corsac.modinfo; and a module of
# several files, a unit a file against its own index made on the kernel's.
# Then a kernel whose method a module calls has another signature, and one
# whose Driver has a field more -- its layout's name another (LayoutNames)
# -- each refusing the module by that name, its loader and the link; and one
# changed only where no module looks, which loads it.
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
    # Its own: the several-file module's units, listed before kernel b is
    # made, are what kernel b's wrong link is handed.
    local objects
    objects=$(cut -f2 "$out/units.tsv" | tr '\n' ' ')
    # shellcheck disable=SC2086
    "$corc" link $objects -o "$out/kernel" --exports 2> "$out/link.log" \
        || { cat "$out/link.log"; fail "kernel link"; }
}

# A kernel's exports, "name address" a line, read from its file's .corsac.exports.
exports() { python3 -I "$root/$here/exports.py" "$1"; }

kernel "$work/a" $here/Host.cor $here/Kernel.cor $here/Loader.cor
a=$work/a

# THE KERNEL AS A LIBRARY: its export table a section of its own file,
# outside every loaded segment, and in it every global but the per-image
# ones, at the kernel's addresses. No stamp anywhere.
section=$(readelf -S -W "$a/kernel" | sed 's/\[ */[/' | awk '$2 == ".corsac.exports" { print strtonum("0x" $5), strtonum("0x" $6) }')
test -n "$section" || fail "the kernel carries no export table"
readelf -l -W "$a/kernel" | awk -v s="$section" 'BEGIN { split(s, t, " ") } $1 == "LOAD" { o = strtonum($2); n = strtonum($5); if (t[1] < o + n && t[1] + t[2] > o) bad = 1 } END { exit bad }' \
    || fail "the kernel's export table is loaded"
if readelf -s -W "$a/kernel" | grep -q '__corsac_build_stamp'; then fail "the kernel carries a build stamp"; fi
python3 -I "$root/$here/exports.py" "$a/kernel" --identity | grep -qx 'identity ok' \
    || fail "the kernel's link identity is not its loaded bytes' hash, or its copies differ"
exports "$a/kernel" > "$work/exports.syms"
for name in 'm_Kernel$Drivers_Register_1_T$Kernel$002eDriver' 't_Kernel$002eDriver' 'm_Runtime_BeginImage_3_V$NUInt_V$NUInt_V$NUInt'; do
    awk -v n="$name" '$1 == n { found = 1 } END { exit !found }' "$work/exports.syms" || fail "the kernel does not export $name"
done
kernelAddress=$(readelf -s -W "$a/kernel" | awk '$8 == "m_Kernel$Log_Write_1_V$String" { print $2 }')
exportAddress=$(awk '$1 == "m_Kernel$Log_Write_1_V$String" { print $2 }' "$work/exports.syms")
test -n "$kernelAddress" && test "$kernelAddress" = "$exportAddress" || fail "an export is not where the kernel has it"
for name in _start __data_start _end __corsac_units; do
    if awk -v n="$name" '$1 == n { found = 1 } END { exit !found }' "$work/exports.syms"; then fail "the kernel exports its own $name"; fi
done
if readelf -s -W "$a/kernel" | grep -q 'Seat'; then fail "a [Ring1] class is in the ring-0 kernel"; fi

# A MODULE, compiled whole, and the same compiled to an object and linked.
"$corc" compile --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor -o "$work/sb16.ko" 2> "$work/sb16.log" || { cat "$work/sb16.log"; fail "module compile"; }
"$corc" compile --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj -o "$work/sb16.o" 2> "$work/sb16-object.log" || { cat "$work/sb16-object.log"; fail "module object"; }
mkdir -p "$work/linked"
"$corc" link "$work/sb16.o" --kernel "$a/kernel" -o "$work/linked/sb16.ko" 2> "$work/sb16-link.log" \
    || { cat "$work/sb16-link.log"; fail "module link"; }

# And as the image builder builds one: its units in one compile-project.
mkdir -p "$work/project"
printf '%s\t%s\t%s\tentry\n' "$(realpath $here/Sb16.cor)" "$work/project/sb16.o" "$work/project/sb16.o.deps" > "$work/project/units.tsv"
"$corc" compile-project --units "$work/project/units.tsv" --obj --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    2> "$work/project/units.log" || { cat "$work/project/units.log"; fail "module units"; }
"$corc" link "$work/project/sb16.o" --kernel "$a/kernel" -o "$work/project/sb16.ko" 2> "$work/project/link.log" \
    || { cat "$work/project/link.log"; fail "module units' link"; }

# NO KERNEL BODY IS COPIED INTO A MODULE, however small: Tick.Twice is
# called through the kernel's name, which binds or refuses the module.
for ko in "$work/sb16.ko" "$work/linked/sb16.ko" "$work/project/sb16.ko"; do
    readelf --dyn-syms -W "$ko" | awk '$7 == "UND" && $8 ~ /Tick_Twice/ { found = 1 } END { exit !found }' \
        || fail "$ko does not call the kernel's Tick.Twice"
done
for ko in "$work/sb16.ko" "$work/linked/sb16.ko" "$work/project/sb16.ko"; do
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
    for name in 'm_Kernel$Drivers_Register_1_T$Kernel$002eDriver' 'm_Runtime_BeginImage_3_V$NUInt_V$NUInt_V$NUInt' 't_Kernel$002eDriver'; do
        awk -v n="$name" '$8 == n && $7 == "UND" { found = 1 } END { exit !found }' "$ko.syms" || fail "$ko does not import $name"
    done
    # AND THE LAYOUT IT WAS COMPILED AGAINST, by name: the kernel's Driver's.
    driverShape=$(awk '$1 ~ /^z_Kernel\$002eDriver\$/ { print $1 }' "$work/exports.syms")
    test -n "$driverShape" || fail "the kernel does not export its Driver's layout"
    awk -v n="$driverShape" '$8 == n && $7 == "UND" { found = 1 } END { exit !found }' "$ko.syms" || fail "$ko does not import the kernel's Driver's layout"
    # DT_INIT is the link's: __corsac_init, then the [ModuleInitializer].
    init=$(awk '/(INIT)/ { print strtonum($3) }' "$ko.dynamic")
    stub=$(readelf --dyn-syms -W "$ko" | awk '$8 == "__corsac_module_init" { print strtonum("0x" $2); exit }')
    test -n "$init" && test "$init" = "$stub" || fail "$ko: DT_INIT is not its initialisers"
    if readelf -S -W "$ko" | grep -q '\.corsac\.\(stamp\|reads\|moduleunit\)'; then fail "$ko carries more than its modinfo of the build"; fi
    readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
    printf '%s\n' name=sb16 ring=0 alias=pnp:CTL0031 alias=isa:sb16@220,240,260,280 depends=mpu401 | cmp -s - "$ko.modinfo" \
        || { cat "$ko.modinfo"; fail "$ko: .corsac.modinfo"; }
    if readelf -s -W "$ko" | grep -q 'Sb16Mixer'; then fail "$ko holds its [Ring1] half"; fi
done

# THE KERNEL LOADS IT: mapped, bound against the table in the kernel's own
# file, DT_INIT run -- the module's statics roots, its driver registered.
cp "$work/linked/sb16.ko" "$a/module.ko"
(cd "$a" && ./kernel) > "$work/run.out" 2>&1 || { cat "$work/run.out"; fail "the kernel did not load the module"; }
printf '%s\n' 'kernel 0' 'registered sb16' 'modinfo name=sb16' 'modinfo ring=0' 'modinfo alias=pnp:CTL0031' \
    'modinfo alias=isa:sb16@220,240,260,280' 'modinfo depends=mpu401' 'drivers 1' 'sb16 at 544, loaded 1 time' 'bind 1' \
    | cmp -s - "$work/run.out" || { cat "$work/run.out"; fail "the module ran other than it should"; }

# RELOCATED TEXT (--relocated-text): the same module without position-
# independent code -- no GOT register found by a call and a pop, the kernel's
# functions called directly -- its text fixed by the binder (R_386_PC32 for
# the calls), and running as the other does.
mkdir -p "$work/relocated"
"$corc" compile --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj --relocated-text -o "$work/relocated/sb16.o" 2> "$work/relocated/compile.log" || { cat "$work/relocated/compile.log"; fail "relocated module object"; }
"$corc" link "$work/relocated/sb16.o" --kernel "$a/kernel" --relocated-text -o "$work/relocated/sb16.ko" 2> "$work/relocated/link.log" \
    || { cat "$work/relocated/link.log"; fail "relocated module link"; }
readelf -r "$work/relocated/sb16.ko" | grep -q R_386_PC32 || fail "the relocated module calls the kernel through no PC32 relocation"
readelf -d "$work/relocated/sb16.ko" | grep -q TEXTREL || fail "the relocated module does not say it relocates its text"
if objdump -d "$work/relocated/sb16.ko" | grep -A1 'pop *%ebx' | grep -q 'add *\$0x.*,%ebx'; then fail "the relocated module still finds a GOT"; fi
cp "$work/relocated/sb16.ko" "$a/module.ko"
(cd "$a" && ./kernel) > "$work/relocated/run.out" 2>&1 || { cat "$work/relocated/run.out"; fail "the kernel did not load the relocated module"; }
cmp -s "$work/run.out" "$work/relocated/run.out" || { cat "$work/relocated/run.out"; fail "the relocated module ran other than the other"; }
cp "$work/linked/sb16.ko" "$a/module.ko"

# PRUNED WITH --keep: the kernel linked again keeping only what it and the
# module reach -- the module's imports the roots beside the entry -- drops
# what nobody calls, keeps every import, and loads and runs the module the
# same, bound by name against the pruned kernel's table.
readelf -s -W "$a/kernel" | grep -q 'Unused_Never' || fail "the open kernel dropped what a module might call"
readelf --dyn-syms -W "$work/linked/sb16.ko" | awk '$7 == "UND" && $8 != "" { print $8 }' | sort -u > "$work/keep"
mkdir -p "$work/pruned"
objects=$(cut -f2 "$a/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects -o "$work/pruned/kernel" --exports --keep "$work/keep" \
    2> "$work/pruned/link.log" || { cat "$work/pruned/link.log"; fail "the kernel did not link with --keep"; }
if readelf -s -W "$work/pruned/kernel" | grep -q 'Unused_Never'; then fail "the pruned kernel kept what nothing reaches"; fi
test "$(stat -c %s "$work/pruned/kernel")" -lt "$(stat -c %s "$a/kernel")" || fail "the pruned kernel is no smaller"
exports "$work/pruned/kernel" | awk '{ print $1 }' | sort -u > "$work/pruned/exported"
comm -23 "$work/keep" "$work/pruned/exported" > "$work/pruned/missing"
test ! -s "$work/pruned/missing" || { cat "$work/pruned/missing"; fail "the pruned kernel does not export what the module imports"; }
cp "$work/linked/sb16.ko" "$work/pruned/module.ko"
(cd "$work/pruned" && ./kernel) > "$work/pruned/run.out" 2>&1 || { cat "$work/pruned/run.out"; fail "the pruned kernel did not load the module"; }
cmp -s "$work/run.out" "$work/pruned/run.out" || { cat "$work/pruned/run.out"; fail "the module ran otherwise in the pruned kernel"; }
if "$corc" link $objects -o "$work/pruned/wrong" --keep "$work/keep" 2> "$work/pruned/wrong.log"; then fail "--keep linked without --exports"; fi

# A MODULE OF SEVERAL FILES, built as the image builder builds every module:
# its own index made on the kernel's, a unit a file in one compile-project
# against both indexes, and the units
# linked, and optimised, together. Each unit sees the others' types only
# through the module's index; the kernel loads it and calls through a
# kernel interface into it, which the module's own interface, sorting
# before, must not have moved.
# Where a method is in an object's method table: the offset from the
# table's symbol to the relocation that names the method, in .data.rel.ro.
table_offset() {
    local at
    at=$(readelf -s -W "$1" | awk -v n="$2" '$8 == n && $7 != "UND" { print $2; exit }')
    test -n "$at" || return 0
    readelf -r -W "$1" | awk -v at="$at" -v m="$3" '/^Relocation section/ { s = ($0 ~ /\.rel\.data\.rel\.ro. /) }
        s && $5 ~ m { print strtonum("0x" $1) - strtonum("0x" at); exit }'
}
m=$work/units
mkdir -p "$m"
mixer="$here/MixerAudio.cor $here/MixerDriver.cor $here/MixerEntry.cor"
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 --on "$a/kernel.idx" $mixer -o "$m/module.idx" 2> "$m/index.log" \
    || { cat "$m/index.log"; fail "the module's index"; }
: > "$m/units.tsv"
for source in $mixer; do
    role=lib; case $source in */MixerEntry.cor) role=entry ;; esac
    name=$(basename "$source" .cor)
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$m/$name.o" "$m/$name.o.deps" "$role" >> "$m/units.tsv"
done
"$corc" compile-project --units "$m/units.tsv" --obj --kernel "$a/kernel" --decl-index "$a/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/units.log" || { cat "$m/units.log"; fail "the module's units"; }
for name in MixerAudio MixerDriver MixerEntry; do
    test -s "$m/$name.o" || fail "no unit $name"
    readelf -S -W "$m/$name.o" | grep -q '\.corsac\.moduleunit' || fail "unit $name is not marked a module's"
done
# Each type defined by its own unit alone; the others name it.
readelf -s -W "$m/MixerAudio.o" | awk '$8 == "t_Sound$002eChannel" && $7 != "UND" { found = 1 } END { exit !found }' \
    || fail "the unit that declares Channel does not define it"
if readelf -s -W "$m/MixerEntry.o" | awk '$8 == "t_Sound$002eChannel" && $7 != "UND" { found = 1 } END { exit !found }'; then
    fail "the entry unit defines Channel, which another unit declares"
fi
objects=$(cut -f2 "$m/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects --kernel "$a/kernel" -o "$m/mixer.ko" 2> "$m/link.log" || { cat "$m/link.log"; fail "the module's units' link"; }
ko=$m/mixer.ko
readelf -h "$ko" | grep -q 'DYN (Shared object file)' || fail "$ko is not a shared object"
readelf -d "$ko" > "$ko.dynamic"
if grep -q 'NEEDED\|TEXTREL' "$ko.dynamic"; then fail "$ko needs a library or relocates its text"; fi
readelf -r -W "$ko" | awk '/^[0-9a-f]+ / { print $3 }' | sort -u > "$ko.kinds"
if grep -v -x -e R_386_RELATIVE -e R_386_32 -e R_386_GLOB_DAT -e R_386_JMP_SLOT "$ko.kinds"; then fail "$ko has a relocation the binder does not apply"; fi
readelf --dyn-syms -W "$ko" > "$ko.syms"
if awk '$7 != "UND" && $8 ~ /^m_(Runtime|Gc|String)_/ { found = 1 } END { exit !found }' "$ko.syms"; then fail "$ko carries a runtime"; fi
readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
printf '%s\n' name=mixer ring=0 alias=pnp:MIX0001 | cmp -s - "$ko.modinfo" || { cat "$ko.modinfo"; fail "$ko: .corsac.modinfo"; }
if readelf -s -W "$ko" | grep -q 'MixerPanel'; then fail "$ko holds its [Ring1] half"; fi
mkdir -p "$m/run"
cp "$a/kernel" "$m/run/"
cp "$ko" "$m/run/module.ko"
(cd "$m/run" && ./kernel) > "$m/run.out" 2>&1 || { cat "$m/run.out"; fail "the kernel did not load the several-file module"; }
printf '%s\n' 'kernel 0' 'registered mixer' 'modinfo name=mixer' 'modinfo ring=0' 'modinfo alias=pnp:MIX0001' 'drivers 1' \
    'probe 2' 'mixer at 544, master=7 wave=5' 'bind 12' \
    | cmp -s - "$m/run.out" || { cat "$m/run.out"; fail "the several-file module ran other than it should"; }
# THE SAME UNITS, TEXT RELOCATED (--relocated-text), as the image builder
# makes the GUI and NT modules: a literal is one object across the units
# (Lowering.SharedLiterals) -- each unit's copy of a text was the module's
# own until its link, a third of the NT module's read-only data -- and
# exported by none (KernelExports.PerImage).
rt=$m/relocated
mkdir -p "$rt/run"
sed "s|$m/|$rt/|g" "$m/units.tsv" > "$rt/units.tsv"
"$corc" compile-project --units "$rt/units.tsv" --obj --kernel "$a/kernel" --decl-index "$a/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 --relocated-text 2> "$rt/units.log" || { cat "$rt/units.log"; fail "the module's relocated units"; }
objects=$(cut -f2 "$rt/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects --kernel "$a/kernel" --relocated-text -o "$rt/mixer.ko" 2> "$rt/link.log" || { cat "$rt/link.log"; fail "the module's relocated units' link"; }
cp "$a/kernel" "$rt/run/"
cp "$rt/mixer.ko" "$rt/run/module.ko"
(cd "$rt/run" && ./kernel) > "$rt/run.out" 2>&1 || { cat "$rt/run.out"; fail "the kernel did not load the relocated several-file module"; }
cmp -s "$m/run.out" "$rt/run.out" || { cat "$rt/run.out"; fail "the relocated several-file module ran other than the other"; }
# Every unit names its literals by their text: one definition each in the link.
for unit in $objects; do readelf -s -W "$unit" | awk '$8 ~ /^str_/ && $7 != "UND" { print $8 }'; done | sort | uniq -d > "$rt/shared"
test -s "$rt/shared" || fail "no literal is in two of the relocated units: nothing shows the sharing"
readelf -s -W "$rt/mixer.ko" | awk '$8 ~ /^str_/ && $7 != "UND" { print $8 }' | sort | uniq -d > "$rt/twice"
test ! -s "$rt/twice" || { head "$rt/twice"; fail "a literal is defined twice in the relocated module"; }
if readelf --dyn-syms -W "$rt/mixer.ko" | awk '$8 ~ /^(str_|__corsac_unit|__corsac_frame|__corsac_stackmaps)/ { found = 1 } END { exit !found }'; then
    fail "the relocated module exports what is its own"
fi
# Its relocations on pages of their own, which the binder can give back:
# .rel.dyn starts a page, and .text starts the page after its end.
readelf -S -W "$rt/mixer.ko" | sed 's/\[ */[/' | awk '$2 == ".rel.dyn" || $2 == ".text" { if (strtonum("0x" $4) % 4096 != 0) bad = 1; n++ } END { exit bad || n != 2 }' \
    || fail "the relocated module's relocations do not have pages of their own"

# A module may not declare a type of its kernel's: its index is refused.
if "$corc" index --assembly Kernel --ring 0 --on "$a/kernel.idx" $here/KernelType.cor -o "$m/wrong.idx" 2> "$m/wrong-index.log"; then
    fail "a module's index declared a type of its kernel's"
fi
grep -q 'a module may not declare a type of its kernel' "$m/wrong-index.log" || { cat "$m/wrong-index.log"; fail "the wrong index said otherwise"; }

# A MODULE ON A MODULE: Chorus's index made on the mixer's, itself made on
# the kernel's (a chain), its units compiled against the chain, and linked
# against the kernel's exports
# and the mixer's file. It imports the mixer's names as it does the
# kernel's, needs and depends on the mixer, and defines none of its types.
c=$work/chorus
mkdir -p "$c"
"$corc" index --assembly Kernel --ring 0 --on "$m/module.idx" $here/Chorus.cor -o "$c/chorus.idx" 2> "$c/index.log" \
    || { cat "$c/index.log"; fail "an index on a module's index"; }
printf '%s\t%s\t%s\tentry\n' "$(realpath $here/Chorus.cor)" "$c/Chorus.o" "$c/Chorus.o.deps" > "$c/units.tsv"
"$corc" compile-project --units "$c/units.tsv" --obj --kernel "$a/kernel" --decl-index "$a/kernel.idx" \
    --module-index "$c/chorus.idx" --assembly Kernel --ring 0 2> "$c/units.log" || { cat "$c/units.log"; fail "a module's units on a module"; }
readelf -S -W "$c/Chorus.o" | grep -q '\.corsac\.moduleunit' || fail "the module on a module is not marked a module's"
"$corc" link "$c/Chorus.o" --kernel "$a/kernel" --link-shared "$m/mixer.ko" -o "$c/chorus.ko" 2> "$c/link.log" \
    || { cat "$c/link.log"; fail "a module linked against a module"; }
ko=$c/chorus.ko
readelf -d "$ko" | grep -q 'Shared library: \[mixer.ko\]' || fail "$ko does not need the mixer"
readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
grep -qx 'depends=mixer' "$ko.modinfo" || { cat "$ko.modinfo"; fail "$ko does not depend on the mixer"; }
readelf --dyn-syms -W "$ko" > "$ko.syms"
# The kernel's Log.Write is called by its name, never copied in.
for name in 't_Sound$002eChannel' 'm_Sound$Channels_Total_0' 'm_Kernel$Log_Write_1_V$String'; do
    awk -v n="$name" '$8 == n && $7 == "UND" { found = 1 } END { exit !found }' "$ko.syms" || fail "$ko does not import $name"
done
if readelf -s -W "$ko" | awk '$8 == "t_Sound$002eChannel" && $7 != "UND" { found = 1 } END { exit !found }'; then fail "$ko defines the mixer's Channel"; fi
readelf -s -W "$ko" | awk '$8 == "t_Chorus$002eVoice" && $7 != "UND" { found = 1 } END { exit !found }' || fail "$ko does not define its own Voice"
# ITS SLOTS ARE THE KERNEL'S: the kernel's Driver.Bind and IDevice.Probe
# sit in its Echo's method table where they sit in the mixer's Mixer.
for method in Bind Probe; do
    mixer_at=$(table_offset "$m/MixerDriver.o" 't_Sound$002eMixer' "^m_Sound.Mixer_${method}_")
    echo_at=$(table_offset "$c/Chorus.o" 't_Chorus$002eEcho' "^m_Chorus.Echo_${method}_")
    test -n "$mixer_at" && test "$mixer_at" = "$echo_at" || fail "$method is at ${echo_at:-nowhere} in a module on a module, at ${mixer_at:-nowhere} in the kernel's"
done
# Without the mixer's file its imports of the mixer are nobody's.
if "$corc" link "$c/Chorus.o" --kernel "$a/kernel" -o "$c/alone.ko" 2> "$c/alone.log"; then fail "a module on a module linked without it"; fi
grep -q "neither kernel nor a module this one depends on exports it" "$c/alone.log" || { cat "$c/alone.log"; fail "the link without the mixer said otherwise"; }
# A module on a module may not declare the other's type.
if "$corc" index --assembly Kernel --ring 0 --on "$m/module.idx" $here/MixerAudio.cor -o "$c/wrong.idx" 2> "$c/wrong-index.log"; then
    fail "an index on a module's declared a type of that module's"
fi
grep -q 'is declared by the index this one is made on' "$c/wrong-index.log" || { cat "$c/wrong-index.log"; fail "the wrong chained index said otherwise"; }

# RING 0'S FILE CALLS SERVED BY THE KERNEL (--kernel-syscalls): every
# Sys.Syscall of the module, of any arity, a call of the kernel's
# KernelFiles.Syscall with the missing arguments 0 -- no trap in it -- and
# each argument arriving, as the kernel's answer says.
f=$work/files
mkdir -p "$f/run"
"$corc" compile --kernel "$a/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 --kernel-syscalls \
    $here/FileCalls.cor --obj -o "$f/files.o" 2> "$f/compile.log" || { cat "$f/compile.log"; fail "a module compiled --kernel-syscalls"; }
"$corc" link "$f/files.o" --kernel "$a/kernel" -o "$f/files.ko" 2> "$f/link.log" || { cat "$f/link.log"; fail "the --kernel-syscalls module's link"; }
if objdump -d "$f/files.ko" | grep -q 'int *\$0x8[03]'; then fail "the --kernel-syscalls module still traps"; fi
cp "$a/kernel" "$f/run/"
cp "$f/files.ko" "$f/run/module.ko"
(cd "$f/run" && ./kernel) > "$f/run.out" 2>&1 || { cat "$f/run.out"; fail "the kernel did not load the --kernel-syscalls module"; }
grep -qx 'served 5031 6021' "$f/run.out" || { cat "$f/run.out"; fail "the kernel's file calls answered other than they should"; }
if "$corc" compile --kernel-syscalls $here/FileCalls.cor -o "$f/wrong" 2> "$f/wrong.log"; then fail "--kernel-syscalls compiled a program for Linux"; fi
grep -q 'is for ring-0 code' "$f/wrong.log" || { cat "$f/wrong.log"; fail "the wrong --kernel-syscalls said otherwise"; }

# A KERNEL WHOSE LOG.WRITE TAKES ANOTHER PARAMETER: the name every module
# calls is not in its table. The module built for the first is refused by
# its loader and by the link, naming it; a compile against it, linking, too.
mkdir -p "$work/b"
sed 's/^    public static void Write(string text)$/    public static void Write(string text, int level = 0)/' $here/Kernel.cor > "$work/b/Kernel.cor"
grep -q 'Write(string text, int level = 0)' "$work/b/Kernel.cor" || fail "kernel b is not different"
kernel "$work/b" $here/Host.cor "$work/b/Kernel.cor" $here/Loader.cor
b=$work/b
cp "$work/linked/sb16.ko" "$b/module.ko"
status=0
(cd "$b" && ./kernel) > "$work/refused.out" 2>&1 || status=$?
test "$status" = 3 && grep -qx 'refused: undefined symbol m_Kernel$Log_Write_1_V$String' "$work/refused.out" \
    || { cat "$work/refused.out"; fail "a module calling what the kernel no longer has was loaded"; }
if "$corc" link "$work/sb16.o" --kernel "$b/kernel" -o "$work/wrong.ko" 2> "$work/wrong-link.log"; then fail "a module calling what the kernel no longer has linked"; fi
grep -q "'m_Kernel\$Log_Write_1_V\$String' is imported, and neither kernel nor a module this one depends on exports it" "$work/wrong-link.log" \
    || { cat "$work/wrong-link.log"; fail "the wrong link said otherwise"; }
if "$corc" compile --kernel "$b/kernel" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 $here/Sb16.cor -o "$work/wrong.ko" 2> "$work/wrong-compile.log"; then
    fail "a module compiled against an index its kernel does not answer"
fi
grep -q "'m_Kernel\$Log_Write_1_V\$String' is imported" "$work/wrong-compile.log" || { cat "$work/wrong-compile.log"; fail "the wrong compile said otherwise"; }
test ! -e "$work/wrong.ko"

# AND THE SEVERAL-FILE MODULE'S: its units are refused by kernel b's link,
# and its index made on a's refused beside b's index.
if "$corc" link $objects --kernel "$b/kernel" -o "$m/wrong.ko" 2> "$m/wrong-link.log"; then fail "a several-file module calling what the kernel no longer has linked"; fi
grep -q "'m_Kernel\$Log_Write_1_V\$String' is imported" "$m/wrong-link.log" || { cat "$m/wrong-link.log"; fail "the several-file wrong link said otherwise"; }
mkdir -p "$m/wrong"
sed "s|$m/|$m/wrong/|g" "$m/units.tsv" > "$m/wrong/units.tsv"
if "$corc" compile-project --units "$m/wrong/units.tsv" --obj --kernel "$b/kernel" --decl-index "$b/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/wrong/units.log"; then
    fail "a module's units compiled with an index made on another kernel's"
fi
grep -q 'was not made on' "$m/wrong/units.log" || { cat "$m/wrong/units.log"; fail "the wrong module index said otherwise"; }

# A KERNEL WHOSE DRIVER HAS A FIELD MORE: every name the module calls is
# there, but its Driver's layout is another's, and so is its name. Refused
# by the loader and the link, naming the type.
mkdir -p "$work/d"
sed '/^public abstract class Driver/{n;s/^{$/{\n    public int Generation;/}' $here/Kernel.cor > "$work/d/Kernel.cor"
grep -q 'public int Generation;' "$work/d/Kernel.cor" || fail "kernel d is not different"
kernel "$work/d" $here/Host.cor "$work/d/Kernel.cor" $here/Loader.cor
d=$work/d
cp "$work/linked/sb16.ko" "$d/module.ko"
status=0
(cd "$d" && ./kernel) > "$d/refused.out" 2>&1 || status=$?
test "$status" = 3 && grep -qx "refused: undefined symbol $driverShape" "$d/refused.out" \
    || { cat "$d/refused.out"; fail "a module compiled against another Driver was loaded"; }
if "$corc" link "$work/sb16.o" --kernel "$d/kernel" -o "$d/wrong.ko" 2> "$d/wrong-link.log"; then fail "a module compiled against another Driver linked"; fi
grep -q "compiled against type Kernel.Driver's layout" "$d/wrong-link.log" || { cat "$d/wrong-link.log"; fail "the wrong layout's link said otherwise"; }

# A KERNEL CHANGED ONLY WHERE NO MODULE LOOKS -- a method more in Unused,
# which nothing calls -- binds the same module's object, built for the
# first, and loads and runs it the same.
mkdir -p "$work/c"
sed 's/^    public static string Never() { return "never " + Log.Lines; }$/&\n    public static int Also() { return 1; }/' $here/Kernel.cor > "$work/c/Kernel.cor"
grep -q 'public static int Also()' "$work/c/Kernel.cor" || fail "kernel c is not different"
kernel "$work/c" $here/Host.cor "$work/c/Kernel.cor" $here/Loader.cor
"$corc" link "$work/sb16.o" --kernel "$work/c/kernel" -o "$work/c/sb16.ko" 2> "$work/c/sb16-link.log" \
    || { cat "$work/c/sb16-link.log"; fail "a module was refused by a kernel changed only where it never looks"; }
cp "$work/c/sb16.ko" "$work/c/module.ko"
(cd "$work/c" && ./kernel) > "$work/c/run.out" 2>&1 || { cat "$work/c/run.out"; fail "kernel c did not load the module"; }
cmp -s "$work/run.out" "$work/c/run.out" || { cat "$work/c/run.out"; fail "the module ran otherwise in kernel c"; }

# A KERNEL'S FILE WRITTEN OVER WHILE IT RUNS: kernel a running, kernel c's
# file where it booted from. The loader reads c's table, whose link identity
# is not the running kernel's, and refuses to bind to addresses of a kernel
# not running.
mkdir -p "$work/over"
cp "$a/kernel" "$work/over/running"
cp "$work/c/kernel" "$work/over/kernel"
cp "$work/linked/sb16.ko" "$work/over/module.ko"
status=0
(cd "$work/over" && ./running) > "$work/over/run.out" 2>&1 || status=$?
test "$status" = 3 && grep -qx "refused: the kernel's file is another build now; reboot" "$work/over/run.out" \
    || { cat "$work/over/run.out"; fail "a module was bound against a kernel file not the running kernel's"; }

printf 'PASS kernel modules: %s\n' "$work"
