using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Cadence driver for the liveness heartbeat: writes a <see cref="ServerHeartbeat"/> through an
/// <see cref="IServerHeartbeatSink"/> no more often than a configured interval. The game server calls
/// <see cref="TickAsync"/> from its own loop (passing the current UTC), or runs the built-in
/// <see cref="RunAsync"/> loop on a background task. Single-caller: drive it from one place (the server tick
/// or one background loop), not concurrently.
///
/// <para>A write failure is contained (logged, never rethrown into the server loop) and does NOT reset the
/// cadence, so a transient DB error skips at most one beat instead of causing a retry storm or freezing the
/// server. A skipped beat is truthful: the endpoint sees a staler heartbeat and reports health accordingly.
/// <see cref="ConsecutiveFailures"/> / <see cref="LastError"/> surface the failure for ops.</para>
/// </summary>
public sealed class ServerHeartbeatService
{
    private readonly IServerHeartbeatSink sink;
    private readonly Func<string> versionProvider;
    private readonly TimeSpan interval;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ILogger log = Log.For<ServerHeartbeatService>();

    private DateTimeOffset? lastBeatUtc;
    private int consecutiveFailures;
    private Exception? lastError;

    /// <summary>Fixed-version convenience ctor. Default interval 15 seconds.</summary>
    public ServerHeartbeatService(
        IServerHeartbeatSink sink,
        string serverVersion,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : this(sink, () => serverVersion, interval, delay)
    {
    }

    /// <summary>
    /// Builds the service. <paramref name="versionProvider"/> is read fresh on each beat so a live server can
    /// report a rolling version. <paramref name="delay"/> is an injectable seam so <see cref="RunAsync"/> is
    /// headless-testable. It defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public ServerHeartbeatService(
        IServerHeartbeatSink sink,
        Func<string> versionProvider,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        this.interval = interval ?? TimeSpan.FromSeconds(15);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>UTC of the last beat that was attempted (whether or not the write succeeded), or null before the first.</summary>
    public DateTimeOffset? LastBeatUtc => lastBeatUtc;

    /// <summary>Consecutive write failures since the last success. Zero right after a successful write.</summary>
    public int ConsecutiveFailures => consecutiveFailures;

    /// <summary>The last write exception (contained, not thrown), or null if the last beat succeeded.</summary>
    public Exception? LastError => lastError;

    /// <summary>True when <paramref name="nowUtc"/> is at least one interval past the last beat (or none yet).</summary>
    public bool IsDue(DateTimeOffset nowUtc) => lastBeatUtc is not { } last || nowUtc - last >= interval;

    /// <summary>
    /// Writes a heartbeat if one is due at <paramref name="nowUtc"/>, else does nothing. Returns true when a
    /// beat was written, false when not due OR when the write failed (inspect <see cref="LastError"/>). The
    /// cadence marker advances on any due attempt, so a failing sink does not busy-retry. Never throws
    /// (barring cancellation of the passed token surfacing from the sink).
    /// </summary>
    public async Task<bool> TickAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (!IsDue(nowUtc))
        {
            return false;
        }

        // Advance the cadence marker BEFORE awaiting the write: a slow or failing write must not let the next
        // Tick fire again within the interval (no storm), and a skipped beat truthfully ages the endpoint's view.
        lastBeatUtc = nowUtc;

        try
        {
            await sink.WriteAsync(new ServerHeartbeat(nowUtc, versionProvider()), cancellationToken).ConfigureAwait(false);
            consecutiveFailures = 0;
            lastError = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            lastError = ex;
            log.Warn($"Heartbeat write failed (attempt {consecutiveFailures} since last success): {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Runs the heartbeat loop until <paramref name="cancellationToken"/> is cancelled: beat immediately, then
    /// wait one interval and beat again. Returns cleanly on cancellation. Use this when the server has no
    /// natural tick to hang <see cref="TickAsync"/> off.
    /// </summary>
    public async Task RunAsync(Func<DateTimeOffset> clock, CancellationToken cancellationToken)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await TickAsync(clock(), cancellationToken).ConfigureAwait(false);

            try
            {
                await delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
