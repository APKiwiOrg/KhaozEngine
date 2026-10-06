using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

// Test-only finite cell arrangement. Every box face is a partition plane, so membership is
// constant inside each open cell. This is exact classification of boxes, not sampled terrain.
internal static class AnalyticBoxComplement
{
    const int MaximumCells = 512;

    public static bool TryCreate(AnalyticBox scope, List<AnalyticBox> water, out List<AnalyticBox> dry)
    {
        dry = [];
        float[] xs = Cuts(scope.Min.X, scope.Max.X, water, 0);
        float[] ys = Cuts(scope.Min.Y, scope.Max.Y, water, 1);
        float[] zs = Cuts(scope.Min.Z, scope.Max.Z, water, 2);
        int cells = (xs.Length - 1) * (ys.Length - 1) * (zs.Length - 1);
        if (cells > MaximumCells) return false;
        for (int x = 0; x + 1 < xs.Length; x++)
            for (int y = 0; y + 1 < ys.Length; y++)
                for (int z = 0; z + 1 < zs.Length; z++)
                {
                    var cell = new AnalyticBox(new Vector3(xs[x], ys[y], zs[z]),
                        new Vector3(xs[x + 1], ys[y + 1], zs[z + 1]));
                    bool wet = false;
                    // Check the whole cell instead of relying on a rounded midpoint near a plane.
                    foreach (AnalyticBox box in water)
                        if (cell.Min.X >= box.Min.X && cell.Max.X <= box.Max.X &&
                            cell.Min.Y >= box.Min.Y && cell.Max.Y <= box.Max.Y &&
                            cell.Min.Z >= box.Min.Z && cell.Max.Z <= box.Max.Z)
                        { wet = true; break; }
                    if (!wet) dry.Add(cell);
                }
        return true;
    }

    static float[] Cuts(float min, float max, List<AnalyticBox> boxes, int axis)
    {
        var values = new SortedSet<float> { min, max };
        foreach (AnalyticBox box in boxes)
        {
            float low = Axis(box.Min, axis), high = Axis(box.Max, axis);
            if (low > min && low < max) values.Add(low);
            if (high > min && high < max) values.Add(high);
        }
        var result = new float[values.Count];
        values.CopyTo(result);
        return result;
    }

    static float Axis(Vector3 value, int axis) => axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
}
