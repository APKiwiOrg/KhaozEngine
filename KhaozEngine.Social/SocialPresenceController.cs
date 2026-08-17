using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Social;

/// <summary>
/// Game-facing presence orchestrator over any <see cref="ISocialProvider"/>. Handles lazy init, dedupe
/// (only re-send when content changed), throttled republish, an elapsed-timer helper, and the split
/// between a platform client that is not up YET, one that has gone away, and one that has failed. A failed
/// connect is retried from <see cref="Update"/> on a bounded backoff (and on demand via <see cref="Retry"/>),
/// so a game that launched before Discord did picks presence up on its own. A CONNECTION that later drops
/// (the player quit Discord mid-session) re-enters that same backoff and republishes the presence that was
/// live at the drop. A provider that THROWS is the one terminal case: it ends the session and disposes the
/// provider, so a platform failure never reaches the game loop. Provider-neutral, so it drives Discord today
/// and any future backend unchanged.
/// </summary>
/// <remarks>
/// Presence held during the wait is published BEFORE <see cref="StateChanged"/> announces
/// <see cref="SocialPresenceState.Connected"/>, so a handler that publishes its own line on that event wins
/// and stays published.
/// <para>
/// A drop only refills the connect budget when the session HELD, meaning it lasted at least
/// <see cref="SocialPresenceOptions.StableConnectionSpan"/>. A connect that dies sooner than that carries its
/// spent attempts forward, so a platform client that accepts every connect and loses it again immediately
/// reaches <see cref="SocialPresenceState.GivenUp"/> rather than cycling for the rest of the process. With
/// <see cref="SocialPresenceOptions.MaxConnectAttempts"/> of 1, a held session's drop therefore gets ONE
/// reconnect attempt, one <see cref="SocialPresenceOptions.ConnectRetryDelay"/> later rather than
/// immediately as the cold start at <see cref="Initialize"/> does, and then gives up.
/// </para>
/// <para>
/// A handler wired straight to <see cref="Retry"/> on <see cref="SocialPresenceState.GivenUp"/> should know
/// that the forced attempt runs inside the event, while the state is still <c>GivenUp</c>. With no budget
/// left to re-arm (<see cref="SocialPresenceOptions.MaxConnectAttempts"/> of 1) that is one extra attempt and
/// NO second <c>GivenUp</c> event, because the repeat transition is deduped by the equality guard. With a
/// larger budget the forced attempt re-arms the whole schedule and the controller lands back in
/// <see cref="SocialPresenceState.Connecting"/>, so the handler is a reconnect loop rather than one extra try.
/// </para>
/// </remarks>
public sealed class SocialPresenceController : IDisposable
{
    // Ceiling both configured retry waits are clamped to. Nothing above it can serve the case the retry
    // exists for (a platform client that starts within the first few minutes), and an unbounded wait
    // overflows the date arithmetic that schedules the next attempt. TimeSpan.MaxValue is the natural way
    // to spell "no cap" on MaxConnectRetryDelay, so it has to land here rather than throw.
    private static readonly TimeSpan RetryDelayCeiling = TimeSpan.FromDays(1);

    private readonly ISocialProvider provider;
    private readonly SocialPresenceOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly ILogger log = Log.For<SocialPresenceController>();

    private readonly TimeSpan initialRetryDelay;
    private readonly TimeSpan maxRetryDelay;
    private readonly double retryBackoff;
    private readonly int maxConnectAttempts;
    private readonly TimeSpan stableConnectionSpan;

    private SocialPresenceState state = SocialPresenceState.Uninitialized;
    private bool hasConnected;
    private int connectAttempts;
    private TimeSpan retryDelay;
    private DateTime nextAttemptUtc;
    private DateTime connectedAtUtc = DateTime.MinValue;

    private RichPresence lastPresence;
    private bool hasLastPresence;
    private DateTime lastPublishUtc = DateTime.MinValue;

