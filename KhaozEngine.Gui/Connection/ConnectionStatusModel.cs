using System;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// Coarse connectivity state of the game's connection to its server: the signal a consumer feeds into
/// <see cref="ConnectionStatusController.Update"/> every frame via <see cref="ConnectionStatusSignals.Phase"/>.
/// </summary>
public enum ConnectionPhase
{
    /// <summary>Connected and functioning normally.</summary>
    Connected,
    /// <summary>The initial connection attempt is in progress (never yet connected this session).</summary>
    Connecting,
    /// <summary>A previously live connection dropped and the client is retrying.</summary>
    Reconnecting,
    /// <summary>Disconnected, with no active connection attempt.</summary>
    Disconnected,
}

/// <summary>
/// How much connection-outage UI a <see cref="ConnectionStatusController"/> decision asks the consumer to show.
/// </summary>
public enum ConnectionUiMode
{
    /// <summary>Show nothing: connected, or a fresh outage that has not yet crossed a display threshold.</summary>
    None,
    /// <summary>Show a small, non-blocking indicator. The consumer draws its own banner, the engine ships none.</summary>
    Banner,
    /// <summary>Show the full-screen, modal <see cref="ReconnectScreen"/> takeover.</summary>
    Screen,
}

/// <summary>Why a connection takeover is showing, so a shared visual can pick the right title and copy.</summary>
public enum ConnectionStatusKind
{
    /// <summary>An unplanned drop. The client is retrying to reconnect.</summary>
    Reconnecting,
    /// <summary>A planned, server-initiated maintenance/update window (may carry an ETA).</summary>
    PlannedUpdate,
}

/// <summary>
/// The per-frame connection signal a consumer feeds into <see cref="ConnectionStatusController.Update"/>. Plain
/// data and netcode-free: a consumer maps its own transport/server-status types onto this shape.
/// </summary>
public readonly struct ConnectionStatusSignals
{
    /// <summary>The current coarse connection phase.</summary>
    public ConnectionPhase Phase { get; init; }

    /// <summary>
    /// True when the current outage is a planned server update/maintenance window rather than an unplanned drop.
    /// Meaningless while <see cref="Phase"/> is <see cref="ConnectionPhase.Connected"/>.
    /// </summary>
    public bool PlannedUpdate { get; init; }

    /// <summary>The estimated time (UTC) the outage ends, or null when no ETA is known or trusted.</summary>
    public DateTime? EtaUtc { get; init; }

    /// <summary>The current reconnect-attempt counter (1-based), or 0 when not tracked.</summary>
    public int Attempt { get; init; }

    /// <summary>Seconds until the next reconnect attempt, or null when not tracked.</summary>
    public float? SecondsUntilRetry { get; init; }

    /// <summary>An optional server-supplied message key to show instead of (or alongside) the built-in copy.</summary>
    public StringId? MessageId { get; init; }
}

/// <summary>
/// The per-frame decision <see cref="ConnectionStatusController.Update"/> hands back: how much UI to show, and
/// the data to show it with. A consumer switches on <see cref="Mode"/> and renders accordingly - nothing, its
/// own banner, or the engine's <see cref="ReconnectScreen"/>.
/// </summary>
public readonly struct ConnectionStatusView
{
    /// <summary>How much connection-outage UI to show this frame.</summary>
    public ConnectionUiMode Mode { get; init; }

    /// <summary>Which kind of outage this is. Meaningless when <see cref="Mode"/> is <see cref="ConnectionUiMode.None"/>.</summary>
    public ConnectionStatusKind Kind { get; init; }

    /// <summary>
    /// The outage ETA in UTC, or null when unknown. Carried through verbatim from the signals (or, during an
    /// anti-flicker hold, the cached outage) with no clamping - a renderer applies its own at/after-zero clamp.
    /// </summary>
    public DateTime? EtaUtc { get; init; }

    /// <summary>The current reconnect-attempt counter, or 0 when not tracked.</summary>
    public int Attempt { get; init; }

    /// <summary>Seconds until the next reconnect attempt, or null when not tracked.</summary>
    public float? SecondsUntilRetry { get; init; }

    /// <summary>An optional server-supplied message key, or null for the built-in copy.</summary>
    public StringId? MessageId { get; init; }
}
