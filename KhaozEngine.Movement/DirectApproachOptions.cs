using System;

namespace KhaozEngine.Movement;

/// <summary>Progress windows for <see cref="DirectMoveToRange"/>. A window of N ticks spans N intervals between
/// N + 1 counted samples and is first eligible on the (N + 1)th counted tick. A window holds at most 65,535 ticks.
/// There are no engine defaults.</summary>
public sealed record DirectApproachOptions
{
    private const int MaxWindowTicks = ushort.MaxValue;

    /// <exception cref="ArgumentOutOfRangeException">A window is not from 1 to 65,535 ticks, or a distance is not
    /// finite and positive.</exception>
    public DirectApproachOptions(int stallWindowTicks, float stallTravelMetres,
        int approachWindowTicks, float approachGainMetres)
    {
        StallWindowTicks = Ticks(stallWindowTicks, nameof(stallWindowTicks));
        StallTravelMetres = Metres(stallTravelMetres, nameof(stallTravelMetres));
        ApproachWindowTicks = Ticks(approachWindowTicks, nameof(approachWindowTicks));
        ApproachGainMetres = Metres(approachGainMetres, nameof(approachGainMetres));
    }

    /// <summary>Counted intervals across which net horizontal feet displacement must reach the stall travel.</summary>
    public int StallWindowTicks { get; }

    /// <summary>Net horizontal displacement below which a full stall window latches Blocked.</summary>
    public float StallTravelMetres { get; }

    /// <summary>Counted intervals across which reach distance must fall by the approach gain.</summary>
    public int ApproachWindowTicks { get; }

    /// <summary>Reach distance gain below which a full approach window latches Blocked for a static target.</summary>
    public float ApproachGainMetres { get; }

    private static int Ticks(int value, string name)
        => value is > 0 and <= MaxWindowTicks
            ? value : throw new ArgumentOutOfRangeException(name, "Window ticks must be from 1 to 65,535.");

    private static float Metres(float value, string name)
        => float.IsFinite(value) && value > 0f
            ? value : throw new ArgumentOutOfRangeException(name, "Window distance must be finite and positive.");
}
