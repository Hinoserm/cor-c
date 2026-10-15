#!/bin/bash
# compile-units.sh CORC TREE OUT [--flag ...] [--] UNIT ...
#
# Compiles the named units of the compiler in TREE (Binder, Lowering, ... --
# file names without .cs) as the project build compiles them, in one
# compile-project process, with --gc-stats and any flags given before the
# units (--alloc-sample, --gc-census, --gc-dead-census, --gc-parent-census,
# --report-phases). TREE is a copy of this repository whose declaration index
# is built (see README.md); CORC is a native corc or "dotnet .../corc.dll".
# Prints the wall seconds, the peak resident KB and the collector's figures;
# the whole log is OUT/log, the objects OUT/*.o.
#
# With MEMORY set (MEMORY=256M) each unit is compiled alone in its own
# process under that cgroup limit instead, one line a unit in OUT/results:
# its exit status, seconds, peak KB and name. A unit list file may be given
# as the only unit, @FILE (units-256-hard.txt).
set -u
CORC=$1; TREE=$(cd "$2" && pwd); OUT=$3; shift 3
flags=(); while [ $# -gt 0 ] && [ "${1#--}" != "$1" ]; do [ "$1" = -- ] && { shift; break; }; flags+=("$1"); shift; done
units=(); for u in "$@"; do if [ "${u#@}" != "$u" ]; then mapfile -t -O ${#units[@]} units < "${u#@}"; else units+=("$u"); fi; done
INDEX=$TREE/compiler/obj/cor-c/Release/net10.0/declarations.idx
[ -f "$INDEX" ] || { echo "no declaration index at $INDEX (README.md)" >&2; exit 1; }
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
# The options the project build passes a compiler unit (corsac.build's corc
# project, Release, the 486 baseline), less the per-run ones given below.
options=(--nostdlib --obj --assembly corc --cpu=486 --tune=486 --fpu=x87 --disable-mmx --disable-3dnow -Wno-error
  --define TRACE --define NET --define NETCOREAPP --define NET10_0 --define NET5_0_OR_GREATER --define NET6_0_OR_GREATER
  --define NET7_0_OR_GREATER --define NET8_0_OR_GREATER --define NET9_0_OR_GREATER --define NET10_0_OR_GREATER
  --using System --using System.Collections.Generic --using System.IO --using System.Linq --using System.Net.Http
  --using System.Threading --using System.Threading.Tasks)
refs=(); for l in $($CORC --lib-root "$TREE" library-sources); do refs+=(--ref "$l"); done
line() { local f; f=$(find "$TREE/compiler/src" "$TREE/linker/src" -name "$1.cs" | head -1); [ -n "$f" ] || { echo "no unit $1" >&2; return 1; }
  printf "%s\t%s\t%s\tlib\n" "$f" "$OUT/$1.o" "$OUT/$1.o.deps"; }
if [ -z "${MEMORY:-}" ]; then
  : > "$OUT/units.tsv"; for u in "${units[@]}"; do line "$u" >> "$OUT/units.tsv" || exit 1; done
  /usr/bin/time -o "$OUT/time" -f "%e %M" env TMPDIR="$OUT" $CORC --lib-root "$TREE" --gc-stats "${flags[@]}" compile-project \
    --units "$OUT/units.tsv" --jobs 1 --decl-index "$INDEX" "${options[@]}" "${refs[@]}" > "$OUT/log" 2>&1
  echo "rc=$? seconds,KB=$(cat "$OUT/time" | tail -1) objects=$(ls "$OUT"/*.o 2>/dev/null | wc -l)"
  grep -a -E "^gc: (freed by the program MB|freed by the collector MB|given back by regions MB|freed by the program percent|peak live|peak heap)" "$OUT/log" | tr '\n' ' '; echo
else
  : > "$OUT/results"
  [ ${#units[@]} -gt 0 ] || mapfile -t units < <(find "$TREE/compiler/src" -name "*.cs" -printf "%f\n" | sed 's/\.cs$//' | sort)
  for u in "${units[@]}"; do
    line "$u" > "$OUT/one.tsv" || continue
    /usr/bin/time -o "$OUT/time" -f "%e %M" timeout -s KILL 120 systemd-run --user --scope -q -p MemoryMax="$MEMORY" -p MemorySwapMax=0 \
      env TMPDIR="$OUT" $CORC --lib-root "$TREE" "${flags[@]}" compile-project --units "$OUT/one.tsv" --jobs 1 --decl-index "$INDEX" \
      "${options[@]}" "${refs[@]}" > "$OUT/$u.log" 2>&1
    echo "$? $(tail -1 "$OUT/time") $u" >> "$OUT/results"
  done
  echo "$(awk '$1 == 0' "$OUT/results" | wc -l) of ${#units[@]} units compiled under $MEMORY"
fi
