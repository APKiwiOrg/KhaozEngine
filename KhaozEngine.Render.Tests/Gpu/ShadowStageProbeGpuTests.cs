using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class ShadowStageProbeGpuTests(PreparePhaseSplitFixture fixture, ITestOutputHelper log)
    : IClassFixture<PreparePhaseSplitFixture>
{
    [GpuTheory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void Off_counts_and_timings_preserve_output_and_same_frame_capture_bytes(
        bool transparent, bool temporal, bool geometry, bool hdr)
    {
        byte[][] off = fixture.Render(true, transparent, temporal, geometry, hdr);
        Assert.All(fixture.ProbeSnapshots, snapshot => Assert.Null(snapshot));
        var offOrder = fixture.ProbeDrawOrder;
        byte[][] counted = fixture.Render(true, transparent, temporal, geometry, hdr, probeMode: ShadowStageProbeMode.Counts);
        var counts = fixture.ProbeSnapshots.ToArray();
        var countedOrder = fixture.ProbeDrawOrder;
        byte[][] timed = fixture.Render(true, transparent, temporal, geometry, hdr, probeMode: ShadowStageProbeMode.Timings);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(off[i], counted[i]); Assert.Equal(off[i], timed[i]);
            Assert.Equal(offOrder[i], countedOrder[i]); Assert.Equal(offOrder[i], fixture.ProbeDrawOrder[i]);
            var c = counts[i]!.Value; var t = fixture.ProbeSnapshots[i]!.Value;
            Assert.Equal(ShadowStageProbeStatus.Valid, c.Status); Assert.Equal(ShadowStageProbeStatus.Valid, t.Status);
            Assert.Equal(ShadowStageProbeMode.Counts, c.Request.Mode); Assert.Equal(ShadowStageProbeMode.Timings, t.Request.Mode);
            Assert.Equal(i + 1, c.Request.Sequence); Assert.Equal(i + 1, t.Request.Sequence);
            Assert.Equal(c.Counters, t.Counters); Assert.Null(c.Timings);
            Assert.True(t.Timings!.Value.TimestampReads > 0); Assert.True(t.Timings.Value.ScenePrefix >= 0);
        }
        log.WriteLine(fixture.ValidationProof);
        log.WriteLine($"8 frames per mode, transparent={transparent}, temporal={temporal}, geometry={geometry}, hdr={hdr}. Output and same-frame captures agree.");
    }

    [GpuFact]
    public void Populated_rigid_and_foliage_point_shadow_payloads_prove_collection_ran()
    {
        // Warm through a complete native jitter cycle, preserving the shared fixture's phase alignment.
        fixture.Render(true, false, false, true, false, pointShadows: true);
        var off = fixture.Render(true, false, false, true, false, pointShadows: true);
        var counted = fixture.Render(true, false, false, true, false, probeMode: ShadowStageProbeMode.Counts, pointShadows: true);
        var counts = fixture.ProbeSnapshots.ToArray();
        var timed = fixture.Render(true, false, false, true, false, probeMode: ShadowStageProbeMode.Timings, pointShadows: true);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(off[i], counted[i]); Assert.Equal(counted[i], timed[i]);
            var c = counts[i]!.Value.Counters!.Value;
            Assert.True(c.PointAtlasAvailable); Assert.Equal(1, c.DynamicRequests);
            Assert.Equal(1, c.RigidIndexPopulation); Assert.Equal(1, c.FoliageSelectedProxies);
            Assert.Equal(6, c.PointFoliageFaceDrawCalls); Assert.Equal(12, c.PointFaceDrawCalls);
            Assert.Equal(c, fixture.ProbeSnapshots[i]!.Value.Counters!.Value);
            Assert.True(fixture.ProbeSnapshots[i]!.Value.Timings!.Value.PointPreparation >= 0);
            log.WriteLine($"point-frame={i + 1}, dynamic={c.DynamicRequests}, rigid={c.RigidIndexPopulation}, "
                + $"foliage={c.FoliageSelectedProxies}, faceCalls={c.PointFaceDrawCalls}, "
                + $"foliageFaceCalls={c.PointFoliageFaceDrawCalls}, clockReads={fixture.ProbeSnapshots[i]!.Value.Timings!.Value.TimestampReads}");
        }
        log.WriteLine(fixture.ValidationProof);
    }

    [GpuFact]
    public void Shared_fixture_probe_modes_match_a_fresh_scene_and_changing_atmosphere()
    {
        fixture.Render(true, true, true, false, true, probeMode: ShadowStageProbeMode.Counts);
        var aged = fixture.Render(true, false, true, true, true, probeMode: ShadowStageProbeMode.Timings,
            atmosphereBillboards: BillboardBlend.Alpha);
        using var fresh = new PreparePhaseSplitFixture();
        var isolated = fresh.Render(true, false, true, true, true, probeMode: ShadowStageProbeMode.Timings,
            atmosphereBillboards: BillboardBlend.Alpha);
        var counted = fixture.Render(true, false, true, true, true, probeMode: ShadowStageProbeMode.Counts,
            atmosphereBillboards: BillboardBlend.Alpha);
        var off = fixture.Render(true, false, true, true, true, atmosphereBillboards: BillboardBlend.Alpha);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(isolated[i], aged[i]); Assert.Equal(off[i], aged[i]); Assert.Equal(off[i], counted[i]);
        }
    }
}
