using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Accepts sweep data for this complete request under the current read interval.
    /// Known validates the backend's declared result, not its geometric proof or a safe placement.</summary>
    internal MovementAvailability QuerySolidSweep(in MovementBodyQuery body, Vector3 displacement,
        out CapsuleSweepResult result)
    {
        result = default;
        AssertUsable();
        if (!_selectionReady) return MovementAvailability.Unresolved;
        if (!body.IsValid || !SameWorld(body.CurrentSpace) ||
            !MovementEnvironmentValidation.Finite(displacement) ||
            !MovementEnvironmentValidation.Finite(body.Centre + displacement))
            return MovementAvailability.Invalid;
        if (!ContainsSolidSweep(body, displacement)) return MovementAvailability.Unresolved;
        if (_view is not IPhysicsCapsuleSweep sweep) return MovementAvailability.Unresolved;

        // Float squared length can overflow even when all vector components are finite.
        double length = Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        float representedLength = (float)length;
        if (!float.IsFinite(representedLength)) return MovementAvailability.Unresolved;
        try
        {
            AssertCurrent();
            var capsule = new CapsuleShape(body.Radius, 2f * (body.HalfHeight - body.Radius));
            CapsuleSweepResult query = sweep.SweepCapsuleCertified(capsule, Pose.At(body.Centre), displacement);
            AssertCurrent();
            if (!query.IsValid) return MovementAvailability.Invalid;
            if (!query.IsComplete || query.CertifiedErrorMetres > CoverageSkinMetres)
                return MovementAvailability.Unresolved;
            if (query.Status == CapsuleSweepStatus.Clear)
            {
                // The field encodes the full original vector, never a shorter accepted path.
                if (query.ClearThroughDistance != representedLength) return MovementAvailability.Invalid;
            }
            else if (query.ClearThroughDistance > representedLength || query.ImpactDistance > representedLength)
                return MovementAvailability.Invalid;
            result = query;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
        catch (NotSupportedException) { return MovementAvailability.Unresolved; }
    }

    bool ContainsSolidSweep(in MovementBodyQuery body, Vector3 displacement)
    {
        Vector3 min = Witness.Scope.EnvelopeMin;
        Vector3 max = Witness.Scope.EnvelopeMax;
        // Compare in double so outward skin expansion cannot round inward at a float scope edge.
        double radial = (double)body.Radius + 2d * CoverageSkinMetres;
        double vertical = (double)body.HalfHeight + 2d * CoverageSkinMetres;
        return AxisInside(body.Centre.X, displacement.X, radial, min.X, max.X) &&
            AxisInside(body.Centre.Y, displacement.Y, vertical, min.Y, max.Y) &&
            AxisInside(body.Centre.Z, displacement.Z, radial, min.Z, max.Z);
    }

    static bool AxisInside(double centre, double delta, double extent, double min, double max) =>
        Math.Min(centre, centre + delta) - extent >= min &&
        Math.Max(centre, centre + delta) + extent <= max;
}
