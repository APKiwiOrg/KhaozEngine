using System;

namespace KhaozEngine.Simulation;

/// <summary>Tracks authoritative world-clock state and advances it locally between messages.</summary>
public sealed class WorldClockMirror
{
    private WorldClock? clock;

    /// <summary>Whether the mirror has received its first valid authoritative state.</summary>
    public bool HasState => clock is not null;

    /// <summary>The current mirrored state, or the default state before initialization.</summary>
    public WorldClockState State => clock?.Snapshot ?? default;

    /// <summary>Replaces the mirror only when the complete authoritative payload is valid.</summary>
    public bool Apply(ReadOnlySpan<byte> payload)
    {
        if (!WorldClockCodec.TryDecodeState(payload, out WorldClockState state))
            return false;

        clock = new WorldClock(state.DayLengthSeconds, state.TimeOfDay)
        {
            TimeScale = state.TimeScale,
        };
        return true;
    }

    /// <summary>Advances the mirror locally when authoritative state is present.</summary>
    public void Advance(float dt) => clock?.Advance(dt);
}
