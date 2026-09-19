#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// Position-independent rewriting: one selected function, over virtual
/// registers, turned into code that reaches everything named through a GOT
/// pointer.
///
/// This runs between selection and register allocation, which is the only
/// place it can: it invents registers, so the allocator must still be to
/// come, and it must see the symbol references the selector chose rather
/// than the bytes the encoder would make of them.
///
/// The scheme, which is the i386 System V one in every respect but the last:
///
///   * a symbol this object defines and does not export is reached with
///     `lea r, [got + sym@GOTOFF]` -- the object cannot be interposed on
///     its own private names, so no indirection is needed;
///   * anything else -- imported, or exported and therefore interposable --
///     is reached through its GOT slot, `mov r, [got + sym@GOT]`;
///   * a call to a symbol this object defines is a direct relative call.
///     The linker binds defined symbols to their own definitions (GNU ld's
///     -Bsymbolic; see docs/X86-BACKEND.md), so there is nothing
///     for an indirection to decide.
///   * a call to a symbol this object does NOT define goes through the GOT
///     rather than through a PLT: `mov r, [got + sym@GOT]; call r`.
///
/// THE GOT POINTER IS A VIRTUAL REGISTER, and that is the one deliberate
/// departure from the ABI. The ABI pins it in EBX, which costs a register
/// in every function that names anything at all -- on a machine with six of
/// them, enough that eight functions of the runtime could not be allocated
/// at all. Here it is materialised at entry into a register like any other
/// value, and the allocator places it, or spills it under pressure, the way
/// it treats everything else.
///
/// What pinning EBX buys is the ABI's PLT, whose stubs jump through
/// `offset(%ebx)` by definition. Nothing needs them: binding is eager
/// (DT_BIND_NOW), so every GOT slot is filled before the first instruction
/// runs, and an imported function reached through its GOT slot is reached
/// correctly the first time. It costs a register at a call to an imported
/// function -- of which a self-contained COR-C# library has none -- and
/// gives back one everywhere else.
/// </summary>
internal static class Pic
{
    /// <summary>
    /// Rewrite <paramref name="m"/> in place.
    ///
    /// <paramref name="isPrivate"/> answers whether a name is defined by
    /// this object and invisible outside it, which is the only case GOTOFF
    /// is sound for; <paramref name="isDefined"/> answers whether this
    /// object defines it at all, which is what decides whether a call can be
    /// direct.
    /// </summary>
    public static void Run(MFunction m, Func<string, bool> isPrivate, Func<string, bool> isDefined, List<string> errors)
    {
        MReg? got = null;
        MReg Got() => got ??= m.NewReg();

        foreach (MBlock b in m.Blocks)
        {
            List<MInstr> outList = new();
            foreach (MInstr i in b.Instrs)
            {
                RewriteInstr(m, i, Got, isPrivate, isDefined, outList, errors);
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
        }

        // A function that names nothing outside itself is position
        // independent already, and paying for the GOT pointer would be waste.
        if (got is null)
        {
            return;
        }

        // Straight after the prologue, so that it dominates every use and so
        // that the frame is already set up if the allocator has to spill it.
        MBlock entry = m.Blocks[0];
        int at = entry.Instrs.Count > 0 && entry.Instrs[0].Op == MOp.Prologue ? 1 : 0;
        entry.Instrs.Insert(at, new MInstr(MOp.GotPc, got));

        // Unwind restores ESP/EBP and passes the exception in EAX; other
        // registers belong to the throwing frame. A landing pad is another
        // entry, not a path dominated by the normal prologue. Reconstruct
        // this object's GOT after preserving the incoming exception value.
        foreach (MBlock block in m.Blocks)
        {
            if (block.Source?.IsLandingPad != true) continue;
            int insert = block.Instrs.Count > 0
                && block.Instrs[0].Op == MOp.Mov
                && block.Instrs[0].Operands.Count == 2
                && block.Instrs[0].Operands[1] is MReg source
                && source.Id == (int)Gpr.Eax ? 1 : 0;
            block.Instrs.Insert(insert, new MInstr(MOp.GotPc, got));
        }
    }

    private static void RewriteInstr(
        MFunction m, MInstr i, Func<MReg> got,
        Func<string, bool> isPrivate, Func<string, bool> isDefined,
        List<MInstr> outList, List<string> errors)
    {
        if (i.Op == MOp.JmpTable)
        {
            // `jmp [table + index*4]`, where the table is one this object
            // wrote and nobody can interpose on. Reached from the GOT
            // pointer, which comes along as a second operand so that the
            // allocator knows it is live here; the encoder makes the
            // displacement a GOTOFF.
            i.Operands.Add(got());
            outList.Add(i);
            return;
        }

        if (i.Op == MOp.Call && i.Operands.Count > 0 && i.Operands[0] is MImm { Symbol: not null } callee)
        {
            if (isDefined(callee.Symbol!))
            {
                // Bound to this object's own definition by the linker, so the
                // relative call reaches it whatever address the object is
                // loaded at: both ends move together.
                outList.Add(new MInstr(MOp.Call, callee) { Width = i.Width, CallReloc = RelocKind.Rel32 });
                return;
            }
            // Imported. Eager binding means the slot is filled before this
            // ever runs, so the address can be loaded and called with no PLT
            // stub and no register pinned for one.
            MReg target = m.NewReg();
            Address(outList, got, target, callee.Symbol!, callee.Value, isPrivate);
            outList.Add(new MInstr(MOp.CallInd, target) { Width = i.Width });
            return;
        }

        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MImm { Label: not null } label:
                {
                    // The address of a block in this function: always this
                    // object's own copy, whatever interposition does to the
                    // function's name, so GOTOFF is right even when it is exported.
                    MReg r = m.NewReg();
                    outList.Add(new MInstr(MOp.Lea, r, new MMem(got(), 0) { Label = label.Label, Reloc = RelocKind.GotOff }));
                    i.Operands[k] = r;
                    break;
                }
                case MImm { Symbol: not null, Label: null } imm:
                {
                    if (i.Op is MOp.Imul3 or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd)
                    {
                        // These take a literal and nothing else, so there is
                        // no register form to fall back on. Nothing in
                        // lowering produces one; the check is here so that if
                        // something ever does, it is a diagnostic and not
                        // silently wrong code.
                        errors.Add($"{m.Source.Name}: cannot make '{imm.Symbol}' position-independent as an immediate of {i.Op}");
                        continue;
                    }
                    MReg r = m.NewReg();
                    Address(outList, got, r, imm.Symbol!, imm.Value, isPrivate);
                    i.Operands[k] = r;
                    break;
                }
                case MMem { Symbol: not null, Base: null } mem when isPrivate(mem.Symbol):
                {
                    // Same instruction, same length: the absolute
                    // displacement becomes one measured from the GOT and the
                    // GOT pointer supplies the base.
                    i.Operands[k] = new MMem(got(), mem.Disp)
                    {
                        Index = mem.Index, Scale = mem.Scale, Symbol = mem.Symbol, Reloc = RelocKind.GotOff,
                    };
                    break;
                }
                case MMem { Symbol: not null, Base: null } mem:
                {
                    // An interposable name has no fixed offset from the GOT,
                    // only a slot in it, so the address is loaded first.
                    MReg r = m.NewReg();
                    Address(outList, got, r, mem.Symbol, 0, isPrivate);
                    i.Operands[k] = new MMem(r, mem.Disp) { Index = mem.Index, Scale = mem.Scale };
                    break;
                }
                case MMem { Symbol: not null, Base: not null } mem:
                {
                    // A symbol's address added to a register -- an element of
                    // a static array, a field of a static object. The base
                    // register is taken, so the GOT-relative form has nowhere
                    // to go and the two are added instead.
                    //
                    // Left as an absolute displacement this was the one thing
                    // in a shared object that still needed the loader to write
                    // into the code: a text relocation, which costs the whole
                    // point of a shared text segment.
                    MReg r = m.NewReg();
                    Address(outList, got, r, mem.Symbol, 0, isPrivate);
                    outList.Add(new MInstr(MOp.Lea, r, new MMem(r, 0) { Index = mem.Base, Scale = 1 }));
                    i.Operands[k] = new MMem(r, mem.Disp) { Index = mem.Index, Scale = mem.Scale };
                    break;
                }
            }
        }
        outList.Add(i);
    }

    /// <summary>Emit whatever puts the address of a symbol plus an addend in a register.</summary>
    private static void Address(List<MInstr> outList, Func<MReg> got, MReg dst, string symbol, long addend, Func<string, bool> isPrivate)
    {
        if (isPrivate(symbol))
        {
            outList.Add(new MInstr(MOp.Lea, dst, MMem.GotOff(got(), symbol, checked((int)addend))));
            return;
        }
        // R_386_GOT32 gives the slot's offset from the GOT base, and the slot
        // holds the symbol's address alone, so an addend is a separate add.
        outList.Add(new MInstr(MOp.Mov, dst, new MMem(got(), 0) { Symbol = symbol, Reloc = RelocKind.Got32 }));
        if (addend != 0)
        {
            // LEA and not ADD: this may land between a compare and the
            // branch that reads its flags.
            outList.Add(new MInstr(MOp.Lea, dst, new MMem(dst, checked((int)addend))));
        }
    }
}

