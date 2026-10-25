using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfWaterUploadTraceTests;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>The viewport-prepared water path. <c>device.water.prepare</c> is a root CPU-only owner on every such
/// frame. <c>device.water.bathymetry</c> belongs to the legacy path only.</summary>
public sealed class RendererPerfViewportWaterTraceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Revised_bathymetry_is_owned_by_water_prepare(bool culled, bool bound)
    {
        var definitions = Discover(bound, rig => rig.Frame());
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        using var recorded = new WaterRig(recorder: recorder, viewport: true, bound: bound);
        recorded.Frame(); recorded.Frame();
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorded.Scene.PassObserver = adapter;
        recorded.Field.Depths[0] = 3f;
        recorded.Field.MarkChanged();
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(culled, RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        var sample = recorder.Harvest(0, Timing(recorder));
        Assert.Empty(sample.Errors);
        Assert.DoesNotContain(definitions, p => p.Id == "device.water.bathymetry");
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.TextureUpdates));
        Assert.Equal(128, recorder.ReadCounter(0, RendererPerfCounter.TextureUploadBytes));
        AssertNoParentWork(recorder, definitions);
        int owner = Owner(definitions, "device.water.prepare");
        Assert.Equal(-1, definitions[owner - 1].Parent);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.TextureUpdates, owner));
        AssertExecutedDeviceRow(sample, "device.water.prepare");
        Assert.Equal((uint)definitions.Count(p => p.BeginMarker > 0) * 2 + 2, recorder.MarkerCount);
        var surface = Assert.Single(sample.Passes, p => p.Id == "water.surface");
        Assert.Equal(culled ? RendererPerfMetricStatus.NotExecuted : RendererPerfMetricStatus.Available, surface.Duration.Status);
        RendererPerfDeviceReportTests.AssertPublishable(sample);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_visible_frame_charges_slot_and_set_setup_to_water_prepare(bool bound)
    {
        var definitions = Discover(bound, rig => rig.Frame(planes: 5));
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        using var recorded = new WaterRig(recorder: recorder, viewport: true, bound: bound);
        // Culled single-plane frames initialize the ocean placeholder, bathymetry, a four-slot UBO and its sets.
        recorded.Frame(culled: true); recorded.Frame(culled: true);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorded.Scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        // Five visible planes grow the UBO, which retires and recreates every water set, then allocate flat geometry.
        recorded.Frame(list: RendererPerfCommandList.Wrap(recorded.Commands, recorder), planes: 5);
        recorder.EndFrame();
        Assert.Empty(recorder.Harvest(0, Timing(recorder)).Errors);
        AssertNoParentWork(recorder, definitions);
        int prepare = Owner(definitions, "device.water.prepare");
        int geometry = Owner(definitions, "device.water.geometry");
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.BufferCreations, prepare));
        Assert.Equal(bound ? 3 : 2, recorder.ReadCounter(0, RendererPerfCounter.ResourceCreations, prepare));
        Assert.True(recorder.ReadCounter(0, RendererPerfCounter.BufferCreations, geometry) > 0);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.DeviceBufferUpdates, geometry));
        Assert.Equal(recorder.ReadCounter(0, RendererPerfCounter.ResourceCreations),
            recorder.ReadCounter(0, RendererPerfCounter.ResourceCreations, prepare)
            + recorder.ReadCounter(0, RendererPerfCounter.ResourceCreations, geometry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_water_viewport_frame_executes_prepare_without_work(bool bound)
    {
        using var rig = new WaterRig(viewport: true, bound: bound);
        rig.Frame(); rig.Frame();
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame();
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        rig.Scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        rig.Frame(list: RendererPerfCommandList.Wrap(rig.Commands, recorder), water: false);
        recorder.EndFrame();
        var sample = recorder.Harvest(0, Timing(recorder));
        Assert.Empty(sample.Errors);
        Assert.DoesNotContain(definitions, p => p.Id == "device.water.bathymetry");
        int prepare = Owner(definitions, "device.water.prepare");
        foreach (RendererPerfCounter counter in Enum.GetValues<RendererPerfCounter>())
            Assert.Equal(0, recorder.ReadCounter(0, counter, prepare));
        AssertExecutedDeviceRow(sample, "device.water.prepare");
        var geometry = Assert.Single(sample.Passes, p => p.Id == "device.water.geometry");
        Assert.Equal(RendererPerfMetricStatus.NotExecuted, geometry.Duration.Status);
        Assert.Null(geometry.ElapsedTicks);
        RendererPerfDeviceReportTests.AssertPublishable(sample);
    }

    static RendererPerfPassDefinition[] Discover(bool bound, Action<WaterRig> frame)
    {
        using var discovery = new WaterRig(viewport: true, bound: bound);
        discovery.Frame(); discovery.Frame();
        var observer = new PostTraceObserver(discovery.Commands);
        discovery.Scene.PassObserver = observer;
        frame(discovery);
        return observer.Freeze();
    }

    static int Owner(RendererPerfPassDefinition[] definitions, string id)
    {
        int owner = Array.FindIndex(definitions, p => p.Id == id) + 1;
        Assert.True(owner > 0, id);
        Assert.True(definitions[owner - 1].DeviceOnly);
        return owner;
    }

    static GpuPerfTimestampResult Timing(RendererPerfRecorder recorder) => new(Enumerable.Range(0, (int)recorder.MarkerCount)
        .Select(i => (ulong)i).ToImmutableArray(), 1d, true, "synthetic marker ticks");

    static void AssertExecutedDeviceRow(RendererPerfFrameSample sample, string id)
    {
        var row = Assert.Single(sample.Passes, p => p.Id == id);
        Assert.True(row.DeviceOnly);
        Assert.Null(row.ParentId);
        Assert.Equal(RendererPerfMetricStatus.Unsupported, row.Duration.Status);
        Assert.Null(row.Duration.Value);
        Assert.Null(row.ElapsedTicks);
        Assert.Equal(0u, row.BeginMarker);
        Assert.Equal(0u, row.EndMarker);
    }
}
