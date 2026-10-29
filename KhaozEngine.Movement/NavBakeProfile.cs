using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One named capsule class in a baked navigation set. The name is 1 to 64 characters from <c>a</c> to
/// <c>z</c>, <c>0</c> to <c>9</c>, <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>. The probe overwrites
/// <see cref="MoveTuning.WalkSpeed"/>, <see cref="MoveTuning.RunSpeed"/> and <see cref="MoveTuning.AirMomentum"/>
/// for supported proofs. Those fields enter identity only when <see cref="MaxDropMetres"/> is positive.</summary>
public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas)
{
    private float _maxDropMetres;

    /// <summary>Maximum certified unpowered descent in metres, finite and nonnegative. Default zero preserves
    /// the supported-edge identity. Positive values require non-aquatic OrdinaryContact and retain actual
    /// walk speed, run speed and air momentum in format 3 identity. Either signed zero is stored as positive zero.</summary>
    public float MaxDropMetres { get => _maxDropMetres; init => _maxDropMetres = value == 0 ? 0 : value; }

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
        if (!float.IsFinite(MaxDropMetres) || MaxDropMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDropMetres), "Drop allowance must be finite and nonnegative.");
        if (MaxDropMetres > 0 && (kind != EffectiveMovementBindingKind.OrdinaryContact || Aquatic))
            throw new NotSupportedException("Drop navigation requires non-aquatic OrdinaryContact movement.");
        if (kind == EffectiveMovementBindingKind.OrdinaryContact && Aquatic)
            throw new NotSupportedException(DeferredAquaticMessage);
        return kind;
    }
}
