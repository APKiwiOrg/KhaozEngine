using System;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Netcode;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The catalog content door's two refusals reach a <see cref="WorldClient"/> as typed reasons (#1071):
/// <c>ke:content-mismatch:</c> as <see cref="DisconnectReason.ContentVersionMismatch"/> and
/// <c>ke:content-client-too-old:</c> as <see cref="DisconnectReason.ContentClientTooOld"/>. Both are terminal like
/// <see cref="DisconnectReason.IncompatibleVersion"/>. NetWorld only RECOGNIZES them, by the prefixes
/// <see cref="HandshakeToken"/> holds, and keeps the whole token in <see cref="WorldClient.DisconnectReasonDetail"/>,
/// which a game running the content door parses with <see cref="ContentRefusal"/>, as these tests do.
/// </summary>
public class CatalogRefusalMappingTests
{
    private static readonly string ServedHash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);
    private static readonly ContentVersionIdentity Served = new(47, ServedHash);
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private const float Tick = 1f / 30f;

    // The catalog door as the only consumer gate. WorldServer adds the wire-generation gate around it, so a
    // WorldClient's own token (the auth bytes under the wire layer) is exactly what the content gate peels.
    private static WorldServer Server(InMemoryTransportHub hub, int minimumClientBuild = 0) =>
        new(hub.Server, new WorldServerConfig { TickSeconds = Tick, MaxPlayers = 4 }, Flat, MoveTuning.Default,
            authenticator: new ContentIdentityGateAuthenticator(Served, minimumClientBuild: minimumClientBuild));

    private static void Pump(WorldServer server, WorldClient client, int rounds = 30)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            server.Tick(Tick);
            client.Poll(Tick);
        }
    }

    private static WorldClient SingleShot(InMemoryTransportHub hub, byte[]? token) =>
        new(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Tick }, token: token);

    [Fact]
    public void The_matching_content_version_joins()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub);
        WorldClient client = SingleShot(hub, ContentIdentityLayer.Wrap(Served));

        Pump(server, client);

        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void A_content_mismatch_is_typed_and_the_kept_token_parses_with_the_catalog_parser()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub);
        var held = new ContentVersionIdentity(46, OtherHash);
        WorldClient client = SingleShot(hub, ContentIdentityLayer.Wrap(held));

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ContentVersionMismatch, client.DisconnectReason);
        Assert.Equal(ContentRefusal.Mismatch(Served, held), client.DisconnectReasonDetail);
        Assert.True(ContentRefusal.TryParseMismatch(client.DisconnectReasonDetail,
            out ContentVersionIdentity serverSide, out ContentVersionIdentity? clientSide));
        Assert.Equal(Served, serverSide);
        Assert.Equal(held, clientSide);
        Assert.Null(client.ContentMismatch);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_client_with_no_content_layer_reads_a_mismatch_whose_client_side_parses_as_none()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub);
        WorldClient client = SingleShot(hub, token: null);

        Pump(server, client);

        Assert.Equal(DisconnectReason.ContentVersionMismatch, client.DisconnectReason);
        Assert.True(ContentRefusal.TryParseMismatch(client.DisconnectReasonDetail, out _, out ContentVersionIdentity? clientSide));
        Assert.Null(clientSide);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_client_below_the_minimum_build_is_typed_and_the_build_parses_out_of_the_kept_token()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, minimumClientBuild: 4118);
        WorldClient client = SingleShot(hub, ContentIdentityLayer.Wrap(Served, clientBuild: 4117));

        Pump(server, client);

        Assert.Equal(DisconnectReason.ContentClientTooOld, client.DisconnectReason);
        Assert.Equal("ke:content-client-too-old:4118", client.DisconnectReasonDetail);
        Assert.True(ContentRefusal.TryParseClientTooOld(client.DisconnectReasonDetail, out int minimumClientBuild));
        Assert.Equal(4118, minimumClientBuild);
        Assert.Equal(0, server.PlayerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Neither_catalog_refusal_is_retried_not_even_with_RetryOnReject(bool tooOld)
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, minimumClientBuild: 10);
        byte[] token = tooOld
            ? ContentIdentityLayer.Wrap(Served, clientBuild: 9)
            : ContentIdentityLayer.Wrap(new ContentVersionIdentity(46, OtherHash));
        var client = new WorldClient(() => hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Tick, RetryOnReject = true }, token: token);

        Pump(server, client, rounds: 120);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(tooOld ? DisconnectReason.ContentClientTooOld : DisconnectReason.ContentVersionMismatch,
            client.DisconnectReason);
        Assert.Equal(0, client.ReconnectAttempt);
    }

    // The prefixes are STABLE WIRE STRINGS with ONE source in Netcode. Pinned literally, and the catalog's own
    // constants are the same values, so the builder and the recognizer cannot drift apart.
    [Fact]
    public void The_prefixes_live_once_in_Netcode_and_the_catalog_builds_from_them()
    {
        Assert.Equal("ke:content-mismatch:", HandshakeToken.ContentMismatchPrefix);
        Assert.Equal("ke:content-client-too-old:", HandshakeToken.ContentClientTooOldPrefix);
        Assert.Equal(HandshakeToken.ContentMismatchPrefix, ContentRefusal.MismatchPrefix);
        Assert.Equal(HandshakeToken.ContentClientTooOldPrefix, ContentRefusal.ClientTooOldPrefix);
        Assert.StartsWith(HandshakeToken.ContentMismatchPrefix, ContentRefusal.Mismatch(Served, null), StringComparison.Ordinal);
        Assert.StartsWith(HandshakeToken.ContentClientTooOldPrefix, ContentRefusal.ClientTooOld(3), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ke:content-mismatch:47|aaaa|46|bbbb", DisconnectReason.ContentVersionMismatch)]
    [InlineData("ke:content-mismatch:", DisconnectReason.ContentVersionMismatch)]
    [InlineData("ke:content-client-too-old:4118", DisconnectReason.ContentClientTooOld)]
    [InlineData("ke:content-client-too-old:", DisconnectReason.ContentClientTooOld)]
    public void A_token_carrying_either_prefix_maps_to_its_typed_reason_keeps_the_whole_token_and_never_retries(
        string reason, DisconnectReason expected)
    {
        ConnectRefusal read = ConnectRefusal.Read(reason);

        Assert.Equal(expected, read.Reason);
        Assert.Equal(reason, read.Detail);
        Assert.False(read.AllowsReconnect(retryOnReject: true));
    }

    [Fact]
    public void The_typed_reason_names_the_refusal_and_the_catalog_parser_still_judges_the_body()
    {
        // NetWorld recognizes the door by prefix only, so a body the strict parser refuses still reads as the
        // content door's refusal. What it SAYS is the catalog parser's call, and a malformed body says nothing.
        const string mangled = "ke:content-mismatch:not-a-token";

        Assert.Equal(DisconnectReason.ContentVersionMismatch, ConnectRefusal.Read(mangled).Reason);
        Assert.False(ContentRefusal.TryParseMismatch(mangled, out _, out _));
    }

    [Theory]
    [InlineData("ke:content-mismatch")]
    [InlineData("KE:CONTENT-MISMATCH:47|a|46|b")]
    [InlineData("ke:content-client-too-old")]
    [InlineData("x ke:content-client-too-old:1")]
    public void Anything_short_of_the_exact_prefix_stays_a_plain_rejection(string reason)
    {
        ConnectRefusal read = ConnectRefusal.Read(reason);

        Assert.Equal(DisconnectReason.RejectedToken, read.Reason);
        Assert.Equal(reason, read.Detail);
        Assert.True(read.AllowsReconnect(retryOnReject: true));
        Assert.False(read.AllowsReconnect(retryOnReject: false));
    }

    [Fact]
    public void The_existing_mappings_are_unchanged()
    {
        Assert.Equal(DisconnectReason.IncompatibleVersion, ConnectRefusal.Read("ke:incompatible-version:7").Reason);
        Assert.Equal("7", ConnectRefusal.Read("ke:incompatible-version:7").Detail);
        Assert.Equal(DisconnectReason.ContentMismatch, ConnectRefusal.Read("ke:world-mismatch:a|b").Reason);
        Assert.Equal("a", ConnectRefusal.Read("ke:world-mismatch:a|b").Detail);
        Assert.Equal(DisconnectReason.SignedInElsewhere, ConnectRefusal.Read(SessionRejectReason.SignedInElsewhere).Reason);
        Assert.True(ConnectRefusal.Read(SessionRejectReason.AlreadySignedIn).AllowsReconnect(retryOnReject: false));
        Assert.Equal(DisconnectReason.RejectedToken, ConnectRefusal.Read(HandshakeToken.BannedReason).Reason);
        Assert.Equal(HandshakeToken.BannedReason, ConnectRefusal.Read(HandshakeToken.BannedReason).Detail);
        Assert.Equal(DisconnectReason.RejectedToken, ConnectRefusal.Read(null).Reason);
        Assert.Equal(string.Empty, ConnectRefusal.Read(null).Detail);
    }

    // Appended LAST so no shipped numeric value moves: a consumer that persisted or switched on the raw value keeps
    // its meaning.
    [Fact]
    public void The_new_reasons_are_appended_after_every_shipped_value()
    {
        Assert.Equal(0, (int)DisconnectReason.None);
        Assert.Equal(1, (int)DisconnectReason.RejectedToken);
        Assert.Equal(5, (int)DisconnectReason.IncompatibleVersion);
        Assert.Equal(8, (int)DisconnectReason.Banned);
        Assert.Equal(9, (int)DisconnectReason.ContentMismatch);
        Assert.Equal(10, (int)DisconnectReason.ContentVersionMismatch);
        Assert.Equal(11, (int)DisconnectReason.ContentClientTooOld);
        Assert.Equal(12, Enum.GetValues<DisconnectReason>().Length);
    }
}
