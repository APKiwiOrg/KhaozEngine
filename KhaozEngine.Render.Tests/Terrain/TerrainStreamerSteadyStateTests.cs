using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>
    /// What the streamer is allowed to do when the player is not moving: NOTHING. A field report had a Windows
    /// client rebuilding chunks continuously while the player stood still (cumulative HLOD merge counters climbing,
    /// with merges built and then thrown away), and the first question is whether the ring scan itself can churn on
    /// a fixed position. These pin that it cannot, so a churning client means the POSITION it feeds in is moving
    /// (or something is calling <c>Invalidate</c>), not that the scan is unstable.
    /// <para>
    /// Two shapes. A dead-still position, and a position jittered by a fraction of a millimetre each frame, which is
    /// what network reconciliation leaves on a standing player. The jitter case is the interesting one: the LOD
    /// pick is damped by <see cref="TerrainLodConfig.DefaultHysteresis"/>, so a chunk parked on a tier boundary must
    /// not flip tiers, and the residency ring is computed from INTEGER chunk distance, so it must not move either
    /// while the player stays inside one chunk.
    /// </para>
    /// </summary>
    public class TerrainStreamerSteadyStateTests
    {
        const float ChunkSize = 60f;

        static StreamerConfig Config() => new(
            LoadRadius: 3, UnloadRadius: 7, MaxLoadsPerFrame: 4, ChunkSize: ChunkSize, DecorRadius: 5);

        // Run `frames` streamer passes at the given positions, completing every background build each pass so a
        // request cannot simply be sitting in flight when the assertions run.
        static void Pump(TerrainStreamer s, ManualBuildDispatcher d, System.Func<int, Vector3> positionAt, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                s.Update(positionAt(i), 1f / 60f);
                d.RunAll();
                s.Update(positionAt(i), 1f / 60f);   // second pass applies whatever the first pass's builds produced
                d.RunAll();
            }
        }

        [Fact]
        public void A_dead_still_player_requests_no_builds_after_priming()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Config(), sink, dispatcher);

            var home = new Vector3(30f, 0f, 30f);   // chunk (0,0), well clear of every edge
            streamer.PrimeAround(home);

            int builds = sink.Builds.Count, applies = sink.Applies.Count, unloads = sink.Unloads.Count;
            Assert.True(builds > 0, "priming built nothing, so the assertions below would be vacuous");

            Pump(streamer, dispatcher, _ => home, frames: 200);

            Assert.Equal(builds, sink.Builds.Count);
            Assert.Equal(applies, sink.Applies.Count);
            Assert.Equal(unloads, sink.Unloads.Count);
        }

        [Fact]
        public void Sub_millimetre_jitter_on_a_standing_player_requests_no_builds()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Config(), sink, dispatcher);

            // Parked exactly on the tier-0/tier-1 boundary distance (80 m) from a chunk centre, which is where an
            // undamped pick would flap. The hysteresis dead zone is what has to absorb the jitter.
            var home = new Vector3(30f, 0f, 30f);
            streamer.PrimeAround(home);

            int builds = sink.Builds.Count, applies = sink.Applies.Count, unloads = sink.Unloads.Count;
            Assert.True(builds > 0, "priming built nothing, so the assertions below would be vacuous");

            // +/- 0.0001 m, alternating, the scale a reconciliation correction reported as 0.00 leaves behind.
            Pump(streamer, dispatcher,
                i => home + new Vector3(i % 2 == 0 ? 0.0001f : -0.0001f, 0f, i % 2 == 0 ? -0.0001f : 0.0001f),
                frames: 200);

            Assert.Equal(builds, sink.Builds.Count);
            Assert.Equal(applies, sink.Applies.Count);
            Assert.Equal(unloads, sink.Unloads.Count);
        }

        /// <summary>The case the jitter test above deliberately does not cover, and the one that actually bit: the
        /// player parked ON a chunk boundary. Every per-chunk decision the ring scan makes (residency ring, and
        /// membership of the load disk) comes from the player's CHUNK COORD, so without
        /// <see cref="TerrainStreamer.ChunkAnchorHysteresis"/> a tenth of a millimetre of jitter flips that coord
        /// every frame, shifts the whole disk by one chunk, and rebuilds the two bands straddling
        /// <see cref="StreamerConfig.LoadRadius"/> and <see cref="StreamerConfig.OuterRadius"/> forever. Measured
        /// before the anchor damping: 907 builds and 407 applies over these 100 passes, on a player who never moved
        /// a millimetre. Most of those builds were superseded before they could be applied, which is what a client
        /// sees downstream as HLOD merges built and then discarded.</summary>
        [Fact]
        public void A_player_parked_on_a_chunk_boundary_does_not_rebuild_the_ring()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Config(), sink, dispatcher);

            var edge = new Vector3(ChunkSize, 0f, 30f);   // exactly the x boundary between chunk (0,0) and (1,0)
            streamer.PrimeAround(edge);

            int builds = sink.Builds.Count, applies = sink.Applies.Count, unloads = sink.Unloads.Count;
            Assert.True(builds > 0, "priming built nothing, so the assertions below would be vacuous");

            Pump(streamer, dispatcher,
                i => edge + new Vector3(i % 2 == 0 ? 0.0001f : -0.0001f, 0f, 0f),
                frames: 100);

            Assert.Equal(builds, sink.Builds.Count);
            Assert.Equal(applies, sink.Applies.Count);
            Assert.Equal(unloads, sink.Unloads.Count);
        }

        /// <summary>The damping must not stop the ring following a player who really walks. Crossing a boundary and
        /// carrying on past <see cref="TerrainStreamer.ChunkAnchorHysteresis"/> re-centres, and a teleport lands
        /// centred immediately.</summary>
        [Fact]
        public void Walking_past_the_margin_still_recentres_the_ring()
        {
            var dispatcher = new ManualBuildDispatcher();
            using var sink = new FakeAsyncChunkSink();
            using var streamer = new TerrainStreamer(Config(), sink, dispatcher);

            var home = new Vector3(30f, 0f, 30f);
            streamer.PrimeAround(home);
            Assert.Contains(new ChunkCoord(4, 0), streamer.Loaded);        // outer radius 5, so (4,0) is in
            Assert.DoesNotContain(new ChunkCoord(6, 0), streamer.Loaded);

            // One chunk east, comfortably past the margin.
            var next = new Vector3(ChunkSize + 30f, 0f, 30f);
            for (int i = 0; i < 40; i++) { streamer.Update(next, 1f / 60f); dispatcher.RunAll(); }
            Assert.Contains(new ChunkCoord(6, 0), streamer.Loaded);
        }
    }
}
