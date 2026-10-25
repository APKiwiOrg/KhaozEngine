using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Optional scene capability required by <see cref="TileWaterDepthMode.AuthoredBed"/>. TileWorld
/// publishes certified triangle fields built from the actual uploaded ground meshes and releases each one after
/// its last prepared owner leaves.</summary>
public interface ITileWorldWaterTriangleOpticsScene
{
    /// <summary>Upload one immutable certified triangle field before frame recording.</summary>
    WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field);

    /// <summary>Release a scene-owned field after its last prepared owner leaves.</summary>
    void UnloadWaterOpticsField(WaterOpticsHandle handle);

    /// <summary>Non-throwing displacement preflight for the effective look, resolved once per view flush.
    /// Implementations must refuse an uncertified displacement, such as FFT water, rather than return a padding.
    /// A refusal withdraws authored-bed water while terrain still publishes, and each withdrawn body reports
    /// <paramref name="reason"/> mapped to its <see cref="TileWorld.Render3D.TileWaterOpticsFailureKind"/>.
    /// <paramref name="reason"/> is meaningful only when the method returns false.</summary>
    bool TryGetWaterOpticsPadding(WaterLook? look, out float padding, out WaterOpticsSurfaceFailure reason);
}
