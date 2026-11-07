using System;

namespace KhaozEngine.Locomotion;

public sealed partial class EffectiveMovementBinding
{
    /// <summary>Validates tuning and controller selection before allocating a cell.
    /// Factory-specific environments, views and explicit water policies are validated by binding construction.</summary>
    public static void Preflight(in MoveTuning tuning, EffectiveMovementBindingKind kind)
    {
        switch (kind)
        {
            case EffectiveMovementBindingKind.Legacy:
                MovementConfigurationValidation.ValidateLegacy(tuning);
                if (tuning.Controller != MovementControllerKind.Legacy)
                    throw new MovementConfigurationException(MovementConfigurationRefusal.LegacyControllerRequired, "A Legacy binding requires the Legacy controller.", nameof(tuning))
                    { ActualController = tuning.Controller, ExpectedController = MovementControllerKind.Legacy };
                break;
            case EffectiveMovementBindingKind.OrdinaryContact:
                MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water);
                if (tuning.Controller != MovementControllerKind.Contact)
                    throw new MovementConfigurationException(MovementConfigurationRefusal.ContactControllerRequired, "An ordinary contact binding requires the Contact controller.", nameof(tuning))
                    { ActualController = tuning.Controller, ExpectedController = MovementControllerKind.Contact };
                break;
            case EffectiveMovementBindingKind.PublicExplicit:
                if (tuning.Controller == MovementControllerKind.Contact)
                    throw new MovementConfigurationException(MovementConfigurationRefusal.ConflictingMovementSelections,
                        "A public explicit environment and the Contact selector are two movement selections.", nameof(tuning))
                    { ActualController = tuning.Controller, RequestedKind = EffectiveMovementBindingKind.PublicExplicit };
                MovementConfigurationValidation.ValidateContactGeometry(tuning);
                if (!ExplicitCharacterMovement.Valid(tuning))
                    throw MovementKernelValidation.FindInvalid(tuning)!.Value.ToException("Public explicit movement requires valid kernel tuning.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
