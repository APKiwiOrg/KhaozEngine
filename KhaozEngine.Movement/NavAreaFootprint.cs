using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Pure area eligibility over captured cells intersected by a complete circular footprint.</summary>
internal sealed class NavAreaFootprint
{
    private readonly PhysicsNavColumns _columns;
    private readonly PhysicsNavBakeOptions _options;
    private readonly NavAreaFilter _areas;
    private readonly double _radius;
    private readonly double _height;
    private readonly double _surfaceReach;

    internal NavAreaFootprint(PhysicsNavColumns columns, PhysicsNavBakeOptions options,
        in MoveTuning tuning, NavAreaFilter areas)
    {
        _columns = columns;
        _options = options;
        _areas = areas;
        _radius = tuning.CapsuleRadius;
        _height = 2d * tuning.CapsuleHalfHeight;
        // A footprint can straddle a step or slope while the core seats or paces the body.
        _surfaceReach = tuning.StepHeight + (double)tuning.GroundedEpsilon +
            _radius * Math.Tan(tuning.MaxSlopeRadians);
    }

    internal bool Accepts(Vector3 feet) => AcceptsSegment(feet, feet);

    internal bool AcceptsSegment(Vector3 from, Vector3 to)
    {
        if (!Finite(from) || !Finite(to)) return false;
        double minX = Math.Min(from.X, to.X) - _radius, maxX = Math.Max(from.X, to.X) + _radius;
        double minZ = Math.Min(from.Z, to.Z) - _radius, maxZ = Math.Max(from.Z, to.Z) + _radius;
        if (minX < _options.MinX || minZ < _options.MinZ || maxX >= _options.MaxX || maxZ >= _options.MaxZ)
            return false;
        int x0 = Cell(minX, _options.MinX), x1 = Cell(maxX, _options.MinX);
        int z0 = Cell(minZ, _options.MinZ), z1 = Cell(maxZ, _options.MinZ);
        double lowY = Math.Min(from.Y, to.Y), highY = Math.Max(from.Y, to.Y);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                double left = _options.MinX + x * (double)_options.CellSize;
                double bottom = _options.MinZ + z * (double)_options.CellSize;
                if (!Touches(from, to, left, bottom, _options.CellSize)) continue;
                if (!AcceptsColumn(x, z, lowY, highY)) return false;
            }
        return true;
    }

    private bool AcceptsColumn(int x, int z, double lowY, double highY)
    {
        if ((uint)x >= (uint)_columns.Width || (uint)z >= (uint)_columns.Height) return false;
        ReadOnlySpan<PhysicsNavSurface> column = _columns.GetColumn(x, z);
        if (column.IsEmpty) return false;
        bool matched = false;
        for (int i = 0; i < column.Length; i++)
        {
            PhysicsNavSurface surface = column[i];
            // Select by nearest captured surface height before applying tags, never by nearest allowed tag.
            double lower = i == 0 ? double.NegativeInfinity : ((double)column[i - 1].Height + surface.Height) * 0.5d;
            double upper = i + 1 == column.Length ? double.PositiveInfinity : ((double)surface.Height + column[i + 1].Height) * 0.5d;
            if (highY < lower || lowY > upper) continue;
            matched = true;
            double selectedLow = Math.Max(lowY, lower), selectedHigh = Math.Min(highY, upper);
            if (surface.Height - selectedLow > _surfaceReach || selectedHigh - surface.Height > _surfaceReach ||
                surface.Headroom < _height || !_areas.Allows(surface.Areas)) return false;
        }
        return matched;
    }

    private bool Touches(Vector3 from, Vector3 to, double left, double bottom, double size)
    {
        double right = left + size, top = bottom + size;
        if (Intersects(from, to, left, right, bottom, top)) return true;
        double distance = Math.Min(PointRectangle(from.X, from.Z, left, right, bottom, top),
            PointRectangle(to.X, to.Z, left, right, bottom, top));
        distance = Math.Min(distance, PointSegment(left, bottom, from, to));
        distance = Math.Min(distance, PointSegment(left, top, from, to));
        distance = Math.Min(distance, PointSegment(right, bottom, from, to));
        distance = Math.Min(distance, PointSegment(right, top, from, to));
        return distance <= _radius * _radius;
    }

    private static bool Intersects(Vector3 a, Vector3 b, double left, double right, double bottom, double top)
    {
        double enter = 0d, exit = 1d;
        return Clip(a.X, (double)b.X - a.X, left, right, ref enter, ref exit) &&
            Clip(a.Z, (double)b.Z - a.Z, bottom, top, ref enter, ref exit);
    }

    private static bool Clip(double start, double delta, double min, double max, ref double enter, ref double exit)
    {
        if (delta == 0d) return start >= min && start <= max;
        double a = (min - start) / delta, b = (max - start) / delta;
        enter = Math.Max(enter, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return enter <= exit;
    }

    private static double PointRectangle(double x, double z, double left, double right, double bottom, double top)
    {
        double dx = x - Math.Clamp(x, left, right), dz = z - Math.Clamp(z, bottom, top);
        return dx * dx + dz * dz;
    }

    private static double PointSegment(double x, double z, Vector3 a, Vector3 b)
    {
        double dx = (double)b.X - a.X, dz = (double)b.Z - a.Z;
        double length = dx * dx + dz * dz;
        double fraction = length == 0d ? 0d : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / length, 0d, 1d);
        double gapX = x - a.X - fraction * dx, gapZ = z - a.Z - fraction * dz;
        return gapX * gapX + gapZ * gapZ;
    }

    private int Cell(double value, float origin) => (int)Math.Floor((value - origin) / _options.CellSize);

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
