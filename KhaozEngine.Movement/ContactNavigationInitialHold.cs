using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One exact canonical Hold input. Tokens bind the immutable composition, footprint and authenticated
/// provider snapshot. Scope values are logical evidence, never a lease, pin or witness instance.</summary>
internal readonly struct ContactNavigationHoldKey(in FramedMovementState initial, in MoveTuning tuning,
    in MovementQueryScope scope, MovementQueryLeaseId basis, float seconds, int maxSteps,
    object profileIdentity, ContactNavigationSource source, object providerBasisIdentity)
{
    readonly MoveState _state = initial.State;
    readonly MovementFrameDescriptor _frame = initial.Frame;
    readonly MoveTuning _tuning = tuning;
    readonly MovementQueryScope _scope = scope;
    readonly MovementQueryLeaseId _basis = basis;
    readonly int _seconds = BitConverter.SingleToInt32Bits(seconds);
    readonly int _maxSteps = maxSteps;
    readonly object _profile = profileIdentity;
    readonly ContactNavigationSource _source = source;
    readonly object _providers = providerBasisIdentity;

    internal bool Matches(in ContactNavigationHoldKey other) =>
        ReferenceEquals(_profile, other._profile) && ReferenceEquals(_providers, other._providers) &&
        _source == other._source && _basis == other._basis && _seconds == other._seconds && _maxSteps == other._maxSteps &&
        Same(_frame, other._frame) && Same(_scope, other._scope) && Same(_state, other._state) && Same(_tuning, other._tuning);

    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Same(Vector2 a, Vector2 b) => Same(a.X, b.X) && Same(a.Y, b.Y);
    static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(MovementFrameDescriptor a, MovementFrameDescriptor b) =>
        a.Frame == b.Frame && a.Epoch == b.Epoch && Same(a.PhysicsOrigin, b.PhysicsOrigin);
    static bool Same(MovementQueryScope a, MovementQueryScope b) =>
        Same(a.Min, b.Min) && Same(a.Max, b.Max) && Same(a.MaxRise, b.MaxRise) && Same(a.MaxDrop, b.MaxDrop) &&
        a.WorldId == b.WorldId && a.CurrentSpace == b.CurrentSpace && a.Identity == b.Identity && Same(a.Frame, b.Frame);

    static bool Same(in MoveState a, in MoveState b) =>
        Same(a.Position, b.Position) && Same(a.VerticalVelocity, b.VerticalVelocity) && a.Grounded == b.Grounded &&
        Same(a.TimeSinceGrounded, b.TimeSinceGrounded) && Same(a.JumpBufferRemaining, b.JumpBufferRemaining) &&
        a.Swimming == b.Swimming && a.WaterExcursion == b.WaterExcursion && a.SpeedScaleStorageBits == b.SpeedScaleStorageBits &&
        Same(a.HorizontalVelocity, b.HorizontalVelocity) && Same(a.CommandedVelocity, b.CommandedVelocity) &&
        Same(a.ClimbRate, b.ClimbRate) && Same(a.ClimbRateEwma, b.ClimbRateEwma) && Same(a.StepDeltaY, b.StepDeltaY) &&
        Same(a.LandingImpactSpeed, b.LandingImpactSpeed) && a.SupportGranted == b.SupportGranted &&
        Same(a.FacingYaw, b.FacingYaw) && Same(a.Commitment, b.Commitment);

    static bool Same(in MovementCommitment a, in MovementCommitment b) =>
        a.Sequence == b.Sequence && a.Phase == b.Phase && Same(a.Direction, b.Direction) &&
        Same(a.HorizontalSpeed, b.HorizontalSpeed) && Same(a.VerticalSpeed, b.VerticalSpeed) && Same(a.Gravity, b.Gravity) &&
        Same(a.PreparationRemaining, b.PreparationRemaining) && Same(a.RecoveryRemaining, b.RecoveryRemaining) &&
        Same(a.TimeoutRemaining, b.TimeoutRemaining) && a.EndReason == b.EndReason;

