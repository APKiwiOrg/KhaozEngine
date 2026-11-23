using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainPresentationHierarchy
{
    internal static void Interpolate(ChainRig rig, ReadOnlySpan<Matrix4x4> from, ReadOnlySpan<Matrix4x4> to,
        Span<Matrix4x4> output, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<Matrix4x4> inverses, float amount)
    {
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            var anchor = anchors[definition.Anchor];
            var inverse = inverses[definition.Anchor];
            var fromParent = anchor;
            var toParent = anchor;
            var blendedParent = anchor;
            for (int joint = first; joint < end; joint++)
            {
                var local = rig.RestLocal(joint);
                var fromWorld = from[joint] * anchor;
                var fromSwing = ReadSwing(local * fromParent, fromWorld);
                fromParent = fromWorld;

                var toSwing = Quaternion.Identity;
                if (!to.IsEmpty)
                {
                    var toWorld = to[joint] * anchor;
                    toSwing = ReadSwing(local * toParent, toWorld);
                    toParent = toWorld;
                }

                // The attachment follows the blended parent so a large swing cannot shorten the segment.
                var unswung = local * blendedParent;
                var swing = Quaternion.Normalize(Quaternion.Slerp(fromSwing, toSwing, amount));
                blendedParent = unswung * Matrix4x4.CreateFromQuaternion(swing);
                blendedParent.Translation = unswung.Translation;
                output[joint] = blendedParent * inverse;
                if (joint == first)
                    output[joint].Translation = rig.RestFromAnchor[first].Translation;
            }
        }
    }

    private static Quaternion ReadSwing(in Matrix4x4 unswung, in Matrix4x4 world) =>
        new ChainPose(world).Rotation * Quaternion.Conjugate(new ChainPose(unswung).Rotation);
}
