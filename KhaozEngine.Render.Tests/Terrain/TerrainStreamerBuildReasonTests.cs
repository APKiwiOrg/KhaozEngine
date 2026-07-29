using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>
    /// Per-reason build attribution (<see cref="StreamerBuildReasons"/>). The merge counters say how much build work
    /// happened and how much was wasted. These say WHY it was asked for, which is the only question left once a
    /// client is rebuilding chunks with its player standing still. Each test drives exactly one reason and pins that
    /// the others do not move with it.
    /// </summary>
    public class TerrainStreamerBuildReasonTests
    {
        const float ChunkSize = 60f;

        // The shipped shape: a decor ring outside the gameplay ring, and the default multi-tier LOD table.
        static StreamerConfig Rings() => new(
            LoadRadius: 3, UnloadRadius: 7, MaxLoadsPerFrame: 4, ChunkSize: ChunkSize, DecorRadius: 5);

        // No decor ring, so every loaded chunk is a gameplay chunk and a RING change is impossible by construction.
        static StreamerConfig TiersOnly() => new(
            LoadRadius: 3, UnloadRadius: 7, MaxLoadsPerFrame: 4, ChunkSize: ChunkSize);

        // One LOD tier covering every distance, so PickLod always answers 0 and a TIER change is impossible by
        // construction. The decor ring stays, so a walk can only produce ring changes.
        static StreamerConfig RingsOnly() => new(
            LoadRadius: 3, UnloadRadius: 7, MaxLoadsPerFrame: 4, ChunkSize: ChunkSize, DecorRadius: 5,
            LodConfig: new TerrainLodConfig(new TerrainLodTier(64, float.PositiveInfinity)));

        static void Pump(TerrainStreamer s, ManualBuildDispatcher d, Vector3 at, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                s.Update(at, 1f / 60f);
                d.RunAll();
            }
        }

        [Fact]
        public void Priming_attributes_every_build_to_a_fresh_load()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Rings(), sink, dispatcher);

            streamer.PrimeAround(new Vector3(30f, 0f, 30f));

            StreamerBuildReasons r = streamer.BuildReasons;
            Assert.True(r.FreshLoad > 0, "priming built nothing, so the assertions below would be vacuous");
            Assert.Equal((long)streamer.Loaded.Count, r.FreshLoad);   // one request per chunk, none re-targeted
            Assert.Equal(0L, r.TierChange);
            Assert.Equal(0L, r.RingChange);
            Assert.Equal(0L, r.Invalidate);
            Assert.Equal(0L, r.AnchorRecentre);                  // the first anchor is not a re-centre
            Assert.Equal(r.FreshLoad, r.Total);
        }

        /// <summary>The counter that makes the field report answerable: a player who is not moving must move no
        /// counter at all, so a session where they DO move names its own cause.</summary>
        [Fact]
        public void A_still_player_moves_no_counter()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Rings(), sink, dispatcher);

            var home = new Vector3(30f, 0f, 30f);
            streamer.PrimeAround(home);
            StreamerBuildReasons before = streamer.BuildReasons;

            Pump(streamer, dispatcher, home, frames: 200);

            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.Equal(before.FreshLoad, after.FreshLoad);
            Assert.Equal(before.TierChange, after.TierChange);
            Assert.Equal(before.RingChange, after.RingChange);
            Assert.Equal(before.Invalidate, after.Invalidate);
            Assert.Equal(before.AnchorRecentre, after.AnchorRecentre);
        }

        [Fact]
        public void An_explicit_invalidate_counts_only_the_loaded_chunks_it_rebuilds()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Rings(), sink, dispatcher);

            streamer.PrimeAround(new Vector3(30f, 0f, 30f));
            StreamerBuildReasons before = streamer.BuildReasons;

            streamer.Invalidate(new ChunkCoord(0, 0));
            Assert.Equal(before.Invalidate + 1, streamer.BuildReasons.Invalidate);

            // Far outside the ring: nothing loaded there, so nothing is rebuilt and nothing is counted.
            streamer.Invalidate(new ChunkCoord(500, 500));
            Assert.Equal(before.Invalidate + 1, streamer.BuildReasons.Invalidate);

            // A rect spanning two loaded chunks counts both.
            streamer.Invalidate(new RectArea(1f, 1f, ChunkSize + 1f, 1f));
            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.Equal(before.Invalidate + 3, after.Invalidate);

            // An invalidate rebuilds in place: no fresh load, no tier or ring change, and the ring never moved.
            Assert.Equal(before.FreshLoad, after.FreshLoad);
            Assert.Equal(before.TierChange, after.TierChange);
            Assert.Equal(before.RingChange, after.RingChange);
            Assert.Equal(before.AnchorRecentre, after.AnchorRecentre);
        }

        [Fact]
        public void Walking_into_the_next_chunk_counts_one_recentre_and_fresh_loads()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Rings(), sink, dispatcher);

            streamer.PrimeAround(new Vector3(30f, 0f, 30f));
            StreamerBuildReasons before = streamer.BuildReasons;

            // One chunk east, centred, comfortably past the anchor margin.
            Pump(streamer, dispatcher, new Vector3(ChunkSize + 30f, 0f, 30f), frames: 40);

            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.Equal(before.AnchorRecentre + 1, after.AnchorRecentre);
            Assert.True(after.FreshLoad > before.FreshLoad, "the new leading edge of the disk must load");
            Assert.Equal(before.Invalidate, after.Invalidate);
        }

        [Fact]
        public void A_tier_flip_counts_a_tier_change_and_no_ring_change()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(TiersOnly(), sink, dispatcher);   // no decor ring at all

            streamer.PrimeAround(new Vector3(30f, 0f, 30f));
            StreamerBuildReasons before = streamer.BuildReasons;

            // Walking east pushes the chunks behind the player across the 80 m tier boundary.
            Pump(streamer, dispatcher, new Vector3(ChunkSize + 30f, 0f, 30f), frames: 40);

            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.True(after.TierChange > before.TierChange, "chunks left behind must re-tier");
            Assert.Equal(before.RingChange, after.RingChange);   // impossible without a decor ring
            Assert.Equal(before.Invalidate, after.Invalidate);
        }

        [Fact]
        public void A_ring_flip_counts_a_ring_change_and_no_tier_change()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(RingsOnly(), sink, dispatcher);   // one LOD tier, decor ring on

            streamer.PrimeAround(new Vector3(30f, 0f, 30f));
            StreamerBuildReasons before = streamer.BuildReasons;

            // The band straddling LoadRadius swaps gameplay for decor (and back) as the disk shifts one chunk east.
            Pump(streamer, dispatcher, new Vector3(ChunkSize + 30f, 0f, 30f), frames: 40);

            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.True(after.RingChange > before.RingChange, "the gameplay/decor band must re-ring");
            Assert.Equal(before.TierChange, after.TierChange);   // impossible with a single LOD tier
            Assert.Equal(before.Invalidate, after.Invalidate);
        }

        /// <summary>The synchronous path has to attribute the same way the async one does, since a tool or editor
        /// running <c>StreamerConfig.Synchronous()</c> reads the same counters.</summary>
        [Fact]
        public void The_synchronous_path_attributes_the_same_reasons()
        {
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Rings().Synchronous(), sink);

            var home = new Vector3(30f, 0f, 30f);
            streamer.PrimeAround(home);
            StreamerBuildReasons primed = streamer.BuildReasons;
            Assert.Equal((long)streamer.Loaded.Count, primed.FreshLoad);
            Assert.Equal(0L, primed.TierChange);
            Assert.Equal(0L, primed.RingChange);

            for (int i = 0; i < 200; i++) streamer.Update(home, 1f / 60f);
            Assert.Equal(primed.Total, streamer.BuildReasons.Total);   // still: no builds at all

            for (int i = 0; i < 60; i++) streamer.Update(new Vector3(ChunkSize + 30f, 0f, 30f), 1f / 60f);
            StreamerBuildReasons after = streamer.BuildReasons;
            Assert.Equal(primed.AnchorRecentre + 1, after.AnchorRecentre);
            Assert.True(after.TierChange > primed.TierChange);
            Assert.True(after.FreshLoad > primed.FreshLoad);
        }
    }
}