    private RichPresence pendingPresence;
    private bool hasPendingPresence;

    /// <param name="provider">The backend. Null means no social platform: a silent <see cref="NullSocialProvider"/>.</param>
    /// <param name="options">Dedupe/republish and connect-retry tuning.</param>
    /// <param name="clock">Wall clock, injectable so the retry schedule is testable. Defaults to UTC now.</param>
    public SocialPresenceController(
        ISocialProvider? provider = null,
        SocialPresenceOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.provider = provider ?? new NullSocialProvider();
        this.options = options ?? new SocialPresenceOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

        initialRetryDelay = ClampDelay(this.options.ConnectRetryDelay);
        TimeSpan cappedMax = ClampDelay(this.options.MaxConnectRetryDelay);
        maxRetryDelay = cappedMax > initialRetryDelay ? cappedMax : initialRetryDelay;
        retryBackoff = this.options.ConnectRetryBackoff > 1.0 ? this.options.ConnectRetryBackoff : 1.0;
        maxConnectAttempts = this.options.MaxConnectAttempts > 1 ? this.options.MaxConnectAttempts : 1;
        stableConnectionSpan = ClampDelay(this.options.StableConnectionSpan);
        retryDelay = initialRetryDelay;

        this.provider.JoinRequested += OnJoinRequested;
        this.provider.JoinRequestReceived += OnJoinRequestReceived;
    }

    /// <summary>The platform application/client id to initialize with. Set before <see cref="Initialize"/>.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Where the controller is in its connection lifecycle. Safe to poll for a status line.</summary>
    public SocialPresenceState State => state;

    /// <summary>True when a provider is connected and presence will be published.</summary>
    public bool IsEnabled => state == SocialPresenceState.Connected;

    /// <summary>Raised whenever <see cref="State"/> changes. A throwing handler is logged and swallowed.</summary>
    public event Action<SocialPresenceState>? StateChanged;

    /// <summary>Raised when a friend activates "Join Game"; carries the game-encoded join secret.</summary>
    public event Action<string>? JoinRequested;

    /// <summary>Raised when another user asks to join.</summary>
    public event Action<JoinRequest>? JoinRequestReceived;

    /// <summary>
    /// Make the first connect attempt. Safe to call repeatedly: only the first call attempts anything,
    /// and a failure schedules a retry rather than disabling the session. See <see cref="Retry"/> to
    /// force an attempt.
    /// </summary>
    public void Initialize()
    {
        if (state != SocialPresenceState.Uninitialized)
        {
            return;
        }

        // No backend at all is a configuration choice, not a cold-start race: a NullSocialProvider never
        // connects by design, so retrying it would tick a timer every frame of every headless run for a
        // result that cannot change. Go terminal on the spot and never arm the backoff.
        if (provider is NullSocialProvider)
        {
            SetState(SocialPresenceState.Disabled);
            return;
        }

        AttemptConnect();
    }

    /// <summary>
    /// Force a connect attempt now, ignoring any wait still owed, and re-arm the schedule if the
    /// controller had given up. For a game that knows something the controller cannot ("the player just
    /// launched Discord", "the user pressed Reconnect"). Works the same whether the controller has never
    /// connected or is working its way back from a drop. A no-op once the session is connected, disabled
    /// by a provider that threw, or disposed.
    /// </summary>
    public void Retry()
    {
        if (state is SocialPresenceState.Disposed or SocialPresenceState.Disabled or SocialPresenceState.Connected)
        {
            return;
        }

        if (state == SocialPresenceState.Uninitialized)
        {
            Initialize();
            return;
        }

        if (state == SocialPresenceState.GivenUp)
        {
            connectAttempts = 0;
            retryDelay = initialRetryDelay;
        }

        AttemptConnect();
    }

