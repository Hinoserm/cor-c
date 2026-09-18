#nullable enable
namespace Corsac.Lang;

/// <summary>Parser-only marker lowered to a declaration and try/finally.</summary>
public sealed class UsingDeclStmt : Stmt
{
    public required LocalDecl Declaration { get; init; }
}
