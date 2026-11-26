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
    private readonly int[] _linkedChains;
    private readonly bool[] _moved;
    private readonly ChainFirstPassContacts[] _firstPass;
    private readonly ChainBoundGroups _boundGroups;

    internal ChainLinks(ChainRig rig, IReadOnlyList<ChainLink> links, int colliderCount, int? solidColliderCount = null)
    {
        int capacity = links.Count == 0 ? 0 : checked(rig.JointCount + rig.Chains.Count);
        _corrections = new Vector3[capacity];
        _degrees = new int[capacity];
        var pairs = new List<(int, int)>();
        var widthPairs = new List<ChainWidthBounds.Pair>();
        var linkedChains = new SortedSet<int>();
        foreach (var link in links)
        {
            linkedChains.Add(link.First);
            linkedChains.Add(link.Second);
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
        _linkedChains = [.. linkedChains];
        _moved = new bool[_linkedChains.Length];
        _firstPass = AllocateFirstPass(_linkedChains.Length, colliderCount);
        _boundGroups = new ChainBoundGroups(rig, widthPairs, colliderCount, solidColliderCount ?? colliderCount);
    }

    internal int PlacementRows => _boundGroups.PlacementRows;

    private static ChainFirstPassContacts[] AllocateFirstPass(int chains, int obstacles)
    {
        try
        {
            var cache = new ChainFirstPassContacts[chains];
            for (int index = 0; index < cache.Length; index++)
                cache[index] = new ChainFirstPassContacts(obstacles);
            return cache;
        }
        catch (OutOfMemoryException)
        {
            return [];
        }
    }

    internal void BeginTier(ChainExclusion excluded)
    {
        for (int i = 0; i < _firstPass.Length; i++)
            if (excluded.Includes(_linkedChains[i])) _firstPass[i].Begin();
    }

    internal ChainFirstPassContacts? FirstPass(int chain)
    {
        if (_firstPass.Length == 0)
            return null;

        int slot = Array.BinarySearch(_linkedChains, chain);
        return slot < 0 ? null : _firstPass[slot];
    }

    private void Apply(ChainParticles particles, float seconds, ChainExclusion excluded)
    {
        Array.Clear(_moved);
        if (_pairs.Length == 0 || seconds == 0)
            return;

        Array.Clear(_corrections);
        float halfWeight = 0.5f * ChainMath.MidpointSpringWeight(LinkFrequency, seconds);
        var (firstExcluded, excludedCount) = excluded.ParticleRange(particles.Rig);
        foreach (var pair in _pairs)
        {
            int first = pair.First;
            int second = pair.Second;
            if ((uint)(first - firstExcluded) < (uint)excludedCount || (uint)(second - firstExcluded) < (uint)excludedCount)
                continue;
            var rest = particles.RestPositions[second] - particles.RestPositions[first];
            var predicted = particles.Positions[second] - particles.Positions[first];
            var started = particles.SubstepStarts[second] - particles.SubstepStarts[first];
            var axis = ChainMath.Direction(predicted, ChainMath.Direction(rest, Vector3.UnitX));
            float midpointSpacing = Vector3.Dot(0.5f * (started + predicted), axis);
            var correction = halfWeight * (midpointSpacing - rest.Length()) * axis;
            _corrections[first] += correction;
            _corrections[second] -= correction;
        }

        var rig = particles.Rig;
        for (int index = 0; index < _linkedChains.Length; index++)
        {
            int chain = _linkedChains[index];
            if (!excluded.Includes(chain)) continue;
            int root = rig.FirstJoint(chain) + chain;
            int tip = root + rig.Chains[chain].Joints.Count;
            for (int particle = root + 1; particle <= tip; particle++)
            {
                if (_corrections[particle] == Vector3.Zero)
                    continue;

                // Each pair reads the same snapshot. Averaging keeps shared particles from
                // taking a stiffer step simply because several links touch them.
                particles.ApplySpring(particle, _corrections[particle] / _degrees[particle], seconds);
                _moved[index] = true;
            }
        }
    }

    internal void FinishStep(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ColliderCapsule> previousColliders, ReadOnlySpan<ChainSettings> settings, float seconds,
        ChainExclusion excluded = default)
    {
        Apply(particles, seconds, excluded);
        for (int index = 0; index < _linkedChains.Length; index++)
        {
            int chain = _linkedChains[index];
            if (!excluded.Includes(chain) || !_moved[index] || _boundGroups.Contains(chain))
                continue;

            int anchor = particles.Rig.Chains[chain].Anchor;
            ChainConstraints.FollowChain(particles, chain, anchors[anchor], colliders, previousColliders,
                settings[chain], seconds, pullTowardRest: false, FirstPass(chain));
        }

        _boundGroups.Follow(particles, anchors, colliders, previousColliders, settings, seconds, this, excluded);
    }
}
