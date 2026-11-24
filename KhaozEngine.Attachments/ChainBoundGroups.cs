using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainBoundGroups
{
    private const int TipDepth = 0;
    private sealed record Group(int[] Chains, ChainWidthBounds?[] BoundsByDepth, ChainWidthBounds Tips);

    private readonly bool[] _bounded;
    private readonly Group[] _groups;
    private readonly ChainConstraints.JointStep[] _placed;
    private readonly Vector3[] _incomingTurns;
    private readonly ChainCapsuleCull[] _capsuleCulls;
    private readonly int[] _capsuleCounts;

    internal ChainBoundGroups(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs)
    {
        int count = rig.Chains.Count;
        _bounded = new bool[count];
        _placed = pairs.Count == 0 ? [] : new ChainConstraints.JointStep[count];
        _incomingTurns = pairs.Count == 0 ? [] : new Vector3[count];
        _capsuleCulls = pairs.Count == 0 ? [] : new ChainCapsuleCull[count];
        _capsuleCounts = pairs.Count == 0 ? [] : new int[count];
        if (pairs.Count == 0)
        {
            _groups = [];
            return;
        }

        var roots = Enumerable.Range(0, count).ToArray();
        foreach (var pair in pairs)
        {
            _bounded[pair.First.Chain] = _bounded[pair.Second.Chain] = true;
            roots[Root(pair.Second.Chain)] = Root(pair.First.Chain);
        }

        var chainsByRoot = Enumerable.Range(0, count).Where(chain => _bounded[chain]).ToLookup(Root);
        _groups = pairs.GroupBy(pair => Root(pair.First.Chain)).Select(group =>
        {
            var chains = chainsByRoot[group.Key].ToArray();
            foreach (int chain in chains)
                _capsuleCulls[chain] = new ChainCapsuleCull();
            var depths = new ChainWidthBounds?[chains.Max(chain => rig.Chains[chain].Joints.Count)];
            var tips = new List<ChainWidthBounds.Pair>();
            foreach (var depth in group.GroupBy(pair => IsTip(pair.First) ? TipDepth : pair.Depth))
            {
                if (depth.Key == TipDepth)
                    tips.AddRange(depth);
                else
                    depths[depth.Key] = new ChainWidthBounds(depth.ToArray());
            }
            return new Group(chains, depths, new ChainWidthBounds(tips.ToArray()));
        }).ToArray();

        bool IsTip(ChainWidthBounds.Member member) =>
            member.Particle - member.Root == rig.Chains[member.Chain].Joints.Count;

        int Root(int chain)
        {
            while (chain != roots[chain])
                chain = roots[chain];
            return chain;
        }
    }

    internal bool Contains(int chain) => _bounded[chain];

    internal void Follow(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        ReadOnlySpan<ChainSettings> settings, float seconds)
    {
        foreach (var group in _groups)
        {
            // A chain keeps its own candidate indices because its depths are interleaved with its neighbours.
            foreach (int chain in group.Chains)
                _capsuleCounts[chain] = ChainConstraints.SelectCapsules(_capsuleCulls[chain], particles, chain,
                    settings[chain].Radius, colliders).Count;

            for (int depth = 1; depth < group.BoundsByDepth.Length; depth++)
                SolveDepth(particles, group.Chains, anchors, colliders, previousColliders, settings, seconds,
                    depth, group.BoundsByDepth[depth]);

            // All tip pairs share one solve after every possible parent has finished.
            SolveDepth(particles, group.Chains, anchors, colliders, previousColliders, settings, seconds,
                TipDepth, group.Tips);
        }
    }

    private void SolveDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        ReadOnlySpan<ChainSettings> settings, float seconds, int depth, ChainWidthBounds? width)
    {
        PlaceDepth(particles, chains, anchors, colliders, settings, seconds, depth);
        if (width is not null)
        {
            width.ProjectPositions(particles);
            ReapplyLimits(particles, chains, colliders, settings, depth);
            // Local velocity constraints get the last word after the shared width impulses.
            width.StopVelocities(particles, seconds);
        }

        FinishDepth(particles, chains, colliders, previousColliders, settings, seconds, depth);
    }

    private static bool HasParticle(int depth, int joints) => depth == TipDepth || depth < joints;

    private void PlaceDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        foreach (int chain in chains)
        {
            var definition = rig.Chains[chain];
            if (!HasParticle(depth, definition.Joints.Count))
                continue;

            int first = rig.FirstJoint(chain);
            int joint = first + (depth == TipDepth ? definition.Joints.Count : depth) - 1;
            var previous = _placed[chain];
            _incomingTurns[chain] = joint == first ? Vector3.Zero : ChainVelocityConstraints.NextTurn(particles,
                previous.Particle, previous.Length, previous.Direction, _incomingTurns[chain]);
            var parent = joint == first ? anchors[definition.Anchor] : particles.JointWorlds[joint - 1];
            _placed[chain] = ChainConstraints.PlaceJoint(particles, chain, joint, parent, 0,
                ChainConstraints.BendCosine(settings[chain]), settings[chain].Radius, seconds, Capsules(chain, colliders));
            if (_placed[chain].ReplayedAll)
                _capsuleCounts[chain] = colliders.Length;
        }
    }

    private void ReapplyLimits(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainSettings> settings, int depth)
    {
        foreach (int chain in chains)
        {
            if (!HasParticle(depth, particles.Rig.Chains[chain].Joints.Count))
                continue;
            var step = _placed[chain];
            if (particles.Positions[step.Particle + 1] == step.End)
                continue;
            _placed[chain] = ChainConstraints.ReapplyLimits(particles, chain, step,
                ChainConstraints.BendCosine(settings[chain]), settings[chain].Radius, Capsules(chain, colliders));
            if (_placed[chain].ReplayedAll)
                _capsuleCounts[chain] = colliders.Length;
        }
    }

    private void FinishDepth(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        foreach (int chain in chains)
        {
            if (!HasParticle(depth, rig.Chains[chain].Joints.Count))
                continue;
            var step = _placed[chain];
            var setting = settings[chain];
            int first = rig.FirstJoint(chain);
            var velocity = new ChainVelocityConstraints(particles, first + chain, particles.StepCarries[chain],
                previousColliders, setting.Radius, seconds);
            velocity.FrameTurn = _incomingTurns[chain];
            if (step.Joint == first)
                velocity.Start(rig.Chains[chain].Anchor);
            _incomingTurns[chain] = velocity.FrameTurn;
            var placement = particles.PlacementCull.Selected(chain, Capsules(chain, colliders), step.PlacementCount);
            velocity.ApplyLocal(step.Particle, step.Length, step.Direction, step.Rest, step.Clamped, placement);
            ChainConstraints.WriteSwing(particles, step);
        }
    }

    private ChainCapsuleCandidates Capsules(int chain, ReadOnlySpan<ColliderCapsule> colliders) =>
        _capsuleCulls[chain].Selected(colliders, _capsuleCounts[chain]);
}
