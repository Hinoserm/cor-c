#!/usr/bin/env bash
#
# THE BARE-METAL RUNTIME, RUN HERE.
#
#   tests/freestanding/run.sh            every test, for i386 and x86-64
#   tests/freestanding/run.sh 02         the ones whose name matches
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
#   // run: no                   compile and read, do not run
#   // expect-asm-<target>: F: a b c
#                                the disassembly of function F (its COR-C#
#                                name; Main's or the entry stub's, when F
#                                was inlined into it)
#                                contains each of a, b, c
#
# Environment: CORC (the compiler), TARGETS (default "x86 x86-64").

set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
targets="${TARGETS:-x86 x86-64}"
filter="${1:-}"
work="$(mktemp -d "${TMPDIR:-/tmp}/corc-freestanding.XXXXXX")"
passed=0
failed=0
failed_names=""

header() { sed -n "s|^// $2: *||p" "$1" | head -n 1; }

for source in "$here"/[0-9]*.cor; do
    name="$(basename "$source" .cor)"
    if [ -n "$filter" ] && [[ "$name" != *"$filter"* ]]; then continue; fi
    flags="$(header "$source" flags)"
    run="$(header "$source" run)"
    want_exit="$(header "$source" expect-exit)"; want_exit="${want_exit:-0}"
    awk '/^\/\/ expect-output:/{on=1;next} on&&/^\/\/ [a-z0-9-]+:/{exit} on&&/^\/\/ /{print substr($0,4);next} on{exit}' "$source" > "$work/$name.want"

    for target in $targets; do
        label="$name ($target)"
        exe="$work/$name.$target"
        # shellcheck disable=SC2086
        if ! "$corc" compile --target "$target" --freestanding $flags "$here/host.cor" "$source" -o "$exe" > "$work/$name.$target.log" 2>&1; then
            sed 's/^/    /' "$work/$name.$target.log" | head -n 20
            failed=$((failed + 1)); failed_names="$failed_names $label"; echo "FAIL $label (did not compile)"; continue
        fi

        wrong=""
        asm="$(header "$source" "expect-asm-$target")"
        if [ -n "$asm" ]; then
            function="${asm%%:*}"
            words="${asm#*:}"
            disassemble() {
                objdump -d --no-show-raw-insn "$exe" | awk -v f="$1" '
                    /^[0-9a-f]+ <.*>:$/ { on = index($0, f) > 0 }
                    on { print }'
            }
            body="$(disassemble "_${function}_")"
            # Inlined into its caller, it has no symbol of its own: then it
            # is in Main, or in the entry stub Main was inlined into.
            [ -z "$body" ] && body="$(disassemble _Main_)"
            [ -z "$body" ] && body="$(disassemble '<_start>:')"
            if [ -z "$body" ]; then
                wrong="no function $function in the disassembly"
            else
                for word in $words; do
                    if ! grep -qF -- "$word" <<< "$body"; then wrong="$wrong $word"; fi
                done
                [ -n "$wrong" ] && wrong="$function lacks:$wrong"
            fi
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

echo
echo "$passed passed, $failed failed"
[ "$failed" = 0 ] && rm -rf "$work"
[ "$failed" = 0 ] || { echo "failed:$failed_names"; echo "kept: $work"; exit 1; }
