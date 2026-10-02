using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Bounded directed ground proofs through the shared movement context and collision core.</summary>
internal static class GroundTraversalProbe
{
    internal const float ArrivalTolerance = 0.001f;

    internal static bool TryEdge(GroundMoveContext context, in MoveTuning tuning,
        Vector3 fromFeet, Vector3 toFeet, Func<Vector3, bool> acceptsFootprint,
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
        // Reuse the absolute adapter with dry medium, preserving ground, normals, bounds and physics.
        GroundMoveContext dry = context.Medium is null ? context : new GroundMoveContext(
            context.GroundHeight, context.GroundNormal, context.Physics, context.ClampXz);
        var body = new MoveState
        {
            Position = fromFeet + Vector3.UnitY * probe.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1f,
        };
        if (!Finite(body.Position)) return false;

        body = dry.Step(body, Vector2.Zero, false, stepSeconds, probe);
        Vector3 feet = Feet(body, probe);
        if (!body.Grounded || !Finite(feet) || !Near(feet, fromFeet) || !acceptsFootprint(feet)) return false;
        if (Near(feet, toFeet)) return true;

        // The hold is the first slice of the budget. All further positions come from the core.
        for (int step = 1; step < maxSteps; step++)
        {
            Vector2 delta = new(toFeet.X - feet.X, toFeet.Z - feet.Z);
            float distance = delta.Length();
            if (!float.IsFinite(distance)) return false;
            Vector2 direction = distance > 0f
                ? delta / distance * MathF.Min(1f, distance / stepSeconds)
                : Vector2.Zero;
            body = dry.Step(body, direction, false, stepSeconds, probe);
            feet = Feet(body, probe);
            if (!body.Grounded || !Finite(feet) || !acceptsFootprint(feet)) return false;
            if (Near(feet, toFeet)) return true;
        }
        return false;
    }

    private static Vector3 Feet(in MoveState body, in MoveTuning tuning)
        => body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;

    private static bool Near(Vector3 a, Vector3 b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        return x * x + y * y + z * z <= (double)ArrivalTolerance * ArrivalTolerance;
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
