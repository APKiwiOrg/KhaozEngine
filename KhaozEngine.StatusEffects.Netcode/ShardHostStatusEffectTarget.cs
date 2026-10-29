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

    public bool TryPublish(long actor, in ActiveStatusEffects state)
    {
        if (!_host.TryGetOwner(actor, out var cell, out var entity) || !cell.World.IsAlive(entity))
            return false;
        cell.World.Set(entity, state);
        return true;
    }
}
