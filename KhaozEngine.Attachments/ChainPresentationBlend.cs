using System;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationBlend(ChainRig rig, ChainPresentationHierarchy hierarchy)
{
    private float _elapsed;

    internal ChainPresentationFrame LiveTarget { get; } = new(rig);

    internal void Begin() => _elapsed = 0;

    internal bool Advance(ChainPresentationFrame shown, ReadOnlySpan<ChainPose> anchors,
        ReadOnlySpan<ChainPresentationAnchor> frame, float seconds, float duration, bool toRest)
    {
        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            if (toRest)
                shown.SetRest(frame);
            else
                shown.CopyFrom(LiveTarget);
            _elapsed = duration;
            return false;
        }

        if (seconds == 0)
            return true;

        // Continue from the last displayed swings so a changing target cannot switch arcs across a half-turn.
        float amount = seconds / remaining;
        _elapsed += seconds;
        hierarchy.Interpolate(shown, toRest ? null : LiveTarget, shown, anchors, frame, amount);
        return true;
    }
}
