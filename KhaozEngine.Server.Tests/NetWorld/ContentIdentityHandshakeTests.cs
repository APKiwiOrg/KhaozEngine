using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The opt-in content-identity slot: <see cref="WorldClientConfig.ContentIdentity"/> on the client, a
/// <see cref="WorldIdentityGateAuthenticator"/> inside the version gate on the server, and a typed
/// <see cref="DisconnectReason.ContentMismatch"/> carrying both identities. Covers the match, the mismatch, the
/// unconfigured wire staying byte-identical, a server requiring an identity the client never sent, the version gate
/// winning over the content gate, and composition with an inner consumer authenticator.
/// </summary>
public class ContentIdentityHandshakeTests
{
    private const string Version = "game-4";
    private const string ServerContent = "content-a1b2";
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("content-identity-secret");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    private static WorldServer Server(InMemoryTransportHub hub, IConnectionAuthenticator auth)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        return new WorldServer(hub.Server, config, Flat, MoveTuning.Default, authenticator: auth);
    }

    // The documented composition: consumer version gate outermost, content gate just inside it, the game's own
    // authenticator innermost. WorldServer adds the wire-generation gate around all of it.
    private static IConnectionAuthenticator Door(IConnectionAuthenticator? inner = null) =>
        new VersionCheckingAuthenticator(Version, v => v == Version,
            new WorldIdentityGateAuthenticator(ServerContent, inner));

    private static void Pump(WorldServer server, WorldClient client, int rounds = 10)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            server.Tick(1f / 30f);
            client.Poll();
        }
    }

    // One handshake layer exactly as HandshakeToken writes it, spelled out byte by byte so the byte-identity test
    // pins the layout rather than reusing the builder under test.
    private static IEnumerable<byte> Layer(string label) =>
        new byte[] { 0x00, (byte)'K', (byte)'E', (byte)'V', (byte)'1', (byte)Encoding.UTF8.GetByteCount(label) }
            .Concat(Encoding.UTF8.GetBytes(label));

    private static byte[] Hello(IEnumerable<byte> token) =>
        new[] { (byte)SessionOpcode.Hello }.Concat(token).ToArray();

    [Fact]
    public void Matching_identities_join()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = ServerContent });

        Pump(server, client);

        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
        Assert.Null(client.ContentMismatch);
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void Mismatched_identities_are_rejected_as_ContentMismatch_with_both_identities()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = "content-ffff" });

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ContentMismatch, client.DisconnectReason);
        Assert.Equal(ServerContent, client.DisconnectReasonDetail);
        Assert.Equal(new ContentMismatchDetail(ServerContent, "content-ffff"), client.ContentMismatch);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_content_mismatch_is_terminal_even_with_auto_reconnect()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var client = new WorldClient(() => hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = "content-ffff" });

        Pump(server, client, rounds: 30);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ContentMismatch, client.DisconnectReason);
        Assert.Equal(0, client.ReconnectAttempt);
    }

    [Fact]
    public void Server_requiring_an_identity_rejects_a_client_that_sent_none_with_an_empty_client_identity()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version });

        Pump(server, client);

        Assert.Equal(DisconnectReason.ContentMismatch, client.DisconnectReason);
        Assert.Equal(new ContentMismatchDetail(ServerContent, string.Empty), client.ContentMismatch);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_version_mismatch_is_reported_as_the_version_rejection_even_when_content_also_differs()
    {
        // Ordering is load-bearing: the version gate is outermost, so a client that is skewed on both reads
        // IncompatibleVersion and never reaches the content check.
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = "game-3", ContentIdentity = "content-ffff" });

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.Equal(Version, client.DisconnectReasonDetail);
        Assert.Null(client.ContentMismatch);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void An_identity_without_a_consumer_version_sits_directly_inside_the_wire_gate()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, new WorldIdentityGateAuthenticator(ServerContent));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ContentIdentity = ServerContent });

        Pump(server, client);

        Assert.True(client.Joined);
    }

    [Fact]
    public void Neither_side_configured_sends_a_byte_identical_hello_and_connects()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, new VersionCheckingAuthenticator(Version, v => v == Version));
        var recording = new RecordingTransport(hub.CreateClient());
        byte[] inner = Encoding.UTF8.GetBytes("player-7");
        var client = new WorldClient(recording, Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version }, token: inner);

        Pump(server, client);

        byte[] expected = Hello(Layer(ProtocolHandshake.WireGenerationLabel(MoveProtocol.WireProtocolVersion))
            .Concat(Layer(Version)).Concat(inner));
        Assert.Equal(expected, recording.Sends[0].payload);
        Assert.True(client.Joined);
        Assert.Same(inner, ProtocolHandshake.WrapContentIdentity(null, inner));
    }

    [Fact]
    public void A_configured_identity_rides_just_inside_the_consumer_version_layer()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door());
        var recording = new RecordingTransport(hub.CreateClient());
        byte[] inner = Encoding.UTF8.GetBytes("player-7");
        var client = new WorldClient(recording, Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = ServerContent }, token: inner);

        Pump(server, client);

        byte[] expected = Hello(Layer(ProtocolHandshake.WireGenerationLabel(MoveProtocol.WireProtocolVersion))
            .Concat(Layer(Version)).Concat(Layer(ServerContent)).Concat(inner));
        Assert.Equal(expected, recording.Sends[0].payload);
    }

    [Fact]
    public void An_inner_consumer_authenticator_still_resolves_the_subject_and_display_name()
    {
        string signed = SignedToken.Mint("acct-42", "Daniel", Now.AddHours(1), Secret);
        byte[] token = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, Version,
            ProtocolHandshake.WrapContentIdentity(ServerContent, Encoding.UTF8.GetBytes(signed)));
        var door = new WireGenerationAuthenticator(MoveProtocol.WireProtocolVersion,
            Door(new HmacTokenAuthenticator(Secret, () => Now)));

        Assert.True(door.TryAuthenticate(token, out string subject, out _));
        Assert.Equal("acct-42", subject);
        Assert.Equal("Daniel", door.ReadDisplayName(token));
    }

    [Fact]
    public void Matching_content_still_meets_the_inner_authenticator_and_mismatched_content_never_does()
    {
        // A matching identity delegates inward, so a bad credential is the inner gate's RejectedToken. A mismatched
        // identity is refused at the content gate before the credential is ever read.
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, Door(new HmacTokenAuthenticator(Secret, () => Now)));
        byte[] bad = Encoding.UTF8.GetBytes("not-a-signed-token");
        var matching = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = ServerContent }, token: bad);
        var mismatched = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = Version, ContentIdentity = "content-ffff" }, token: bad);

        for (int i = 0; i < 10; i++)
        {
            server.Poll();
            server.Tick(1f / 30f);
            matching.Poll();
            mismatched.Poll();
        }

        Assert.Equal(DisconnectReason.RejectedToken, matching.DisconnectReason);
        Assert.Null(matching.ContentMismatch);
        Assert.Equal(DisconnectReason.ContentMismatch, mismatched.DisconnectReason);
        Assert.Equal(0, server.PlayerCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("content|a")]
    public void An_unusable_client_identity_is_refused_at_construction(string identity)
    {
        var hub = new InMemoryTransportHub();

        Assert.Throws<ArgumentException>(() => ProtocolHandshake.WrapContentIdentity(identity, null));
        Assert.Throws<ArgumentException>(() => new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ContentIdentity = identity }));
    }

    [Fact]
    public void An_identity_over_the_label_cap_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            ProtocolHandshake.WrapContentIdentity(new string('a', HandshakeToken.MaxLabelBytes + 1), null));
    }

    [Fact]
    public void Detail_parses_only_the_content_refusal_token()
    {
        Assert.True(ContentMismatchDetail.TryParse(
            HandshakeToken.WorldMismatchReason("server-x", "client-y"), out ContentMismatchDetail detail));
        Assert.Equal(new ContentMismatchDetail("server-x", "client-y"), detail);

        Assert.False(ContentMismatchDetail.TryParse(HandshakeToken.IncompatibleVersionReason("2"), out _));
        Assert.False(ContentMismatchDetail.TryParse("ke:world-mismatch:no-pipe", out _));
        Assert.False(ContentMismatchDetail.TryParse(null, out _));
    }
}
