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
                    throw new ArgumentException("A Legacy binding requires the Legacy controller.", nameof(tuning));
                break;
            case EffectiveMovementBindingKind.OrdinaryContact:
                MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water);
                if (tuning.Controller != MovementControllerKind.Contact)
                    throw new ArgumentException("An ordinary contact binding requires the Contact controller.", nameof(tuning));
                break;
            case EffectiveMovementBindingKind.PublicExplicit:
                if (tuning.Controller == MovementControllerKind.Contact)
                    throw new ArgumentException("A public explicit environment and the Contact selector are two movement selections.", nameof(tuning));
                MovementConfigurationValidation.ValidateContactGeometry(tuning);
                if (!ExplicitCharacterMovement.Valid(tuning))
                    throw new ArgumentException("Public explicit movement requires valid kernel tuning.", nameof(tuning));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
