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
/// <c>ke:content-mismatch:</c> as <see cref="DisconnectReason.ContentVersionMismatch"/> with both versions on
/// <see cref="WorldClient.ContentVersionMismatch"/>, and <c>ke:content-client-too-old:</c> as
/// <see cref="DisconnectReason.ContentClientTooOld"/> with the build on <see cref="WorldClient.MinimumClientBuild"/>.
/// Both are terminal like <see cref="DisconnectReason.IncompatibleVersion"/>, and both keep the whole token in
/// <see cref="WorldClient.DisconnectReasonDetail"/>, which is what a game parsing it under RejectedToken reads.
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
        Assert.Null(client.ContentVersionMismatch);
        Assert.Null(client.MinimumClientBuild);
    }

    [Fact]
    public void A_content_mismatch_is_typed_with_both_versions_and_keeps_the_whole_token()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub);
        var held = new ContentVersionIdentity(46, OtherHash);
        WorldClient client = SingleShot(hub, ContentIdentityLayer.Wrap(held));

        Pump(server, client);

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ContentVersionMismatch, client.DisconnectReason);
        Assert.Equal(new ContentVersionMismatchDetail(Served, held), client.ContentVersionMismatch);
        Assert.Equal(ContentRefusal.Mismatch(Served, held), client.DisconnectReasonDetail);
        Assert.True(ContentRefusal.TryParseMismatch(client.DisconnectReasonDetail, out _, out _));
        Assert.Null(client.ContentMismatch);
        Assert.Null(client.MinimumClientBuild);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_client_with_no_content_layer_reads_a_mismatch_with_no_client_side()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub);
        WorldClient client = SingleShot(hub, token: null);

        Pump(server, client);

        Assert.Equal(DisconnectReason.ContentVersionMismatch, client.DisconnectReason);
        Assert.Equal(new ContentVersionMismatchDetail(Served, null), client.ContentVersionMismatch);
    }

    [Fact]
    public void A_client_below_the_minimum_build_is_typed_with_the_build_it_has_to_reach()
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = Server(hub, minimumClientBuild: 4118);
        WorldClient client = SingleShot(hub, ContentIdentityLayer.Wrap(Served, clientBuild: 4117));

        Pump(server, client);

        Assert.Equal(DisconnectReason.ContentClientTooOld, client.DisconnectReason);
        Assert.Equal(4118, client.MinimumClientBuild);
        Assert.Equal("ke:content-client-too-old:4118", client.DisconnectReasonDetail);
        Assert.Null(client.ContentVersionMismatch);
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

    // The tokens are STABLE WIRE STRINGS, so the classification is pinned on literals rather than on the builders.
    [Fact]
    public void The_literal_tokens_map_to_their_typed_reasons_and_never_retry()
    {
        string mismatch = $"ke:content-mismatch:47|{ServedHash}|46|{OtherHash}";
        ConnectRefusal read = ConnectRefusal.Read(mismatch);
        Assert.Equal(DisconnectReason.ContentVersionMismatch, read.Reason);
        Assert.Equal(mismatch, read.Detail);
        Assert.Equal(new ContentVersionMismatchDetail(Served, new ContentVersionIdentity(46, OtherHash)),
            read.ContentVersionMismatch);
        Assert.False(read.AllowsReconnect(retryOnReject: true));

        read = ConnectRefusal.Read("ke:content-client-too-old:4118");
        Assert.Equal(DisconnectReason.ContentClientTooOld, read.Reason);
        Assert.Equal("ke:content-client-too-old:4118", read.Detail);
        Assert.Equal(4118, read.MinimumClientBuild);
        Assert.False(read.AllowsReconnect(retryOnReject: true));
    }

    [Theory]
    [InlineData("ke:content-mismatch:not-a-token")]
    [InlineData("ke:content-mismatch:47|short|46|short")]
    [InlineData("ke:content-client-too-old:")]
    [InlineData("ke:content-client-too-old:-3")]
    public void A_malformed_catalog_token_stays_a_plain_rejection(string reason)
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
