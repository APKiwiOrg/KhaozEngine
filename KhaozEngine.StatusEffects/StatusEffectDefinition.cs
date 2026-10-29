using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.StatusEffects;

/// <summary>Immutable, game-authored rules for one timed effect.</summary>
public sealed class StatusEffectDefinition
{
    public StatusEffectDefinition(StatusEffectId id, int durationTicks,
        StatusEffectScope scope = StatusEffectScope.PerActor, int maxStacks = 1, int periodTicks = 0,
        StatusEffectPolarity polarity = StatusEffectPolarity.Beneficial, bool shown = true,
        string iconKey = "", IEnumerable<StatusEffectModifier>? modifiers = null)
    {
        if (!id.IsValid)
            throw new ArgumentException("An effect id must be nonzero.", nameof(id));
        if (durationTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (maxStacks is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxStacks));
        if (periodTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(periodTicks));
        ArgumentNullException.ThrowIfNull(iconKey);

        Id = id;
        DurationTicks = durationTicks;
        Scope = scope;
        MaxStacks = maxStacks;
        PeriodTicks = periodTicks;
        Polarity = polarity;
        Shown = shown;
        IconKey = iconKey;
        Modifiers = modifiers?.ToImmutableArray() ?? ImmutableArray<StatusEffectModifier>.Empty;
    }

    public StatusEffectId Id { get; }
    public int DurationTicks { get; }
    public StatusEffectScope Scope { get; }
    public int MaxStacks { get; }
    public int PeriodTicks { get; }
    public StatusEffectPolarity Polarity { get; }
    public bool Shown { get; }
    public string IconKey { get; }
    public ImmutableArray<StatusEffectModifier> Modifiers { get; }
}
