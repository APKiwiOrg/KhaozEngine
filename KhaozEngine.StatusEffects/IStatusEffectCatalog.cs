using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.StatusEffects;

public interface IStatusEffectCatalog
{
    bool TryGet(StatusEffectId id, [MaybeNullWhen(false)] out StatusEffectDefinition definition);
}
