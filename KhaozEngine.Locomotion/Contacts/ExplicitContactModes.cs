using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Swimming's policy and dynamics inside one contact tick. The contact cores own every shell move,
/// footprint landing and climb payment. Only selected substeps update this medium history.</summary>
internal sealed class ExplicitContactModes : ContactMediumPolicy
{
    ContactQueryContext _queries;
    MoveTuning _tuning;
    WaterTraversalPolicy _water;
    MoveState _state;
    bool _surfacePress;
    internal bool EnteredWater { get; private set; }
    internal bool PressConsumed { get; private set; }
    internal bool IsFluid => _state.Swimming || _state.WaterExcursion == WaterExcursionState.AirborneFromWater;
    internal bool Swimming => _state.Swimming;
    internal WaterExcursionState Excursion => _state.WaterExcursion;

    internal ExplicitContactModes(ContactQueryContext queries, in MoveState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, bool jump)
        => Initialize(queries, state, tuning, water, jump);

    internal ExplicitContactModes() { }

    internal void Reset()
    {
        _queries = default;
        _state = default;
        _tuning = default;
        _water = default;
        _surfacePress = EnteredWater = PressConsumed = false;
    }

    internal void Initialize(ContactQueryContext queries, in MoveState state, in MoveTuning tuning,
        in WaterTraversalPolicy water, bool jump)
    {
        Reset();
        (_queries, _state, _tuning, _water) = (queries, state, tuning, water);
        _surfacePress = state.Swimming && jump;
        PressConsumed = IsFluid || water.Mode == WaterTraversalMode.SurfaceSwimmer && jump && !state.Grounded &&
            queries.SampleMedium(queries.KernelFeet(state.Position, tuning))!.Value.InWater;
        EnteredWater = state.Swimming;
    }

    internal override bool AllowsFooting(Vector3 feet, in SupportSample support)
    {
        Vector3 seated = feet with { Y = support.Height };
        MovementWaterPoint point = _queries.SampleMedium(seated)!.Value;
        return ExplicitCharacterMovement.FootingAllowed(point, _state.Swimming,
            _queries.BodyCentre(seated).Y, _tuning, _water);
    }

    MoveState Classified(Vector3 feet, float vertical, in SupportSample support, bool grounded, bool descending = false)
    {
        MoveState next = _state with { Position = _queries.BodyCentre(feet), VerticalVelocity = vertical };
        bool footing = grounded && AllowsFooting(feet, support);
        MovementWaterPoint point = _queries.SampleMedium(feet)!.Value;
        ExplicitCharacterMovement.ClassifyWater(ref next, point, footing, _state.Swimming,
            _tuning, _water, vertical < 0 || descending);
        next.Grounded = footing;
        return next;
    }

    internal override bool ChangesOwner(Vector3 feet, float verticalVelocity, in SupportSample support, bool grounded)
    {
        MoveState next = Classified(feet, verticalVelocity, support, grounded);
        return next.Swimming != _state.Swimming || next.WaterExcursion != _state.WaterExcursion;
    }

    internal bool Accept(Vector3 feet, float vertical, in SupportSample support, bool grounded, bool descending = false)
    {
        _state = Classified(feet, vertical, support, grounded, descending);
        if (_state.Swimming)
        {
            EnteredWater = true;
            PressConsumed = true;
        }
        if (_water.Mode == WaterTraversalMode.SurfaceSwimmer && !_state.Grounded &&
            _queries.SampleMedium(feet)!.Value.InWater) PressConsumed = true;
        return _state.Grounded;
    }

    internal void ResolvePress(bool jump) => _surfacePress = Swimming && jump;

    internal float LandScale(Vector3 feet) => ExplicitCharacterMovement.MediumSpeed(
        _queries.SampleMedium(feet)!.Value, _queries.BodyCentre(feet).Y, false, _tuning);

    internal Vector2 Drive(Vector3 feet, Vector2 direction, float fraction, float speedScale) => direction *
        (_tuning.SwimSpeed * fraction * speedScale * ExplicitCharacterMovement.MediumSpeed(
            _queries.SampleMedium(feet)!.Value, _queries.BodyCentre(feet).Y, true, _tuning));

    internal AirStepResult Step(in ContactController.ContactTickContinuation continuation,
        in MoveTuning tuning, in GroundCoreSettings settings, Func<float, float, Vector2>? clamp)
    {
        Vector3 feet = continuation.Feet;
        float time = continuation.RemainingSeconds;
        MoveState from = _state with { Position = continuation.PublishedCentre, VerticalVelocity = continuation.VerticalVelocity };
        MovementWaterPoint point = _queries.SampleMedium(feet)!.Value;
        bool press = _surfacePress;
        _surfacePress = false;
        MovementAvailability status = SurfaceWaterMotion.TryPropose(from, !from.Swimming, press, point,
            time, tuning, _water.SurfaceJumpSpeed, out Vector2 proposed, out bool launched);
        if (status != MovementAvailability.Known) throw new ContactQueryFailure(status);
        bool bedClipped = false;
        if (proposed.X < from.Position.Y)
        {
            // A rejected seat can leave the feet below the bed within StepHeight. Water motion still treats
            // certified axis support as a floor, without retaining land footing or waiting for another crossing.
            SupportSample bed = _queries.FindSupport(new(new(feet.X, feet.Z), feet.Y,
                settings.FootRadiusFraction * tuning.CapsuleRadius, tuning.StepHeight, 0,
                MathF.Cos(continuation.TractionGate)));
            bedClipped = bed.Status is SupportStatus.Walkable or SupportStatus.Steep &&
                (double)bed.Height + bed.HeightError >= feet.Y;
            if (bedClipped) proposed = new(from.Position.Y, MathF.Max(0, proposed.Y));
        }
        if (launched)
        {
            _state.Swimming = false;
            _state.WaterExcursion = WaterExcursionState.AirborneFromWater;
            PressConsumed = true;
        }
        AirStepResult step = AirPass.StepWithMedium(feet, continuation.HorizontalVelocity, proposed.Y, time, 0,
            tuning, settings, continuation.TractionGate, continuation.Queries, continuation.Budget,
            clamp, this, proposed.X - from.Position.Y, buoyant: from.Swimming && !launched);
        if (step.RemainingTime > 0)
        {
            status = SurfaceWaterMotion.TryPropose(from, !from.Swimming, press, point,
                time - step.RemainingTime, tuning, _water.SurfaceJumpSpeed, out Vector2 consumed, out _);
            if (status != MovementAvailability.Known) throw new ContactQueryFailure(status);
            if (bedClipped) consumed.Y = MathF.Max(0, consumed.Y);
            step = step with
            {
                VerticalVelocity = step.VerticalVelocity == proposed.Y ? consumed.Y : step.VerticalVelocity,
                FallSpeedAtContact = step.Outcome == AirOutcome.Landed ? MathF.Max(0, -consumed.Y) : step.FallSpeedAtContact,
            };
        }
        return step;
    }
}
