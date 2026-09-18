#nullable enable

namespace Corsac.Lang.Ir;

/// <summary>
/// A code generator for one target. Given IR, produce an object file. It
/// owns instruction selection, register allocation, the calling convention
/// and the encoding, and nothing above it knows any of those exist.
/// </summary>
public interface IBackend
{
    Target Target { get; }

    /// <summary>
    /// Generate. Diagnostics -- an instruction the target refuses, a
    /// function too large -- go to <paramref name="errors"/> as messages
    /// naming the function.
    /// </summary>
    ObjectFile Generate(Module module, List<string> errors);

    /// <summary>
    /// The same, as readable assembly, for `corc compile --asm` and for
    /// anyone debugging the backend. Not fed to any assembler.
    /// </summary>
    string Assembly(Module module);
}
