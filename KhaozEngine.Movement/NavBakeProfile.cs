using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One named capsule class in a baked navigation set. The name is 1 to 64 characters from <c>a</c> to
/// <c>z</c>, <c>0</c> to <c>9</c>, <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>. The probe overwrites
/// <see cref="MoveTuning.WalkSpeed"/>, <see cref="MoveTuning.RunSpeed"/> and <see cref="MoveTuning.AirMomentum"/>,
/// so those three never take part in the bake identity.</summary>
public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas)
{
    internal const string DeferredAquaticMessage =
        "Contact aquatic navigation is not supported yet. See https://github.com/APKiwiOrg/KhaozEngine/issues/1389.";

    /// <summary>Bakes the profile with <see cref="GroundProfileOptions.Aquatic"/>, so its float nodes follow the
    /// capture's sampled water. Part of the bake identity, so an aquatic profile never loads as a ground profile.
    /// Default false.</summary>
    public bool Aquatic { get; init; }

    internal EffectiveMovementBindingKind ResolveKind()
    {
        EffectiveMovementBindingKind kind = Tuning.Controller switch
        {
            MovementControllerKind.Legacy => EffectiveMovementBindingKind.Legacy,
            MovementControllerKind.Contact => EffectiveMovementBindingKind.OrdinaryContact,
            _ => throw new ArgumentOutOfRangeException(nameof(Tuning), "MovementControllerKind must be Legacy or Contact."),
        };
        EffectiveMovementBinding.Preflight(Tuning, kind);
        if (kind == EffectiveMovementBindingKind.OrdinaryContact && Aquatic)
            throw new NotSupportedException(DeferredAquaticMessage);
        return kind;
    }
}
