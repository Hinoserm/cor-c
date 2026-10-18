# Partial types and runtime names across project units

A .csproj whose units are compiled one by one: `Grow` is partial across two
files that are both non-entry units, each compiled against the index of the
other, and the program declares its own `Clock` and `Os`, which the runtime
has too. Each unit must see the whole of `Grow`, and the program's types must
be the program's while the runtime's code keeps its own.
