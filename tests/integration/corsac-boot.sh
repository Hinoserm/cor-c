#!/usr/bin/env bash
#
# The OS built by this checkout's compiler, and booted.
#
#     tests/integration/corsac-boot.sh [--os DIR] [--revision REV] [--win2k DIR] [--jobs N]
#
# A snapshot of the OS repository at REV (default HEAD of DIR, default the
# sibling corsac86-gui) -- read with git archive, so the checkout is never
# touched -- built through the OS's own corsac.build with this checkout as
# its compiler, and handed to the OS's own boot-test: QEMU boots the image,
# logs in, mounts, runs nano, iperf3 and a Windows process, and reboots.
# One way of building and booting the OS, the OS's; this only says which
# compiler and which revision.
#
# IT BOOTS A VIRTUAL MACHINE, and is no step of `corc build test`: it is run
# by name (test/corsac-boot), with permission each time. It needs Windows 2000
# media (--win2k, default ~/src/win2k), qemu-system-i386, e2fsprogs, binutils,
# nasm, a C toolchain and network access the first time (the OS's Linux tools).
# Everything it makes is under build/corsac-boot/ in this tree: one folder per
# OS revision (about 4 GB at its peak), and the Linux tools' cache shared.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
os="$root/../corsac86-gui"
revision=HEAD
win2k="$HOME/src/win2k"
jobs=8
while [ $# -gt 0 ]; do
    case "$1" in
        --os) os=$2; shift 2 ;;
        --revision) revision=$2; shift 2 ;;
        --win2k) win2k=$2; shift 2 ;;
        --jobs) jobs=$2; shift 2 ;;
        *) echo "corsac-boot.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done
corc="${CORC:-$root/compiler/bin/managed/Release/net10.0/corc}"

fail() { echo "corsac-boot: $1" >&2; exit 1; }
git -C "$os" rev-parse --git-dir > /dev/null 2>&1 || fail "no OS repository at $os (--os DIR)"
commit="$(git -C "$os" rev-parse --verify "$revision^{commit}")" || fail "no revision $revision in $os"
[ -f "$win2k/i386/txtsetup.sif" ] && [ -d "$win2k/installed" ] || fail "no Windows 2000 media at $win2k (--win2k DIR)"
for tool in qemu-system-i386 mke2fs e2fsck debugfs objcopy nasm curl make cc tar sha256sum; do
    command -v "$tool" > /dev/null || fail "$tool is needed and not found"
done
[ -d /usr/share/zoneinfo ] || fail "/usr/share/zoneinfo is needed and not found"
[ -x "$corc" ] || fail "no compiler at $corc"

base="$root/build/corsac-boot"
dir="$base/os-${commit:0:12}"
mkdir -p "$dir/tmp"
# Only this revision's folder is kept: the one the last run named, if it was
# another, goes.
previous="$(cat "$base/revision" 2>/dev/null || true)"
if [ -n "$previous" ] && [ "$previous" != "${commit:0:12}" ]; then rm -rf "$base/os-$previous"; fi
echo "${commit:0:12}" > "$base/revision"
# Its compilers and tools write their temporary files here, not in /tmp.
export TMPDIR="$dir/tmp"
trap 'echo "corsac-boot: failed; the build logs are in $dir/source/build/logs, the boot logs in $dir/pc/boot-test-*" >&2' ERR

# The snapshot, once a revision: its documents and art the build never reads
# are left out (a gigabyte of PDFs).
if [ ! -f "$dir/source/corsac.build" ]; then
    mkdir -p "$dir/source"
    git -C "$os" archive "$commit" -- . ':(exclude)docs' ':(exclude)art/net' ':(exclude)art/vs97' | tar -x -C "$dir/source"
fi

{
    printf 'OS revision: %s\n' "$commit"
    printf 'cor-c revision: %s%s\n' "$(git -C "$root" rev-parse HEAD)" "$(git -C "$root" diff --quiet || echo ' (modified)')"
    printf 'profile: x86-486-isa\nWindows media: %s\n' "$win2k"
    sha256sum "$corc" "$(dirname "$corc")"/*.dll
} > "$dir/provenance.txt"

"$corc" build --file "$dir/source/corsac.build" --jobs "$jobs" boot-test \
    CorC="$root" Compiler="$corc" profile=x86-486-isa \
    work="$dir/pc" disk="$dir/disc.img" size=1G fresh=true \
    linux_cache="$base/linuxbin" win2k="$win2k"

rm -f "$dir/disc.img"
printf 'PASS the OS at %s, built by this compiler, booted: %s\n' "${commit:0:12}" "$dir"
