using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>The shared bounded ordinary-input policy for a baked fall and its live execution.</summary>
internal static class ContactDropControl
{
    internal static int PreviewSteps(PhysicsNavBakeOptions options, float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        double count = Math.Floor((double)options.EdgeProbeSeconds * options.MaxEdgeProbeSteps / dt);
        if (!double.IsFinite(count) || count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(dt), "The bounded Drop preview count is not representable.");
        return (int)count;
    }

    internal static Vector2 Input(in MoveState body, Vector3 landingFeet, in MoveTuning tuning,
        bool run, float landScale, float dt)
    {
        Vector2 offset = new(landingFeet.X - body.Position.X, landingFeet.Z - body.Position.Z);
        float pace = (run ? tuning.RunSpeed : tuning.WalkSpeed) * body.SpeedScale * landScale;
        if (!body.Grounded && !tuning.AirMomentum) pace *= tuning.AirControl;
        float distance = offset.Length();
        if (distance == 0 || pace == 0) return Vector2.Zero;
        float denominator = MathF.Max(distance, pace * dt);
        return float.IsFinite(denominator) && denominator > 0 ? offset / denominator : Vector2.Zero;
    }
}
