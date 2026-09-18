#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lower;

using Type = Corsac.Lang.Type;

/// <summary>
/// WHAT AN ENUM VALUE IS CALLED.
///
/// .NET answers `Colour.Green.ToString()` out of the type's metadata, and
/// there is no metadata here: an enum is a four-byte number with a symbol
/// beside it, and by the time a program runs the names have gone. So the code
/// generator writes them down -- one table per enum a program ever renders,
/// the names in one array and their values in another, both ordinary read-only
/// arrays the runtime indexes.
///
/// TWO ARRAYS RATHER THAN ONE BLOB, and both of them real managed arrays,
/// because everything that reads them is written in the language:
/// `Runtime.EnumName` walks them the way any other code walks a `string[]`,
/// and `Enum.GetNames` can hand one straight back. The names are the same
/// interned literals the program's own strings are, so a table costs a word
/// and four bytes per member and nothing at all for the text.
///
/// ON DEMAND: a table exists only for an enum something actually asks the name
/// of. The shipped libraries declare twenty-seven enums between them and a
/// program that prints none of them carries none of the tables.
///
/// IN VALUE ORDER, which is the order .NET reports an enum's members in, and
/// what lets a `[Flags]` value be taken apart largest member first.
/// </summary>
public sealed partial class Lowering
{
    private readonly Dictionary<string, string> _enumNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumValues = new(StringComparer.Ordinal);

    /// <summary>
    /// An enum's members in value order, which is the order .NET reports them
    /// in. A STABLE sort, so that two members which alias one value -- C#
    /// allows it -- stay in the order they were written and the name the
    /// table answers with does not depend on how the sort happened to run.
    /// </summary>
    private static List<KeyValuePair<string, long>> EnumMembers(TypeSymbol e)
        => e.EnumValues.OrderBy(m => m.Value).ToList();

    /// <summary>The `string[]` of an enum's member names, in value order.</summary>
    private string EnumNamesTable(TypeSymbol e)
    {
        string key = TypeKey(e);

        if (_enumNames.TryGetValue(key, out string? sym))
        {
            return sym;
        }

        List<KeyValuePair<string, long>> members = EnumMembers(e);
        int w = _t.WordSize;
        byte[] block = new byte[_t.ArrayHeaderBytes + members.Count * w];
        WriteWord(block, _t.ArrayCountOffset, members.Count);

        // Named and registered BEFORE a string is interned, for the reason
        // InternString itself gives: interning asks for a descriptor, whose
        // own name is a string, and the way back in must find this already
        // written down.
        sym = "en_" + key;
        _enumNames[key] = sym;

        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, Exported = false };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(0, SequenceDescriptor("string", w, isString: false), _t.DescriptorBytes));

        for (int i = 0; i < members.Count; i++)
        {
            item.Relocs.Add(new DataReloc(_t.ArrayHeaderBytes + i * w, InternString(members[i].Key), 0));
        }
        return sym;
    }

    /// <summary>The `int[]` beside it: what each of those names is worth.</summary>
    private string EnumValuesTable(TypeSymbol e)
    {
        string key = TypeKey(e);

        if (_enumValues.TryGetValue(key, out string? sym))
        {
            return sym;
        }

        List<KeyValuePair<string, long>> members = EnumMembers(e);
        byte[] block = new byte[_t.ArrayHeaderBytes + members.Count * 4];
        WriteWord(block, _t.ArrayCountOffset, members.Count);

        for (int i = 0; i < members.Count; i++)
        {
            int at = _t.ArrayHeaderBytes + i * 4;
            long value = members[i].Value;

            for (int b = 0; b < 4; b++)
            {
                block[at + b] = (byte)(value >> (8 * b));
            }
        }

        sym = "ev_" + key;
        _enumValues[key] = sym;

        DataItem item = new(sym, block) { ReadOnly = true, Align = _t.Align64, Exported = false };
        _m.Data.Add(item);
        item.Relocs.Add(new DataReloc(0, SequenceDescriptor("int", 4, isString: false), _t.DescriptorBytes));
        return sym;
    }

    /// <summary>
    /// An enum value as text: the member's name, the members a `[Flags]`
    /// value is made of, and otherwise the number -- which is exactly what
    /// .NET prints. Null when the runtime that reads the table is not linked,
    /// so the caller can fall back to the number.
    /// </summary>
    private VReg? EnumText(Expr at, VReg value, TypeSymbol e)
    {
        MethodSymbol? render = RuntimeMethod("EnumName", 4);

        if (render is null)
        {
            return null;
        }

        Require(render);

        VReg names = _e.Address(EnumNamesTable(e));
        VReg values = _e.Address(EnumValuesTable(e));
        VReg number = Convert(at, value, Type.I32, render.Params[3].Type);

        return _e.Call(CallLabel(render), IrTypes.Word,
                       R(names), R(values), Imm(e.IsFlags ? 1 : 0, IrType.I32), R(number));
    }

    /// <summary>
    /// One of System.Enum's statics, over the table this enum's names are in.
    ///
    /// The binder settled which enum and which member (BindResult.EnumStatics)
    /// and turned the two that are simply a list into that list; what is left
    /// is the four that read the table, each of them one call.
    /// </summary>
    private VReg EmitEnumStatic(CallExpr call, TypeSymbol e, string name)
    {
        // The older spelling names the enum with a leading `typeof`, which
        // said everything it had to say at compile time.
        int skip = call.Args.Count > 0 && call.Args[0] is TypeOfExpr ? 1 : 0;
        List<Expr> rest = call.Args.Skip(skip).ToList();

        string wanted = name switch
        {
            "GetName"   => "EnumLabel",
            "IsDefined" => "EnumDefined",
            "Parse"     => "EnumParse",
            _           => "EnumTryParse",
        };

        int arity = name switch
        {
            "GetName"   => 3,
            "IsDefined" => 2,
            "Parse"     => 4,
            _           => 5,
        };

        MethodSymbol? run = RuntimeMethod(wanted, arity);

        if (run is null)
        {
            return Fail(call, $"'Enum.{name}' needs {RuntimeType}.{wanted}, which no compiled source provides; compile with the runtime library");
        }

        Require(run);

        VReg names = _e.Address(EnumNamesTable(e));
        VReg values = _e.Address(EnumValuesTable(e));

        if (name == "IsDefined")
        {
            VReg one = Convert(call, EvalAs(rest[0], Type.I32), Type.I32, run.Params[1].Type);
            return _e.Call(CallLabel(run), IrType.I32, R(values), R(one))!;
        }

        if (name == "GetName")
        {
            VReg one = Convert(call, EvalAs(rest[0], Type.I32), Type.I32, run.Params[2].Type);
            return _e.Call(CallLabel(run), IrTypes.Word, R(names), R(values), R(one))!;
        }

        // Parse and TryParse: the text, then whether case matters, then --
        // for TryParse -- where to write the answer.
        bool tries = name == "TryParse";
        int folded = tries ? rest.Count - 2 : rest.Count - 1;
        VReg text = EvalAs(rest[0], Type.String);
        VReg fold = folded > 0 ? EvalAs(rest[1], Type.Bool) : _e.Const(0, IrType.I32);

        if (!tries)
        {
            return _e.Call(CallLabel(run), IrType.I32, R(names), R(values), R(text), R(fold))!;
        }

        VReg into = EvalAs(rest[^1], new Type { Prim = Prim.I32, Symbol = e }, byRef: true);
        return _e.Call(CallLabel(run), IrType.I32, R(names), R(values), R(text), R(fold), R(into))!;
    }
}
