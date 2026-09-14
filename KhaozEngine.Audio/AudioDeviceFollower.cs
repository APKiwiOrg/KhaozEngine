using System;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// Decides when the OpenAL device has to be reopened. Two things leave audio on the wrong output or on none: the
/// system default output changed while the device follows the default, or the device reports it is no longer
/// connected. Both go through the one <see cref="IAlcOutputDevice.TryReopen"/>.
/// <para>Polled from the thread <see cref="AudioSystem"/> is pinned to, at most once per <see cref="PollInterval"/>.
/// The bundled OpenAL Soft 1.23.1 has no device-change event (<c>ALC_SOFT_system_events</c> arrived in 1.24), so a
/// poll is the only detection there is. The two triggers are not redundant: the 1.23.1 CoreAudio backend never
/// reports a disconnect, so on macOS the default change is what catches an unplug, while WASAPI and PulseAudio do
/// report one.</para>
/// <para>A default change is measured against the default seen at the last successful open, not against the open
/// device's own name. The two name the same output on every backend checked, but a backend whose names disagreed
/// would make a name comparison reopen every second forever, and comparing the default with itself cannot.</para>
/// </summary>
internal sealed class AudioDeviceFollower
{
    /// <summary>How often the device is checked. A default-device probe costs about 0.4 ms on macOS.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between attempts to follow a default output that would not open.</summary>
    public static readonly TimeSpan MaxFollowBackoff = TimeSpan.FromSeconds(30);

    readonly IAlcOutputDevice _device;
    readonly bool _followsDefault;
    readonly AlErrorLog _errors;
    readonly ILogger _logger;
    readonly bool _enabled;
    string? _knownDefault;
    TimeSpan _nextPoll;
    TimeSpan _nextFollowAttempt;
    TimeSpan _followBackoff = PollInterval;

    /// <param name="device">The device to watch and reopen.</param>
    /// <param name="followsDefault">
    /// True when the device was opened as the default output. A device opened by name is never moved to the default,
    /// only reopened when it is lost.
    /// </param>
    /// <param name="defaultAtOpen">
    /// The default output as probed just before the device opened. Probing before rather than after means a change
    /// racing the open costs one extra reopen instead of going unnoticed.
    /// </param>
    /// <param name="errors">Where a failed reopen is reported, once.</param>
    /// <param name="logger">Where a successful reopen is reported.</param>
    /// <param name="now">The poll clock at construction. The first check is one interval later.</param>
    public AudioDeviceFollower(
        IAlcOutputDevice device,
        bool followsDefault,
        string? defaultAtOpen,
        AlErrorLog errors,
        ILogger logger,
        TimeSpan now)
    {
        _device = device;
        _followsDefault = followsDefault;
        _knownDefault = defaultAtOpen;
        _errors = errors;
        _logger = logger;
        _nextPoll = now + PollInterval;
        _enabled = device.CanReopen;
        if (!_enabled)
            _logger.Info("OpenAL: the device cannot be reopened (no ALC_SOFT_reopen_device), so audio stays on the output it opened on.");
    }

    /// <summary>Successful reopens since construction.</summary>
    public int ReopenCount { get; private set; }

    /// <summary>Reopen attempts that failed since construction.</summary>
    public int FailedReopenCount { get; private set; }

    /// <summary>
    /// Checks the device when an interval has passed since the last check, and reopens it when it has to move.
    /// <paramref name="now"/> is any monotonic clock, the same one construction used.
    /// </summary>
    public void Poll(TimeSpan now)
    {
        if (!_enabled || now < _nextPoll) return;
        _nextPoll = now + PollInterval;

        bool connected = _device.IsConnected;
        string? currentDefault = _followsDefault ? _device.ProbeDefaultDeviceName() : null;
        // A null probe says nothing about where the default went, so it never counts as a change.
        bool defaultMoved = currentDefault is not null && currentDefault != _knownDefault;

        if (connected && !defaultMoved)
        {
            _followBackoff = PollInterval;
            _nextFollowAttempt = now;
            return;
        }

        // A failed follow is not free while the old output still plays: OpenAL Soft stops that output to try the
        // new one and restarts it on failure, which is an audible gap. So a follow that keeps failing backs off. A
        // lost device has nothing left to interrupt and retries on every poll.
        if (connected && now < _nextFollowAttempt) return;

        if (_device.TryReopen(out ContextError error))
        {
            ReopenCount++;
            if (currentDefault is not null) _knownDefault = currentDefault;
            _followBackoff = PollInterval;
            _nextFollowAttempt = now;
            string reason = connected ? "the default output changed" : "the output device was lost";
            _logger.Info($"OpenAL: {reason}, audio reopened on '{_device.CurrentDeviceName}'.");
            return;
        }

        FailedReopenCount++;
        _errors.Check("device reopen", error);
        if (connected)
        {
            _followBackoff = _followBackoff * 2 < MaxFollowBackoff ? _followBackoff * 2 : MaxFollowBackoff;
            _nextFollowAttempt = now + _followBackoff;
        }
    }
}
