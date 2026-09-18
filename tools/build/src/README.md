# Build sources

One type per file. Manifest loading and validation precede graph execution.
ProcessRunner owns subprocess lifetimes and bounded output logging. Project
providers must preserve standard .csproj semantics.
