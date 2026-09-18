#!/usr/bin/env bash
# Build Linux-native profiling workloads using a chosen preserved compiler.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
export CORC_LIB="${CORC_LIB:-$root}"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
out="${OUT:-$root/build/perf}"
mkdir -p "$out"
libs=(--dynamic --libdir "${PERF_LIBDIR:-$root/build/lib}")
if [ "${PERF_STATIC:-0}" = 1 ]; then libs=(); fi
libs+=(--cpu "${CPU:-486}" --jobs "${JOBS:-4}")
if [ "${PERF_DISABLE_MMX:-0}" = 1 ]; then libs+=(--disable-mmx); fi
if [ "${PERF_SSA:-0}" = 1 ]; then libs+=(--experimental-ssa); fi
if [ "${PERF_SIZE:-0}" = 1 ]; then libs+=(--opt-size); fi
if [ "${PERF_BATCH:-0}" = 1 ]; then libs+=(--experimental-batch); fi
read -r -a workloads <<< "${BENCHMARKS:-allocation loops division power-divide byte-swap byte-packing arithmetic constant-chains owned-paths}"
for workload in "${workloads[@]}"; do
  sources=()
  case "$workload" in
    crypto|chacha)
      : "${CRYPTO_ROOT:?Set CRYPTO_ROOT to a read-only CORSAC86 os/lib directory for crypto workloads}"
      sources+=("$CRYPTO_ROOT/crypto_base.cor")
      if [ "$workload" = crypto ]; then sources+=("$CRYPTO_ROOT/crypto_field25519.cor" "$CRYPTO_ROOT/crypto_x25519.cor")
      else sources+=("$CRYPTO_ROOT/crypto_chacha.cor"); fi ;;
    allocation|loops|division|power-divide|byte-swap|byte-packing|arithmetic|constant-chains|owned-paths) ;;
    *) echo "Unknown benchmark: $workload" >&2; exit 2 ;;
  esac
  sources+=("tests/benchmarks/$workload.cor")
  "$corc" compile "${libs[@]}" "${sources[@]}" -o "$out/$workload"
  objdump -d "$out/$workload" > "$out/$workload.asm"
  nm -S --size-sort "$out/$workload" > "$out/$workload-symbols.txt"
  sha256sum "${sources[@]}" > "$out/$workload-sources.sha256"
done
