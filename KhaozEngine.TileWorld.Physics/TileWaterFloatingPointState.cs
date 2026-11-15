using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

// Preserve the water test seam while both numeric caches share one native controls reader.
internal static class TileWaterFloatingPointState
{
    internal static bool TryRead(out ulong token) => PhysicsFloatingPointControls.TryRead(out token);
}
