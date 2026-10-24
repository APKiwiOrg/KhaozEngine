using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Optional scene capability required by <see cref="TileWaterDepthMode.AuthoredBed"/>. TileWorld
/// publishes certified triangle fields built from the actual uploaded ground meshes. Handles share the base
/// interface's release path.</summary>
public interface ITileWorldWaterTriangleOpticsScene : ITileWorldWaterOpticsScene
{
    /// <summary>Upload one immutable certified triangle field before frame recording.</summary>
    WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field);
}
