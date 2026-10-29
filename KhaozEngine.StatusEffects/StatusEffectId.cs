namespace KhaozEngine.StatusEffects;

/// <summary>A game-defined effect identity. Zero is reserved for an invalid identity.</summary>
public readonly record struct StatusEffectId(int Value)
{
    public bool IsValid => Value != 0;
}
