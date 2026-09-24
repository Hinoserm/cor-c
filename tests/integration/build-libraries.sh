#!/usr/bin/env bash
#
# Builds the class library as shared objects into build/lib.
#
#   os/build-libs.sh           build every library that is out of date
#   os/build-libs.sh -f        build them all again
#   os/build-libs.sh -v        say what each one needs and what it cost
#
# ONE SHARED OBJECT PER .NET ASSEMBLY. A type goes to the file named after
# the assembly it lives in in .NET -- System.Collections, System.IO,
# System.Net -- so that a program loads what it uses and nothing else. The
# exception is the bottom of the stack, and it is an honest one: the runtime
# is written in COR-C# and the core of the class library is written against
# the runtime, so `stdlib/src/System/Core.cor` and `lib/rt/*.cor` refer to each other and
# cannot be two files. That one is libcorsacrt.so.
#
# THE SOURCE LIST IS PART OF THE ABI. Interface method slots are numbered
# program-wide, in declaration order, so every image that meets another must
# have been compiled from the same sources in the same order -- which is why
# each library is given the whole list and marks everything that is not its
# own `--ref`. The list here and Driver.DefaultLibraries must agree, and
# tests/language/run.sh's CORC_LIBS with them.
#
# Environment:
#   CORC   the compiler (default: compiler/bin/managed/Release/net10.0/corc)
#   OUT    where the .so files go (default: build/lib)

set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${OUT:-$root/build/lib}"
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"
jobs="${CORSAC_BUILD_JOBS:-1}"
force=0
verbose=0

for arg in "$@"; do
    case "$arg" in
        -f|--force) force=1 ;;
        -v|--verbose) verbose=1 ;;
        -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    esac
done

if [ ! -x "$corc" ]; then
    echo "cannot find the compiler at $corc (set CORC)" >&2
    exit 2
fi

# ---- the sources, in the order every compilation must see them --------------

SOURCES="stdlib/src/System/Core.cor stdlib/src/System/Runtime/ExceptionServices/ExceptionDispatchInfo.cor runtime/src/core/runtime.cor runtime/src/core/gc.cor runtime/src/core/threading.cor \
runtime/src/platforms/linux/threading.cor runtime/src/platforms/linux/system.cor runtime/src/platforms/linux/abi.cor runtime/src/platforms/linux/files.cor stdlib/src/System/interop.cor stdlib/src/System/IO/io.cor \
stdlib/src/System/Collections/Collections.cor stdlib/src/System/IO/io-streams.cor stdlib/src/System/IO/compression.cor stdlib/src/System/IO/tar.cor \
stdlib/src/System/time.cor stdlib/src/System/values.cor stdlib/src/System/numerics.cor stdlib/src/System/Text/RegularExpressions.cor stdlib/src/System/console.cor \
stdlib/src/System/environment.cor stdlib/src/System/Net/Net.cor stdlib/src/System/Security/Cryptography/Cryptography.cor stdlib/src/System/signals.cor stdlib/src/System/unix.cor \
stdlib/src/System/process.cor stdlib/src/System/power.cor"

# ---- what goes where, bottom of the stack first -----------------------------
#
# Each line is: <soname> <sources it owns>. The order is the build order and
# the dependency order: a library may only need the ones above it in this
# list, which is checked after every build.

LIBRARIES="
libcorsacrt.so|stdlib/src/System/Core.cor stdlib/src/System/Runtime/ExceptionServices/ExceptionDispatchInfo.cor runtime/src/core/runtime.cor runtime/src/core/gc.cor runtime/src/core/threading.cor runtime/src/platforms/linux/threading.cor runtime/src/platforms/linux/system.cor runtime/src/platforms/linux/abi.cor runtime/src/platforms/linux/files.cor stdlib/src/System/signals.cor
libSystem.Runtime.InteropServices.so|stdlib/src/System/interop.cor
libSystem.Security.Cryptography.so|stdlib/src/System/Security/Cryptography/Cryptography.cor
libSystem.Runtime.Extensions.so|stdlib/src/System/time.cor stdlib/src/System/values.cor stdlib/src/System/environment.cor
libSystem.Runtime.Numerics.so|stdlib/src/System/numerics.cor
libSystem.Collections.so|stdlib/src/System/Collections/Collections.cor
libSystem.Text.RegularExpressions.so|stdlib/src/System/Text/RegularExpressions.cor
libSystem.IO.so|stdlib/src/System/IO/io.cor stdlib/src/System/IO/io-streams.cor
libSystem.IO.Compression.so|stdlib/src/System/IO/compression.cor
libSystem.Formats.Tar.so|stdlib/src/System/IO/tar.cor
libSystem.Console.so|stdlib/src/System/console.cor
libSystem.Net.so|stdlib/src/System/Net/Net.cor
libMono.Posix.so|stdlib/src/System/unix.cor stdlib/src/System/power.cor
libSystem.Diagnostics.Process.so|stdlib/src/System/process.cor
"

