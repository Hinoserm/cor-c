# Measurement and checking scripts

For the collector's share, compile memory and speed, and for checking a
language test against real .NET. Nothing here is part of the build.

## Compiling the compiler's own units, measured (`compile-units.sh`)

```
compile-units.sh CORC TREE OUT [--flag ...] [--] UNIT ...
```

Compiles the named units of the compiler in TREE (file names without `.cs`)
in one `compile-project` process with `--gc-stats`, as the project build
compiles them, and prints the wall seconds, the peak resident KB and the
collector's figures: freed by the program, freed by the collector, peak
live. The collector's share is collector / (program + collector). Flags
given before the units go to the compiler: `--alloc-sample` for the `asamp*`
readers, `--gc-census`, `--gc-dead-census`, `--gc-parent-census`,
`--report-phases`.

With `MEMORY=256M` each unit is compiled alone under that cgroup limit
(`systemd-run --user --scope`). `@units-256-hard.txt` names the units that
were hardest to fit.

TREE is a copy of this repository taken so the work tree can move on while a
measurement runs. Its declaration index is made once:

```
corc --lib-root TREE project TREE/compiler/corc.csproj --configuration Release --link-only -o /dev/null
```

The index is then at `TREE/compiler/obj/cor-c/Release/net10.0/declarations.idx`.

## Reading the logs (`tools/`)

Each takes the log and the corc binary the log came from, which supplies
the symbols:

- `asamp.py LOG BIN [N]`: `--alloc-sample` records, ranked by the bytes the
  collector took, by site and callers.
- `asamp3.py`: the same by the first frame outside the runtime and the
  collections. `asampof.py LOG BIN PATTERN` gives what is above a frame,
  `asampbelow.py` what is below it, `asampsize.py` small blocks only, and
  `acaller.py` slow-path allocations by function and caller.
- `census.py`, `deadcensus.py`, `parents.py`: the live, dead and parent
  censuses, by type.

## Other

- `dncheck.sh TEST.cor [WORK]`: runs a language test's program, and its
  `// units:` files, under real .NET (`canonis.csproj`). It compares the
  output and exit status with the test's expectation. Under .NET,
  `GcShim.cs` stands in for the runtime's counters.
- `killsuite.py HOSTDIR`: stops a running language suite by exact pids.
  It kills every process whose `CORC` names HOSTDIR.
