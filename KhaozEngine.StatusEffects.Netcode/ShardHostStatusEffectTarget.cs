using System;
using KhaozEngine.Sharding;

namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Publishes to the live entity in the actor's current owning cell.</summary>
public sealed class ShardHostStatusEffectTarget : IStatusEffectProjectionTarget
{
    private readonly ShardHost _host;

    public ShardHostStatusEffectTarget(ShardHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public StatusEffectPublishResult Publish(long actor, in ActiveStatusEffects state)
    {
        if (_host.TryGetOwner(actor, out var cell, out var entity) && cell.World.IsAlive(entity))
        {
            cell.World.Set(entity, state);
            return StatusEffectPublishResult.Published;
        }
        return _host.IsInHandoff(actor) ? StatusEffectPublishResult.Deferred : StatusEffectPublishResult.Gone;
    }
}
