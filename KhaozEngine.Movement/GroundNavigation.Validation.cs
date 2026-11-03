using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigation
{
    /// <summary>Requires the runtime binding to match this graph's effective profile. Call before using
    /// <see cref="Planner"/> or <see cref="AllowsSegment"/> directly. <see cref="MoveToRange"/> calls this door
    /// against its context's resolved binding before planning and prediction.</summary>
    /// <exception cref="NotSupportedException">Public explicit navigation profiles are not supported.</exception>
    /// <exception cref="ArgumentException">The binding kind or a retained profile input differs.</exception>
    public void Validate(EffectiveMovementBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Validate(binding.Kind, binding.Tuning, binding.Water, binding.PlacementOffset, nameof(binding));
    }

    /// <summary>Resolves Legacy or OrdinaryContact from <see cref="MoveTuning.Controller"/> and requires the same
    /// profile compatibility as <see cref="Validate(EffectiveMovementBinding)"/>. This convenience overload cannot
    /// see a public explicit binding. Use the binding overload when one is available.</summary>
    /// <exception cref="ArgumentException">The selection is malformed, or a retained profile input differs.</exception>
    public void Validate(in MoveTuning tuning)
    {
        EffectiveMovementBindingKind kind = new NavBakeProfile("", tuning, default).ResolveKind();
        Validate(kind, tuning, tuning.Water, 0, nameof(tuning));
    }

    private void Validate(EffectiveMovementBindingKind kind, in MoveTuning tuning, in WaterTraversalPolicy water,
        float placementOffset, string parameter)
    {
        if (kind == EffectiveMovementBindingKind.PublicExplicit)
            throw new NotSupportedException("Explicit navigation profiles are not supported yet. " +
                "See https://github.com/APKiwiOrg/KhaozEngine/issues/1389.");
        if (kind != Identity.Kind)
            throw new ArgumentException($"Movement binding Kind {kind} must equal the baked profile Kind {Identity.Kind}.", parameter);
        if (kind == EffectiveMovementBindingKind.Legacy)
        {
            if (tuning.CapsuleRadius != _tuning.Radius || tuning.CapsuleHalfHeight != _tuning.HalfHeight ||
                tuning.MaxSlopeRadians != _tuning.Slope || tuning.StepHeight != _tuning.Step)
                throw new ArgumentException("Movement geometry must equal the baked profile geometry.", parameter);
            if (Aquatic && (tuning.SwimEnterDepthFraction != _swim.Enter || tuning.SwimExitDepthFraction != _swim.Exit ||
                tuning.SwimSurfaceSubmersionFraction != _swim.Submersion))
                throw new ArgumentException("Swim fractions must equal the baked aquatic profile's.", parameter);
            return;
        }
        MoveTuning runtime = tuning with { Water = water };
        string? difference = NavBakeIdentity.FirstRuntimeDifference(Identity.Tuning, runtime);
        if (MaxDropMetres > 0) difference ??= NavBakeIdentity.FirstDropRuntimeDifference(Identity.Tuning, runtime);
        if (difference is not null) throw new ArgumentException($"Movement {difference}.", parameter);
        if (placementOffset != Identity.PlacementOffset)
            throw new ArgumentException("Movement PlacementOffset must equal the baked profile's.", parameter);
    }
}
