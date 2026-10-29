using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.StatusEffects;

/// <summary>
/// Supplies effect definitions. Each id's definition must remain the same for the lifetime of any
/// <see cref="StatusEffectBook"/> using this catalog. Books retain a captured definition per instance,
/// including for matching and reapplication.
/// </summary>
public interface IStatusEffectCatalog
{
    bool TryGet(StatusEffectId id, [MaybeNullWhen(false)] out StatusEffectDefinition definition);
}
