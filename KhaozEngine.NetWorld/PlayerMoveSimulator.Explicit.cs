using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.NetWorld;

public sealed partial class PlayerMoveSimulator
{
    readonly ExplicitPlayerMovement? explicitMovement;
    PlayerMoveReadScope? explicitRead;
    int explicitReadActive;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        Func<float, float, float, MovementMedium>? medium, SamplerSpace samplerSpace,
        ExplicitPlayerMovement? explicitMovement)
        : this(groundHeight, tuning, groundNormal, bounds, physics, medium, samplerSpace)
    {
        this.explicitMovement = explicitMovement;
    }

    public MovementStepOutcome? LastExplicitOutcome { get; private set; }

    /// <summary>Acquires and classifies a cold basis. Hold this scope across Step, Predict or
    /// Reconcile and pure result publication. Null means deliberately unconfigured legacy movement.</summary>
    public PlayerMoveReadScope? BeginExplicitRead(in PlayerMoveState basis)
    {
        if (explicitMovement is null) return null;
        if (Interlocked.CompareExchange(ref explicitReadActive, 1, 0) != 0)
            throw new InvalidOperationException("Explicit player reads cannot overlap or recurse.");
        MovementQueryLease? queries = null;
        try
        {
            MovementStepOutcome outcome = MovementStepOutcome.FrameMismatch;
            if (basis.FrameAnchor == new Vector2(Frame.Anchor.X, Frame.Anchor.Z))
            {
                MovementQueryScope scope = explicitMovement.Scope(basis, Frame);
                if (scope.Frame.Frame == Frame && scope.Frame.PhysicsOrigin == new Vector3(Frame.Anchor.X, 0, Frame.Anchor.Z))
                {
                    outcome = MovementStepOutcome.EnvironmentInvalid;
                    if (scope.CurrentSpace is null)
                    {
                        MovementAvailability status = explicitMovement.Environment.TryAcquire(scope, out queries);
                        outcome = status == MovementAvailability.Invalid ? MovementStepOutcome.EnvironmentInvalid
                            : MovementStepOutcome.EnvironmentUnresolved;
                        if (status == MovementAvailability.Known && queries is not null)
                        {
                            IPhysicsWorld? expected = physics is IPhysicsWorldQueryView selected ? selected.SourceWorld : physics;
                            if (ReferenceEquals(queries.Id.SourceWorld, expected))
                            {
                                var framed = new FramedMovementState(basis.Move, queries.Frame, null);
                                outcome = ExplicitCharacterMovement.ValidatePlacement(framed, tuning,
                                    explicitMovement.Water, queries).Outcome;
                            }
                            else outcome = MovementStepOutcome.EnvironmentInvalid;
                        }
                    }
                }
            }
            explicitRead = new PlayerMoveReadScope(queries, outcome, EndExplicitRead);
            return explicitRead;
        }
        catch
        {
            try { queries?.Dispose(); }
            finally { Volatile.Write(ref explicitReadActive, 0); }
            throw;
        }
    }

    void EndExplicitRead(PlayerMoveReadScope read)
    {
        if (!ReferenceEquals(explicitRead, read))
            throw new InvalidOperationException("The explicit player read owner changed.");
        explicitRead = null;
        Volatile.Write(ref explicitReadActive, 0);
    }

    PlayerMoveState StepExplicit(in PlayerMoveState state, in MoveCommand command, float dt)
    {
        PlayerMoveReadScope? read = explicitRead;
        if (read is null) return HoldExplicit(state, MovementStepOutcome.EnvironmentUnresolved);
        read.AssertUsable();
        if (!read.BasisValid || read.Queries is not { } queries) return HoldExplicit(state, read.Outcome);
        if (state.FrameAnchor != new Vector2(Frame.Anchor.X, Frame.Anchor.Z) || queries.Frame.Frame != Frame)
            return HoldExplicit(state, MovementStepOutcome.FrameMismatch);
        try
        {
            // Replay reconstructs each corrected pose from the null-hint witness. No predicted
            // selection or local handle survives through the player state or this adapter.
            var framed = new FramedMovementState(state.Move, queries.Frame, null);
            MovementStepResult result = ExplicitCharacterMovement.Step(framed, command, dt, tuning,
                explicitMovement!.Water, queries);
            LastExplicitOutcome = result.Outcome;
            return new PlayerMoveState
            {
                Move = result.State.State,
                TeleportEpoch = state.TeleportEpoch,
                FrameAnchor = state.FrameAnchor
            };
        }
        catch (ArgumentException) { return HoldExplicit(state, MovementStepOutcome.EnvironmentInvalid); }
    }

    PlayerMoveState HoldExplicit(in PlayerMoveState state, MovementStepOutcome outcome)
    {
        LastExplicitOutcome = outcome;
        PlayerMoveState held = state;
        held.Move = MovementStepResult.HoldState(state.Move);
        return held;
    }
}
