using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Optional scene capability for preparing and releasing immutable optical fields.</summary>
public interface ITileWorldWaterOpticsScene
{
    /// <summary>Upload one immutable field before frame recording.</summary>
    WaterOpticsHandle LoadWaterOpticsField(WaterOpticsField field);
    /// <summary>Release a scene-owned field after its last prepared owner leaves.</summary>
    void UnloadWaterOpticsField(WaterOpticsHandle handle);
    /// <summary>The certified horizontal displacement reach of the effective look.</summary>
    float GetWaterOpticsPadding(WaterLook? look = null);
}
