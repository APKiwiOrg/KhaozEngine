using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Counted samples of feet XZ and reach distance. The approach window keeps its own start, so clearing
/// it leaves the stall history intact. Storage is allocated once.</summary>
internal sealed class RangeProgressRing
{
    private readonly (Vector2 FeetXz, float Distance)[] _samples;
    private int _next;
    private int _count;
    private int _approachCount;

    internal RangeProgressRing(int capacity) => _samples = new (Vector2, float)[capacity];

    internal void Record(Vector2 feetXz, float distance)
    {
        _samples[_next] = (feetXz, distance);
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
        if (_approachCount < _samples.Length) _approachCount++;
    }

    internal void ClearAll()
    {
        _count = 0;
        _approachCount = 0;
    }

    internal void ClearApproach() => _approachCount = 0;

    /// <summary>Net horizontal displacement across the last window is below the travel threshold.</summary>
    internal bool StallBreached(float travelMetres, int windowTicks)
    {
        if (_count <= windowTicks) return false;
        Vector2 now = Back(0).FeetXz, then = Back(windowTicks).FeetXz;
        double dx = (double)now.X - then.X, dz = (double)now.Y - then.Y;
        return Math.Sqrt(dx * dx + dz * dz) < travelMetres;
    }

    /// <summary>Reach distance fell by less than the gain threshold across the last window.</summary>
    internal bool ApproachBreached(float gainMetres, int windowTicks)
    {
        if (_approachCount <= windowTicks) return false;
        return (double)Back(windowTicks).Distance - Back(0).Distance < gainMetres;
    }

    private (Vector2 FeetXz, float Distance) Back(int ticks)
        => _samples[(_next - 1 - ticks + _samples.Length) % _samples.Length];
}
