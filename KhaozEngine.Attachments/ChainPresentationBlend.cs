using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationBlend(int jointCount)
{
    private readonly Quaternion[] _displayedSwings = new Quaternion[jointCount];
    private float _elapsed;

    internal void Begin(ChainParticles particles, ReadOnlySpan<ChainPose> previousAnchors)
    {
        _elapsed = 0;
        var rig = particles.Rig;
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            var rotation = previousAnchors[definition.Anchor].Rotation;
            int first = rig.FirstJoint(chain);
            for (int joint = first; joint < first + definition.Joints.Count; joint++)
                _displayedSwings[joint] = ChangeBasis(particles.WorldSwings[joint], rotation);
        }
    }

    internal bool Advance(ChainParticles particles, ReadOnlySpan<ChainPose> anchors, float seconds, float duration, bool toRest)
    {
        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            _elapsed = duration;
            return false;
        }

        // For a moving live target, continue from the last displayed swing so crossing
        // the half-turn relative to the original held pose cannot switch blend arcs.
        float blend = seconds / remaining;
        _elapsed += seconds;
        var rig = particles.Rig;
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            var anchor = anchors[definition.Anchor];
            var parent = anchor.Matrix;
            var intoWorld = Quaternion.Conjugate(anchor.Rotation);
            int first = rig.FirstJoint(chain);
            for (int joint = first; joint < first + definition.Joints.Count; joint++)
            {
                var displayedSwing = ChangeBasis(_displayedSwings[joint], intoWorld);
                var targetSwing = toRest ? Quaternion.Identity : particles.WorldSwings[joint];
                var worldSwing = Quaternion.Slerp(displayedSwing, targetSwing, blend);
                _displayedSwings[joint] = ChangeBasis(worldSwing, anchor.Rotation);
                var unswung = rig.RestLocal(joint) * parent;
                parent = unswung * Matrix4x4.CreateFromQuaternion(worldSwing);
                parent.Translation = unswung.Translation;
                // On resume these are presentation frames only. Physics has already advanced.
                if (toRest)
                {
                    particles.Positions[joint + chain] = parent.Translation;
                    var offset = Vector3.TransformNormal(rig.SegmentOffset(joint), unswung);
                    particles.PreviousRestDirections[joint] = ChainMath.Direction(offset, Vector3.UnitY);
                }
                particles.JointWorlds[joint] = parent;
                particles.WorldSwings[joint] = worldSwing;
            }

            if (toRest)
                particles.Positions[first + definition.Joints.Count + chain] = Vector3.Transform(definition.Tip, parent);
        }

        if (toRest)
            particles.ZeroVelocity();
        return true;
    }

    private static Quaternion ChangeBasis(Quaternion swing, Quaternion basis) =>
        Quaternion.Conjugate(basis) * swing * basis;
}
