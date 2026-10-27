#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Asm;

/// <summary>
/// AN ASSEMBLED OBJECT'S FLOAT-CODE FACTS (Lto.X87Notes), as a compiled
/// unit's are (Selector.X87Fact): for each routine -- the code from a
/// .global label to the next, and the code before the first -- the line of
/// its first x87, MMX or SSE instruction, and the routines and symbols it
/// calls or jumps to, every one outside a packed block; a routine whose
/// last instruction does not end its flow falls into the next, a call too.
/// A routine's address stored whole into a symbol (`mov dword [S], L`,
/// how a kernel's start leaves its natives' addresses for Sys.CallNative)
/// is the routine "in S": a compiled call through a load of S calls it
/// (X87Notes.InField). Any other name an instruction or a datum refers to
/// has its address taken. Not the instructions that only keep the x87's
/// state -- its save and restore, its control and status words read --
/// which a context switch is made of.
/// </summary>
public sealed partial class X86Assembler
{
    private readonly record struct Noted(string Section, long At, int Line, string Mnemonic, bool Floats, string? Target);

    private readonly List<Noted> _noted = new();
    private readonly List<(string Field, string Routine)> _stored = new();
    private readonly HashSet<string> _referred = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notReferred = new(StringComparer.Ordinal);

    private static readonly HashSet<string> FpuStateOnly = new(StringComparer.Ordinal)
    {
        "fxsave", "fxrstor", "fnsave", "fsave", "frstor", "fnstenv", "fstenv", "fnstsw", "fstsw", "fnstcw", "fstcw", "fwait", "wait",
    };

    private static readonly string[] PackedRegisters = ["mm0", "mm1", "mm2", "mm3", "mm4", "mm5", "mm6", "mm7"];

    private bool NotingFloats => _pass == 2 && _object && !_everLongMode;

    /// <summary>An instruction, as it is assembled (pass two): whether it is float code, and whom a call or jump names.</summary>
    private void NoteInstruction(string mn, string rest)
    {
        _notReferred.Clear();
        if (!NotingFloats) return;
        string[] operands = Split(rest);
        string lower = rest.ToLowerInvariant();
        bool floats = mn.StartsWith('f') && !FpuStateOnly.Contains(mn)
            || mn is "emms" or "femms" or "ldmxcsr" || MmxOpcodes.ContainsKey(mn) || ThreeDNowOpcodes.ContainsKey(mn)
            || lower.Contains("xmm") || PackedRegisters.Any(r => Words(lower).Contains(r));
        string? target = null;
        if (mn is "call" or "jmp" && operands.Length == 1 && IsName(operands[0].Trim()) && !operands[0].Trim().StartsWith('.'))
        {
            target = operands[0].Trim();
            _notReferred.Add(target);
        }
        // A ROUTINE'S ADDRESS INTO A SYMBOL, whole: `mov dword [S], L`.
        if (mn == "mov" && operands.Length == 2)
        {
            string into = operands[0].Trim(), from = operands[1].Trim();
            if (into.StartsWith("dword ", StringComparison.OrdinalIgnoreCase)) into = into[6..].Trim();
            if (into.StartsWith('[') && into.EndsWith(']') && IsName(into[1..^1].Trim()) && IsName(from) && !from.StartsWith('.'))
            {
                _stored.Add((into[1..^1].Trim(), from));
                _notReferred.Add(from);
            }
        }
        _noted.Add(new Noted(_sec.Name, Pc, _line, mn, floats, target));
    }

    /// <summary>The instruction done: its targets are no longer what a datum's names are checked against.</summary>
    private void NotedInstruction() => _notReferred.Clear();

    /// <summary>A name a field of code or data is made of, its address therefore taken -- but a call's or a jump's target, or a routine stored into a symbol (NoteInstruction).</summary>
    private void NoteReference(string name)
    {
        if (NotingFloats && !_notReferred.Contains(name)) _referred.Add(name);
    }

    private static IEnumerable<string> Words(string text)
        => text.Split([' ', ',', '[', ']', '+', '-', '*', '(', ')', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The object's facts (NoteInstruction, NoteReference), by routine.</summary>
    private List<X87Notes.Fact> FloatFacts()
    {
        string file = Path.GetFileName(_file);
        // Each section's routines, by where they start: its .global labels,
        // and its start, whose code is the file's own before the first.
        Dictionary<string, List<(long At, string Name)>> starts = new(StringComparer.Ordinal);
        foreach (Sec sec in _sections) starts[sec.Name] = new() { (long.MinValue, OwnName(sec.Name)) };
        foreach (string name in _globals)
            if (_labels.TryGetValue(name, out long at) && _labelSection.TryGetValue(name, out string? where) && starts.TryGetValue(where, out var list))
                list.Add((at, name));
        foreach (var list in starts.Values) list.Sort((a, b) => a.At.CompareTo(b.At));

        string Owner(string section, long at)
        {
            var list = starts[section];
            string name = list[0].Name;
            foreach (var start in list) { if (start.At > at) break; name = start.Name; }
            return name;
        }
        // A name of this file is its routine's; any other is the linker's.
        string Routine(string name)
            => _labels.TryGetValue(name, out long at) && _labelSection.TryGetValue(name, out string? where) ? Owner(where, at) : name;

        Dictionary<string, (int FloatLine, List<X87Notes.Call> Calls)> routines = new(StringComparer.Ordinal);
        foreach (var list in starts.Values) foreach (var start in list) routines.TryAdd(start.Name, (-1, new()));
        Noted? last = null;
        string? lastOwner = null;
        foreach (Noted n in _noted)
        {
            string owner = Owner(n.Section, n.At);
            // FALLEN INTO from the routine before, where its last instruction
            // went on to the next.
            if (lastOwner is not null && owner != lastOwner && last is { } before && before.Section == n.Section
                && before.Mnemonic is not ("ret" or "retf" or "iret" or "iretd" or "jmp" or "ud2" or "sysexit" or "sysret"))
                routines[lastOwner].Calls.Add(new(owner, before.Line, false));
            var r = routines[owner];
            if (n.Floats && r.FloatLine < 0) routines[owner] = r = (n.Line, r.Calls);
            if (n.Target is { } target)
            {
                string callee = Routine(target);
                if (callee != owner) r.Calls.Add(new(callee, n.Line, false));
            }
            last = n;
            lastOwner = owner;
        }
        List<X87Notes.Fact> facts = new();
        foreach ((string name, var r) in routines)
            facts.Add(new(name, name, file, r.FloatLine, false, r.Calls.ToArray()));
        foreach (var group in _stored.GroupBy(s => s.Field, StringComparer.Ordinal))
            facts.Add(new(X87Notes.InField(group.Key), "the routine in " + group.Key, file, -1, false,
                group.Select(s => new X87Notes.Call(Routine(s.Routine), 0, false)).ToArray()));
        foreach (string name in _referred)
            facts.Add(new(Routine(name), "", "", -1, true, Array.Empty<X87Notes.Call>()));
        return facts;

        string OwnName(string section) => file + ":" + section;
    }
}
