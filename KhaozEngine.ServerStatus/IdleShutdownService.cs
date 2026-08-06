using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Watches a player-count source and asks the host to shut down once the server has been empty
/// continuously for <see cref="IdleAfter"/>. The game server drives <see cref="Tick"/> from its own loop,
/// or runs <see cref="RunAsync"/> on a background task.
///
/// <para>This service decides WHEN, never HOW. It raises a request and stops there, because ending the
/// process is the host's business: a headless server exits, a listen server tears down its session, and a
/// test host just records the call. That split is also what keeps this headless-testable with no process
/// control involved.</para>
///
/// <para><b>Why a host would want this.</b> A server head billed by the second (Azure Container Instances
/// is the case this was written for) charges the same for an empty world as a full one. A game with
/// scheduled or infrequent play sessions spends most of its month serving nobody. Shutting down when empty
/// turns that into near-zero, provided something can start the server again on demand: see the per-game
/// wake path, without which this only makes the server unreachable.</para>
///
/// <para><b>Exit code 0 is load-bearing on ACI.</b> Billing stops only when the whole container group
/// reaches a terminal state. Under the default <c>restartPolicy: Always</c> a group never terminates on its
/// own and bills forever. Under <c>OnFailure</c> a clean exit 0 reaches <c>Succeeded</c> and the meter
/// stops, while a genuine crash still restarts. So a host acting on this request must exit 0, and must not
/// route a crash through the same path.</para>
/// </summary>
public sealed class IdleShutdownService
{
    private readonly Func<int> playerCount;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ILogger log = Log.For<IdleShutdownService>();

    private DateTimeOffset? emptySinceUtc;
    private bool requested;

    /// <summary>
    /// Builds the service. <paramref name="playerCount"/> is read fresh on every tick, so the host can hand
    /// over a live accessor rather than a snapshot. <paramref name="delay"/> is an injectable seam so
    /// <see cref="RunAsync"/> is headless-testable, defaulting to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </summary>
    /// <param name="playerCount">Reads the number of connected players right now.</param>
    /// <param name="idleAfter">How long the server must stay empty before shutdown is requested.</param>
    /// <param name="pollInterval">How often <see cref="RunAsync"/> samples. Defaults to 60 seconds.</param>
    /// <param name="enabled">
    /// False disables the service outright: it never requests anything and <see cref="Tick"/> always returns
    /// false. This is the switch a local or developer run holds off with, so a dev server does not quietly
    /// exit while someone is reading logs.
    /// </param>
    /// <param name="delay">Injectable delay seam for tests.</param>
    public IdleShutdownService(
        Func<int> playerCount,
        TimeSpan idleAfter,
        TimeSpan? pollInterval = null,
        bool enabled = true,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.playerCount = playerCount ?? throw new ArgumentNullException(nameof(playerCount));
        if (idleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleAfter), idleAfter, "Idle window must be positive.");
        }

        TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(60);
        if (poll <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), poll, "Poll interval must be positive.");
        }

        IdleAfter = idleAfter;
        PollInterval = poll;
        Enabled = enabled;
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>Raised the first time the idle window elapses. Never raised more than once per empty streak.</summary>
    public event Action? IdleShutdownRequested;

    /// <summary>How long the server must stay empty before shutdown is requested.</summary>
    public TimeSpan IdleAfter { get; }

    /// <summary>How often <see cref="RunAsync"/> samples the player count.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>False disables the service outright. See the constructor.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// When the current empty streak began, or null while at least one player is connected (and before the
    /// first tick). Exposed so a host can report "empty for 12 min" without duplicating the bookkeeping.
    /// </summary>
    public DateTimeOffset? EmptySinceUtc => emptySinceUtc;

    /// <summary>True once the request has been raised, until a player reconnects and clears it.</summary>
    public bool HasRequestedShutdown => requested;

    /// <summary>
    /// How much of the idle window is left, or null when the server is not currently empty. Returns
    /// <see cref="TimeSpan.Zero"/> once the window has elapsed rather than going negative.
    /// </summary>
    public TimeSpan? RemainingBeforeShutdown(DateTimeOffset nowUtc)
    {
        if (!Enabled || emptySinceUtc is not { } since)
        {
            return null;
        }

        TimeSpan elapsed = nowUtc - since;
        return elapsed >= IdleAfter ? TimeSpan.Zero : IdleAfter - elapsed;
    }

    /// <summary>
    /// Samples the player count and advances the idle window.
    /// </summary>
    /// <returns>
    /// True on the single tick where the window elapses, so a caller driving this from its own loop can act
    /// on the return value and ignore the event. False on every other tick, including every tick after the
    /// request until a player reconnects.
    /// </returns>
    public bool Tick(DateTimeOffset nowUtc)
    {
        if (!Enabled)
        {
            return false;
        }

        int count = ReadCount();
        if (count > 0)
        {
            // Somebody is here. Clear the streak AND the latch: a host that ignored an earlier request gets a
            // fresh full window rather than a stale one, and a host that acted on it is already leaving, so
            // clearing costs nothing.
            if (requested)
            {
                log.Info($"Idle shutdown request cleared: {count} player(s) connected.");
            }

            emptySinceUtc = null;
            requested = false;
            return false;
        }

        emptySinceUtc ??= nowUtc;

        if (requested || nowUtc - emptySinceUtc.Value < IdleAfter)
        {
            return false;
        }

        requested = true;
        log.Info($"Server empty since {emptySinceUtc.Value:o}, past the {IdleAfter} idle window: requesting shutdown.");
        IdleShutdownRequested?.Invoke();
        return true;
    }

    /// <summary>
    /// Samples on <see cref="PollInterval"/> until cancelled. Returns once a shutdown has been requested, so
    /// a host can simply await this and then exit. Returns without requesting anything if cancelled first,
    /// or immediately when the service is disabled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (Tick(DateTimeOffset.UtcNow))
            {
                return;
            }

            try
            {
                await delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads the count, treating a throwing accessor as "not empty". A player-count source that fails is
    /// unknown, not zero, and shutting a live server down on a failed read is the one mistake this service
    /// must never make.
    /// </summary>
    private int ReadCount()
    {
        try
        {
            return playerCount();
        }
        catch (Exception ex)
        {
            log.Warn("Player-count read failed, treating the server as occupied this tick.", ex);
            return 1;
        }
    }
}
