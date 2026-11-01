#!/usr/bin/env bash
# A KERNEL'S EXPORTS ARE ITS PUBLIC SURFACE (--exports, PublicApi): a
# public method of a public type is exported and kept out of line though
# nothing in the kernel calls it -- Unused.Never, in the kernel's entry unit,
# whose own sweep keeps it (Module.ExportsSurface); an internal type's members
# and an internal member of a public type are not, and a module importing one
# is refused, naming it. What the kernel's generic code names is exported
# too, for a module's copy of it.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/kernel-public-exports.XXXXXX")
fail() { printf 'FAIL %s\n' "$1"; exit 1; }
cp tests/integration/modules/Host.cor tests/integration/modules/Kernel.cor tests/integration/modules/Loader.cor tests/integration/modules/Sb16.cor "$work/"
# An internal member of a public type, and an internal type, both used by the kernel.
sed -i 's/    public static int Twice(int value) => value \* 2 + Log.Lines;/&\n    internal static int Hidden(int value) => value + 1;/' "$work/Kernel.cor"
printf '\nstatic class Secret\n{\n    public static int Value() => 7;\n}\n' >> "$work/Kernel.cor"
# Nested types: public in public, public in internal; and a public type with startup work.
printf '\npublic static class Shelf\n{\n    public static class Rack\n    {\n        public static int Count() => 3;\n    }\n}\n' >> "$work/Kernel.cor"
printf '\nstatic class Vault\n{\n    public static class Inner\n    {\n        public static int Value() => 9;\n    }\n}\n' >> "$work/Kernel.cor"
printf '\npublic static class Counter\n{\n    static int _made = Make();\n    public static int Made => _made;\n    static int Make() => Log.Lines + 1;\n}\n' >> "$work/Kernel.cor"
# A public class the kernel never makes, which the module does; an internal one the kernel does.
printf '\npublic sealed class Packet\n{\n    public int Size = 3;\n}\n' >> "$work/Kernel.cor"
printf '\nsealed class Crate\n{\n    public int Weight = 1;\n}\n' >> "$work/Kernel.cor"
sed -i 's/        Host.Start(32 \* 1024 \* 1024);/&\n        Log.Lines += new Crate().Weight - 1;/' "$work/Kernel.cor"
sed -i 's/Log.Write("sb16 at " + port + ", " + Sb16Module.Greeting);/Log.Write("sb16 at " + port + ", " + Sb16Module.Greeting + new Kernel.Packet().Size);/' "$work/Sb16.cor"
grep -q 'new Kernel.Packet().Size' "$work/Sb16.cor" || fail "the module's use of Packet was not written"
sed -i 's/        Host.Start(32 \* 1024 \* 1024);/&\n        Log.Lines += Tick.Hidden(0) + Secret.Value() + Vault.Inner.Value() - 17;/' "$work/Kernel.cor"
# GENERIC CODE THE KERNEL NEVER INSTANTIATES, naming a private method, a
# private static and an internal type's member: the module's copy of it for
# its own struct calls them, the kernel's code as it is, and the kernel
# exports them for it (Binder.GenericNames, `^`) -- and nothing else of them.
printf '\npublic static class Bag\n{\n    static int _seen;\n    static int Bump() => ++_seen;\n    public static T Pick<T>(T a, T b) => Bump() %% 2 == 1 && Rules.First(_seen) ? a : b;\n}\n' >> "$work/Kernel.cor"
printf '\nstatic class Rules\n{\n    public static bool First(int n) => n > 0;\n    public static bool Never(int n) => n < 0;\n}\n' >> "$work/Kernel.cor"
printf '\nstruct Spot\n{\n    public int X;\n}\n' >> "$work/Sb16.cor"
sed -i 's/        _base = port;/&\n        Spot spot = Bag.Pick(new Spot { X = 1 }, new Spot { X = 2 });\n        _base += spot.X - 1;/' "$work/Sb16.cor"
grep -q 'Bag.Pick(new Spot' "$work/Sb16.cor" || fail "the module's generic call was not written"
grep -q 'internal static int Hidden' "$work/Kernel.cor" || fail "the internal member was not written"
grep -q 'Tick.Hidden(0)' "$work/Kernel.cor" || fail "the kernel's call was not written"
k=$work/k
mkdir -p "$k"
runtime=$("$corc" library-sources --freestanding)
# shellcheck disable=SC2086
"$corc" index --assembly Kernel --ring 0 "$work/Host.cor" "$work/Kernel.cor" "$work/Loader.cor" $runtime -o "$k/kernel.idx" 2> "$k/index.log"
: > "$k/units.tsv"
for source in "$work/Host.cor" "$work/Kernel.cor" "$work/Loader.cor" $runtime; do
    role=lib; case $source in */Kernel.cor) role=entry ;; esac
    printf '%s\t%s\t%s\t%s\n' "$(realpath "$source")" "$k/$(basename "$source" .cor).o" "$k/$(basename "$source" .cor).o.deps" "$role" >> "$k/units.tsv"
done
"$corc" compile-project --units "$k/units.tsv" --nostdlib --obj --freestanding --ring 0 --decl-index "$k/kernel.idx" --assembly Kernel \
    2> "$k/units.log" || { cat "$k/units.log"; fail "kernel units"; }
