using System;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>Tuning for <see cref="ServerStatusEvaluator.Evaluate"/>.</summary>
public sealed class ServerStatusEvaluationOptions
{
    /// <summary>
    /// How old the last successful report may be before the evaluator stops trusting it and returns
    /// <see cref="ServerStatusState.StatusUnknown"/>. Default 90 seconds - three times the client's 30 s poll
    /// interval, so a single missed poll does not flip the state, but a real outage does.
    /// </summary>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Pure mapping from (retained report + local client version + poll staleness) to an actionable
/// <see cref="ServerStatusView"/>. No IO, no clock of its own (the caller passes <c>nowUtc</c>), so it is
/// fully deterministic and unit-testable.
///
/// <para>Precedence, first match wins: (1) no report or the report is staler than
/// <see cref="ServerStatusEvaluationOptions.MaxStaleness"/> or its health is Unknown -> StatusUnknown,
/// (2) Down -> ServerDown, (3) Restarting -> ServerRestarting, (4) Healthy but the client is below
/// minClientVersion -> UpdateRequired, (5) Healthy but below latestClientVersion -> UpdateAvailable,
/// (6) otherwise ServerOk. Transient health (Down/Restarting) is surfaced ahead of the version gates on
/// purpose: during a deploy window the "back soon" screen wins, and the version gate applies once the server
/// reports Healthy again. A consumer that wants a different policy can read the raw report fields off the view.</para>
/// </summary>
public static class ServerStatusEvaluator
{
    /// <summary>Evaluates the snapshot for a specific client version at a specific instant.</summary>
    /// <param name="snapshot">The poller's latest snapshot (retained report + staleness).</param>
    /// <param name="localClientVersion">This build's version (x.y.z) for the update gates.</param>
    /// <param name="nowUtc">Current UTC instant, used only to measure staleness.</param>
    /// <param name="options">Staleness tuning; a default 90 s window is used when null.</param>
    public static ServerStatusView Evaluate(
        ServerStatusSnapshot snapshot,
        string localClientVersion,
        DateTimeOffset nowUtc,
        ServerStatusEvaluationOptions? options = null)
    {
        options ??= new ServerStatusEvaluationOptions();
        ServerStatusReport? report = snapshot.LastReport;

        // No report ever, or the last-known one is too old to trust -> unknown. Still surface the retained
        // report's MOTD / ETA if we have one, so a stale "back soon" note keeps showing during an outage.
        if (report is null || snapshot.StalenessAt(nowUtc) > options.MaxStaleness)
        {
            return new ServerStatusView(ServerStatusState.StatusUnknown, report?.ExpectedBackUtc, report?.Motd, report);
        }

        switch (report.Health)
        {
            case ServerHealth.Down:
                return View(ServerStatusState.ServerDown, report);

            case ServerHealth.Restarting:
                return View(ServerStatusState.ServerRestarting, report);

            case ServerHealth.Healthy:
                if (!string.IsNullOrWhiteSpace(report.MinClientVersion)
                    && VersionOrder.IsBelow(localClientVersion, report.MinClientVersion))
                {
                    return View(ServerStatusState.UpdateRequired, report);
                }

                if (!string.IsNullOrWhiteSpace(report.LatestClientVersion)
                    && VersionOrder.IsBelow(localClientVersion, report.LatestClientVersion))
                {
                    return View(ServerStatusState.UpdateAvailable, report);
                }

                return View(ServerStatusState.ServerOk, report);

            case ServerHealth.Unknown:
            default:
                return View(ServerStatusState.StatusUnknown, report);
        }
    }

    private static ServerStatusView View(ServerStatusState state, ServerStatusReport report) =>
        new(state, report.ExpectedBackUtc, report.Motd, report);
}
