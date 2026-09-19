using Corsac.Lang;

namespace Corsac.Tests.Metadata;

/// <summary>
/// The separately compiled fixtures name the library sources by hand, in
/// tests/integration/managed-runtime.sources, and hand them to a `--nostdlib`
/// compilation as `--ref`. The other side of every such link compiled the
/// compiler's DEFAULT library set. Interface slots are numbered over the
/// interfaces those sources declare, so the moment the two lists differ the
/// two sides number every slot after the first difference apart, and the link
/// stops with a layout conflict. That is what happened when Forms was added to
/// the default set and not to the list: eight families, twelve slots.
/// </summary>
public static class RuntimeSourceListTests
{
    public static void Run(string work)
    {
        string[] listed = RuntimeDeclarations.Sources().Select(Path.GetFullPath).ToArray();
        string[] defaults = Driver.DefaultLibraries(Target.X86).Select(Path.GetFullPath).ToArray();
        if (!listed.SequenceEqual(defaults))
        {
            throw new Exception("tests/integration/managed-runtime.sources no longer matches the default library set:\n  listed: "
                + string.Join("\n          ", listed) + "\n  default: " + string.Join("\n          ", defaults));
        }
    }
}
