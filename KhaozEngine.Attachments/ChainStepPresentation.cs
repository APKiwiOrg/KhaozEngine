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

    internal void BeginHeld(ChainPresentationFrame shown) => _previous.CopyFrom(shown);

    internal void SampleHeld(ChainPresentationFrame closing, ReadOnlySpan<ChainPose> anchors,
        ReadOnlySpan<ChainPresentationAnchor> frame, float fraction) =>
        hierarchy.Interpolate(_previous, closing, _current, anchors, frame, fraction);

    internal ReadOnlySpan<Matrix4x4> HeldFrames => _current.Frames;

    internal void SeedJoints(ChainPresentationFrame source, Range joints)
    {
        _previous.CopyJoints(source, joints);
        _current.CopyJoints(source, joints);
    }

    internal void Capture(ChainParticles particles, ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<Matrix4x4> anchorInverses, ChainExclusion excluded)
    {
        (_previous, _current) = (_current, _previous);
        for (int anchor = 0; anchor < anchorInverses.Length; anchor++)
            _current.Anchors[anchor] = new ChainPresentationAnchor(anchorWorlds[anchor], anchorInverses[anchor],
                particles.StepAnchorRotations[anchor]);

        _current.CaptureJoints(particles, excluded);
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
