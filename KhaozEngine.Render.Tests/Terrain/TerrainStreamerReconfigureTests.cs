using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class TerrainStreamerReconfigureTests
{
    const float ChunkSize = 60f;
    static readonly Vector3 Home = new(30f, 0f, 30f);

    static StreamerConfig Config(int load, int unload, int loads = 100, int unloads = 100, bool async = false,
        TerrainLodConfig? lod = null, float hysteresis = 0f) =>
        new(load, unload, loads, ChunkSize, async, LodConfig: lod, LodHysteresis: hysteresis,
            MaxUnloadsPerFrame: unloads);

    static HashSet<ChunkCoord> Disk(int radius)
    {
        var result = new HashSet<ChunkCoord>();
        for (int z = -radius; z <= radius; z++)
        for (int x = -radius; x <= radius; x++)
            if (x * x + z * z <= radius * radius) result.Add(new ChunkCoord(x, z));
        return result;
    }

    [Fact]
    public void Reconfigure_expands_through_the_live_load_budget()
    {
        using var sink = new FakeChunkSink();
        using var streamer = new TerrainStreamer(Config(1, 3), sink);
        streamer.PrimeAround(Home);
        Assert.Equal(Disk(1), new HashSet<ChunkCoord>(streamer.Loaded));

        streamer.Reconfigure(Config(2, 3, loads: 1));
        Assert.Equal(Disk(1), new HashSet<ChunkCoord>(streamer.Loaded));
        while (streamer.Loaded.Count < Disk(2).Count)
        {
            int before = streamer.Loaded.Count;
            sink.ResetFrame();
            streamer.Update(Home, 1f / 60f);
            Assert.InRange(streamer.Loaded.Count - before, 0, 1);
            Assert.InRange(sink.OpsThisFrame, 0, 1);
        }
        Assert.Equal(Disk(2), new HashSet<ChunkCoord>(streamer.Loaded));
    }

    [Fact]
    public void Reconfigure_contracts_through_the_live_unload_budget()
    {
        using var sink = new FakeChunkSink();
        using var streamer = new TerrainStreamer(Config(2, 3), sink);
        streamer.PrimeAround(Home);

        streamer.Reconfigure(Config(1, 2, unloads: 1));
        while (streamer.Loaded.Count > Disk(1).Count)
        {
            int before = streamer.Loaded.Count;
            streamer.Update(Home, 1f / 60f);
            Assert.InRange(before - streamer.Loaded.Count, 0, 1);
        }
        Assert.Equal(Disk(1), new HashSet<ChunkCoord>(streamer.Loaded));
    }

    [Fact]
    public void Reconfigure_a_lod_table_rebuilds_loaded_chunks_even_when_the_tier_index_stays_zero()
    {
        var dense = new TerrainLodConfig(new TerrainLodTier(64, float.PositiveInfinity));
        var coarse = new TerrainLodConfig(new TerrainLodTier(32, float.PositiveInfinity));
        using var sink = new FakeChunkSink();
        using var streamer = new TerrainStreamer(Config(1, 3, lod: dense), sink);
        streamer.PrimeAround(Home);

        streamer.Reconfigure(Config(1, 3, lod: coarse));
        streamer.PrimeAround(Home);

        Assert.Equal(streamer.Loaded.Count, sink.Reasons.Count(x => x.reason == ChunkBuildReason.ConfigurationChange));
        Assert.Equal(streamer.Loaded.Count, streamer.BuildReasons.ConfigurationChange);
    }

    [Fact]
    public void Reconfigure_hysteresis_reevaluates_a_chunk_parked_inside_the_old_dead_zone()
    {
        var lod = new TerrainLodConfig(new TerrainLodTier(64, 80f), new TerrainLodTier(32, float.PositiveInfinity));
        using var sink = new FakeChunkSink();
        using var streamer = new TerrainStreamer(Config(3, 5, lod: lod, hysteresis: 10f), sink);
        streamer.PrimeAround(Home);
        var target = new ChunkCoord(2, 0);
        Assert.Equal(1, streamer.LodOf(target));

        var insideDeadZone = new Vector3(75f, 0f, 30f);
        streamer.Update(insideDeadZone, 1f / 60f);
        Assert.Equal(1, streamer.LodOf(target));

        streamer.Reconfigure(Config(3, 5, lod: lod, hysteresis: 0f));
        streamer.PrimeAround(insideDeadZone);
        Assert.Equal(0, streamer.LodOf(target));
    }

    [Fact]
    public void Reconfigure_discards_inflight_old_profile_builds_before_requesting_the_new_ring()
    {
        var dispatcher = new ManualBuildDispatcher();
        using var sink = new FakeAsyncChunkSink();
        using var streamer = new TerrainStreamer(Config(1, 3, async: true), sink, dispatcher);
        streamer.Update(Home, 1f / 60f);
        Assert.Equal(Disk(1).Count, dispatcher.PendingCount);

        streamer.Reconfigure(Config(2, 3, async: true));
        Assert.Empty(sink.Applies);

        streamer.PrimeAround(Home);
        Assert.Equal(Disk(2), new HashSet<ChunkCoord>(streamer.Loaded));
        Assert.Equal(Disk(2).Count, sink.Applies.Count);
    }

    [Fact]
    public void Reconfigure_before_priming_uses_the_new_profile_and_refuses_construction_only_changes()
    {
        using var sink = new FakeChunkSink();
        using var streamer = new TerrainStreamer(Config(1, 3), sink);
        StreamerConfig original = streamer.Config;

        Assert.Contains("construction-only",
            Assert.Throws<ArgumentException>(() => streamer.Reconfigure(original with { ChunkSize = 120f })).Message);
        Assert.Contains("construction-only",
            Assert.Throws<ArgumentException>(() => streamer.Reconfigure(original with { Async = true })).Message);
        Assert.Equal(original, streamer.Config);

        streamer.Reconfigure(Config(2, 3));
        streamer.PrimeAround(Home);
        Assert.Equal(Disk(2), new HashSet<ChunkCoord>(streamer.Loaded));
    }
}
