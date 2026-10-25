namespace KhaozEngine.TileWorld;

/// <summary>The source of depth used to shade TileWorld water.</summary>
public enum TileWaterDepthMode
{
    /// <summary>Use the opaque scene depth, preserving existing adapters.</summary>
    SceneDepth,
    /// <summary>Prepare immutable per-body fields from available authored terrain.</summary>
    AuthoredBed,
}
