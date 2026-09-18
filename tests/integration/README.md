# Integration tests

Multi-artifact compiler/runtime/library workflows, including shared-library builds.

## CORSAC86 boot acceptance

The boot fixture uses a separate CORSAC86 checkout, defaulting to the sibling
`corsac86-integration` directory. Create it with
`git clone https://github.com/Hinoserm/corsac86.git ../corsac86-integration`, or
set `CORSAC_ROOT` to another dedicated integration checkout. Do not use an active
kernel developer's workspace.

Run `build --toolchain dotnet test/corsac-boot` from this repository. Each run
snapshots the checkout's committed revision into a private directory under
`build/`, records its revision, and builds the image there. It does not update
the checkout or use its uncommitted files. Update the dedicated checkout
explicitly when a newer OS revision is wanted.
