#!/usr/bin/env bash
# The link's allocation report past the throw budget: where unwinds are
# unbounded the answer is a collector for that reason alone, and the report
# goes on to list the sites a walk as if they were bounded still finds --
# what would need a collector without them -- as the compile's report does.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p build
work="$(mktemp -d "$root/build/alloc-report.XXXXXX")"
echo "Allocation report past the throw budget: $work"
refs=()
while IFS= read -r source; do refs+=(--ref "$source"); done < <("$corc" library-sources)
provider="$root/tests/integration/alloc-report/Provider.cor"
caller="$root/tests/integration/alloc-report/Caller.cor"
fail() { echo "$1; diagnostics: $work" >&2; exit 1; }

"$corc" index --assembly Budget "$provider" -o "$work/provider.idx"
"$corc" compile --lib "$provider" --obj -o "$work/provider.o" > "$work/provider.log" 2>&1
"$corc" compile --nostdlib "${refs[@]}" --decl-index "$work/provider.idx" --assembly Budget \
    "$caller" --obj -o "$work/caller.o" > "$work/caller.log" 2>&1
"$corc" link --closed --alloc-report all "$work/caller.o" "$work/provider.o" -o "$work/image" > "$work/link.log" 2>&1
status=0
"$work/image" > "$work/image.out" 2>&1 || status=$?
[ "$status" = 42 ] || fail "the linked program exited $status"
grep -q 'LTO heap: a collector, unwinds unbounded' "$work/link.log" || fail "the link did not find its unwinds unbounded"
grep -q 'alloc report: link past the throw budget, as if unwinds were bounded: [1-9]' "$work/link.log" \
    || fail "the link's report stopped at the reasons"
grep -q 'alloc report: link past the budget _start: \|alloc report: link past the budget m_Program_Main' "$work/link.log" \
    || fail "the report past the budget does not name the static's array"
echo 'PASS alloc-report: the link lists the sites past the throw budget'
