using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

sealed class TileInteractionPolicyTargets(
    ITileTargets inner,
    Func<long, TileInteractionReachPolicy> policy) : ITileTargets
{
    public bool TryGetFootprint(long target, out TileRect footprint, out int plane) =>
        inner.TryGetFootprint(target, out footprint, out plane);

    public bool TryGetAimPoint(long target, out Vector2 tilePlanar, out int plane) =>
        inner.TryGetAimPoint(target, out tilePlanar, out plane);

    public TileInteractionReachPolicy GetInteractionReachPolicy(long target) => policy(target);
}
