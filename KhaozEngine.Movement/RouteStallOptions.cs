namespace KhaozEngine.Movement;

/// <summary>Stall window for <see cref="MoveToRange"/>, opted in by <see cref="RouteApproachOptions"/>. A window of N
/// ticks spans N intervals between N + 1 counted samples and is first eligible on the (N + 1)th counted tick. A window
/// holds at most 65,535 ticks. There is no engine default.</summary>
public sealed record RouteStallOptions
{
    /// <exception cref="System.ArgumentOutOfRangeException">The window is not from 1 to 65,535 ticks, or the travel
    /// is not finite and positive.</exception>
    public RouteStallOptions(int windowTicks, float travelMetres)
    {
        WindowTicks = DirectApproachOptions.Ticks(windowTicks, nameof(windowTicks));
        TravelMetres = DirectApproachOptions.Metres(travelMetres, nameof(travelMetres));
    }

    /// <summary>Counted intervals across which net horizontal feet displacement must reach the travel.</summary>
    public int WindowTicks { get; }

    /// <summary>Net horizontal displacement below which a full window latches Blocked.</summary>
    public float TravelMetres { get; }
}
