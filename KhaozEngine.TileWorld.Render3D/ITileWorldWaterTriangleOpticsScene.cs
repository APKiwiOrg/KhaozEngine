using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Optional scene capability required by <see cref="TileWaterDepthMode.AuthoredBed"/>. TileWorld
/// publishes certified triangle fields built from the actual uploaded ground meshes. Handles share the base
/// interface's release path.</summary>
public interface ITileWorldWaterTriangleOpticsScene : ITileWorldWaterOpticsScene
{
    /// <summary>Upload one immutable certified triangle field before frame recording.</summary>
    WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field);

    /// <summary>Non-throwing displacement preflight for the effective look, resolved once per view flush.
    /// Implementations must refuse an uncertified displacement, such as FFT water, with
    /// <see cref="WaterOpticsSurfaceFailure.UncertifiedDisplacement"/> rather than return a padding. A refusal
    /// withdraws authored-bed water while terrain still publishes. <paramref name="reason"/> is meaningful only
    /// when the method returns false.</summary>
    bool TryGetWaterOpticsPadding(WaterLook? look, out float padding, out WaterOpticsSurfaceFailure reason);
}
