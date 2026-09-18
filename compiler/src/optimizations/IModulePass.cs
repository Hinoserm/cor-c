#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>
/// A transformation that needs the whole module: inlining, and later
/// whole-program analyses such as devirtualisation. Run before the
/// per-function rounds, because they clean up what a module pass leaves.
/// </summary>
public interface IModulePass
{
    string Name { get; }
    void Run(Module module);
}
