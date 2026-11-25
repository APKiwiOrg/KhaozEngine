using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Matches a Legacy resting capsule to the slope sampled at a raw captured endpoint.</summary>
internal struct LegacySlopeEndpoint(Vector3 target)
{
    private const float Tolerance = GroundTraversalProbe.ArrivalTolerance;
    private const float SweepLift = 2f * LegacySlopePlane.SurfaceTolerance;
    private LegacySlopePlane? _plane;
    private bool _sampled;

    internal bool Arrived(GroundMoveContext context, in MoveTuning tuning, Vector3 feet)
        => GroundTraversalProbe.Near(feet, target) ||
           feet.Y > target.Y && GroundTraversalProbe.Near(new(feet.X, target.Y, feet.Z), target) &&
           TryMatchSlope(context, tuning, feet);

    private bool TryMatchSlope(GroundMoveContext context, in MoveTuning tuning, Vector3 feet)
    {
        if (((IPhysicsWorld?)context.MovementQueries ?? context.Physics) is not { } world) return false;
        Vector3 origin = world.Origin;
        float cosLimit = MathF.Cos(tuning.MaxSlopeRadians);
        if (!_sampled)
        {
            _sampled = true;
            if (LegacySlopePlane.TryRead(world, target - origin, cosLimit, out LegacySlopePlane sampled))
                _plane = sampled;
        }
        if (_plane is not { } plane) return false;

        // The lower sphere centre is one radius along the normal from its tangent plane. Project that plane
        // to the capsule's actual XZ and solve for the capsule's resting feet height there.
        Vector3 normal = plane.Normal;
        Vector3 localFeet = feet - origin;
        Vector3 fromAxis = localFeet - plane.AxisPoint;
        double expectedOffset = (tuning.CapsuleRadius * (1d - normal.Y) -
            (double)fromAxis.X * normal.X - (double)fromAxis.Z * normal.Z) / normal.Y;
        if (!(Math.Abs(fromAxis.Y - expectedOffset) <= LegacySlopePlane.SurfaceTolerance)) return false;

        // The actual pose must be supported by that same slope. The handle alone cannot distinguish two floors
        // inside a mesh, so also match the contact normal and plane at the off-axis point of support.
        Vector3 centre = localFeet + Vector3.UnitY * (tuning.CapsuleHalfHeight + SweepLift);
        return world.SweepCapsule(CharacterMovement.CachedCapsuleFor(tuning), Pose.At(centre), -Vector3.UnitY,
            2f * SweepLift, out SweepHit support, QueryFilter.StaticsOnly) &&
            MathF.Abs(support.Distance - SweepLift) <= LegacySlopePlane.SurfaceTolerance &&
            plane.Matches(support.Body, support.Point, support.Normal, cosLimit);
    }
}
