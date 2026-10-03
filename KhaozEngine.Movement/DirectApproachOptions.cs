using System;

namespace KhaozEngine.Movement;

/// <summary>Progress windows for <see cref="DirectMoveToRange"/>. A window of N ticks spans N intervals between
/// N + 1 counted samples and is first eligible on the (N + 1)th counted tick. A window holds at most 65,535 ticks.
/// There are no engine defaults for the windows. <see cref="MaxDropMetres"/> is an opt-in drop allowance that
/// defaults to zero.</summary>
public sealed record DirectApproachOptions
{
    private const int MaxWindowTicks = ushort.MaxValue;
    private readonly float _maxDropMetres;

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

    /// <summary>How far below the current feet a step that leaves the ground may land and still be taken. Zero, the
    /// default, refuses every step that leaves the ground. A positive allowance admits a step that settles, under zero
    /// input, grounded and not swimming no lower than this below the current feet. A landing the next step would swim
    /// from is refused. A wade-depth landing is admitted.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or is negative.</exception>
    public float MaxDropMetres
    {
        get => _maxDropMetres;
        init => _maxDropMetres = float.IsFinite(value) && value >= 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxDropMetres), "Drop allowance must be finite and not negative.");
    }

    private static int Ticks(int value, string name)
        => value is > 0 and <= MaxWindowTicks
            ? value : throw new ArgumentOutOfRangeException(name, "Window ticks must be from 1 to 65,535.");

    private static float Metres(float value, string name)
        => float.IsFinite(value) && value > 0f
            ? value : throw new ArgumentOutOfRangeException(name, "Window distance must be finite and positive.");
}