mkdir -p "$out"

# Is anything this library is made of newer than the library?
stale() {
    local target="$1"
    [ "$force" = 1 ] && return 0
    [ -f "$target" ] || return 0
    [ "$corc" -nt "$target" ] && return 0
    [ "$here/build-libraries.sh" -nt "$target" ] && return 0
    # A .NET apphost can remain unchanged while its compiler/linker DLLs are
    # rebuilt. Treat those payloads as toolchain inputs, not just the launcher.
    local payload
    for payload in "$(dirname "$corc")/corc.dll" "$(dirname "$corc")/corlink.dll"; do
        [ -f "$payload" ] && [ "$payload" -nt "$target" ] && return 0
    done
    local src
    for src in $SOURCES; do
        [ "$root/$src" -nt "$target" ] && return 0
    done
    local dependency
    for dependency in $linked; do
        [ "$out/$dependency" -nt "$target" ] && return 0
    done
    return 1
}

built=0
failed=0
failed_names=""
linked=""

while IFS='|' read -r soname owned; do
    [ -n "$soname" ] || continue
    target="$out/$soname"

    args=()
    for src in $SOURCES; do
        if [[ " $owned " == *" $src "* ]]; then
            args+=("$root/$src")
        else
            args+=(--ref "$root/$src")
        fi
    done
    for lib in $linked; do
        args+=(--link-shared "$out/$lib")
    done

    if stale "$target"; then
        if "$corc" compile --jobs "$jobs" --nostdlib --shared "${args[@]}" -o "$target" >"$out/$soname.log" 2>&1; then
            rm -f "$out/$soname.log"
            built=$((built + 1))
        else
            failed=$((failed + 1))
            failed_names="$failed_names $soname"
            printf 'FAILED %s (see %s)\n' "$soname" "$out/$soname.log"
            [ "$verbose" = 1 ] && sed 's/^/    /' "$out/$soname.log" | head -20
            linked="$linked $soname"
            continue
        fi
    fi

    linked="$linked $soname"

    if [ "$verbose" = 1 ] && [ -f "$target" ]; then
        needs="$(readelf -dW "$target" 2>/dev/null | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' | tr '\n' ' ')"
        printf '%-42s %8d  needs:%s\n' "$soname" "$(stat -c %s "$target")" " ${needs:-nothing}"
    fi
done <<< "$LIBRARIES"

# ---- nothing may point back down the stack ---------------------------------
#
# A shared object may leave a symbol undefined only if a library BELOW it
# defines it. One that does not is a dependency pointing the wrong way, and
# the loader would have to close a loop to satisfy it.

if [ "$failed" -eq 0 ]; then
    below=""
    while IFS='|' read -r soname owned; do
        [ -n "$soname" ] || continue
        [ -f "$out/$soname" ] || continue
        missing="$(
            readelf -sW --dyn-syms "$out/$soname" 2>/dev/null |
                awk '$7 == "UND" && $8 != "" { print $8 }' | sort -u > /tmp/.corsac-und.$$
            : > /tmp/.corsac-def.$$
            for lower in $below; do
                readelf -sW --dyn-syms "$out/$lower" 2>/dev/null |
                    awk '$7 != "UND" && $8 != "" { print $8 }' >> /tmp/.corsac-def.$$
            done
            sort -u /tmp/.corsac-def.$$ -o /tmp/.corsac-def.$$
            comm -23 /tmp/.corsac-und.$$ /tmp/.corsac-def.$$
            rm -f /tmp/.corsac-und.$$ /tmp/.corsac-def.$$
        )"
        if [ -n "$missing" ]; then
            echo "CYCLE $soname needs names no library below it defines:"
            echo "$missing" | head -10 | sed 's/^/    /'
            failed=$((failed + 1))
            failed_names="$failed_names $soname"
        fi
        below="$below $soname"
    done <<< "$LIBRARIES"
fi

total=0
for f in "$out"/*.so; do
    [ -e "$f" ] || continue
    total=$((total + $(stat -c %s "$f")))
done
echo "$built built, $(ls -1 "$out"/*.so 2>/dev/null | wc -l) libraries, $total bytes in $out"
if [ "$failed" -ne 0 ]; then
    echo "FAILED:$failed_names"
    exit 1
fi
exit 0
