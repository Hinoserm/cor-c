#nullable enable
namespace Corsac.Lang;

public sealed class Param : Node
{
    public required string Name { get; init; }
    public required TypeRef Type { get; init; }
    public bool IsRef { get; init; }
    public bool IsOut { get; init; }

    /// <summary>
    /// `in T x`: the address is passed, as it is for a ref, and the callee may
    /// not write through it. What `in` is written for is the address -- a
    /// struct too big to copy at every call -- so it is a ref that cannot be
    /// assigned rather than a value with a rule about it.
    /// </summary>
    public bool IsReadOnlyRef { get; init; }
    public bool IsParams { get; init; }

    /// <summary>
    /// `this` on the first parameter of a static method: an EXTENSION METHOD.
    ///
    /// `list.Where(f)` means `Enumerable.Where(list, f)`, and this is what
    /// says so. The whole of LINQ is written this way in C#, and writing it
    /// any other way here would mean the compiler's own source could not be
    /// compiled by it.
    /// </summary>
    public bool IsThis { get; init; }

    /// <summary>
    /// What a particular answer from this method PROVES about this argument.
    ///
    /// .NET writes it `[NotNullWhen(false)] string? value` on
    /// string.IsNullOrEmpty: a false answer means the argument was not null,
    /// so `if (!string.IsNullOrEmpty(dir)) { Use(dir); }` reads dir as a
    /// string. Null where the attribute was not written.
    /// </summary>
    public bool? NotNullWhen { get; init; }

    /// <summary>
    /// The value a caller that leaves this argument out passes.
    ///
    /// Settable because the checker WRITES THE NAMES IN IT OUT IN FULL the
    /// first time a call leaves the argument out: C# evaluates a default
    /// where it was declared, and the expression is copied into call sites in
    /// other classes, other namespaces and other files, where a bare name
    /// means something else or nothing at all.
    /// </summary>
    public Expr? Default { get; set; }
}
