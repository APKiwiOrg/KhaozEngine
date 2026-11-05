using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class ContactTerrainTopology
{
    /// <summary>Enumerates the source's own bounded lattice faces. No normal or height invents an address.</summary>
    internal static IEnumerable<MovementSurfaceFeature> Faces(IMovementAnalyticSupportSource source,
        int minX, int minZ, int maxX, int maxZ, float tileSize = 1)
    {
        for (int z = minZ; z < maxZ; z++)
            for (int x = minX; x < maxX; x++)
            {
                Vector2 centre = new((x + 0.5f) * tileSize, -(z + 0.5f) * tileSize);
                if (source.Describe(centre, out var selected) != MovementAvailability.Known) continue;
                for (int part = 0; part < TileTriangulation.MaxTriangles; part++)
                    if (source.GetFeature(selected.Key with { Part = part }, out var face) == MovementAvailability.Known)
                        yield return face;
            }
    }
}
