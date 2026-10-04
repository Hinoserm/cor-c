#!/bin/sh
# --unused-report: the program's own dead functions and statics, judged as
# written (an inlined call still counts), from one compile and from units
# linked by `corc link`; none of the class library's.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
corc=${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}
mkdir -p "$root/build"
work=$(mktemp -d "$root/build/unused-report.XXXXXX")
source=tests/integration/unused/Program.cor

expected() {
    f=tests/integration/unused/Program.cor
    printf '%s\n' \
        "$f:5: static Program._counted is written but never read" \
        "$f:6: static Program._never is never used" \
        "$f:8: static Program._right is never used" \
        "$f:9: static Program.Half is written but never read" \
        "$f:11: function Program.Unused() is never called" \
        "$f:12: function Program.OnlyFromUnused() is never called" \
        "$f:13: function Program.Chain() is never called" \
        "$f:16: function Program.Nope(T x) is never called" \
        "$f:21: function Never.Sides() is never called"
}

check() {
    tail -n +2 "$1" | grep -v '^not judged' > "$1.lines"
    if ! expected | cmp -s - "$1.lines"; then
        printf 'FAIL %s\n' "$2"; expected | diff - "$1.lines" || true; exit 1
    fi
}

"$corc" compile "$source" -o "$work/whole" --unused-report "$work/whole.txt"
check "$work/whole.txt" "unused report from one compile"

"$corc" compile "$source" --obj -o "$work/program.o" --unused-report -
"$corc" link "$work/program.o" -o "$work/linked" --unused-report "$work/linked.txt"
check "$work/linked.txt" "unused report from corc link"
if grep -q '.corsac.uses' "$work/linked"; then printf 'FAIL the notes reached the image\n'; exit 1; fi
"$work/linked" | grep -qx 10

printf 'PASS unused-code report: %s\n' "$work"
