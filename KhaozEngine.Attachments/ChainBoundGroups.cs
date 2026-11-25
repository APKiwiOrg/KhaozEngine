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
    private readonly Vector3[] _beforeSheetPass;
    private readonly ChainConstraints.JointStep[] _beforeWidthRecovery;
    private readonly int[] _beforeWidthCandidateCounts;
    private readonly ChainWidthNearSolids? _widthNearSolids;

    internal ChainBoundGroups(ChainRig rig, IReadOnlyList<ChainWidthBounds.Pair> pairs, int colliderCount, int solidColliderCount)
    {
        int count = rig.Chains.Count;
        _bounded = pairs.Count == 0 ? [] : new bool[count];
        _placed = pairs.Count == 0 ? [] : new ChainConstraints.JointStep[count];
        _incomingTurns = pairs.Count == 0 ? [] : new Vector3[count];
        _beforeSheetPass = colliderCount == 0 || pairs.Count == 0 ? [] : new Vector3[count];
        if (pairs.Count == 0)
        {
            _groups = [];
            _capsuleCulls = [];
            _capsuleCounts = [];
            _activeCulls = [];
            _bendCosines = [];
            _beforeWidthRecovery = [];
            _beforeWidthCandidateCounts = [];
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
        _beforeWidthRecovery = colliderCount == 0 ? [] : new ChainConstraints.JointStep[count];
        _beforeWidthCandidateCounts = colliderCount == 0 ? [] : new int[PlacementRows];
        for (int row = 0; row < PlacementRows; row++)
            _capsuleCulls[row] = new ChainCapsuleCull(colliderCount);
        _widthNearSolids = ChainWidthNearSolids.TryCreate(count, PlacementRows, solidColliderCount);
        if (_widthNearSolids is not null)
            foreach (var group in _groups)
                for (int row = 0; row < group.Chains.Length; row++)
                    _widthNearSolids.SetRow(group.Chains[row], row);

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
        if (_groups.Length == 0)
            return;

        var disabled = ChainContactOptimizations.DisabledForTests;
        bool reuseCandidates = ChainContactOptimizations.Use(disabled, ChainContactOptimization.FirstPassCandidates);
        bool reuseBend = ChainContactOptimizations.Use(disabled, ChainContactOptimization.BendCosine);
        var nearSolids = ChainContactOptimizations.Use(disabled, ChainContactOptimization.WidthNearSolids) ? _widthNearSolids : null;
        var counts = ChainContactCounts.Active;
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
                    counts?.Record(ChainContactQuery.FirstPassReuses);
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
                    depth, group.BoundsByDepth[depth], group.Sheet, reuseBend, nearSolids);

            // All tip pairs share one solve after every possible parent has finished.
            SolveDepth(particles, group.Chains, anchors, colliders, previousColliders, settings, seconds,
                TipDepth, group.Tips, group.Sheet, reuseBend, nearSolids);
        }
    }

    private void SolveDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ColliderCapsule> previousColliders,
        ReadOnlySpan<ChainSettings> settings, float seconds, int depth, ChainWidthBounds? width, ChainSheetContact? sheet, bool reuseBend,
        ChainWidthNearSolids? nearSolids)
    {
        PlaceDepth(particles, chains, anchors, colliders, settings, seconds, depth, reuseBend);
        if (width is not null)
        {
            width.ProjectPositions(particles, _placed, colliders, settings, nearSolids);
            ReapplyLimits(particles, chains, colliders, settings, depth, reuseBend);
        }

        bool sheetActive = width is not null && sheet is not null && !ChainSheetContact.DisabledForTests;
        if (sheetActive)
            SolveSheetPositions(sheet!, width!, particles, chains, colliders, settings, depth, reuseBend, nearSolids);

        // Local velocity constraints run after the shared width impulses.
        width?.StopVelocities(particles, seconds);
        FinishDepth(particles, chains, colliders, previousColliders, settings, seconds, depth);
        if (sheetActive)
            sheet!.StopVelocities(particles, depth, _placed, colliders, previousColliders, seconds);
    }

    private void SolveSheetPositions(ChainSheetContact sheet, ChainWidthBounds width, ChainParticles particles, int[] chains,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, int depth, bool reuseBend,
        ChainWidthNearSolids? nearSolids)
    {
        bool moved = false;
        sheet.BeginRow(particles, depth, _placed, colliders, settings);
        for (int pass = 0; pass < ChainSheetContact.MaxPasses; pass++)
        {
            foreach (int chain in chains)
                _beforeSheetPass[chain] = _placed[chain].End;

            if (!sheet.ProjectPass(particles, depth, _placed, colliders))
                break;

            ReapplyLimits(particles, chains, colliders, settings, depth, reuseBend);
            bool accepted = false;
            foreach (int chain in chains)
            {
                ref readonly var step = ref _placed[chain];
                var parent = new ChainCapsuleCollision.Offset(step.Start);
                accepted |= ChainContactPass.Moved(new ChainCapsuleCollision.Offset(_beforeSheetPass[chain]) - parent,
                    new ChainCapsuleCollision.Offset(step.End) - parent);
            }
            if (!accepted)
                break;
            moved = true;
        }

        if (moved && width.NeedsRecovery(particles))
            RecoverWidth(sheet, width, particles, chains, colliders, settings, depth, reuseBend, nearSolids);
    }

    private void RecoverWidth(ChainSheetContact sheet, ChainWidthBounds width, ChainParticles particles, int[] chains,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, int depth, bool reuseBend,
        ChainWidthNearSolids? nearSolids)
    {
        width.ProjectPositions(particles, _placed, colliders, settings, nearSolids);
        if (!sheet.KeepsClearance(particles, depth, _placed, colliders))
        {
            foreach (int chain in chains)
                if (HasParticle(depth, particles.Rig.Chains[chain].Joints.Count))
                    particles.Positions[_placed[chain].Particle + 1] = _placed[chain].End;
            return;
        }

        for (int row = 0; row < chains.Length; row++)
        {
            _beforeWidthRecovery[chains[row]] = _placed[chains[row]];
            _beforeWidthCandidateCounts[row] = _capsuleCounts[row];
        }

        ReapplyLimits(particles, chains, colliders, settings, depth, reuseBend);
        if (sheet.KeepsClearance(particles, depth, _beforeWidthRecovery, colliders))
            return;

        // A local contact can redirect the certified turn. Restore the
        // accepted endpoints and reconstruct their velocity candidate rows.
        for (int row = 0; row < chains.Length; row++)
        {
            int chain = chains[row];
            if (!HasParticle(depth, particles.Rig.Chains[chain].Joints.Count))
                continue;

            var step = _beforeWidthRecovery[chain];
            _placed[chain] = step;
            particles.Positions[step.Particle + 1] = step.End;
            _capsuleCounts[row] = _beforeWidthCandidateCounts[row];
            var capsules = Capsules(row, colliders);
            if (step.PlacementCount < capsules.Count)
                particles.PlacementCull.Select(row, step.Start, step.Length, settings[chain].Radius, capsules);
        }
    }

    private static bool HasParticle(int depth, int joints) => depth == TipDepth || depth < joints;

    private void PlaceDepth(ChainParticles particles, int[] chains, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<ChainSettings> settings, float seconds, int depth, bool reuseBend)
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
            ref readonly var previous = ref _placed[chain];
            _incomingTurns[chain] = joint == first ? Vector3.Zero : ChainVelocityConstraints.NextTurn(particles,
                previous.Particle, previous.Length, previous.Direction, _incomingTurns[chain]);
            var parent = joint == first ? anchors[definition.Anchor] : particles.JointWorlds[joint - 1];
            _placed[chain] = ChainConstraints.PlaceJoint(particles, chain, joint, parent, 0,
                BendCosine(row, settings[chain], reuseBend), settings[chain].Radius, seconds, Capsules(row, colliders), row);
            if (_placed[chain].ReplayedAll)
                _capsuleCounts[row] = colliders.Length;
        }
    }

    private void ReapplyLimits(ChainParticles particles, int[] chains, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainSettings> settings, int depth, bool reuseBend)
    {
        for (int row = 0; row < chains.Length; row++)
        {
            int chain = chains[row];
            if (!HasParticle(depth, particles.Rig.Chains[chain].Joints.Count))
                continue;
            ref readonly var step = ref _placed[chain];
            if (particles.Positions[step.Particle + 1] == step.End)
                continue;
            _placed[chain] = ChainConstraints.ReapplyLimits(particles, row, in step,
                BendCosine(row, settings[chain], reuseBend), settings[chain].Radius, Capsules(row, colliders));
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
            ref readonly var step = ref _placed[chain];
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

    private float BendCosine(int row, in ChainSettings settings, bool reuse) =>
        reuse ? _bendCosines[row] : ChainConstraints.BendCosine(settings);
}
