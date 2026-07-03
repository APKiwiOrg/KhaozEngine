using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Social;

/// <summary>Tuning for <see cref="SocialPresenceController"/>.</summary>
public sealed class SocialPresenceOptions
{
    /// <summary>Minimum wall-clock time before an unchanged presence is re-published. Default 15s.</summary>
    public TimeSpan RepublishInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Game-facing presence orchestrator over any <see cref="ISocialProvider"/>. Handles lazy init,
/// dedupe (only re-send when content changed), throttled republish, an elapsed-timer helper, and
/// session self-disable: any throw from the provider permanently disables social for the session and
/// disposes the provider, so a platform failure never reaches the game loop. Provider-neutral, so it
/// drives Discord today and any future backend unchanged.
/// </summary>
public sealed class SocialPresenceController : IDisposable
{
    private readonly ISocialProvider provider;
    private readonly SocialPresenceOptions options;
    private readonly ILogger log = Log.For<SocialPresenceController>();

    private bool initializeAttempted;
    private bool enabled;
    private bool disabled;
    private bool disposed;

    private RichPresence lastPresence;
    private bool hasLastPresence;
    private DateTime lastPublishUtc = DateTime.MinValue;

    public SocialPresenceController(ISocialProvider? provider = null, SocialPresenceOptions? options = null)
    {
        this.provider = provider ?? new NullSocialProvider();
        this.options = options ?? new SocialPresenceOptions();

        this.provider.JoinRequested += OnJoinRequested;
        this.provider.JoinRequestReceived += OnJoinRequestReceived;
    }

    /// <summary>The platform application/client id to initialize with. Set before <see cref="Initialize"/>.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>True when a provider is connected and presence will be published.</summary>
    public bool IsEnabled => enabled && !disabled && !disposed;

    /// <summary>Raised when a friend activates "Join Game"; carries the game-encoded join secret.</summary>
    public event Action<string>? JoinRequested;

    /// <summary>Raised when another user asks to join.</summary>
    public event Action<JoinRequest>? JoinRequestReceived;

    /// <summary>Connect the provider. Safe to call repeatedly; only the first attempt connects.</summary>
    public void Initialize()
    {
        if (initializeAttempted || disabled || disposed)
        {
            return;
        }

        initializeAttempted = true;
        try
        {
            enabled = provider.TryInitialize(ApplicationId);
            if (!enabled)
            {
                Disable();
            }
        }
        catch (Exception ex)
        {
            log.Debug($"social: initialize failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>Publish presence, deduped by content and throttled by <see cref="SocialPresenceOptions.RepublishInterval"/>.</summary>
    public void SetPresence(in RichPresence presence, bool force = false)
    {
        if (!EnsureReady())
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
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
            Disable();
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
            return;
        }

        TimeSpan clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        RichPresence withTimer = presence with { StartTimestampUtc = DateTime.UtcNow - clamped };

        // Dedupe on everything except the timestamp so we do not spam a per-frame elapsed update,
        // but still republish on the interval so the timer stays live after a reconnect.
        bool contentChanged = !hasLastPresence
            || !(lastPresence with { StartTimestampUtc = null }).Equals(presence with { StartTimestampUtc = null });
        bool stale = DateTime.UtcNow - lastPublishUtc >= options.RepublishInterval;
        if (!force && !contentChanged && !stale)
        {
            return;
        }

        try
        {
            provider.SetPresence(withTimer);
            lastPresence = withTimer;
            hasLastPresence = true;
            lastPublishUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            log.Debug($"social: set-elapsed-presence failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>Clear the published presence.</summary>
    public void ClearPresence()
    {
        if (!EnsureReady())
        {
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
            Disable();
        }
    }

    /// <summary>Pump the provider. Call once per frame.</summary>
    public void Update()
    {
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
            Disable();
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
            Disable();
            user = default;
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        provider.JoinRequested -= OnJoinRequested;
        provider.JoinRequestReceived -= OnJoinRequestReceived;
        SafeDisposeProvider();
    }

    private bool EnsureReady()
    {
        if (disabled || disposed)
        {
            return false;
        }

        if (!initializeAttempted)
        {
            Initialize();
        }

        return enabled && !disabled;
    }

    private void Disable()
    {
        disabled = true;
        enabled = false;
        SafeDisposeProvider();
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
    // Disable()d): a bad subscriber callback is not a provider/transport failure, so it must not kill
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
