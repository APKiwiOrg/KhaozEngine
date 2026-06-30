using System;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Covers the opt-in connect-time version handshake: a compatible client joins; an incompatible (or
/// legacy/version-less) client is rejected cleanly as <see cref="DisconnectReason.IncompatibleVersion"/> and never
/// proceeds to snapshots; and the wire is unchanged when the handshake is not used. Plus unit coverage of the
/// wrap/unwrap + reject-reason helpers and the authenticator's delegation to its inner gate.
/// </summary>
public class VersionHandshakeTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("handshake-secret");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    private static WorldServer Server(InMemoryHub hub, IConnectionAuthenticator auth)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        return new WorldServer(hub.Server, config, Flat, MoveTuning.Default, authenticator: auth);
    }

    private static void Pump(WorldServer server, WorldClient client, int rounds = 10)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            server.Tick(1f / 30f);
            client.Poll();
        }
    }

    [Fact]
    public void Compatible_version_joins()
    {
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new VersionCheckingAuthenticator("2", v => v == "2"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = "2" });

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void Incompatible_version_is_rejected_as_IncompatibleVersion_with_required_in_detail()
    {
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new VersionCheckingAuthenticator("2", v => v == "2"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = "1" });

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.Equal("2", client.DisconnectReasonDetail);   // server's required version
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);                // never admitted -> never receives snapshots
    }

    [Fact]
    public void Versionless_client_against_version_checking_server_is_rejected_not_admitted()
    {
        // Proxy for an out-of-date client that predates the handshake: it sends no version, the server's rule
        // rejects the empty version, so it is turned away at connect rather than admitted and later crashing on a
        // snapshot it cannot decode.
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new VersionCheckingAuthenticator("2", v => v == "2"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig());

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void No_handshake_is_backwards_compatible()
    {
        // Neither side opts in: a plain server + a version-less client connect exactly as before.
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new AllowAllAuthenticator());
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig());

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void WrapToken_round_trips()
    {
        byte[] inner = Encoding.UTF8.GetBytes("player-7");
        byte[] wrapped = ProtocolHandshake.WrapToken("3.2.1", inner);

        Assert.True(ProtocolHandshake.TryUnwrapToken(wrapped, out string version, out byte[] gotInner));
        Assert.Equal("3.2.1", version);
        Assert.Equal(inner, gotInner);
    }

    [Fact]
    public void Unwrapping_a_plain_token_yields_empty_version_and_the_original_bytes()
    {
        byte[] plain = Encoding.UTF8.GetBytes("v1.player-7.1700000000.abc");

        Assert.False(ProtocolHandshake.TryUnwrapToken(plain, out string version, out byte[] inner));
        Assert.Equal(string.Empty, version);
        Assert.Equal(plain, inner);
    }

    [Fact]
    public void Incompatible_reason_round_trips()
    {
        string reason = ProtocolHandshake.IncompatibleReason("4.5.6");
        Assert.True(ProtocolHandshake.TryParseIncompatibleReason(reason, out string required));
        Assert.Equal("4.5.6", required);
        Assert.False(ProtocolHandshake.TryParseIncompatibleReason("bad token", out _));
    }

    [Fact]
    public void Authenticator_delegates_inner_subject_on_compatible_version()
    {
        // A compatible version unwraps to the real signed token, which the inner HMAC authenticator verifies.
        string token = SignedToken.Mint("player-42", Now.AddHours(1), Secret);
        byte[] wrapped = ProtocolHandshake.WrapToken("2", Encoding.UTF8.GetBytes(token));
        var auth = new VersionCheckingAuthenticator("2", v => v == "2",
            inner: new HmacTokenAuthenticator(Secret, () => Now));

        bool ok = auth.TryAuthenticate(wrapped, out string subject, out _);

        Assert.True(ok);
        Assert.Equal("player-42", subject);
    }

    [Fact]
    public void Authenticator_rejects_incompatible_before_consulting_inner()
    {
        // Even a validly-signed token is rejected when the version is wrong (version gate runs first).
        string token = SignedToken.Mint("player-42", Now.AddHours(1), Secret);
        byte[] wrapped = ProtocolHandshake.WrapToken("1", Encoding.UTF8.GetBytes(token));
        var auth = new VersionCheckingAuthenticator("2", v => v == "2",
            inner: new HmacTokenAuthenticator(Secret, () => Now));

        bool ok = auth.TryAuthenticate(wrapped, out string subject, out string reason);

        Assert.False(ok);
        Assert.Equal(string.Empty, subject);
        Assert.True(ProtocolHandshake.TryParseIncompatibleReason(reason, out string required));
        Assert.Equal("2", required);
    }
}
