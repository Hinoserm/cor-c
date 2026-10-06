#!/usr/bin/env bash
#
# THE BARE-METAL RUNTIME, RUN HERE.
#
#   tests/freestanding/run.sh            every test, for i386 and x86-64
#   tests/freestanding/run.sh 02         the ones whose name matches
#   tests/freestanding/run.sh --work=DIR 02
#                                        the images and logs made in DIR
#
# Each test is compiled --freestanding: the runtime is baremetal.cor, the
# entry stub is the one a kernel gets, and nothing of the Linux platform
# library is linked. host.cor gives it three Linux system calls -- a heap
# mapped for it, a write and an exit -- and nothing else, so the image runs
# as an ordinary process and its output is compared with the test's header.
#
# Header lines:
#   // expect-exit: N            the exit status (default 0)
#   // expect-output:            then the lines, each after "// "
#   // flags: ...                extra compiler flags
#   // flags-<target>: ...       the same, for one target (instead of flags)
#   // sources: a b              library sources compiled in as well, from
#                                the repository's root: what a kernel lists
#                                beside the runtime (Interlocked's, say),
#                                which a freestanding compile leaves out
#   // expect-readelf-<target>: text
#                                readelf -hl of the image shows this text
#   // run: no                   compile and read, do not run
#   // with-asm: NAME             assemble NAME.<target>.asm (corc asm --obj)
#                                and link it in (--with)
#   // expect-asm-<target>: F: a b c
#                                the disassembly of function F (its COR-C#
#                                name; Main's or the entry stub's, when F
#                                was inlined into it)
#                                contains each of a, b, c
#   // expect-no-asm-<target>: F: a b c
#                                the same function's disassembly contains
#                                none of a, b, c (expect-asm and this may each
#                                be given more than once). In either, a word
#                                [SYM] is SYM's address as objdump writes an
#                                operand (0x8049000): a read of a static
#                                named, which objdump prints only as a number
#   // expect-asm-count-<target>: F: word N
#                                the same function's disassembly has at
#                                least N lines containing word
#   // expect-bytes-<target>: SYMBOL: b b [SYM] (SYM) b ...
#                                the symbol's bytes in the image, all of them
#                                and nothing more: each b a byte in hex,
#                                [SYM] the four bytes of SYM's address, and
#                                (SYM) a rel32 that reaches SYM (a call's or
#                                a jump's, counted from the end of the four)
#
# Environment: CORC (the compiler), TARGETS (default "x86 x86-64").

set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
targets="${TARGETS:-x86 x86-64}"
work=""
made=""
case "${1:-}" in
    --work=*) work="${1#--work=}"; shift; mkdir -p "$work" || exit 2 ;;
esac
filter="${1:-}"
[ -n "$work" ] || { work="$(mktemp -d "${TMPDIR:-/tmp}/corc-freestanding.XXXXXX")"; made=1; }
passed=0
failed=0
failed_names=""

header() { sed -n "s|^// $2: *||p" "$1" | head -n 1; }

# A symbol's address and size in an image, in decimal: "address size".
symbol() {
    nm -S "$1" | awk -v s="$2" 'NF == 4 && $4 == s { print strtonum("0x" $1), strtonum("0x" $2); exit }
                                NF == 3 && $3 == s { print strtonum("0x" $1), 0; exit }'
}

# The bytes at an address of an image, `count` of them, in lower-case hex
# separated by single spaces: read from the file through the LOAD segment
# that holds the address.
image_bytes() {
    local exe="$1" address="$2" count="$3" offset
    offset="$(readelf -lW "$exe" | awk -v a="$address" '$1 == "LOAD" {
        o = strtonum($2); v = strtonum($3); n = strtonum($5)
        if (a >= v && a < v + n) { print o + a - v; exit } }')"
    [ -n "$offset" ] || return 1
    od -An -v -tx1 -j "$offset" -N "$count" "$exe" | tr -s ' \n' '  ' | sed 's/^ *//; s/ *$//'
}

# Four little-endian bytes of a value, in the same form.
word_bytes() {
    local v=$(( $1 & 0xFFFFFFFF ))
    printf '%02x %02x %02x %02x' $(( v & 255 )) $(( (v >> 8) & 255 )) $(( (v >> 16) & 255 )) $(( (v >> 24) & 255 ))
}

# An expect-asm word as the disassembly has it: [SYM] made SYM's address.
asm_word() {
    local exe="$1" word="$2" address
    case "$word" in
        \[*\])
            read -r address _ < <(symbol "$exe" "${word:1:${#word}-2}")
            if [ -n "$address" ]; then printf '0x%x' "$address"; else printf 'no-symbol-%s' "$word"; fi ;;
        *) printf '%s' "$word" ;;
    esac
}

