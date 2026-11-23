#!/usr/bin/env bash
#
# The whole of test 2701: every diagnostic Roslyn gives the file's span
# tuples, by its code and its place, and no other of those codes --
# 2701_ref_struct_tuples_as_roslyn.expected is .NET 10's build of the same
# file. (The .cor itself checks the first only.)
#
#     tests/language/2701_ref_struct_tuples_as_roslyn.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/ref-tuples.XXXXXX")}"

$corc compile -Wno-error "$here/2701_ref_struct_tuples_as_roslyn.cor" -o "$work/rs" --cpu=athlon64 --disable-3dnow > "$work/rs.log" 2>&1
codes='CS9244|CS0103|CS8130|CS8131|CS0029'
grep -oE "2701_ref_struct_tuples_as_roslyn.cor\([0-9]+,[0-9]+\): (error|warning):? ($codes)" "$work/rs.log" \
    | sed -E 's/.*\.cor(\([0-9]+,[0-9]+\)): (error|warning):? (CS[0-9]+)/\1 \3/' | sort -u > "$work/said"
sort -u "$here/2701_ref_struct_tuples_as_roslyn.expected" > "$work/wanted"
if diff "$work/wanted" "$work/said" > "$work/diff"; then
    echo "PASS every span tuple diagnostic as Roslyn's ($(wc -l < "$work/wanted"))"
else
    echo "FAIL span tuple diagnostics differ from Roslyn's (< Roslyn, > here)"; cat "$work/diff"; exit 1
fi
