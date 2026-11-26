#!/usr/bin/env bash
#
# The half of test 2456 its output cannot show: that a copy or a fill of a
# length known only as it runs goes the runtime's streaming way at 256 KB
# and over, in BOTH modes -- i386's by MOVNTQ through an MMX register, long
# mode's by MOVNTI of a general register (it had none: every such block
# went by REP MOVS, through the caches).
#
#     tests/language/2456_packed_scans_and_streaming_copies.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/streams.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

# A function's lines in a listing: from its label to the blank line after it.
body() { awk -v n="$2" 'index($0, n) == 1 && /:$/ { on = 1; next } on && /^$/ { exit } on { sub(/^ +/, ""); print }' "$1"; }

for target in x86 x86-64; do
    flags="--cpu=athlon64"; [ $target = x86 ] && flags="$flags --disable-3dnow"
    listing="$work/streams-$target.s"
    if $corc compile "$here/2456_packed_scans_and_streaming_copies.cor" --asm --target $target $flags > "$listing" 2> "$listing.err" && [ -s "$listing" ]
    then pass "$target: the listing is printed"
    else fail "$target: the listing is printed"; head -5 "$listing.err"; continue
    fi
    store=movntq; [ $target = x86-64 ] && store=movnti
    for routine in StreamCopy StreamFill; do
        code=$(body "$listing" "m_Runtime_${routine}_")
        stores=$(echo "$code" | grep -cE "^$store ")
        if [ "$stores" -eq 8 ]; then pass "$target: Runtime.$routine stores a cache line by eight $store"; else fail "$target: Runtime.$routine stores a cache line by eight $store ($stores)"; echo "$code" | head -40; fi
        if echo "$code" | grep -qE '^sfence$'; then pass "$target: Runtime.$routine ends with the fence"; else fail "$target: Runtime.$routine ends with the fence"; fi
        # Long mode leaves no MMX state: no EMMS, and no MMX or XMM register in the routine at all.
        if [ $target = x86-64 ]; then
            if echo "$code" | grep -qE 'emms|[^x]mm[0-7]|xmm[0-9]'; then fail "$target: Runtime.$routine uses no packed register"; echo "$code" | grep -E 'emms|mm[0-7]' | head -4; else pass "$target: Runtime.$routine uses no packed register"; fi
        fi
        calls=$(grep -cE "^ +call m_Runtime_${routine}_" "$listing")
        if [ "$calls" -ge 1 ]; then pass "$target: $calls blocks of a length known only as it runs call Runtime.$routine"; else fail "$target: some block calls Runtime.$routine"; fi
        # Not from inside itself: its own short and overlapping blocks go the ordinary way.
        if echo "$code" | grep -qE "^call m_Runtime_${routine}_"; then fail "$target: Runtime.$routine never calls itself"; else pass "$target: Runtime.$routine never calls itself"; fi
    done
    # The call is on the far side of a compare with 256 KB.
    if grep -qE '^ +cmp [a-z0-9]+, 262144$' "$listing"; then pass "$target: a length is compared with 256 KB"; else fail "$target: a length is compared with 256 KB"; fi
done

echo
if [ $failures -eq 0 ]; then echo "all passed"; exit 0; else echo "$failures failed"; exit 1; fi
