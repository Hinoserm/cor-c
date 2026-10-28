#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Asm;

/// <summary>
/// AN ASSEMBLED OBJECT'S FLOAT-CODE FACTS (Lto.X87Notes), as a compiled
/// unit's are (Selector.X87Fact): for each routine -- the code from a
/// .global label to the next, and the code before the first -- the line of
/// its first x87, MMX or SSE instruction, and the routines and symbols it
/// calls or jumps to -- every branch, a call or a jump of any kind, to
/// another routine: a pc-relative field -- each outside a packed block; a
/// routine whose last instruction does not end its flow falls into the
/// next, a call too. A routine's address stored whole into a symbol (`mov
/// dword [S], L`, how a kernel's start leaves its natives' addresses for
/// Sys.CallNative and Sys.CallNativeFloat) is the routine "in S": a compiled call through a load of
/// S calls it (X87Notes.InField), and S named any other way -- `call [S]`,
/// its address taken -- takes the routine in it anywhere. Any other name an
/// absolute field refers to has its address taken. Every routine is one a
/// processor or a loader may enter by itself (Fact.Entered): nothing named
/// calling it, it runs outside. Not the instructions that only keep the
/// x87's state -- its save and restore, its control and status words read
/// -- which a context switch is made of, nor those that set a thread's
/// state up or load it (FNINIT, FLDCW, FLDENV, FNCLEX, LDMXCSR, STMXCSR);
/// nor float code written as bytes (.db), which no mnemonic says.
/// </summary>
public sealed partial class X86Assembler
{
    private readonly record struct Noted(string Section, long At, int Line, string Mnemonic, bool Floats);

    private readonly List<Noted> _noted = new();
    private readonly List<(string Section, long At, int Line, string Target)> _branches = new();
    private readonly List<(string Field, string Routine)> _stored = new();
    private readonly HashSet<string> _referred = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notReferred = new(StringComparer.Ordinal);
    private string? _storeField;
    private long _instructionAt;
    private readonly List<(string Section, long At)> _selfTaken = new();

    private static readonly HashSet<string> FpuStateOnly = new(StringComparer.Ordinal)
    {
        "fxsave", "fxrstor", "fnsave", "fsave", "frstor", "fnstenv", "fstenv", "fnstsw", "fstsw", "fnstcw", "fstcw", "fwait", "wait",
        // And what sets a thread's state up or loads it: its start (FNINIT,
        // MXCSR's first value) and a switch's other half.
        "fninit", "finit", "fldcw", "fldenv", "fnclex", "fclex", "ldmxcsr", "stmxcsr",
    };

    private static readonly string[] PackedRegisters = ["mm0", "mm1", "mm2", "mm3", "mm4", "mm5", "mm6", "mm7"];

    private bool NotingFloats => _pass == 2 && _object && !_everLongMode;

    /// <summary>An instruction, as it is assembled (pass two): whether it is float code, where it is, and a routine's address it stores into a symbol; its branches are NoteReference's.</summary>
    private void NoteInstruction(string mn, string rest)
    {
        _notReferred.Clear();
        _storeField = null;
        if (!NotingFloats) return;
        _instructionAt = Pc;
        string[] operands = Split(rest);
        string lower = rest.ToLowerInvariant();
        string[] words = Words(lower).ToArray();
        bool floats = mn.StartsWith('f') && !FpuStateOnly.Contains(mn)
            || mn is "emms" or "femms" || MmxOpcodes.ContainsKey(mn) || ThreeDNowOpcodes.ContainsKey(mn)
            || words.Any(w => w.StartsWith("xmm", StringComparison.Ordinal) || PackedRegisters.Contains(w));
        // A prefix's instruction is what ends a flow or not: `rep ret`.
        string flow = mn is "rep" or "repe" or "repz" or "repne" or "repnz" or "lock" && words.Length > 0 ? words[0] : mn;
        // A ROUTINE'S ADDRESS INTO A SYMBOL, whole: `mov dword [S], L`.
        if (mn == "mov" && operands.Length == 2)
        {
            string into = operands[0].Trim(), from = operands[1].Trim();
            if (into.StartsWith("dword ", StringComparison.OrdinalIgnoreCase)) into = into[6..].Trim();
            if (into.StartsWith('[') && into.EndsWith(']') && IsName(into[1..^1].Trim()) && IsName(from) && !from.StartsWith('.'))
            {
                _stored.Add((into[1..^1].Trim(), from));
                _notReferred.Add(from);
                _storeField = into[1..^1].Trim();
            }
        }
        _noted.Add(new Noted(_sec.Name, Pc, _line, flow, floats));
    }

