using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The guest: prefix is RESERVED for the id both heads derive for a tokenless connection, and since #647 it is
/// what decides whether persistence files a record at all. A game that namespaces its own account ids could mint
/// a verified subject inside that namespace and play a whole session that was silently never saved: no log line,
/// no event, no rejection. The join gate is the last place that can still tell a minted subject from a derived
/// seat key, so that is where it is refused.
/// </summary>
public class ReservedGuestSubjectTests
{
    static float Flat(float x, float z) => 0f;

    const string Reserved = ResumePositionCache.GuestAccountPrefix + "alice";

    [Fact]
    public void WorldServer_refuses_a_verified_subject_inside_the_reserved_guest_namespace()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(Reserved));   // AllowAllAuthenticator: subject = token

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void ShardedWorldServer_refuses_a_verified_subject_inside_the_reserved_guest_namespace()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(Reserved));

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }

    /// <summary>
    /// Additive, and it stays additive: it is the PREFIX that decides, so every subject format that worked before
    /// still works, including one that merely starts with the word. A TOKENLESS connection is untouched too, since
    /// its seat key is derived after this gate rather than presented to it.
    /// </summary>
    [Theory]
    [InlineData("alice")]
    [InlineData("guest")]
    [InlineData("guests:alice")]
    [InlineData("myguest:alice")]
    public void An_ordinary_subject_still_joins(string subject)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(subject));

        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void A_tokenless_connection_still_joins_on_its_derived_seat_key()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        string? account = null;
        server.PlayerJoined += (_, a) => account = a;
        var client = new NetClient(ct, TestHandshake.Wire(string.Empty));

        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(ResumePositionCache.GuestAccountPrefix + "0", account);
    }
}
