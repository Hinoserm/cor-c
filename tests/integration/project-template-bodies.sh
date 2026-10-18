#!/usr/bin/env bash
# A TEMPLATE'S TEXT IS A UNIT'S DEPENDENCE ONLY WHERE THE UNIT COPIED ONE OF
# ITS BODIES (IndexedDeclarations.WriteDependencies, TypeDecl.BodiesUsed).
# Grow is a class with a generic method, so every unit naming it imports it
# as a template; only the units that call Wider make a copy of a body of it.
# An edit to Grow's ordinary method, or to a number in Wider, compiles again
# the unit that owns that text and the units that copied Wider, and leaves
# the one that only calls Limit current. A NAME ADDED TO WIDER compiles that
# one again too: a template's bodies are read for what they name in every
# unit importing it, and what Wider names here is a [Registry] class, whose
# schema goes into the object of every unit that loads it (Dependence.Template).
# Grow is partial: the entry unit's own part takes the imported part's
# bodies when the two are merged, and a unit importing both parts merges
# them into the part it reads first, so a copy made out of either merged
# type is one of Grow's. Every object the incremental builds leave is the
# object a clean build of the same text makes, byte for byte.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-template-bodies.XXXXXX")"
corc="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"

cat > "$work/Entry.cor" <<'COR'
namespace TemplateBodies;
static partial class Grow
{
    static int Main() => Copier.Run() + Limiter.Run() + Twice(new int[2]);
    static int Twice(int[] had) => Wider(had, had.Length * 2).Length;
}
COR
cat > "$work/GrowWider.cor" <<'COR'
namespace TemplateBodies;
static partial class Grow
{
    public static int Limit(int n) { return n < 16 ? n : 16; }

    public static T[] Wider<T>(T[] had, int count)
    {
        T[] made = new T[count];
        for (int i = 0; i < had.Length; i++) made[i] = had[i];
        return made;
    }
}
COR
cat > "$work/Copier.cor" <<'COR'
namespace TemplateBodies;
static class Copier
{
    public static int Run()
    {
        int[] made = Grow.Wider(new int[] { 1, 2, 3 }, 8);
        return made.Length + made[2];
    }
}
COR
cat > "$work/Limiter.cor" <<'COR'
namespace TemplateBodies;
static class Limiter
{
    public static int Run() => Grow.Limit(40);
}
COR
cat > "$work/Settings.cor" <<'COR'
namespace TemplateBodies;
[Registry("TemplateBodies.Probe")]
static class Settings
{
    public static int Width = 800;
}
COR
# What a declared int setting reads and writes through, for a program with
# no kernel under it (tests/language/support/540_registry.cor).
cat > "$work/Store.cor" <<'COR'
enum RegistryScope
{
    User = 0,
    Machine = 1,
    Both = 2,
}

static class Registry
{
    public static long ReadInt(string key, long fallback, RegistryScope scope = RegistryScope.Both) => fallback;
    public static long WriteInt(string key, long value, RegistryScope scope) => 0;
}
COR

refs=""
for lib in $(dotnet "$corc" library-sources); do refs="$refs --ref $lib"; done
sources=("$work/Entry.cor" "$work/GrowWider.cor" "$work/Copier.cor" "$work/Limiter.cor" "$work/Settings.cor" "$work/Store.cor")
# The units of the project, their objects in $1.
units() {
    mkdir -p "$work/$1"
    : > "$work/$1.tsv"
    for source in "${sources[@]}"; do
        name="$(basename "$source" .cor)"; kind=lib
        [ "$name" = Entry ] && kind=entry
        printf '%s\t%s\t%s\t%s\n' "$source" "$work/$1/$name.o" "$work/$1/$name.o.deps" "$kind" >> "$work/$1.tsv"
    done
}
# Indexes the sources as they are now, and compiles the units listed in $1.tsv.
build() {
    dotnet "$corc" index --assembly TemplateBodies "${sources[@]}" -o "$work/declarations.idx" > "$work/index.log" 2>&1
    # shellcheck disable=SC2086
    dotnet "$corc" compile-project --units "$work/$1.tsv" --decl-index "$work/declarations.idx" --assembly TemplateBodies \
        --nostdlib $refs --obj > "$work/$2.log" 2>&1
}
# The units the last build of $1 compiled, by name, in order.
compiled() {
    sed -n 's/^unit [0-9]*\/[0-9]* \([A-Za-z]*\)\.cor.*/\1/p' "$work/$1.log" | sort | tr '\n' ' '
}
# Each object of $1 against a clean build of the same text.
same_as_clean() {
    units "$2"
    build "$2" "$2"
    for source in "${sources[@]}"; do
        name="$(basename "$source" .cor)"
        if ! cmp -s "$work/$1/$name.o" "$work/$2/$name.o"; then
            echo "$name: the incremental object is not the clean build's ($3)" >&2; exit 1
        fi
    done
}

units inc
build inc first
test "$(compiled first)" = "Copier Entry GrowWider Limiter Settings Store "

# A body-only edit to Limit, which is no template's body.
sed -i 's/return n < 16 ? n : 16;/return n < 0x10 ? n : 0x10;/' "$work/GrowWider.cor"
build inc limit
if [ "$(compiled limit)" != "Copier Entry GrowWider " ]; then
    echo "A body-only edit to Limit compiled: $(compiled limit)(wanted Copier Entry GrowWider)" >&2; exit 1
fi
same_as_clean inc clean-limit "after the edit to Limit"

# A number edited inside Wider, the generic method two units copied.
sed -i 's/for (int i = 0; i < had.Length; i++)/for (int i = 0x0; i < had.Length; i++)/' "$work/GrowWider.cor"
build inc wider
if [ "$(compiled wider)" != "Copier Entry GrowWider " ]; then
    echo "A number edited in Wider compiled: $(compiled wider)(wanted Copier Entry GrowWider)" >&2; exit 1
fi
same_as_clean inc clean-wider "after the edit to Wider"

# A name added to Wider: the [Registry] class it names is loaded by Limiter
# now, whose clean object carries its schema.
sed -i 's/        return made;/        if (Settings.Width < 0) return had;\n        return made;/' "$work/GrowWider.cor"
grep -q "Settings.Width" "$work/GrowWider.cor"
build inc named
if [ "$(compiled named)" != "Copier Entry GrowWider Limiter " ]; then
    echo "A name added to Wider compiled: $(compiled named)(wanted Copier Entry GrowWider Limiter)" >&2; exit 1
fi
same_as_clean inc clean-named "after the name added to Wider"
echo "PASS a template's text is a dependence only of the units that copied its bodies: $work"
