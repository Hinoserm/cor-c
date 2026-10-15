# Integration tests

Multi-artifact compiler/runtime/library workflows, including shared-library builds.

## CORSAC86 boot acceptance

`test/corsac-boot` builds the OS with this checkout's compiler and boots it:
a snapshot of the OS repository at a revision (read with `git archive`, so
the checkout itself is never touched), built through the OS's own
`corsac.build` with `CorC` set to this tree, and handed to the OS's own
`boot-test`, which boots the image in QEMU, logs in, mounts, runs nano,
iperf3 and a Windows process, and reboots.

    compiler/bin/managed/Release/net10.0/corc build test/corsac-boot \
        [CorsacRoot=../corsac86-gui] [CorsacRevision=HEAD] [CorsacWin2k=~/src/win2k]

It boots a virtual machine, so it is not a step of `test` and is run by name,
with permission each time. It needs Windows 2000 media, qemu-system-i386,
e2fsprogs, binutils, nasm, a C toolchain and, the first time, the network
(the OS's Linux tools). Everything it makes is under `build/corsac-boot/`:
one folder per OS revision -- the snapshot, the OS's work and logs, the disc
while it is booted, about 4 GB at the peak -- and the Linux tools' cache,
kept between revisions. The OS's build logs are in the snapshot's
`build/logs/`, the boot's in `pc/boot-test-*/`.

## Separate units

`separate-units.sh` compiles language tests the way the OS builds its
programs: each test as one unit against the library's declarations
(`--ref`, `--decl-index`), linked with a runtime and class library object
compiled once, apart. It runs a regression set by default, `all` for every
language test, or the tests it is given. Every language test must pass this
way as well as compiled whole.
