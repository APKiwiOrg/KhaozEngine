using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>The one seam between tile space (x east, z NORTH, y up) and the engine's render space, which is
/// right handed with y up, where a camera facing +z has +x on its LEFT. Mapping the document's north straight
/// onto +world z therefore renders the whole world mirrored against a compass, and a north-up minimap would
/// contradict what the player sees. So world z is MINUS tile z: north becomes -z, which is also the default
/// forward of a right-handed camera, and (east, north, up) = (+x, -z, +y) stays a right-handed triple, so one
/// top-down view can have north up and east right at the same time. Every conversion between the two spaces goes
/// through here, so the sign lives in exactly one place.</summary>
public static class TileWorldSpace
{
    /// <summary>World x in metres of a tile x, which the two spaces agree on.</summary>
    public static float WorldX(float tileX, float tileSize) => tileX * tileSize;

    /// <summary>World z in metres of a tile z, negated so north (+tile z) is -z in the render space.</summary>
    public static float WorldZ(float tileZ, float tileSize) => -tileZ * tileSize;

    /// <summary>Tile x of a world x in metres, the inverse of <see cref="WorldX"/>.</summary>
    public static float TileX(float worldX, float tileSize) => worldX / tileSize;

    /// <summary>Tile z of a world z in metres, the inverse of <see cref="WorldZ"/>.</summary>
    public static float TileZ(float worldZ, float tileSize) => -worldZ / tileSize;

    /// <summary>A tile point at a height in metres as a world position.</summary>
    public static Vector3 ToWorld(float tileX, float heightMetres, float tileZ, float tileSize) =>
        new(WorldX(tileX, tileSize), heightMetres, WorldZ(tileZ, tileSize));
}
