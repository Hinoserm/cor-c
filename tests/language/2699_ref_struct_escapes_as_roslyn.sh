#!/usr/bin/env bash
#
# The whole of test 2699: every diagnostic Roslyn gives the file's ref
# struct escapes, by its code and its place, and no other of those codes --
# 2699_ref_struct_escapes_as_roslyn.expected is .NET 10's build of the same
# file. (The .cor itself checks the first only.)
#
#     tests/language/2699_ref_struct_escapes_as_roslyn.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/ref-escapes.XXXXXX")}"

$corc compile -Wno-error "$here/2699_ref_struct_escapes_as_roslyn.cor" -o "$work/rs" --cpu=athlon64 --disable-3dnow > "$work/rs.log" 2>&1
codes='CS8347|CS8350|CS8352|CS8353'
grep -oE "2699_ref_struct_escapes_as_roslyn.cor\([0-9]+,[0-9]+\): (error|warning):? ($codes)" "$work/rs.log" \
    | sed -E 's/.*\.cor(\([0-9]+,[0-9]+\)): (error|warning):? (CS[0-9]+)/\1 \3/' | sort -u > "$work/said"
sort -u "$here/2699_ref_struct_escapes_as_roslyn.expected" > "$work/wanted"
if diff "$work/wanted" "$work/said" > "$work/diff"; then
    echo "PASS every ref struct escape as Roslyn's ($(wc -l < "$work/wanted"))"
else
    echo "FAIL ref struct escapes differ from Roslyn's (< Roslyn, > here)"; cat "$work/diff"; exit 1
fi
