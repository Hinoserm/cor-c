#!/usr/bin/env bash
# Build Linux-native profiling workloads using a chosen preserved compiler.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
export CORC_LIB="${CORC_LIB:-$root/lib}"
corc="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
out="${OUT:-$root/build/perf}"
mkdir -p "$out"
libs=(--dynamic --libdir "${PERF_LIBDIR:-$root/build/lib}")
if [ "${PERF_SSA:-0}" = 1 ]; then libs+=(--experimental-ssa); fi
if [ "${PERF_SIZE:-0}" = 1 ]; then libs+=(--opt-size); fi
if [ "${PERF_BATCH:-0}" = 1 ]; then libs+=(--experimental-batch); fi
"$corc" compile "${libs[@]}" tests/perf/allocation.cor --cpu 486 -o "$out/allocation"
"$corc" compile "${libs[@]}" tests/perf/loops.cor --cpu 486 -o "$out/loops"
"$corc" compile "${libs[@]}" tests/perf/division.cor --cpu 486 -o "$out/division"
"$corc" compile "${libs[@]}" tests/perf/power-divide.cor --cpu 486 -o "$out/power-divide"
"$corc" compile "${libs[@]}" tests/perf/byte-swap.cor --cpu 486 -o "$out/byte-swap"
"$corc" compile "${libs[@]}" tests/perf/byte-packing.cor --cpu 486 -o "$out/byte-packing"
"$corc" compile "${libs[@]}" tests/perf/arithmetic.cor --cpu 486 -o "$out/arithmetic"
"$corc" compile "${libs[@]}" tests/perf/constant-chains.cor --cpu 486 -o "$out/constant-chains"
"$corc" compile "${libs[@]}" tests/perf/owned-paths.cor --cpu 486 -o "$out/owned-paths"
"$corc" compile "${libs[@]}" os/lib/crypto_base.cor os/lib/crypto_chacha.cor tests/perf/chacha.cor --cpu 486 -o "$out/chacha"
"$corc" compile "${libs[@]}" os/lib/crypto_base.cor os/lib/crypto_field25519.cor \
  os/lib/crypto_x25519.cor tests/perf/crypto.cor --cpu 486 -o "$out/crypto"
objdump -d "$out/crypto" > "$out/crypto.asm"
nm -S --size-sort "$out/crypto" > "$out/crypto-symbols.txt"
