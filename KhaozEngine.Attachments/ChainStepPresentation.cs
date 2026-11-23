using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainStepPresentation(int jointCount)
{
    private Matrix4x4[] _previous = new Matrix4x4[jointCount];
    private Matrix4x4[] _current = new Matrix4x4[jointCount];

    internal void Reset(ReadOnlySpan<Matrix4x4> frames)
    {
        frames.CopyTo(_previous);
        frames.CopyTo(_current);
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
            _current[first] = root;
            for (int joint = first + 1; joint < end; joint++)
                _current[joint] = worlds[joint] * inverse;
        }
    }

    internal void Show(float amount, ChainRig rig, ReadOnlySpan<Matrix4x4> anchors,
        ReadOnlySpan<Matrix4x4> inverses, Span<Matrix4x4> frames)
    {
        if (amount == 0)
            _previous.CopyTo(frames);
        else if (amount == 1)
            _current.CopyTo(frames);
        else
            ChainPresentationHierarchy.Interpolate(rig, _previous, _current, frames, anchors, inverses, amount);
    }
}
