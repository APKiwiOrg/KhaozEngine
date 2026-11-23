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

    internal ChainLinks(ChainRig rig)
    {
        int capacity = rig.Links.Count == 0 ? 0 : checked(rig.JointCount + rig.Chains.Count);
        _corrections = new Vector3[capacity];
        _degrees = new int[capacity];
        var pairs = new List<(int, int)>();
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
        }

        _pairs = pairs.ToArray();

        void AddPair(int first, int second)
        {
            pairs.Add((first, second));
            _degrees[first]++;
            _degrees[second]++;
        }
    }

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
}
