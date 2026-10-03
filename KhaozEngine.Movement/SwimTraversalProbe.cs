using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Bounded directed proofs for an edge with a float endpoint, stepped through the live context and its
/// medium. A float start floats at its feet, any other start stands grounded. Every slice must stay finite and
/// footprint-eligible and be grounded and not swimming, or swimming and clear of statics.</summary>
internal static class SwimTraversalProbe
{
    internal static bool TryEdge(GroundMoveContext context, in MoveTuning tuning,
        Vector3 fromFeet, bool fromFloats, Vector3 toFeet, bool toFloats, Func<Vector3, bool> acceptsFootprint,
        float stepSeconds, int maxSteps)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(acceptsFootprint);
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        if (maxSteps < 1 || !float.IsFinite(stepSeconds * maxSteps))
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        context.ValidateTuning(tuning);
        if (!Finite(fromFeet) || !Finite(toFeet)) return false;
        if (!acceptsFootprint(fromFeet) || !acceptsFootprint(toFeet)) return false;

        MoveTuning probe = tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false };
        var body = new MoveState
        {
            Position = fromFeet + Vector3.UnitY * probe.CapsuleHalfHeight,
            Grounded = !fromFloats,
            Swimming = fromFloats,
            SpeedScale = 1f,
        };
        if (!Finite(body.Position)) return false;

        // The hold must keep the start's mode near the start, so a float node itself is clear.
        body = context.Step(body, Vector2.Zero, false, stepSeconds, probe);
        Vector3 feet = Feet(body, probe);
        if (!Finite(feet) || !acceptsFootprint(feet)) return false;
        bool held = fromFloats
            ? body.Swimming && SwimArrived(feet, fromFeet, probe) && context.SwimClear(body, probe)
            : !body.Swimming && GroundTraversalProbe.GroundArrived(body, feet, fromFeet, probe);
        if (!held) return false;
        if (Arrived(body, feet, toFeet, probe)) return true;

        for (int step = 1; step < maxSteps; step++)
        {
            Vector2 delta = new(toFeet.X - feet.X, toFeet.Z - feet.Z);
            float distance = delta.Length();
            if (!float.IsFinite(distance)) return false;
            // Cap each slice at the capsule radius, so consecutive clearance samples overlap.
            float pace = SwimPace.Bound(body, probe, false, stepSeconds, context);
            float bound = MathF.Min(pace, probe.CapsuleRadius);
            if (!(bound > 0f)) return false;
            Vector2 direction = distance > 0f ? delta / distance * (MathF.Min(distance, bound) / pace) : Vector2.Zero;
            body = context.Step(body, direction, false, stepSeconds, probe);
            feet = Feet(body, probe);
            if (!Finite(feet) || !acceptsFootprint(feet)) return false;
            if (body.Swimming ? !context.SwimClear(body, probe) : !body.Grounded) return false;
            if (Arrived(body, feet, toFeet, probe)) return true;
        }
        return false;
    }

    private static bool Arrived(in MoveState body, Vector3 feet, Vector3 target, in MoveTuning tuning)
        => body.Swimming ? SwimArrived(feet, target, tuning) : GroundTraversalProbe.GroundArrived(body, feet, target, tuning);

    // Buoyancy, not geometry, sets a swimmer's height, so it arrives within the vertical band Resolve accepts.
    private static bool SwimArrived(Vector3 feet, Vector3 target, in MoveTuning tuning)
    {
        double x = (double)feet.X - target.X, z = (double)feet.Z - target.Z;
        double tolerance = GroundTraversalProbe.ArrivalTolerance;
        return x * x + z * z <= tolerance * tolerance &&
            Math.Abs((double)feet.Y - target.Y) <= Math.Max(tuning.StepHeight, GroundTraversalProbe.ArrivalTolerance);
    }

    private static Vector3 Feet(in MoveState body, in MoveTuning tuning)
        => body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
