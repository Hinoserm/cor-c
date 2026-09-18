#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>
/// One transformation of one function. Passes are target-independent and
/// stateless between functions; anything they need (a CFG, liveness, the
/// definition table) they build on entry, because the previous pass may
/// have changed everything.
/// </summary>
public interface IPass
{
    string Name { get; }
    void Run(Function function);
}
