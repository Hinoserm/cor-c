#!/usr/bin/env bash
# Native compiler workload. Bootstrap timing is preparation, never a native result.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
revision="${SELF_REVISION:-HEAD}"
revision="$(git rev-parse --verify "$revision^{commit}")"
bootstrap="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
bootstrap="$(realpath "$bootstrap")"
test -x "$bootstrap"
mkdir -p "$root/build"
work="$(mktemp -d "$root/build/selfcompile.XXXXXX")"
snapshot="$work/source"
mkdir "$snapshot"
printf 'selfcompile work: %s\n' "$work"
git archive "$revision" compiler linker runtime stdlib tests/integration/managed-runtime.sources tests/benchmarks/selfcompile-smoke.cor | tar -x -C "$snapshot"
cd "$snapshot"
export CORC_LIB="$snapshot"
mapfile -t libraries < tests/integration/managed-runtime.sources
test "${#libraries[@]}" -gt 0
mapfile -t sources < <(rg --files compiler/src linker/src -g '*.cs' -g '!linker/src/driver/Program.cs' -g '!**/tests/**' -g '!**/bin/**' -g '!**/obj/**' -g '!**/Legacy/**' | LC_ALL=C sort)
test "${#sources[@]}" -gt 0
jobs="${SELF_JOBS:-1}"
if ! [[ "$jobs" =~ ^[1-9][0-9]?$ ]] || [ "$jobs" -gt 64 ]; then
    echo "SELF_JOBS must be a worker count from 1 to 64" >&2
    exit 2
fi
flags=(--nostdlib -Wno-error --cpu 486 -D COR_SELFHOST_BENCHMARK --jobs "$jobs")
case "${SELF_PROFILE:-default}" in
    default) ;;
    size) flags+=(--opt-size) ;;
    batch) flags+=(--experimental-batch) ;;
    batch-size) flags+=(--experimental-batch --opt-size) ;;
    *) echo "unknown SELF_PROFILE" >&2; exit 2 ;;
esac
printf 'revision=%s\nprofile=%s\nbootstrap=%s\ncompiler-files=%s\n' "$revision" "${SELF_PROFILE:-default}" "$bootstrap" "${#sources[@]}" > "$work/manifest.txt"
printf 'backend-workers=%s\n' "$jobs" >> "$work/manifest.txt"
sha256sum "$bootstrap" "${libraries[@]}" "${sources[@]}" > "$work/input-sha256.txt"
if [ -f "$bootstrap.dll" ]; then sha256sum "$bootstrap.dll" >> "$work/input-sha256.txt"; fi
printf '%s\n' "${libraries[@]}" "${sources[@]}" > "$work/sources.list"
measure() {
    local name="$1"; shift
    printf 'selfcompile phase: %s\n' "$name"
    local status=0
    /usr/bin/time -f 'elapsed-seconds=%e user-seconds=%U system-seconds=%S peak-kib=%M exit=%x' \
        -o "$work/$name.time" timeout "${SELF_TIMEOUT:-3600}" "$@" > "$work/$name.log" 2>&1 || status=$?
    # time's %x may say zero after a signal; preserve the shell status too.
    printf 'command-status=%s\n' "$status" >> "$work/$name.time"
    if [ "$status" -ne 0 ]; then
        printf 'selfcompile FAILED phase=%s status=%s; logs retained at %s\n' "$name" "$status" "$work" >&2
        tail -60 "$work/$name.log" >&2
        exit "$status"
    fi
}
measure bootstrap "$bootstrap" compile "${flags[@]}" "${libraries[@]}" "${sources[@]}" -o "$work/stage1"
test -s "$work/stage1"
file "$work/stage1" > "$work/stage1.file"
measure reference-smoke-compile "$bootstrap" compile "${flags[@]}" "${libraries[@]}" tests/benchmarks/selfcompile-smoke.cor -o "$work/smoke-reference"
measure reference-smoke-run "$work/smoke-reference"
printf 'selfcompile smoke passed\n' | cmp - "$work/reference-smoke-run.log"
measure smoke-compile "$work/stage1" compile "${flags[@]}" "${libraries[@]}" tests/benchmarks/selfcompile-smoke.cor -o "$work/smoke"
measure smoke-run "$work/smoke"
cmp "$work/reference-smoke-run.log" "$work/smoke-run.log"
cmp "$work/smoke-reference" "$work/smoke"
measure native-selfcompile "$work/stage1" compile "${flags[@]}" "${libraries[@]}" "${sources[@]}" -o "$work/stage2"
test -s "$work/stage2"
measure stage2-smoke-compile "$work/stage2" compile "${flags[@]}" "${libraries[@]}" tests/benchmarks/selfcompile-smoke.cor -o "$work/smoke2"
measure stage2-smoke-run "$work/smoke2"
cmp "$work/smoke-run.log" "$work/stage2-smoke-run.log"
cmp "$work/smoke" "$work/smoke2"
stat -c '%n bytes=%s' "$work/stage1" "$work/stage2" >> "$work/manifest.txt"
if cmp -s "$work/stage1" "$work/stage2"; then
    printf 'stage-parity=identical\n' >> "$work/manifest.txt"
else
    printf 'stage-parity=different; investigate before self-host acceptance\n' >> "$work/manifest.txt"
    printf 'selfcompile FAILED compiler-generation parity; artifacts retained at %s\n' "$work" >&2
    exit 1
fi
printf 'selfcompile completed; native timing: %s/native-selfcompile.time\n' "$work"
