#!/usr/bin/env bash
#
# Language test runner for the COR-C# compiler.
#
#   tests/lang/run.sh            run every tests/lang/*.cor
#   tests/lang/run.sh strings    run the one whose file is *strings*.cor
#   tests/lang/run.sh -v hello   also show the compiler's output
#   tests/lang/run.sh --opt-size  exercise size-oriented compilation
#   tests/lang/run.sh --experimental-batch  exercise the staged optimizer group
#
# Each test is compiled to a native Linux executable together with the
# standard and system libraries, run with a timeout, and its stdout and exit
# code are compared with the header comment at the top of the file (the
# format is described in README.md). Exits non-zero if any test failed.
#
# Environment:
#   CORC     command that runs the compiler (default: the Release build of
#            compiler/corc.csproj, built here if it is missing)
#   CORC_LIBS  space-separated library sources compiled before each test

#            (default: the runtime and standard-library sources in this repository)
#   TIMEOUT  seconds a compiled test may run (default: 10)
#   KEEP     set to 1 to keep the build directory for inspection

set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
corc_for_libs="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
# Asked of the compiler, not kept in a list that has to match it.
libs="${CORC_LIBS:-$("$corc_for_libs" library-sources | tr '\n' ' ')}"
timeout_s="${TIMEOUT:-10}"
verbose=0
filter=""
compiler_flags=()

for arg in "$@"; do
    case "$arg" in
        -v|--verbose)
            verbose=1
            ;;
        --opt-size|--experimental-batch)
            compiler_flags+=("$arg")
            ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            filter="$arg"
            ;;
    esac
done

# ---- find or build the compiler --------------------------------------------

if [ -z "${CORC:-}" ]; then
    dll="$(ls -1t "$root"/compiler/bin/Release/net*/corc.dll 2>/dev/null | head -1 || true)"
    if [ -z "$dll" ]; then
        echo "building compiler ..."
        if ! (cd "$root/compiler" && dotnet build -c Release -v q --nologo >/dev/null); then
            echo "compiler build failed" >&2
            exit 2
        fi
        dll="$(ls -1t "$root"/compiler/bin/Release/net*/corc.dll 2>/dev/null | head -1 || true)"
    fi
    if [ -z "$dll" ]; then
        echo "cannot find corc.dll under compiler/bin/Release" >&2
        exit 2
    fi
    CORC="dotnet $dll"
fi

