using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

internal static partial class ContactController
{
    /// <summary>The state handed between modes of one tick. Queries retain the published-body mapping and
    /// borrowed interval. Binding belongs to the accepted support, never a speculative probe. Last describes
    /// the last segment, while the ground accumulators include only accepted walking.</summary>
    internal readonly record struct ContactTickContinuation(Vector3 Feet, float VerticalVelocity, bool Grounded, float FallSpeed,
        Vector2 Commanded, Segment Last, bool Supported, double Budget, Mode Mode, SupportSample Support,
        float StepRise, float MoveRise, Vector2 GroundTravel, bool RiseSeen, bool DropSeen, float CarriedLag,
        float RemainingSeconds, ContactQueryContext Queries, float TractionGate)
    {
        internal Vector2 HorizontalVelocity { get; init; }
        internal MovementContactSupportBinding? Binding { get; init; }
        internal bool Swimming { get; init; }
        internal WaterExcursionState Excursion { get; init; }
        internal bool FluidVisited { get; init; }
        internal bool DescendingAtContact { get; init; }
        internal bool LastWasWalking { get; init; }
        internal bool Completed { get; init; }
        internal bool AirLanded { get; init; }
        internal bool UnwalkableSupport { get; init; }
        internal Vector3? FirstAirLandingFeet { get; init; }
        internal double UnroundedX { get; init; }
        internal double UnroundedZ { get; init; }
        internal Vector3 PublishedCentre => Supported && Support.Status is SupportStatus.Walkable or SupportStatus.Steep
            ? Queries.SupportedBodyCentre(Feet, Support) : Queries.BodyCentre(Feet);
    }

