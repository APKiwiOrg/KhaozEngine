using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

internal static partial class ContactController
{
    internal static MoveState ClassifyStartWater(in MoveState state, in MovementWaterPoint point, Vector3 feet,
        in MoveTuning tuning, in WaterTraversalPolicy water, LeasedContactQueryContext leased, in GroundCoreSettings foot)
    {
        bool wasSwimming = state.Swimming || state.WaterExcursion == WaterExcursionState.Surface;
        float gate = CharacterMovement.TractionGate(state.Grounded, tuning);
        SupportSample initial = leased.FindSupport(new(new(feet.X, feet.Z), feet.Y,
            foot.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, tuning.StepHeight, MathF.Cos(gate)));
        bool footingAllowed = ExplicitCharacterMovement.FootingAllowed(point, wasSwimming, state.Position.Y, tuning, water);
        bool supported = initial.Status == SupportStatus.Walkable && footingAllowed &&
            (state.Grounded || state.VerticalVelocity <= 0 && MathF.Abs(initial.Height - feet.Y) <= tuning.GroundedEpsilon);
        MoveState start = state;
        ExplicitCharacterMovement.ClassifyWater(ref start, point, supported, wasSwimming, tuning, water, start.VerticalVelocity < 0);
        if (supported && state.Swimming)
        {
            start.Grounded = true;
            start.VerticalVelocity = 0;
        }
        if (water.Mode != WaterTraversalMode.SurfaceSwimmer)
        {
            start.Swimming = false;
            start.WaterExcursion = WaterExcursionState.None;
        }
        return start;
    }
}
