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
            throw new MovementConfigurationException(tuning.FootRadiusFraction != 0.5f
                ? MovementConfigurationRefusal.LegacyFootRadiusNondefault : MovementConfigurationRefusal.LegacyWaterNondefault,
                "Legacy movement requires default FootRadiusFraction and Water.", nameof(tuning))
            {
                FieldName = tuning.FootRadiusFraction != 0.5f ? nameof(tuning.FootRadiusFraction) : nameof(tuning.Water),
                ActualNumber = tuning.FootRadiusFraction != 0.5f ? tuning.FootRadiusFraction : null,
                ExpectedNumber = tuning.FootRadiusFraction != 0.5f ? 0.5 : null,
                ActualWater = tuning.FootRadiusFraction != 0.5f ? null : tuning.Water,
                ExpectedWater = tuning.FootRadiusFraction != 0.5f ? null : default(WaterTraversalPolicy)
            };
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
            throw new MovementCapabilityException(MovementConfigurationRefusal.QueryLeaseSourceRequired, "Contact movement requires IPhysicsQueryLeaseSource.")
            { RequiredCapability = typeof(IPhysicsQueryLeaseSource) };
        if (physics is not IPhysicsSupportFootprint)
            throw new MovementCapabilityException(MovementConfigurationRefusal.SupportFootprintRequired, "Contact movement requires IPhysicsSupportFootprint.")
            { RequiredCapability = typeof(IPhysicsSupportFootprint) };
    }

    internal static void ValidateOrdinaryContact(in MoveTuning tuning, IPhysicsWorldQueryView physics,
        bool hasRegistration, bool hasBoundary, bool hasMediumDelegate, bool hasWaterField)
    {
        ValidateExplicit(tuning, tuning.Water);
        ArgumentNullException.ThrowIfNull(physics);
        if (!hasRegistration || !hasBoundary)
            throw new MovementConfigurationException(!hasRegistration ? MovementConfigurationRefusal.RegistrationRequired
                : MovementConfigurationRefusal.BoundaryRequired,
                "Ordinary contact movement requires a registration and a MovementBoundary.")
            { RequiredCapability = !hasRegistration ? typeof(OrdinaryMovementRegistration) : typeof(MovementBoundary) };
        if (hasMediumDelegate && !hasWaterField)
            throw new MovementConfigurationException(MovementConfigurationRefusal.WaterFieldRequired,
                "An ordinary contact medium delegate requires IMovementWaterField.")
            { RequiredCapability = typeof(IMovementWaterField) };
        ValidateDryDirect(tuning, physics);
        if (physics is not IPhysicsCapsuleContacts)
            throw new MovementCapabilityException(MovementConfigurationRefusal.CapsuleContactsRequired, "Ordinary contact movement requires IPhysicsCapsuleContacts.")
            { RequiredCapability = typeof(IPhysicsCapsuleContacts) };
        if (physics is not IPhysicsCapsuleSweep)
            throw new MovementCapabilityException(MovementConfigurationRefusal.CapsuleSweepRequired, "Ordinary contact movement requires IPhysicsCapsuleSweep.")
            { RequiredCapability = typeof(IPhysicsCapsuleSweep) };
    }

    internal static void ValidateExplicit(in MoveTuning tuning, in WaterTraversalPolicy water)
    {
        ValidateContactGeometry(tuning);
        if (!ExplicitCharacterMovement.Valid(tuning) || !water.IsValid || water.Mode == WaterTraversalMode.Legacy)
        {
            const string message = "Contact movement requires valid explicit kernel tuning and non-Legacy water.";
            if (MovementKernelValidation.FindInvalid(tuning) is { } finding) throw finding.ToException(message);
            // Internal invariant: the immutable policy is valid by default and every constructor validates it.
            if (!water.IsValid) throw new ArgumentException(message, nameof(tuning));
            throw new MovementConfigurationException(MovementConfigurationRefusal.ExplicitWaterModeRequired, message, nameof(tuning))
            { ActualMode = water.Mode };
        }
        if (tuning.Water != default && tuning.Water != water)
            throw new MovementConfigurationException(MovementConfigurationRefusal.WaterPoliciesConflict,
                "MoveTuning.Water conflicts with the supplied explicit water policy.", nameof(tuning))
            { ExpectedWater = water, ActualWater = tuning.Water };
    }

    static void ValidateSelector(in MoveTuning tuning)
    {
        if (tuning.Controller is not (MovementControllerKind.Legacy or MovementControllerKind.Contact))
            throw new ArgumentOutOfRangeException(nameof(tuning), "MovementControllerKind must be Legacy or Contact.");
    }
}
