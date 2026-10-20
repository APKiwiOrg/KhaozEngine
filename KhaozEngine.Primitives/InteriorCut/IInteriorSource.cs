using System;
using System.Collections.Generic;

namespace KhaozEngine.Primitives;

/// <summary>Collects detached world-format-independent interiors touching a window.</summary>
public interface IInteriorSource
{
    InteriorSourceResult Collect(InteriorWindow window);
}

/// <summary>Source-wide vertical encoding, kept stable across window changes.</summary>
public readonly record struct InteriorEncoding(float BaseY, float StepY)
{
    public static InteriorEncoding For(float lowestFloorY, float highestTopY)
    {
        if (!float.IsFinite(lowestFloorY) || !float.IsFinite(highestTopY) || highestTopY < lowestFloorY)
            throw new ArgumentException("Interior height bounds must be finite and ordered.");
        double range = (double)highestTopY - lowestFloorY;
        var encoding = new InteriorEncoding(lowestFloorY, range <= 655.35d ? 0.01f : (float)(range / 65534d));
        encoding.Validate();
        return encoding;
    }

    internal void Validate()
    {
        if (!float.IsFinite(BaseY) || !float.IsFinite(StepY) || StepY <= 0f || !float.IsFinite(BaseY + 65535f * StepY))
            throw new ArgumentException("Interior encoding must have a finite base, positive step and finite maximum.");
    }
}

public sealed record InteriorSourceResult(IReadOnlyList<InteriorVolume> Volumes, InteriorEncoding Encoding, IReadOnlyList<string> Issues);
