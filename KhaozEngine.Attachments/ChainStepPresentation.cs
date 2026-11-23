using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainStepPresentation(ChainRig rig, ChainPresentationHierarchy hierarchy)
{
    private ChainPresentationFrame _previous = new(rig);
    private ChainPresentationFrame _current = new(rig);

    internal void Reset(ChainPresentationFrame shown)
    {
        _previous.CopyFrom(shown);
        _current.CopyFrom(shown);
    }

    internal void Capture(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<Matrix4x4> anchorInverses)
    {
        (_previous, _current) = (_current, _previous);
        for (int anchor = 0; anchor < anchorInverses.Length; anchor++)
            _current.Anchors[anchor] = new ChainPresentationAnchor(anchorWorlds[anchor], anchorInverses[anchor],
                particles.StepAnchorRotations[anchor]);

        var worlds = particles.JointWorlds;
        for (int chain = 0; chain < rig.Chains.Count; chain++)
        {
            var definition = rig.Chains[chain];
            int first = rig.FirstJoint(chain);
            int end = first + definition.Joints.Count;
            var inverse = anchorInverses[definition.Anchor];
            var root = worlds[first] * inverse;
            // The root is anchored exactly even when inverse composition rounds its offset.
            root.Translation = rig.RestFromAnchor[first].Translation;
            _current.Frames[first] = root;
            for (int joint = first + 1; joint < end; joint++)
                _current.Frames[joint] = worlds[joint] * inverse;
        }
        particles.JointSwings.CopyTo(_current.Swings, 0);
    }

    internal void Show(float amount, ReadOnlySpan<ChainPose> anchors, ReadOnlySpan<ChainPresentationAnchor> frame,
        ChainPresentationFrame shown)
    {
        if (amount == 0)
            shown.CopyFrom(_previous);
        else if (amount == 1)
            shown.CopyFrom(_current);
        else
            hierarchy.Interpolate(_previous, _current, shown, anchors, frame, amount);
    }
}
