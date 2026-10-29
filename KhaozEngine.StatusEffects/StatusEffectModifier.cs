namespace KhaozEngine.StatusEffects;

/// <summary>An integer contribution per stack. The game owns the channel and its units.</summary>
public readonly record struct StatusEffectModifier(int Channel, int Value);
