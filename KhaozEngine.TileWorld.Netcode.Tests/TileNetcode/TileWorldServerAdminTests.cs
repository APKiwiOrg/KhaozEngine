using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// <see cref="TileWorldServer"/> as an <see cref="IAdminControllable"/> (#826). The interface is named under
/// <c>KhaozEngine.NetWorld</c> but lives in <c>KhaozEngine.Netcode</c>, which is why this suite can implement against
/// it while <c>ArchitectureTests.TileWorldNetcode_NeverReferencesNetWorld</c> still holds for the package and for
/// this project.
/// </summary>
public class TileWorldServerAdminTests
{
    const float Dt = 0.25f;
    static readonly TileCoord Spawn = new(10, 10, 0);

    static TileWorldServer Server(InMemoryTransportHub hub, TileCollisionMap? map = null, TilePresenter? presenter = null) =>
        new(hub.Server, TileWorldServerTickTests.Config(Spawn) with { Presenter = presenter },
            map ?? TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));

    // A server and a client that joined with a token, so the account the admin surface resolves is a real subject
    // rather than a seat's guest id.
    sealed class Joined : IDisposable
    {
        public readonly InMemoryTransportHub Hub = new();
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        public readonly List<string> Notices = new();
        public int Disconnects;
        float serverAccum;

        public Joined(string account, TilePresenter? presenter = null)
        {
            Server = TileWorldServerAdminTests.Server(Hub, presenter: presenter);
            Client = new TileWorldClient(Hub.CreateClient(), new TileWorldClientConfig
            {
                TickSeconds = TileLoopbackHarness.Tick,
                StepTicks = TileLoopbackHarness.Ticks,
            }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
                connectToken: Encoding.UTF8.GetBytes(account));
            Client.NoticeReceived += Notices.Add;
            Client.Disconnected += () => Disconnects++;
            Client.Tick(0.13f);
            Client.Poll();
            // Long enough for the join AND the first reconciled snapshot, whose seeding cut is not a test's to count.
            Frames(16);
            Assert.True(Client.IsJoined);
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Client.Tick(TileLoopbackHarness.Frame);
                Server.Poll();
                serverAccum += TileLoopbackHarness.Frame;
                while (serverAccum >= TileLoopbackHarness.Tick)
                {
                    serverAccum -= TileLoopbackHarness.Tick;
                    Server.Tick(TileLoopbackHarness.Tick);
                }
                Client.Poll();
                Client.AdvancePresentation(TileLoopbackHarness.Frame);
            }
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void ListOnline_is_published_by_the_tick_with_accounts_names_net_ids_and_tile_centres()
    {
        var hub = new InMemoryTransportHub();
        // Two metre tiles and three metre planes, so a position that ignored the presenter could not pass.
        using TileWorldServer s = Server(hub, presenter: new TilePresenter(2f, 3f));
        long ari = s.SpawnPlayer(0, "acct-ari", "Ari");
        long bea = s.SpawnPlayer(3, "acct-bea", "Bea");
        s.SetPlayerState(3, TileMoveState.At(new TileCoord(12, 7, 1), TileDirection.N), teleport: true);

        // Nothing is published until a tick has run: the read is a snapshot, never a walk of the live index.
        Assert.Empty(s.ListOnline());

        s.Tick(Dt);
        IReadOnlyList<OnlinePlayer> online = s.ListOnline();
        Assert.Equal(2, online.Count);
        OnlinePlayer a = Assert.Single(online, p => p.Slot == 0);
        OnlinePlayer b = Assert.Single(online, p => p.Slot == 3);

        Assert.Equal("acct-ari", a.AccountId);
        Assert.Equal("Ari", a.DisplayName);
        Assert.Equal(ari, a.NetId);
        // The committed tile's centre through the presenter: x and z get the half tile, z is negated, and the height
        // is the plane index times the plane height on a presenter with no ground.
        Assert.Equal(new Vector3(10.5f * 2f, 0f, -10.5f * 2f), a.Position);
        Assert.True(a.Grounded);
        Assert.Equal(0f, a.VerticalVelocity);

        Assert.Equal("acct-bea", b.AccountId);
        Assert.Equal(bea, b.NetId);
        Assert.Equal(new Vector3(12.5f * 2f, 3f, -7.5f * 2f), b.Position);
    }

    [Fact]
    public void ListOnline_defaults_to_the_clients_placeholder_presenter()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);
        s.SpawnPlayer(0, "acct", "A");
        s.Tick(Dt);

        OnlinePlayer p = Assert.Single(s.ListOnline());
        Assert.Equal(new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight).PoseAt(Spawn).Position, p.Position);
    }

    // The snapshot a reader holds is never written again: a later tick publishes a NEW array when something changed,
    // and hands back the SAME one when nothing did, which is what keeps an idle tick allocation free.
    [Fact]
    public void A_published_snapshot_is_immutable_and_republished_only_on_change()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);
        s.SpawnPlayer(0, "acct", "A");
        s.Tick(Dt);
        IReadOnlyList<OnlinePlayer> first = s.ListOnline();

        s.Tick(Dt);
        Assert.Same(first, s.ListOnline());

        s.SpawnPlayer(1, "acct-2", "B");
        s.Tick(Dt);
        Assert.NotSame(first, s.ListOnline());
        Assert.Single(first);
        Assert.Equal(2, s.ListOnline().Count);
    }

    [Fact]
    public void Kick_is_queued_for_the_next_tick_and_reaches_the_client_as_its_token()
    {
        using var j = new Joined("acct-kick");

        j.Server.Kick(PlayerRef.Account("acct-kick"), "game:afk");
        // Queued, not applied: the call ran on "another thread" and touched nothing.
        Assert.Equal(1, j.Server.PlayerCount);

        j.Frames(8);
        Assert.Equal(0, j.Server.PlayerCount);
        Assert.Equal(new[] { "game:afk" }, j.Notices);
        Assert.Empty(j.Server.ListOnline());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a reason far longer than the one hundred and twenty eight bytes a notice frame carries, which an "
              + "operator typing a sentence into a console could easily produce")]
    public void A_kick_reason_that_cannot_ride_the_wire_goes_out_as_the_kicked_token(string? reason)
    {
        using var j = new Joined("acct-long");

        j.Server.Kick(PlayerRef.Slot(0), reason!);
        j.Frames(8);

        Assert.Equal(0, j.Server.PlayerCount);
        Assert.Equal(new[] { TileServerReason.Kicked }, j.Notices);
    }

    [Fact]
    public void A_kick_for_nobody_is_ignored()
    {
        using var j = new Joined("acct-here");

        j.Server.Kick(PlayerRef.Account("acct-elsewhere"), "game:x");
        j.Server.Kick(PlayerRef.Slot(7), "game:x");
        j.Server.Kick(PlayerRef.Account(""), "game:x");
        j.Frames(8);

        Assert.Equal(1, j.Server.PlayerCount);
        Assert.Empty(j.Notices);
    }

    [Fact]
    public void Broadcast_reaches_the_client_as_a_token_on_the_next_tick()
    {
        using var j = new Joined("acct-hear");

        j.Server.Broadcast("game:restart-soon");
        Assert.Empty(j.Notices);
        j.Frames(8);

        Assert.Equal(new[] { "game:restart-soon" }, j.Notices);
        Assert.Equal(1, j.Server.PlayerCount);
    }

    [Fact]
    public void Broadcast_refuses_a_token_the_wire_cannot_carry_on_the_callers_thread()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);

        Assert.Throws<ArgumentException>(() => s.Broadcast(""));
        Assert.Throws<ArgumentException>(() => s.Broadcast(null!));
        Assert.Throws<ArgumentException>(() => s.Broadcast(new string('x', TileProtocol.MaxNoticeBytes + 1)));
        s.Broadcast(new string('x', TileProtocol.MaxNoticeBytes));   // exactly at the cap is a token
        s.Tick(Dt);
    }

    [Fact]
    public void Teleport_moves_the_player_onto_the_snapped_tile_and_the_client_cuts_and_resyncs()
    {
        using var j = new Joined("acct-tp");
        int cuts = 0;
        j.Client.Teleported += () => cuts++;
        Assert.True(j.Server.TryGetPlayerState(0, out TileMoveState before));

        // A point inside the tile, off its centre, so the snap is exercised rather than a round trip.
        var target = new TileCoord(30, 41, 0);
        j.Server.Teleport(PlayerRef.Account("acct-tp"), new Vector3(30.8f, 0.4f, -41.2f));
        j.Frames(12);

        Assert.True(j.Server.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(target, after.Tile);
        Assert.Equal(before.Epoch + 1u, after.Epoch);
        Assert.False(after.IsStepping);
        Assert.Equal(target, j.Client.Prediction.PredictedState.Tile);
        Assert.Equal(1, cuts);
        Assert.Equal(new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight).PoseAt(target).Position,
            Assert.Single(j.Server.ListOnline()).Position);
    }

    // Mid walk, mid click: the route and the pending interaction go with the teleport, so the player stands on the
    // tile they were put on instead of walking back toward what they were doing.
    [Fact]
    public void Teleport_drops_the_walk_in_progress()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);
        s.SpawnPlayer(0, "acct", "A");
        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState walking));
        Assert.True(walking.IsStepping);

        var target = new TileCoord(40, 12, 0);
        s.Teleport(PlayerRef.Slot(0), new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight).PoseAt(target).Position);
        for (int i = 0; i < 12; i++) s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(target, after.Tile);
        Assert.True(after.Route.IsIdle);
        Assert.Equal(walking.Epoch + 1u, after.Epoch);
    }

    [Fact]
    public void A_teleport_onto_a_blocked_tile_is_refused_and_leaves_the_player_where_they_were()
    {
        TileCollisionMap map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var wall = new TileCoord(20, 20, 0);
        map.Or(wall.X, wall.Z, wall.Plane, TileCollisionFlags.Blocked);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, map);
        s.SpawnPlayer(0, "acct", "A");
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState before));
        var refusals = new List<(int, TileCoord, TileTeleportRefusal)>();
        s.TeleportRefused += (slot, tile, why) => refusals.Add((slot, tile, why));

        var presenter = new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight);
        s.Teleport(PlayerRef.Slot(0), presenter.PoseAt(wall).Position);
        s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(before.Tile, after.Tile);
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Equal(new[] { (0, wall, TileTeleportRefusal.Blocked) }, refusals);
    }

    [Fact]
    public void A_teleport_outside_the_loaded_world_is_refused()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);
        s.SpawnPlayer(0, "acct", "A");
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState before));
        var refusals = new List<(int, TileCoord, TileTeleportRefusal)>();
        s.TeleportRefused += (slot, tile, why) => refusals.Add((slot, tile, why));

        // Region (7, 7) is not loaded, and 1e30 metres is no tile coordinate at all.
        s.Teleport(PlayerRef.Slot(0), new Vector3(500.5f, 0f, -500.5f));
        s.Teleport(PlayerRef.Slot(0), new Vector3(1e30f, 0f, 0f));
        s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(before.Tile, after.Tile);
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Equal(new[]
        {
            (0, new TileCoord(500, 500, 0), TileTeleportRefusal.OutsideWorld),
            (0, default(TileCoord), TileTeleportRefusal.OutsideWorld),
        }, refusals);
    }

    [Fact]
    public void A_teleport_to_a_non_finite_position_is_refused_on_the_callers_thread()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);

        Assert.Throws<ArgumentException>(() => s.Teleport(PlayerRef.Slot(0), new Vector3(float.NaN, 0f, 0f)));
        Assert.Throws<ArgumentException>(() => s.Teleport(PlayerRef.Slot(0), new Vector3(0f, float.PositiveInfinity, 0f)));
    }

    // What an operator does with a console: read one player's position off the list and send another there.
    [Fact]
    public void A_position_read_off_ListOnline_teleports_onto_the_tile_it_came_from()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub, presenter: new TilePresenter(1.5f, 2.5f));
        s.SpawnPlayer(0, "acct-a", "A");
        s.SpawnPlayer(1, "acct-b", "B");
        var there = new TileCoord(37, 22, 0);
        s.SetPlayerState(1, TileMoveState.At(there, TileDirection.S), teleport: true);
        s.Tick(Dt);

        OnlinePlayer b = Assert.Single(s.ListOnline(), p => p.AccountId == "acct-b");
        s.Teleport(PlayerRef.Account("acct-a"), b.Position);
        s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState a));
        Assert.Equal(there, a.Tile);
    }

    [Fact]
    public void Through_the_interface_SetPosition_cuts_like_a_teleport_and_commitments_are_not_supported()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(hub);
        s.SpawnPlayer(0, "acct", "A");
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState before));
        IAdminControllable admin = s;

        var target = new TileCoord(14, 18, 0);
        admin.SetPosition(PlayerRef.Slot(0), new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight).PoseAt(target).Position);
        s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(target, after.Tile);
        Assert.Equal(before.Epoch + 1u, after.Epoch);
        Assert.Throws<NotSupportedException>(() =>
            admin.BeginMovementCommitment(PlayerRef.Slot(0), new MovementCommitmentRequest(Vector2.UnitX, 1f, 1f)));
    }
}
