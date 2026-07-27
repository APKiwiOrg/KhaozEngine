using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Covers <see cref="MapTileResidency"/>'s ring: Chebyshev geometry, hysteresis, the multi-focus
    /// union and its strongest-wins ring resolution, the async apply path, and the sink lifecycle a consumer
    /// hangs physics off. Mirrors the shape of <c>TerrainStreamerTests</c> and
    /// <c>TerrainAsyncStreamerTests</c>.</summary>
    public class MapTileResidencyTests
    {
        static MapResidencyConfig Sync(int load, int unload, int decor = 0, int budget = 64) =>
            new MapResidencyConfig(load, unload, budget, decor).Synchronous();

        static MapResidencyConfig Async(int load, int unload, int budget, int decor = 0) =>
            new(load, unload, budget, decor);

        static MapTileCoord[] Sorted(IEnumerable<MapTileCoord> coords) =>
            coords.OrderBy(c => c.Z).ThenBy(c => c.X).ToArray();

        [Fact]
        public void ResidentSetIsAChebyshevSquare()
        {
            // The regression test for the geometry bug. At LoadRadius 1 the resident set is the 9-tile SQUARE
            // including the diagonals, not the 5-tile plus-shape a Euclidean ring gives. The diagonals are the
            // whole point: a focus hard against its tile's corner is arbitrarily close to the diagonal
            // neighbour, so excluding it means zero guaranteed coverage rather than one tile of it.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0));

            Assert.Equal(9, residency.Resident.Count);
            foreach ((int x, int z) in ResidencyFixture.Square(1))
                Assert.Contains(new MapTileCoord(x, z), residency.Resident);
            Assert.Contains(new MapTileCoord(1, 1), residency.Resident);      // named explicitly: a Euclidean
            Assert.Contains(new MapTileCoord(-1, -1), residency.Resident);    // radius-1 ring excludes both
            Assert.All(residency.Resident, c => Assert.Equal(ChunkRing.Gameplay, residency.RingOf(c)));
        }

        [Fact]
        public void ResidentSetIsTheSameFromAnyCornerOfTheFocusTile()
        {
            // The coverage guarantee is stated for the WORST focus position, so the ring must not move as the
            // focus slides around inside its own tile.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(3));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0, 0.01f, 0.01f));
            MapTileCoord[] atCorner = Sorted(residency.Resident);

            residency.UnloadAll();
            residency.Update(ResidencyFixture.At(0, 0, 0.99f, 0.99f));

            Assert.Equal(atCorner, Sorted(residency.Resident));
        }

        [Fact]
        public void PrimeAround_FillsTheRing()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new RecordingTileSink();
            var dispatcher = new ManualTileDispatcher();
            // Async, budget 1: a plain Update would take 9 updates. PrimeAround is a loading moment, not a frame.
            using var residency = new MapTileResidency(source, Async(1, 2, budget: 1), sink, dispatcher);

            residency.PrimeAround(ResidencyFixture.At(0, 0));

            Assert.Equal(9, residency.Resident.Count);
            Assert.Equal(9, sink.Loaded.Count);
            Assert.Equal(0, dispatcher.PendingCount);
        }

        [Fact]
        public void AbsentTile_IsNeverRead()
        {
            // The decisive reason residency is not a second TerrainStreamer: in a sparse world most of the ring
            // holds no authored content and has no file. The source throws for a tile the index does not mark
            // occupied, so an implementation that read the ring blind would fail this loudly.
            using MapDocumentSource source = ResidencyFixture.Source((0, 0));
            var sink = new RecordingTileSink();
            var dispatcher = new ManualTileDispatcher();
            using var residency = new MapTileResidency(source, Async(1, 2, budget: 64), sink, dispatcher);

            residency.PrimeAround(ResidencyFixture.At(0, 0));

            Assert.Equal(1, dispatcher.Scheduled);   // 9 tiles in the ring, ONE of them occupied
            MapTileCoord only = Assert.Single(residency.Resident);
            Assert.Equal(new MapTileCoord(0, 0), only);
        }

        [Fact]
        public void OscillatingFocus_DoesNotChurn()
        {
            // Hysteresis: a focus walking back and forth across a tile boundary must not shed and re-add a
            // consumer's colliders every frame. One tile of band is 512 m of travel, which absorbs it entirely.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(3));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0));
            int afterFirst = sink.Loaded.Count;
            sink.Reset();

            for (int i = 0; i < 20; i++)
            {
                residency.Update(ResidencyFixture.At(0, 0, fx: 0.99f));   // hard against the +X edge of tile 0
                residency.Update(ResidencyFixture.At(1, 0, fx: 0.01f));   // one metre later, tile 1
            }

            Assert.Equal(9, afterFirst);
            Assert.Empty(sink.Unloaded);                                   // nothing churned
            Assert.Equal(3, sink.Loaded.Count);                            // only the new column, loaded once
            Assert.Equal(12, residency.Resident.Count);                    // the union of both rings, all kept
        }

        [Fact]
        public void TileUnloaded_FiresExactlyOncePerDeparture()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0));
            sink.Reset();

            residency.Update(ResidencyFixture.At(20, 20));   // nothing occupied out here
            residency.Update(ResidencyFixture.At(20, 20));   // and a second update must not re-fire

            Assert.Equal(9, sink.Unloaded.Count);
            Assert.Equal(9, sink.Unloaded.Distinct().Count());
            Assert.Empty(residency.Resident);
            Assert.Empty(sink.Loaded);
        }

        [Fact]
        public void RingChange_FiresRingChangedNotLoadUnload()
        {
            // A Gameplay to Decor transition keeps the tile's DATA and only changes what a consumer builds from
            // it, which is what lets a consumer shed colliders on a far tile without dropping the tile.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(load: 0, unload: 2, decor: 1), sink);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.Equal(ChunkRing.Gameplay, residency.RingOf(new MapTileCoord(0, 0)));
            Assert.Equal(ChunkRing.Decor, residency.RingOf(new MapTileCoord(1, 0)));
            sink.Reset();

            residency.Update(ResidencyFixture.At(1, 0));

            Assert.Contains((new MapTileCoord(0, 0), ChunkRing.Decor), sink.RingChanged);
            Assert.Contains((new MapTileCoord(1, 0), ChunkRing.Gameplay), sink.RingChanged);
            Assert.Empty(sink.Unloaded);
            Assert.DoesNotContain(new MapTileCoord(0, 0), sink.LoadedCoords());
            Assert.True(residency.TryGetContent(new MapTileCoord(0, 0), out MapTileContent decor));
            Assert.NotNull(decor);   // a Decor tile is fully LOADED, ring governs what a consumer builds
        }

        [Fact]
        public void MultiFocus_ResidentSetIsTheUnion()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(6));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            Vector3[] foci = { ResidencyFixture.At(0, 0), ResidencyFixture.At(5, 0) };
            residency.Update(foci);

            Assert.Equal(18, residency.Resident.Count);
            Assert.Contains(new MapTileCoord(1, 1), residency.Resident);
            Assert.Contains(new MapTileCoord(4, -1), residency.Resident);
            Assert.DoesNotContain(new MapTileCoord(2, 0), residency.Resident);   // between the two rings
        }

        [Fact]
        public void TileStaysResidentWhileAnyFocusKeepsIt()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(6));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);
            var far = new MapTileCoord(4, 0);

            residency.Update(new[] { ResidencyFixture.At(0, 0), ResidencyFixture.At(4, 0) });
            Assert.Contains(far, residency.Resident);

            residency.Update(new[] { ResidencyFixture.At(0, 0), ResidencyFixture.At(4, 0) });
            Assert.Contains(far, residency.Resident);   // still there, and nothing is reference counted

            residency.Update(ResidencyFixture.At(0, 0));   // the second focus is gone
            Assert.DoesNotContain(far, residency.Resident);
            Assert.Contains(far, sink.Unloaded);
        }

        [Fact]
        public void MultiFocus_RingIsStrongestWinsRegardlessOfFocusOrder()
        {
            // A tile that is Decor for one focus and Gameplay for another resolves to Gameplay, the numerically
            // lowest ring, which is order-independent by construction. Without that rule the answer depends on
            // which focus was enumerated last, so the tile flaps between rings and a consumer sheds and re-adds
            // its colliders every frame.
            var contested = new MapTileCoord(2, 0);
            Vector3 a = ResidencyFixture.At(0, 0);
            Vector3 b = ResidencyFixture.At(3, 0);

            ChunkRing Run(params Vector3[] foci)
            {
                using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(4));
                var sink = new RecordingTileSink();
                using var residency = new MapTileResidency(source, Sync(load: 1, unload: 3, decor: 2), sink);
                residency.Update(foci);
                return residency.RingOf(contested)!.Value;
            }

            Assert.Equal(ChunkRing.Decor, Run(a));        // from A alone the tile is two rings out
            Assert.Equal(ChunkRing.Gameplay, Run(b));     // from B alone it is adjacent
            Assert.Equal(ChunkRing.Gameplay, Run(a, b));
            Assert.Equal(ChunkRing.Gameplay, Run(b, a));  // the same answer, fed in the other order
        }

        [Fact]
        public void AsyncLoads_ApplyInNearestFirstOrder()
        {
            // Reads are unbudgeted (they run off the frame thread). APPLIES are budgeted, because that is where
            // the consumer's own per-tile work lands. The manual dispatcher completes them in reverse order, so
            // a first-completed-first-applied implementation fails here.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(1));
            var sink = new RecordingTileSink();
            var dispatcher = new ManualTileDispatcher();
            using var residency = new MapTileResidency(source, Async(1, 2, budget: 1), sink, dispatcher);
            Vector3 focus = ResidencyFixture.At(0, 0);

            residency.Update(focus);
            Assert.Equal(9, dispatcher.PendingCount);   // every read requested at once
            Assert.Empty(sink.Loaded);                  // and none applied until one completes

            dispatcher.RunReverse();
            for (int i = 0; i < 9; i++) residency.Update(focus);

            MapTileCoord[] order = sink.LoadedCoords().ToArray();
            Assert.Equal(9, order.Length);
            Assert.Equal(new MapTileCoord(0, 0), order[0]);                       // the focus tile, distance 0
            Assert.Equal(4, order.Skip(1).Take(4).Count(IsAxial));                // then the four axials
            Assert.Equal(4, order.Skip(5).Take(4).Count(c => !IsAxial(c)));       // then the four diagonals
            static bool IsAxial(MapTileCoord c) => c.X == 0 || c.Z == 0;
        }

        [Fact]
        public void CancelledLoadIsDiscarded()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(1));
            var sink = new RecordingTileSink();
            var dispatcher = new ManualTileDispatcher();
            using var residency = new MapTileResidency(source, Async(1, 2, budget: 64), sink, dispatcher);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.Equal(9, dispatcher.PendingCount);

            residency.Update(ResidencyFixture.At(20, 20));   // every tile leaves before its read landed
            dispatcher.RunAll();                             // the bodies still finish on their worker thread
            residency.Update(ResidencyFixture.At(20, 20));

            Assert.Empty(sink.Loaded);                       // and every result is dropped, not applied
            Assert.Empty(sink.Unloaded);                     // nothing was ever resident, so nothing departs
            Assert.Empty(residency.Resident);
        }

        [Fact]
        public void SyncMode_AppliesAtMostTheBudgetPerUpdate()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(1));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2, budget: 2), sink);
            Vector3 focus = ResidencyFixture.At(0, 0);

            residency.Update(focus);
            Assert.Equal(2, residency.Resident.Count);
            Assert.Equal(new MapTileCoord(0, 0), sink.Loaded[0].Coord);   // nearest first here too

            for (int i = 0; i < 4; i++) residency.Update(focus);
            Assert.Equal(9, residency.Resident.Count);
        }

        [Fact]
        public void TileSinkLifecycle_DrivesAConsumersColliders()
        {
            // The physics seam, exercised the way a game uses it: per-tile add on arrival, drop on a Decor
            // downgrade, re-add on the upgrade back, remove on departure. Nothing in the engine touches a body.
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(2));
            var sink = new ColliderConsumerSink();
            using var residency = new MapTileResidency(source, Sync(load: 0, unload: 2, decor: 1), sink);
            var home = new MapTileCoord(0, 0);
            var east = new MapTileCoord(1, 0);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.True(sink.HasBodies(home));
            Assert.False(sink.HasBodies(east));                 // loaded, but Decor: data without colliders
            Assert.Equal(1, sink.BodyCount);

            residency.Update(ResidencyFixture.At(1, 0));        // home downgrades, east upgrades
            Assert.False(sink.HasBodies(home));
            Assert.True(sink.HasBodies(east));
            Assert.Equal(1, sink.BodyCount);

            residency.Update(ResidencyFixture.At(0, 0));        // and back
            Assert.True(sink.HasBodies(home));
            Assert.False(sink.HasBodies(east));

            residency.Update(ResidencyFixture.At(20, 20));
            Assert.Equal(0, sink.BodyCount);
            Assert.Equal(sink.AddCalls, sink.RemoveCalls);      // every body added was freed exactly once
        }

        [Fact]
        public void Update_WithNoFociUnloadsEverything()
        {
            using MapDocumentSource source = ResidencyFixture.Source(ResidencyFixture.Square(1));
            var sink = new RecordingTileSink();
            using var residency = new MapTileResidency(source, Sync(1, 2), sink);

            residency.Update(ResidencyFixture.At(0, 0));
            Assert.NotEmpty(residency.Resident);

            residency.Update(ReadOnlySpan<Vector3>.Empty);   // a server whose last player logged out

            Assert.Empty(residency.Resident);
            Assert.Equal(9, sink.Unloaded.Count);
        }

        [Fact]
        public void Constructor_RejectsDegenerateConfig()
        {
            using MapDocumentSource source = ResidencyFixture.Source((0, 0));
            var sink = new RecordingTileSink();

            Assert.Throws<ArgumentNullException>(() => new MapTileResidency(null!, MapResidencyConfig.Default, sink));
            Assert.Throws<ArgumentNullException>(() => new MapTileResidency(source, MapResidencyConfig.Default, null!));
            Assert.Throws<ArgumentException>(() =>
                new MapTileResidency(source, new MapResidencyConfig(2, 2, 2), sink));       // no hysteresis band
            Assert.Throws<ArgumentException>(() =>
                new MapTileResidency(source, new MapResidencyConfig(2, 3, 0), sink));       // no budget
            Assert.Throws<ArgumentException>(() =>
                new MapTileResidency(source, new MapResidencyConfig(-1, 3, 2), sink));      // negative radius
        }
    }
}
