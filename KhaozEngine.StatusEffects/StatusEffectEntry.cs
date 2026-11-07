namespace KhaozEngine.StatusEffects;

/// <summary>The current duration window and stack count of one effect instance.</summary>
public readonly record struct StatusEffectEntry(
    StatusEffectId Effect, long Source, int Stacks, long StartTick, long EndTick);
