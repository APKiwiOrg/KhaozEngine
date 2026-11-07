using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Counted samples of feet XZ and reach distance. The approach window keeps its own start, so clearing
/// it leaves the stall history intact. Storage is allocated once.</summary>
internal sealed class RangeProgressRing
{
    private readonly (Vector2 FeetXz, float Distance, double Travel)[] _samples;
    private readonly int _stallWindowTicks;
    private double _stallTravel;
    private int _movingIntervals;
    private int _next;
    private int _count;
    private int _approachCount;

    internal RangeProgressRing(int stallWindowTicks, int approachWindowTicks = 0)
    {
        _stallWindowTicks = stallWindowTicks;
        _samples = new (Vector2, float, double)[Math.Max(stallWindowTicks, approachWindowTicks) + 1];
    }

    internal void Record(Vector2 feetXz, float distance)
    {
        double travel = 0d;
        if (_count > 0)
        {
            Vector2 previous = Back(0).FeetXz;
            double dx = (double)feetXz.X - previous.X, dz = (double)feetXz.Y - previous.Y;
            travel = Math.Sqrt(dx * dx + dz * dz);
        }
        if (_count > _stallWindowTicks)
        {
            double expired = Back(_stallWindowTicks - 1).Travel;
            _stallTravel -= expired;
            if (expired > 0d) _movingIntervals--;
        }
        if (travel > 0d) _movingIntervals++;
        // A fully stationary window is exactly zero, without residue from retired diagonal intervals.
        _stallTravel = _movingIntervals == 0 ? 0d : Math.Max(0d, _stallTravel + travel);
        _samples[_next] = (feetXz, distance, travel);
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
        if (_approachCount < _samples.Length) _approachCount++;
    }

    internal void ClearAll()
    {
        _stallTravel = 0d;
        _movingIntervals = 0;
        _count = 0;
        _approachCount = 0;
    }

    internal void ClearApproach() => _approachCount = 0;

    /// <summary>Accumulated horizontal travel over counted intervals is below the stall threshold.</summary>
    internal bool StallBreached(float travelMetres)
        => _count > _stallWindowTicks && _stallTravel < travelMetres;

    /// <summary>Reach distance fell by less than the gain threshold across the last window.</summary>
    internal bool ApproachBreached(float gainMetres, int windowTicks)
    {
        if (_approachCount <= windowTicks) return false;
        return (double)Back(windowTicks).Distance - Back(0).Distance < gainMetres;
    }

    private (Vector2 FeetXz, float Distance, double Travel) Back(int ticks)
        => _samples[(_next - 1 - ticks + _samples.Length) % _samples.Length];
}
