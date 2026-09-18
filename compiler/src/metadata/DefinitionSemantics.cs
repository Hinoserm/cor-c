using System.Security.Cryptography;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;
using IrBlock = Corsac.Lang.Ir.Block;

namespace Corsac.Lang.Metadata;

/// <summary>
/// Pre-optimization structural identity for explicitly shareable definitions.
/// Local constants are identified by contents, not unit-local serial numbers.
/// Native integrity is certified separately after code generation.
/// </summary>
public static class DefinitionSemantics
{
    public static Dictionary<string, byte[]> Capture(Module module)
    {
        Dictionary<string, Function> functions = module.Functions.ToDictionary(value => value.Name, StringComparer.Ordinal);
        Dictionary<string, DataItem> data = module.Data.ToDictionary(value => value.Name, StringComparer.Ordinal);
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (string name in functions.Values.Where(value => value.Exported && value.Coalescible && value.Async is null).Select(value => value.Name)
            .Concat(data.Values.Where(value => value.Exported && value.Coalescible).Select(value => value.Name)))
        {
            using SHA256 hash = SHA256.Create();
            using CryptoStream stream = new(Stream.Null, hash, CryptoStreamMode.Write);
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            Dictionary<string, int> active = new(StringComparer.Ordinal);
            int visits = 0;
            void Reference(string symbol)
            {
                bool local = data.TryGetValue(symbol, out DataItem? item) && !item.Exported
                    || functions.TryGetValue(symbol, out Function? function) && !function.Exported;
                writer.Write(local);
                if (local) Definition(symbol); else writer.Write(symbol);
            }
            void Register(VReg? register)
            {
                writer.Write(register is not null);
                if (register is not null) { writer.Write(register.Id); writer.Write((byte)register.Type); }
            }
            void Definition(string symbol)
            {
                if (active.TryGetValue(symbol, out int cycle)) { writer.Write((byte)0); writer.Write(cycle); return; }
                if (++visits > 65536 || active.Count >= 128) throw new InvalidDataException("Definition semantics exceed graph budget: " + name);
                active.Add(symbol, active.Count);
                if (data.TryGetValue(symbol, out DataItem? item))
                {
                    writer.Write((byte)1); writer.Write(item.Align); writer.Write(item.ReadOnly); writer.Write(item.Zero);
                    writer.Write(item.Bytes.Length); writer.Write(item.Bytes);
                    writer.Write(item.Relocs.Count);
                    foreach (DataReloc relocation in item.Relocs.OrderBy(value => value.Offset))
                    { writer.Write(relocation.Offset); writer.Write(relocation.Addend); Reference(relocation.Symbol); }
                }
                else
                {
                    Function function = functions[symbol];
                    if (function.Async is not null) throw new InvalidDataException("Async definition requires a state-machine identity: " + symbol);
                    writer.Write((byte)2); writer.Write((byte)function.Returns);
                    writer.Write(function.Params.Count);
                    foreach (VReg parameter in function.Params) Register(parameter);
                    writer.Write(function.Slots.Count);
                    foreach (FrameSlot slot in function.Slots) { writer.Write(slot.Id); writer.Write(slot.Bytes); writer.Write(slot.Align); }
                    Dictionary<IrBlock, int> blocks = function.Blocks.Select((block, ordinal) => (block, ordinal)).ToDictionary(value => value.block, value => value.ordinal);
                    writer.Write(function.Blocks.Count);
                    foreach (IrBlock block in function.Blocks)
                    {
                        writer.Write(block.IsLandingPad); writer.Write(block.Instrs.Count);
                        foreach (Instr instruction in block.Instrs)
                        {
                            writer.Write((int)instruction.Op); Register(instruction.Dest);
                            writer.Write(instruction.Size); writer.Write(instruction.Signed); writer.Write(instruction.Offset);
                            writer.Write(instruction.Callee is not null);
                            if (instruction.Callee is not null) Reference(instruction.Callee);
                            writer.Write(instruction.Operands.Count);
                            foreach (Operand operand in instruction.Operands)
                            {
                                writer.Write((byte)operand.Type);
                                switch (operand)
                                {
                                    case RegOperand reg: writer.Write((byte)1); Register(reg.Reg); break;
                                    case ImmOperand immediate: writer.Write((byte)2); writer.Write(immediate.Value); break;
                                    case SymOperand address: writer.Write((byte)3); Reference(address.Name); writer.Write(address.Offset); break;
                                    case SlotOperand slot: writer.Write((byte)4); writer.Write(slot.Slot.Id); break;
                                    default: throw new InvalidDataException("Unknown IR operand in semantic identity");
                                }
                            }
                            writer.Write(instruction.Targets.Count);
                            foreach (IrBlock target in instruction.Targets) writer.Write(blocks[target]);
                            writer.Write(instruction.Default is null ? -1 : blocks[instruction.Default]);
                        }
                    }
                }
                active.Remove(symbol);
            }
            writer.Write(1); writer.Write(Target.Current.Name); writer.Write(Target.Current.WordSize);
            Definition(name);
            writer.Flush(); stream.FlushFinalBlock(); result.Add(name, hash.Hash!);
        }
        return result;
    }

    public static void Attach(ObjectFile obj, IReadOnlyDictionary<string, byte[]> semantics)
    {
        HashSet<string> definitions = obj.Symbols.Where(symbol => symbol.IsDefined && symbol.Global).Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        CoalescingContract.Attach(obj, semantics.Where(pair => definitions.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}
