using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationBlend(ChainRig rig)
{
    private float _elapsed;

    internal Matrix4x4[] LiveTarget { get; } = new Matrix4x4[rig.JointCount];

    internal void Begin() => _elapsed = 0;

    internal bool Advance(Span<Matrix4x4> shown, ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<Matrix4x4> anchorInverses,
        float seconds, float duration, bool toRest)
    {
        if (seconds == 0)
            return true;

        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            ReadOnlySpan<Matrix4x4> target = toRest ? rig.RestFromAnchor : LiveTarget;
            target.CopyTo(shown);
            _elapsed = duration;
            return false;
        }

        // Continue from the last displayed swings so a changing target cannot switch arcs across a half-turn.
        float amount = seconds / remaining;
        _elapsed += seconds;
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            BlendChain(shown, anchors[definition.Anchor], anchorInverses[definition.Anchor],
                rig.FirstJoint(chain), definition.Joints.Count, amount, toRest);
        }
        return true;
    }

    private void BlendChain(Span<Matrix4x4> shown, in Matrix4x4 anchor, in Matrix4x4 inverse,
        int first, int count, float amount, bool toRest)
    {
        var shownParent = anchor;
        var targetParent = anchor;
        var blendedParent = anchor;
        for (int joint = first; joint < first + count; joint++)
        {
            var local = rig.RestLocal(joint);
            var shownWorld = shown[joint] * anchor;
            var from = ReadSwing(local * shownParent, shownWorld);
            shownParent = shownWorld;

            var to = Quaternion.Identity;
            if (!toRest)
            {
                var targetWorld = LiveTarget[joint] * anchor;
                to = ReadSwing(local * targetParent, targetWorld);
                targetParent = targetWorld;
            }

            // Rebuild the child attachment on the blended parent instead of lerping its position.
            var unswung = local * blendedParent;
            var swing = Quaternion.Normalize(Quaternion.Slerp(from, to, amount));
            blendedParent = unswung * Matrix4x4.CreateFromQuaternion(swing);
            blendedParent.Translation = unswung.Translation;
            shown[joint] = blendedParent * inverse;
            if (joint == first)
                shown[joint].Translation = rig.RestFromAnchor[first].Translation;
        }
    }

    private static Quaternion ReadSwing(in Matrix4x4 unswung, in Matrix4x4 world) =>
        new ChainPose(world).Rotation * Quaternion.Conjugate(new ChainPose(unswung).Rotation);
}
