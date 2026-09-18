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
"$corc" index --assembly Initialized tests/integration/indexed/InitB.cor tests/integration/indexed/InitA.cor \
    tests/integration/indexed/InitConstants.cor -o "$work/init.idx"
for part in A B; do
    "$corc" compile --nostdlib --lib "${refs[@]}" --decl-index "$work/init.idx" --assembly Initialized \
        "tests/integration/indexed/Init$part.cor" --obj -o "$work/init-$part.o" > "$work/init-$part.log" 2>&1
done
"$corc" compile --nostdlib "${refs[@]}" --decl-index "$work/init.idx" --assembly Initialized \
    tests/integration/indexed/InitCaller.cor --obj -o "$work/init-caller.o" > "$work/init-caller.log" 2>&1
"$corlink" "$work/init-caller.o" "$work/init-B.o" "$work/init-A.o" "$work/provider.o" -o "$work/init" > "$work/init-link.log" 2>&1
status=0
"$work/init" > "$work/init-run.log" 2>&1 || status=$?
if [ "$status" != 42 ]; then echo "Partial initialization failed, exit $status; diagnostics: $work" >&2; cat "$work/init-run.log" >&2; exit 1; fi
echo 'PASS partial static auto-properties, lexical initializer scopes, instance constructors and once-only static initialization'
