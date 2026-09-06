using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    // Desired state follows the stable player id rather than the recyclable connection slot. Values stay here after
    // application so a handoff or restore that temporarily loses the component is repaired on a later tick.
    private readonly Dictionary<long, sbyte> desiredSpeedScaleByNetId = new();

    private void SetDesiredSpeedScale(long netId, float scale)
    {
        desiredSpeedScaleByNetId[netId] = MovementState.QuantizeSpeedScale(scale);
        TryApplyDesiredSpeedScale(netId);
    }

    private void ApplyDesiredSpeedScales()
    {
        foreach (long netId in desiredSpeedScaleByNetId.Keys)
            TryApplyDesiredSpeedScale(netId);
    }

    private void TryApplyDesiredSpeedScale(long netId)
    {
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity entity)
            || !cell.World.TryGet(entity, out MovementState movement)) return;

        sbyte desired = desiredSpeedScaleByNetId[netId];
        if (movement.SpeedScaleQ == desired) return;
        movement.SpeedScaleQ = desired;
        cell.World.Set(entity, movement);
    }
}
