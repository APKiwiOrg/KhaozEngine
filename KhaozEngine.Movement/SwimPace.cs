using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>The horizontal travel one tick commands, aware of whether the core swims this tick.</summary>
internal static class SwimPace
{
    /// <summary>When the selected controller's water classification says the body swims
    /// this tick, the bound is <c>SwimSpeed x max(0, WadeSpeedScale) x SpeedScale
    /// x dt</c>, the travel the core's swim step commands at full input. Otherwise it is the walk or run bound of
    /// <see cref="RangeApproachCore.TravelBound"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The swim bound is not finite.</exception>
    internal static float Bound(in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context)
    {
        var sample = context.SampleWaterForPacing(body, tuning);
        if (sample.Swimming)
        {
            float bound = tuning.SwimSpeed * MathF.Max(0f, sample.Medium.WadeSpeedScale) * body.SpeedScale * dt;
            if (!float.IsFinite(bound)) throw new ArgumentOutOfRangeException(nameof(context), "Swim travel must be finite.");
            return bound;
        }
        return RangeApproachCore.TravelBound(body, tuning, run, dt, context);
    }
}
