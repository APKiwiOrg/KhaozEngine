using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.StatusEffects;

/// <summary>A snapshot of definitions keyed by their unique effect identities.</summary>
public sealed class StatusEffectCatalog : IStatusEffectCatalog
{
    private readonly Dictionary<StatusEffectId, StatusEffectDefinition> _definitions = new();

    public StatusEffectCatalog(IEnumerable<StatusEffectDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (definition is null)
                throw new ArgumentException("Definitions must not contain null entries.", nameof(definitions));
            if (!_definitions.TryAdd(definition.Id, definition))
                throw new ArgumentException("Definitions must have unique effect ids.", nameof(definitions));
        }
    }

    public int Count => _definitions.Count;

    public bool TryGet(StatusEffectId id, [MaybeNullWhen(false)] out StatusEffectDefinition definition) =>
        _definitions.TryGetValue(id, out definition);
}
