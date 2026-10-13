using System;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    /// <summary>Samples certified frame-local water and applies the controller's tick-start swimmer classification
    /// for approach pacing. This does not authorize motion or placement. A refused point never grants swimming.</summary>
    public MovementWaterPoint SampleWaterForPacing(in FramedMovementState state, MovementQueryLease queries, out bool swimming)
    {
        swimming = false;
        var boundary = Boundary(queries, null);
        if (!state.IsValid || !ExplicitCharacterMovement.ValidCarriedState(state.State) || state.Frame != queries.Frame)
            return NoPacingWater(MovementAvailability.Invalid);
        if (!MovementFrameRebinding.TryRebind(state, state.Frame, out _))
            return NoPacingWater(MovementAvailability.Unresolved);
        bool beganTick = false;
        try
        {
            queries.BeginContactTick();
            beganTick = true;
            var status = queries.RebuildSelection(new(state.State, state.Frame, null), out var selection);
            if (status != MovementAvailability.Known) return NoPacingWater(status);
            var body = new MovementBodyQuery(state.State.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight,
                selection.Space, selection.Support);
            var leased = queries.Workspace.Queries;
            leased.Initialize(queries, Tuning, selection, new(selection.Space, body.Feet), _composition, Water.Mode, boundary);
            var feet = leased.KernelFeet(state.State.Position, Tuning);
            var point = leased.SampleMedium(feet)!.Value;
            if (point.Space != selection.Space) return NoPacingWater(MovementAvailability.Invalid);
            var classified = ContactController.ClassifyStartWater(state.State, point, feet, Tuning, Water, leased, _composition.Foot);
            queries.AssertCurrent();
            swimming = classified.Swimming;
            return point;
        }
        catch (ContactQueryFailure failure) { return NoPacingWater(failure.Availability); }
        catch (InvalidOperationException) { return NoPacingWater(MovementAvailability.Unresolved); }
        catch (ArgumentException) { return NoPacingWater(MovementAvailability.Invalid); }
        finally { if (beganTick) queries.EndContactTick(); }
    }

    static MovementWaterPoint NoPacingWater(MovementAvailability availability) => new(availability, default, null, false, 0, null);
}