# Whatever is wrong with a symbol's bytes against an expect-bytes line, or
# nothing.
check_bytes() {
    local exe="$1" line="$2" name tokens got want="" size address target position token
    # [SYM] is a bracket pattern to the shell: no globbing of the tokens.
    local -
    set -f
    name="${line%%:*}"
    tokens="${line#*:}"
    read -r address size < <(symbol "$exe" "$name")
    if [ -z "$address" ]; then echo "no symbol $name"; return; fi
    position=0
    for token in $tokens; do
        case "$token" in
            \[*\])
                read -r target _ < <(symbol "$exe" "${token:1:${#token}-2}")
                [ -n "$target" ] || { echo "no symbol ${token:1:${#token}-2}"; return; }
                want="$want $(word_bytes "$target")"; position=$((position + 4)) ;;
            \(*\))
                read -r target _ < <(symbol "$exe" "${token:1:${#token}-2}")
                [ -n "$target" ] || { echo "no symbol ${token:1:${#token}-2}"; return; }
                want="$want $(word_bytes $(( target - (address + position + 4) )))"; position=$((position + 4)) ;;
            *)
                want="$want $(printf '%s' "$token" | tr 'A-F' 'a-f')"; position=$((position + 1)) ;;
        esac
    done
    want="${want# }"
    if [ "$size" != "$position" ]; then echo "$name is $size bytes, not $position"; return; fi
    got="$(image_bytes "$exe" "$address" "$size")"
    [ "$got" = "$want" ] || echo "$name is: $got"
}

