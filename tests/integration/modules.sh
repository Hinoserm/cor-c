#!/bin/sh
# KERNEL MODULES (docs/software/DRIVERS.md in the OS repository): a
# freestanding kernel linked with --exports, a module compiled against its
# declaration index and linked against those exports, and the kernel loading
# the module the way its binder binds a library -- built in rings, stamped,
# with its aliases in .corsac.modinfo; and a module of several files, a unit
# a file against its own index made on the kernel's. Then a kernel whose
# declarations differ, whose stamp the modules do not carry, refusing them.
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

# And as the image builder builds one: its units in one compile-project.
mkdir -p "$work/project"
printf '%s\t%s\t%s\tentry\n' "$(realpath $here/Sb16.cor)" "$work/project/sb16.o" "$work/project/sb16.o.deps" > "$work/project/units.tsv"
"$corc" compile-project --units "$work/project/units.tsv" --obj --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    2> "$work/project/units.log" || { cat "$work/project/units.log"; fail "module units"; }
"$corc" link "$work/project/sb16.o" --kernel "$a/kernel.exports" -o "$work/project/sb16.ko" 2> "$work/project/link.log" \
    || { cat "$work/project/link.log"; fail "module units' link"; }

# THE KERNEL'S SMALL FUNCTIONS COME INTO THE MODULE (KernelExports.SmallBodies):
# Tick.Twice is copied where Sb16 calls it, no call through the GOT for it.
grep -q "small functions' IR" "$a/link.log" || fail "the kernel's exports carry no small functions' IR"
for ko in "$work/sb16.ko" "$work/linked/sb16.ko" "$work/project/sb16.ko"; do
    if objdump -d "$ko" | grep -q 'Tick_Twice'; then fail "$ko calls the kernel's Tick.Twice instead of carrying it"; fi
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

# RELOCATED TEXT (--relocated-text): the same module without position-
# independent code -- no GOT register found by a call and a pop, the kernel's
# functions called directly -- its text fixed by the binder (R_386_PC32 for
# the calls), and running as the other does.
mkdir -p "$work/relocated"
"$corc" compile --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 \
    $here/Sb16.cor --obj --relocated-text -o "$work/relocated/sb16.o" 2> "$work/relocated/compile.log" || { cat "$work/relocated/compile.log"; fail "relocated module object"; }
