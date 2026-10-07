using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Proposes vertical surface buoyancy or water-origin flight. No candidate is a clearance
/// proof. The explicit mover must trace the full path and classify its landing before publication.</summary>
internal static class SurfaceWaterMotion
{
    internal const float ContactToleranceMetres = 0.03f;

    internal static MovementAvailability TryPropose(in MoveState state, bool airborneFromWater, bool jump,
        in MovementWaterPoint point, float dt, in MoveTuning tuning, float surfaceJumpSpeed,
        out Vector2 vertical, out bool launched)
    {
        vertical = default;
        launched = false;
        if (!float.IsFinite(state.Position.Y) || !float.IsFinite(state.VerticalVelocity) ||
            !float.IsFinite(dt) || dt <= 0 || !ValidTuning(tuning) ||
            !float.IsFinite(surfaceJumpSpeed) || surfaceJumpSpeed < 0 || !point.IsValid)
            return MovementAvailability.Invalid;
        if (point.Availability != MovementAvailability.Known) return point.Availability;

        double y = state.Position.Y;
        double velocity = state.VerticalVelocity;
        bool launch = false;
        if (airborneFromWater)
        {
            // Neither a new press nor an old swimming flag can recapture an upward water arc.
            // Landing/entry needs an endpoint query and is owned by the combined mover.
            velocity = Math.Max(-tuning.MaxFallSpeed, velocity - (double)tuning.Gravity * dt);
            y += velocity * dt;
        }
        else
        {
            if (!point.InWater || point.Interval is not { UpperIsFreeSurface: true } interval)
                return MovementAvailability.Unresolved;
            double target = interval.UpperY + (double)tuning.CapsuleHalfHeight *
                (1d - 2d * tuning.SwimSurfaceSubmersionFraction);
            launch = jump && surfaceJumpSpeed > 0 && Math.Abs(y - target) <= ContactToleranceMetres;
            if (launch)
            {
                // Match the established gravity-first integrator and JumpSpeedForApex contract.
                velocity = Math.Max(-tuning.MaxFallSpeed, surfaceJumpSpeed - (double)tuning.Gravity * dt);
                y += velocity * dt;
            }
            else
            {
                double stiffness = tuning.SwimBuoyancyStiffness;
                double offset = y - target;
                double coefficient = velocity + stiffness * offset;
                double decay = Math.Exp(-stiffness * dt);
                double evolved = offset + coefficient * dt;
                y = target + evolved * decay;
                velocity = (coefficient - stiffness * evolved) * decay;
            }
        }

        Vector2 candidate = new((float)y, (float)velocity);
        if (!float.IsFinite(candidate.X) || !float.IsFinite(candidate.Y)) return MovementAvailability.Unresolved;
        vertical = candidate;
        launched = launch;
        return MovementAvailability.Known;
    }

    static bool ValidTuning(in MoveTuning tuning) =>
        float.IsFinite(tuning.CapsuleHalfHeight) && tuning.CapsuleHalfHeight > 0 &&
        float.IsFinite(tuning.SwimSurfaceSubmersionFraction) && tuning.SwimSurfaceSubmersionFraction >= 0 &&
        tuning.SwimSurfaceSubmersionFraction <= 1 &&
        float.IsFinite(tuning.SwimBuoyancyStiffness) && tuning.SwimBuoyancyStiffness >= 0 &&
        float.IsFinite(tuning.Gravity) && tuning.Gravity > 0 &&
        float.IsFinite(tuning.MaxFallSpeed) && tuning.MaxFallSpeed > 0;
}
