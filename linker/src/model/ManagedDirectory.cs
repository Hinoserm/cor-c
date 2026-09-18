namespace Corsac.Lang.Ir;

/// <summary>One image's directories of object-local frame and stack-map tables.</summary>
public static class ManagedDirectory
{
    public const string Symbol = "__corsac_units";
    public const string FrameSymbol = "__corsac_frames";
    public const string StackMapSymbol = "__corsac_stackmaps";
    public const uint Magic = 0x52444d43; // CMDR
    public const int HeaderBytes = 16;
    public const int RecordBytes = 16; // frame address/bytes, stack-map address/bytes
}
