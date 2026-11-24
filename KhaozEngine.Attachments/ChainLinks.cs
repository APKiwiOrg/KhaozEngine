using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainLinks
{
    private const float LinkFrequency = 10;
    private readonly (int First, int Second)[] _pairs;
    private readonly Vector3[] _corrections;
    private readonly int[] _degrees;
    private readonly ChainBoundGroups _boundGroups;

    internal ChainLinks(ChainRig rig)
    {
        int capacity = rig.Links.Count == 0 ? 0 : checked(rig.JointCount + rig.Chains.Count);
        _corrections = new Vector3[capacity];
        _degrees = new int[capacity];
        var pairs = new List<(int, int)>();
        var widthPairs = new List<ChainWidthBounds.Pair>();
        foreach (var link in rig.Links)
        {
            int firstCount = rig.Chains[link.First].Joints.Count;
            int secondCount = rig.Chains[link.Second].Joints.Count;
            int first = rig.FirstJoint(link.First) + link.First;
            int second = rig.FirstJoint(link.Second) + link.Second;
            int commonCount = Math.Min(firstCount, secondCount);
            for (int joint = 1; joint < commonCount; joint++)
                AddPair(first + joint, second + joint);
            AddPair(first + firstCount, second + secondCount);

            void AddPair(int firstParticle, int secondParticle)
            {
                pairs.Add((firstParticle, secondParticle));
                _degrees[firstParticle]++;
                _degrees[secondParticle]++;
                if (link.Stretch is { } stretch)
                    widthPairs.Add(new(new(link.First, firstParticle, first), new(link.Second, secondParticle, second), stretch));
            }
        }

        _pairs = pairs.ToArray();
        _boundGroups = new ChainBoundGroups(rig, widthPairs);
    }

    internal int PlacementRows => _boundGroups.PlacementRows;

    internal bool Apply(ChainParticles particles, float seconds)
    {
        if (_pairs.Length == 0 || seconds == 0)
            return false;

        Array.Clear(_corrections);
        float halfWeight = 0.5f * ChainMath.MidpointSpringWeight(LinkFrequency, seconds);
        foreach (var pair in _pairs)
        {
            int first = pair.First;
            int second = pair.Second;
            var rest = particles.RestPositions[second] - particles.RestPositions[first];
            var predicted = particles.Positions[second] - particles.Positions[first];
            var started = particles.SubstepStarts[second] - particles.SubstepStarts[first];
            var axis = ChainMath.Direction(predicted, ChainMath.Direction(rest, Vector3.UnitX));
            float midpointSpacing = Vector3.Dot(0.5f * (started + predicted), axis);
            var correction = halfWeight * (midpointSpacing - rest.Length()) * axis;
            _corrections[first] += correction;
            _corrections[second] -= correction;
        }

        bool moved = false;
        for (int particle = 0; particle < _corrections.Length; particle++)
        {
            if (_corrections[particle] == Vector3.Zero)
                continue;

            // Each pair reads the same snapshot. Averaging keeps shared particles from
            // taking a stiffer step simply because several links touch them.
            particles.ApplySpring(particle, _corrections[particle] / _degrees[particle], seconds);
            moved = true;
        }

        return moved;
    }

    internal void FinishStep(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings, float seconds)
    {
        if (Apply(particles, seconds))
        {
            for (int chain = 0; chain < settings.Length; chain++)
            {
                if (_boundGroups.Contains(chain))
                    continue;
                int anchor = particles.Rig.Chains[chain].Anchor;
                ChainConstraints.FollowChain(particles, chain, anchors[anchor], colliders, previousColliders,
                    settings[chain], seconds, pullTowardRest: false);
            }
        }

        _boundGroups.Follow(particles, anchors, colliders, previousColliders, settings, seconds);
    }
}
