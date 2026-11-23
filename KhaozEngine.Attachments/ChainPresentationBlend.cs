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
        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            ReadOnlySpan<Matrix4x4> target = toRest ? rig.RestFromAnchor : LiveTarget;
            target.CopyTo(shown);
            _elapsed = duration;
            return false;
        }

        if (seconds == 0)
            return true;

        // Continue from the last displayed swings so a changing target cannot switch arcs across a half-turn.
        float amount = seconds / remaining;
        _elapsed += seconds;
        ChainPresentationHierarchy.Interpolate(rig, shown, toRest ? [] : LiveTarget, shown, anchors, anchorInverses, amount);
        return true;
    }
}