"$corc" link "$work/relocated/sb16.o" --kernel "$a/kernel.exports" --relocated-text -o "$work/relocated/sb16.ko" 2> "$work/relocated/link.log" \
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
# same, bound by name against the pruned exports.
readelf -s -W "$a/kernel" | grep -q 'Unused_Never' || fail "the open kernel dropped what a module might call"
readelf --dyn-syms -W "$work/linked/sb16.ko" | awk '$7 == "UND" && $8 != "" { print $8 }' | sort -u > "$work/keep"
mkdir -p "$work/pruned"
objects=$(cut -f2 "$a/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects -o "$work/pruned/kernel" --exports "$work/pruned/kernel.exports" --decl-index "$a/kernel.idx" --keep "$work/keep" \
    2> "$work/pruned/link.log" || { cat "$work/pruned/link.log"; fail "the kernel did not link with --keep"; }
if readelf -s -W "$work/pruned/kernel" | grep -q 'Unused_Never'; then fail "the pruned kernel kept what nothing reaches"; fi
test "$(stat -c %s "$work/pruned/kernel")" -lt "$(stat -c %s "$a/kernel")" || fail "the pruned kernel is no smaller"
readelf --dyn-syms -W "$work/pruned/kernel.exports" | awk '$8 != "" { print $8 }' | sort -u > "$work/pruned/exported"
comm -23 "$work/keep" "$work/pruned/exported" > "$work/pruned/missing"
test ! -s "$work/pruned/missing" || { cat "$work/pruned/missing"; fail "the pruned kernel does not export what the module imports"; }
test "$(stamp "$work/pruned/kernel")" = "$(stamp "$a/kernel")" || fail "pruning changed the build stamp"
cp "$work/linked/sb16.ko" "$work/pruned/module.ko"
(cd "$work/pruned" && ./kernel) > "$work/pruned/run.out" 2>&1 || { cat "$work/pruned/run.out"; fail "the pruned kernel did not load the module"; }
cmp -s "$work/run.out" "$work/pruned/run.out" || { cat "$work/pruned/run.out"; fail "the module ran otherwise in the pruned kernel"; }
if "$corc" link $objects -o "$work/pruned/wrong" --keep "$work/keep" 2> "$work/pruned/wrong.log"; then fail "--keep linked without --exports"; fi

# A MODULE OF SEVERAL FILES, built as the image builder builds every module:
# its own index made on the kernel's, a unit a file in one compile-project
# against both indexes -- the kernel's still the stamp -- and the units
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
"$corc" compile-project --units "$m/units.tsv" --obj --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/units.log" || { cat "$m/units.log"; fail "the module's units"; }
for name in MixerAudio MixerDriver MixerEntry; do
    test -s "$m/$name.o" || fail "no unit $name"
    test "$(stamp "$m/$name.o")" = "$(stamp "$a/kernel")" || fail "unit $name does not carry the kernel's stamp"
done
# Each type defined by its own unit alone; the others name it.
readelf -s -W "$m/MixerAudio.o" | awk '$8 == "t_Sound$002eChannel" && $7 != "UND" { found = 1 } END { exit !found }' \
    || fail "the unit that declares Channel does not define it"
if readelf -s -W "$m/MixerEntry.o" | awk '$8 == "t_Sound$002eChannel" && $7 != "UND" { found = 1 } END { exit !found }'; then
    fail "the entry unit defines Channel, which another unit declares"
fi
objects=$(cut -f2 "$m/units.tsv" | tr '\n' ' ')
# shellcheck disable=SC2086
"$corc" link $objects --kernel "$a/kernel.exports" -o "$m/mixer.ko" 2> "$m/link.log" || { cat "$m/link.log"; fail "the module's units' link"; }
ko=$m/mixer.ko
readelf -h "$ko" | grep -q 'DYN (Shared object file)' || fail "$ko is not a shared object"
readelf -d "$ko" > "$ko.dynamic"
if grep -q 'NEEDED\|TEXTREL' "$ko.dynamic"; then fail "$ko needs a library or relocates its text"; fi
readelf -r -W "$ko" | awk '/^[0-9a-f]+ / { print $3 }' | sort -u > "$ko.kinds"
if grep -v -x -e R_386_RELATIVE -e R_386_32 -e R_386_GLOB_DAT -e R_386_JMP_SLOT "$ko.kinds"; then fail "$ko has a relocation the binder does not apply"; fi
readelf --dyn-syms -W "$ko" > "$ko.syms"
if awk '$7 != "UND" && $8 ~ /^m_(Runtime|Gc|String)_/ { found = 1 } END { exit !found }' "$ko.syms"; then fail "$ko carries a runtime"; fi
test "$(stamp "$ko")" = "$(stamp "$a/kernel")" || fail "$ko does not carry the kernel's stamp"
readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
printf '%s\n' name=mixer ring=0 alias=pnp:MIX0001 | cmp -s - "$ko.modinfo" || { cat "$ko.modinfo"; fail "$ko: .corsac.modinfo"; }
if readelf -s -W "$ko" | grep -q 'MixerPanel'; then fail "$ko holds its [Ring1] half"; fi
mkdir -p "$m/run"
cp "$a/kernel" "$a/kernel.exports" "$m/run/"
cp "$ko" "$m/run/module.ko"
(cd "$m/run" && ./kernel) > "$m/run.out" 2>&1 || { cat "$m/run.out"; fail "the kernel did not load the several-file module"; }
printf '%s\n' 'kernel 0' 'registered mixer' 'modinfo name=mixer' 'modinfo ring=0' 'modinfo alias=pnp:MIX0001' 'drivers 1' \
    'probe 2' 'mixer at 544, master=7 wave=5' 'bind 12' \
    | cmp -s - "$m/run.out" || { cat "$m/run.out"; fail "the several-file module ran other than it should"; }
# A module may not declare a type of its kernel's: its index is refused.
if "$corc" index --assembly Kernel --ring 0 --on "$a/kernel.idx" $here/KernelType.cor -o "$m/wrong.idx" 2> "$m/wrong-index.log"; then
    fail "a module's index declared a type of its kernel's"
fi
grep -q 'a module may not declare a type of its kernel' "$m/wrong-index.log" || { cat "$m/wrong-index.log"; fail "the wrong index said otherwise"; }

# A MODULE ON A MODULE: Chorus's index made on the mixer's, itself made on
# the kernel's (a chain), its units compiled against the chain -- the
# kernel's index still the stamp -- and linked against the kernel's exports
# and the mixer's file. It imports the mixer's names as it does the
# kernel's, needs and depends on the mixer, and defines none of its types.
c=$work/chorus
mkdir -p "$c"
"$corc" index --assembly Kernel --ring 0 --on "$m/module.idx" $here/Chorus.cor -o "$c/chorus.idx" 2> "$c/index.log" \
    || { cat "$c/index.log"; fail "an index on a module's index"; }
printf '%s\t%s\t%s\tentry\n' "$(realpath $here/Chorus.cor)" "$c/Chorus.o" "$c/Chorus.o.deps" > "$c/units.tsv"
"$corc" compile-project --units "$c/units.tsv" --obj --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" \
    --module-index "$c/chorus.idx" --assembly Kernel --ring 0 2> "$c/units.log" || { cat "$c/units.log"; fail "a module's units on a module"; }
test "$(stamp "$c/Chorus.o")" = "$(stamp "$a/kernel")" || fail "the module on a module does not carry the kernel's stamp"
"$corc" link "$c/Chorus.o" --kernel "$a/kernel.exports" --link-shared "$m/mixer.ko" -o "$c/chorus.ko" 2> "$c/link.log" \
    || { cat "$c/link.log"; fail "a module linked against a module"; }
ko=$c/chorus.ko
test "$(stamp "$ko")" = "$(stamp "$a/kernel")" || fail "$ko does not carry the kernel's stamp"
readelf -d "$ko" | grep -q 'Shared library: \[mixer.ko\]' || fail "$ko does not need the mixer"
readelf -p .corsac.modinfo "$ko" | sed -n 's/^ *\[ *[0-9a-f]*\]  //p' > "$ko.modinfo"
grep -qx 'depends=mixer' "$ko.modinfo" || { cat "$ko.modinfo"; fail "$ko does not depend on the mixer"; }
readelf --dyn-syms -W "$ko" > "$ko.syms"
# Log.Write's body comes into it from the kernel's exports (SmallBodies):
# the kernel's name it imports is Log's line count.
for name in 't_Sound$002eChannel' 'm_Sound$Channels_Total_0' 's_Kernel$002eLog_Lines'; do
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
if "$corc" link "$c/Chorus.o" --kernel "$a/kernel.exports" -o "$c/alone.ko" 2> "$c/alone.log"; then fail "a module on a module linked without it"; fi
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
"$corc" compile --kernel "$a/kernel.exports" --decl-index "$a/kernel.idx" --assembly Kernel --ring 0 --kernel-syscalls \
    $here/FileCalls.cor --obj -o "$f/files.o" 2> "$f/compile.log" || { cat "$f/compile.log"; fail "a module compiled --kernel-syscalls"; }
"$corc" link "$f/files.o" --kernel "$a/kernel.exports" -o "$f/files.ko" 2> "$f/link.log" || { cat "$f/link.log"; fail "the --kernel-syscalls module's link"; }
# (The kernel's answer here is small: its body comes into the module from
# the exports, as Log.Write's does, and nothing of it is imported.)
if objdump -d "$f/files.ko" | grep -q 'int *\$0x8[03]'; then fail "the --kernel-syscalls module still traps"; fi
cp "$a/kernel" "$a/kernel.exports" "$f/run/"
cp "$f/files.ko" "$f/run/module.ko"
(cd "$f/run" && ./kernel) > "$f/run.out" 2>&1 || { cat "$f/run.out"; fail "the kernel did not load the --kernel-syscalls module"; }
grep -qx 'served 5031 6021' "$f/run.out" || { cat "$f/run.out"; fail "the kernel's file calls answered other than they should"; }
if "$corc" compile --kernel-syscalls $here/FileCalls.cor -o "$f/wrong" 2> "$f/wrong.log"; then fail "--kernel-syscalls compiled a program for Linux"; fi
grep -q 'is for ring-0 code' "$f/wrong.log" || { cat "$f/wrong.log"; fail "the wrong --kernel-syscalls said otherwise"; }

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

# AND THE SEVERAL-FILE MODULE'S, unchanged by its index: its units are
# refused by kernel b's link, its index made on a's refused beside b's
# index, and a compile against b's exports with a's index refused as before.
if "$corc" link $objects --kernel "$b/kernel.exports" -o "$m/wrong.ko" 2> "$m/wrong-link.log"; then fail "a several-file module of another kernel linked"; fi
grep -q 'compiled against the declarations of another kernel' "$m/wrong-link.log" || { cat "$m/wrong-link.log"; fail "the several-file wrong link said otherwise"; }
mkdir -p "$m/wrong"
sed "s|$m/|$m/wrong/|g" "$m/units.tsv" > "$m/wrong/units.tsv"
if "$corc" compile-project --units "$m/wrong/units.tsv" --obj --kernel "$b/kernel.exports" --decl-index "$b/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/wrong/units.log"; then
    fail "a module's units compiled with an index made on another kernel's"
fi
grep -q 'was not made on the index kernel was compiled against' "$m/wrong/units.log" || { cat "$m/wrong/units.log"; fail "the wrong module index said otherwise"; }
if "$corc" compile-project --units "$m/wrong/units.tsv" --obj --kernel "$b/kernel.exports" --decl-index "$a/kernel.idx" \
    --module-index "$m/module.idx" --assembly Kernel --ring 0 2> "$m/wrong/units-a.log"; then
    fail "a module's units compiled against another kernel's index"
fi
grep -q 'is not the index kernel was compiled against' "$m/wrong/units-a.log" || { cat "$m/wrong/units-a.log"; fail "the wrong kernel index said otherwise"; }

printf 'PASS kernel modules: %s\n' "$work"
