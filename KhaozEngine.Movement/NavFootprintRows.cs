using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Conservative X candidates for each Z row of the legacy footprint rectangle.</summary>
internal readonly struct NavFootprintRows
{
    private const double RoundoffFactor = 1d / (1L << 40);

    private readonly double _originX, _size;
    private readonly double _startX, _startZ, _dx, _dz;
    private readonly double _strip;

    internal NavFootprintRows(Vector3 from, Vector3 to, PhysicsNavBakeOptions options, double radius,
        int x0, int x1, int z0, int z1)
    {
        // Narrow and horizontal rectangles need no row projection. Keep the rectangle for unsafe index ranges.
        if (z1 <= z0 || (long)x1 - x0 < 4 || from.Z == to.Z || x0 < 0 || z0 < 0 ||
            x1 == int.MaxValue || z1 == int.MaxValue) return;
        double size = options.CellSize;
        double worldX = Math.Max(Math.Abs((double)options.MinX), Math.Abs((double)options.MaxX));
        double worldZ = Math.Max(Math.Abs((double)options.MinZ), Math.Abs((double)options.MaxZ));
        double gridExtent = (Math.Max(x1, z1) + 1d) * size;
        double scale = Math.Max(1d, Math.Max(Math.Max(worldX, worldZ), Math.Max(gridExtent, radius)));
        double pad = scale * RoundoffFactor;
        if (!(radius >= 0d && double.IsFinite(pad) && pad < size * 0.125d)) return;

        if (from.Z > to.Z) (from, to) = (to, from);
        _startX = from.X;
        _startZ = from.Z;
        _dx = (double)to.X - from.X;
        _dz = (double)to.Z - from.Z;
        if (!(_dz > 0d)) return;
        _originX = options.MinX;
        _size = size;
        _strip = radius + pad;
    }

    // Let B = 2 * scale bound the input magnitudes and grid products, and q = B * 2^-52.
    // Every old Touches hit has a segment point within radius + 32q on each axis of the rounded cell.
    // Clip gives a shared t in [0,1]. Multiplying each quotient back by its delta costs at most 4q,
    // even for small deltas. The numerator and delta roundings add less than 8q in world units.
    // PointRectangle has an endpoint witness. PointSegment has its computed clamped t as a witness,
    // so only the gap arithmetic matters. Its subtractions and product cost less than 24q. Nonnegative
    // squared gap terms cannot exceed the rounded radius square on a hit, bounding each gap by radius + 4q.
    //
    // Pad is 2048q. The Z numerators spend less than 16q on rounding. Their slack also covers the
    // delta error and the division rounding for t in [0,1], since |delta| <= 3B. Thus clamping the
    // resulting t interval retains every witness. Projecting that interval on X, forming the strip and
    // converting to cell units spends less than 64q in world units, including up to 8q between a rounded
    // cell edge and the ideal grid. The inclusive integer bounds retain every intersecting cell.
    internal (int First, int Last) At(double bottom, int first, int last)
    {
        if (_strip == 0d) return (first, last);
        double enter = Math.Clamp((bottom - _strip - _startZ) / _dz, 0d, 1d);
        double exit = Math.Clamp((bottom + _size + _strip - _startZ) / _dz, 0d, 1d);
        double xEnter = _startX + enter * _dx, xExit = _startX + exit * _dx;
        double lo = (Math.Min(xEnter, xExit) - _strip - _originX) / _size;
        double hi = (Math.Max(xEnter, xExit) + _strip - _originX) / _size;
        return ((int)Math.Clamp(Math.Ceiling(lo) - 1d, first, last),
            (int)Math.Clamp(Math.Floor(hi), first, last));
    }
}
