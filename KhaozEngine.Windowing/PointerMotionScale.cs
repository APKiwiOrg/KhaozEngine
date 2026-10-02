using System.Numerics;

namespace KhaozEngine.Windowing;

internal static class PointerMotionScale
{
    internal static Vector2 Normalize(Vector2 scale)
        => new(Axis(scale.X), Axis(scale.Y));

    static float Axis(float scale) => float.IsFinite(scale) && scale > 0 ? scale : 1;
}
