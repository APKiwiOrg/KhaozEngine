using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundMoveContext
{
    /// <summary>Runs the exact explicit kernel used by the motion probe. The caller retains queries
    /// through pure result publication. No legacy terrain/medium callback is consulted.</summary>
    public MovementStepResult StepExplicit(in FramedMovementState state, Vector2 worldDirection, bool run,
        ExplicitMovementProfile profile, MovementQueryLease queries, MovementBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(queries);
        if (_stepping) throw new InvalidOperationException("Ground movement steps cannot overlap or recurse.");
        try
        {
            if (Physics is null || MovementQueries is null || !ReferenceEquals(queries.Id.SourceWorld, Physics) ||
                !queries.UsesPhysicsView(MovementQueries) || !string.Equals(profile.BoundaryIdentity,
                    boundary?.SemanticIdentity ?? "", StringComparison.Ordinal))
                return ExplicitHold(state, MovementStepOutcome.EnvironmentInvalid);
            _stepping = true;
            return ExplicitCharacterMovement.StepTowards(state, worldDirection, run, profile.StepSeconds,
                profile.Tuning, profile.Water, queries, boundary);
        }
        catch (InvalidOperationException) { return ExplicitHold(state, MovementStepOutcome.EnvironmentUnresolved); }
        finally { _stepping = false; }
    }

    /// <summary>Proves only directed motion, not area permission or whole-search reachability.
    /// Retain queries through recording the result. No native bake or tiling is performed here.</summary>
    public ExplicitMotionProof ProbeExplicitMotion(in FramedMovementState state, Vector3 targetCentre,
        bool targetSwimming, ExplicitMovementProfile profile, MovementQueryLease queries, MovementBoundary? boundary = null) =>
        ExplicitMotionProbe.Prove(this, state, targetCentre, targetSwimming, profile, queries, boundary);

    static MovementStepResult ExplicitHold(in FramedMovementState state, MovementStepOutcome outcome) =>
        state.IsValid ? new(new(MovementStepResult.HoldState(state.State), state.Frame, null), outcome) : new(state, outcome);
}
