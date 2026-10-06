using System;

namespace KhaozEngine.Locomotion;

/// <summary>Stable canonical selection. Discard it after correction, restore or handoff and rebuild under a lease.</summary>
public readonly record struct MovementSelection
{
    public MovementSpaceKey Space { get; }
    public MovementSupportKey? Support { get; }
    public MovementQueryIdentity Identity { get; }
    public bool IsValid => Space.IsValid && Identity.IsValid &&
        (Support is null || Support.Value.IsValid && string.Equals(Support.Value.WorldId, Space.WorldId, StringComparison.Ordinal));
    public MovementSelection(MovementSpaceKey space, MovementSupportKey? support, MovementQueryIdentity identity)
    {
        Space = space;
        Support = support;
        Identity = identity;
        MovementEnvironmentValidation.Require(IsValid, nameof(space));
    }
}

/// <summary>Kinematic state with its recorded coordinate origin. Construction never relabels or converts positions.</summary>
public readonly record struct FramedMovementState
{
    public MoveState State { get; }
    public MovementFrameDescriptor Frame { get; }
    public MovementSelection? Selection { get; }
    public bool IsValid => MovementEnvironmentValidation.Finite(State.Position) && float.IsFinite(State.VerticalVelocity) &&
        Frame.IsValid && (Selection is null || Selection.Value.IsValid);
    public FramedMovementState(MoveState state, MovementFrameDescriptor frame, MovementSelection? selection)
    {
        State = state;
        Frame = frame;
        Selection = selection;
        MovementEnvironmentValidation.Require(IsValid, nameof(state));
    }
}
