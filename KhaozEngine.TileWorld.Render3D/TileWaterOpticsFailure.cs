namespace KhaozEngine.TileWorld.Render3D;

/// <summary>Why a complete authored-bed optical field could not be built.</summary>
public enum TileWaterOpticsFailureKind
{
    /// <summary>A required corner has no available owner, or its earlier-priority owner is unloaded.</summary>
    MissingHeightData,
    /// <summary>The authored lattice or world mapping exceeds the supported field bounds.</summary>
    FieldTooLarge,
}

/// <summary>A refused CPU field request, with the unavailable region when height data is missing.</summary>
public readonly record struct TileWaterOpticsFailure(RegionCoord Region, int Plane, float ReferenceSurfaceY,
    TileWaterOpticsFailureKind Kind, RegionCoord? MissingRegion = null);