    /// <summary>The instruction done: its targets are no longer what a datum's names are checked against.</summary>
    private void NotedInstruction()
    {
        _notReferred.Clear();
        _storeField = null;
    }

    /// <summary>
    /// A name a field of code or data is made of. A pc-relative field is a
    /// branch's, a call's or a jump's: to the routine it names. Any other
    /// takes the name's address -- but a routine stored into a symbol
    /// (NoteInstruction) -- and a symbol named so, other than as such a
    /// store's, takes the routine in it too.
    /// </summary>
    private void NoteReference(string name, bool pcRelative)
    {
        if (!NotingFloats) return;
        // `$`, the instruction itself: a jump to it goes nowhere else, its
        // value taken takes the routine it is in.
        if (name == "$") { if (!pcRelative) _selfTaken.Add((_sec.Name, _instructionAt)); return; }
        if (pcRelative) { _branches.Add((_sec.Name, _instructionAt, _line, name)); return; }
        if (_notReferred.Contains(name)) return;
        _referred.Add(name);
        if (name != _storeField) _referred.Add(X87Notes.InField(name));
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
        foreach (var list in starts.Values) list.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : string.CompareOrdinal(a.Name, b.Name));

        // A routine is the first of the labels at its start; another there
        // is a name of it (aliases, below).
        string Owner(string section, long at)
        {
            var list = starts[section];
            (long At, string Name) found = list[0];
            foreach (var start in list) { if (start.At > at) break; if (start.At != found.At) found = start; }
            return found.Name;
        }
        // A name of this file is its routine's; any other is the linker's.
        string Routine(string name)
            => _labels.TryGetValue(name, out long at) && _labelSection.TryGetValue(name, out string? where) ? Owner(where, at) : name;

        Dictionary<string, (int FloatLine, List<X87Notes.Call> Calls)> routines = new(StringComparer.Ordinal);
        List<(string Alias, string Of)> aliases = new();
        foreach ((string section, var list) in starts)
            foreach (var start in list)
            {
                string owner = Owner(section, start.At);
                if (owner == start.Name) routines.TryAdd(start.Name, (-1, new()));
                else aliases.Add((start.Name, owner));
            }
        Noted? last = null;
        string? lastOwner = null;
        foreach (Noted n in _noted)
        {
            string owner = Owner(n.Section, n.At);
            // FALLEN INTO from the routine before, where its last instruction
            // went on to the next.
            if (lastOwner is not null && owner != lastOwner && last is { } before && before.Section == n.Section
                && before.Mnemonic is not ("ret" or "retn" or "retf" or "iret" or "iretd" or "jmp" or "ud2" or "sysexit" or "sysret"))
                routines[lastOwner].Calls.Add(new(owner, before.Line, false));
            var r = routines[owner];
            if (n.Floats && r.FloatLine < 0) routines[owner] = r = (n.Line, r.Calls);
            last = n;
            lastOwner = owner;
        }
        // EVERY BRANCH, CALL AND JUMP to another routine, or to a name only the
        // linker knows.
        foreach (var branch in _branches)
        {
            string owner = Owner(branch.Section, branch.At), callee = Routine(branch.Target);
            if (callee != owner && routines.TryGetValue(owner, out var r)) r.Calls.Add(new(callee, branch.Line, false));
        }
        List<X87Notes.Fact> facts = new();
        foreach ((string name, var r) in routines)
            facts.Add(new(name, name, file, r.FloatLine, false, r.Calls.ToArray()) { Entered = true });
        // AN ALIAS is a name of its routine, entered as it is (the routine's
        // own fact) and not on its own: one nothing names pulls nothing out.
        foreach ((string alias, string of) in aliases)
            facts.Add(new(alias, alias, file, -1, false, [new X87Notes.Call(of, 0, false)]));
        foreach (var self in _selfTaken)
            facts.Add(new(Owner(self.Section, self.At), "", "", -1, true, Array.Empty<X87Notes.Call>()));
        foreach (var group in _stored.GroupBy(s => s.Field, StringComparer.Ordinal))
            facts.Add(new(X87Notes.InField(group.Key), "the routine in " + group.Key, file, -1, false,
                group.Select(s => new X87Notes.Call(Routine(s.Routine), 0, false)).ToArray()));
        foreach (string name in _referred)
            facts.Add(new(Routine(name), "", "", -1, true, Array.Empty<X87Notes.Call>()));
        return facts;

        string OwnName(string section) => file + ":" + section;
    }
}
