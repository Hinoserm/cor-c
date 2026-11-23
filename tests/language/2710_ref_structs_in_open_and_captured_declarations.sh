#!/usr/bin/env bash
#
# The whole of test 2710: every diagnostic Roslyn gives the file, by its
# code and its place, and no other of those codes --
# 2710_ref_structs_in_open_and_captured_declarations.expected is .NET 10's build of the same
# file. (The .cor itself checks the first only.)
#
#     tests/language/2710_ref_structs_in_open_and_captured_declarations.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/ref_structs_in_open_and_captured_declarations.XXXXXX")}"

$corc compile -Wno-error "$here/2710_ref_structs_in_open_and_captured_declarations.cor" -o "$work/rs" --cpu=athlon64 --disable-3dnow > "$work/rs.log" 2>&1
codes='CS4007|CS0611|CS9244|CS8345|CS0029|CS0103'
grep -oE "2710_ref_structs_in_open_and_captured_declarations.cor\([0-9]+,[0-9]+\): (error|warning):? ($codes)" "$work/rs.log" \
    | sed -E 's/.*\.cor(\([0-9]+,[0-9]+\)): (error|warning):? (CS[0-9]+)/\1 \3/' | sort -u > "$work/said"
sort -u "$here/2710_ref_structs_in_open_and_captured_declarations.expected" > "$work/wanted"
if diff "$work/wanted" "$work/said" > "$work/diff"; then
    echo "PASS every diagnostic as Roslyn's ($(wc -l < "$work/wanted"))"
else
    echo "FAIL diagnostics differ from Roslyn's (< Roslyn, > here)"; cat "$work/diff"; exit 1
fi