    /// <summary>
    /// Publish presence, deduped by content and throttled by <see cref="SocialPresenceOptions.RepublishInterval"/>.
    /// While the controller is still trying to connect, the presence is held (latest wins) and published
    /// as soon as the connect lands.
    /// </summary>
    public void SetPresence(in RichPresence presence, bool force = false)
    {
        if (!EnsureReady())
        {
            HoldForConnect(presence);
            return;
        }

        DateTime now = UtcNow;
        bool changed = !hasLastPresence || !presence.Equals(lastPresence);
        bool stale = now - lastPublishUtc >= options.RepublishInterval;
        if (!force && !changed && !stale)
        {
            return;
        }

        try
        {
            provider.SetPresence(presence);
            lastPresence = presence;
            hasLastPresence = true;
            lastPublishUtc = now;
        }
        catch (Exception ex)
        {
            log.Debug($"social: set-presence failed ({ex.GetType().Name}); disabling.");
            DisableSession();
        }
    }

    /// <summary>
    /// Publish presence whose <see cref="RichPresence.StartTimestampUtc"/> is set to
    /// <c>UtcNow - elapsed</c>, so the platform renders a live "elapsed" timer. Dedupe ignores the
    /// derived timestamp (it changes every call), keying on the rest of the presence instead.
    /// </summary>
    public void SetElapsedPresence(in RichPresence presence, TimeSpan elapsed, bool force = false)
    {
        if (!EnsureReady())
        {
            HoldElapsedForConnect(presence, elapsed);
            return;
        }

        DateTime now = UtcNow;
        RichPresence withTimer = presence with { StartTimestampUtc = StartInstant(now, elapsed) };

        // Dedupe on everything except the timestamp so we do not spam a per-frame elapsed update,
        // but still republish on the interval so the timer stays live after a reconnect.
        bool contentChanged = !hasLastPresence
            || !(lastPresence with { StartTimestampUtc = null }).Equals(presence with { StartTimestampUtc = null });
        bool stale = now - lastPublishUtc >= options.RepublishInterval;
        if (!force && !contentChanged && !stale)
        {
            return;
        }

        try
        {
            provider.SetPresence(withTimer);
            lastPresence = withTimer;
            hasLastPresence = true;
            lastPublishUtc = now;
        }
        catch (Exception ex)
        {
            log.Debug($"social: set-elapsed-presence failed ({ex.GetType().Name}); disabling.");
            DisableSession();
        }
    }

    /// <summary>Clear the published presence, and drop anything held for a pending connect.</summary>
    public void ClearPresence()
    {
        if (!EnsureReady())
        {
            // Nothing can be published right now, so the clear cancels whatever was waiting on the connect.
            // It also has to forget what the controller believes is live: a clear during an outage means the
            // game wants nothing on screen, and leaving the dedupe cache holding the pre-drop line would
            // dedupe the game's own attempt to put that same line back after the reconnect.
            DropHeldPresence();
            hasLastPresence = false;
            lastPresence = default;
            return;
        }

        try
        {
            provider.ClearPresence();
            hasLastPresence = false;
            lastPresence = default;
        }
        catch (Exception ex)
        {
            log.Debug($"social: clear-presence failed ({ex.GetType().Name}); disabling.");
            DisableSession();
        }
    }

    /// <summary>
    /// Pump the provider, drive the connect-retry schedule, and notice a connection that has dropped.
    /// Call once per frame.
    /// </summary>
    public void Update()
    {
        if (IsAwaitingConnect && UtcNow >= nextAttemptUtc)
        {
            AttemptConnect();
        }

        if (!EnsureReady())
        {
            return;
        }

        try
        {
            provider.Update();
        }
        catch (Exception ex)
        {
            log.Debug($"social: update failed ({ex.GetType().Name}); disabling.");
            DisableSession();
            return;
        }

        // The pump above runs game callbacks (a join event), and a game handler is allowed to dispose the
        // controller from inside one, so the session may be gone by the time it returns.
        if (state == SocialPresenceState.Connected)
        {
            ProbeConnection();
        }
    }