/// <summary>
/// The other half of the same problem: an executable that is NOT
/// position-independent and reaches a shared library's DATA.
///
/// Its code is absolute, so `mov eax, sym` is four bytes holding an address
/// the linker writes. For a name the library owns there is no such address
/// at link time, and the two answers the i386 ABI offers are both bad: a
/// COPY relocation gives the executable its own duplicate of the object,
/// which for a type descriptor means `x is Stream` comparing two different
/// addresses of the same type; a text relocation makes the loader write
/// into the code, which costs the shared text segment.
///
/// The third answer is the one taken here, and it is available because this
/// compiler owns both ends: the executable's GOT is at an address known
/// when it is linked, so an imported name is reached by loading its slot --
/// `mov eax, [0x804a01c]` -- with the loader filling that one word through
/// an ordinary R_386_GLOB_DAT. One extra load at the reference, no copy, no
/// text relocation, and the address is the library's own.
///
/// FUNCTIONS ARE NOT REWRITTEN: a call to an imported function already goes
/// through this executable's PLT, whose entries are absolute jumps through
/// the same GOT.
/// </summary>
internal static class Imports
{
    /// <summary>Rewrite every reference in <paramref name="m"/> to a name the loader will supply.</summary>
    public static void Run(MFunction m, Func<string, bool> isImported, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(isImported);
        ArgumentNullException.ThrowIfNull(errors);

        foreach (MBlock b in m.Blocks)
        {
            List<MInstr> outList = new();
            foreach (MInstr i in b.Instrs)
            {
                Rewrite(m, i, isImported, outList, errors);
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
        }
    }

    private static void Rewrite(MFunction m, MInstr i, Func<string, bool> isImported, List<MInstr> outList, List<string> errors)
    {
        // A call is the PLT's business and a jump table is this object's own.
        if (i.Op is MOp.Call or MOp.Jmp)
        {
            outList.Add(i);
            return;
        }

        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MImm { Symbol: not null, Label: null } imm when isImported(imm.Symbol!):
                {
                    if (i.Op is MOp.Imul3 or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd)
                    {
                        errors.Add($"{m.Source.Name}: '{imm.Symbol}' comes from a shared library and cannot be an immediate of {i.Op}");
                        continue;
                    }
                    MReg r = m.NewReg();
                    Slot(outList, r, imm.Symbol!, imm.Value);
                    i.Operands[k] = r;
                    break;
                }
                case MMem { Symbol: not null, Base: null } mem when isImported(mem.Symbol!):
                {
                    MReg r = m.NewReg();
                    Slot(outList, r, mem.Symbol!, 0);
                    i.Operands[k] = new MMem(r, mem.Disp) { Index = mem.Index, Scale = mem.Scale };
                    break;
                }
                case MMem { Symbol: not null, Base: not null } mem when isImported(mem.Symbol!):
                {
                    // A symbol's address plus a register: an element of a
                    // static array, a field of a static object. The base is
                    // taken, so the two are added.
                    MReg r = m.NewReg();
                    Slot(outList, r, mem.Symbol!, 0);
                    outList.Add(new MInstr(MOp.Lea, r, new MMem(r, 0) { Index = mem.Base, Scale = 1 }));
                    i.Operands[k] = new MMem(r, mem.Disp) { Index = mem.Index, Scale = mem.Scale };
                    break;
                }
            }
        }
        outList.Add(i);
    }

    /// <summary>Load the symbol's address out of its GOT slot, and add the addend if there is one.</summary>
    private static void Slot(List<MInstr> outList, MReg dst, string symbol, long addend)
    {
        outList.Add(new MInstr(MOp.Mov, dst, new MMem(null, 0) { Symbol = symbol, Reloc = RelocKind.GotAddr }));
        if (addend != 0)
        {
            // LEA and not ADD: this may land between a compare and the
            // branch that reads its flags.
            outList.Add(new MInstr(MOp.Lea, dst, new MMem(dst, checked((int)addend))));
        }
    }
}
