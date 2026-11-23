using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainPresentationBlend(int jointCount)
{
    private float _elapsed;

    internal Matrix4x4[] LiveTarget { get; } = new Matrix4x4[jointCount];

    internal void Begin() => _elapsed = 0;

    internal bool Advance(Span<Matrix4x4> shown, ReadOnlySpan<Matrix4x4> target, float seconds, float duration)
    {
        if (seconds == 0)
            return true;

        float remaining = duration - _elapsed;
        if (seconds >= remaining)
        {
            target.CopyTo(shown);
            _elapsed = duration;
            return false;
        }

        // Continue from the last displayed frame so changing the target across a half-turn
        // from the original held pose cannot switch blend arcs.
        float amount = seconds / remaining;
        _elapsed += seconds;
        for (int joint = 0; joint < shown.Length; joint++)
            if (shown[joint] != target[joint])
                shown[joint] = new ChainPose(shown[joint]).Interpolate(new ChainPose(target[joint]), amount);
        return true;
    }
}
