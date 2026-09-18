#nullable enable

namespace Corsac.Lang.Opt;

/// <summary>A whole-module pass that may analyze independent functions in parallel.</summary>
public interface IParallelModulePass : IModulePass
{
    int Workers { get; set; }
}
