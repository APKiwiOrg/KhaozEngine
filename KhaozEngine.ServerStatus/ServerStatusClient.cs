using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>Tuning for <see cref="ServerStatusClient"/>.</summary>
public sealed class ServerStatusClientOptions
{
    /// <summary>How often <see cref="ServerStatusClient.RunAsync"/> polls the endpoint. Default 30 seconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Polls an <see cref="IServerStatusSource"/> and exposes the latest <see cref="ServerStatusSnapshot"/>.
/// Never throws to the caller: a failed fetch retains the last-known report and bumps the staleness/failure
/// counters instead of surfacing an error, so a UI can keep rendering the last state while the endpoint is
/// unreachable. Feed <see cref="Current"/> into <see cref="ServerStatusEvaluator.Evaluate"/> for an
/// actionable state. Drive it either by calling <see cref="PollOnceAsync"/> yourself (e.g. from a game tick)
/// or by running the built-in <see cref="RunAsync"/> loop on a background task.
/// </summary>
public sealed class ServerStatusClient
{
    private readonly IServerStatusSource source;
    private readonly ServerStatusClientOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    // Guards the snapshot swap so a background RunAsync loop and a foreground Current read don't tear.
    private readonly object gate = new();
    private ServerStatusSnapshot snapshot = ServerStatusSnapshot.Empty;

    /// <summary>
    /// Builds a client. <paramref name="clock"/> and <paramref name="delay"/> are injectable seams so the
    /// poll loop is headless-testable with no real wall-clock or timer. Both default to the system clock and
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public ServerStatusClient(
        IServerStatusSource source,
        ServerStatusClientOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.options = options ?? new ServerStatusClientOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>The latest snapshot. Safe to read from any thread.</summary>
    public ServerStatusSnapshot Current
    {
        get { lock (gate) { return snapshot; } }
    }

    /// <summary>
    /// Fetches once and folds the result into <see cref="Current"/>. On success the fresh report replaces the
    /// retained one and the failure counter resets. On a miss the retained report survives and the attempt/
    /// failure counters advance. Returns the resulting snapshot. Never throws (barring cancellation).
    /// </summary>
    public async Task<ServerStatusSnapshot> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ServerStatusReport? report = await source.FetchAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock();

        lock (gate)
        {
            snapshot = report is not null
                ? new ServerStatusSnapshot
                {
                    LastReport = report,
                    LastSuccessUtc = now,
                    LastAttemptUtc = now,
                    ConsecutiveFailures = 0,
                }
                : snapshot with
                {
                    LastAttemptUtc = now,
                    ConsecutiveFailures = snapshot.ConsecutiveFailures + 1,
                };
            return snapshot;
        }
    }

    /// <summary>
    /// Runs the poll loop until <paramref name="cancellationToken"/> is cancelled: poll immediately, then
    /// wait <see cref="ServerStatusClientOptions.PollInterval"/> and poll again. Returns cleanly on
    /// cancellation. Intended to run on a background task for the app's lifetime.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
