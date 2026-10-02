namespace Corsac.Lang.Lto;

/// <summary>
/// Another unit's body, brought in for the link's inliner. With it, what the
/// link's regions chose of it in its own unit (RegionSolver): the ordinals of
/// its allocator calls made in the innermost region, numbered over this same
/// IR (RegionPointsTo.MarkSites), and whether it opens a region of its own.
/// </summary>
public sealed record IrImport(string Symbol, byte[] Body, long DecodeBytes = 0, int[]? RegionSites = null, bool RegionBoundary = false);
