namespace Corsac.Lang.Lto;

/// <summary>
/// The labels of the runtime's hot helpers, as the compiler and the link name
/// them. Their parameters are nint -- one machine word, one push on i386
/// where a long was two -- so each label is built from the mangling of nint
/// (Lowering.Mangle of Type.NInt, checked against this when lowering starts).
/// Mangling names the type, not its width, so the one spelling is right for
/// every target.
/// </summary>
public static class RuntimeAbi
{
    /// <summary>How a parameter of type nint is mangled.</summary>
    public const string Word = "V$NInt";

    public const string Alloc = "m_Runtime_Alloc_1_" + Word;
    public const string AllocLeaf = "m_Runtime_AllocLeaf_1_" + Word;
    public const string AllocObject = "m_Runtime_AllocObject_1_" + Word;
    /// <summary>
    /// THE ALLOCATION IN PLACE (Opt.AllocatorFastPaths): after every pass
    /// that knows a `new` by its call to the three above, each becomes a call
    /// to AllocFast, with the kind, and AllocFast's body is put where it was;
    /// AllocMissed is its long way, a call at each site.
    /// </summary>
    public const string AllocFast = "m_Runtime_AllocFast_2_" + Word + "_" + Word;
    public const string AllocMissed = "m_Runtime_AllocMissed_2_" + Word + "_" + Word;
    public const string AllocManual = "m_Runtime_AllocManual_1_" + Word;
    public const string AllocManualObject = "m_Runtime_AllocManualObject_1_" + Word;
    public const string Free = "m_Runtime_Free_1_" + Word;
    public const string FreeReplaced = "m_Runtime_FreeReplaced_2_" + Word + "_" + Word;
    public const string FreeOwnedReplaced = "m_Runtime_FreeOwnedReplaced_2_" + Word + "_" + Word;
    public const string FreeField = "m_Runtime_FreeField_2_" + Word + "_" + Word;
    public const string KeepField = "m_Runtime_KeepField_2_" + Word + "_" + Word;
    public const string WriteBarrier = "m_Runtime_WriteBarrier_2_" + Word + "_" + Word;
    public const string WriteBarrierValues = "m_Runtime_WriteBarrierValues_2_" + Word + "_" + Word;
    public const string CardMark = "m_Runtime_CardMark_1_" + Word;
    public const string CardMarkObject = "m_Runtime_CardMarkObject_1_" + Word;
    /// <summary>An array grown where it is, at its region's top: keeps no pointer (Runtime.GrowInPlace).</summary>
    public const string GrowInPlace = "m_Runtime_GrowInPlace_2_" + Word + "_" + Word;
    /// <summary>
    /// THE STORE SEQUENCES (i386 images whose threads stop anywhere: CORSAC's
    /// ring-1 kernels): a reference store with its Marking test, snapshot
    /// barrier and card mark (RefStore), and one with its card mark alone
    /// (CardStore), each a stub of the image that defines the runtime, laid
    /// out together between SequencesStart and SequencesEnd -- the range its
    /// kernel never stops a thread inside (X86Backend, CardMarks.FuseStores).
    /// RefStore in a unit's runtime helpers says the unit's stores are made
    /// so.
    /// </summary>
    public const string RefStore = "__corsac_refstore";
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
    /// <summary>
    /// What makes a thread's block its own (Platform.SetThreadBlock): a
    /// region is opened in the block, so nothing that runs before this does
    /// -- it, and every function that calls it -- may open one.
    /// </summary>
    public const string SetThreadBlock = "m_Platform_SetThreadBlock_1_V$I64";

    /// <summary>
    /// What hands a thread's arena back as the thread ends (Gc.ReleaseRegion):
    /// nothing that calls it is a boundary, its region's leave coming after
    /// the arena it was opened in is gone (RegionSolver.BeforeThreadBlock).
    /// </summary>
    public const string ReleaseRegion = "m_Gc_ReleaseRegion_1_V$I64";
    /// <summary>Made in the region of another object, or on the heap beside one there is none of (RegionPointsTo.Near).</summary>
    public const string AllocNear = "m_Runtime_AllocNear_4_" + Word + "_" + Word + "_" + Word + "_" + Word;
}
