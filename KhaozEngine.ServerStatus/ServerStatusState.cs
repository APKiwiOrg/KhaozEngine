using System;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// The actionable state a client's waiting screen / reconnect logic consumes, derived by
/// <see cref="ServerStatusEvaluator.Evaluate"/> from a report + the local client version + poll staleness.
/// Distinct from the raw wire <see cref="ServerHealth"/>: it folds in the client-version gates and the
/// "endpoint unreachable / report too stale" case. No display strings live here - a game owns the words and
/// localizes them off the state.
/// </summary>
public enum ServerStatusState
{
    /// <summary>No trustworthy report: never polled, the retained report is too stale, or health is unknown.</summary>
    StatusUnknown = 0,

    /// <summary>Server is up and this client may connect.</summary>
    ServerOk = 1,

    /// <summary>Server is mid-deploy inside a downtime window. Show a "back soon" screen (see ExpectedBackUtc).</summary>
    ServerRestarting = 2,

    /// <summary>Server is down outside any planned window. Back off and retry.</summary>
    ServerDown = 3,

    /// <summary>This client is below the server's minimum version and must update before it can connect.</summary>
    UpdateRequired = 4,

    /// <summary>A newer client exists but this one can still connect. Offer an optional update.</summary>
    UpdateAvailable = 5,
}

/// <summary>
/// The evaluator's result: the derived <see cref="State"/> plus the fields a waiting screen needs alongside
/// it - the restart ETA (populated for <see cref="ServerStatusState.ServerRestarting"/>), the operator MOTD,
/// and the underlying report (null when the state is <see cref="ServerStatusState.StatusUnknown"/> with no
/// report ever received). ExpectedBackUtc and Motd are surfaced in every state where a report is retained, so
/// a game can show a countdown or message regardless of the headline state.
/// </summary>
public readonly record struct ServerStatusView(
    ServerStatusState State,
    DateTimeOffset? ExpectedBackUtc,
    string? Motd,
    ServerStatusReport? Report);
