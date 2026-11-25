using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainBoundGroups
{
    internal const int TipDepth = 0;
    private sealed record Group(int[] Chains, ChainWidthBounds?[] BoundsByDepth, ChainWidthBounds Tips, ChainSheetContact? Sheet);

    private readonly bool[] _bounded;
    private readonly Group[] _groups;
    private readonly ChainConstraints.JointStep[] _placed;
    private readonly Vector3[] _incomingTurns;
    private readonly ChainCapsuleCull[] _capsuleCulls;
    private readonly int[] _capsuleCounts;
    private readonly ChainCapsuleCull[] _activeCulls;
    private readonly float[] _bendCosines;

    internal ChainBoundGroups(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs, int colliderCount)
    {
        int count = rig.Chains.Count;
        _bounded = pairs.Count == 0 ? [] : new bool[count];
        _placed = pairs.Count == 0 ? [] : new ChainConstraints.JointStep[count];
        _incomingTurns = pairs.Count == 0 ? [] : new Vector3[count];
        if (pairs.Count == 0)
        {
            _groups = [];
            _capsuleCulls = [];
            _capsuleCounts = [];
            _activeCulls = [];
            _bendCosines = [];
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
            var depths = new ChainWidthBounds?[chains.Max(chain => rig.Chains[chain].Joints.Count)];
            var tips = new List<ChainWidthBounds.Pair>();
            foreach (var depth in group.GroupBy(pair => IsTip(pair.First) ? TipDepth : pair.Depth))
            {
                if (depth.Key == TipDepth)
                    tips.AddRange(depth);
                else
                    depths[depth.Key] = new ChainWidthBounds(depth.ToArray());
            }
            return new Group(chains, depths, new ChainWidthBounds(tips.ToArray()),
                colliderCount > 0 ? new ChainSheetContact(rig, group.ToArray(), depths.Length, colliderCount) : null);
        }).ToArray();
        PlacementRows = _groups.Max(group => group.Chains.Length);
        _capsuleCulls = new ChainCapsuleCull[PlacementRows];
        _capsuleCounts = new int[PlacementRows];
        _activeCulls = new ChainCapsuleCull[PlacementRows];
        _bendCosines = new float[PlacementRows];
        for (int row = 0; row < PlacementRows; row++)
            _capsuleCulls[row] = new ChainCapsuleCull(colliderCount);

        bool IsTip(ChainWidthBounds.Member member) =>
            member.Particle - member.Root == rig.Chains[member.Chain].Joints.Count;

        int Root(int chain)
        {
            while (chain != roots[chain])
                chain = roots[chain];
            return chain;
        }
    }

    internal int PlacementRows { get; } = 1;

    internal bool Contains(int chain) => _bounded.Length != 0 && _bounded[chain];

    internal void Follow(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        ReadOnlySpan<ChainSettings> settings, float seconds, ChainLinks links)
    {
        bool reuseCandidates = ChainContactOptimizations.Use(ChainContactOptimization.FirstPassCandidates);
        bool reuseBend = ChainContactOptimizations.Use(ChainContactOptimization.BendCosine);
        float largestObstacle = 0;
        if (_groups.Length > 0 && ChainContactOptimizations.Use(ChainContactOptimization.ThickSheet))
            foreach (ref readonly var obstacle in colliders)
                largestObstacle = MathF.Max(largestObstacle, obstacle.Radius);

        foreach (var group in _groups)
        {
            // Chains within a group need separate indices. The next group reuses these rows.
            for (int row = 0; row < group.Chains.Length; row++)
            {
                int chain = group.Chains[row];
                var first = links.FirstPass(chain);
                if (reuseCandidates && first is not null &&
                    first.TryReuse(particles, chain, settings[chain].Radius, colliders.Length, out int count))
                {
                    _activeCulls[row] = first.Cull;
                    _capsuleCounts[row] = count;
                    ChainContactCounts.Record(ChainContactQuery.FirstPassReuses);
                }
                else
                {
                    _activeCulls[row] = _capsuleCulls[row];
                    _capsuleCounts[row] = ChainConstraints.SelectCapsules(_activeCulls[row], particles, chain,
                        settings[chain].Radius, colliders).Count;
                }
                if (reuseBend)
                    _bendCosines[row] = first?.BendCosine ?? ChainConstraints.BendCosine(settings[chain]);
            }

            for (int depth = 1; depth < group.BoundsByDepth.Length; depth++)
                SolveDepth(particles, group.Chains, anchors, colliders, previousColliders, settings, seconds,
                    depth, group.BoundsByDepth[depth], group.Sheet, largestObstacle);

            // All tip pairs share one solve after every possible parent has finished.
            SolveDepth(particles, group.Chains, anchors, colliders, previousColliders, settings, seconds,
                TipDepth, group.Tips, group.Sheet, largestObstacle);
        }
    }

    private void SolveDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        ReadOnlySpan<ChainSettings> settings, float seconds, int depth, ChainWidthBounds? width, ChainSheetContact? sheet,
        float largestObstacle)
    {
        PlaceDepth(particles, chains, anchors, colliders, settings, seconds, depth);
        if (width is not null)
        {
            width.ProjectPositions(particles, _placed, colliders, settings);
            ReapplyLimits(particles, chains, colliders, settings, depth);
        }

        if (sheet is not null && width is not null)
        {
            sheet.ProjectPositions(particles, depth, _placed, colliders, settings, largestObstacle);
            ReapplyLimits(particles, chains, colliders, settings, depth);
        }
        // Local velocity constraints run after the shared width impulses.
        width?.StopVelocities(particles, seconds);
        FinishDepth(particles, chains, colliders, previousColliders, settings, seconds, depth);
        sheet?.StopVelocities(particles, depth, _placed, colliders, previousColliders, settings, seconds);
    }

    private static bool HasParticle(int depth, int joints) => depth == TipDepth || depth < joints;

    private void PlaceDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        for (int row = 0; row < chains.Length; row++)
        {
            int chain = chains[row];
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
                BendCosine(row, settings[chain]), settings[chain].Radius, seconds, Capsules(row, colliders), row);
            if (_placed[chain].ReplayedAll)
                _capsuleCounts[row] = colliders.Length;
        }
    }

    private void ReapplyLimits(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainSettings> settings, int depth)
    {
        for (int row = 0; row < chains.Length; row++)
        {
            int chain = chains[row];
            if (!HasParticle(depth, particles.Rig.Chains[chain].Joints.Count))
                continue;
            var step = _placed[chain];
            if (particles.Positions[step.Particle + 1] == step.End)
                continue;
            _placed[chain] = ChainConstraints.ReapplyLimits(particles, row, step,
                BendCosine(row, settings[chain]), settings[chain].Radius, Capsules(row, colliders));
            if (_placed[chain].ReplayedAll)
                _capsuleCounts[row] = colliders.Length;
        }
    }

    private void FinishDepth(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth)
    {
        var rig = particles.Rig;
        for (int row = 0; row < chains.Length; row++)
        {
            int chain = chains[row];
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
            var placement = particles.PlacementCull.Selected(row, Capsules(row, colliders), step.PlacementCount);
            velocity.ApplyLocal(step.Particle, step.Length, step.Direction, step.Rest, step.Clamped, placement);
            ChainConstraints.WriteSwing(particles, step);
        }
    }

    private ChainCapsuleCandidates Capsules(int row, ReadOnlySpan<ColliderCapsule> colliders) =>
        _activeCulls[row].Selected(colliders, _capsuleCounts[row]);

    private float BendCosine(int row, in ChainSettings settings) =>
        ChainContactOptimizations.Use(ChainContactOptimization.BendCosine) ? _bendCosines[row] :
            ChainConstraints.BendCosine(settings);
}
