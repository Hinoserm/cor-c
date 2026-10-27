namespace Corsac.Lang.Lto;

/// <summary>
/// The labels of the runtime's hot helpers, as the compiler and the link name
/// them. Their parameters are nuint -- one machine word, one push on i386
/// where a long was two -- so each label is built from the mangling of nuint
/// (Lowering.Mangle of Type.NUInt, checked against this when lowering starts).
/// Mangling names the type, not its width, so the one spelling is right for
/// every target.
/// </summary>
public static class RuntimeAbi
{
    /// <summary>How a parameter of type nuint is mangled: the machine word compiled code passes.</summary>
    public const string Word = "V$NUInt";

    /// <summary>How a parameter of type nuint, an address, is mangled.</summary>
    public const string Address = "V$NUInt";

    /// <summary>
    /// The calls that end the program, never coming back by return or by
    /// unwind: Runtime.Exit and Unhandled, the platform's and the system's
    /// exits, Environment.Exit. A block that ends in a trap dies at one of
    /// them (Escape.DyingFrom); any other call it ends with -- a throw --
    /// comes back to a catch.
    /// </summary>
    public static bool EndsProcess(string? callee) => callee is "m_Runtime_Exit_1_V$I32" or "m_Runtime_Unhandled_1_V$Any"
        or "m_Platform_Exit_1_V$I32" or "m_Os_Exit_1_V$I32" or "m_Environment_Exit_1_V$I32";

    public const string Alloc = "m_Runtime_Alloc_1_" + Word;
    public const string AllocLeaf = "m_Runtime_AllocLeaf_1_" + Word;
    public const string AllocObject = "m_Runtime_AllocObject_1_" + Word;
    /// <summary>
    /// THE ALLOCATION IN PLACE (Opt.AllocatorFastPaths): after every pass
    /// that knows a `new` by its call to the three above, one of a size a
    /// class holds becomes AllocFastSized, its class's slot and size given,
    /// put in place; one of a size not known, a call to AllocFast. Both take
    /// AllocMissed, the call the site was, as their long way.
    /// </summary>
    public const string AllocFastSized = "m_Runtime_AllocFastSized_4_" + Word + "_" + Word + "_" + Word + "_" + Word;
    public const string AllocFast = "m_Runtime_AllocFast_2_" + Word + "_" + Word;
    public const string AllocMissed = "m_Runtime_AllocMissed_2_" + Word + "_" + Word;
    /// <summary>The safepoint a loop's back edge calls (BackEdgePolls), written by the backend after the IR is archived.</summary>
    public const string Poll = "m_Runtime_Poll_0";
    public const string AllocManual = "m_Runtime_AllocManual_1_" + Word;
    public const string AllocManualObject = "m_Runtime_AllocManualObject_1_" + Word;
    /// <summary>The manual heap's free and its liveness test (Escape's ManualFreer, ManualLive).</summary>
    public const string FreeManual = "m_Runtime_FreeManual_1_" + Address;
    public const string ManualObject = "m_Runtime_ManualObject_1_" + Address;
    public const string Free = "m_Runtime_Free_1_" + Word;
    public const string FreeReplaced = "m_Runtime_FreeReplaced_2_" + Word + "_" + Word;
    public const string FreeOwnedReplaced = "m_Runtime_FreeOwnedReplaced_2_" + Word + "_" + Word;
    public const string FreeField = "m_Runtime_FreeField_2_" + Word + "_" + Word;
    public const string KeepField = "m_Runtime_KeepField_2_" + Word + "_" + Word;
    public const string WriteBarrier = "m_Runtime_WriteBarrier_2_" + Word + "_" + Word;
    public const string WriteBarrierValues = "m_Runtime_WriteBarrierValues_2_" + Word + "_" + Word;
    /// <summary>
    /// The store sequences' barriers (RefStore, RefExchange, RefCompareExchange
    /// while Marking is set): the report, the store and its card in one call,
    /// inside the thread's InAlloc, which no handshake is sent into.
    /// </summary>
    public const string WriteBarrierStore = "m_Runtime_WriteBarrierStore_2_" + Word + "_" + Word;
    public const string WriteBarrierExchange = "m_Runtime_WriteBarrierExchange_2_" + Word + "_" + Word;
    public const string WriteBarrierCompareExchange = "m_Runtime_WriteBarrierCompareExchange_3_" + Word + "_" + Word + "_" + Word;
    public const string CardMark = "m_Runtime_CardMark_1_" + Word;
    public const string CardMarkObject = "m_Runtime_CardMarkObject_1_" + Word;
    /// <summary>What a coroutine's machine holds, reported before its frame is saved over it (Runtime.ShadeObject).</summary>
    public const string ShadeObject = "m_Runtime_ShadeObject_1_" + Word;
    /// <summary>An array grown where it is, at its region's top: keeps no pointer (Runtime.GrowInPlace).</summary>
    public const string GrowInPlace = "m_Runtime_GrowInPlace_2_" + Word + "_V$I32";
    /// <summary>
    /// THE STORE SEQUENCES (images whose threads stop anywhere: CORSAC's
    /// kernels, i386 and long mode): a reference store with its Marking test,
    /// snapshot barrier and card mark (RefStore), and one with its card mark
    /// alone (CardStore), each a stub of the image that defines the runtime,
    /// laid out together between SequencesStart and SequencesEnd -- the range
    /// its kernel never stops a thread inside (X86Backend, X64Backend,
    /// CardMarks.FuseStores).
    /// RefStore in a unit's runtime helpers says the unit's stores are made
    /// so.
    /// </summary>
    public const string RefStore = "__corsac_refstore";

