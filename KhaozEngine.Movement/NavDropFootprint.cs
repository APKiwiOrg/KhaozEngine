using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>A complete projected capsule corridor over known, eligible downward support. Air poses are not
/// required to be close to the lower surface. This certificate never replaces the ordinary boundary.</summary>
internal sealed class NavDropFootprint
{
    readonly Vector3 _from, _to;
    readonly double _radius, _low, _high;
    readonly NavAreaFootprint _ground;
    readonly MoveTuning _tuning;
    readonly float _allowance;
    double _highest;

    internal NavDropFootprint(NavAreaFootprint ground, MoveTuning tuning, Vector3 from, Vector3 to, float allowance)
    {
        _ground = ground;
        _tuning = tuning;
        _from = from;
        _to = to;
        _radius = tuning.CapsuleRadius;
        _allowance = allowance;
        _highest = from.Y;
        _low = (double)from.Y - allowance;
        _high = (double)from.Y + tuning.StepHeight;
    }

    internal bool Certify()
    {
        PhysicsNavBakeOptions options = _ground.Options;
        // Centres may brake or slide within one radius of the intended affine path. Certify the full shell
        // projection around that volume, including both endpoint caps, before running any fall slice.
        double radius = 2 * _radius;
        double minX = Math.Min(_from.X, _to.X) - radius, maxX = Math.Max(_from.X, _to.X) + radius;
        double minZ = Math.Min(_from.Z, _to.Z) - radius, maxZ = Math.Max(_from.Z, _to.Z) + radius;
        if (minX < options.MinX || minZ < options.MinZ || maxX >= options.MaxX || maxZ >= options.MaxZ) return false;
        int x0 = Cell(minX, options.MinX), x1 = Cell(maxX, options.MinX);
        int z0 = Cell(minZ, options.MinZ), z1 = Cell(maxZ, options.MinZ);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                double left = options.MinX + x * (double)options.CellSize;
                double bottom = options.MinZ + z * (double)options.CellSize;
                if (!NavFootprintGeometry.Touches(_from, _to, left, bottom, options.CellSize, radius)) continue;
                ReadOnlySpan<PhysicsNavSurface> column = _ground.Columns.GetColumn(x, z);
                int selected = column.Length - 1;
                while (selected >= 0 && column[selected].Height > _high) selected--;
                if (selected < 0 || column[selected].Height < _low || !_ground.Areas.Allows(column[selected].Areas)) return false;
            }
        return true;
    }

    internal bool Contains(Vector3 feet)
    {
        if (!Finite(feet) || feet.Y < _low || feet.Y > _high) return false;
        return ContainsHorizontal(feet);
    }

    internal void BeginMode() => _highest = _from.Y;

    bool ContainsHorizontal(Vector3 feet)
    {
        if (!Finite(feet)) return false;
        double dx = (double)_to.X - _from.X, dz = (double)_to.Z - _from.Z;
        double length = dx * dx + dz * dz;
        double fraction = length == 0 ? 0 : Math.Clamp(((feet.X - (double)_from.X) * dx +
            (feet.Z - (double)_from.Z) * dz) / length, 0, 1);
        double x = feet.X - (double)_from.X - fraction * dx, z = feet.Z - (double)_from.Z - fraction * dz;
        return x * x + z * z <= _radius * _radius;
    }

    internal MovementBoundary Compose(MovementBoundary ordinary, MovementFrameDescriptor frame) => new Boundary(this, ordinary, frame);

    int Cell(double coordinate, float origin) => (int)Math.Floor((coordinate - origin) / _ground.Options.CellSize);
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    sealed class Boundary(NavDropFootprint owner, MovementBoundary ordinary, MovementFrameDescriptor frame)
        : MovementBoundary, IMovementRouteBoundary
    {
        public override string SemanticIdentity => ordinary.SemanticIdentity + "/drop-corridor";

        bool ContainsCentre(Vector3 centre) => owner.Contains(centre + frame.PhysicsOrigin -
            Vector3.UnitY * owner._tuning.CapsuleHalfHeight);
        Vector3 Feet(Vector3 centre) => centre + frame.PhysicsOrigin - Vector3.UnitY * owner._tuning.CapsuleHalfHeight;
        bool ContainsHorizontal(Vector3 centre) => owner.ContainsHorizontal(Feet(centre));

        public override MovementAvailability Classify(Vector3 centre, out bool inside)
        {
            var status = ordinary.Classify(centre, out inside);
            inside &= ContainsCentre(centre);
            return status;
        }

        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
        {
            var status = ordinary.Constrain(start, displacement, out constrained);
            if (status == MovementAvailability.Known && (!ContainsHorizontal(start) || !ContainsHorizontal(start + constrained)))
                constrained = Vector3.Zero;
            return status;
        }

        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
        {
            var status = ordinary.ProveSegment(start, displacement, storedEnd, out admitted);
            // Sweep and support proposals must stay in the known projected corridor. Physics resolves their
            // final height before the accepted-route certificate below applies the exact descent permission.
            admitted &= ContainsHorizontal(start) && ContainsHorizontal(start + displacement) && ContainsHorizontal(storedEnd);
            return status;
        }

        public MovementAvailability ProveRouteSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
        {
            var status = ordinary.ProveSegment(start, displacement, storedEnd, out admitted);
            Vector3 a = Feet(start), b = Feet(start + displacement), c = Feet(storedEnd);
            admitted &= ContainsCentre(start) && ContainsCentre(start + displacement) && ContainsCentre(storedEnd) &&
                owner._highest - Math.Min(a.Y, Math.Min(b.Y, c.Y)) <= owner._allowance;
            return status;
        }

        public void RecordSupportedCentre(Vector3 centre) => owner._highest = Math.Max(owner._highest, Feet(centre).Y);
    }
}
