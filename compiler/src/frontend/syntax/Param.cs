#nullable enable
namespace Corsac.Lang;

public sealed class Param : Node
{
    public required string Name { get; init; }
    public required TypeRef Type { get; init; }

    /// <summary>
    /// THE FLAGS IN ONE BYTE, and what few parameters carry -- a default, a
    /// caller attribute, a marshalling or a null proof -- in a side object
    /// (ParamRare) made only when one of those is set to something other than
    /// its default. A large unit binds over a hundred thousand parameters;
    /// with a word or a byte for each of these a Param was a 64-byte block,
    /// and is 48 this way. Every property reads back exactly what was written.
    /// </summary>
    private ParamRare? _rare;
    private ParamRare Rare => _rare ??= new();
    private byte _flags;

    private const byte RefFlag = 1, OutFlag = 2, ReadOnlyRefFlag = 4, ParamsFlag = 8, ThisFlag = 16;

    private void SetFlag(byte flag, bool value) => _flags = value ? (byte)(_flags | flag) : (byte)(_flags & ~flag);

    public bool IsRef { get => (_flags & RefFlag) != 0; init => SetFlag(RefFlag, value); }
    public bool IsOut { get => (_flags & OutFlag) != 0; init => SetFlag(OutFlag, value); }

    /// <summary>
    /// `in T x`: the address is passed, as it is for a ref, and the callee may
    /// not write through it. What `in` is written for is the address -- a
    /// struct too big to copy at every call -- so it is a ref that cannot be
    /// assigned rather than a value with a rule about it.
    /// </summary>
    public bool IsReadOnlyRef { get => (_flags & ReadOnlyRefFlag) != 0; init => SetFlag(ReadOnlyRefFlag, value); }
    public bool IsParams { get => (_flags & ParamsFlag) != 0; init => SetFlag(ParamsFlag, value); }

    /// <summary>
    /// `this` on the first parameter of a static method: an EXTENSION METHOD.
    ///
    /// `list.Where(f)` means `Enumerable.Where(list, f)`, and this is what
    /// says so. The whole of LINQ is written this way in C#, and writing it
    /// any other way here would mean the compiler's own source could not be
    /// compiled by it.
    /// </summary>
    public bool IsThis { get => (_flags & ThisFlag) != 0; init => SetFlag(ThisFlag, value); }

    /// <summary>
    /// What a particular answer from this method PROVES about this argument.
    ///
    /// .NET writes it `[NotNullWhen(false)] string? value` on
    /// string.IsNullOrEmpty: a false answer means the argument was not null,
    /// so `if (!string.IsNullOrEmpty(dir)) { Use(dir); }` reads dir as a
    /// string. Null where the attribute was not written.
    /// </summary>
    public bool? NotNullWhen { get => _rare?.NotNullWhen; init { if (value is not null) Rare.NotNullWhen = value; } }

    /// <summary>
    /// The value a caller that leaves this argument out passes.
    ///
    /// Settable because the checker WRITES THE NAMES IN IT OUT IN FULL the
    /// first time a call leaves the argument out: C# evaluates a default
    /// where it was declared, and the expression is copied into call sites in
    /// other classes, other namespaces and other files, where a bare name
    /// means something else or nothing at all.
    /// </summary>
    public Expr? Default
    {
        get => _rare?.Default;
        set { if (value is not null) Rare.Default = value; else if (_rare is not null) _rare.Default = null; }
    }

    /// <summary>
    /// [CallerMemberName], [CallerFilePath], [CallerLineNumber] or
    /// [CallerArgumentExpression]: what a call that leaves this optional
    /// argument out passes instead of its default.
    /// </summary>
    public CallerInfo Caller { get => _rare?.Caller ?? CallerInfo.None; init { if (value != CallerInfo.None) Rare.Caller = value; } }

    /// <summary>For [CallerArgumentExpression]: the parameter whose argument's source text is passed.</summary>
    public string? CallerArgument { get => _rare?.CallerArgument; init { if (value is not null) Rare.CallerArgument = value; } }

    /// <summary>
    /// [MarshalAs(UnmanagedType.X)]: the X, or null where it was not
    /// written. What a COM interface's parameter is on the native side
    /// when its type alone does not say (ComDeclarations): a string as a
    /// BSTR, an LPWStr or an LPStr; a bool as a VARIANT_BOOL, a BOOL or a
    /// byte; an object as a VARIANT, an IUnknown or an IDispatch.
    /// </summary>
    public string? MarshalAs { get => _rare?.MarshalAs; init { if (value is not null) Rare.MarshalAs = value; } }
}

/// <summary>What few parameters carry (Param._rare): null until one is set.</summary>
internal sealed class ParamRare
{
    public Expr? Default;
    public string? CallerArgument;
    public string? MarshalAs;
    public bool? NotNullWhen;
    public CallerInfo Caller;
}
