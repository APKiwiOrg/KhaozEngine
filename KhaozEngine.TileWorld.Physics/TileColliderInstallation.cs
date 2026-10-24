using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Selects canonical rows before performing the single static installation transaction.</summary>
internal static class TileColliderInstallation
{
    internal static TileColliderRegistration Install(IPhysicsWorld world, IReadOnlyList<TileCollider> colliders,
        TileColliderProvenance[] provenance, string worldId, OrdinaryMovementExtent extent, Func<TileCollider, bool>? include)
    {
        ArgumentNullException.ThrowIfNull(world);
        var selected = new List<int>(colliders.Count);
        var retained = new List<TileColliderProvenance>(colliders.Count);
        for (int i = 0; i < colliders.Count; i++)
        {
            if (include is not null && !include(colliders[i])) continue;
            selected.Add(i);
            retained.Add(provenance[i]);
        }
        int[] indices = selected.ToArray();
        TileColliderProvenance[] owners = retained.ToArray();
        Vector3 origin = world.Origin;
        var handles = new StaticHandle[indices.Length];
        var groundHandles = new List<StaticHandle>();
        int added = 0;
        try
        {
            for (; added < handles.Length; added++)
            {
                TileCollider collider = colliders[indices[added]];
                handles[added] = world.AddStatic(collider.Shape,
                    new Pose(collider.Pose.Position - origin, collider.Pose.Orientation));
                if (collider.Kind == TileColliderKind.Ground) groundHandles.Add(handles[added]);
            }
        }
        catch (Exception addFailure)
        {
            try
            {
                new TileColliderRegistration(world, handles[..added]).Remove();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(addFailure, cleanupFailure);
            }
            throw;
        }
        return new TileColliderRegistration(world, handles, groundHandles.ToArray(), owners, worldId, extent, indices);
    }
}
