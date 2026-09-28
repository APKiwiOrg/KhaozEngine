using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Newest applied snapshot plus at most one tick of presentation advance. Independent of movement clocks.</summary>
internal sealed class TileCombatPresentationClock
{
    long anchor = -1;
    double elapsedTicks;

    internal double Tick => anchor < 0 ? -1d : anchor + elapsedTicks;

    internal void Observe(long serverTick)
    {
        // An integer newer anchor is at least one tick ahead, so it cannot rewind the bounded old estimate.
        if (serverTick < 0 || serverTick <= anchor) return;
        anchor = serverTick;
        elapsedTicks = 0d;
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
    }
}
