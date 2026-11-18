#!/usr/bin/env bash
#
# The compare-and-swap that puts a static struct's first block in place is
# followed by the card mark of the static's word.
#
#     tests/language/2628_static_struct_block_card.sh
#
# The half of test 2628 a program this small cannot show from inside itself:
# every `lock cmpxchg` on Holder.Last in the compiled Main has, within the
# few instructions after it, the load of Runtime.Cards that begins a card
# mark (its test and the byte store follow, in line or out of it), as a
# reference's store has. Without it the block is swept at the next minor collection.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
mkdir -p "$root/build"
work="${WORK:-$(mktemp -d "$root/build/static-struct-card.XXXXXX")}"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

if $corc compile "$here/2628_static_struct_block_card.cor" -o "$work/card" > "$work/card.log" 2>&1
then pass "the program compiles"
else fail "the program compiles"; cat "$work/card.log"; exit 1
fi

objdump -d --no-show-raw-insn "$work/card" | awk '/<m_Program_Main[^>]*>:$/{p=1} p&&/^$/{p=0} p' > "$work/main.dis"
swaps=$(grep -c 'lock cmpxchg' "$work/main.dis")
if [ "$swaps" -ge 1 ]; then pass "Main puts the static's block in place with a compare-and-swap ($swaps)"
else fail "Main puts the static's block in place with a compare-and-swap (none found)"; fi

# Each swap, and the card table's address read among the four instructions
# after it (Runtime.Cards, then its test and the byte store, in line or out).
cards=$(nm "$work/card" | awk '$3 ~ /^s_Runtime_Cards_/ {print $1}' | sed 's/^0*//')
if [ -n "$cards" ]; then pass "the image has Runtime.Cards (0x$cards)"; else fail "the image has Runtime.Cards"; fi
marked=$(awk -v cards="0x$cards," '/lock cmpxchg/{left=4; next} left>0{ if (index($0, cards) > 0) {n++; left=0; next} left-- } END{print n+0}' "$work/main.dis")
if [ "$marked" -eq "$swaps" ] && [ "$swaps" -ge 1 ]; then pass "each is followed by the card mark ($marked of $swaps)"
else fail "each is followed by the card mark ($marked of $swaps)"; fi

if [ "$failures" -eq 0 ]; then echo "PASS 2628_static_struct_block_card"; exit 0; fi
echo "FAIL 2628_static_struct_block_card ($failures)"; exit 1
