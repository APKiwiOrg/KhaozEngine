using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Social;

/// <summary>
/// Game-facing presence orchestrator over any <see cref="ISocialProvider"/>. Handles lazy init, dedupe
/// (only re-send when content changed), throttled republish, an elapsed-timer helper, and the split
/// between a platform client that is not up YET and one that has died. A failed connect is retried from
/// <see cref="Update"/> on a bounded backoff (and on demand via <see cref="Retry"/>), so a game that
/// launched before Discord did picks presence up on its own. A failure of an already-connected provider
/// stays terminal for the session and disposes the provider, so a platform failure never reaches the
/// game loop. Provider-neutral, so it drives Discord today and any future backend unchanged.
/// </summary>
public sealed class SocialPresenceController : IDisposable
{
    private readonly ISocialProvider provider;
    private readonly SocialPresenceOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly ILogger log = Log.For<SocialPresenceController>();

    private readonly TimeSpan initialRetryDelay;
    private readonly TimeSpan maxRetryDelay;
    private readonly double retryBackoff;
    private readonly int maxConnectAttempts;

    private SocialPresenceState state = SocialPresenceState.Uninitialized;
    private int connectAttempts;
    private TimeSpan retryDelay;
    private DateTime nextAttemptUtc;

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

        initialRetryDelay = this.options.ConnectRetryDelay > TimeSpan.Zero ? this.options.ConnectRetryDelay : TimeSpan.Zero;
        maxRetryDelay = this.options.MaxConnectRetryDelay > initialRetryDelay ? this.options.MaxConnectRetryDelay : initialRetryDelay;
        retryBackoff = this.options.ConnectRetryBackoff > 1.0 ? this.options.ConnectRetryBackoff : 1.0;
        maxConnectAttempts = this.options.MaxConnectAttempts > 1 ? this.options.MaxConnectAttempts : 1;
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
    /// launched Discord", "the user pressed Reconnect"). A no-op once the session is connected, disabled
    /// by a mid-session failure, or disposed.
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
        RichPresence withTimer = presence with { StartTimestampUtc = now - Clamp(elapsed) };

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
            // Nothing has been published, so the clear just cancels whatever was waiting on the connect.
            DropHeldPresence();
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

    /// <summary>Pump the provider, and drive the connect-retry schedule. Call once per frame.</summary>
    public void Update()
    {
        if (state == SocialPresenceState.Connecting && UtcNow >= nextAttemptUtc)
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
            SetState(SocialPresenceState.Connected);
            PublishHeldPresence();
            return;
        }

        if (connectAttempts >= maxConnectAttempts)
        {
            log.Debug($"social: no platform client after {connectAttempts} attempt(s); giving up until Retry().");
            SetState(SocialPresenceState.GivenUp);
            return;
        }

        nextAttemptUtc = UtcNow + retryDelay;
        retryDelay = GrowDelay(retryDelay);
        SetState(SocialPresenceState.Connecting);
    }

    private TimeSpan GrowDelay(TimeSpan current)
    {
        double grown = current.Ticks * retryBackoff;
        return grown >= maxRetryDelay.Ticks ? maxRetryDelay : TimeSpan.FromTicks((long)grown);
    }

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

        HoldForConnect(presence with { StartTimestampUtc = UtcNow - Clamp(elapsed) });
    }

    private static TimeSpan Clamp(TimeSpan elapsed) => elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;

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

    // Terminal for the session: a provider that failed while connected has a dead transport, which is a
    // different case from a platform client that has not started yet, and is not retried.
    private void DisableSession()
    {
        DropHeldPresence();
        SetState(SocialPresenceState.Disabled);
        SafeDisposeProvider();
    }

    private void SetState(SocialPresenceState next)
    {
        if (state == next)
        {
            return;
        }

        state = next;
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
