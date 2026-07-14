using System;
using System.Collections.Generic;
using System.Globalization;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Stable, string machine keys for the rows <see cref="ServerStatusReadout.Build"/> emits. A game maps
/// each key to its own localized label. Never string-match a row by hand, reference these constants.
/// <see cref="All"/> lists them in the exact order <see cref="ServerStatusReadout.Build"/> emits rows.
/// </summary>
public static class ServerStatusReadoutKeys
{
    /// <summary>The report's <see cref="ServerHealth"/> token. Raw type <see cref="ServerHealth"/>?.</summary>
    public const string Health = "health";

    /// <summary>The deployed server build version. Raw type string?.</summary>
    public const string ServerVersion = "serverVersion";

    /// <summary>The server's client-version floor. Raw type string?.</summary>
    public const string MinClientVersion = "minClientVersion";

    /// <summary>The newest published client version. Raw type string?.</summary>
    public const string LatestClientVersion = "latestClientVersion";

    /// <summary>This build's own client version, as passed to <see cref="ServerStatusReadout.Build"/>. Raw type string.</summary>
    public const string ClientVersion = "clientVersion";

    /// <summary>Age of the server's last liveness heartbeat. Raw type <see cref="DateTimeOffset"/>?.</summary>
    public const string LastHeartbeat = "lastHeartbeat";

    /// <summary>Age of the last CI/CD deploy record. Raw type <see cref="DateTimeOffset"/>?.</summary>
    public const string LastDeploy = "lastDeploy";

    /// <summary>ETA for a restart in progress, when known. Raw type <see cref="DateTimeOffset"/>?.</summary>
    public const string ExpectedBack = "expectedBack";

    /// <summary>Age of the client's own last successful poll. Raw type <see cref="TimeSpan"/>?.</summary>
    public const string Staleness = "staleness";

    /// <summary>The evaluated <see cref="ServerStatusState"/> token. Raw type <see cref="ServerStatusState"/>.</summary>
    public const string State = "state";

    /// <summary>The operator message-of-the-day, when set. Raw type string?.</summary>
    public const string Motd = "motd";

    /// <summary>Every key above, in the exact order <see cref="ServerStatusReadout.Build"/> emits rows.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Health, ServerVersion, MinClientVersion, LatestClientVersion, ClientVersion,
        LastHeartbeat, LastDeploy, ExpectedBack, Staleness, State, Motd,
    };
}

/// <summary>
/// One row of a <see cref="ServerStatusReadout.Build"/> result: a stable machine <see cref="Key"/> (one of
/// <see cref="ServerStatusReadoutKeys"/>), a preformatted invariant-culture <see cref="Value"/> ready to draw
/// as-is, and the underlying <see cref="Raw"/> value for a game that wants to format it itself (e.g. a fully
/// localized duration string). <see cref="Raw"/> is null exactly when the row has nothing to show (no report,
/// or the field was never set) - see each key's doc in <see cref="ServerStatusReadoutKeys"/> for its CLR type.
/// </summary>
public readonly record struct ServerStatusReadoutRow(string Key, string Value, object? Raw);

