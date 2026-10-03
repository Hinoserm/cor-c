#!/usr/bin/env bash
# Lifetimes across separately compiled units: what a unit frees by itself,
# what the link frees with every unit's hints, and that the link's answer is
# the same bytes however little memory it is given.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
corlink() { "${CORLINK:-$corc}" link "$@"; }
export CORC="$corc"
mkdir -p build
work="$(mktemp -d "$root/build/lifetimes.XXXXXX")"
echo "Lifetimes across units: $work"
refs=()
while IFS= read -r source; do refs+=(--ref "$source"); done < <("$corc" library-sources)
provider="$root/tests/integration/lifetimes/Provider.cor"
caller="$root/tests/integration/lifetimes/Caller.cor"
fail() { echo "$1; diagnostics: $work" >&2; exit 1; }

"$corc" index --assembly Lifetimes "$provider" -o "$work/provider.idx"
# The provider owns the runtime; the caller sees only declarations.
"$corc" compile --lib "$provider" --obj -o "$work/provider.o" > "$work/provider.log" 2>&1
"$corc" compile --nostdlib "${refs[@]}" --decl-index "$work/provider.idx" --assembly Lifetimes \
    "$caller" --obj --stats -o "$work/caller.o" > "$work/caller.log" 2>&1

# 1. By itself: the scratch array that dies in its loop is freed, although
#    the runtime's Free is another unit's.
freed=$(sed -n 's/.* \([0-9][0-9]*\) freed by the compiler.*/\1/p' "$work/caller.log")
[ "${freed:-0}" -gt 0 ] || fail "a separately compiled unit freed nothing by itself"
readelf -SW "$work/caller.o" | grep -q '\.corsac\.life' || fail "the caller left no lifetime hints"

run() {
    local status=0
    "$1" > "$1.out" 2>&1 || status=$?
    [ "$status" = 42 ] || fail "$(basename "$1") exited $status"
}

# 2. At the link: the array lent to Provider.Sum and the one Provider.Make
#    hands over are freed; the one Provider.Keep holds is not (exit 24).
corlink "$work/caller.o" "$work/provider.o" -o "$work/on" > "$work/on.log" 2>&1
run "$work/on"
# The link reruns the pass over the unit's own frees: an object the unit
# already frees (Held's holder, with its field site) must not be owned and
# freed a second time. Every free checked, a double free exits 96 or 97.
status=0
CORSAC_VERIFY_FREES=1 "$work/on" > "$work/on.verify" 2>&1 || status=$?
[ "$status" = 42 ] || fail "with every free checked the linked program exited $status (a double free?)"
grep -q 'LTO lifetimes: units with hints=2, units gaining=1' "$work/on.log" || fail "the link found no unit to gain"
taken=$(sed -n 's/.*lifetimes placed or freed=\([0-9][0-9]*\).*/\1/p' "$work/on.log" | head -1)
[ "${taken:-0}" -ge 2 ] || fail "the link placed or freed ${taken:-0} objects, expected the lent and the received arrays"
if readelf -SW "$work/on" | grep -q '\.corsac\.life'; then fail "lifetime hints leaked into the final image"; fi
# Fields: a holder's array made by Provider.Make is freed with the holder
# (its field site becomes FreeField); one Provider.Shared hands everybody
# is not (its site becomes KeepField, and the shared array survives, exit 28).
sites=$(sed -n 's/.*field sites=\([0-9][0-9]*\) freed=\([0-9][0-9]*\).*/\1 \2/p' "$work/on.log" | head -1)
set -- $sites
[ "${2:-0}" -gt 0 ] || fail "no field site became a free"
[ "${1:-0}" -gt "${2:-0}" ] || fail "every field site became a free, the shared array's included"

corlink --no-lto "$work/caller.o" "$work/provider.o" -o "$work/off" > "$work/off.log" 2>&1
run "$work/off"
grep -q 'units gaining=0, field sites=[0-9]* freed=0' "$work/off.log" || fail "--no-lto freed through a field site or gained a unit"

# 3. The same bytes again, and the same bytes with the backend made to work
#    one function at a time: memory sizes the work, never the answer.
corlink "$work/caller.o" "$work/provider.o" -o "$work/again" > "$work/again.log" 2>&1
cmp "$work/on" "$work/again" || fail "two links of the same objects differ"
corlink --work-budget 1 "$work/caller.o" "$work/provider.o" -o "$work/small" > "$work/small.log" 2>&1
cmp "$work/on" "$work/small" || fail "the link on a one-function budget differs"
grep -q 'peak batch functions=1,' "$work/small.log" || fail "the small budget did not batch one function at a time"
echo 'PASS lifetimes: per-unit frees, link-time hints solved across units, fields across units, kept objects kept, identical under a one-function budget'
