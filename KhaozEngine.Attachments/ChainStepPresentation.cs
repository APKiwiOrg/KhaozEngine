using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainStepPresentation(int jointCount)
{
    private ChainPose[] _previous = new ChainPose[jointCount];
    private ChainPose[] _current = new ChainPose[jointCount];

    internal void Reset(ReadOnlySpan<Matrix4x4> frames)
    {
        for (int joint = 0; joint < frames.Length; joint++)
            _previous[joint] = _current[joint] = new ChainPose(frames[joint]);
    }

    internal void Capture(ChainRig rig, ReadOnlySpan<Matrix4x4> worlds, ReadOnlySpan<Matrix4x4> anchorInverses)
    {
        (_previous, _current) = (_current, _previous);
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            var inverse = anchorInverses[definition.Anchor];
            var root = worlds[first] * inverse;
            // The root is anchored exactly even when inverse composition rounds its offset.
            root.Translation = rig.RestFromAnchor[first].Translation;
            _current[first] = new ChainPose(root);
            for (int joint = first + 1; joint < end; joint++)
                _current[joint] = new ChainPose(worlds[joint] * inverse);
        }
    }

    internal void Show(float amount, Span<Matrix4x4> frames)
    {
        for (int joint = 0; joint < frames.Length; joint++)
            frames[joint] = amount == 0 ? _previous[joint].Matrix : _previous[joint].Interpolate(_current[joint], amount);
    }
}