/// <summary>
/// Builds the pure, ordered row list an in-game "server status" page renders. GPU-free and Gui-free by
/// design: the engine ships the structure, a game supplies the labels (via <see cref="ServerStatusReadoutKeys"/>)
/// and the Gui. <see cref="Build"/> takes no clock of its own (<c>nowUtc</c> is a parameter), does no IO, and
/// always returns the same 11 rows in the same order, so it is fully deterministic and unit-testable.
///
/// <para>Every row is always present, even when its data is missing (no report ever, an optional field left
/// unset, or an ETA that is not applicable to the current state): a missing value is an empty
/// <see cref="ServerStatusReadoutRow.Value"/> and a null <see cref="ServerStatusReadoutRow.Raw"/>, never a
/// dropped row. A game can therefore render a fixed row set and simply hide/gray a row whose value is empty.</para>
///
/// <para>Duration rows (<see cref="ServerStatusReadoutKeys.LastHeartbeat"/>, <see cref="ServerStatusReadoutKeys.LastDeploy"/>,
/// <see cref="ServerStatusReadoutKeys.ExpectedBack"/>, <see cref="ServerStatusReadoutKeys.Staleness"/>) are
/// formatted as compact, invariant-culture, English strings ("12 s ago", "3 min ago", "2 h ago", "in 5 min").
/// This is deliberately not localized: the engine has no localization catalog dependency here (this package is
/// GPU-free and Gui-free), and these strings feed a game-localized page anyway. A game that wants a fully
/// localized duration should format it from the row's <see cref="ServerStatusReadoutRow.Raw"/> value instead
/// of the preformatted <see cref="ServerStatusReadoutRow.Value"/>.</para>
/// </summary>
public static class ServerStatusReadout
{
    /// <summary>
    /// Builds the row list for one instant. Report-derived rows (<see cref="ServerStatusReadoutKeys.Health"/>,
    /// <see cref="ServerStatusReadoutKeys.ServerVersion"/>, <see cref="ServerStatusReadoutKeys.MinClientVersion"/>,
    /// <see cref="ServerStatusReadoutKeys.LatestClientVersion"/>, <see cref="ServerStatusReadoutKeys.LastHeartbeat"/>,
    /// <see cref="ServerStatusReadoutKeys.LastDeploy"/>) read <paramref name="view"/>'s retained report (which,
    /// per <see cref="ServerStatusEvaluator"/>, can still be a stale-but-present report while the state reads
    /// <see cref="ServerStatusState.StatusUnknown"/>) so a "back soon" note keeps showing during a brief outage.
    /// <see cref="ServerStatusReadoutKeys.Staleness"/> reads <paramref name="snapshot"/> directly (the client's
    /// own poll clock), since it can differ from the report's data even when the report is fresh.
    /// </summary>
    /// <param name="snapshot">The poller's latest snapshot, for the client-side poll staleness row.</param>
    /// <param name="view">The evaluator's result for this snapshot, for the state and report-derived rows.</param>
    /// <param name="clientVersion">This build's own client version (x.y.z), echoed back as its own row.</param>
    /// <param name="nowUtc">Current UTC instant. All durations are measured against this, never a live clock.</param>
    public static IReadOnlyList<ServerStatusReadoutRow> Build(
        ServerStatusSnapshot snapshot,
        ServerStatusView view,
        string clientVersion,
        DateTimeOffset nowUtc)
    {
        ServerStatusReport? report = view.Report;
        TimeSpan? staleness = snapshot.LastSuccessUtc is null ? (TimeSpan?)null : snapshot.StalenessAt(nowUtc);
        DateTimeOffset? expectedBack = view.ExpectedBackUtc;

        return new[]
        {
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.Health, report?.Health.ToString() ?? "", report?.Health),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.ServerVersion, report?.ServerVersion ?? "", report?.ServerVersion),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.MinClientVersion, report?.MinClientVersion ?? "", report?.MinClientVersion),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.LatestClientVersion, report?.LatestClientVersion ?? "", report?.LatestClientVersion),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.ClientVersion, clientVersion, clientVersion),
            new ServerStatusReadoutRow(
                ServerStatusReadoutKeys.LastHeartbeat,
                report is null ? "" : FormatAgo(nowUtc - report.LastHeartbeatUtc),
                report?.LastHeartbeatUtc),
            new ServerStatusReadoutRow(
                ServerStatusReadoutKeys.LastDeploy,
                report is null ? "" : FormatAgo(nowUtc - report.LastDeployUtc),
                report?.LastDeployUtc),
            new ServerStatusReadoutRow(
                ServerStatusReadoutKeys.ExpectedBack,
                expectedBack is { } eta ? FormatIn(eta - nowUtc) : "",
                expectedBack),
            new ServerStatusReadoutRow(
                ServerStatusReadoutKeys.Staleness,
                staleness is { } age ? FormatAgo(age) : "",
                staleness),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.State, view.State.ToString(), view.State),
            new ServerStatusReadoutRow(ServerStatusReadoutKeys.Motd, view.Motd ?? "", view.Motd),
        };
    }

    /// <summary>Formats a past duration as a compact "N unit ago" string. Negative (clock skew) clamps to zero.</summary>
    private static string FormatAgo(TimeSpan elapsed) => FormatMagnitude(elapsed) + " ago";

    /// <summary>Formats a future duration as a compact "in N unit" string. Negative (already due) clamps to zero.</summary>
    private static string FormatIn(TimeSpan remaining) => "in " + FormatMagnitude(remaining);

    /// <summary>
    /// Compact invariant-culture magnitude: seconds under a minute, minutes under an hour, hours under a day,
    /// whole days beyond that. Each threshold is exclusive on the smaller unit (e.g. exactly 60 s reads
    /// "1 min", not "60 s"), so the boundaries are unambiguous.
    /// </summary>
    private static string FormatMagnitude(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        }

        if (span.TotalMinutes < 60)
        {
            return ((long)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (span.TotalHours < 24)
        {
            return ((long)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
        }

        return ((long)span.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";
    }
}
