using System;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Tier order affects solving only. Each tier keeps its chains in their original rig order.
internal sealed class ChainSolveTiers
{
    private sealed record Tier(int[] Chains, ChainLinks Links, bool ExposesSegments);

    private readonly Tier[] _tiers;
    private readonly int _colliderCount;

    internal ChainSolveTiers(ChainRig rig, int colliderCount)
    {
        _colliderCount = colliderCount;
        var groups = Enumerable.Range(0, rig.Chains.Count)
            .GroupBy(chain => rig.Chains[chain].Layer)
            .OrderBy(group => group.Key)
            .ToArray();
        var links = rig.Links.ToLookup(link => rig.Chains[link.First].Layer);
        _tiers = new Tier[groups.Length];
        int obstacleCount = colliderCount;
        int placementRows = 1;
        for (int index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var chains = group.ToArray();
            var tierLinks = new ChainLinks(rig, links[group.Key].ToArray(), obstacleCount, colliderCount);
            bool exposesSegments = group.Key is not null && index + 1 < groups.Length;
            _tiers[index] = new Tier(chains, tierLinks, exposesSegments);
            placementRows = Math.Max(placementRows, tierLinks.PlacementRows);
            if (exposesSegments)
                obstacleCount = checked(obstacleCount + chains.Sum(chain => rig.Chains[chain].Joints.Count));
        }

        ObstacleCount = obstacleCount;
        PlacementRows = placementRows;
    }

    internal int ObstacleCount { get; }
    internal int PlacementRows { get; }

    internal void SeedOpeningRadii(ChainRig rig, Span<ColliderCapsule> opening, ReadOnlySpan<ChainSettings> settings,
        ChainExclusion excluded = default)
    {
        int obstacle = _colliderCount;
        foreach (var tier in _tiers)
        {
            if (!tier.ExposesSegments)
                continue;

            foreach (int chain in tier.Chains)
            {
                if (!excluded.Includes(chain)) continue;
                float radius = settings[chain].Radius;
                int count = rig.Chains[chain].Joints.Count;
                for (int segment = 0; segment < count; segment++, obstacle++)
                    opening[obstacle] = opening[obstacle] with { Radius = radius };
            }
        }
    }

    internal void Solve(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors, Span<ColliderCapsule> current,
        Span<ColliderCapsule> previous, ReadOnlySpan<ChainSettings> settings, Vector3 gravity, float seconds,
        Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn, ChainExclusion excluded = default)
    {
        int obstacle = _colliderCount;
        foreach (var tier in _tiers)
        {
            var obstacles = current[..obstacle];
            var opening = previous[..obstacle];
            tier.Links.BeginTier(excluded);
            foreach (int chain in tier.Chains)
            {
                if (!excluded.Includes(chain)) continue;
                int anchor = particles.Rig.Chains[chain].Anchor;
                if (seconds > 0)
                    particles.Predict(chain, settings[chain], gravity, seconds, oldWearer, newWearer, wearerTurn);
                ChainConstraints.FollowChain(particles, chain, anchors[anchor],
                    obstacles, opening, settings[chain], seconds, pullTowardRest: true, tier.Links.FirstPass(chain));
            }

            tier.Links.FinishStep(particles, anchors, obstacles, opening, settings, seconds, excluded);
            if (tier.ExposesSegments)
                obstacle = AppendSegments(tier, particles, current, previous, settings, obstacle, excluded);
        }
    }

    private static int AppendSegments(Tier tier, ChainParticles particles, Span<ColliderCapsule> current,
        Span<ColliderCapsule> previous, ReadOnlySpan<ChainSettings> settings, int obstacle, ChainExclusion excluded)
    {
        var rig = particles.Rig;
        foreach (int chain in tier.Chains)
        {
            if (!excluded.Includes(chain)) continue;
            int root = rig.FirstJoint(chain) + chain;
            int tip = root + rig.Chains[chain].Joints.Count;
            float radius = settings[chain].Radius;
            for (int start = root; start < tip; start++, obstacle++)
            {
                current[obstacle] = new ColliderCapsule(particles.Positions[start], particles.Positions[start + 1], radius);
                // The exchanged buffers retain this segment's last solved radius.
                previous[obstacle] = new ColliderCapsule(particles.OpeningPositions[start],
                    particles.OpeningPositions[start + 1], previous[obstacle].Radius);
                particles.SetCapsuleGeometry(obstacle, current[obstacle], previous[obstacle]);
            }
        }
        return obstacle;
    }
}
