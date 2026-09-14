using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// The ALC device calls <see cref="AudioDeviceFollower"/> makes its decisions from, kept behind a seam so the poll
/// and reopen rules are tested without an audio device. <see cref="OpenAlContext"/> is the one real implementation.
/// </summary>
internal interface IAlcOutputDevice
{
    /// <summary>
    /// True when the device can be moved to another output in place (<c>ALC_SOFT_reopen_device</c>). Without it
    /// there is nothing to recover with, so the follower never polls.
    /// </summary>
    bool CanReopen { get; }

    /// <summary>
    /// <c>ALC_CONNECTED</c>. Reads true when the device has no <c>ALC_EXT_disconnect</c>, since a loss cannot be
    /// seen then.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>The name of the output the device is playing on, for the log line after a reopen.</summary>
    string? CurrentDeviceName { get; }

    /// <summary>
    /// The system default output as a fresh enumeration sees it, or null when the backend cannot enumerate or
    /// lists no output at all.
    /// </summary>
    string? ProbeDefaultDeviceName();

    /// <summary>
    /// Reopens the device on the output it follows, keeping its context, sources and buffers. Returns false when the
    /// output would not open, with the ALC error that left behind in <paramref name="error"/>.
    /// </summary>
    bool TryReopen(out ContextError error);
}
