using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

public sealed partial class InteriorHeightField
{
    /// <summary>Conservatively test whether any part of a world box may be cut.</summary>
    public bool MayIntersect(Vector3 worldMin, Vector3 worldMax) => MayIntersect(worldMin, worldMax, default);

    /// <summary>Test the box against the predicate in the visible scene's render frame.</summary>
    public bool MayIntersect(Vector3 worldMin, Vector3 worldMax, InteriorCutFrame frame)
    {
        // Unknown bounds cannot prove a miss. Empty boxes cannot contain a cut point.
        if (!Finite(worldMin) || !Finite(worldMax)) return true;
        if (worldMin.X > worldMax.X || worldMin.Y > worldMax.Y || worldMin.Z > worldMax.Z) return false;
        var (window, heights) = frame.Uniforms(this);
        Vector3 min = frame.ToRender(worldMin), max = frame.ToRender(worldMax);
        if (!Finite(min) || !Finite(max)) return true;
        float x0 = (min.X - window.X) * window.Z, x1 = (max.X - window.X) * window.Z;
        float z0 = (min.Z - window.Y) * window.Z, z1 = (max.Z - window.Y) * window.Z;
        // One cell on every side covers represented lattice edges and reciprocal addressing.
        if (x1 < -1f || z1 < -1f || x0 >= window.W + 1f || z0 >= window.W + 1f) return false;
        int last = Window.Cells - 1;
        int firstX = (int)Math.Clamp(MathF.Floor(x0) - 1f, 0f, last);
        int lastX = (int)Math.Clamp(MathF.Floor(x1) + 1f, 0f, last);
        int firstZ = (int)Math.Clamp(MathF.Floor(z0) - 1f, 0f, last);
        int lastZ = (int)Math.Clamp(MathF.Floor(z1) + 1f, 0f, last);
        // These are the literal shader operations, not decoded world heights. Float32 subtraction and
        // multiplication are monotone, so their endpoint values bound every point in the box. Widen one
        // represented code value outwards as well, preserving equality at rounded endpoints.
        float low = MathF.BitDecrement((min.Y - heights.X) * heights.Y);
        float high = MathF.BitIncrement((max.Y - heights.X) * heights.Y);
        return _overlap.MayIntersect(firstX, lastX, firstZ, lastZ, low, high);
    }
}
