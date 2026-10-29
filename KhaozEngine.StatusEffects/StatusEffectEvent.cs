namespace KhaozEngine.StatusEffects;

public enum StatusEffectEventKind : byte
{
    Applied,
    Refreshed,
    Stacked,
    Periodic,
    Ended
}

public enum StatusEffectEndReason : byte
{
    None,
    Expired,
    Removed,
    Cleared
}

/// <summary>A queued lifecycle or periodic notification. Periodic ticks retain their due time.</summary>
public readonly record struct StatusEffectEvent(StatusEffectEventKind Kind, long Actor,
    StatusEffectId Effect, long Source, int Stacks, int Magnitude, long Tick, StatusEffectEndReason EndReason);
