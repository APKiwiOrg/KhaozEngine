using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

internal static class InteriorRoofRasterizer
{
    /// <summary>Lower a bounded row-major slice to exact clipped triangle minima. Infinity means no roof.</summary>
    internal static void Lower(InteriorRoof roof, InteriorWindow window, int firstCellX, int firstCellZ,
        int width, int height, Span<float> lowestSamples)
    {
        ArgumentNullException.ThrowIfNull(roof.Samples);
        ClearanceMesh mesh = roof.Samples.Mesh;
        for (int triangle = 0; triangle < mesh.Indices.Count; triangle += 3)
        {
            Vector3 a = World(mesh.Positions[mesh.Indices[triangle]]);
            Vector3 b = World(mesh.Positions[mesh.Indices[triangle + 1]]);
            Vector3 c = World(mesh.Positions[mesh.Indices[triangle + 2]]);
            for (int z = 0; z < height; z++)
                for (int x = 0; x < width; x++)
                {
                    Vector2 min = InteriorLattice.Corner(window, firstCellX + x, firstCellZ + z);
                    Vector2 max = InteriorLattice.Corner(window, firstCellX + x + 1, firstCellZ + z + 1);
                    float x0 = min.X, z0 = min.Y, x1 = max.X, z1 = max.Y;
                    if (Math.Max(a.X, Math.Max(b.X, c.X)) < x0 || Math.Min(a.X, Math.Min(b.X, c.X)) > x1 ||
                        Math.Max(a.Z, Math.Max(b.Z, c.Z)) < z0 || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > z1) continue;
                    float minimum = InteriorRoofTriangle.Minimum(a, b, c, x0, z0, x1, z1);
                    int index = z * width + x;
                    lowestSamples[index] = Math.Min(lowestSamples[index], minimum);
                }
        }

        Vector3 World(Vector3 local)
        {
            Vector3 world = Vector3.Transform(local, roof.LocalToWorld);
            if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
                throw new ArgumentException("Roof transforms must produce finite triangle positions.", nameof(roof));
            return world;
        }
    }

}
