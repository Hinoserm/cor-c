# Project compilation

Standard .csproj evaluation, project graphs, per-file compilation scheduling,
incremental dependency records and final linker orchestration. Project files
remain the only source inventory; generated indexes and state are artifacts.
Evaluation belongs to this component, not MSBuild. MSBuild is allowed only to
bootstrap the build utility; normal project evaluation and compilation must
not invoke it or silently fall back to it.
