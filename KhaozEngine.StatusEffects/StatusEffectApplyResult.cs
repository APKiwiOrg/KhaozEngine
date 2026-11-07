namespace KhaozEngine.StatusEffects;

public enum StatusEffectApplyResult : byte
{
    Applied,
    Refreshed,
    Stacked,
    UnknownDefinition,
    ActorFull
}
