#nullable enable
namespace Corsac.Lang;

/// <summary>
/// What the compiler passes for an optional parameter a call leaves out, when
/// one of C#'s caller-information attributes is on it: the calling member's
/// name, its source file's full path, the line of the call, or the source
/// text of another argument of the same call.
/// </summary>
public enum CallerInfo : byte
{
    None,
    MemberName,
    FilePath,
    LineNumber,
    ArgumentExpression,
}
