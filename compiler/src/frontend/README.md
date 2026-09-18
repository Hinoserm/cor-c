# Frontend

Lexing, parsing, syntax trees, binding, type checking, generic specialization,
and declaration metadata used before lowering.

`BindResult.cs` owns the binding-result model and its release/copy operations.
Resolved-name records live individually under `symbols/`; syntax nodes are
under `syntax/`. Binder partial files remain implementation subdivisions of
the same compiler component, not separate language implementations.
