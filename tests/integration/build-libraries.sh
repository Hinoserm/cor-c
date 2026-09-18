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
# the runtime, so `lib/std.cor` and `lib/rt/*.cor` refer to each other and
# cannot be two files. That one is libcorsacrt.so.
#
# THE SOURCE LIST IS PART OF THE ABI. Interface method slots are numbered
# program-wide, in declaration order, so every image that meets another must
# have been compiled from the same sources in the same order -- which is why
# each library is given the whole list and marks everything that is not its
# own `--ref`. The list here and Driver.DefaultLibraries must agree, and
# tests/lang/run.sh's CORC_LIBS with them.
#
# Environment:
#   CORC   the compiler (default: compiler/bin/Release/net10.0/corc)
#   OUT    where the .so files go (default: build/lib)

set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
out="${OUT:-$root/build/lib}"
. "$root/tools/fresh-corc.sh"
corc="${CORC:-$root/compiler/bin/Release/net10.0/corc}"
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

SOURCES="lib/std.cor lib/rt/runtime.cor lib/rt/gc.cor lib/threading.cor \
lib/threading-linux.cor lib/sys/linux.cor lib/interop.cor lib/io.cor \
lib/collections.cor lib/io-streams.cor lib/compression.cor lib/tar.cor \
lib/time.cor lib/values.cor lib/numerics.cor lib/regex.cor lib/console.cor \
lib/environment.cor lib/net.cor lib/security.cor lib/signals.cor lib/unix.cor \
lib/process.cor lib/power.cor"

# ---- what goes where, bottom of the stack first -----------------------------
#
# Each line is: <soname> <sources it owns>. The order is the build order and
# the dependency order: a library may only need the ones above it in this
# list, which is checked after every build.

LIBRARIES="
libcorsacrt.so|lib/std.cor lib/rt/runtime.cor lib/rt/gc.cor lib/threading.cor lib/threading-linux.cor lib/sys/linux.cor lib/signals.cor
libSystem.Runtime.InteropServices.so|lib/interop.cor
libSystem.Security.Cryptography.so|lib/security.cor
libSystem.Runtime.Extensions.so|lib/time.cor lib/values.cor lib/environment.cor
libSystem.Runtime.Numerics.so|lib/numerics.cor
libSystem.Collections.so|lib/collections.cor
libSystem.Text.RegularExpressions.so|lib/regex.cor
libSystem.IO.so|lib/io.cor lib/io-streams.cor
libSystem.IO.Compression.so|lib/compression.cor
libSystem.Formats.Tar.so|lib/tar.cor
libSystem.Console.so|lib/console.cor
libSystem.Net.so|lib/net.cor
libMono.Posix.so|lib/unix.cor lib/power.cor
libSystem.Diagnostics.Process.so|lib/process.cor
"

mkdir -p "$out"

# Is anything this library is made of newer than the library?
stale() {
    local target="$1"
    [ "$force" = 1 ] && return 0
    [ -f "$target" ] || return 0
    [ "$target" -nt "$corc" ] || return 0
    local src
    for src in $SOURCES; do
        [ "$root/$src" -nt "$target" ] && return 0
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
        if "$corc" compile --nostdlib --shared "${args[@]}" -o "$target" >"$out/$soname.log" 2>&1; then
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
