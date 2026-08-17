using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The real transport's lifecycle, on the paths that need no Discord socket. Connecting itself is covered
/// by <see cref="DiscordLiveSocketTests"/>, which is excluded from CI for exactly that reason.
/// </summary>
public class NamedPipeDiscordTransportTests
{
    [Fact]
    public void Disconnect_OnATransportThatNeverConnected_IsANoOp_AndRepeats()
    {
        // The client calls Disconnect() on every failure path, including ones that never got a socket, and
        // the reconnect loop (#655) now walks those paths several times per session rather than once.
        using var transport = new NamedPipeDiscordTransport();

        Assert.False(transport.IsConnected);
        Assert.Null(Record.Exception(() => transport.Disconnect()));
        Assert.Null(Record.Exception(() => transport.Disconnect()));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void DisposeAfterDisconnect_IsStillSafe()
    {
        var transport = new NamedPipeDiscordTransport();
        transport.Disconnect();

        Assert.Null(Record.Exception(() => transport.Dispose()));
        Assert.Null(Record.Exception(() => transport.Dispose()));
    }

    [Fact]
    public void ReadOnAnIdleTransport_ReturnsNothing_RatherThanBlocking()
    {
        using var transport = new NamedPipeDiscordTransport();
        byte[] buffer = new byte[64];

        Assert.Equal(0, transport.Read(buffer));
    }
}
