#!/usr/bin/env bash
# The collector's checked mode for the compiler's frees (Gc.VerifyFree):
# CORSAC_VERIFY_FREES=1 poisons what is freed and ends the process on a
# second free; without it, frees behave as they always have.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p build
work="$(mktemp -d "$root/build/checked-frees.XXXXXX")"
fail() { echo "$1; diagnostics: $work" >&2; exit 1; }
"$corc" compile tests/integration/checked/Twice.cor -o "$work/twice" > "$work/compile.log" 2>&1 || fail "compile failed"

status=0
CORSAC_VERIFY_FREES=1 "$work/twice" > "$work/checked.out" 2> "$work/checked.err" || status=$?
[ "$status" = 97 ] || fail "checked: a second free exited $status, not 97"
grep -qx poisoned "$work/checked.out" || fail "checked: the freed payload was not the pattern"
grep -q "freed twice" "$work/checked.err" || fail "checked: the second free was not reported"
if grep -q survived "$work/checked.out"; then fail "checked: the process went on after a second free"; fi

status=0
"$work/twice" > "$work/plain.out" 2>&1 || status=$?
[ "$status" = 0 ] && grep -qx survived "$work/plain.out" || fail "unchecked: a second free was not ignored (exit $status)"
if grep -qx poisoned "$work/plain.out"; then fail "unchecked: the payload was poisoned"; fi
CORSAC_VERIFY_FREES=0 "$work/twice" > "$work/zero.out" 2>&1 || fail "CORSAC_VERIFY_FREES=0 turned the checked mode on"
echo 'PASS checked frees: poison after a free, a second free reported and fatal; off by default and with 0'
