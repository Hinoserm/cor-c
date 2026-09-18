#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// WHEN A TYPE'S STATIC INITIALISERS RUN.
///
/// C#'s answer is "the first time anything touches the type, once", and it
/// used to be "in source order, from the entry stub, before Main". That was a
/// defensible simplification until this machine grew a kernel: the entry stub
/// runs before there is a heap, so a `static byte[] table = new byte[24];` in
/// any file the kernel linked brought the kernel down at entry -- twice in one
/// day, once for a filesystem's table and once for a scratch buffer in the
/// collector itself.
///
/// So the generated StaticInit$ uses an owner/completed/failed state word
/// (see Binder.StaticInitialisers), and the three things that TOUCH a
/// type in C#'s sense reach it first:
///
///   * creating an instance,
///   * calling one of its static methods,
///   * reading or writing one of its static fields.
///
/// The test is INLINE and the call is behind it -- a load, a branch, and in
/// an acquire fence, and no call when the state is completed. A call every time would be two more
/// instructions on a machine where every instruction is interpreted, and the
/// common case is that the type was initialised long ago.
///
/// FROM INSIDE THE TYPE ITSELF NOTHING IS EMITTED. Reaching a static method or
/// a static field of T from a method of T means something already entered T,
/// and that entry did the test. This is what keeps the guard off the inside of
/// every library class's own loops -- testing everywhere instead cost grep
/// eighteen per cent of its code, which on this machine is eighteen per cent
/// of the instructions it interprets.
///
/// That shortcut is only worth anything if EVERY way in is covered, so two
/// more go with the three above:
///
///   * MAIN, which nothing else calls -- the entry stub touches its type
///     before calling it, as C# does;
///   * A DELEGATE over a static method, where the address is taken here and
///     the call that follows has no name to hang a trigger on;
///
/// and a struct is asked at every instance call, because a struct can come
/// into being without a `new` -- `S s = default(S); s.Read();` -- and a class
/// cannot.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>
    /// Run <paramref name="owner"/>'s static initialisers if they have not
    /// run, before whatever is about to touch the type.
    /// </summary>
    private void TouchType(TypeSymbol? owner)
    {
        if (owner is null)
        {
            return;
        }

        // Being done right now: StaticInit$ itself, and anything it calls, is
        // inside the type by definition.
        //
        // EVERY TOUCH IS TESTED, not just the first one a function makes. The
        // first attempt remembered which types a function had already
        // triggered and skipped the rest, which is only sound in a straight
        // line: `if (rare) { T.A = 1; } T.B = 2;` put the only test inside the
        // branch, and a run that did not take it read T's statics unwritten.
        // os/bin/uniq printed nothing at all. A load and a branch is what this
        // costs, and the optimiser folds the repeats within a block.
        if (ReferenceEquals(owner, _method?.Owner))
        {
            return;
        }

        MethodSymbol? start = owner.FindMethods("StaticInit$")
            .FirstOrDefault(m => m.Static && m.Params.Count == 0);
        FieldSymbol? ready = owner.Fields
            .FirstOrDefault(f => f.Static && f.Name == BindResult.ReadyField);

        if (start is null || ready is null)
        {
            return;
        }

        Require(start);
        _statics.Add(ready);

        Block run = _f.NewBlock("ctorun");
        Block on = _f.NewBlock("ctdone");
        if (ready.Type.Prim != Prim.I32)
            throw new InvalidOperationException("obsolete static initializer state; rebuild the library/header");
        VReg state = _e.Load(IrType.I32, new SymOperand(StaticSymbol(ready)), 0, 4, false);
        _e.Emit(Opcode.Fence, null);
        VReg done = _e.Binary(Opcode.Eq, state, 1);
        _e.Branch(done, on, run);
        _e.SetBlock(run);
        _e.Call(CallLabel(start), IrType.Void);
        _e.Jump(on);
        _e.SetBlock(on);
    }
}