for source in "$here"/[0-9]*.cor; do
    name="$(basename "$source" .cor)"
    if [ -n "$filter" ] && [[ "$name" != *"$filter"* ]]; then continue; fi
    common_flags="$(header "$source" flags)"
    sources=""
    for extra in $(header "$source" sources); do sources="$sources $root/$extra"; done
    run="$(header "$source" run)"
    want_exit="$(header "$source" expect-exit)"; want_exit="${want_exit:-0}"
    awk '/^\/\/ expect-output:/{on=1;next} on&&/^\/\/ [a-z0-9-]+:/{exit} on&&/^\/\/ /{print substr($0,4);next} on{exit}' "$source" > "$work/$name.want"

    for target in $targets; do
        label="$name ($target)"
        flags="$(header "$source" "flags-$target")"
        [ -n "$flags" ] || flags="$common_flags"
        exe="$work/$name.$target"
        with=""
        asm="$(header "$source" with-asm)"
        if [ -n "$asm" ]; then
            object="$work/$name.$target.o"
            asmtarget="$target"; [ "$target" = x86 ] && asmtarget=x86-32
            if ! "$corc" asm --target "$asmtarget" "$here/$asm.$target.asm" --obj -o "$object" > "$work/$name.$target.asm.log" 2>&1; then
                sed 's/^/    /' "$work/$name.$target.asm.log" | head -n 20
                failed=$((failed + 1)); failed_names="$failed_names $label"; echo "FAIL $label (the assembly did not assemble)"; continue
            fi
            with="--with $object"
        fi
        # shellcheck disable=SC2086
        if ! "$corc" compile --target "$target" --freestanding $flags $with $sources "$here/host.cor" "$source" -o "$exe" > "$work/$name.$target.log" 2>&1; then
            sed 's/^/    /' "$work/$name.$target.log" | head -n 20
            failed=$((failed + 1)); failed_names="$failed_names $label"; echo "FAIL $label (did not compile)"; continue
        fi

        wrong=""
        disassemble() {
            objdump -d --no-show-raw-insn "$exe" | awk -v f="$1" '
                /^[0-9a-f]+ <.*>:$/ { on = index($0, f) > 0 }
                on { print }'
        }
        # A function's body: inlined into its caller, it has no symbol of its
        # own, and is then in Main, or in the entry stub Main was inlined into.
        body_of() {
            local found
            found="$(disassemble "_${1}_")"
            [ -z "$found" ] && found="$(disassemble _Main_)"
            [ -z "$found" ] && found="$(disassemble '<_start>:')"
            printf '%s' "$found"
        }
        # Every expect-asm line, and every expect-no-asm line: words the
        # function's disassembly must, and must not, contain.
        while IFS= read -r asm; do
            [ -n "$asm" ] && [ -z "$wrong" ] || continue
            function="${asm%%:*}"
            words="${asm#*:}"
            body="$(body_of "$function")"
            if [ -z "$body" ]; then
                wrong="no function $function in the disassembly"
            else
                set -f
                for word in $words; do
                    if ! grep -qF -- "$(asm_word "$exe" "$word")" <<< "$body"; then wrong="$wrong $word"; fi
                done
                set +f
                [ -n "$wrong" ] && wrong="$function lacks:$wrong"
            fi
        done < <(sed -n "s|^// expect-asm-$target: *||p" "$source")
        while IFS= read -r asm; do
            [ -n "$asm" ] && [ -z "$wrong" ] || continue
            function="${asm%%:*}"
            words="${asm#*:}"
            body="$(body_of "$function")"
            set -f
            for word in $words; do
                if grep -qF -- "$(asm_word "$exe" "$word")" <<< "$body"; then wrong="$wrong $word"; fi
            done
            set +f
            [ -n "$wrong" ] && wrong="$function has:$wrong"
        done < <(sed -n "s|^// expect-no-asm-$target: *||p" "$source")

        counted="$(header "$source" "expect-asm-count-$target")"
        if [ -z "$wrong" ] && [ -n "$counted" ]; then
            function="${counted%%:*}"
            set -- ${counted#*:}
            word="$1"; least="$2"
            body="$(objdump -d --no-show-raw-insn "$exe" | awk -v f="_${function}_" '
                /^[0-9a-f]+ <.*>:$/ { on = index($0, f) > 0 }
                on { print }')"
            [ -z "$body" ] && body="$(objdump -d --no-show-raw-insn "$exe" | awk '/^[0-9a-f]+ <.*>:$/ { on = index($0, "_Main_") > 0 } on { print }')"
            got="$(grep -cF -- "$word" <<< "$body")"
            [ "$got" -ge "$least" ] || wrong="$function has $got of $word, not $least"
        fi

        while IFS= read -r line; do
            [ -n "$line" ] && [ -z "$wrong" ] || continue
            wrong="$(check_bytes "$exe" "$line")"
        done < <(sed -n "s|^// expect-bytes-$target: *||p" "$source")

        readelf_want="$(header "$source" "expect-readelf-$target")"
        if [ -z "$wrong" ] && [ -n "$readelf_want" ] && ! readelf -hlW "$exe" | grep -qF -- "$readelf_want"; then
            wrong="readelf does not show '$readelf_want'"
        fi

        if [ -z "$wrong" ] && [ "$run" != no ]; then
            timeout 20 "$exe" > "$work/$name.$target.out" 2>&1
            status=$?
            if [ "$status" != "$want_exit" ]; then
                wrong="exit $status, not $want_exit"
            elif ! cmp -s "$work/$name.$target.out" "$work/$name.want"; then
                wrong="output differs: $(diff "$work/$name.want" "$work/$name.$target.out" | head -n 4 | tr '\n' ' ')"
            fi
        fi

        if [ -z "$wrong" ]; then
            passed=$((passed + 1)); echo "PASS $label"
        else
            failed=$((failed + 1)); failed_names="$failed_names $label"; echo "FAIL $label ($wrong)"
        fi
    done
done

# THE ASSEMBLER'S LONG MODE, against binutils: each *.listing.asm is
# assembled flat and read back by objdump, which must say exactly what its
# .expected file says.
for listing in "$here"/*.listing.asm; do
    [ -e "$listing" ] || continue
    name="$(basename "$listing" .asm)"
    if [ -n "$filter" ] && [[ "$name" != *"$filter"* ]]; then continue; fi
    if ! "$corc" asm --target x86-64 "$listing" -o "$work/$name.bin" > "$work/$name.log" 2>&1; then
        sed 's/^/    /' "$work/$name.log" | head -n 10
        failed=$((failed + 1)); failed_names="$failed_names $name"; echo "FAIL $name (did not assemble)"; continue
    fi
    objdump -D -b binary -m i386:x86-64 -M intel --no-show-raw-insn "$work/$name.bin" \
        | sed -n '/<.data>:/,$p' | tail -n +2 | awk -F'\t' '{print $2}' | sed 's/ *$//' > "$work/$name.got"
    if cmp -s "$work/$name.got" "$here/$name.expected"; then
        passed=$((passed + 1)); echo "PASS $name"
    else
        failed=$((failed + 1)); failed_names="$failed_names $name"
        echo "FAIL $name (differs: $(diff "$here/$name.expected" "$work/$name.got" | head -n 4 | tr '\n' ' '))"
    fi
done

echo
echo "$passed passed, $failed failed"
# A directory given with --work is the caller's, and kept.
[ "$failed" = 0 ] && [ -n "$made" ] && rm -rf "$work"
[ "$failed" = 0 ] || { echo "failed:$failed_names"; echo "kept: $work"; exit 1; }
