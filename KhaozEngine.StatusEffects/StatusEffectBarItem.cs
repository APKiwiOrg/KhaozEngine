namespace KhaozEngine.StatusEffects;

/// <summary>Presentation keys, instance state and countdown values for one status effect row.</summary>
public readonly record struct StatusEffectBarItem(
    StatusEffectId Effect, string IconKey, StatusEffectPolarity Polarity, long Source, int Stacks,
    double RemainingTicks, float Fraction);
