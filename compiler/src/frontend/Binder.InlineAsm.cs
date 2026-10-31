#nullable enable
using System.Globalization;
using Corsac.Asm;

namespace Corsac.Lang;

/// <summary>
/// An asm block, bound (Corsac.Asm.InlineAsm): its names found as the
/// method's locals, parameters and constants, what it reads and writes
/// worked out from its instructions, and the block assembled once here --
/// every error it has is the source's, at the block's line, before any code
/// is made.
/// </summary>
/// <param name="Text">The block's text, its constants written as numbers.</param>
/// <param name="Bits">32 or 64: the target's.</param>
/// <param name="Locals">The locals and parameters it names, in the order the IR passes them.</param>
/// <param name="Names">A name of each, bound as any read of it is, for lowering to read and write.</param>
/// <param name="Read">Per local: the block needs its value.</param>
/// <param name="Written">Per local: the block gives it one.</param>
public sealed record AsmBinding(string Text, int Bits, AsmLocal[] Locals, NameExpr[] Names, bool[] Read, bool[] Written);

public sealed partial class BindResult
{
    /// <summary>Every asm block of the unit, bound (Binder.CheckAsm).</summary>
    public Dictionary<AsmStmt, AsmBinding> AsmBlocks { get; } = new(ReferenceEqualityComparer.Instance);
}

public sealed partial class Binder
{
    /// <summary>A position in an asm block: the block's line `line`, as a node a diagnostic can name.</summary>
    private static NameExpr AsmAt(AsmStmt a, int line) => new() { Name = "", Line = a.BodyLine + Math.Max(1, line) - 1, Col = 1 };

    /// <summary>
    /// A local's width and kind as a block sees it, or null with the reason:
    /// integers, characters, booleans, enums, pointers and nint/nuint by
    /// their width, floats and doubles; nothing the collector traces -- an
    /// object reference in a register is one it could not follow -- and no
    /// struct, which is no one register's worth.
    /// </summary>
    private static (int Width, char Kind)? AsmShape(Type t, out string why)
    {
        why = "";
        int word = Target.Current.WordSize;
        if (t.ParamName is not null) { why = "is of a type parameter's type, whose width is not known here"; return null; }
        if (t.IsNullableValue) { why = "is a nullable value, which is no single register's worth"; return null; }
        if (t.IsPointer || t.Function is not null) return (word, 'u');
        if (t.IsReference || t.IsArray)
        {
            why = "is an object reference, which the collector could not follow into a register: pass its address as a nuint, kept alive while the block runs";
            return null;
        }
        Prim p = t.IsEnumValue && t.Symbol is { } e ? e.EnumUnderlying : t.Symbol is not null ? Prim.Void : t.Prim;
        switch (p)
        {
            case Prim.Bool: case Prim.U8: return (1, 'u');
            case Prim.I8: return (1, 's');
            case Prim.I16: return (2, 's');
            case Prim.U16: case Prim.Char: return (2, 'u');
            case Prim.I32: return (4, 's');
            case Prim.U32: return (4, 'u');
            case Prim.I64: return (8, 's');
            case Prim.U64: return (8, 'u');
            case Prim.NInt: return (word, 's');
            case Prim.NUInt: return (word, 'u');
            case Prim.F32: return (4, 'f');
            case Prim.F64: return (8, 'f');
        }
        why = t.Symbol is not null ? "is a struct, which is no one register's worth: name its fields' copies, or its address as a nuint" : $"is a {t}, which no register holds";
        return null;
    }

