using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>One successful cold placement, borrowed by its player read for its first Step only.
/// Binding identity fixes tuning, water policy and composition. The exact live lease fixes the captured
/// selection and generations. The controller still authenticates that lease and rebuilds selection.
/// Opaque and process-local. Copies share one use under the issuing lease and cannot renew its lifetime.</summary>
public readonly struct MovementColdPlacement
{
    readonly EffectiveMovementBinding? _binding;
    readonly MovementQueryLease? _queries;
    readonly MovementBoundary? _boundary;
    readonly FramedMovementState _basis;
    readonly MovementSelection _selection;
    readonly long _version;

    internal MovementColdPlacement(EffectiveMovementBinding binding, MovementQueryLease queries,
        MovementBoundary? boundary, in FramedMovementState basis, in MovementSelection placementSelection)
    {
        _binding = binding;
        _queries = queries;
        _boundary = boundary;
        _basis = basis;
        _selection = placementSelection;
        _version = queries.IssueColdPlacement();
    }

    internal MovementSelection? Selection(EffectiveMovementBinding binding, in FramedMovementState original,
        in FramedMovementState prepared, MovementQueryLease queries, MovementBoundary? boundary) =>
        ReferenceEquals(_queries, queries) && queries.TryConsumeColdPlacement(_version) &&
        ReferenceEquals(_binding, binding) && ReferenceEquals(_boundary, boundary) && Same(original) && Same(prepared) ? _selection : null;

    bool Same(in FramedMovementState other) => _basis.Selection == other.Selection &&
        _basis.Frame.Frame == other.Frame.Frame && _basis.Frame.Epoch == other.Frame.Epoch &&
        Same(_basis.Frame.PhysicsOrigin, other.Frame.PhysicsOrigin) && Same(_basis.State, other.State);

    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Same(Vector2 a, Vector2 b) => Same(a.X, b.X) && Same(a.Y, b.Y);
    static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

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
        Same(a.TimeoutRemaining, b.TimeoutRemaining) && a.EndReason == b.EndReason && a.Kind == b.Kind &&
        Same(a.DistanceRemaining, b.DistanceRemaining) && a.ConsecutiveBlockedTicks == b.ConsecutiveBlockedTicks;
}
