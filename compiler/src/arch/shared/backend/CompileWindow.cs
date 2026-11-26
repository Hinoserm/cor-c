#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Backends;

/// <summary>
/// A MODULE'S FUNCTIONS COMPILED A WINDOW AT A TIME, for any backend: its
/// workers select and allocate disjoint functions of the window while the
/// backend takes the results in the module's order and encodes them one
/// after another -- so the object is the same whatever the worker count,
/// and no more than a window of machine code is held at once. Each backend
/// had this loop of its own, word for word.
///
/// THE WINDOW is four functions a worker where the module holds its
/// bodies. Where they are loaded as they are compiled (`loader`: a unit
/// made again at the link) it is as many as the memory budget covers by
/// their costs, a worker each at most; a function bigger than the whole
/// budget is compiled alone, as slowly as it must be, never refused -- the
/// batch sizes the work, not the code. One worker and no loader is no
/// window at all: each function compiled as it is asked for.
///
/// A function's diagnostics are its own list on its worker, handed over
/// with it in order.
/// </summary>
internal sealed class CompileWindow<T> where T : class
{
    private readonly IReadOnlyList<Function> _functions;
    private readonly int _workers, _window;
    private readonly Func<int, Function>? _loader;
    private readonly Func<int, long>? _cost;
    private readonly long _budget;
    private readonly Func<Function, List<string>, T?> _compile;
    private readonly T?[] _compiled;
    private readonly List<string>?[] _diagnostics;
    private int _batchStart, _batchEnd;

    /// <summary>The most a batch was given, in the loader's costs and in functions.</summary>
    public long PeakBatchBytes { get; private set; }
    public int PeakBatchFunctions { get; private set; }

    public CompileWindow(IReadOnlyList<Function> functions, int workers, Func<int, Function>? loader, Func<int, long>? cost, long budget,
        Func<Function, List<string>, T?> compile)
    {
        if (workers < 1 || workers > 64) throw new ArgumentOutOfRangeException(nameof(workers));
        _functions = functions;
        _workers = workers;
        _window = loader is null ? workers * 4 : workers;
        _loader = loader;
        _cost = cost;
        _budget = budget;
        _compile = compile;
        _compiled = new T?[_window];
        _diagnostics = new List<string>?[_window];
    }

    /// <summary>
    /// The function at `index`, compiled, its diagnostics added to
    /// `errors`; null where it could not be. Asked for every index once,
    /// in order.
    /// </summary>
    public T? Take(int index, List<string> errors)
    {
        if (_workers == 1 && _loader is null) return _compile(_functions[index], errors);
        if (index == _batchEnd)
        {
            int first = index;
            int count = Math.Min(_window, _functions.Count - first);
            long bytes = 0;
            if (_loader is not null)
            {
                count = 0;
                while (count < _window && first + count < _functions.Count)
                {
                    long cost = _cost?.Invoke(first + count)
                        ?? throw new InvalidOperationException("Deferred functions require memory costs");
                    if (cost <= 0)
                        throw new InvalidDataException("Function has no backend working cost: " + _functions[first + count].Name);
                    if (count > 0 && cost > _budget - bytes) break;
                    bytes += cost; count++;
                    // Bigger than the whole budget: compiled alone.
                    if (cost > _budget) break;
                }
            }
            _batchStart = first; _batchEnd = first + count;
            PeakBatchBytes = Math.Max(PeakBatchBytes, bytes);
            PeakBatchFunctions = Math.Max(PeakBatchFunctions, count);
            int active = Math.Min(_workers, count);
            Task[] tasks = new Task[active];
            for (int worker = 0; worker < active; worker++)
            {
                int lane = worker;
                tasks[worker] = Corsac.UnitScope.Run(() =>
                {
                    for (int item = lane; item < count; item += active)
                    {
                        List<string> localErrors = new();
                        Function body = _loader?.Invoke(first + item) ?? _functions[first + item];
                        _compiled[item] = _compile(body, localErrors);
                        _diagnostics[item] = localErrors;
                    }
                });
            }
            Task.WhenAll(tasks).Wait();
        }
        int slot = index - _batchStart;
        errors.AddRange(_diagnostics[slot]!);
        T? made = _compiled[slot];
        _compiled[slot] = null;
        _diagnostics[slot] = null;
        return made;
    }
}
