using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>The horizontal travel one tick commands, aware of whether the core swims this tick.</summary>
internal static class SwimPace
{
    /// <summary>When the context has a medium and <see cref="CharacterMovement.ResolveSwimming"/> says the body swims
    /// this tick, from the medium sampled at its feet, the bound is <c>SwimSpeed x max(0, WadeSpeedScale) x SpeedScale
    /// x dt</c>, the travel the core's swim step commands at full input. Otherwise it is the walk or run bound of
    /// <see cref="RangeApproachCore.TravelBound"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The swim bound is not finite.</exception>
    internal static float Bound(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context)
    {
        if (context.Medium is { } medium)
        {
            float feetY = body.Position.Y - tuning.CapsuleHalfHeight;
            MovementMedium sample = medium(body.Position.X, body.Position.Z, feetY);
            if (CharacterMovement.ResolveSwimming(body.Swimming, sample, feetY, tuning))
            {
                float bound = tuning.SwimSpeed * MathF.Max(0f, sample.WadeSpeedScale) * body.SpeedScale * dt;
                if (!float.IsFinite(bound)) throw new ArgumentOutOfRangeException(nameof(context), "Swim travel must be finite.");
                return bound;
            }
        }
        return RangeApproachCore.TravelBound(body, tuning, run, dt, context);
    }
}
