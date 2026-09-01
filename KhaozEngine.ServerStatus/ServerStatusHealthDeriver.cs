using System;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// The deploy state a status endpoint knows about, independent of any heartbeat: when CI/CD last deployed the
/// current build, and when it expects to be back. <c>default</c> means no window at all, which is the ordinary
/// steady state. Both fields are nullable because a game may write one, the other, or neither.
/// </summary>
/// <param name="LastDeployUtc">When CI/CD last wrote a deploy record, or null when it never has.</param>
/// <param name="ExpectedBackUtc">
/// When the restart is expected to finish, or null when nobody declared one. A value in the past no longer
/// explains anything, so it stops counting as an active window rather than pinning the server to Restarting.
/// </param>
public readonly record struct ServerDeployWindow(DateTimeOffset? LastDeployUtc, DateTimeOffset? ExpectedBackUtc);

/// <summary>Tuning for <see cref="ServerStatusHealthDeriver.Derive"/>.</summary>
public sealed class ServerStatusHealthOptions
{
    /// <summary>
    /// Heartbeat age beyond which health degrades to <see cref="ServerHealth.Down"/>, outside any deploy
    /// window. Default 60 seconds. Set this to a comfortable multiple of the server's heartbeat interval, so
    /// one skipped write does not read as an outage.
    /// </summary>
    public TimeSpan HeartbeatStaleAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long after <see cref="ServerDeployWindow.LastDeployUtc"/> health still reports
    /// <see cref="ServerHealth.Restarting"/> when no explicit end instant was declared. Default 120 seconds,
    /// which is the window a rollout needs before its replacement process starts beating.
    /// </summary>
    public TimeSpan DeployGrace { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Pure server-side derivation of <see cref="ServerHealth"/> from heartbeat age plus the deploy window. No IO
/// and no clock of its own (the caller passes <c>nowUtc</c>), so a status endpoint can unit-test its own
/// answers without a database.
///
/// <para>This is the producing half of the contract <see cref="ServerStatusEvaluator"/> consumes, and it ships
/// here for that reason rather than as a convenience. Two games wrote the same deriver, four lines apart, each
/// carrying a comment about keeping its precedence in step with the evaluator by hand. Precedence that has to
/// agree across a wire is not something to maintain in two places.</para>
///
/// <para>Precedence, first match wins: (1) no heartbeat has ever been written -> Unknown, (2) a deploy window
/// is active -> Restarting, (3) the heartbeat is fresher than
/// <see cref="ServerStatusHealthOptions.HeartbeatStaleAfter"/> -> Healthy, (4) otherwise Down.</para>
/// </summary>
public static class ServerStatusHealthDeriver
{
    /// <summary>Derives health at a specific instant.</summary>
    /// <param name="heartbeat">
    /// The newest heartbeat the status store holds, or null when it holds none at all. Null is "never seen",
    /// not "seen long ago": a server with no heartbeat in its history has never been observed up, so calling
    /// it Down or Restarting would be inventing history it does not have.
    /// </param>
    /// <param name="deployWindow">What CI/CD declared. Pass <c>default</c> when nothing is in flight.</param>
    /// <param name="nowUtc">Current UTC instant, used only to measure ages.</param>
    /// <param name="options">Thresholds. Defaults (60 s stale, 120 s grace) are used when null.</param>
    public static ServerHealth Derive(
        ServerHeartbeat? heartbeat,
        ServerDeployWindow deployWindow,
        DateTimeOffset nowUtc,
        ServerStatusHealthOptions? options = null)
    {
        if (heartbeat is not { } beat)
        {
            return ServerHealth.Unknown;
        }

        options ??= new ServerStatusHealthOptions();

        // A declared window wins even when the old process is still beating: CI/CD set it deliberately to
        // signal a rollout in progress, and a client is better off waiting than connecting to a server that is
        // about to go away under it.
        bool expectedBackInFuture = deployWindow.ExpectedBackUtc is { } eta && eta > nowUtc;
        bool withinDeployGrace = deployWindow.LastDeployUtc is { } deployedAt
            && nowUtc - deployedAt <= options.DeployGrace;
        if (expectedBackInFuture || withinDeployGrace)
        {
            return ServerHealth.Restarting;
        }

        return nowUtc - beat.TimestampUtc <= options.HeartbeatStaleAfter
            ? ServerHealth.Healthy
            : ServerHealth.Down;
    }
}
