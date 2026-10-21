using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Block = Corsac.Lang.Ir.Block;
using Type = Corsac.Lang.Type;

/// <summary>
/// AN ATTRIBUTE IS EQUAL TO ANOTHER WHEN EVERY FIELD IS, as .NET's
/// System.Attribute has it: the same type, and each instance field of that
/// type and of its bases up to Attribute equal -- a reference by
/// Attribute.AreFieldValuesEqual (an array element by element, anything else
/// by its Equals), a value as its own Equals sees it (SameField). Its hash is
/// the first field's that has a value and is not an array, the most derived
/// class's fields first.
///
/// .NET READS THE FIELDS BY REFLECTION. Here the library's Attribute calls
/// two virtual members for them, EqualFields and FieldHash, and every class
/// derived from it whose slot still holds Attribute's own gets them written
/// here over its layout (SlotTarget), as a struct's Equals is (StructEquals).
/// </summary>
public sealed partial class Lowering
{
    /// <summary>Whether a type is the class library's System.Attribute.</summary>
    private static bool IsLibraryAttribute(TypeSymbol t)
        => t is { Name: "Attribute", Kind: TypeKind.Class, Decl: { } d } && Binder.ClassLibrarySide(d);

    /// <summary>What fills a slot of a class derived from Attribute that holds Attribute's own EqualFields or FieldHash, or null for any other slot.</summary>
    private string? AttributeSlot(TypeSymbol t, MethodSymbol? filled)
    {
        if (filled is not { Owner: { } owner } || ReferenceEquals(owner, t) || !IsLibraryAttribute(owner))
        {
            return null;
        }
        return filled.Name switch
        {
            "EqualFields" => AttributeFieldsEqual(t, owner, IrTypes.Of(filled.Returns)),
            "FieldHash" => AttributeFieldHash(t, owner),
            _ => null,
        };
    }

    /// <summary>Every instance field an attribute class holds, its own first and then each base's, up to Attribute.</summary>
    private static IEnumerable<FieldSymbol> AttributeFields(TypeSymbol t)
    {
        for (TypeSymbol? at = t; at is not null && !IsLibraryAttribute(at); at = at.Base)
        {
            foreach (FieldSymbol field in at.Fields)
            {
                if (!field.Static && !field.Boxed)
                {
                    yield return field;
                }
            }
        }
    }

    /// <summary>One of Attribute's static helpers, by name and parameter count.</summary>
    private MethodSymbol? AttributeHelper(TypeSymbol attribute, string name, int arity)
    {
        MethodSymbol? m = attribute.FindMethods(name).FirstOrDefault(m => m.Static && m.Params.Count == arity);
        if (m is not null)
        {
            Require(m);
        }
        return m;
    }

    /// <summary>`__attribute_equals$T(this, other)`: every field of two T's equal, `other` known to be a T.</summary>
    private string AttributeFieldsEqual(TypeSymbol t, TypeSymbol attribute, IrType returns)
    {
        string name = "__attribute_equals$" + TypeKey(t);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, returns) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "this"), b = f.NewReg(IrTypes.Word, "other");
        f.Params.Add(a);
        f.Params.Add(b);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail; HelperContext context = EnterHelper();
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;
        Node at = new MethodDecl { Name = name, Line = 0, Col = 0 };

        MethodSymbol? references = AttributeHelper(attribute, "AreFieldValuesEqual", 2);
        Block differ = _f.NewBlock("adiffer");
        foreach (FieldSymbol field in AttributeFields(t))
        {
            VReg same;
            if (CouldBeObject(field.Type) && references is not null)
            {
                VReg x = _e.Load(IrTypes.Word, a, field.Offset), y = _e.Load(IrTypes.Word, b, field.Offset);
                VReg said = CallDirect(references, IrTypes.Of(references.Returns), new List<Operand> { R(x), R(y) })!;
                same = _e.Binary(Opcode.Ne, R(said), Imm(0, said.Type), IrType.I32);
            }
            else
            {
                same = SameField(at, a, b, field);
            }
            Block next = _f.NewBlock("anext");
            _e.Branch(same, next, differ);
            _e.SetBlock(next);
        }
        _e.Ret(Imm(1, returns));
        _e.SetBlock(differ);
        _e.Ret(Imm(0, returns));

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail; LeaveHelper(context);
        return name;
    }

    /// <summary>`__attribute_hash$T(this)`: the hash of T's first field that has a value and is not an array, or 0.</summary>
    private string AttributeFieldHash(TypeSymbol t, TypeSymbol attribute)
    {
        string name = "__attribute_hash$" + TypeKey(t);
        if (!_structHelpers.Add(name))
        {
            return name;
        }
        Function f = new(name, IrType.I32) { Coalescible = true };
        VReg a = f.NewReg(IrTypes.Word, "this");
        f.Params.Add(a);
        Function savedFn = _f; Builder savedB = _e; Block? savedFail = _boundsFail; HelperContext context = EnterHelper();
        _f = f; _e = new Builder(f, f.NewBlock("entry")); _boundsFail = null;

        MethodSymbol? hashesBy = AttributeHelper(attribute, "HashesBy", 1);
        bool answered = false;
        foreach (FieldSymbol field in AttributeFields(t))
        {
            // A REFERENCE ONLY WHEN IT HOLDS SOMETHING THAT IS NOT AN ARRAY,
            // whose hash ignores the elements Equals compares; a value always,
            // as a boxed one is never null.
            if (CouldBeObject(field.Type) && hashesBy is not null)
            {
                VReg v = _e.Load(IrTypes.Word, a, field.Offset);
                VReg said = CallDirect(hashesBy, IrTypes.Of(hashesBy.Returns), new List<Operand> { R(v) })!;
                Block hash = _f.NewBlock("ahash"), next = _f.NewBlock("anext");
                _e.Branch(_e.Binary(Opcode.Ne, R(said), Imm(0, said.Type), IrType.I32), hash, next);
                _e.SetBlock(hash);
                _e.Ret(R(FieldHashOf(a, field)));
                _e.SetBlock(next);
                continue;
            }
            _e.Ret(R(FieldHashOf(a, field)));
            answered = true;
            break;
        }
        if (!answered)
        {
            _e.Ret(Imm(0, IrType.I32));
        }

        _m.Functions.Add(f);
        _f = savedFn; _e = savedB; _boundsFail = savedFail; LeaveHelper(context);
        return name;
    }
}
