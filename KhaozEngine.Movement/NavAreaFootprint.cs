using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Pure area eligibility over captured cells intersected by a complete circular footprint.</summary>
internal sealed class NavAreaFootprint
{
    private readonly PhysicsNavColumns _columns;
    private readonly PhysicsNavBakeOptions _options;
    private readonly NavAreaFilter _areas;
    private readonly uint _excludedAreas;
    private readonly double _radius;
    private readonly double _height;
    private readonly bool _checkHeadroom;
    private readonly double _surfaceReach;

    internal NavAreaFootprint(PhysicsNavColumns columns, PhysicsNavBakeOptions options,
        in MoveTuning tuning, NavAreaFilter areas, double? surfaceReach = null)
    {
        _columns = columns;
        _options = options;
        _areas = areas;
        _radius = tuning.CapsuleRadius;
        _height = 2d * tuning.CapsuleHalfHeight;
        // Contact proves clearance at the resting pose and along each swept shell route. A neighbouring
        // column can also capture ground under a low tread, whose headroom is not the body's clearance.
        _checkHeadroom = tuning.Controller != MovementControllerKind.Contact;
        // A footprint can straddle a step or slope while the core seats or paces the body.
        _surfaceReach = surfaceReach ?? (tuning.StepHeight + (double)tuning.GroundedEpsilon +
            _radius * Math.Tan(tuning.MaxSlopeRadians));
        AcceptsPredicate = Accepts;
    }

    private NavAreaFootprint(NavAreaFootprint source, uint excludedAreas)
    {
        _columns = source._columns;
        _options = source._options;
        _areas = source._areas;
        _excludedAreas = source._excludedAreas | excludedAreas;
        _radius = source._radius;
        _height = source._height;
        _checkHeadroom = source._checkHeadroom;
        _surfaceReach = source._surfaceReach;
        AcceptsPredicate = Accepts;
    }

    internal NavAreaFootprint ExcludingAreas(uint excludedAreas) => new(this, excludedAreas);

    /// <summary>The captured columns this footprint reads. Profiles of one capture share the instance.</summary>
    internal PhysicsNavColumns Columns => _columns;

    /// <summary>The capture options that place the columns.</summary>
    internal PhysicsNavBakeOptions Options => _options;

    /// <summary>The area filter applied to every touched surface.</summary>
    internal NavAreaFilter Areas => _areas;
    internal uint ExcludedAreas => _excludedAreas;
    internal double SurfaceReach => _surfaceReach;
    internal double Radius => _radius;

    /// <summary>One delegate over <see cref="Accepts"/>, built once so traversal proofs do not allocate it per call.</summary>
    internal Func<Vector3, bool> AcceptsPredicate { get; }

    internal bool Accepts(Vector3 feet) => AcceptsSegment(feet, feet);

    internal bool AcceptsSegment(Vector3 from, Vector3 to) => AcceptsSegment(from, to, onlyExclusions: false);

    // The graph already owns support and clearance proofs, including non-walking links. Applying those
    // checks to a link's straight chord would reject an otherwise permitted dry Drop or Hop.
    internal bool AcceptsExclusions(Vector3 from, Vector3 to) => AcceptsSegment(from, to, onlyExclusions: true);

    private bool AcceptsSegment(Vector3 from, Vector3 to, bool onlyExclusions)
    {
        if (!TryBounds(from, to, out int x0, out int x1, out int z0, out int z1)) return false;
        HeightRange(from, to, out double lowY, out double highY);
        var rows = new NavFootprintRows(from, to, _options, _radius, x0, x1, z0, z1);
        NavFootprintDiagnostics? diagnostics = NavFootprintDiagnostics.Current;
        for (int z = z0; z <= z1; z++)
        {
            double bottom = RowBottom(z);
            (int first, int last) = rows.At(bottom, x0, x1);
            for (int x = first; x <= last; x++)
            {
                if (RefusesCell(from, to, x, z, bottom, lowY, highY, onlyExclusions))
                {
                    diagnostics?.CountRow(first, x);
                    return false;
                }
            }
            diagnostics?.CountRow(first, last);
        }
        return true;
    }

    /// <summary>Rejects nonfinite endpoints and bounds outside the half-open capture before enumerating cells.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryBounds(Vector3 from, Vector3 to, out int x0, out int x1, out int z0, out int z1)
    {
        if (Finite(from) && Finite(to))
        {
            double minX = Math.Min(from.X, to.X) - _radius, maxX = Math.Max(from.X, to.X) + _radius;
            double minZ = Math.Min(from.Z, to.Z) - _radius, maxZ = Math.Max(from.Z, to.Z) + _radius;
            if (!(minX < _options.MinX || minZ < _options.MinZ || maxX >= _options.MaxX || maxZ >= _options.MaxZ))
            {
                x0 = CellIndex(minX, _options.MinX);
                x1 = CellIndex(maxX, _options.MinX);
                z0 = Cell(minZ, _options.MinZ);
                z1 = Cell(maxZ, _options.MinZ);
                return true;
            }
        }
        x0 = x1 = z0 = z1 = default;
        return false;
    }

    /// <summary>Column support is selected against the complete height range of the swept segment.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void HeightRange(Vector3 from, Vector3 to, out double low, out double high)
    {
        low = Math.Min(from.Y, to.Y);
        high = Math.Max(from.Y, to.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double RowBottom(int z) => _options.MinZ + z * (double)_options.CellSize;

    /// <summary>Untouched cells pass. Touched cells apply the full column rule and can stop the scan.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RefusesCell(Vector3 from, Vector3 to, int x, int z, double bottom,
        double lowY, double highY, bool onlyExclusions)
    {
        double left = _options.MinX + x * (double)_options.CellSize;
        return NavFootprintGeometry.Touches(from, to, left, bottom, _options.CellSize, _radius) &&
            !AcceptsColumn(x, z, lowY, highY, onlyExclusions);
    }

    private bool AcceptsColumn(int x, int z, double lowY, double highY, bool onlyExclusions)
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
            if ((surface.Areas & _excludedAreas) != 0) return false;
            if (onlyExclusions) continue;
            double selectedLow = Math.Max(lowY, lower), selectedHigh = Math.Min(highY, upper);
            if (surface.Height - selectedLow > _surfaceReach || selectedHigh - surface.Height > _surfaceReach ||
                (_checkHeadroom && surface.Headroom < _height) || !_areas.Allows(surface.Areas)) return false;
        }
        return matched;
    }

    // Preserve the synchronous caller's two out-of-line Z conversions and inline X conversions.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Cell(double value, float origin) => CellIndex(value, origin);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellIndex(double value, float origin) => (int)Math.Floor((value - origin) / _options.CellSize);

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
