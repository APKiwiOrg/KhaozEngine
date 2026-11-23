using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Two chains whose corresponding movable joints and virtual tips are coupled by soft spacing springs.</summary>
public readonly record struct ChainLink(int First, int Second)
{
    /// <summary>The maximum fractional departure from rest spacing for a rig link, or null for no bound.</summary>
    public float? Stretch { get; init; }

    internal static bool IsValidStretch(float stretch) => stretch is > 0f and <= 1f;
}

/// <summary>A joint's local transform relative to its parent at rest.</summary>
public readonly record struct ChainJointRest(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    internal Matrix4x4 ToMatrix()
    {
        // Rescale before normalization so a finite nonzero quaternion cannot underflow or overflow.
        float largest = MathF.Max(MathF.Max(MathF.Abs(Rotation.X), MathF.Abs(Rotation.Y)),
            MathF.Max(MathF.Abs(Rotation.Z), MathF.Abs(Rotation.W)));
        var rotation = Quaternion.Normalize(new Quaternion(
            Rotation.X / largest, Rotation.Y / largest, Rotation.Z / largest, Rotation.W / largest));
        var matrix = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(rotation);
        matrix.Translation = Translation;
        return matrix;
    }
}

/// <summary>An ordered chain on an anchor, with a virtual tip in its last joint's local frame.</summary>
public sealed record ChainRigChain(int Anchor, IReadOnlyList<ChainJointRest> Joints, Vector3 Tip, ChainSettings Settings);

/// <summary>The immutable rest data and output layout of a collection of chains.</summary>
public sealed class ChainRig
{
    private readonly int[] _firstJoints;
    private readonly Matrix4x4[] _restLocals;
    private readonly Matrix4x4[] _restFromAnchor;
    private readonly Vector3[] _segmentOffsets;
    private readonly double[] _restSquaredBasisConditions;

    /// <summary>Validates and copies the supplied chains, joints and links.</summary>
    public ChainRig(int anchorCount, IEnumerable<ChainRigChain> chains, IEnumerable<ChainLink> links)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(anchorCount);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(links);
        AnchorCount = anchorCount;

        var snapshot = chains.ToArray();
        _firstJoints = new int[snapshot.Length];
        int jointCount = 0;
        for (int chain = 0; chain < snapshot.Length; chain++)
        {
            var definition = snapshot[chain];
            if (definition is null || (uint)definition.Anchor >= (uint)anchorCount)
                throw new ArgumentException($"Chain {chain} must name an existing anchor.", nameof(chains));
            if (definition.Joints is null || definition.Joints.Count is < 1 or > 16)
                throw new ArgumentException($"Chain {chain} must have one to sixteen joints.", nameof(chains));
            if (!ChainMath.IsFinite(definition.Tip) || definition.Tip == Vector3.Zero)
                throw new ArgumentException($"Chain {chain} must have a finite nonzero tip.", nameof(chains));
            if (definition.Settings.FirstInvalidMember() is { } member)
                throw new ArgumentException($"Chain {chain} has an out-of-range {member}.", nameof(chains));

            snapshot[chain] = definition with { Joints = ImmutableArray.CreateRange(definition.Joints.ToArray()) };
            _firstJoints[chain] = jointCount;
            jointCount = checked(jointCount + snapshot[chain].Joints.Count);
        }

        JointCount = jointCount;
        _restLocals = new Matrix4x4[jointCount];
        _restFromAnchor = new Matrix4x4[jointCount];
        _segmentOffsets = new Vector3[jointCount];
        _restSquaredBasisConditions = new double[snapshot.Length];
        for (int chain = 0; chain < snapshot.Length; chain++)
        {
            var definition = snapshot[chain];
            int start = _firstJoints[chain];
            var parent = Matrix4x4.Identity;
            double restSquaredBasisCondition = 1;
            for (int joint = 0; joint < definition.Joints.Count; joint++)
            {
                int index = start + joint;
                var local = definition.Joints[joint].ToMatrix();
                var rest = local * parent;
                if (!ChainMath.TryInvert(local, out _) || !ChainMath.TryInvert(rest, out _))
                    throw new ArgumentException($"Chain {chain} joint {joint} has an invalid rest transform.", nameof(chains));

                // Rotations do not amplify scale spread. A product bounds it even after the joints swing.
                restSquaredBasisCondition *= ChainMath.SquaredBasisCondition(local);
                _restLocals[index] = local;
                _restFromAnchor[index] = rest;
                _segmentOffsets[index] = joint + 1 < definition.Joints.Count ? definition.Joints[joint + 1].Translation : definition.Tip;
                parent = rest;
            }

            _restSquaredBasisConditions[chain] = restSquaredBasisCondition;
            if (!ChainMath.IsFinite(Vector3.Transform(definition.Tip, parent)))
                throw new ArgumentException($"Chain {chain} has a non-finite rest tip position.", nameof(chains));
        }

        var linkSnapshot = links.ToArray();
        var pairs = new HashSet<(int, int)>();
        foreach (var link in linkSnapshot)
        {
            if ((uint)link.First >= (uint)snapshot.Length || (uint)link.Second >= (uint)snapshot.Length || link.First == link.Second)
                throw new ArgumentException("A link must name two distinct existing chains.", nameof(links));
            if (link.Stretch is { } stretch && !ChainLink.IsValidStretch(stretch))
                throw new ArgumentException("A link's stretch must be finite, greater than zero and at most one.", nameof(links));
            if (!pairs.Add((Math.Min(link.First, link.Second), Math.Max(link.First, link.Second))))
                throw new ArgumentException("A pair of chains can be linked only once.", nameof(links));
        }

        Chains = ImmutableArray.CreateRange(snapshot);
        Links = ImmutableArray.CreateRange(linkSnapshot);
    }

    public int AnchorCount { get; }
    public IReadOnlyList<ChainRigChain> Chains { get; }
    public IReadOnlyList<ChainLink> Links { get; }
    public int JointCount { get; }

    /// <summary>Returns the index of a chain's first joint in the flattened output.</summary>
    public int FirstJoint(int chain)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chain);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chain, _firstJoints.Length);
        return _firstJoints[chain];
    }

    internal ReadOnlySpan<Matrix4x4> RestFromAnchor => _restFromAnchor;
    internal Matrix4x4 RestLocal(int joint) => _restLocals[joint];
    internal Vector3 SegmentOffset(int joint) => _segmentOffsets[joint];
    internal double RestSquaredBasisCondition(int chain) => _restSquaredBasisConditions[chain];
}
