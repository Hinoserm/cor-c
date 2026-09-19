#!/usr/bin/env bash
# Compare preserved compiler executables on the identical indexed kernel inputs.
set -euo pipefail
if [ "$#" -ne 5 ]; then
    echo "usage: bash $0 before-corc after-corc os-root declaration-index output-directory" >&2
    exit 2
fi
before=$(realpath "$1")
after=$(realpath "$2")
osroot=$(realpath "$3")
index=$(realpath "$4")
mkdir -p "$5"
output=$(realpath "$5")
cd "$osroot"
printf 'source,variant,elapsed_seconds,user_seconds,system_seconds,peak_kib\n' > "$output/timings.csv"
for source in os/kernel/arch/x86/irqspinlock.cor os/kernel/drivers/kbddecode.cor os/kernel/fs/procfs.cor; do
    name=$(basename "$source" .cor)
    for variant in before after; do
        compiler="$before"
        if [ "$variant" = after ]; then compiler="$after"; fi
        /usr/bin/time -a -o "$output/timings.csv" -f "$source,$variant,%e,%U,%S,%M" \
            "$compiler" compile "$source" --nostdlib --obj --jobs 1 \
            --decl-index "$index" --assembly CORSAC.kernel --freestanding \
            --asm-entry corc_start --tls-gs --cpu 486 --lib \
            -o "$output/$name-$variant.o" > "$output/$name-$variant.log" 2>&1
    done
    cmp "$output/$name-before.o" "$output/$name-after.o"
done
cat "$output/timings.csv"
