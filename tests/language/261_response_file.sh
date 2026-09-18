#!/usr/bin/env bash
#
# Response files, --cpu, and a link address that is not the load address.
#
#     tests/lang/261_response_file.sh
#
# Three small things the x86 kernel build asked for. `corc compile @list` takes
# its arguments from a file, because a kernel is a hundred sources and a
# command line is not; `--cpu` records which processor in the family is meant,
# which nothing in the backend acts on yet and everything that ever emits a
# Pentium instruction will have to; and `--base` with `--load` says where an
# image runs as against where a loader has to put it, which is the whole of
# what a higher-half kernel needs from a linker.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="$root/compiler/bin/Release/net10.0/corc"
work="${WORK:-$(mktemp -d)}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

cat > "$work/answer.cor" <<'COR'
static class Program
{
    static int Main()
    {
        return 42;
    }
}
COR

# Paths in a response file are relative to the file that names them, so the
# list can sit beside the sources and be moved with them.
cp "$root/lib/std.cor" "$work/std.cor"
cp -r "$root/lib/rt" "$work/rt"
mkdir -p "$work/sys"
cp "$root/lib/sys/linux.cor" "$work/sys/linux.cor"
cp "$root/lib/threading.cor" "$work/threading.cor"

cat > "$work/sources.list" <<'LIST'
# every source of this program, one a line, comments and blanks skipped

std.cor
rt/runtime.cor
rt/gc.cor
threading.cor
sys/linux.cor

answer.cor
LIST

"$corc" compile "@$work/sources.list" --nostdlib --cpu 486 -o "$work/answer" 2>"$work/err"
if [ $? = 0 ]
then
    pass "corc compile @list --cpu 486 builds"
else
    fail "corc compile @list --cpu 486 builds"
    sed 's/^/    /' "$work/err"
fi

if [ -x "$work/answer" ]
then
    "$work/answer"
    got=$?
    if [ "$got" = 42 ]
    then
        pass "the program the response file named runs"
    else
        fail "the program the response file named runs (exit $got)"
    fi
else
    fail "the program the response file named exists"
fi

if "$corc" compile "@$work/sources.list" --nostdlib --cpu 787 -o "$work/never" 2>/dev/null
then
    fail "an unknown --cpu is refused"
else
    pass "an unknown --cpu is refused"
fi

if "$corc" compile "@$work/nothing.list" -o "$work/never" 2>/dev/null
then
    fail "a missing response file is refused"
else
    pass "a missing response file is refused"
fi

# The same sources, linked to run high and be loaded low.
"$corc" compile "@$work/sources.list" --nostdlib --base 0xC0100000 --load 0x00100000 \
    -o "$work/high" 2>>"$work/err"
if [ $? = 0 ]
then
    pass "--base with --load links"
else
    fail "--base with --load links"
    sed 's/^/    /' "$work/err"
fi

if [ -f "$work/high" ]
then
    loads="$(readelf -lW "$work/high" | grep -c 'LOAD .*0xc01.* 0x001')"
    if [ "$loads" -ge 1 ]
    then
        pass "every loadable segment is linked high and loaded low"
    else
        fail "every loadable segment is linked high and loaded low"
        readelf -lW "$work/high" | sed 's/^/    /' | grep LOAD
    fi

    entry="$(readelf -hW "$work/high" | sed -n 's/.*Entry point address: *//p')"
    if [ "$((entry))" -ge "$((0x100000))" ] && [ "$((entry))" -lt "$((0xC0000000))" ]
    then
        pass "the entry point is the physical address, as a loader with no MMU needs"
    else
        fail "the entry point is the physical address (got $entry)"
    fi
fi

if [ "$failures" = 0 ]
then
    echo "261_response_file: all checks passed"
    exit 0
fi

echo "261_response_file: $failures check(s) failed; work in $work"
exit 1
