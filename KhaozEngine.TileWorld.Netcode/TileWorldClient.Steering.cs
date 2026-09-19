namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The STEERING half of <see cref="TileWorldClient"/>: the held direction a keyboard player walks on, beside the
/// click event door the rest of the client is built around. Nothing here touches the wire or the prediction: the
/// level is read once per command tick by <c>OnCommandTick</c> and turned into an ordinary
/// <see cref="TileCommand.Steer"/> that both heads step identically.
/// </summary>
public sealed partial class TileWorldClient
{
    TileMoveMode steeringMode;

    /// <summary>The direction currently held, or null. A LEVEL, where <see cref="Queue"/> is an event: a head
    /// sets it every frame from its input and the command clock reads it on every tick, so a steering tick can
    /// never be lost to frame timing.</summary>
    public TileDirection? Steering { get; private set; }

    /// <summary>Holds or releases a steering direction. While one is held, every command tick with no queued
    /// click predicts and sends <see cref="TileCommand.Steer"/> instead of <see cref="TileCommand.Continue"/>.
    /// <para><paramref name="mode"/> rides the level and is NOT adopted as <see cref="RunMode"/>, unlike a
    /// queued click's. A head that lets a modifier invert the pace while steering passes the inverted mode
    /// here, and the saved toggle is what the next Continue carries once the keys are released.</para></summary>
    /// <param name="direction">The held direction, or null to release.</param>
    /// <param name="mode">Walk or run for steered steps. Ignored when releasing.</param>
    public void SetSteering(TileDirection? direction, TileMoveMode mode)
    {
        Steering = direction;
        steeringMode = mode;
    }
}
