using System;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    static bool FootingAllowed(in MovementWaterPoint point, bool wasSwimming, float centreY,
        in MoveTuning tuning, in WaterTraversalPolicy water)
    {
        if (!point.InWater) return true;
        if (water.Mode is not (WaterTraversalMode.SurfaceSwimmer or WaterTraversalMode.WadeOnly) || !point.Interval!.Value.UpperIsFreeSurface) return false;
        return !RequiresSwimming(point, wasSwimming && water.Mode == WaterTraversalMode.SurfaceSwimmer, centreY, tuning);
    }

    static bool RequiresSwimming(in MovementWaterPoint point, bool wasSwimming, float centreY, in MoveTuning tuning) =>
        point.InWater && CharacterMovement.ResolveSwimming(wasSwimming,
            new MovementMedium(point.Interval!.Value.UpperY, true, point.SpeedScale),
            centreY - tuning.CapsuleHalfHeight, tuning);

    static double SurfaceCentre(in MovementWaterPoint point, in MoveTuning tuning) =>
        point.Interval!.Value.UpperY + (double)tuning.CapsuleHalfHeight *
            (1d - 2d * tuning.SwimSurfaceSubmersionFraction);

    static void ClassifyWater(ref MoveState state, in MovementWaterPoint point, bool footing,
        bool wasSwimming, in MoveTuning tuning, in WaterTraversalPolicy water, bool descending)
    {
        if (footing)
        {
            state.Swimming = false;
            state.WaterExcursion = WaterExcursionState.None;
            return;
        }
        bool free = water.Mode == WaterTraversalMode.SurfaceSwimmer && point.InWater && point.Interval!.Value.UpperIsFreeSurface;
        bool swimming = free && (state.WaterExcursion == WaterExcursionState.AirborneFromWater
            ? (state.VerticalVelocity < 0 || descending) && state.Position.Y <= SurfaceCentre(point, tuning) + water.SurfaceContactToleranceMetres
            : RequiresSwimming(point, wasSwimming, state.Position.Y, tuning));
        state.Swimming = swimming;
        if (swimming) state.WaterExcursion = WaterExcursionState.Surface;
        else if (state.WaterExcursion == WaterExcursionState.Surface)
            state.WaterExcursion = WaterExcursionState.AirborneFromWater;
    }

    static float MediumSpeed(in MovementWaterPoint point, float centreY, bool swimmingOrWaterFlight, in MoveTuning tuning)
    {
        if (!point.InWater) return 1;
        if (swimmingOrWaterFlight) return point.SpeedScale;
        float depth = (point.Interval!.Value.UpperY - (centreY - tuning.CapsuleHalfHeight)) / (2f * tuning.CapsuleHalfHeight);
        float ramp = depth <= tuning.WadeStartDepthFraction ? 1
            : depth >= tuning.WadeEndDepthFraction ? tuning.WadeMinSpeedScale
            : 1 - (depth - tuning.WadeStartDepthFraction) / (tuning.WadeEndDepthFraction - tuning.WadeStartDepthFraction) *
                (1 - tuning.WadeMinSpeedScale);
        return ramp * point.SpeedScale;
    }
}
