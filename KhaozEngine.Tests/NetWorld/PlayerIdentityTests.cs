using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerIdentityTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("display-name-secret");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    // ---- A: codec round-trip + clamp (registry level) ----

    [Fact]
    public void DisplayName_round_trips_through_a_filtered_snapshot()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(7));
        server.Set(e, new PlayerIdentity { DisplayName = "Daniel" });

        byte[] snapshot = SnapshotWriter.WriteFiltered(server, registry, new System.Collections.Generic.HashSet<long> { 7 });

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(client, snapshot);

        Assert.True(view.TryGetEntity(7, out Entity ce));
        Assert.True(client.TryGet(ce, out PlayerIdentity identity));
        Assert.Equal("Daniel", identity.DisplayName);
    }

    [Fact]
    public void Over_long_name_is_clamped_on_the_wire_not_blowing_the_buffer()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        string huge = new string('x', 500);   // 500 ASCII bytes, well over the cap
        server.Set(e, new PlayerIdentity { DisplayName = huge });

        byte[] snapshot = SnapshotWriter.WriteFiltered(server, registry, new System.Collections.Generic.HashSet<long> { 1 });
        // The cap bounds the encoded name: nothing close to the 500-byte string reaches the wire.
        Assert.True(snapshot.Length < 128, $"snapshot {snapshot.Length} bytes - name was not clamped on write");

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(client, snapshot);   // must not throw / over-read
        view.TryGetEntity(1, out Entity ce);
        Assert.True(client.TryGet(ce, out PlayerIdentity identity));
        Assert.True(Encoding.UTF8.GetByteCount(identity.DisplayName) <= MoveProtocol.MaxDisplayNameBytes);
        Assert.Equal(new string('x', MoveProtocol.MaxDisplayNameBytes), identity.DisplayName);
    }

    [Fact]
    public void Over_long_name_truncates_on_a_utf8_character_boundary()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(2));
        // '€' (U+20AC) is 3 UTF-8 bytes; 30 of them = 90 bytes. The 64-byte cap cuts mid-codepoint, so the writer
        // must back up to 21 whole glyphs (63 bytes) rather than emit a split, invalid sequence.
        string euros = new string('€', 30);
        server.Set(e, new PlayerIdentity { DisplayName = euros });

        byte[] snapshot = SnapshotWriter.WriteFiltered(server, registry, new System.Collections.Generic.HashSet<long> { 2 });
        var client = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(client, snapshot);
        view.TryGetEntity(2, out Entity ce);
        client.TryGet(ce, out PlayerIdentity identity);

        Assert.Equal(new string('€', 21), identity.DisplayName);                 // 21 * 3 = 63 bytes <= 64
        Assert.Equal(63, Encoding.UTF8.GetByteCount(identity.DisplayName));
    }

    // ---- A: EntityRenderState surface ----

    [Fact]
    public void EntityRenderState_three_arg_ctor_leaves_DisplayName_null()
    {
        var s = new EntityRenderState(new NetId(5), Vector3.Zero, isLocal: true);
        Assert.Null(s.DisplayName);
    }

    [Fact]
    public void WorldClient_surfaces_DisplayName_when_set_and_null_when_absent()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0);
        Assert.Null(LocalState(client).DisplayName);   // no name yet

        foreach (int slot in server.JoinedSlots) server.SetPlayerDisplayName(slot, "Daniel");
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.Equal("Daniel", LocalState(client).DisplayName);
    }

    // ---- A: token display-name claim auto-applied on join, end to end ----

    [Fact]
    public void Token_display_name_claim_is_auto_applied_and_reaches_the_client()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 4 };
        var auth = new HmacTokenAuthenticator(Secret, () => Now);
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, authenticator: auth);

        // The account id (subject) and the display name are deliberately different strings.
        string token = SignedToken.Mint("acct-1", "Zara", Now.AddHours(1), Secret);
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, token: Encoding.UTF8.GetBytes(token));

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        EntityRenderState local = LocalState(client);
        Assert.Equal("Zara", local.DisplayName);                       // name auto-applied from the token claim
        foreach (int slot in server.JoinedSlots)
        {
            Assert.True(server.TryGetAccountId(slot, out string acct));
            Assert.Equal("acct-1", acct);                              // account id unchanged (subject != name)
        }
    }

    private static EntityRenderState LocalState(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e;
        throw new Xunit.Sdk.XunitException("no local entity in client snapshot");
    }
}
