#!/usr/bin/env bash
# A TEMPLATE READ FROM ANOTHER UNIT'S INDEX KEEPS ITS FILE'S `using static`
# (SourceDeclaration: the declaration's StaticImports stored and read with
# its usings and aliases). Boxes.cor imports Helpers' statics; its generic
# struct's members name Twice and Base bare (static, nothing allocated: the
# units are compiled against the library's declarations alone). Entry.cor,
# which imports nothing, makes the copies -- binding each template body in
# Entry's unit, from the index. Lost, the bodies' Twice was CS0103 in Entry. And the
# import is the template's file's alone: Bare.cor, a unit of its own with no
# `using static`, naming Twice bare is still refused.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
mkdir -p build
work="$(mktemp -d "$root/build/project-using-static.XXXXXX")"
corc="${CORC_DLL:-$root/compiler/bin/managed/Release/net10.0/corc.dll}"
fail() { printf 'FAIL %s\n' "$1"; exit 1; }

cat > "$work/Helpers.cor" <<'COR'
namespace Statics;
static class Helpers
{
    public static int Twice(int x) => x * 2;
    public const int Base = 5;
}
COR
cat > "$work/Boxes.cor" <<'COR'
using static Statics.Helpers;
namespace Statics;
struct Box<T>
{
    public T Item;
    public static int Size() => Twice(3);
    public static int Total() => Twice(4) + Base;
}
COR
cat > "$work/Entry.cor" <<'COR'
namespace Statics;
static class Program
{
    static int Main() => Box<long>.Total() + Box<long>.Size() - 19;
}
COR
cat > "$work/Bare.cor" <<'COR'
namespace Statics;
static class Bare
{
    public static int Wrong() => Twice(1);
}
COR

refs=""
for lib in $(dotnet "$corc" library-sources); do refs="$refs --ref $lib"; done
units() {
    list="$work/$1.tsv"
    : > "$list"
    shift
    for source in "$@"; do
        name="$(basename "$source" .cor)"; kind=lib
        [ "$name" = Entry ] && kind=entry
        printf '%s\t%s\t%s\t%s\n' "$work/$source" "$work/$name.o" "$work/$name.o.deps" "$kind" >> "$list"
    done
}

units good Helpers.cor Boxes.cor Entry.cor
dotnet "$corc" index --assembly Statics "$work/Helpers.cor" "$work/Boxes.cor" "$work/Entry.cor" -o "$work/good.idx" > "$work/good-index.log" 2>&1 \
    || { cat "$work/good-index.log"; fail "the index"; }
# shellcheck disable=SC2086
dotnet "$corc" compile-project --units "$work/good.tsv" --decl-index "$work/good.idx" --assembly Statics --nostdlib $refs --obj \
    > "$work/good.log" 2>&1 || { cat "$work/good.log"; fail "Entry's copies of Boxes' templates lost their file's using static"; }

units bare Helpers.cor Boxes.cor Entry.cor Bare.cor
dotnet "$corc" index --assembly Statics "$work/Helpers.cor" "$work/Boxes.cor" "$work/Entry.cor" "$work/Bare.cor" -o "$work/bare.idx" > "$work/bare-index.log" 2>&1 \
    || { cat "$work/bare-index.log"; fail "the index with Bare"; }
# shellcheck disable=SC2086
if dotnet "$corc" compile-project --units "$work/bare.tsv" --decl-index "$work/bare.idx" --assembly Statics --nostdlib $refs --obj \
    > "$work/bare.log" 2>&1; then
    fail "Bare named Twice bare with no using static of its own"
fi
grep -q "CS0103.*'Twice'" "$work/bare.log" || { cat "$work/bare.log"; fail "Bare's refusal does not say CS0103 of Twice"; }

echo "PASS project-using-static"
