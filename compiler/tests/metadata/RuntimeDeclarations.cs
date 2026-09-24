using Corsac;
using Corsac.Lang;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The library sources the fixtures compile against: the compiler's own
/// default set, asked of the compiler. There used to be a list of them
/// checked in beside the tests, which had to be identical to the default set
/// and twice was not -- interface slots are numbered over the declarations,
/// so a unit built from a short list numbers every later slot differently
/// and the link stops. A copy that must match is a copy that will drift.
/// </summary>
internal static class RuntimeDeclarations
{
    public static string[] Sources() => Driver.DefaultLibraries(Target.X86).ToArray();
}
