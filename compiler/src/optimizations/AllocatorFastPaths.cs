#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE ALLOCATION PUT IN PLACE AT EVERY `new`, last. Lowering makes each
/// allocation a call to Runtime.AllocObject, AllocLeaf or Alloc, and every
/// pass that reasons about objects knows a `new` by that call -- Escape and
/// the lifetime rules, ScalarObjects, the region passes that make it
/// AllocRegion -- so it stays one through all of them, as the card marks do
/// (CardMarks). After the last of them, each such call of a size the
/// compiler knows and a size class holds becomes Runtime.AllocFastSized,
/// given the kind the runtime's own function passes Gc.AllocWord and the
/// class's slot and size as constants, and that body is put where the call
/// was: a load of the thread's cache, one test, a pop and two stores. A
/// call of a size not known becomes a call to Runtime.AllocFast, which
/// finds the class in the collector's tables. A size no class holds stays
/// the call it was.
///
/// Only where the runtime defines them, and never inside AllocMissed, whose
/// calls to the three are the long way itself.
/// </summary>
public static class AllocatorFastPaths
{
    public const string Sized = Corsac.Lang.Lto.RuntimeAbi.AllocFastSized;
    public const string Fast = Corsac.Lang.Lto.RuntimeAbi.AllocFast;
    public const string Missed = Corsac.Lang.Lto.RuntimeAbi.AllocMissed;
    public const string Region = Corsac.Lang.Lto.RuntimeAbi.AllocRegion;
    public const string RegionSized = Corsac.Lang.Lto.RuntimeAbi.AllocRegionSized;

    /// <summary>
    /// A REGION'S BLOCK FOR A REQUEST, as Gc.AllocRegion lays it: the request
    /// with a header (two words) and a footer (one), rounded to eight, and at
    /// least four words (Gc.Smallest). Null for a size this does not hold
    /// to be safe to fold (negative, or past a megabyte, which the long way
    /// takes as it always did). THE RUNTIME'S LAYOUT AND THIS MUST AGREE.
    /// </summary>
    public static long? RegionBlock(long bytes, int wordSize)
    {
        if (bytes < 0 || bytes > 1048576) return null;
        long need = (bytes + 3 * wordSize + 7) & -8;
        return need < 4 * wordSize ? 4 * wordSize : need;
    }

    /// <summary>
    /// Each call to AllocRegion of a constant size made AllocRegionSized,
    /// its block's size added (RegionBlock): the site stays a region's, its
    /// frame the one it computed. Whether there was one.
    /// </summary>
    public static bool RetargetRegions(Function f)
    {
        if (f.Name is Region or RegionSized) return false;
        int w = Target.Current.WordSize;
        bool any = false;
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Call || i.Callee != Region || i.Operands.Count != 3
                    || i.Operands[0] is not ImmOperand constant || RegionBlock(constant.Value, w) is not long need) continue;
                b.Instrs[k] = new Instr
                {
                    Op = Opcode.Call, Dest = i.Dest, Callee = RegionSized,
                    Operands = { i.Operands[0], i.Operands[1], i.Operands[2], new ImmOperand(need, IrTypes.Word) },
                    Line = i.Line,
                };
                any = true;
            }
        }
        return any;
    }

    // The kinds Runtime passes Gc.AllocWord for each (Gc.KindObject, KindLeaf;
    // 0 for a block scanned word by word), as RegionPointsTo knows them.
    private const long LeafKind = 0x4C454146, ObjectKind = 0x4F424A54;

    private static long? KindOf(string? callee) => callee switch
    {
        Corsac.Lang.Lto.RuntimeAbi.AllocObject => ObjectKind,
        Corsac.Lang.Lto.RuntimeAbi.AllocLeaf => LeafKind,
        Corsac.Lang.Lto.RuntimeAbi.Alloc => 0,
        _ => null,
    };

    /// <summary>
    /// THE SIZE CLASS OF A REQUEST, as Gc.StartClasses lays the classes out
    /// and Gc._classOf answers: the request rounded with its header (two
    /// words) to eight; classes of 16 to 128 bytes by eights, then eighths of
    /// each power of two to ClassMax, 1024. The slot is the class's free-list
    /// word in a thread's cache (two words a class: the page, the list).
    /// Null for a size no class holds. THE RUNTIME'S TABLE AND THIS MUST
    /// AGREE: change one, change both.
    /// </summary>
    public static (long Slot, long Size)? ClassOf(long bytes, int wordSize)
    {
        const long ClassMax = 1024;
        if (bytes < 0) return null;
        long small = (bytes + 2 * wordSize + 7) & -8;
        if (small > ClassMax) return null;
        long size = 16;
        long c = 0;
        while (size < small)
        {
            size += size < 128 ? 8 : size < 256 ? 16 : size < 512 ? 32 : 64;
            c++;
        }
        return (c * 2 * wordSize + wordSize, size);
    }

    /// <summary>
    /// Each allocation call of `f` made AllocFastSized (a constant size a
    /// class holds) or AllocFast (a size not known); whether any became
    /// AllocFastSized, the one to put in place.
    /// </summary>
    public static bool Retarget(Function f)
    {
        if (f.Name is Sized or Fast or Missed) return false;
        int w = Target.Current.WordSize;
        bool sized = false;
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                // A site the link chose for a region is its call still, for
                // MakeSitesInRegion to make so.
                if (i.Op != Opcode.Call || i.Operands.Count != 1 || i.RegionSite || KindOf(i.Callee) is not long kind) continue;
                if (i.Operands[0] is ImmOperand constant)
                {
                    if (ClassOf(constant.Value, w) is not (long slot, long size)) continue;
                    b.Instrs[k] = new Instr
                    {
                        Op = Opcode.Call, Dest = i.Dest, Callee = Sized,
                        Operands = { i.Operands[0], new ImmOperand(kind, IrTypes.Word), new ImmOperand(slot, IrTypes.Word), new ImmOperand(size, IrTypes.Word) },
                        Line = i.Line,
                    };
                    sized = true;
                    continue;
                }
                b.Instrs[k] = new Instr
                {
                    Op = Opcode.Call, Dest = i.Dest, Callee = Fast,
                    Operands = { i.Operands[0], new ImmOperand(kind, IrTypes.Word) },
                    Line = i.Line,
                };
            }
        }
        return sized;
    }

    /// <summary>
    /// `function`'s allocations made AllocFastSized or AllocFast, and
    /// AllocFastSized's body put in place by an inliner given that body
    /// alone -- nothing else `function` calls changes -- and `cleanup` run
    /// over what came in. `body` finds AllocFastSized's definition; none --
    /// a runtime without these -- and the calls are left as they were.
    /// </summary>
    public static void Run(Function function, Module local, Func<string, Function?> body, Pipeline cleanup)
    {
        if (function.Name is Sized or Fast or Missed || Skipped) return;
        if (!function.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.Call && i.Operands.Count == 1 && KindOf(i.Callee) is not null))) return;
        if (body(Sized) is not Function fast || fast.Name != Sized || ReferenceEquals(fast, function)) return;
        if (!Retarget(function)) return;
        Module tail = new(local.Name) { Entry = function.Name, PreserveExports = true, NeedsHeap = local.NeedsHeap };
        tail.Functions.Add(function);
        tail.Functions.Add(fast);
        new Inline { SmallBody = 200, GrowthLimit = 1 << 20, ConstantBranchBody = 200, FreshOwnerBody = 0, PlacesAllocations = true }.Run(tail);
        cleanup.Run(tail);
    }

    /// <summary>
    /// --skip-passes InlineAllocators: every allocation left the call it was,
    /// at the link as in a unit's compile -- the A/B of this against the two
    /// calls, and of the code it grows.
    /// </summary>
    public static bool Skipped => Switches.SkipPasses is { Length: > 0 } skip
        && skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(nameof(InlineAllocators));

    /// <summary>The cleanup run over what came in: the folds that make a constant size's test and rounding nothing.</summary>
    public static Pipeline Cleanup()
    {
        Pipeline cleanup = new() { Rounds = 2 };
        cleanup.Passes.Add(new ConstantAndCopyPropagation());
        cleanup.Passes.Add(new ConstantFold());
        cleanup.Passes.Add(new Peephole());
        cleanup.Passes.Add(new BranchSimplify());
        cleanup.Passes.Add(new DeadCodeElimination());
        return cleanup;
    }
}