    /// <summary>
    /// The hosted runtime's copy and fill around the caches (Runtime.StreamCopy,
    /// StreamFill), which the i386 backend calls for a block of 256 KB or more
    /// whose length is known only as it runs: named by no unit's IR, kept as
    /// the store sequences' barriers are (Module.RuntimeHelpers, the link's roots).
    /// </summary>
    public const string StreamCopy = "m_Runtime_StreamCopy_3_" + Word + "_" + Word + "_" + Word;
    public const string StreamFill = "m_Runtime_StreamFill_3_" + Word + "_V$I32_" + Word;
    public const string CardStore = "__corsac_cardstore";
    /// <summary>Interlocked's reference exchanges as sequences (Sys.ExchangeReference, CompareExchangeReference).</summary>
    public const string RefExchange = "__corsac_refxchg";
    public const string RefCompareExchange = "__corsac_refcas";
    public const string SequencesStart = "__corsac_store_sequences";
    public const string SequencesEnd = "__corsac_store_sequences_end";
    /// <summary>
    /// Regions (RegionPointsTo, RegionSolver): opened on a boundary's entry,
    /// given back on its return, allocated in. RegionEnter and RegionLoop
    /// take, last, the bytes the link proved the region holds (0: not known).
    /// </summary>
    public const string RegionEnter = "m_Runtime_RegionEnter_2_" + Word + "_" + Word;
    public const string RegionLeave = "m_Runtime_RegionLeave_1_" + Word;
    /// <summary>A loop's region at the top of every lap (RegionPointsTo, "loops").</summary>
    public const string RegionLoop = "m_Runtime_RegionLoop_3_" + Word + "_" + Word + "_" + Word;
    /// <summary>A catch closes the regions it caught out of (every landing pad of a program with regions).</summary>
    public const string RegionCatch = "m_Runtime_RegionCatch_1_" + Word;
    public const string AllocRegion = "m_Runtime_AllocRegion_3_" + Word + "_" + Word + "_" + Word;
    /// <summary>An AllocRegion of a size the compiler knows, its block's size given, put in place (Opt.AllocatorFastPaths).</summary>
    public const string AllocRegionSized = "m_Runtime_AllocRegionSized_4_" + Word + "_" + Word + "_" + Word + "_" + Word;
    /// <summary>
    /// What makes a thread's block its own (Platform.SetThreadBlock): a
    /// region is opened in the block, so nothing that runs before this does
    /// -- it, and every function that calls it -- may open one.
    /// </summary>
    public const string SetThreadBlock = "m_Platform_SetThreadBlock_1_" + Address;

    /// <summary>
    /// What hands a thread's arena back as the thread ends (Gc.ReleaseRegion):
    /// nothing that calls it is a boundary, its region's leave coming after
    /// the arena it was opened in is gone (RegionSolver.BeforeThreadBlock).
    /// </summary>
    public const string ReleaseRegion = "m_Gc_ReleaseRegion_1_" + Address;
    /// <summary>Made in the region of another object, or on the heap beside one there is none of (RegionPointsTo.Near).</summary>
    public const string AllocNear = "m_Runtime_AllocNear_4_" + Word + "_" + Word + "_" + Word + "_" + Word;

    /// <summary>
    /// Every label above: the routines the late passes call by name, and so
    /// may add calls to (UnitBackend.ReachableInUnit keeps them). Written out,
    /// not gathered by reflection, which the compiler does not have for its
    /// own build; a label added above goes here too.
    /// </summary>
    public static readonly string[] Helpers =
    {
        Alloc, AllocLeaf, AllocObject, AllocFastSized, AllocFast, AllocMissed, Poll, AllocManual, AllocManualObject,
        FreeManual, ManualObject, Free, FreeReplaced, FreeOwnedReplaced, FreeField, KeepField,
        WriteBarrier, WriteBarrierValues, WriteBarrierStore, WriteBarrierExchange, WriteBarrierCompareExchange,
        CardMark, CardMarkObject, ShadeObject, GrowInPlace,
        RefStore, CardStore, RefExchange, RefCompareExchange, SequencesStart, SequencesEnd,
        RegionEnter, RegionLeave, RegionLoop, RegionCatch, AllocRegion, AllocRegionSized,
        SetThreadBlock, ReleaseRegion, AllocNear,
    };
}
