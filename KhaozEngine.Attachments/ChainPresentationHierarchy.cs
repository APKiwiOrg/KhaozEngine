using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationHierarchy(ChainRig rig)
{
    private readonly (bool Cached, ChainSwingTransport From, ChainSwingTransport To)[] _transports = new
        (bool, ChainSwingTransport, ChainSwingTransport)[rig.AnchorCount];

    internal void Interpolate(ChainPresentationFrame from, ChainPresentationFrame? to, ChainPresentationFrame output,
        ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<Matrix4x4> inverses, float amount)
    {
        for (int index = 0; index < anchors.Length; index++)
        {
            ChainSwingTransport toTransport = default;
            bool cached = from.Anchors[index].TryCarryTo(anchors[index], out var fromTransport) &&
                (to is null || to.Anchors[index].TryCarryTo(anchors[index], out toTransport));
            _transports[index] = (cached, fromTransport, toTransport);
        }

        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            var anchor = anchors[definition.Anchor].Matrix;
            var inverse = inverses[definition.Anchor];
            var (cached, fromTransport, toTransport) = _transports[definition.Anchor];
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
                    if (MathF.Abs(Quaternion.Dot(fromSwing, toSwing)) <= 0.000004f)
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

        output.SetAnchors(anchors, inverses);
    }

    private static Quaternion ReadSwing(in Matrix4x4 local, in Matrix4x4 frame, in Matrix4x4 anchor, ref Matrix4x4 parent)
    {
        var world = frame * anchor;
        var unswung = local * parent;
        parent = world;
        return new ChainPose(world).Rotation * Quaternion.Conjugate(new ChainPose(unswung).Rotation);
    }
}
