using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public enum ExplicitMotionProofStatus { Unresolved, Proven, Blocked, Invalid, BudgetExceeded, FrameMismatch }

/// <summary>A local directed-motion proof. It never means globally unreachable. A failed edge
/// exposes no usable state prefix. The result is recorded while its query lease is still held.</summary>
public readonly record struct ExplicitMotionProof(ExplicitMotionProofStatus Status, FramedMovementState? End,
    int Steps, string ProfileFingerprint, MovementQueryIdentity QueryIdentity);

internal static class ExplicitMotionProbe
{
    internal static ExplicitMotionProof Prove(GroundMoveContext context, in FramedMovementState start,
        Vector3 target, bool targetSwimming, ExplicitMovementProfile profile, MovementQueryLease queries, MovementBoundary? boundary)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(queries);
        string key = profile.Fingerprint;
        MovementQueryIdentity identity = queries.Identity;
        if (!Finite(target)) return Failed(ExplicitMotionProofStatus.Invalid, 0, key, identity);
        try
        {
            MovementStepResult hold = context.StepExplicit(start, Vector2.Zero, false, profile, queries, boundary);
            if (!Usable(hold.Outcome)) return Failed(Status(hold.Outcome), 1, key, identity);
            var current = hold.State;
            if (!Supported(current.State) || !Near(current.State.Position, start.State.Position, current.State.Swimming, profile))
                return Failed(ExplicitMotionProofStatus.Blocked, 1, key, identity);
            for (int step = 1; step <= profile.MaxSteps; step++)
            {
                if (current.State.Swimming == targetSwimming && Near(current.State.Position, target, targetSwimming, profile))
                {
                    queries.AssertCurrent();
                    return new(ExplicitMotionProofStatus.Proven, current, step, key, identity);
                }
                if (step == profile.MaxSteps) break;
                Vector2 delta = new(target.X - current.State.Position.X, target.Z - current.State.Position.Z);
                double distance = Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y);
                var selected = current.Selection!.Value;
                var body = new MovementBodyQuery(current.State.Position, profile.Tuning.CapsuleRadius,
                    profile.Tuning.CapsuleHalfHeight, selected.Space, selected.Support);
                MovementWaterPoint point = queries.SampleCentreWater(body);
                if (point.Availability != MovementAvailability.Known)
                    return Failed(point.Availability == MovementAvailability.Invalid ? ExplicitMotionProofStatus.Invalid
                        : ExplicitMotionProofStatus.Unresolved, step, key, identity);
                double speed = Math.Max(profile.Tuning.WalkSpeed, profile.Tuning.SwimSpeed) *
                    (double)current.State.SpeedScale * point.SpeedScale;
                if (!(speed > 0) || !double.IsFinite(speed)) return Failed(ExplicitMotionProofStatus.Unresolved, step, key, identity);
                double fraction = Math.Min(1, Math.Min(distance, profile.Tuning.CapsuleRadius) / (speed * profile.StepSeconds));
                Vector2 direction = distance > 0 ? delta / (float)distance * (float)fraction : Vector2.Zero;
                MovementStepResult next = context.StepExplicit(current, direction, false, profile, queries, boundary);
                if (!Usable(next.Outcome)) return Failed(Status(next.Outcome), step + 1, key, identity);
                if (!Supported(next.State.State)) return Failed(ExplicitMotionProofStatus.Blocked, step + 1, key, identity);
                if (next.State.State.Position == current.State.Position)
                    return Failed(next.Outcome == MovementStepOutcome.Blocked ? ExplicitMotionProofStatus.Blocked
                        : ExplicitMotionProofStatus.Unresolved, step + 1, key, identity);
                current = next.State;
            }
            return Failed(ExplicitMotionProofStatus.BudgetExceeded, profile.MaxSteps, key, identity);
        }
        catch (InvalidOperationException) { return Failed(ExplicitMotionProofStatus.Unresolved, 0, key, identity); }
        catch (ArgumentException) { return Failed(ExplicitMotionProofStatus.Invalid, 0, key, identity); }
    }

    static bool Supported(in MoveState state) => state.Grounded || state.Swimming;
    static bool Usable(MovementStepOutcome outcome) => outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked;
    static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    static bool Near(Vector3 point, Vector3 target, bool swimming, ExplicitMovementProfile profile)
    {
        double x = (double)point.X - target.X, z = (double)point.Z - target.Z;
        double tolerance = GroundTraversalProbe.ArrivalTolerance;
        double vertical = swimming ? profile.Water.SurfaceContactToleranceMetres : tolerance;
        return x * x + z * z <= tolerance * tolerance && Math.Abs((double)point.Y - target.Y) <= vertical;
    }
    static ExplicitMotionProofStatus Status(MovementStepOutcome outcome) => outcome switch
    {
        MovementStepOutcome.EnvironmentInvalid => ExplicitMotionProofStatus.Invalid,
        MovementStepOutcome.PlacementRefused => ExplicitMotionProofStatus.Blocked,
        MovementStepOutcome.FrameMismatch => ExplicitMotionProofStatus.FrameMismatch,
        _ => ExplicitMotionProofStatus.Unresolved
    };
    static ExplicitMotionProof Failed(ExplicitMotionProofStatus status, int steps, string key, MovementQueryIdentity identity) =>
        new(status, null, steps, key, identity);
}
