using System;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Reconstructs canonical selection under a null-hint certificate without upgrading that certificate.</summary>
    public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
    {
        selection = default;
        AssertThread();
        // Readiness authorizes later selected-space queries. Never retain it through a failed attempt,
        // including failure before the producer call or an exception after an earlier successful rebuild.
        _selectionReady = false;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Witness.Scope.CurrentSpace.HasValue || !state.IsValid || state.Frame != Frame)
            return MovementAvailability.Invalid;
        if (!Witness.Scope.ContainsBounds(state.State.Position, state.State.Position))
            return MovementAvailability.Unresolved;
        try
        {
            AssertCurrent();
            var unselected = new FramedMovementState(state.State, state.Frame, null);
            MovementAvailability availability = _pin.RebuildSelection(unselected, out MovementSelection rebuilt);
            AssertCurrent();
            if (!MovementEnvironmentValidation.Availability(availability)) return MovementAvailability.Invalid;
            if (availability != MovementAvailability.Known) return availability;
            if (!rebuilt.IsValid || rebuilt.Identity != Identity || !SameWorld(rebuilt.Space) ||
                !state.State.Grounded && rebuilt.Support.HasValue) return MovementAvailability.Invalid;

            // Witness, acquisition hint and resource identity stay unchanged. This is lifecycle readiness,
            // not a membership cache. The producer still validates membership and transitions per query.
            selection = rebuilt;
            _selectionReady = true;
            return MovementAvailability.Known;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementAvailability.Stale; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
    }
}