    static bool Same(in MoveTuning a, in MoveTuning b) =>
        Same(a.WalkSpeed, b.WalkSpeed) && Same(a.RunSpeed, b.RunSpeed) && Same(a.CapsuleHalfHeight, b.CapsuleHalfHeight) &&
        Same(a.MaxSlopeRadians, b.MaxSlopeRadians) && Same(a.CapsuleRadius, b.CapsuleRadius) && Same(a.Gravity, b.Gravity) &&
        Same(a.JumpSpeed, b.JumpSpeed) && Same(a.MaxFallSpeed, b.MaxFallSpeed) && Same(a.CoyoteTime, b.CoyoteTime) &&
        Same(a.JumpBuffer, b.JumpBuffer) && Same(a.AirControl, b.AirControl) && Same(a.GroundedEpsilon, b.GroundedEpsilon) &&
        Same(a.StepHeight, b.StepHeight) && Same(a.MaxStepClimbSpeed, b.MaxStepClimbSpeed) &&
        Same(a.WadeStartDepthFraction, b.WadeStartDepthFraction) && Same(a.WadeEndDepthFraction, b.WadeEndDepthFraction) &&
        Same(a.WadeMinSpeedScale, b.WadeMinSpeedScale) && Same(a.SwimEnterDepthFraction, b.SwimEnterDepthFraction) &&
        Same(a.SwimExitDepthFraction, b.SwimExitDepthFraction) && Same(a.SwimSpeed, b.SwimSpeed) &&
        Same(a.SwimSurfaceSubmersionFraction, b.SwimSurfaceSubmersionFraction) &&
        Same(a.SwimBuoyancyStiffness, b.SwimBuoyancyStiffness) && a.AirMomentum == b.AirMomentum &&
        Same(a.AirBrakeAccel, b.AirBrakeAccel) && Same(a.FacingTurnSpeed, b.FacingTurnSpeed) &&
        Same(a.TractionHysteresisRadians, b.TractionHysteresisRadians) &&
        Same(a.SlideFrictionRampRadians, b.SlideFrictionRampRadians) && Same(a.StrafeSpeedScale, b.StrafeSpeedScale) &&
        Same(a.BackpedalSpeedScale, b.BackpedalSpeedScale) && a.BackpedalAllowsRun == b.BackpedalAllowsRun &&
        a.Controller == b.Controller && Same(a.FootRadiusFraction, b.FootRadiusFraction) &&
        a.Water.Mode == b.Water.Mode && Same(a.Water.SurfaceJumpSpeed, b.Water.SurfaceJumpSpeed);
}

/// <summary>A source-local successful Hold's numeric outputs. Struct assignment preserves all carried state,
/// including private numeric storage. No selection, binding, support, lease or continuity survives here.</summary>
internal sealed class ContactNavigationInitialHold
{
    ContactNavigationHoldKey _key;
    MoveState _state;
    MovementFrameDescriptor _frame;
    Vector3 _feet;
    MovementStepOutcome _outcome;
    bool _present;

    internal bool TryGet(in ContactNavigationHoldKey key, out MoveState state, out MovementFrameDescriptor frame,
        out Vector3 feet, out MovementStepOutcome outcome)
    {
        state = default;
        frame = default;
        feet = default;
        outcome = default;
        if (!_present || !_key.Matches(key)) return false;
        state = _state;
        frame = _frame;
        feet = _feet;
        outcome = _outcome;
        return true;
    }

    internal void Remember(in ContactNavigationHoldKey key, in MoveState state, in MovementFrameDescriptor frame,
        Vector3 feet, MovementStepOutcome outcome)
    {
        _key = key;
        _state = state;
        _frame = frame;
        _feet = feet;
        _outcome = outcome;
        _present = true;
    }

    internal void Reset()
    {
        _present = false;
        _key = default;
        _state = default;
        _frame = default;
        _feet = default;
        _outcome = default;
    }
}