    /// <summary>The local platform identity (e.g. Discord username), once connected.</summary>
    public bool TryGetLocalUser(out SocialUser user)
    {
        user = default;
        if (!EnsureReady())
        {
            return false;
        }

        try
        {
            return provider.TryGetLocalUser(out user);
        }
        catch (Exception ex)
        {
            log.Debug($"social: get-local-user failed ({ex.GetType().Name}); disabling.");
            DisableSession();
            user = default;
            return false;
        }
    }

    public void Dispose()
    {
        if (state == SocialPresenceState.Disposed)
        {
            return;
        }

        DropHeldPresence();
        SetState(SocialPresenceState.Disposed);
        provider.JoinRequested -= OnJoinRequested;
        provider.JoinRequestReceived -= OnJoinRequestReceived;
        SafeDisposeProvider();
    }

    private DateTime UtcNow => clock().UtcDateTime;

    // Connecting (never reached the platform yet) and Reconnecting (had it, lost it) run one schedule, so
    // everything that drives the backoff asks this rather than naming one of them and quietly excluding
    // the other.
    private bool IsAwaitingConnect
        => state is SocialPresenceState.Connecting or SocialPresenceState.Reconnecting;

    // Which of the two a failed attempt lands in. A session that has connected ONCE is reconnecting for the
    // rest of its life, however many times it drops and however many attempts a round takes: what separates
    // the two is whether presence was ever live, not where the schedule is.
    private SocialPresenceState WaitingState
        => hasConnected ? SocialPresenceState.Reconnecting : SocialPresenceState.Connecting;

    private bool EnsureReady()
    {
        if (state == SocialPresenceState.Uninitialized)
        {
            Initialize();
        }

        return state == SocialPresenceState.Connected;
    }

    // One connect attempt, wherever it came from: the first Initialize(), a due backoff tick in Update(),
    // or an explicit Retry(). A provider that THROWS here (a backend whose native/transport layer is
    // missing does) is a failed attempt like any other, never an exception into the game loop.
    private void AttemptConnect()
    {
        connectAttempts++;
        bool connected;
        try
        {
            connected = provider.TryInitialize(ApplicationId);
        }
        catch (Exception ex)
        {
            log.Debug($"social: connect attempt {connectAttempts} threw ({ex.GetType().Name}).");
            connected = false;
        }

        if (connected)
        {
            // Assign, publish, THEN notify - in that order, and not for tidiness. A game handler that
            // publishes its own presence on Connected (the pattern the docs teach) has to land AFTER the
            // line held during the wait, or the stale hold overwrites it and, because the hold also primes
            // the dedupe cache, nothing republishes for the rest of the session.
            hasConnected = true;
            connectedAtUtc = UtcNow;
            bool transitioned = AssignState(SocialPresenceState.Connected);
            PublishHeldPresence();

            // A held publish that fails takes the session to Disabled and announces it. There is no
            // Connected left to announce at that point.
            if (transitioned && state == SocialPresenceState.Connected)
            {
                NotifyStateChanged(SocialPresenceState.Connected);
            }

            return;
        }

        if (connectAttempts >= maxConnectAttempts)
        {
            log.Debug($"social: no platform client after {connectAttempts} attempt(s); giving up until Retry().");
            SetState(SocialPresenceState.GivenUp);
            return;
        }

        nextAttemptUtc = Schedule(UtcNow, retryDelay);
        retryDelay = GrowDelay(retryDelay);
        SetState(WaitingState);
    }

    // The drop check, and the whole per-frame cost of it: one bool read on the seam. The clock is read only
    // once the answer is NO, which is what keeps a live session's Update() off the clock entirely. It runs
    // AFTER the pump because pumping is where a backend notices its own transport died, so a drop is seen on
    // the frame it happened rather than the one after.
    private void ProbeConnection()
    {
        bool live;
        try
        {
            live = provider.IsConnected;
        }
        catch (Exception ex)
        {
            log.Debug($"social: connection probe failed ({ex.GetType().Name}); disabling.");
            DisableSession();
            return;
        }

        if (!live)
        {
            BeginReconnect();
        }
    }

