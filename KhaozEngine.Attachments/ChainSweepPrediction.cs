using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// The sweep reads an unconstrained position history. A placed parent or a
// contact correction must never become a sample in another segment's motion.
internal sealed class ChainSweepPrediction(int particles, int chains)
{
    private readonly Vector3[] _positions = new Vector3[particles];
    private readonly bool[] _ready = new bool[chains];

    internal void Begin() => Array.Clear(_ready);

    internal void Capture(ChainParticles particles, int chain, Matrix4x4 parent, float springWeight)
    {
        if (_ready[chain])
            return;

        var rig = particles.Rig;
        int first = rig.FirstJoint(chain);
        int count = rig.Chains[chain].Joints.Count;
        int root = first + chain;
        particles.Positions.AsSpan(root, count + 1).CopyTo(_positions.AsSpan(root));
        _ready[chain] = true;

        if (springWeight <= 0)
            return;

        // Build the spring's input frames without writing any positions or
        // applying length, bend, width or contact corrections to the prediction.
        for (int joint = first; joint < first + count; joint++)
        {
            int start = joint + chain;
            var unswung = rig.RestLocal(joint) * parent;
            var rest = ChainMath.Direction(Vector3.TransformNormal(rig.SegmentOffset(joint), unswung), Vector3.UnitY);
            var opening = particles.SubstepStarts[start + 1] - particles.SubstepStarts[start];
            var prediction = _positions[start + 1] - _positions[start];
            var correction = ChainConstraints.SpringCorrection(springWeight, particles.Lengths[joint], rest, opening, prediction);
            _positions[start + 1] += correction;
            var direction = ChainMath.Direction(prediction + correction, rest);
            parent = unswung * Matrix4x4.CreateFromQuaternion(ChainMath.Swing(rest, direction));
            parent.Translation = _positions[start];
        }
    }

    internal ChainSegmentContact.Segment? Read(int chain, int parent)
    {
        if (!_ready[chain])
            return null;

        var start = new ChainCapsuleCollision.Offset(_positions[parent]);
        return new(start, new ChainCapsuleCollision.Offset(_positions[parent + 1]) - start);
    }
}
