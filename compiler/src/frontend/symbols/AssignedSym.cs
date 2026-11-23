#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A local a condition assigned where it held (Binder.Assume): a pattern's
/// `x is Foo f` binding, definitely assigned in the branch the test leads to
/// and no further. Forget takes it back with the condition's other proofs.
/// </summary>
public sealed record AssignedSym(LocalSym Local) : Sym;
