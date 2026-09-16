using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>Interest measured from the NEAREST footprint tile rather than the anchor (#906): an NxN body enters a
/// viewer's serve on the tick its near edge crosses the radius, a body whose whole square is outside stays out, and
/// a world of one-tile bodies is served exactly the set it was served before footprints existed. Every body is
/// anchored on its south-west tile, z counts north, and the metric is Euclidean because that is what the interest
/// grid has always used.</summary>
public class TileFootprintInterestTests
{
    const float Dt = 0.25f;

    // The viewer for every ORTHOGONAL case below. At radius 15 the tile 15 west of it is the last one in interest.
    static readonly TileCoord West = new(30, 30, 0);

    [Fact]
    public void A_two_by_two_enters_on_the_same_tick_a_one_tile_body_on_its_near_tile_does()
    {
        using var h = new Harness();
        int viewer = h.Viewer(West);
        // Anchor 16 west of the viewer, so the anchor is one tile OUTSIDE the radius and the east column of the
        // body is the last tile inside it. Before this change the body arrived one tile late.
        long body = h.Body(new TileCoord(14, 30, 0), size: 2);
        long onTheNearTile = h.Body(new TileCoord(15, 30, 0), size: 1);
        long onTheAnchor = h.Body(new TileCoord(14, 30, 0), size: 1);

        HashSet<long> served = h.Serve(viewer);

        Assert.Contains(onTheNearTile, served);
        Assert.Contains(body, served);
        Assert.DoesNotContain(onTheAnchor, served);
    }

    [Fact]
    public void An_eight_by_eight_enters_seven_tiles_before_its_anchor_does()
    {
        using var h = new Harness();
        int viewer = h.Viewer(West);
        // Anchor 22 west, east column 15 west: seven tiles of lateness at the default radius.
        long body = h.Body(new TileCoord(8, 30, 0), size: 8);
        long onTheNearTile = h.Body(new TileCoord(15, 30, 0), size: 1);

        HashSet<long> served = h.Serve(viewer);

        Assert.Contains(onTheNearTile, served);
        Assert.Contains(body, served);
    }

    [Fact]
    public void An_eight_by_eight_enters_on_the_diagonal_its_near_corner_crosses()
    {
        using var h = new Harness();
        int viewer = h.Viewer(new TileCoord(40, 40, 0));
        // The case the anchor sits FURTHEST behind the near tile: a viewer north-east of the body, so the near tile
        // is the body's north-east corner and the anchor is the square's diagonal behind it. The corner is 14.14
        // from the viewer and the anchor is 24.04, so an inflation of seven tiles rather than the diagonal's
        // 7 * sqrt(2) would leave the body out of the grid query and no post filter could put it back.
        long body = h.Body(new TileCoord(23, 23, 0), size: 8);
        long onTheNearTile = h.Body(new TileCoord(30, 30, 0), size: 1);

        HashSet<long> served = h.Serve(viewer);

        Assert.Contains(onTheNearTile, served);
        Assert.Contains(body, served);
    }

    [Fact]
    public void A_two_by_two_whose_whole_square_is_outside_the_radius_stays_out()
    {
        using var h = new Harness();
        int viewer = h.Viewer(West);
        // East column 16 west of the viewer, one tile past the radius, so nothing the body covers is in range.
        long body = h.Body(new TileCoord(13, 30, 0), size: 2);

        HashSet<long> served = h.Serve(viewer);

        Assert.DoesNotContain(body, served);
    }

    [Fact]
    public void A_world_of_one_tile_bodies_is_served_the_pre_footprint_set()
    {
        using var h = new Harness();
        int viewer = h.Viewer(West);
        Assert.True(h.Server.TryGetPlayerNetId(viewer, out long self));
        Layout l = h.OneTileLayout();

        HashSet<long> served = h.Serve(viewer);

        Assert.Equal(new HashSet<long> { self, l.PlayerOnTheEdge, l.Actor, l.GroundItem, l.ObjectState }, served);
    }

    [Fact]
    public void A_large_body_in_the_world_leaves_every_one_tile_body_served_as_it_was()
    {
        using var h = new Harness();
        int viewer = h.Viewer(West);
        Assert.True(h.Server.TryGetPlayerNetId(viewer, out long self));
        Layout l = h.OneTileLayout();
        // The inflated query now reaches 16.41 tiles, so the player 16 tiles out is INSIDE it and the post filter
        // is the only thing keeping them out of the serve. The player at exactly 15 is the other end of the same
        // rule and must survive it.
        long body = h.Body(new TileCoord(14, 30, 0), size: 2);

        HashSet<long> served = h.Serve(viewer);

        Assert.Equal(new HashSet<long> { self, l.PlayerOnTheEdge, l.Actor, l.GroundItem, l.ObjectState, body },
            served);
    }

    [Fact]
    public void A_footprint_the_overlap_margin_cannot_ghost_is_refused_at_the_spawn()
    {
        // 15 tiles of interest inside a 16 tile border band, the margin the engine shipped before 19.0.0. A 2x2
        // needs 16.41.
        using var h = new Harness(radius: 15f, margin: 16f);
        var spawn = new TileActorSpawn(30, 0, TileDirection.S) { FootprintSize = 2 };

        ArgumentOutOfRangeException error =
            Assert.Throws<ArgumentOutOfRangeException>(() => h.Server.SpawnActor(new TileCoord(20, 20, 0), spawn));

        Assert.Contains("InterestRadius", error.Message, StringComparison.Ordinal);
        Assert.Contains("OverlapMargin", error.Message, StringComparison.Ordinal);
        Assert.Contains("16.41", error.Message, StringComparison.Ordinal);
        // The refusal is the PAIR rather than the body, so one tile is still fine and a wider band admits the 2x2.
        Assert.NotEqual(0L, h.Body(new TileCoord(20, 20, 0), size: 1));
        using var wider = new Harness(radius: 15f, margin: 17f);
        Assert.NotEqual(0L, wider.Body(new TileCoord(20, 20, 0), size: 2));
    }

    [Fact]
    public void The_default_margin_admits_every_legal_footprint_at_the_default_radius()
    {
        // 15 + 7 * sqrt(2) is 24.9, so a default of 25 holds the largest legal body as a ghost and a fresh config
        // needs no sum from the game that adopts it.
        TileWorldServerConfig defaults = TileWorldServerTickTests.Config(new TileCoord(0, 0, 0));
        using var h = new Harness(radius: defaults.InterestRadius, margin: defaults.OverlapMargin);

        Assert.NotEqual(0L, h.Body(new TileCoord(20, 20, 0), size: TileMoveState.MaxFootprintSize));
        Assert.Equal(TileMoveState.MaxFootprintSize, h.Server.LargestFootprintSize);
    }

    [Fact]
    public void A_definition_the_overlap_margin_cannot_ghost_is_refused_when_it_is_added()
    {
        // The second door, and the one that matters most: a spawner fires from inside the tick, so a definition
        // admitted here would throw out of TileActorHost.TrySpawn and take the tick down for every player.
        using var h = new Harness(radius: 15f, margin: 16f);
        var definition = new TileActorDefinition
        {
            Id = "cow",
            MaxHealth = 30,
            WanderRadius = 0,
            LeashRadius = 10,
            FootprintSize = 2,
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => h.Server.Actors.Add(definition, new TileCoord(20, 20, 0)));

        Assert.Contains("OverlapMargin", error.Message, StringComparison.Ordinal);
        Assert.Empty(h.Server.Actors.Spawners);
        // Nothing to spawn, so the tick the definition would have fired on is uneventful.
        h.Server.Tick(Dt);
        Assert.Equal(0, h.Server.ActorCount);
    }

    [Fact]
    public void The_inflation_follows_the_largest_body_ever_spawned_and_never_shrinks()
    {
        using var h = new Harness();
        Assert.Equal(1, h.Server.LargestFootprintSize);
        // A refused spawn is not a body, so it must not widen the query. This one is refused for its plane, which
        // is checked after the footprint pair and before the size is recorded.
        Assert.Throws<ArgumentException>(() =>
            h.Server.SpawnActor(new TileCoord(20, 20, 99), new TileActorSpawn(30, 0, TileDirection.S)
            {
                FootprintSize = 4,
            }));
        Assert.Equal(1, h.Server.LargestFootprintSize);

        long big = h.Body(new TileCoord(20, 20, 0), size: 5);
        Assert.Equal(5, h.Server.LargestFootprintSize);
        Assert.NotEqual(0L, h.Body(new TileCoord(40, 40, 0), size: 2));
        Assert.Equal(5, h.Server.LargestFootprintSize);
        Assert.True(h.Server.DespawnActor(big));
        Assert.Equal(5, h.Server.LargestFootprintSize);
    }

    sealed record Layout(long PlayerOnTheEdge, long Actor, long GroundItem, long ObjectState);

    sealed class Harness : IDisposable
    {
        readonly InMemoryTransportHub hub = new();
        int nextSlot;

        // The margin defaults wide enough for an 8x8 at a 15 tile radius, which is 15 + 7 * sqrt(2).
        public Harness(float radius = 15f, float margin = 26f)
        {
            TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
            TileWorldServerConfig config = TileWorldServerTickTests.Config(new TileCoord(0, 0, 0)) with
            {
                InterestRadius = radius,
                OverlapMargin = margin,
            };
            Server = new TileWorldServer(hub.Server, config, TileMoveSimulatorTests.Bake(doc),
                new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        }

        public TileWorldServer Server { get; }

        public int Viewer(TileCoord tile)
        {
            int slot = nextSlot++;
            Server.SpawnPlayer(slot, $"account-{slot}", $"player-{slot}");
            Server.SetPlayerState(slot, TileMoveState.At(tile, TileDirection.S));
            return slot;
        }

        public long Body(TileCoord anchor, int size) =>
            Server.SpawnActor(anchor, new TileActorSpawn(30, 0, TileDirection.S) { FootprintSize = size });

        // One of everything the interest grid holds, half of it inside the radius of West and half outside, so the
        // asserted set pins both the members and the non-members.
        public Layout OneTileLayout()
        {
            int outOfRange = Viewer(new TileCoord(30, 46, 0));
            _ = outOfRange;
            int onTheEdge = Viewer(new TileCoord(30, 45, 0));
            Assert.True(Server.TryGetPlayerNetId(onTheEdge, out long edgeNetId));
            long actor = Body(new TileCoord(20, 20, 0), size: 1);
            _ = Body(new TileCoord(30, 50, 0), size: 1);
            long item = Server.SpawnGroundItem(new TileCoord(25, 25, 0), itemId: 4, count: 1, ttlTicks: 100);
            _ = Server.SpawnGroundItem(new TileCoord(30, 60, 0), itemId: 4, count: 1, ttlTicks: 100);
            long state = Server.SetObjectState(412, 1, new TileCoord(33, 33, 0));
            _ = Server.SetObjectState(413, 1, new TileCoord(55, 55, 0));
            return new Layout(edgeNetId, actor, item, state);
        }

        public HashSet<long> Serve(int slot)
        {
            Server.Tick(Dt);
            return Server.ServeInterest(slot);
        }

        public void Dispose() => Server.Dispose();
    }
}
