using System;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Tier order affects solving only. Each tier keeps its chains in their original rig order.
internal sealed class ChainSolveTiers
{
    private sealed record Tier(int[] Chains, ChainLinks Links, int ObstacleCount, bool ExposesSegments);

    private readonly Tier[] _tiers;

    internal ChainSolveTiers(ChainRig rig, int colliderCount)
    {
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
            var tierLinks = new ChainLinks(rig, links[group.Key].ToArray(), obstacleCount);
            bool exposesSegments = group.Key is not null && index + 1 < groups.Length;
            _tiers[index] = new Tier(chains, tierLinks, obstacleCount, exposesSegments);
            placementRows = Math.Max(placementRows, tierLinks.PlacementRows);
            if (exposesSegments)
                obstacleCount = checked(obstacleCount + chains.Sum(chain => rig.Chains[chain].Joints.Count));
        }

        ObstacleCount = obstacleCount;
        PlacementRows = placementRows;
    }

    internal int ObstacleCount { get; }
    internal int PlacementRows { get; }

    internal void Solve(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors, Span<ColliderCapsule> current,
        Span<ColliderCapsule> previous, ReadOnlySpan<ChainSettings> settings, Vector3 gravity, float seconds,
        Vector3 oldWearer, Vector3 newWearer, Quaternion wearerTurn)
    {
        foreach (var tier in _tiers)
        {
            var obstacles = current[..tier.ObstacleCount];
            var opening = previous[..tier.ObstacleCount];
            tier.Links.BeginTier();
            foreach (int chain in tier.Chains)
            {
                int anchor = particles.Rig.Chains[chain].Anchor;
                particles.Predict(chain, settings[chain], gravity, seconds, oldWearer, newWearer, wearerTurn);
                ChainConstraints.FollowChain(particles, chain, anchors[anchor],
                    obstacles, opening, settings[chain], seconds, pullTowardRest: true, tier.Links.FirstPass(chain));
            }

            tier.Links.FinishStep(particles, anchors, obstacles, opening, settings, seconds);
            if (tier.ExposesSegments)
                AppendSegments(tier, particles, current, previous, settings);
        }
    }

    private static void AppendSegments(Tier tier, ChainParticles particles, Span<ColliderCapsule> current,
        Span<ColliderCapsule> previous, ReadOnlySpan<ChainSettings> settings)
    {
        var rig = particles.Rig;
        int obstacle = tier.ObstacleCount;
        foreach (int chain in tier.Chains)
        {
            int root = rig.FirstJoint(chain) + chain;
            int tip = root + rig.Chains[chain].Joints.Count;
            float radius = settings[chain].Radius;
            for (int start = root; start < tip; start++, obstacle++)
            {
                current[obstacle] = new ColliderCapsule(particles.Positions[start], particles.Positions[start + 1], radius);
                previous[obstacle] = new ColliderCapsule(particles.OpeningPositions[start], particles.OpeningPositions[start + 1], radius);
                particles.SetCapsuleGeometry(obstacle, current[obstacle], previous[obstacle]);
            }
        }
    }
}
