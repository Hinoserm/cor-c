#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
corc="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
corlink="${CORLINK:-$root/linker/bin/Release/net10.0/corlink}"
export CORC="$corc"
mkdir -p build
work="$(mktemp -d "$root/build/managed-units.XXXXXX")"
echo "Managed separate-unit acceptance: $work"
refs=()
while IFS= read -r source; do refs+=(--ref "$root/$source"); done < tests/integration/managed-runtime.sources
provider="$root/tests/integration/indexed/TraceProvider.cor"
caller="$root/tests/integration/indexed/TraceCaller.cor"
"$corc" index --assembly ManagedTrace "$provider" -o "$work/trace.idx"
# One unit owns the runtime; the independently compiled caller imports its
# declarations. This is metadata acceptance, not demand-loaded stdlib acceptance.
"$corc" compile --lib "$provider" --obj --jobs "${CORSAC_BUILD_JOBS:-1}" -o "$work/provider.o" > "$work/provider.log" 2>&1
for workers in 1 2; do
    "$corc" compile --nostdlib "${refs[@]}" --decl-index "$work/trace.idx" --assembly ManagedTrace \
        "$caller" --obj --jobs "$workers" -o "$work/caller-$workers.o" > "$work/caller-$workers.log" 2>&1
done
cmp "$work/caller-1.o" "$work/caller-2.o"
for mode in on off; do
    flags=(); if [ "$mode" = off ]; then flags=(--no-lto); fi
    "$corlink" "${flags[@]}" "$work/caller-1.o" "$work/provider.o" -o "$work/trace-$mode" > "$work/link-$mode.log" 2>&1
    status=0
    "$work/trace-$mode" > "$work/trace-$mode.log" 2>&1 || status=$?
    if [ "$status" != 42 ]; then
        echo "Managed cross-unit trace failed ($mode), exit $status; full diagnostics: $work" >&2
        cat "$work/trace-$mode.log" >&2
        exit 1
    fi
done
echo 'PASS cross-unit exception frames, exact source lines and worker-count determinism with/without LTO'
