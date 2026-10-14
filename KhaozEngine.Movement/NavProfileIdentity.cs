using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal sealed record NavProfileIdentity(EffectiveMovementBindingKind Kind, MoveTuning Tuning)
{
    public float PlacementOffset => 0f;
}
