using System;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Issue #374: UpdateAsync's nearest-first apply order built a fresh <c>Comparison&lt;ChunkCoord&gt;</c>
    /// closure over <c>playerPos</c>/<c>cs</c> on every call, once per frame on the streaming path. The comparison
    /// is now bound once, at construction, to an instance method reading two fields the call site restamps in
    /// place, so the delegate itself is allocated once for the streamer's lifetime.
    /// <para>Measured through <see cref="TerrainStreamer.NearestFirstForTest"/>, the exact restamp-then-hand-back
    /// sequence the real call site runs, rather than a full <c>Update</c>: <c>Update</c>/<c>UpdateAsync</c> also
    /// allocate elsewhere (the far-chunk list, the ready/taken lists sized to the variable ready set), so a
    /// whole-frame zero-allocation assertion would fail for reasons unrelated to this closure. A warm-up pass first,
    /// matching the PropRendererAllocationTests pattern (issue #393).</para></summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class TerrainStreamerAllocationTests
    {
        [Fact]
        public void NearestFirst_PerCall_AllocatesNothing()
        {
            var sink = new FakeAsyncChunkSink();
            var manual = new ManualBuildDispatcher();
            var config = new StreamerConfig(LoadRadius: 2, UnloadRadius: 4, MaxLoadsPerFrame: 2, ChunkSize: 60f);
            using var streamer = new TerrainStreamer(config, sink, manual);

            var pos = new Vector3(123.5f, 0f, -87.25f);
            const float cs = 60f;

            for (int i = 0; i < 4; i++) streamer.NearestFirstForTest(pos, cs);   // warm-up

            // Retries once before failing (see AllocAssert.NoPerCallAllocation) to ride out an unrelated gen-0
            // collision from the rest of the process, per issue #284.
            AllocAssert.NoPerCallAllocation("NearestFirst over 20 calls", () =>
            {
                for (int i = 0; i < 20; i++) streamer.NearestFirstForTest(pos, cs);
            });
        }

        [Fact]
        public void NearestFirst_ReflectsRestampedState_NearerCoordSortsFirst()
        {
            var sink = new FakeAsyncChunkSink();
            var manual = new ManualBuildDispatcher();
            var config = new StreamerConfig(LoadRadius: 2, UnloadRadius: 4, MaxLoadsPerFrame: 2, ChunkSize: 60f);
            using var streamer = new TerrainStreamer(config, sink, manual);

            // Correctness check alongside the allocation claim: the bound delegate must still read the LATEST
            // restamped playerPos/cs, not a stale snapshot from an earlier call (the risk a shared mutable-field
            // delegate introduces that a fresh-closure-per-call never had).
            Comparison<ChunkCoord> cmp = streamer.NearestFirstForTest(new Vector3(0f, 0f, 0f), 60f);
            var near = new ChunkCoord(0, 0);
            var far = new ChunkCoord(5, 5);
            Assert.True(cmp(near, far) < 0);

            // Restamp around a different player position where `far` is now the nearer of the two.
            streamer.NearestFirstForTest(new Vector3(300f, 0f, 300f), 60f);
            Assert.True(cmp(far, near) < 0);
        }
    }
}
