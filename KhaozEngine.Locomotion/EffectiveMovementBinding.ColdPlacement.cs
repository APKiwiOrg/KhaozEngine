namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    /// <summary>Classifies an exact cold pose and issues an opaque, single-use placement certificate on success.
    /// Keep the certificate within the caller's read scope. Refusal returns a default certificate.</summary>
    public MovementStepResult CertifyPlayerPlacement(in FramedMovementState state, MovementQueryLease queries,
        out MovementColdPlacement certificate, MovementBoundary? boundary = null)
    {
        var captured = Boundary(queries, boundary);
        var result = ExplicitCharacterMovement.ValidatePlacement(state, Tuning, Water, queries, _composition,
            out var selection, captured);
        certificate = result.Outcome == MovementStepOutcome.Advanced
            ? new(this, queries, captured, state, selection) : default;
        return result;
    }

    /// <summary>Steps and publishes a player, consuming the supplied cold certificate on its issuing lease.
    /// Only an identical raw and prepared basis, binding, boundary and rebuilt selection reuse placement.
    /// Default, consumed or mismatched certificates take the ordinary StepPlayer validation path.</summary>
    public MovementStepResult StepPlayerWithPlacement(in FramedMovementState state, in MoveCommand command,
        float dt, MovementQueryLease queries, in MovementColdPlacement certificate, MovementBoundary? boundary = null)
    {
        var prepared = PlayerState(state);
        var captured = Boundary(queries, boundary);
        var selection = certificate.Selection(this, state, prepared, queries, captured);
        return PlayerMovementPublication.Canonicalize(ExplicitCharacterMovement.Step(prepared, command, dt,
            Tuning, Water, queries, _composition, captured, selection));
    }
}
