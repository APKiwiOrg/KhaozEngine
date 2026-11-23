using System;
using System.Collections.Generic;
using Components = KhaozEngine.Replication.CapturedComponents;
using Projection = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>Selects and owner-scopes interest in capture order for both AoI delta formats.</summary>
internal sealed class AoiProjectionBuilder
{
    private readonly ReplicationRegistry registry;
    private readonly bool hasOwnerScopedCodec;
    private readonly List<(int Order, long NetId, Components Components)> scratch = new();
    private bool scratchInUse;
    private static readonly Comparison<(int Order, long NetId, Components Components)> ByCaptureOrder =
        static (a, b) => a.Order.CompareTo(b.Order);

    internal AoiProjectionBuilder(ReplicationRegistry registry)
    {
        this.registry = registry;
        foreach (ComponentCodec codec in registry.Ordered)
        {
            if ((codec.Channels & ReplicationChannels.OwnerOnly) == 0) continue;
            hasOwnerScopedCodec = true;
            break;
        }
    }

    internal void Fill(Projection destination, Projection capture, IReadOnlySet<long> interestSet,
        long? ownerNetId, bool useSharedScratch)
    {
        bool share = useSharedScratch && !scratchInUse;
        var selected = share ? scratch : new List<(int Order, long NetId, Components Components)>();
        if (share) scratchInUse = true;
        try
        {
            // The hosts supply concrete hash sets. A caller's other set can customize interface enumeration.
            if (interestSet.GetType() == typeof(HashSet<long>))
                foreach (long netId in (HashSet<long>)interestSet) Select(netId);
            else
                foreach (long netId in interestSet) Select(netId);

            // Capture positions are unique, so ordering stays deterministic even when interest order differs.
            selected.Sort(ByCaptureOrder);
            destination.EnsureCapacity(selected.Count);
            foreach (var (_, netId, components) in selected)
                destination[netId] = hasOwnerScopedCodec
                    ? CaptureProjection.OwnerScope(components, registry, netId, ownerNetId)
                    : components;
        }
        finally
        {
            selected.Clear();
            if (share) scratchInUse = false;
        }

        void Select(long netId)
        {
            if (capture.TryGetValue(netId, out Components? components))
                selected.Add((components.Order, netId, components));
        }
    }
}
