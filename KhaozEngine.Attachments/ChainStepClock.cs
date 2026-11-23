using System;

namespace KhaozEngine.Attachments;

internal struct ChainStepClock(float stepSeconds, int maximumSteps)
{
    private readonly double _step = stepSeconds;
    private readonly int _maximumSteps = maximumSteps;
    private double _remainder;

    internal float Interpolation => (float)(_remainder / _step);

    internal void Reset() => _remainder = 0;

    internal int Advance(float frameSeconds)
    {
        double elapsed = _remainder + frameSeconds;
        _remainder = elapsed % _step;
        // Use the same remainder to count steps and place the grid inside the frame.
        double complete = Math.Round((elapsed - _remainder) / _step);
        return (int)Math.Min(complete, _maximumSteps);
    }

    internal float StepFraction(int step, int count, float frameSeconds)
    {
        // Counting back from the last boundary drops the oldest steps when over budget.
        double beforeFrameEnd = _remainder + (count - 1 - step) * _step;
        return (float)Math.Clamp(1 - beforeFrameEnd / frameSeconds, 0, 1);
    }
}
