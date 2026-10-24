#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

/// <summary>
/// A VTABLE SLOT'S METHOD IS COMPILED WHEN THE SLOT IS CALLED. A descriptor
/// names a method in every slot its type fills, and naming one used to make
/// it compiled, with everything it calls: one string literal kept
/// string.CompareTo through string's descriptor, and with it the allocator,
/// the collector and the exception reports -- 148 KB of code in a program
/// whose Main returned 3. In a closed world (ClosedWorld: everything that can
/// call through a vtable is in this compile) a slot's method is required
/// only once code that runs reads that slot, which every read says it does
/// (SlotCalled); a slot never read is left empty once lowering has finished
/// (FillUnreachedSlots), and its method is never compiled unless something
/// calls it by name. Slots are told apart by
/// number alone: a call through slot 5 of any type keeps slot 5 of every type
/// made, which is coarser than the declaring type allows, and safe.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>
    /// Everything that can call through a vtable is in this compile: no
    /// object for another link, no shared object or library, no kernel
    /// exporting to modules. Set by the driver; off, every slot's method is
    /// required as it is named.
    /// </summary>
    public static bool ClosedWorld { get; set; }

    private readonly HashSet<int> _slotsCalled = new();

    /// <summary>What naming a slot's method put off, by slot: each requires one method or makes one stub.</summary>
    private readonly Dictionary<int, List<Action>> _slotsWaiting = new();

    /// <summary>The labels a descriptor names in a slot nothing has read yet, and the slot.</summary>
    private readonly Dictionary<string, int> _slotLabelsWaiting = new(StringComparer.Ordinal);

    /// <summary>Code that runs reads slot <paramref name="slot"/> of some vtable: every method waiting on it is required now.</summary>
    private void SlotCalled(int slot)
    {
        if (!_slotsCalled.Add(slot) || !_slotsWaiting.Remove(slot, out List<Action>? waiting)) return;
        foreach (Action need in waiting) need();
    }

    /// <summary>
    /// A descriptor names <paramref name="label"/> in slot <paramref name="slot"/>:
    /// <paramref name="need"/>, which compiles it, runs now if the slot has
    /// been read or the world is open, and otherwise when it is read.
    /// </summary>
    private string SlotNamed(int slot, string label, Action need)
    {
        if (!ClosedWorld || _library || PartOfALibrary || SharedObject || Dynamic || _slotsCalled.Contains(slot))
        {
            need();
            return label;
        }
        (_slotsWaiting.TryGetValue(slot, out List<Action>? list) ? list : _slotsWaiting[slot] = new()).Add(need);
        _slotLabelsWaiting.TryAdd(label, slot);
        return label;
    }

    /// <summary>
    /// LOWERING DONE: each label still waiting on a slot nothing read, that
    /// nothing else compiled either, leaves its slot empty in every
    /// descriptor, as an abstract method's is: the slot is never read, and
    /// were it read, the call would fault at once rather than run something.
    /// </summary>
    private void FillUnreachedSlots()
    {
        if (_slotLabelsWaiting.Count == 0) return;
        HashSet<string> defined = new(_m.Functions.Select(f => f.Name), StringComparer.Ordinal);
        foreach (DataItem item in _m.Data)
            item.Relocs.RemoveAll(r =>
            {
                if (r.Addend != 0 || !_slotLabelsWaiting.TryGetValue(r.Symbol, out int slot)
                    || _slotsCalled.Contains(slot) || defined.Contains(r.Symbol)) return false;
                // Said, so that a call through it is known to call nothing (Module.EmptiedSlots).
                _m.EmptiedSlots.Add((item.Name, r.Offset));
                return true;
            });
    }

    /// <summary>
    /// A KNOB THE COMPILE WAS NOT GIVEN IS OFF, and is said so here, as a
    /// constant: Runtime.KnobValue, KnobFlag or KnobLetter asked by a literal
    /// name that no --runtime-knob named. The image's knobs are its own
    /// literal and nothing else (Runtime.UseKnobs, called by the entry and
    /// only with them), so in a closed world the answer is known now -- and
    /// what only that knob turns on (the collector's checked modes, its
    /// census, its sampling) is not compiled into an image that cannot ask
    /// for it. Null for any other call.
    /// </summary>
    private VReg? KnobNotGiven(CallExpr call)
    {
        if (!ClosedWorld || _library || PartOfALibrary || SharedObject || Dynamic) return null;
        if (call.Args.Count != 1 || call.Args[0] is not LiteralExpr { Kind: Lit.Str } name) return null;
        if (!_b.Calls.TryGetValue(call, out MethodSymbol? m) || !m.Static || m.Owner.Name != RuntimeType
            || m.Name is not ("KnobValue" or "KnobFlag" or "KnobLetter")) return null;
        if (RuntimeKnobs.Any(k => k.StartsWith(name.Text + "=", StringComparison.Ordinal))) return null;
        return _e.Const(0, IrTypes.Of(m.Returns));
    }
}
