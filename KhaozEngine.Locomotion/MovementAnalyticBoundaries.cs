using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

internal abstract class AnalyticBoundary(string identity) : MovementBoundary
{
    public sealed override string SemanticIdentity { get; } = string.IsNullOrWhiteSpace(identity)
        ? throw new ArgumentException("A boundary identity is required.", nameof(identity)) : identity;
    protected abstract bool Inside(float x, float z, float dx, float dz);
    protected abstract Vector2 Project(Vector2 desired, double inset);

    public sealed override MovementAvailability Classify(Vector3 centre, out bool inside)
    {
        inside = false;
        if (!MovementEnvironmentValidation.Finite(centre)) return MovementAvailability.Invalid;
        inside = Inside(centre.X, centre.Z, 0, 0);
        return MovementAvailability.Known;
    }

    public sealed override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
    {
        admitted = false;
        if (!MovementEnvironmentValidation.Finite(start) || !MovementEnvironmentValidation.Finite(displacement) ||
            !MovementEnvironmentValidation.Finite(storedEnd)) return MovementAvailability.Invalid;
        // Rectangles and discs are convex. Exact membership of both affine endpoints proves the
        // complete segment. The stored endpoint and its rounding correction are covered too.
        admitted = Inside(start.X, start.Z, 0, 0) && Inside(start.X, start.Z, displacement.X, displacement.Z) &&
            Inside(storedEnd.X, storedEnd.Z, 0, 0);
        return MovementAvailability.Known;
    }

    public sealed override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
    {
        constrained = default;
        Vector3 desired = start + displacement;
        MovementAvailability status = ProveSegment(start, displacement, desired, out bool clear);
        if (status != MovementAvailability.Known) return status;
        if (clear) { constrained = displacement; return MovementAvailability.Known; }
        if (!Inside(start.X, start.Z, 0, 0)) return MovementAvailability.Unresolved;
        // Projection is a proposal, not a certificate. An inward proposal handles a rounded point
        // outside a curved boundary. Every candidate must pass the exact complete-segment proof.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Vector2 point = Project(new(desired.X, desired.Z), attempt == 0 ? 0 : 2d * MovementQueryLease.CoverageSkinMetres);
            Vector3 candidate = new(point.X - start.X, displacement.Y, point.Y - start.Z);
            status = ProveSegment(start, candidate, start + candidate, out clear);
            if (status == MovementAvailability.Known && clear) { constrained = candidate; return status; }
        }
        return MovementAvailability.Unresolved;
    }
}

internal sealed class RectangleBoundary(double minX, double minZ, double maxX, double maxZ, string identity) : AnalyticBoundary(identity)
{
    protected override bool Inside(float x, float z, float dx, float dz) =>
        MovementBoundaryArithmetic.Compare(x, dx, minX) >= 0 && MovementBoundaryArithmetic.Compare(x, dx, maxX) <= 0 &&
        MovementBoundaryArithmetic.Compare(z, dz, minZ) >= 0 && MovementBoundaryArithmetic.Compare(z, dz, maxZ) <= 0;
    protected override Vector2 Project(Vector2 desired, double inset)
    {
        double ix = Math.Min(inset, (maxX - minX) * 0.5), iz = Math.Min(inset, (maxZ - minZ) * 0.5);
        return new((float)Math.Clamp((double)desired.X, minX + ix, maxX - ix),
            (float)Math.Clamp((double)desired.Y, minZ + iz, maxZ - iz));
    }
}

internal sealed class CircleBoundary(double x, double z, double radius, string identity) : AnalyticBoundary(identity)
{
    protected override bool Inside(float px, float pz, float dx, float dz) =>
        MovementBoundaryArithmetic.InCircle(px, pz, dx, dz, x, z, radius);
    protected override Vector2 Project(Vector2 desired, double inset)
    {
        double dx = desired.X - x, dz = desired.Y - z;
        double scale = Math.Max(Math.Abs(dx), Math.Abs(dz));
        if (scale == 0 || radius == 0) return new((float)x, (float)z);
        double length = scale * Math.Sqrt((dx / scale) * (dx / scale) + (dz / scale) * (dz / scale));
        double factor = Math.Min(1, Math.Max(0, radius - inset) / length);
        return new((float)(x + dx * factor), (float)(z + dz * factor));
    }
}