    private void CheckAsm(AsmStmt a)
    {
        int bits = Target.Current.WordSize * 8;
        try
        {
            InlineAsm.Scan(a.Body, bits);
        }
        catch (AsmException e)
        {
            Error(AsmAt(a, e.Line), "asm: " + e.Message);
            return;
        }

        // THE NAMES (the parser's, one each: AsmStmt.Names): a local or a
        // parameter, which the block reads or writes; a constant, which it is
        // given as the number; nothing else.
        Dictionary<string, string> constants = new(StringComparer.Ordinal);
        List<AsmLocal> locals = new();
        List<Sym> symbols = new();
        List<NameExpr> localNames = new();
        // Those already bound as reads (a closure's field, found by binding it).
        HashSet<NameExpr> checkedNames = new(ReferenceEqualityComparer.Instance);
        bool failed = false;
        // A REGISTER IS A REGISTER: a local, a parameter or a constant spelt
        // as one a block names (`cx`, `si`) would be read as the register
        // without a word, so the block may not use that spelling.
        foreach (string register in InlineAsm.RegisterWords(a.Body))
            if (Lookup(register) is LocalSym or ParamSym or ConstSym)
            {
                Error(AsmAt(a, 1), $"asm: '{register}' is a register in an asm block, and so is not the local of that name: rename the local");
                if (Lookup(register) is LocalSym spelt) _assigned.Add(spelt);
                failed = true;
            }
        foreach (NameExpr name in a.Names)
        {
            Sym? sym = Lookup(name.Name);
            switch (sym)
            {
                case LocalSym or ParamSym:
                {
                    Type type = sym is LocalSym l ? l.Type : ((ParamSym)sym).Type;
                    if (AsmShape(type, out string why) is not { } shape)
                    {
                        Error(name, $"asm: '{name.Name}' {why}");
                        failed = true;
                        continue;
                    }
                    locals.Add(new AsmLocal(name.Name, shape.Width, shape.Kind));
                    symbols.Add(sym);
                    localNames.Add(name);
                    continue;
                }
                case ConstSym { Text: null, Number: null } constant when !IsReal(constant.Type):
                    constants[name.Name] = constant.Value.ToString(CultureInfo.InvariantCulture);
                    // Bound as any read of it is: every name under the statement resolved.
                    CheckName(name);
                    continue;
                case ConstSym:
                    Error(name, $"asm: '{name.Name}' is a constant no instruction takes as a number");
                    failed = true;
                    continue;
                case null when _thisType is not null && FindConstantOutward(_thisType, name.Name) is { } field:
                    if (IsReal(field.Type))
                    {
                        Error(name, $"asm: '{name.Name}' is a floating-point constant, which no instruction takes as a number");
                        failed = true;
                        continue;
                    }
                    constants[name.Name] = field.Value.ToString(CultureInfo.InvariantCulture);
                    CheckName(name);
                    continue;
                default:
                {
                    // A LOCAL A LAMBDA CAPTURED is a field of its closure where
                    // the lambda's body is bound for it: the same variable.
                    int errors = _r.Errors.Count;
                    CheckName(name);
                    Sym? resolved = _r.Resolved.TryGetValue(name, out Sym? named) ? named : sym;
                    FieldSymbol? closureField = resolved switch
                    {
                        CapturedFieldSym c => c.Field,
                        FieldSym { Field: var f } when IsClosure(f.Owner) => f,
                        _ => null,
                    };
                    if (closureField is not null)
                    {
                        checkedNames.Add(name);
                        if (AsmShape(closureField.Type, out string capturedWhy) is not { } capturedShape)
                        {
                            Error(name, $"asm: '{name.Name}' {capturedWhy}");
                            failed = true;
                            continue;
                        }
                        locals.Add(new AsmLocal(name.Name, capturedShape.Width, capturedShape.Kind));
                        symbols.Add(resolved!);
                        localNames.Add(name);
                        continue;
                    }
                    if (_r.Errors.Count == errors)
                        Error(name, $"asm: '{name.Name}' is not a local, a parameter, a constant or a label of this block");
                    failed = true;
                    continue;
                }
            }
        }
        if (failed)
        {
            // Its locals taken as written: the block's error is the one to read.
            foreach (Sym sym in symbols)
                if (sym is LocalSym refused) _assigned.Add(refused);
            return;
        }

        string text = InlineAsm.Substitute(a.Body, constants);
        AsmLocal[] shapes = locals.ToArray();
        InlineAsm.Analysis analysis;
        try
        {
            analysis = InlineAsm.Analyze(text, bits, shapes);
            // ONCE, AS IT WILL BE: every local a memory operand (the
            // bindings only take instructions away), every instruction held
            // to the 386 unless the block's `.runtime` names it.
            // Each slot one byte's displacement from the frame, the shortest
            // a slot's form is: a rel8 branch (LOOP, JECXZ) is out of reach
            // here only where it is out of reach in any frame.
            string trial = InlineAsm.Render(analysis, null, _ => bits == 64 ? "[rbp - 8]" : "[ebp - 8]");
            InlineAsm.Assemble(trial, bits, InlineAsm.Baseline(Target.Current.X86Profile, bits));
            // THE IR CARRIES IT IN A NAME (MachineIntrinsics.Asm), and an IR
            // name is at most 16384 bytes (IrBinary.Text).
            int spec = System.Text.Encoding.UTF8.GetByteCount(InlineAsm.Encode(new InlineAsm.Spec(bits, shapes, analysis.Read, analysis.Written, text)));
            if (spec > 16000)
                throw new AsmException("<asm>", 1, $"the block is {spec} bytes, and one is at most 16000: split it, or put it in an .asm file");
        }
        catch (AsmException e)
        {
            Error(AsmAt(a, e.Line), "asm: " + e.Message);
            // Its locals taken as written: the block's error is the one to read.
            foreach (Sym sym in symbols)
                if (sym is LocalSym refused) _assigned.Add(refused);
            return;
        }

        // Each local as the program reads it, or writes it: a write is an
        // assignment, which needs nothing before it.
        NameExpr[] bound = localNames.ToArray();
        for (int k = 0; k < shapes.Length; k++)
        {
            NameExpr n = bound[k];
            if (!analysis.Read[k] && symbols[k] is LocalSym assigned) _assigned.Add(assigned);
            if (!checkedNames.Contains(n)) CheckName(n);
            if (analysis.Written[k])
            {
                RequireAssignable(n, "write in an asm block");
                NoteWritten(n);
                if (symbols[k] is LocalSym written) _assigned.Add(written);
            }
        }
        _r.AsmBlocks[a] = new AsmBinding(text, bits, shapes, bound, analysis.Read, analysis.Written);
    }
}
