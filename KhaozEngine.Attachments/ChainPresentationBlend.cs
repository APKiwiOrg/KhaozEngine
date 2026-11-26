using System;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationBlend(ChainRig rig, ChainPresentationHierarchy hierarchy)
{
    private float _elapsed;

    internal ChainPresentationFrame LiveTarget { get; } = new(rig);

    internal void Begin() => _elapsed = 0;

    internal void Finish(float duration) => _elapsed = duration;

    internal bool Continues(float seconds, float duration) => seconds < duration - _elapsed;

    internal bool Advance(ChainPresentationFrame shown, ReadOnlySpan<ChainPose> anchors,
        ReadOnlySpan<ChainPresentationAnchor> frame, float seconds, float duration, ChainPresentationFrame? target)
    {
        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            if (target is null)
                shown.SetRest(frame);
            else
                shown.CopyFrom(target);
            _elapsed = duration;
            return false;
        }

        if (seconds == 0)
            return true;

        // Continue from the last displayed swings so a changing target cannot switch arcs across a half-turn.
        float amount = seconds / remaining;
        _elapsed += seconds;
        hierarchy.Interpolate(shown, target, shown, anchors, frame, amount);
        return true;
    }
}
