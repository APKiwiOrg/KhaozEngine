using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    /// <summary>Steps and publishes one player command. Use for proofs, forward prediction and replay alike.</summary>
    public MovementStepResult StepPlayer(in FramedMovementState state, in MoveCommand command, float dt,
        MovementQueryLease queries, MovementBoundary? boundary = null) => PlayerMovementPublication.Canonicalize(
            Step(PlayerState(state), command, dt, queries, boundary));

    /// <summary>World-space execution of the same publication policy, including certified Drop consumers.</summary>
    public MovementStepResult StepPlayerTowards(in FramedMovementState state, Vector2 direction, bool run, float dt,
        MovementQueryLease queries, MovementBoundary? boundary = null) => PlayerMovementPublication.Canonicalize(
            StepTowards(PlayerState(state), direction, run, dt, queries, boundary));

    static FramedMovementState PlayerState(in FramedMovementState state) =>
        new(PlayerMovementPublication.PrepareStep(state.State), state.Frame, state.Selection);
}
