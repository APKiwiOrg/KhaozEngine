using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal sealed record NavProfileIdentity(EffectiveMovementBindingKind Kind, MoveTuning Tuning, float MaxDropMetres = 0,
    bool AllowTerrainDescents = false)
{
    public float PlacementOffset => 0f;
}