/// <summary>
/// AllocatorFastPaths over a whole module (Pipeline.Default's late passes,
/// after the region pass and before the last inliner): AllocFastSized's body
/// is the module's own, kept until here by the inliners that pin the helpers
/// the late passes call (Inline.KeepFreeHelper).
/// </summary>
public sealed class InlineAllocators : IModulePass
{
    public string Name => "inline-allocators";

    public void Run(Module m)
    {
        // NOT IN THE LINK'S RUN OF THE LATE PASSES (Module.AtLink): the sites
        // the link put in regions are made so after them, one function at a
        // time, by the allocator calls they still are (RegionPointsTo.
        // MakeSitesInRegion), and the allocation is put in place only then
        // (UnitBackend, AllocatorFastPaths.Run). Put in place here first, the
        // calls were gone, and every object the link had given a region was
        // made on the heap: nothing given back at a region's end.
        if (AllocatorFastPaths.Skipped || m.AtLink) return;
        Function? fast = m.Functions.FirstOrDefault(f => f.Name == AllocatorFastPaths.Sized);
        Function? region = m.Functions.FirstOrDefault(f => f.Name == AllocatorFastPaths.RegionSized);
        Pipeline cleanup = AllocatorFastPaths.Cleanup();
        // AllocFast too, which sites of a size not known are made to call.
        if (fast is not null && m.Functions.Any(f => f.Name == AllocatorFastPaths.Fast))
        {
            foreach (Function f in m.Functions.ToArray())
            {
                if (ReferenceEquals(f, fast)) continue;
                AllocatorFastPaths.Run(f, m, name => name == AllocatorFastPaths.Sized ? fast : null, cleanup);
            }
        }
        // And a region's allocation of a size known, where the region pass
        // made any (RegionPointsTo): given its block's size and put in place.
        if (region is not null)
        {
            foreach (Function f in m.Functions.ToArray())
            {
                if (ReferenceEquals(f, region) || !AllocatorFastPaths.RetargetRegions(f)) continue;
                Module tail = new(m.Name) { Entry = f.Name, PreserveExports = true, NeedsHeap = m.NeedsHeap };
                tail.Functions.Add(f);
                tail.Functions.Add(region);
                new Inline { SmallBody = 200, GrowthLimit = 1 << 20, ConstantBranchBody = 200, FreshOwnerBody = 0, PlacesAllocations = true }.Run(tail);
                cleanup.Run(tail);
            }
        }
    }
}
