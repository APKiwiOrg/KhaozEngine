using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>One resident query region, independent of commands, carried speed and replay length.</summary>
internal static class OrdinaryServedDomain
{
    internal static MovementQueryScope Create(OrdinaryMovementRegistration registration,
        in MoveTuning tuning, IMovementWaterField? waterField,
        in MovementQueryIdentity identity, in MovementFrameDescriptor frame)
    {
        OrdinaryMovementExtent extent = registration.Extent;
        Vector3 min = extent.Minimum, max = extent.Maximum;
        if (waterField is not null)
        {
            MovementWaterExtent water = waterField.Extent;
            _ = new MovementWaterExtent(water.Minimum, water.Maximum);
            // Level's represented full-XZ slab never replaces the installer's finite horizontal declaration.
            bool unboundedHorizontal = water.Minimum.X == -float.MaxValue && water.Minimum.Z == -float.MaxValue &&
                water.Maximum.X == float.MaxValue && water.Maximum.Z == float.MaxValue;
            if ((!unboundedHorizontal && (water.Minimum.X < min.X || water.Minimum.Z < min.Z ||
                water.Maximum.X > max.X || water.Maximum.Z > max.Z)) || water.Minimum.Y < min.Y || water.Maximum.Y > max.Y)
                throw new ArgumentException("The water field must fit the declared resident extent.", nameof(waterField));
        }
        float foot = tuning.CapsuleRadius * tuning.FootRadiusFraction;
        float reach = tuning.StepHeight + 2 * GroundSignals.Samples * tuning.CapsuleRadius;
        float band = tuning.TractionHysteresisRadians;
        float enlarged = band > 0 ? tuning.MaxSlopeRadians + band : tuning.MaxSlopeRadians;
        SupportFootprintBounds bare = Bounds(foot, reach, tuning.MaxSlopeRadians);
        SupportFootprintBounds traction = Bounds(foot, reach, enlarged);
        double outwardHorizontal = Math.Max(Math.Max(-bare.Minimum.X, bare.Maximum.X),
            Math.Max(-traction.Minimum.X, traction.Maximum.X)) - foot;
        double margin = tuning.CapsuleRadius + (double)foot + SupportCertification.ContactBand +
            GroundSignals.Samples * (double)tuning.CapsuleRadius + outwardHorizontal;
        double launch = Math.Max(tuning.JumpSpeed, tuning.Water.SurfaceJumpSpeed);
        double apex = launch * launch / (2 * tuning.Gravity);
        double bottom = min.Y - (double)reach + Math.Min(bare.Minimum.Y, traction.Minimum.Y);
        double top = max.Y + apex + 2d * tuning.CapsuleHalfHeight + reach +
            Math.Max(bare.Maximum.Y, traction.Maximum.Y);
        float height = Upper(top - bottom);
        Vector3 localMin = new(Lower(min.X - margin - frame.PhysicsOrigin.X),
            Lower(bottom - frame.PhysicsOrigin.Y), Lower(min.Z - margin - frame.PhysicsOrigin.Z));
        Vector3 localMax = new(Upper(max.X + margin - frame.PhysicsOrigin.X),
            Upper(top - frame.PhysicsOrigin.Y), Upper(max.Z + margin - frame.PhysicsOrigin.Z));
        return new(localMin, localMax, height, height, registration.WorldId, null, identity, frame);
    }

    static SupportFootprintBounds Bounds(float foot, float reach, float slope)
    {
        var query = new FootSupportQuery(Vector2.Zero, 0, foot, reach, reach, MathF.Cos(slope));
        if (!SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query), out var bounds))
            throw new ArgumentException("The ordinary footprint admission region must be finite.");
        return bounds;
    }

    internal static float Lower(double value)
    {
        float result = (float)value;
        return result > value ? MathF.BitDecrement(result) : result;
    }

    internal static float Upper(double value)
    {
        float result = (float)value;
        return result < value ? MathF.BitIncrement(result) : result;
    }
}
