using System;

namespace KhaozEngine.Movement;

/// <summary>Progress windows for <see cref="DirectMoveToRange"/>. A window of N ticks spans N intervals between
/// N + 1 counted samples and is first eligible on the (N + 1)th counted tick. There are no engine defaults.</summary>
public sealed record DirectApproachOptions
{
    /// <exception cref="ArgumentOutOfRangeException">A window is not positive, or a distance is not finite and positive.</exception>
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
        => value > 0 ? value : throw new ArgumentOutOfRangeException(name, "Window ticks must be positive.");

    private static float Metres(float value, string name)
        => float.IsFinite(value) && value > 0f
            ? value : throw new ArgumentOutOfRangeException(name, "Window distance must be finite and positive.");
}
