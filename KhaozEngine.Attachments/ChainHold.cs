using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainHold(int jointCount)
{
    private readonly Quaternion[] _startingSwings = new Quaternion[jointCount];
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
                _startingSwings[joint] = ChangeBasis(particles.WorldSwings[joint], rotation);
        }
    }

    internal bool Advance(ChainParticles particles, ReadOnlySpan<ChainPose> anchors, float seconds, float duration)
    {
        _elapsed = MathF.Min(_elapsed + seconds, duration);
        if (_elapsed >= duration)
            return false;

        float blend = _elapsed / duration;
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
                var localSwing = Quaternion.Slerp(_startingSwings[joint], Quaternion.Identity, blend);
                var worldSwing = ChangeBasis(localSwing, intoWorld);
                var unswung = rig.RestLocal(joint) * parent;
                parent = unswung * Matrix4x4.CreateFromQuaternion(worldSwing);
                parent.Translation = unswung.Translation;
                particles.Positions[joint + chain] = parent.Translation;
                particles.JointWorlds[joint] = parent;
                particles.WorldSwings[joint] = worldSwing;
            }

            particles.Positions[first + definition.Joints.Count + chain] = Vector3.Transform(definition.Tip, parent);
        }

        particles.ZeroVelocity();
        return true;
    }

    private static Quaternion ChangeBasis(Quaternion swing, Quaternion basis) =>
        Quaternion.Conjugate(basis) * swing * basis;
}
