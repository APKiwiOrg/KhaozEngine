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

    // --- 10.0.0 NetId widening: the wire-format generation gate. A consumer folds MoveProtocol.WireProtocolVersion
    // into its handshake version, so a 10.0.0 (wire 2) peer and a pre-10.0.0 (wire 1) peer reject each other cleanly at
    // connect instead of misparsing a 64-bit frame as 32-bit. Both skew directions must produce the clean disconnect.

    private static string WireTag(int wire) => $"ruinborne-1.0;wire{wire}";

    [Fact]
    public void Wire_version_match_joins()
    {
        var hub = new InMemoryHub();
        string ver = WireTag(MoveProtocol.WireProtocolVersion);
        WorldServer server = Server(hub, new VersionCheckingAuthenticator(ver, v => v == ver));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = ver });

        Pump(server, client);

        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void Wire_version_skew_new_server_rejects_old_client_cleanly()
    {
        // A 10.0.0 server (wire 2) and a 9.x client (wire 1): the 64-bit wire would misparse a 32-bit frame, so the
        // handshake rejects at connect - a clean IncompatibleVersion, never an admitted-then-crashing client.
        var hub = new InMemoryHub();
        string serverVer = WireTag(MoveProtocol.WireProtocolVersion);   // wire 2
        WorldServer server = Server(hub, new VersionCheckingAuthenticator(serverVer, v => v == serverVer));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = WireTag(1) });     // an old (wire 1) client

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.Equal(serverVer, client.DisconnectReasonDetail);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);   // never admitted -> never receives a frame to misparse
    }

    [Fact]
    public void Wire_version_skew_old_server_rejects_new_client_cleanly()
    {
        // The reverse skew: a 9.x server (wire 1) and a 10.0.0 client (wire 2). Same clean disconnect, not a misparse.
        var hub = new InMemoryHub();
        string serverVer = WireTag(1);                                   // an old (wire 1) server
        WorldServer server = Server(hub, new VersionCheckingAuthenticator(serverVer, v => v == serverVer));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = WireTag(MoveProtocol.WireProtocolVersion) });   // wire 2

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);
    }

    // --- 10.2.0: the engine wire generation is now enforced UNCONDITIONALLY (pre-10.2.0 it was opt-in, so an
    // unconfigured 9.x/10.0 pairing silently misparsed instead of rejecting). Every client folds
    // MoveProtocol.WireProtocolVersion into its Hello even with NO consumer ProtocolVersion, and WorldServer /
    // ShardedWorldServer always install a WireGenerationAuthenticator. The tests above still pass because they now get
    // BOTH gates (the automatic engine wire gate AND the consumer's string check); folding ;wire{N} into the consumer
    // version is no longer required. These cover the UNCONFIGURED pairing - neither side sets a consumer version -
    // relying purely on the engine gate. A different EXPECTED generation on the server's gate stands in for a
    // different-build peer, since a live build's own WireProtocolVersion is a const (both live ends always match).

    [Fact]
    public void Unconfigured_same_generation_pairing_joins()
    {
        // No consumer version on either side: they rely purely on the engine wire gate, and matching generations join.
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new AllowAllAuthenticator());   // WorldServer wraps this in the always-on wire gate
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig());

        Pump(server, client);

        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void Unconfigured_wire_skew_newer_server_rejects_this_client_cleanly()
    {
        // A server one wire generation AHEAD (a newer build) - the "old client -> new server" direction the fix targets.
        // Our version-less client sends the current generation; the server's gate wants the next -> clean
        // IncompatibleVersion at connect, never an admitted-then-misparsing client.
        var hub = new InMemoryHub();
        int serverGen = MoveProtocol.WireProtocolVersion + 1;
        WorldServer server = Server(hub, new WireGenerationAuthenticator(serverGen, new AllowAllAuthenticator()));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig());

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.Equal(ProtocolHandshake.WireGenerationLabel(serverGen), client.DisconnectReasonDetail);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);   // never admitted -> never a frame to misparse
    }

    [Fact]
    public void Unconfigured_wire_skew_older_server_rejects_this_client_cleanly()
    {
        // The reverse skew: a server one generation BEHIND (an older build that still has the gate). Our client is
        // ahead; the server's gate rejects it. The engine gate is symmetric - a mismatch either way is a clean disconnect.
        var hub = new InMemoryHub();
        int serverGen = MoveProtocol.WireProtocolVersion - 1;
        WorldServer server = Server(hub, new WireGenerationAuthenticator(serverGen, new AllowAllAuthenticator()));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig());

        Pump(server, client);

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void Wire_gate_accepts_the_matching_generation_and_rejects_a_mismatch_or_a_legacy_token()
    {
        var gate = new WireGenerationAuthenticator(MoveProtocol.WireProtocolVersion, new AllowAllAuthenticator());

        // Matching generation: admitted, and the inner (stripped) token becomes the subject.
        byte[] ok = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, consumerVersion: null,
            Encoding.UTF8.GetBytes("player-7"));
        Assert.True(gate.TryAuthenticate(ok, out string subject, out _));
        Assert.Equal("player-7", subject);

        // A pre-10.2.0 / 9.x client sends a RAW token (no wire layer): rejected as IncompatibleVersion, carrying this
        // build's required wire label.
        byte[] legacy = Encoding.UTF8.GetBytes("player-7");
        Assert.False(gate.TryAuthenticate(legacy, out _, out string legacyReason));
        Assert.True(ProtocolHandshake.TryParseIncompatibleReason(legacyReason, out string required));
        Assert.Equal(ProtocolHandshake.WireGenerationLabel(MoveProtocol.WireProtocolVersion), required);

        // A different generation is likewise rejected.
        byte[] skew = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion + 1, consumerVersion: null, null);
        Assert.False(gate.TryAuthenticate(skew, out _, out _));
    }

    [Fact]
    public void Wire_gate_delegates_the_inner_display_name_after_peeling_its_layer()
    {
        // The gate peels its wire layer and delegates to the inner authenticator's display-name resolution, so a v2
        // signed token's name still surfaces through the always-on gate (WorldServer wraps every authenticator).
        string token = SignedToken.Mint("acct-7", "Daniel", Now.AddHours(1), Secret);
        var gate = new WireGenerationAuthenticator(MoveProtocol.WireProtocolVersion,
            new HmacTokenAuthenticator(Secret, () => Now));
        byte[] wrapped = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, consumerVersion: null,
            Encoding.UTF8.GetBytes(token));

        Assert.True(gate.TryAuthenticate(wrapped, out string subject, out _));
        Assert.Equal("acct-7", subject);
        Assert.Equal("Daniel", gate.ReadDisplayName(wrapped));
    }

    [Fact]
    public void Consumer_version_gate_still_layers_on_top_of_the_wire_gate()
    {
        // The opt-in consumer ProtocolVersion is unchanged: it rides as an inner layer the WorldServer's
        // VersionCheckingAuthenticator (composed INSIDE the always-on wire gate) checks after the generation matches.
        var hub = new InMemoryHub();
        WorldServer server = Server(hub, new VersionCheckingAuthenticator("game-3", v => v == "game-3"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { ProtocolVersion = "game-3" });

        Pump(server, client);

        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }
}
