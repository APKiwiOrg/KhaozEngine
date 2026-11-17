using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Every value that differs between two compiled temporal programs' histories over a walk: how many, on how
/// many frames from which, and the largest difference, in value and in half-float steps. The entry identity facts
/// gather each pair with it, and on a software Vulkan device hold its colour to
/// <see cref="TemporalSoftwareCompileDivergenceRule"/>.</summary>
internal sealed class TemporalEntryDifference(string what, bool halfFloat = true)
{
    public int Values, Frames, FirstFrame = -1, WorstSteps;
    public float Worst;
    int _lastFrame = -1;

    public void Add(int frame, float a, float b)
    {
        if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)) return;
        Values++;
        if (frame != _lastFrame) Frames++;
        _lastFrame = frame;
        if (FirstFrame < 0) FirstFrame = frame;
        Worst = MathF.Max(Worst, MathF.Abs(a - b));
        if (halfFloat) WorstSteps = Math.Max(WorstSteps, TemporalHistoryDifference.HalfSteps(a, b));
    }

    public override string ToString() => Values == 0 ? $"{what}: none differ"
        : $"{what}: {Values} values differ on {Frames} frames from frame {FirstFrame}, the largest by {Worst}"
            + (halfFloat ? $" ({WorstSteps} half-float steps)" : " levels");
}
