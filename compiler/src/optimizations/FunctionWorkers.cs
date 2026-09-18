#nullable enable
using System.Threading.Tasks;
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>
/// Bounded lanes over disjoint function graphs. Callers freeze the module's
/// function list and all shared analysis data until the completion barrier.
/// Failures are reported in module order, independently of worker scheduling.
/// </summary>
internal static class FunctionWorkers
{
    public static void Run(Module module, int workers, Action<Function, int> work)
    {
        if (workers < 1 || workers > 64) throw new ArgumentOutOfRangeException(nameof(workers));
        int active = Math.Min(workers, module.Functions.Count);
        Exception?[] failures = new Exception?[module.Functions.Count];
        Task[] tasks = new Task[active];
        for (int worker = 0; worker < active; worker++)
        {
            int lane = worker;
            tasks[worker] = Task.Run(() =>
            {
                for (int i = lane; i < module.Functions.Count; i += active)
                {
                    try { work(module.Functions[i], i); }
                    catch (Exception error) { failures[i] = error; }
                }
            });
        }
        Task.WhenAll(tasks).Wait();
        for (int i = 0; i < failures.Length; i++)
            if (failures[i] is Exception error) throw error;
    }
}
