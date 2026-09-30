#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// The stack frame of one function, as offsets from EBP.
///
/// Layout, from high addresses down:
///
///   [EBP+8..]   incoming arguments, 4-byte slots
///   [EBP+4]     return address
///   [EBP+0]     caller's EBP
///   [EBP-N..-1] IR frame slots, floating-point homes, spill slots
///   below that  the callee-saved registers this function uses
///
/// Locals sit directly under EBP and the saved registers under them, rather
/// than the other way round, so that every local's offset is known the
/// moment it is created: the selector and the allocator hand out offsets as
/// they go, and only the prologue's `sub esp` waits for the total. Which
/// registers get saved is not known until allocation is done, and putting
/// them below the locals keeps that decision out of every address.
/// </summary>
public sealed class Frame
{
    private int _size;
    private readonly Dictionary<FrameSlot, int> _slots = new();
    private readonly Dictionary<int, int> _floatHomes = new();
    private int _scratch;
    private readonly List<(int Offset, int Bytes, int Align)> _allocations = new();

    /// <summary>Every block of the frame handed out, as (offset, bytes, alignment).</summary>
    public IReadOnlyList<(int Offset, int Bytes, int Align)> Allocations => _allocations;

    /// <summary>Bytes of locals below EBP, rounded to a word.</summary>
    public int Size => (_size + 3) & ~3;

    /// <summary>Reserve bytes with the given alignment; returns the (negative) EBP offset.</summary>
    public int Allocate(int bytes, int align)
    {
        if (align < 4)
        {
            align = 4;
        }
        _size = (_size + bytes + align - 1) / align * align;
        _allocations.Add((-_size, bytes, align));
        return -_size;
    }

    /// <summary>
    /// Moves blocks of the frame (FrameCompact): every offset that named the
    /// start of an old block names the new one. The size does not change.
    /// </summary>
    public void Move(Dictionary<int, int> moved)
    {
        foreach (FrameSlot slot in _slots.Keys.ToList())
            if (moved.TryGetValue(_slots[slot], out int to)) _slots[slot] = to;
        foreach (int id in _floatHomes.Keys.ToList())
            if (moved.TryGetValue(_floatHomes[id], out int to)) _floatHomes[id] = to;
        if (_scratch != 0 && moved.TryGetValue(_scratch, out int scratch)) _scratch = scratch;
        for (int i = 0; i < _allocations.Count; i++)
            if (moved.TryGetValue(_allocations[i].Offset, out int to)) _allocations[i] = (to, _allocations[i].Bytes, _allocations[i].Align);
    }

    /// <summary>The offset of an IR frame slot, placing it on first use.</summary>
    public int SlotOffset(FrameSlot slot)
    {
        if (!_slots.TryGetValue(slot, out int off))
        {
            off = Allocate(Math.Max(slot.Bytes, 4), slot.Align);
            _slots[slot] = off;
        }
        return off;
    }

    /// <summary>
    /// The home of a floating-point virtual register. Eight bytes whether
    /// F32 or F64, so a conversion between them can reuse the slot; an
    /// incoming parameter's home is its argument slot above EBP.
    /// </summary>
    public int FloatHome(VReg v)
    {
        if (!_floatHomes.TryGetValue(v.Id, out int off))
        {
            off = Allocate(8, 8);
            _floatHomes[v.Id] = off;
        }
        return off;
    }

    public void PlaceFloatParam(VReg v, int offset) => _floatHomes[v.Id] = offset;

    /// <summary>
    /// Sixteen bytes for the conversions that go through memory: fild wants
    /// its integer in memory, and the truncating fistp needs the old and new
    /// control words beside its result.
    /// </summary>
    public int Scratch
    {
        get
        {
            if (_scratch == 0)
            {
                _scratch = Allocate(16, 8);
            }
            return _scratch;
        }
    }

    /// <summary>A spill slot for one 32-bit virtual register.</summary>
    public int Spill() => Allocate(4, 4);

    /// <summary>
    /// The IR frame slots -- objects the lifetime passes put in the frame, the
    /// lowering's temporaries -- as (offset, bytes): the words of the frame a
    /// stack map does not describe, which the collector reads as they are
    /// (X86Backend.EmitStackMaps). Spill slots are the map's; floating-point
    /// homes and the scratch hold no reference.
    /// </summary>
    public IEnumerable<(int Offset, int Bytes)> SlotRanges()
    {
        foreach (var pair in _slots)
        {
            yield return (pair.Value, Math.Max(pair.Key.Bytes, 4));
        }
    }
}
