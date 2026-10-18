using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

internal static class InteriorRoofRasterizer
{
    /// <summary>Lower a row-major slice with world-space samples. Positive infinity means no sample.</summary>
    internal static void Lower(InteriorRoof roof, InteriorWindow window, int firstCellX, int firstCellZ,
        int width, int height, Span<float> lowestSamples)
    {
        ArgumentNullException.ThrowIfNull(roof.Samples);
        double originX = Math.Round((double)window.Min.X / window.CellSize);
        double originZ = Math.Round((double)window.Min.Y / window.CellSize);
        bool snappedX = (float)(originX * window.CellSize) == window.Min.X;
        bool snappedZ = (float)(originZ * window.CellSize) == window.Min.Y;
        foreach (Vector3 local in roof.Samples.Points)
        {
            Vector3 world = Vector3.Transform(local, roof.LocalToWorld);
            if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
                throw new ArgumentException("Roof transforms must produce finite sample positions.", nameof(roof));
            // Bin snapped windows on the global lattice first, so subtraction cannot move a boundary
            // sample to a different cell after re-centring at a non-binary pitch such as 0.24 m.
            double x = (snappedX ? Math.Floor(world.X / (double)window.CellSize) - originX :
                Math.Floor(((double)world.X - window.Min.X) / window.CellSize)) - firstCellX;
            double z = (snappedZ ? Math.Floor(world.Z / (double)window.CellSize) - originZ :
                Math.Floor(((double)world.Z - window.Min.Y) / window.CellSize)) - firstCellZ;
            if (x < 0d || x >= width || z < 0d || z >= height) continue;
            int index = (int)z * width + (int)x;
            lowestSamples[index] = Math.Min(lowestSamples[index], world.Y);
        }
    }
}
