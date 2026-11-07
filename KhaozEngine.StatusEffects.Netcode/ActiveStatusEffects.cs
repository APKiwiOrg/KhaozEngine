using System.Collections.Immutable;
using KhaozEngine.Ecs;

namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>An immutable snapshot of the shown effect windows for one actor.</summary>
public readonly struct ActiveStatusEffects : IComponent
{
    private readonly ImmutableArray<StatusEffectEntry> _entries;

    /// <summary>The published entries. A default component or array is empty.</summary>
    public ImmutableArray<StatusEffectEntry> Entries
    {
        get => _entries.IsDefault ? ImmutableArray<StatusEffectEntry>.Empty : _entries;
        init => _entries = value;
    }

    public static ActiveStatusEffects Empty => default;
}
