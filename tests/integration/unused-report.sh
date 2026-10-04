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
    printf '%s\n' \
        "Program.cor:5: static Program._counted is written but never read" \
        "Program.cor:6: static Program._never is never used" \
        "Program.cor:9: function Program.Unused() is never called" \
        "Program.cor:10: function Program.OnlyFromUnused() is never called" \
        "Program.cor:11: function Program.Chain() is never called" \
        "Program.cor:16: function Never.Sides() is never called"
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
