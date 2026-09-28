using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Newest applied snapshot plus at most one tick of presentation advance. Independent of movement clocks.</summary>
internal sealed class TileCombatPresentationClock
{
    long anchor = -1;
    double elapsedTicks;
    double maximumTick;

    internal double Tick => anchor < 0 ? -1d : Math.Min(anchor + elapsedTicks, maximumTick);

    internal void Observe(long serverTick)
    {
        // An integer newer anchor is at least one tick ahead, so it cannot rewind the bounded old estimate.
        if (serverTick < 0 || serverTick <= anchor) return;
        anchor = serverTick;
        elapsedTicks = 0d;
        // The exact one-tick limit can round UP when converted to double. Use its representable floor so the
        // clock cannot skip into an impact two ticks away. Unsigned arithmetic also represents long.MaxValue + 1.
        ulong exactLimit = (ulong)serverTick + 1UL;
        double roundedLimit = exactLimit;
        maximumTick = (ulong)roundedLimit > exactLimit ? Math.BitDecrement(roundedLimit) : roundedLimit;
    }

    internal void Advance(float dt, float tickSeconds)
    {
        if (anchor < 0 || !float.IsFinite(dt) || dt <= 0f
            || !float.IsFinite(tickSeconds) || tickSeconds <= 0f) return;
        elapsedTicks = Math.Min(1d, elapsedTicks + (double)dt / tickSeconds);
    }

    internal void Clear()
    {
        anchor = -1;
        elapsedTicks = 0d;
        maximumTick = 0d;
    }
}
