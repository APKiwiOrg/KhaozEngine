using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

internal static class ContactNavigationEnvelope
{
    internal static void Validate(PhysicsNavBake bake, in MoveTuning tuning, in MovementQueryScope scope)
    {
        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        for (int z = 0; z < bake.Columns.Height; z++)
            for (int x = 0; x < bake.Columns.Width; x++)
                foreach (PhysicsNavSurface surface in bake.Columns.GetColumn(x, z))
                {
                    low = MathF.Min(low, surface.Height);
                    high = MathF.Max(high, surface.Height);
                }
        PhysicsNavBakeOptions options = bake.Options;
        if (float.IsPositiveInfinity(low))
        {
            low = options.ProbeHeight - options.ProbeRange;
            high = options.ProbeHeight;
        }
        float foot = tuning.CapsuleRadius * tuning.FootRadiusFraction;
        float reach = tuning.StepHeight + 2 * GroundSignals.Samples * tuning.CapsuleRadius;
        float slope = tuning.TractionHysteresisRadians > 0
            ? tuning.MaxSlopeRadians + tuning.TractionHysteresisRadians : tuning.MaxSlopeRadians;
        SupportFootprintBounds bare = Bounds(foot, reach, tuning.MaxSlopeRadians);
        SupportFootprintBounds traction = Bounds(foot, reach, slope);
        double horizontal = Math.Max(Math.Max(-bare.Minimum.X, bare.Maximum.X),
            Math.Max(-traction.Minimum.X, traction.Maximum.X));
        double margin = tuning.CapsuleRadius + horizontal + SupportCertification.ContactBand +
            GroundSignals.Samples * (double)tuning.CapsuleRadius;
        Vector3 origin = scope.Frame.PhysicsOrigin;
        Vector3 min = new(OrdinaryServedDomain.Lower(options.MinX - margin - origin.X),
            OrdinaryServedDomain.Lower(low - (double)tuning.GroundedEpsilon +
                Math.Min(bare.Minimum.Y, traction.Minimum.Y) - origin.Y),
            OrdinaryServedDomain.Lower(options.MinZ - margin - origin.Z));
        Vector3 max = new(OrdinaryServedDomain.Upper(options.MaxX + margin - origin.X),
            OrdinaryServedDomain.Upper(high + (double)tuning.StepHeight + 2d * tuning.CapsuleHalfHeight +
                Math.Max(bare.Maximum.Y, traction.Maximum.Y) - origin.Y),
            OrdinaryServedDomain.Upper(options.MaxZ + margin - origin.Z));
        if (!scope.ContainsBounds(min, max))
            throw new InvalidOperationException("The contact navigation envelope is outside the ordinary served scope.");
    }

    static SupportFootprintBounds Bounds(float foot, float reach, float slope)
    {
        var query = new FootSupportQuery(Vector2.Zero, 0, foot, reach, reach, MathF.Cos(slope));
        if (!SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query), out var bounds))
            throw new InvalidOperationException("The contact navigation support envelope is unbounded.");
        return bounds;
    }
}
