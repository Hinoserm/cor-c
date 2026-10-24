#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>
/// What a pass works out for a function -- its liveness, its definitions --
/// kept for the last few functions asked about, and worked out again for one
/// asked about after it has gone. A pass that walks the module function by
/// function asks about one at a time, and a cache of every function's answer
/// held the whole module's analyses at once: for the class library's unit,
/// 21,000 functions' liveness, past the compiler's heap limit.
/// The answers are worked out the same way each time, so dropping one
/// changes no result, only time.
/// </summary>
public sealed class RecentByFunction<T> where T : class
{
    private readonly Function?[] _keys;
    private readonly T?[] _values;
    private int _next;

    public RecentByFunction(int size = 32)
    {
        _keys = new Function?[size];
        _values = new T?[size];
    }

    /// <summary>The answer for `f`, worked out by `make` if it is not among the last ones kept.</summary>
    public T Get(Function f, Func<Function, T> make)
    {
        for (int k = 0; k < _keys.Length; k++)
            if (ReferenceEquals(_keys[k], f)) return _values[k]!;
        T made = make(f);
        _keys[_next] = f;
        _values[_next] = made;
        _next = (_next + 1) % _keys.Length;
        return made;
    }
}