    // Each receiving mode uses the exact remainder, carry and climb budget of the accepted segment. The dry
    // path keeps its established arithmetic, including its whole-segment carry clipping convention.
    static ContactTickContinuation Move(in Context c, in MoveState s, Mode mode, SupportSample support, Vector3 feet,
        Vector2 commanded, Vector2 velocity, Vector2 displacement, Vector2 intent, float dt)
    {
        MoveTuning t = c.Tuning;
        var tick = new ContactTickContinuation(feet, s.VerticalVelocity, false, 0, commanded,
            new(commanded, commanded, Vector2.Zero, dt), false,
            t.MaxStepClimbSpeed > 0 ? (double)t.MaxStepClimbSpeed * dt : double.PositiveInfinity,
            mode, support, 0, 0, Vector2.Zero, false, false, 0, dt, c.Queries, c.Gate)
        {
            HorizontalVelocity = mode == Mode.Slide ? s.HorizontalVelocity : velocity,
            UnroundedX = feet.X,
            UnroundedZ = feet.Z,
        };
        bool firstRecorded = false, lagRecorded = false;
        for (int segment = 0; segment < MaxSegments; segment++)
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.ModeSegments);
            SupportQueryDiagnostics.Route(tick.Mode switch
            {
                Mode.Ground => SupportQueryRoute.Ground,
                Mode.Slide => SupportQueryRoute.Slide,
                Mode.Water => SupportQueryRoute.Water,
                _ => SupportQueryRoute.Air,
            });
            float time = tick.RemainingSeconds, remaining;
            bool continueMode, onPlan;
            Vector2 drive = tick.HorizontalVelocity;
            if (tick.Mode == Mode.Ground)
            {
                GroundStepResult step = GroundCore.Step(tick.Feet, displacement, time, t, c.Settings, tick.Queries,
                    tick.TractionGate, tick.Budget, c.ClampXz,
                    stepInProgress: s.StepDeltaY > 0 || GroundSignals.Ascending(s.ClimbRate), medium: c.Fluid);
                tick.Queries.RequireRecoveryAccepted();
                tick.Queries.AcceptRoute(step.Blocked);
                bool grounded = step.Footing is GroundFooting.Walkable or GroundFooting.Held;
                bool steepStart = segment == 0 && step.Footing == GroundFooting.Steep && step.ChangedAtStart;
                if (!steepStart)
                {
                    Vector2 ask = segment == 0 ? commanded : tick.HorizontalVelocity;
                    tick = tick with
                    {
                        Last = new(ask, ask, step.Achieved, c.Fluid is null ? time : time - step.RemainingTime),
                        Commanded = firstRecorded ? tick.Commanded : ask,
                    };
                    firstRecorded = true;
                }
                tick = tick with
                {
                    Feet = step.Feet,
                    Budget = step.ClimbBudget,
                    VerticalVelocity = steepStart ? s.VerticalVelocity : 0,
                    HorizontalVelocity = steepStart ? s.HorizontalVelocity : tick.HorizontalVelocity,
                    Support = step.Support,
                    Grounded = grounded,
                    Supported = grounded || step.Footing == GroundFooting.Steep,
                    StepRise = tick.StepRise + step.StepRise,
                    MoveRise = tick.MoveRise + step.MoveRise,
                    RiseSeen = tick.RiseSeen || step.RiseSeen,
                    DropSeen = tick.DropSeen || step.DropSeen,
                    CarriedLag = lagRecorded ? tick.CarriedLag : step.CarriedLag,
                    GroundTravel = tick.GroundTravel + step.Achieved,
                    Mode = grounded ? Mode.Ground : step.Footing == GroundFooting.Steep ? Mode.Slide : Mode.Air,
                    DescendingAtContact = false,
                    LastWasWalking = step.Footing == GroundFooting.Walkable,
                    UnwalkableSupport = tick.UnwalkableSupport || step.Footing == GroundFooting.Steep,
                };
                lagRecorded = true;
                remaining = step.RemainingTime;
                continueMode = step.MediumHandoff || !grounded && remaining > 0;
                onPlan = step.HorizontalPlanPreserved;
            }
            else if (tick.Mode == Mode.Slide)
            {
                SlideStepResult slide = SlideCore.Step(tick.Feet, tick.HorizontalVelocity, tick.VerticalVelocity,
                    intent, time, t, c.Settings, tick.TractionGate, tick.Support, tick.Queries, tick.Budget,
                    c.ClampXz, c.Fluid);
                drive = slide.HorizontalVelocity + slide.Steer;
                bool grounded = slide.Outcome is SlideOutcome.Landed or SlideOutcome.Wedged;
                tick = tick with
                {
                    Last = new(slide.HorizontalVelocity, drive, slide.Achieved,
                        c.Fluid is null ? time : time - slide.RemainingTime),
                    Commanded = firstRecorded ? tick.Commanded : drive,
                    Feet = slide.Feet,
                    Budget = slide.ClimbBudget,
                    HorizontalVelocity = slide.HorizontalVelocity,
                    VerticalVelocity = grounded ? 0 : slide.VerticalVelocity,
                    Support = slide.Support,
                    Grounded = grounded,
                    Supported = slide.Outcome != SlideOutcome.Airborne,
                    FallSpeed = slide.Outcome == SlideOutcome.Landed ? -slide.ImpactSpeed :
                        slide.Outcome == SlideOutcome.Wedged ? slide.IntegratedVerticalVelocity : tick.FallSpeed,
                    UnwalkableSupport = true,
                    Mode = grounded ? Mode.Ground : slide.Outcome == SlideOutcome.Airborne ? Mode.Air : Mode.Slide,
                    DescendingAtContact = false,
                    LastWasWalking = false,
                };
                firstRecorded = true;
                remaining = slide.RemainingTime;
                continueMode = slide.MediumHandoff || slide.Outcome == SlideOutcome.Airborne && remaining > 0;
                onPlan = false;
            }
            else
            {
                bool waterSegment = tick.Mode == Mode.Water;
                if (waterSegment)
                    tick = tick with { HorizontalVelocity = c.Fluid!.Drive(tick.Feet, c.Direction, c.Fraction, s.SpeedScale) };
                drive = tick.HorizontalVelocity;
                AirStepResult air = waterSegment
                    ? c.Fluid!.Step(tick, t, c.Settings, c.ClampXz)
                    : AirPass.StepWithMedium(tick.Feet, drive, tick.VerticalVelocity, time, t.Gravity, t, c.Settings,
                        tick.TractionGate, tick.Queries, tick.Budget, c.ClampXz, c.Fluid);
                tick.Queries.AcceptRoute(air.Blocked);
                Vector2 asked = segment == 0 ? commanded : drive;
                bool grounded = air.Outcome == AirOutcome.Landed;
                tick = tick with
                {
                    Last = new(asked, asked, air.Achieved, c.Fluid is null ? time : time - air.RemainingTime),
                    Commanded = firstRecorded ? tick.Commanded : asked,
                    Feet = air.Feet,
                    Budget = air.ClimbBudget,
                    HorizontalVelocity = air.HorizontalVelocity,
                    VerticalVelocity = air.VerticalVelocity,
                    Support = air.Support,
                    Grounded = grounded,
                    Supported = air.Outcome != AirOutcome.Airborne,
                    FallSpeed = grounded ? -air.FallSpeedAtContact : tick.FallSpeed,
                    AirLanded = tick.AirLanded || !waterSegment && grounded,
                    FirstAirLandingFeet = tick.FirstAirLandingFeet ?? (!waterSegment && grounded ? air.Feet : null),
                    UnwalkableSupport = tick.UnwalkableSupport || air.Outcome == AirOutcome.Sliding,
                    Mode = grounded ? Mode.Ground : air.Outcome == AirOutcome.Sliding ? Mode.Slide : Mode.Air,
                    DescendingAtContact = air.DescendingAtContact,
                    LastWasWalking = false,
                };
                firstRecorded = true;
                remaining = air.RemainingTime;
                continueMode = air.MediumHandoff || air.Outcome != AirOutcome.Airborne && remaining > 0;
                onPlan = air.HorizontalPlanPreserved;
            }
            if (c.Fluid is { } fluid)
            {
                using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.RoundingHandoff);
                bool grounded = fluid.Accept(tick.Feet, tick.VerticalVelocity, tick.Support, tick.Grounded, tick.DescendingAtContact);
                tick = tick with
                {
                    Grounded = grounded,
                    Swimming = fluid.Swimming,
                    Excursion = fluid.Excursion,
                    Mode = fluid.IsFluid ? Mode.Water : tick.Mode,
                    Supported = !fluid.IsFluid && tick.Supported,
                    Support = fluid.IsFluid ? NoSupport : tick.Support,
                    FluidVisited = tick.FluidVisited || fluid.IsFluid,
                };
                Vector3 accepted = tick.Feet;
                tick = ContactHorizontalRounding.Accept(tick, drive, (double)time - remaining, onPlan, t);
                if (tick.Supported && tick.Feet != accepted)
                {
                    // A rounded axis earns its own certificate. An analytic sample and its binding belong to
                    // their queried axis, even when the previous axis differs by only one representable value.
                    SupportQueryDiagnostics.Route(SupportQueryRoute.RoundedRead);
                    SupportSample rounded = tick.Queries.FindSupport(new(new(tick.Feet.X, tick.Feet.Z), tick.Feet.Y,
                        c.Settings.FootRadiusFraction * t.CapsuleRadius, t.StepHeight, t.StepHeight,
                        MathF.Cos(tick.TractionGate)), out var binding);
                    if (rounded.Status is not (SupportStatus.Walkable or SupportStatus.Steep))
                        throw new ContactQueryFailure(MovementAvailability.Unresolved);
                    tick = tick with { Support = rounded, Binding = binding };
                }
                else
                    tick = tick with
                    {
                        Binding = tick.Supported && tick.Support.Status is SupportStatus.Walkable or SupportStatus.Steep
                            ? tick.Queries.BindSupport(tick.Feet, tick.Support,
                                c.Settings.FootRadiusFraction * t.CapsuleRadius, tick.TractionGate) : null,
                    };
                if (tick.Feet != accepted)
                {
                    grounded = fluid.Accept(tick.Feet, tick.VerticalVelocity, tick.Support, tick.Grounded, tick.DescendingAtContact);
                    tick = tick with
                    {
                        Grounded = grounded,
                        Swimming = fluid.Swimming,
                        Excursion = fluid.Excursion,
                        Mode = fluid.IsFluid ? Mode.Water : tick.Mode,
                        Supported = !fluid.IsFluid && tick.Supported,
                        Support = fluid.IsFluid ? NoSupport : tick.Support,
                        Binding = fluid.IsFluid ? null : tick.Binding,
                    };
                }
            }
            tick = tick with { RemainingSeconds = continueMode && remaining > 0 ? remaining : 0 };
            if (tick.RemainingSeconds == 0) break;
            if (tick.Mode == Mode.Ground) displacement = tick.HorizontalVelocity * tick.RemainingSeconds;
        }
        if (c.Fluid is not null && tick.RemainingSeconds > 0)
            throw new ContactQueryFailure(MovementAvailability.Unresolved);
        return tick;
    }
}
