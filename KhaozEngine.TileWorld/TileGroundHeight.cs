using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>Point sampling of the full-detail surface described by <see cref="TileGroundTriangles"/>.</summary>
public static class TileGroundHeight
{
    /// <summary>Samples the drawn ground at a world position, in metres. Returns false with a zero height when
    /// the plane is outside the document, the position is outside the lattice, or the tile has no drawn ground.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static bool TrySample(TileWorldDocument document, float worldX, float worldZ, int plane, out float height)
    {
        ArgumentNullException.ThrowIfNull(document);
        height = 0f;
        if ((uint)plane >= (uint)document.PlaneCount) return false;

        float tileX = TileWorldSpace.TileX(worldX, document.TileSize);
        float tileZ = TileWorldSpace.TileZ(worldZ, document.TileSize);
        if (!(tileX >= int.MinValue && tileX < (float)int.MaxValue &&
              tileZ >= int.MinValue && tileZ < (float)int.MaxValue)) return false;

        int x = (int)MathF.Floor(tileX), z = (int)MathF.Floor(tileZ);
        Span<DrawnGroundTriangle> triangles = stackalloc DrawnGroundTriangle[TileTriangulation.MaxTriangles];
        int count = DrawnGroundTriangle.Describe(document, x, z, plane, triangles);
        int selected = DrawnGroundTriangle.Select(triangles[..count], new Vector2(tileX - x, tileZ - z),
            out Vector3 weights);
        if (selected < 0) return false;
        height = triangles[selected].Height(weights);
        return true;
    }
}
