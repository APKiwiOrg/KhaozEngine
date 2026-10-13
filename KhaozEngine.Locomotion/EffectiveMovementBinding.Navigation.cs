using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

internal readonly record struct RestingAnchor(Vector3 Feet, SupportSample Support);

public sealed partial class EffectiveMovementBinding
{
    internal float PlacementOffset => _composition.PlacementOffset;

    internal MovementAvailability TryFindRestingAnchor(in FramedMovementState state,
        MovementQueryLease queries, out RestingAnchor anchor)
    {
        anchor = default;
        MovementBoundary? boundary = Boundary(queries, null);
        try
        {
            var unselected = new FramedMovementState(state.State, state.Frame, null);
            MovementAvailability status = queries.RebuildSelection(unselected, out MovementSelection selection);
            if (status != MovementAvailability.Known) return status;
            var contact = ExplicitCharacterMovement.PlacementQueries(state.State, Tuning, _composition,
                Water, queries, selection, boundary);
            Vector3 feet = contact.KernelFeet(state.State.Position, Tuning);
            SupportSample support = ContactPlacement.Find(contact, feet, Tuning, _composition,
                Tuning.StepHeight, Tuning.GroundedEpsilon, out _);
            queries.AssertCurrent();
            if (support.Status != SupportStatus.Walkable) return MovementAvailability.Known;
            float resting = _composition.RestingFeet(support.Height);
            float raw = state.State.Position.Y - Tuning.CapsuleHalfHeight;
            if (!float.IsFinite(resting) || (double)resting < (double)raw - Tuning.GroundedEpsilon ||
                (double)resting > (double)raw + Tuning.StepHeight) return MovementAvailability.Known;
            anchor = new(new(state.State.Position.X, resting, state.State.Position.Z), support);
            return MovementAvailability.Known;
        }
        catch (ContactQueryFailure failure) { return failure.Availability; }
        catch (InvalidOperationException) { return MovementAvailability.Unresolved; }
        catch (ArgumentException) { return MovementAvailability.Invalid; }
    }
}