    // A connection that DROPPED is recoverable, unlike one that threw: the provider reported it itself by
    // going !IsConnected, which the seam defines as leaving it ready for another TryInitialize. So the
    // session re-enters the same backoff a cold start uses, and the provider is deliberately NOT disposed -
    // disposing it is exactly what made the terminal path impossible to come back from. What the budget does
    // depends on whether the session HELD: see the flap guard below.
    private void BeginReconnect()
    {
        log.Debug("social: the provider reports a dropped connection; reconnecting.");

        // Come back showing what was on screen when the platform went away, unless the game moved on during
        // the outage: a SetPresence while disconnected holds the newer line, and latest wins as it always
        // does. A ClearPresence during the outage drops it, so nothing is republished at all.
        if (!hasPendingPresence && hasLastPresence)
        {
            pendingPresence = lastPresence;
            hasPendingPresence = true;
        }

        // The dedupe cache describes a platform client that is GONE, so it is dropped here - after the hold
        // above has taken its copy of it, which is the whole reason these two are in this order. Whatever the
        // reconnect publishes primes it again. Left standing, it swallows the game's first SetPresence after
        // the outage whenever that line matches the pre-drop one, which is exactly what a ClearPresence
        // during the outage produces: the platform is correctly blank, the controller believes the old line
        // is live, and a change-driven publisher stays blank until it happens to set a different line.
        hasLastPresence = false;
        lastPresence = default;
        lastPublishUtc = DateTime.MinValue;

        DateTime now = UtcNow;
        if (now - connectedAtUtc >= stableConnectionSpan)
        {
            // A session that ran for a while and then ended is a real outage: full budget, initial wait.
            connectAttempts = 0;
            retryDelay = initialRetryDelay;
        }
        else if (connectAttempts >= maxConnectAttempts)
        {
            // A connect that dies inside StableConnectionSpan is a FLAP, and a flap resetting the budget is
            // a loop with no exit: only a failed attempt reaches the budget check in AttemptConnect, and a
            // flapping platform client never fails one. Discord mid-restart is the real case, since its
            // handshake succeeds the moment the bytes are written and the socket dies right after. So the
            // spent attempts carry forward, and running out of them here is what ends the cycle.
            log.Debug($"social: the connection dropped {connectAttempts} time(s) without holding; giving up until Retry().");
            SetState(SocialPresenceState.GivenUp);
            return;
        }

        // The first attempt waits out the delay rather than going on the spot. The platform client is
        // mid-shutdown at the instant its socket dies, so an immediate attempt spends a budget on a failure
        // that is as good as certain.
        nextAttemptUtc = Schedule(now, retryDelay);
        retryDelay = GrowDelay(retryDelay);
        SetState(SocialPresenceState.Reconnecting);
    }

    private TimeSpan GrowDelay(TimeSpan current)
    {
        double grown = current.Ticks * retryBackoff;
        return grown >= maxRetryDelay.Ticks ? maxRetryDelay : TimeSpan.FromTicks((long)grown);
    }

    // The options contract is that a nonsensical value degrades rather than throws, so a wait is pulled into
    // [0, RetryDelayCeiling] instead of being handed to date arithmetic that would overflow on it.
    private static TimeSpan ClampDelay(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > RetryDelayCeiling ? RetryDelayCeiling : value;
    }

    // Saturate rather than throw. The clamp above already keeps this off the edge, but scheduling runs on the
    // Update() path, and an ArgumentOutOfRangeException reaching the game loop out of a presence controller is
    // never the right answer to a silly option value.
    private static DateTime Schedule(DateTime now, TimeSpan delay)
        => DateTime.MaxValue - now < delay ? DateTime.MaxValue : now + delay;

