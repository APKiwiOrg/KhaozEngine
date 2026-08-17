using System;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// The raw byte transport under <see cref="DiscordIpcClient"/>: connect to the Discord socket, and
/// non-blocking read/write. Abstracted so the client's handshake + framing logic is unit-testable with
/// an in-memory fake and the real named-pipe / unix-socket IO lives in one small class.
/// </summary>
internal interface IDiscordIpcTransport : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Attempt to connect to a running Discord client. Returns false if none is reachable.</summary>
    bool TryConnect();

    /// <summary>
    /// Drop the live connection and stay REUSABLE: a later <see cref="TryConnect"/> starts clean.
    /// <see cref="IDisposable.Dispose"/> is the terminal one. Called whenever the client notices its session
    /// is over, so the socket and its reader thread do not sit there for the whole reconnect backoff.
    /// Idempotent, and a no-op on a transport that never connected.
    /// </summary>
    void Disconnect();

    /// <summary>Write all bytes. May throw on a broken pipe; the caller treats a throw as disconnect.</summary>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Read available bytes into <paramref name="buffer"/>; returns 0 when nothing is available or the pipe closed.</summary>
    int Read(Span<byte> buffer);
}
