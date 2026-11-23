using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationHierarchy(ChainRig rig)
{
    // Cap the condition number at eight. The round trip through an anchor and the subsequent
    // basis read can each amplify float rounding. The squared cap keeps that contribution
    // to about four millionths (64 times the float round-off unit).
    private const double MaximumSquaredBasisCondition = 64;
    private const float ArcTieTolerance = 0.000004f;

    private readonly (double SquaredBasisCondition, ChainSwingTransport From, ChainSwingTransport To)[] _transports =
        new (double, ChainSwingTransport, ChainSwingTransport)[rig.AnchorCount];

    internal void Interpolate(ChainPresentationFrame from, ChainPresentationFrame? to, ChainPresentationFrame output,
        ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<ChainPresentationAnchor> frame, float amount)
    {
        for (int index = 0; index < anchors.Length; index++)
        {
            ChainSwingTransport fromTransport = default, toTransport = default;
            double condition = Math.Max(frame[index].SquaredBasisCondition, from.Anchors[index].SquaredBasisCondition);
            if (to is not null)
                condition = Math.Max(condition, to.Anchors[index].SquaredBasisCondition);

            bool similar = condition <= MaximumSquaredBasisCondition &&
                from.Anchors[index].TryCarryTo(anchors[index], out fromTransport) &&
                (to is null || to.Anchors[index].TryCarryTo(anchors[index], out toTransport));
            _transports[index] = (similar ? condition : double.PositiveInfinity, fromTransport, toTransport);
        }

        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            var anchor = anchors[definition.Anchor].Matrix;
            var inverse = frame[definition.Anchor].Inverse;
            var (anchorCondition, fromTransport, toTransport) = _transports[definition.Anchor];
            double condition = anchorCondition * rig.RestSquaredBasisCondition(chain);
            bool cached = condition <= MaximumSquaredBasisCondition;
            float arcTieTolerance = (float)condition * ArcTieTolerance;
            var blendedParent = anchor;
            var fromParent = anchor;
            var toParent = anchor;
            Matrix4x4 previousFromFrame = default;
            for (int joint = first; joint < end; joint++)
            {
                var local = rig.RestLocal(joint);
                Quaternion fromSwing, toSwing;
                if (cached)
                {
                    fromSwing = fromTransport.Apply(from.Swings[joint]);
                    toSwing = to is null ? Quaternion.Identity : toTransport.Apply(to.Swings[joint]);
                    // At a half-turn, endpoint rounding can choose the opposite shortest arc.
                    if (MathF.Abs(Quaternion.Dot(fromSwing, toSwing)) <= arcTieTolerance)
                    {
                        fromParent = joint == first ? anchor : previousFromFrame * anchor;
                        toParent = to is null || joint == first ? anchor : to.Frames[joint - 1] * anchor;
                        fromSwing = ReadSwing(local, from.Frames[joint], anchor, ref fromParent);
                        toSwing = to is null ? Quaternion.Identity : ReadSwing(local, to.Frames[joint], anchor, ref toParent);
                    }
                    previousFromFrame = from.Frames[joint];
                }
                else
                {
                    fromSwing = ReadSwing(local, from.Frames[joint], anchor, ref fromParent);
                    toSwing = to is null ? Quaternion.Identity : ReadSwing(local, to.Frames[joint], anchor, ref toParent);
                }

                // The attachment follows the blended parent so a large swing cannot shorten the segment.
                var unswung = local * blendedParent;
                var swing = Quaternion.Normalize(Quaternion.Slerp(fromSwing, toSwing, amount));
                blendedParent = unswung * Matrix4x4.CreateFromQuaternion(swing);
                blendedParent.Translation = unswung.Translation;
                output.Frames[joint] = blendedParent * inverse;
                output.Swings[joint] = swing;
                if (joint == first)
                    output.Frames[joint].Translation = rig.RestFromAnchor[first].Translation;
            }
        }

        frame.CopyTo(output.Anchors);
    }

    private static Quaternion ReadSwing(in Matrix4x4 local, in Matrix4x4 frame, in Matrix4x4 anchor, ref Matrix4x4 parent)
    {
        var world = frame * anchor;
        var unswung = local * parent;
        parent = world;
        return new ChainPose(world).Rotation * Quaternion.Conjugate(new ChainPose(unswung).Rotation);
    }
}