    // Keep the LATEST desired presence, never a queue: what the game wants shown is whatever it asked for
    // last, and replaying an older one on connect would publish a line the game already moved past.
    private void HoldForConnect(in RichPresence presence)
    {
        if (state is SocialPresenceState.Disabled or SocialPresenceState.Disposed)
        {
            return;
        }

        pendingPresence = presence;
        hasPendingPresence = true;
    }

    // The held copy keeps the ABSOLUTE start instant rather than the elapsed span, so the timer still
    // reads correctly however long the connect takes to land.
    private void HoldElapsedForConnect(in RichPresence presence, TimeSpan elapsed)
    {
        if (state is SocialPresenceState.Disabled or SocialPresenceState.Disposed)
        {
            return;
        }

        HoldForConnect(presence with { StartTimestampUtc = StartInstant(UtcNow, elapsed) });
    }

    // The start instant an elapsed span implies, without date arithmetic that can throw out of a public presence
    // call (#657): a negative span starts now, and a span longer than the calendar can hold saturates at the
    // earliest representable instant instead of underflowing DateTime.
    private static DateTime StartInstant(DateTime now, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return now;
        }

        return now - DateTime.MinValue < elapsed ? DateTime.MinValue : now - elapsed;
    }

    private void DropHeldPresence()
    {
        hasPendingPresence = false;
        pendingPresence = default;
    }

    private void PublishHeldPresence()
    {
        if (!hasPendingPresence)
        {
            return;
        }

        RichPresence presence = pendingPresence;
        DropHeldPresence();

        try
        {
            provider.SetPresence(presence);
            lastPresence = presence;
            hasLastPresence = true;
            lastPublishUtc = UtcNow;
        }
        catch (Exception ex)
        {
            log.Debug($"social: held-presence publish failed ({ex.GetType().Name}); disabling.");
            DisableSession();
        }
    }

    // Terminal for the session, and the only path that is. A provider that THREW is in a state the seam
    // cannot promise anything about, which is a different case both from a platform client that has not
    // started yet and from one that went away cleanly, and is not retried.
    private void DisableSession()
    {
        DropHeldPresence();
        SetState(SocialPresenceState.Disabled);
        SafeDisposeProvider();
    }

    private void SetState(SocialPresenceState next)
    {
        if (AssignState(next))
        {
            NotifyStateChanged(next);
        }
    }

    // Assign only, so a caller that has work to do BEFORE the game hears about the transition can order the
    // two itself (the connect does: it publishes the held presence in between). Returns true when this was a
    // real transition, which is also the equality guard that stops a repeat state re-announcing itself.
    private bool AssignState(SocialPresenceState next)
    {
        if (state == next)
        {
            return false;
        }

        state = next;
        return true;
    }

    private void NotifyStateChanged(SocialPresenceState next)
    {
        try
        {
            StateChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            log.Debug($"social: state-changed handler threw ({ex.GetType().Name}); ignored.");
        }
    }

    private void SafeDisposeProvider()
    {
        try
        {
            provider.Dispose();
        }
        catch
        {
            // Suppress all shutdown transport failures.
        }
    }

    // Forward provider events to the game. A throwing game handler is logged and swallowed here (not
    // disabled): a bad subscriber callback is not a provider/transport failure, so it must not kill
    // the social session, and it must never escape the controller into the caller.
    private void OnJoinRequested(string secret)
    {
        try
        {
            JoinRequested?.Invoke(secret);
        }
        catch (Exception ex)
        {
            log.Debug($"social: join-requested handler threw ({ex.GetType().Name}); ignored.");
        }
    }

    private void OnJoinRequestReceived(JoinRequest request)
    {
        try
        {
            JoinRequestReceived?.Invoke(request);
        }
        catch (Exception ex)
        {
            log.Debug($"social: join-request-received handler threw ({ex.GetType().Name}); ignored.");
        }
    }
}
