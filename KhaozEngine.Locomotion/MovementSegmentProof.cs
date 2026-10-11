using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>The shared affine rounding, solid and full-body medium proof. Solid geometry may be the contact
/// shell while medium geometry remains the original body. Neither a trace nor a proposal publishes motion.</summary>
internal static class MovementSegmentProof
{
    internal static MovementAvailability Project(Vector3 value, ReadOnlySpan<Vector3> normals, out Vector3 projected) =>
        MovementConstraintProjection.TryProject(value, normals, out projected);

    internal static MovementAvailability Trace(in MovementBodyQuery solid, in MovementBodyQuery body,
        Vector3 delta, MovementQueryLease queries, bool proveMedium, WaterTraversalMode mode, float enterFraction,
        MovementBoundary? boundary, QueryFilter filter, Span<Vector3> waterNormals, out int waterCount,
        out MovementWaterBoundary.Hit waterHit, out CapsuleSweepResult result, out Vector3 end, out double inflation)
    {
        result = default;
        waterCount = 0;
        waterHit = default;
        end = body.Centre + delta;
        inflation = 0;
        if (!MovementCapsuleRounding.TryEnclose(solid, delta, out MovementBodyQuery enclosure, out _, out inflation))
            return MovementAvailability.Unresolved;
        if (boundary is not null)
        {
            MovementAvailability proof = boundary.ProveSegment(body.Centre, delta, end, out bool admitted);
            if (proof != MovementAvailability.Known) return proof;
            if (!admitted) return MovementAvailability.Unresolved;
        }
        MovementAvailability status = queries.QuerySolidSweepFiltered(enclosure, delta, out result, filter);
        if (status != MovementAvailability.Known || !proveMedium) return status;
        if (mode == WaterTraversalMode.Legacy && result.Status != CapsuleSweepStatus.Clear) return status;
        return Medium(body, delta, queries, mode, enterFraction, null, waterNormals, out waterCount, out waterHit);
    }

    internal static MovementAvailability Medium(in MovementBodyQuery body, Vector3 delta,
        MovementQueryLease queries, WaterTraversalMode mode, float enterFraction, MovementBoundary? boundary,
        Span<Vector3> normals, out int count, out MovementWaterBoundary.Hit hit, Vector3? storedEnd = null)
    {
        count = 0;
        hit = default;
        if (!MovementCapsuleRounding.TryEncloseStored(body, delta, out MovementBodyQuery enclosure, out Vector3 end,
            out _, storedEnd ?? body.Centre + delta))
            return MovementAvailability.Unresolved;
        if (boundary is not null)
        {
            MovementAvailability status = boundary.ProveSegment(body.Centre, delta, end, out bool admitted);
            if (status != MovementAvailability.Known) return status;
            if (!admitted) { hit = new(true, 0, false); return MovementAvailability.Known; }
        }
        if (mode is WaterTraversalMode.SurfaceSwimmer or WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly)
            return MovementWaterBoundary.Find(enclosure, delta, body.HalfHeight, enterFraction, mode, queries,
                normals, out count, out hit);
        return MovementWaterPathProof.Check(enclosure, delta, queries, mode);
    }
}
