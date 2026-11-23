using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainBoundGroups
{
    private sealed record Group(int[] Chains, ChainWidthBounds?[] BoundsByDepth);

    private readonly bool[] _bounded;
    private readonly Group[] _groups;
    private readonly ChainConstraints.JointStep[] _placed;
    private readonly Vector3[] _incomingTurns;

    internal ChainBoundGroups(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs)
    {
        int count = rig.Chains.Count;
        _bounded = new bool[count];
        _placed = pairs.Count == 0 ? [] : new ChainConstraints.JointStep[count];
        _incomingTurns = pairs.Count == 0 ? [] : new Vector3[count];
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
            var depths = new ChainWidthBounds?[chains.Max(chain => rig.Chains[chain].Joints.Count) + 1];
            foreach (var depth in group.GroupBy(pair => pair.Depth))
                depths[depth.Key] = new ChainWidthBounds(depth.ToArray());
            return new Group(chains, depths);
        }).ToArray();

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
            for (int depth = 1; depth < group.BoundsByDepth.Length; depth++)
            {
                PlaceDepth(particles, group.Chains, anchors, colliders, settings, seconds, depth);
                var width = group.BoundsByDepth[depth];
                width?.ProjectPositions(particles);
                FinishDepth(particles, group.Chains, colliders, previousColliders, settings, seconds, depth);
                width?.StopVelocities(particles, seconds);
            }
        }
    }

    private void PlaceDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        foreach (int chain in chains)
        {
            var definition = rig.Chains[chain];
            if (depth > definition.Joints.Count)
                continue;

            int joint = rig.FirstJoint(chain) + depth - 1;
            var previous = _placed[chain];
            _incomingTurns[chain] = depth == 1 ? Vector3.Zero : ChainVelocityConstraints.NextTurn(particles,
                previous.Particle, previous.Length, previous.Direction, _incomingTurns[chain]);
            var parent = depth == 1 ? anchors[definition.Anchor] : particles.JointWorlds[joint - 1];
            _placed[chain] = ChainConstraints.PlaceJoint(particles, chain, joint, parent, 0,
                ChainConstraints.BendCosine(settings[chain]), settings[chain].Radius, seconds, colliders);
        }
    }

    private void FinishDepth(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        foreach (int chain in chains)
        {
            var step = _placed[chain];
            bool moved = particles.Positions[step.Particle + 1] != step.End;
            // A shorter chain's tip can be moved when a linked longer tip is placed.
            if (!moved && depth > rig.Chains[chain].Joints.Count)
                continue;

            var setting = settings[chain];
            if (moved)
                step = ChainConstraints.ReapplyLimits(particles, step, ChainConstraints.BendCosine(setting), setting.Radius, colliders);

            int first = rig.FirstJoint(chain);
            var velocity = new ChainVelocityConstraints(particles, first + chain, particles.StepCarries[chain],
                colliders, previousColliders, setting.Radius, seconds);
            velocity.FrameTurn = _incomingTurns[chain];
            if (depth == 1)
                velocity.Start(particles.PreviousRestDirections[step.Joint], step.Rest);
            _incomingTurns[chain] = velocity.FrameTurn;
            velocity.ApplyLocal(step.Particle, step.Length, step.Direction, step.Rest, step.Clamped);
            ChainConstraints.WriteSwing(particles, step);
            _placed[chain] = step;
        }
    }
}
