#if WATER_PRISM_CENSUS
using System;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

// Research-only observation seam. The symbol also removes every call site from normal binaries.
internal static class TileWaterCensus
{
    [ThreadStatic] internal static Action<string>? Count;
    [ThreadStatic] internal static Action<TileWaterConvexShape, V, V, I, float, double, double, double, double>? Intersection;
    [ThreadStatic] internal static Action<TileWaterPrism, TileWaterConvexShape, int>? Selected;
    [ThreadStatic] internal static Action? IntersectionEnded;
    [ThreadStatic] internal static Action<TileWaterConvexShape.Range>? IntersectionResult;
    [ThreadStatic] internal static string? Phase;
    [ThreadStatic] internal static bool Projecting;

    internal static void Add(string name) => Count?.Invoke(name);
    internal static IntersectionScope Observe(TileWaterConvexShape shape, V start, V drive, I core, float radius,
        double enter, double exit, double heightError, double horizontalError)
    {
        Intersection?.Invoke(shape, start, drive, core, radius, enter, exit, heightError, horizontalError);
        return default;
    }
    internal readonly struct IntersectionScope : IDisposable
    {
        public void Dispose() => IntersectionEnded?.Invoke();
    }
}
#endif
