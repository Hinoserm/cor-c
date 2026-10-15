#!/usr/bin/env bash
#
# A driver its configuration turns off is not in the image.
#
#     tests/language/202_config_dead_branch.sh
#
# The half of test 201 a program cannot see from inside itself. 201 is built
# twice: as written, with DriverFloppy false, and with that one constant true.
# The floppy build probes three drivers. The build as written probes two and
# is the smaller: the branch behind a false constant is dropped, not merely
# never taken, and with it the code only that branch reached.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
# The compiler run.sh uses: CORC, else the build's own.
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/config-dead-branch.XXXXXX")}"

failures=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

source="$here/201_config_dead_branch.cor"
cp "$source" "$work/off.cor"
sed 's/public const bool DriverFloppy = false;/public const bool DriverFloppy = true;/' "$source" > "$work/on.cor"
if grep -q 'public const bool DriverFloppy = true;' "$work/on.cor"
then
    pass "the floppy key turned on"
else
    fail "the floppy key turned on (201 no longer declares DriverFloppy = false)"
fi

for build in off on
do
    if $corc compile "$work/$build.cor" -o "$work/$build" > "$work/$build.log" 2>&1
    then
        pass "the $build build compiles"
    else
        fail "the $build build compiles"
        sed 's/^/    /' "$work/$build.log" | head -20
    fi
done

check_run()
{
    local build=$1 want=$2 got
    got="$("$work/$build" 2>&1)"
    if [ "$got" = "$want" ]
    then
        pass "the $build build probes what it is configured with"
    else
        fail "the $build build probes what it is configured with: got '$got', wanted '$want'"
    fi
}

if [ -x "$work/off" ] && [ -x "$work/on" ]
then
    check_run off "$(printf 'ide probed\nuart probed\ndrivers 2')"
    check_run on "$(printf 'ide probed\nuart probed\nfloppy probed\ndrivers 3')"

    # BY SIZE: a small Probe is inlined before anything is dropped, so
    # neither build has a symbol for it, and its message is data the image
    # keeps either way; the code that prints it is what goes.
    off_size=$(stat -c %s "$work/off") on_size=$(stat -c %s "$work/on")
    if [ "$off_size" -lt "$on_size" ]
    then
        pass "the off build is the smaller ($off_size < $on_size bytes)"
    else
        fail "the off build is the smaller ($off_size vs $on_size bytes)"
    fi
fi

if [ "$failures" = 0 ]
then
    echo "202_config_dead_branch: all checks passed"
    [ -z "${WORK:-}" ] && rm -rf "$work"
    exit 0
fi

echo "202_config_dead_branch: $failures check(s) failed; work in $work"
exit 1
