#!/usr/bin/env bash
# Compare already-built tools, alternating order; never benchmark failed compiles.
set -euo pipefail
if [ "$#" -ne 3 ]; then
    echo "usage: bash $0 managed-corc.dll native-corc output-directory" >&2
    exit 2
fi
managed=$(realpath "$1")
native=$(realpath "$2")
mkdir -p "$3"
output=$(realpath "$3")
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
export CORC_LIB="$root"
printf 'mode,round,seconds,user_seconds,system_seconds,peak_kib\n' > "$output/timings.csv"
for round in 1 2 3; do
    modes='jit aot'
    if [ "$round" = 2 ]; then modes='aot jit'; fi
    for mode in $modes; do
        command=("$native")
        if [ "$mode" = jit ]; then command=(dotnet "$managed"); fi
        /usr/bin/time -a -o "$output/timings.csv" \
            -f "$mode,$round,%e,%U,%S,%M" \
            "${command[@]}" compile --jobs 1 tests/benchmarks/allocation.cor \
            -o "$output/allocation-$mode" > "$output/$mode-$round.log" 2>&1
    done
done
cmp "$output/allocation-jit" "$output/allocation-aot"
"$output/allocation-jit" > "$output/result-jit.log"
"$output/allocation-aot" > "$output/result-aot.log"
cat "$output/timings.csv"
