using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Publishes one complete, validated projection into the live client world. It reconciles against the live
/// <see cref="ClientReplicationView"/>, not only the previous publication, so the first keyframe over a world the
/// legacy path built removes legacy-only state too.
/// <list type="number">
/// <item>Every net id the view tracks that the projection lacks is despawned with its presentation buffers.</item>
/// <item>A surviving net id keeps its entity handle.</item>
/// <item>Every Replicate-channel registered component in the projection is installed from staging through its typed
/// copy, unchanged ones included, because presentation may have changed the live ECS since the last publication.</item>
/// <item>Every Replicate-channel registered component absent from the new set is removed with its sample history, so
/// interpolation cannot resurrect it.</item>
/// <item>Components outside the registry, and registered ones that never replicate, are game-local and untouched.</item>
/// </list>
/// Presentation buffers shift once and take fresh copies of sampled payloads, so they never reference a retained
/// backing array. Interpolation timestamps stay with the ingest path, which calls
/// <see cref="ClientReplicationView.RecordInterpolationSample"/> once after acceptance.
/// </summary>
internal sealed class ProjectionPublication
{
    private ProjectionPublication() { }

    /// <exception cref="ArgumentException">Staging uses a different registry than the view.</exception>
    /// <exception cref="InvalidOperationException">Staging lacks an entity of the projection.</exception>
    internal static void Publish(World target, ClientReplicationView view, ReplicationProjection projection,
        ProjectionStaging staged)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(staged);
        ReplicationRegistry registry = view.Registry;
        if (!ReferenceEquals(registry, staged.Registry))
            throw new ArgumentException("Staging must decode with the view's registry.", nameof(staged));

        // Validate before the first mutation, so a mismatched staging leaves the live world untouched.
        foreach (long netId in projection.Entities.Keys)
            if (!staged.TryGetEntity(netId, out _))
                throw new InvalidOperationException($"Staging holds no entity for net id {netId}.");

        view.ShiftPresentationBuffers();

        List<long>? departed = null;
        foreach (long netId in view.Entities.Keys)
            if (!projection.TryGetEntity(netId, out _)) (departed ??= new List<long>()).Add(netId);
        if (departed is not null)
            foreach (long netId in departed) view.DespawnForPublication(target, netId);

        IReadOnlyList<ComponentCodec> codecs = registry.Ordered;
        foreach (KeyValuePair<long, ProjectedEntity> kv in projection.Entities)
        {
            long netId = kv.Key;
            staged.TryGetEntity(netId, out Entity source);
            Entity live = view.GetOrSpawnForPublication(target, netId);
            foreach (ComponentCodec codec in codecs)
            {
                if ((codec.Channels & ReplicationChannels.Replicate) == 0) continue;
                if (kv.Value.TryGet(codec.TypeId, out ProjectedComponent frame))
                {
                    codec.CopyComponent(staged.World, source, target, live);
                    if (codec.FixedDelaySampled) view.SetPresentationBytes(netId, codec.TypeId, frame.Span);
                }
                else
                {
                    codec.RemoveComponent(target, live);
                    if (codec.FixedDelaySampled) view.RemovePresentationBuffers(netId, codec.TypeId);
                }
            }
        }
    }
}
