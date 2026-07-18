using System;

namespace KhaozEngine.Gui;

/// <summary>
/// Tunables for <see cref="ConnectionStatusController"/>: how long an unplanned outage must persist before it
/// escalates from a banner to the full-screen takeover, and how long the takeover holds once shown so a
/// sub-second reflap back to <see cref="ConnectionPhase.Connected"/> cannot flash it away and back.
/// </summary>
public sealed class ConnectionStatusPolicyOptions
{
    /// <summary>
    /// Seconds an unplanned outage (<see cref="ConnectionStatusSignals.PlannedUpdate"/> false) must persist
    /// before <see cref="ConnectionStatusController.Update"/> escalates from <see cref="ConnectionUiMode.Banner"/>
    /// to <see cref="ConnectionUiMode.Screen"/>. A planned update escalates immediately regardless of this value.
    /// Negative values are treated as zero.
    /// </summary>
    public float EscalateAfterSeconds = 6f;

    /// <summary>
    /// Minimum seconds the full-screen takeover stays visible once shown, even if the connection recovers
    /// sooner - the anti-flicker floor. Negative values are treated as zero.
    /// </summary>
    public float MinScreenDurationSeconds = 1.5f;
}

/// <summary>
/// Headless policy brain deciding how much connection-outage UI to show: nothing, a banner (drawn by the
/// consumer), or the full-screen <see cref="ReconnectScreen"/> takeover. Netcode-free - feed it
/// <see cref="ConnectionStatusSignals"/> every frame via <see cref="Update"/> and render off the returned
/// <see cref="ConnectionStatusView"/>.
/// </summary>
/// <remarks>
/// Owns two clocks, handled deliberately differently. The drop timer is a bounded accumulator tracking how long
/// the current outage has run (it stops growing once it clears the escalation threshold, since nothing reads a
/// larger value) and resets to zero the instant <see cref="ConnectionStatusSignals.Phase"/> returns to
/// <see cref="ConnectionPhase.Connected"/>. The screen-active timer measures how long the takeover has been
/// shown, and gates release behind <see cref="ConnectionStatusPolicyOptions.MinScreenDurationSeconds"/> so a
/// sub-second reconnect blip cannot flicker the takeover away and back. While that floor holds, the returned
/// view's data fields come from the last cached outage signal rather than the (now connected, and therefore
/// blank) current signal, so the on-screen countdown/attempt does not blank out during the hold.
/// </remarks>
public sealed class ConnectionStatusController
{
    readonly ConnectionStatusPolicyOptions _options;

    float _dropElapsed;
    bool _screenActive;
    float _screenElapsed;
    ConnectionStatusKind _screenKind;
    ConnectionStatusSignals _lastOutage;

    /// <summary>Creates a controller. Uses <see cref="ConnectionStatusPolicyOptions"/> defaults when
    /// <paramref name="options"/> is null.</summary>
    public ConnectionStatusController(ConnectionStatusPolicyOptions? options = null)
    {
        _options = options ?? new ConnectionStatusPolicyOptions();
    }

    /// <summary>
    /// Advances the policy by one frame and decides how much connection-outage UI to show. See the type remarks
    /// for the escalation and anti-flicker rules.
    /// </summary>
    /// <param name="signals">This frame's connection signal.</param>
    /// <param name="dt">Elapsed seconds since the last call. Negative values are treated as zero.</param>
    public ConnectionStatusView Update(ConnectionStatusSignals signals, float dt)
    {
        dt = MathF.Max(dt, 0f);
        float escalateAfter = MathF.Max(_options.EscalateAfterSeconds, 0f);
        float minScreenDuration = MathF.Max(_options.MinScreenDurationSeconds, 0f);

        bool connected = signals.Phase == ConnectionPhase.Connected;

        if (connected)
        {
            _dropElapsed = 0f;
        }
        else
        {
            // Bounded: nothing ever reads a value above escalateAfter + 1, so the accumulator cannot grow
            // without limit across a long-running outage.
            _dropElapsed = MathF.Min(_dropElapsed + dt, escalateAfter + 1f);
            _lastOutage = signals; // keeps the display stable during an anti-flicker hold
        }

        bool wantsScreen = !connected && (signals.PlannedUpdate || _dropElapsed >= escalateAfter);

        if (wantsScreen && !_screenActive)
        {
            _screenActive = true;
            _screenElapsed = 0f;
        }

        if (_screenActive)
        {
            _screenElapsed += dt;
            if (!connected)
                _screenKind = signals.PlannedUpdate ? ConnectionStatusKind.PlannedUpdate : ConnectionStatusKind.Reconnecting;
            // Release only once the outage has genuinely stopped wanting the screen AND the anti-flicker floor
            // has been met - a sustained reconnect past the floor is what releases the hold early.
            if (!wantsScreen && _screenElapsed >= minScreenDuration)
                _screenActive = false;
        }

        if (_screenActive)
        {
            ConnectionStatusSignals data = connected ? _lastOutage : signals;
            return new ConnectionStatusView
            {
                Mode = ConnectionUiMode.Screen,
                Kind = _screenKind,
                EtaUtc = data.EtaUtc,
                Attempt = data.Attempt,
                SecondsUntilRetry = data.SecondsUntilRetry,
                MessageId = data.MessageId,
            };
        }

        if (connected)
        {
            _screenElapsed = 0f;
            return new ConnectionStatusView { Mode = ConnectionUiMode.None };
        }

        return new ConnectionStatusView
        {
            Mode = ConnectionUiMode.Banner,
            Kind = signals.PlannedUpdate ? ConnectionStatusKind.PlannedUpdate : ConnectionStatusKind.Reconnecting,
            EtaUtc = signals.EtaUtc,
            Attempt = signals.Attempt,
            SecondsUntilRetry = signals.SecondsUntilRetry,
            MessageId = signals.MessageId,
        };
    }
}
