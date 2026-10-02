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
    /// <summary>Regions (RegionPointsTo, RegionSolver): opened on a boundary's entry, given back on its return, allocated in.</summary>
    public const string RegionEnter = "m_Runtime_RegionEnter_1_" + Word;
    public const string RegionLeave = "m_Runtime_RegionLeave_1_" + Word;
    /// <summary>A loop's region at the top of every lap (RegionPointsTo, "loops").</summary>
    public const string RegionLoop = "m_Runtime_RegionLoop_2_" + Word + "_" + Word;
    /// <summary>A catch closes the regions it caught out of (every landing pad of a program with regions).</summary>
    public const string RegionCatch = "m_Runtime_RegionCatch_1_" + Word;
    public const string AllocRegion = "m_Runtime_AllocRegion_3_" + Word + "_" + Word + "_" + Word;
}
