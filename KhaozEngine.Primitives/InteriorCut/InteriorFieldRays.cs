using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

public sealed partial class InteriorHeightField
{
    /// <summary>Walk the ray's XZ cells and solve each linear height interval. Outside the window is uncut.</summary>
    public bool FirstUncut(Vector3 origin, Vector3 direction, float tStart, float tEnd, out float t)
    {
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(tStart) || !float.IsFinite(tEnd) || tEnd < tStart)
            throw new ArgumentException("Ray coordinates and ordered interval endpoints must be finite.");
        t = tStart;
        while (true)
        {
            Vector3 point = origin + direction * t;
            if (!TryCell(point, out int x, out int z) || !TryGetSpan(x, z, out float floor, out float top) || point.Y < floor || point.Y >= top)
                return true;
            if (t == tEnd) return false;
            // A 2D DDA: advance to the earliest cell edge or height exit, including coincident grid corners.
            float tx = FirstCrossing(origin.X, direction.X, Window.Min.X, Window.CellSize, direction.X > 0f ? x + 1 : x, t, tEnd);
            float tz = FirstCrossing(origin.Z, direction.Z, Window.Min.Y, Window.CellSize, direction.Z > 0f ? z + 1 : z, t, tEnd);
            float ty = FirstCrossing(origin.Y, direction.Y, 0f, 1f, direction.Y > 0f ? top : floor, t, tEnd);
            float next = Math.Min(tx, Math.Min(tz, ty));
            if (float.IsPositiveInfinity(next)) return false;
            t = next;
        }
    }

    static float FirstCrossing(float origin, float direction, float offset, float scale, float boundary, float start, float end)
    {
        if (direction == 0f || !Crossed(end)) return float.PositiveInfinity;
        double exact = ((double)offset + (double)scale * boundary - origin) / direction;
        float candidate = Math.Clamp((float)exact, start, end);
        if (Crossed(candidate))
        {
            float previous = MathF.BitDecrement(candidate);
            if (previous <= start || !Crossed(previous)) return candidate;
        }
        else
        {
            float next = MathF.BitIncrement(candidate);
            if (next <= end && Crossed(next)) return next;
        }
        // Float position arithmetic can keep several adjacent t values on an inclusive edge. Refine the
        // analytic crossing over ordered float bits rather than sampling or nudging through that plateau.
        long low = Ordered(start), high = Ordered(end);
        while (high - low > 1)
        {
            long middle = low + (high - low) / 2;
            if (Crossed(FromOrdered((int)middle))) high = middle;
            else low = middle;
        }
        return FromOrdered((int)high);

        bool Crossed(float time)
        {
            float value = (origin + direction * time - offset) / scale;
            return direction > 0f ? value >= boundary : value < boundary;
        }
    }

    static int Ordered(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? int.MinValue - bits : bits;
    }

    static float FromOrdered(int value) => BitConverter.Int32BitsToSingle(value < 0 ? int.MinValue - value : value);
}
