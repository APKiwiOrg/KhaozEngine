using System;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Shared configuration checks for legacy, dry direct and leased contact movement.</summary>
internal static class MovementConfigurationValidation
{
    internal static void ValidateLegacy(in MoveTuning tuning)
    {
        ValidateSelector(tuning);
        if (tuning.FootRadiusFraction != 0.5f || tuning.Water != default)
            throw new ArgumentException("Legacy movement requires default FootRadiusFraction and Water.", nameof(tuning));
    }

    internal static void ValidateContactGeometry(in MoveTuning tuning)
    {
        ValidateSelector(tuning);
        float fraction = tuning.FootRadiusFraction;
        if (!float.IsFinite(fraction) || fraction <= 0f || fraction > 1f)
            throw new ArgumentOutOfRangeException(nameof(tuning), "FootRadiusFraction must be finite and in (0, 1].");
        ShellGeometry.Validate(tuning, new GroundCoreSettings(fraction));
    }

    internal static void ValidateDryDirect(in MoveTuning tuning, IPhysicsWorld? physics)
    {
        ValidateContactGeometry(tuning);
        if (physics is null) return;
        if (physics is not IPhysicsQueryLeaseSource)
            throw new NotSupportedException("Contact movement requires IPhysicsQueryLeaseSource.");
        if (physics is not IPhysicsSupportFootprint)
            throw new NotSupportedException("Contact movement requires IPhysicsSupportFootprint.");
    }

    internal static void ValidateOrdinaryContact(in MoveTuning tuning, IPhysicsWorldQueryView physics,
        bool hasRegistration, bool hasBoundary, bool hasMediumDelegate, bool hasWaterField)
    {
        ValidateExplicit(tuning, tuning.Water);
        ArgumentNullException.ThrowIfNull(physics);
        if (!hasRegistration || !hasBoundary)
            throw new ArgumentException("Ordinary contact movement requires a registration and a MovementBoundary.");
        if (hasMediumDelegate && !hasWaterField)
            throw new ArgumentException("An ordinary contact medium delegate requires IMovementWaterField.");
        ValidateDryDirect(tuning, physics);
        if (physics is not IPhysicsCapsuleContacts)
            throw new NotSupportedException("Ordinary contact movement requires IPhysicsCapsuleContacts.");
        if (physics is not IPhysicsCapsuleSweep)
            throw new NotSupportedException("Ordinary contact movement requires IPhysicsCapsuleSweep.");
    }

    internal static void ValidateExplicit(in MoveTuning tuning, in WaterTraversalPolicy water)
    {
        ValidateContactGeometry(tuning);
        if (!ExplicitCharacterMovement.Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
            throw new ArgumentException("Contact movement requires valid explicit kernel tuning and non-Legacy water.", nameof(tuning));
        if (tuning.Water != default && tuning.Water != water)
            throw new ArgumentException("MoveTuning.Water conflicts with the supplied explicit water policy.", nameof(tuning));
    }

    static void ValidateSelector(in MoveTuning tuning)
    {
        if (tuning.Controller is not (MovementControllerKind.Legacy or MovementControllerKind.Contact))
            throw new ArgumentOutOfRangeException(nameof(tuning), "MovementControllerKind must be Legacy or Contact.");
    }
}