# shellcheck disable=SC2086
"$corc" link $(cut -f2 "$k/units.tsv" | tr '\n' ' ') -o "$k/kernel" --exports 2> "$work/kernel.log" || { cat "$work/kernel.log"; fail "kernel link"; }
# The table as the module tests read it (exports.py): one name a line, first.
python3 -I "$root/tests/integration/modules/exports.py" "$k/kernel" | awk '{ print $1 }' > "$work/exports.syms"
exported() { grep -q "^$1" "$work/exports.syms"; }
# Every name by its namespace's key (Kernel$Tick): a name without it would
# make the checks below that one is absent pass for nothing.
exported 'm_Kernel\$Unused_Never_' || fail "a public method nothing calls is not exported"
exported 'm_Kernel\$Tick_Twice_' || fail "a public method the module calls is not exported"
exported 'm_Kernel\$Log_Write_' || fail "a public method the kernel calls is not exported"
if exported 'm_Kernel\$Tick_Hidden_'; then fail "an internal member of a public type is exported"; fi
if exported 'm_Kernel\$Secret_Value_'; then fail "a public member of an internal type is exported"; fi
exported 'm_Kernel\$Shelf\$Rack_Count_' || fail "a public member of a public type nested in a public one is not exported"
if exported 'm_Kernel\$Vault\$Inner_Value_'; then fail "a public member of a public type nested in an internal one is exported"; fi
exported 'm_Kernel\$Counter_StaticInit\$_' || fail "a public type's startup work is not exported"
exported 'm_Kernel\$Counter_get_Made_' || fail "a public property's accessor is not exported"
exported 's_Kernel\$002eCounter_StaticReady\$_' || fail "a public type's ready word is not exported"
exported 't_Kernel\$002ePacket' || fail "a public class's descriptor is not exported, though only a module makes one"
if exported 't_Kernel\$002eCrate'; then fail "an internal class's descriptor is exported"; fi
if exported 'm_Kernel\$Counter_Make_'; then fail "a private method is exported"; fi
exported 'm_Kernel\$Bag_Bump_' || fail "a private method the kernel's generic code names is not exported for a module's copy"
exported 's_Kernel\$002eBag__seen_' || fail "a private static the kernel's generic code names is not exported for a module's copy"
exported 'm_Kernel\$Rules_First_' || fail "an internal type's member the kernel's generic code names is not exported"
if exported 'm_Kernel\$Rules_Never_'; then fail "an internal type's member no generic code names is exported"; fi
if exported 'm_Kernel\$Bag_Pick'; then fail "the kernel exported a copy of generic code it never made"; fi
# The module, which imports public members and what its copy of Bag.Pick
# calls: linked, and its copy its own.
"$corc" compile --kernel "$k/kernel" --decl-index "$k/kernel.idx" --assembly Kernel --ring 0 "$work/Sb16.cor" --obj -o "$work/sb16.o" \
    2> "$work/sb16.log" || { cat "$work/sb16.log"; fail "module object"; }
"$corc" link "$work/sb16.o" --kernel "$k/kernel" -o "$work/sb16.ko" 2> "$work/module.log" || { cat "$work/module.log"; fail "module link"; }
readelf --dyn-syms -W "$work/sb16.ko" | awk '$7 == "UND" { print $8 }' > "$work/sb16.imports"
grep -q '^m_Kernel\$Bag_Bump_' "$work/sb16.imports" || { cat "$work/sb16.imports"; fail "the module's copy of Bag.Pick does not call the kernel's Bag.Bump"; }
grep -q '^s_Kernel\$002eBag__seen_' "$work/sb16.imports" || { cat "$work/sb16.imports"; fail "the module's copy of Bag.Pick does not read the kernel's Bag._seen"; }
readelf -sW "$work/sb16.ko" | awk '$7 != "UND" { print $8 }' | grep -q '^m_Kernel\$Bag_Pick' || fail "the module has no copy of Bag.Pick of its own"
# A module importing the internal member: refused, the name said.
sed 's/Tick.Twice(port) - Tick.Twice(port)/Tick.Twice(port) - Tick.Twice(port) + Tick.Hidden(port) - Tick.Hidden(port)/' "$work/Sb16.cor" > "$work/Sb16Hidden.cor"
grep -q 'Tick.Hidden(port)' "$work/Sb16Hidden.cor" || fail "the module's internal call was not written"
"$corc" compile --kernel "$k/kernel" --decl-index "$k/kernel.idx" --assembly Kernel --ring 0 "$work/Sb16Hidden.cor" --obj -o "$work/sb16hidden.o" \
    2> "$work/sb16hidden.log" || { cat "$work/sb16hidden.log"; fail "module object with the internal call"; }
if "$corc" link "$work/sb16hidden.o" --kernel "$k/kernel" -o "$work/sb16hidden.ko" 2> "$work/module-hidden.log"; then fail "a module importing an internal member linked"; fi
grep -q 'm_Kernel\$Tick_Hidden_' "$work/module-hidden.log" || { cat "$work/module-hidden.log"; fail "the refusal does not name the internal member"; }
echo "PASS a kernel exports its public surface, every public member out of line, and a module importing an internal one is refused: $work"
