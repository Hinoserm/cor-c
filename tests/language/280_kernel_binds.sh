#!/usr/bin/env bash
# Compile a committed OS snapshot with this compiler; never edit its checkout.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
os="${CORSAC_ROOT:-$root/../corsac86-integration}"
mkdir -p "$root/build"
work="$(mktemp -d "$root/build/kernel-binds.XXXXXX")"
trap 'echo "Kernel compile failed; diagnostics: $work" >&2' ERR
revision="$(git -C "$os" rev-parse HEAD)"
mkdir -p "$work/source"
git -C "$os" archive "$revision" | tar -x -C "$work/source"
printf '%s\n' "$revision" > "$work/revision.txt"
bash "$work/source/tools/kconfig" --root "$work/source/os/kernel" \
    --out "$work/config" x86-486-isa > "$work/config.log" 2>&1
sources=()
while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    sources+=("$line")
done < "$work/config/sources.list"
read -r -a compiler <<< "${CORC:-$root/compiler/bin/Release/net10.0/corc}"
"${compiler[@]}" compile "${sources[@]}" --freestanding --obj --cpu 486 \
    --asm-entry corc_start --tls-gs --stats -o "$work/kernel.o" \
    > "$work/compile.log" 2>&1
test -s "$work/kernel.o"
echo "Kernel compilation passed: $work"
