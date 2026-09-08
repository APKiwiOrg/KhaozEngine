using System;
using System.Collections.Concurrent;

namespace KhaozEngine.Simulation;

/// <summary>Controls authoritative clock broadcast cadence and command bounds.</summary>
public sealed class WorldClockHostOptions
{
    /// <summary>Real seconds between periodic authoritative state broadcasts.</summary>
    public float BroadcastIntervalSeconds { get; init; } = 5f;

    /// <summary>Largest time scale accepted from an authoritative command.</summary>
    public float MaxTimeScale { get; init; } = 1000f;

    /// <summary>Smallest day length accepted from an authoritative command.</summary>
    public float MinDayLengthSeconds { get; init; } = 60f;
}

/// <summary>
/// Owns the authoritative clock on the tick thread while accepting commands and late-join pushes from other
/// threads.
/// </summary>
public sealed class WorldClockHost
{
    private readonly Action<byte[]> broadcast;
    private readonly Action<int, byte[]> sendTo;
    private readonly WorldClockHostOptions options;
    private readonly ConcurrentQueue<WorldClockCommand> commands = new();
    private readonly ConcurrentQueue<int> pushes = new();
    private float sinceBroadcast;
    private volatile object publishedSnapshot;

    /// <summary>Creates a transport-free authoritative world-clock host.</summary>
    public WorldClockHost(
        WorldClock clock,
        Action<byte[]> broadcast,
        Action<int, byte[]> sendTo,
        WorldClockHostOptions? options = null)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.broadcast = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
        this.sendTo = sendTo ?? throw new ArgumentNullException(nameof(sendTo));
        this.options = options ?? new WorldClockHostOptions();
        publishedSnapshot = Clock.Snapshot;
    }

    /// <summary>The tick-thread-owned authoritative clock.</summary>
    public WorldClock Clock { get; }

    /// <summary>The last tick-consistent state published for lock-free cross-thread reads.</summary>
    public WorldClockState Snapshot => (WorldClockState)publishedSnapshot;

    /// <summary>Queues a command for validation and application on the tick thread.</summary>
    public void EnqueueCommand(WorldClockCommand command) => commands.Enqueue(command);

    /// <summary>Decodes and queues a valid command payload for the tick thread.</summary>
    public bool TryEnqueueCommand(ReadOnlySpan<byte> payload)
    {
        if (!WorldClockCodec.TryDecodeCommand(payload, out WorldClockCommand command))
            return false;

        commands.Enqueue(command);
        return true;
    }

    /// <summary>Queues an authoritative state push to one slot for the next tick.</summary>
    public void PushTo(int slot) => pushes.Enqueue(slot);

    /// <summary>Advances authority, applies commands, sends state, then publishes the completed tick snapshot.</summary>
    public void Tick(float dt)
    {
        Clock.Advance(dt);
        if (float.IsFinite(dt))
            sinceBroadcast += dt;

        while (commands.TryDequeue(out WorldClockCommand command))
        {
            if (!Apply(command))
                continue;

            broadcast(WorldClockCodec.EncodeState(Clock.Snapshot));
            sinceBroadcast = 0f;
        }

        if (sinceBroadcast >= options.BroadcastIntervalSeconds)
        {
            broadcast(WorldClockCodec.EncodeState(Clock.Snapshot));
            sinceBroadcast = 0f;
        }

        while (pushes.TryDequeue(out int slot))
            sendTo(slot, WorldClockCodec.EncodeState(Clock.Snapshot));

        publishedSnapshot = Clock.Snapshot;
    }

    private bool Apply(WorldClockCommand command)
    {
        if (!float.IsFinite(command.Value))
            return false;

        switch (command.Kind)
        {
            case WorldClockCommandKind.SetTimeOfDay:
                return Clock.SetTimeOfDay(command.Value);
            case WorldClockCommandKind.SetTimeScale:
                Clock.TimeScale = Math.Clamp(command.Value, 0f, options.MaxTimeScale);
                return true;
            case WorldClockCommandKind.SetDayLength:
                Clock.DayLengthSeconds = MathF.Max(command.Value, options.MinDayLengthSeconds);
                return true;
            default:
                return false;
        }
    }
}
