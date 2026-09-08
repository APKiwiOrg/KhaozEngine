using System;

namespace KhaozEngine.Simulation;

/// <summary>A normalized world-clock snapshot.</summary>
public readonly record struct WorldClockState(float TimeOfDay, float DayLengthSeconds, float TimeScale);

/// <summary>Advances normalized world time from real elapsed seconds.</summary>
public sealed class WorldClock
{
    private float timeOfDay;
    private float dayLengthSeconds = 1f;
    private float timeScale = 1f;

    /// <summary>Creates a clock with the given real-time day length and normalized start time.</summary>
    public WorldClock(float dayLengthSeconds, float startTimeOfDay = 0f)
    {
        DayLengthSeconds = dayLengthSeconds;
        timeOfDay = Wrap(float.IsFinite(startTimeOfDay) ? startTimeOfDay : 0f);
    }

    /// <summary>The normalized time of day in the range [0, 1).</summary>
    public float TimeOfDay => timeOfDay;

    /// <summary>Real seconds in one world day.</summary>
    public float DayLengthSeconds
    {
        get => dayLengthSeconds;
        set
        {
            if (float.IsFinite(value) && value > 0f)
                dayLengthSeconds = value;
        }
    }

    /// <summary>The non-negative multiplier applied while advancing live time.</summary>
    public float TimeScale
    {
        get => timeScale;
        set
        {
            if (float.IsFinite(value))
                timeScale = MathF.Max(0f, value);
        }
    }

    /// <summary>Gets an immutable snapshot of the current clock state.</summary>
    public WorldClockState Snapshot => new(TimeOfDay, DayLengthSeconds, TimeScale);

    /// <summary>Advances the clock by a real-time delta.</summary>
    public void Advance(float dtSeconds)
    {
        if (!float.IsFinite(dtSeconds))
            return;

        float next = timeOfDay + dtSeconds * timeScale / dayLengthSeconds;
        if (float.IsFinite(next))
            timeOfDay = Wrap(next);
    }

    /// <summary>Sets and normalizes the time of day when the value is finite.</summary>
    public bool SetTimeOfDay(float value)
    {
        if (!float.IsFinite(value))
            return false;

        timeOfDay = Wrap(value);
        return true;
    }

    private static float Wrap(float value)
    {
        value %= 1f;
        return value < 0f ? value + 1f : value;
    }
}