# The compiler answers absolute paths; CORC_LIBS may be relative to the root.
lib_path() { case "$1" in /*) printf '%s' "$1" ;; *) printf '%s' "$root/$1" ;; esac; }

for lib in $libs; do
    if [ ! -f "$(lib_path "$lib")" ]; then
        echo "library source $lib not found (set CORC_LIBS to override)" >&2
        exit 2
    fi
done

if command -v timeout >/dev/null 2>&1; then
    run_with_timeout() {
        timeout --foreground -k 2 "$timeout_s" "$@"
    }
else
    run_with_timeout() {
        "$@"
    }
fi

# ---- header parsing --------------------------------------------------------

# Prints the value of a "// key: value" header line, or nothing.
header_value() {
    sed -n "s|^// $2: *||p" "$1" | head -1
}

# Prints the expected stdout: every "// text" line after "// expect-output:"
# up to the first line that is not one -- a bare "//" or the code itself.
expected_output() {
    awk '
        /^\/\/ expect-output:/ { on = 1; next }
        on && /^\/\/ / { print substr($0, 4); next }
        on { exit }
    ' "$1"
}

has_expected_output() {
    grep -q '^// expect-output:' "$1"
}

# ---- run -------------------------------------------------------------------

work="${TMPDIR:-/tmp}/corc-lang-tests.$$"
mkdir -p "$work"
cleanup() {
    if [ "${KEEP:-0}" != "1" ]; then
        rm -rf "$work"
    fi
}
trap cleanup EXIT

passed=0
failed=0
failed_names=""

tests=()
for f in "$here"/*.cor; do
    [ -e "$f" ] || continue
    name="$(basename "$f" .cor)"
    if [ -n "$filter" ] && [[ "$name" != *"$filter"* ]]; then
        continue
    fi
    tests+=("$f")
done

if [ "${#tests[@]}" -eq 0 ]; then
    echo "no tests match '$filter'" >&2
    exit 2
fi

lib_paths=""
for lib in $libs; do
    lib_paths="$lib_paths $(lib_path "$lib")"
done

for f in "${tests[@]}"; do
    name="$(basename "$f" .cor)"
    timeout_s="${TIMEOUT:-$(header_value "$f" timeout)}"
    timeout_s="${timeout_s:-10}"
    if ! [[ "$timeout_s" =~ ^[1-9][0-9]*$ ]]; then
        echo "invalid positive timeout for $name: $timeout_s" >&2
        exit 2
    fi
    exe="$work/$name"
    want_exit="$(header_value "$f" expect-exit)"
    [ -n "$want_exit" ] || want_exit=0

    # A test may name extra sources -- kernel code under test on Linux, say
    # -- with "// sources: path ..." relative to the repository root.
    extra=""
    for src in $(header_value "$f" sources); do
        extra="$extra $root/$src"
    done

    # A test may ask for compiler flags of its own with "// flags: ...":
    # one about an optimiser mode holds that mode to what it states, whatever
    # the run was asked for.
    own_flags="$(header_value "$f" flags)"

    # WARNINGS ARE NOT ERRORS HERE, and that is deliberate. Warnings became
    # errors by default in this compiler because that is the right policy for
    # BUILDING THIS SYSTEM, and os/build-linux.sh and the kernel keep it. A
    # language test is a different thing: it states what the language does,
    # it was written before the policy, and it may not be edited (the tests
    # are the specification). In C# a warning is not an error unless somebody
    # asks, and here nobody is asking.
    # shellcheck disable=SC2086
    $CORC compile -Wno-error "${compiler_flags[@]}" $own_flags $lib_paths $extra "$f" -o "$exe" >"$work/$name.compile" 2>&1
    cc_status=$?
    if [ "$verbose" = 1 ] && [ -s "$work/$name.compile" ]; then
        sed "s/^/    [corc] /" "$work/$name.compile"
    fi
    # A PROGRAM C# REFUSES: "// expect-compile-error: <text>" passes when the
    # compiler refuses the file -- a clean failure, not a crash -- and says
    # <text>. Nothing is run.
    want_error="$(header_value "$f" expect-compile-error)"
    if [ -n "$want_error" ]; then
        if [ "$cc_status" -ne 0 ] && grep -qF -- "$want_error" "$work/$name.compile" && ! grep -q "Unhandled exception" "$work/$name.compile"; then
            echo "PASS $name"
            passed=$((passed + 1))
        else
            echo "FAIL $name (expected the compile error: $want_error)"
            sed "s/^/    /" "$work/$name.compile" | head -30
            failed=$((failed + 1))
            failed_names="$failed_names $name"
        fi
        continue
    fi
    if [ "$cc_status" -ne 0 ] || [ ! -x "$exe" ]; then
        echo "FAIL $name (compile error, status $cc_status)"
        if [ "$verbose" != 1 ]; then
            sed "s/^/    /" "$work/$name.compile" | head -30
        fi
        failed=$((failed + 1))
        failed_names="$failed_names $name"
        continue
    fi

    run_with_timeout "$exe" >"$work/$name.out" 2>"$work/$name.err"
    got_exit=$?

    ok=1
    reason=""
    if [ "$got_exit" -eq 124 ] && [ "$want_exit" != 124 ]; then
        ok=0
        reason="timed out after ${timeout_s}s"
    elif [ "$want_exit" = "nonzero" ]; then
        if [ "$got_exit" -eq 0 ]; then
            ok=0
            reason="expected a non-zero exit code, got 0"
        fi
    elif [ "$got_exit" -ne "$want_exit" ]; then
        ok=0
        reason="expected exit $want_exit, got $got_exit"
    fi

    if [ "$ok" = 1 ] && has_expected_output "$f"; then
        expected_output "$f" >"$work/$name.want"
        if ! cmp -s "$work/$name.want" "$work/$name.out"; then
            ok=0
            reason="stdout differs"
        fi
    fi

    if [ "$ok" = 1 ]; then
        echo "PASS $name"
        passed=$((passed + 1))
    else
        echo "FAIL $name ($reason)"
        if [ "$reason" = "stdout differs" ]; then
            diff -u --label expected --label actual "$work/$name.want" "$work/$name.out" | sed "s/^/    /"
        else
            if [ -s "$work/$name.out" ]; then
                echo "    stdout:"
                sed "s/^/    | /" "$work/$name.out" | head -20
            fi
        fi
        if [ -s "$work/$name.err" ]; then
            echo "    stderr:"
            sed "s/^/    | /" "$work/$name.err" | head -20
        fi
        failed=$((failed + 1))
        failed_names="$failed_names $name"
    fi
done

echo
echo "$passed passed, $failed failed, $((passed + failed)) total"
if [ "$failed" -ne 0 ]; then
    echo "failed:$failed_names"
    exit 1
fi
exit 0
