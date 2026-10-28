using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfGeometryTraceTests;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfWaterUploadTraceTests;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Water column light under an observer that refuses a repeated CPU phase. The viewport form prepares the
/// column colour source inside its one <c>device.water.prepare</c> root. The no-argument form never opens it.</summary>
public sealed class RendererPerfWaterColumnTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Viewport_frame_opens_water_prepare_once(bool bound)
    {
        using var rig = new WaterRig(viewport: true, bound: bound);
        rig.Scene.Post.Water.WaterColumnLight = true;
        rig.Frame();
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame();
        Assert.Equal(1, observer.Cpu.Count(id => id == "device.water.prepare"));
        Assert.True(rig.Scene.WaterColumnLightDiagnostics.CopyRan);
    }

    [Fact]
    public void No_argument_frame_never_opens_water_prepare()
    {
        using var rig = new WaterRig();
        rig.Scene.Post.Water.WaterColumnLight = true;
        // The first frame creates the colour source at render, the second reuses it.
        for (int frame = 0; frame < 2; frame++)
        {
            var observer = new GeometryObserver(rig.Commands);
            rig.Scene.PassObserver = observer;
            rig.Frame();
            Assert.Contains("cpu.water.prepare", observer.Cpu);
            Assert.DoesNotContain("device.water.prepare", observer.Cpu);
            Assert.True(rig.Scene.WaterColumnLightDiagnostics.CopyRan);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opting_in_charges_colour_source_creation_to_water_prepare(bool bound)
    {
        var definitions = Discover(bound);
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        using var recorded = new WaterRig(recorder: recorder, viewport: true, bound: bound);
        recorded.Frame(); recorded.Frame();
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorded.Scene.PassObserver = adapter;
        recorded.Scene.Post.Water.WaterColumnLight = true;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(list: RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        var sample = recorder.Harvest(0, new GpuPerfTimestampResult(Enumerable.Range(0, (int)recorder.MarkerCount)
            .Select(i => (ulong)i).ToImmutableArray(), 1d, true, "synthetic marker ticks"));
        Assert.Empty(sample.Errors);
        // Only the record-time pre-water copy runs outside a pass. Every device-side creation and update has an owner.
        foreach (RendererPerfCounter counter in Enum.GetValues<RendererPerfCounter>())
        {
            if (counter is not (RendererPerfCounter.TextureSubresourceCopies or RendererPerfCounter.CopyBytes))
                Assert.Equal(0, recorder.ReadCounter(0, counter, 0));
            for (int parent = 0; parent < definitions.Length; parent++)
                if (definitions.Any(p => p.Parent == parent))
                    Assert.Equal(0, recorder.ReadCounter(0, counter, parent + 1));
        }
        int prepare = Array.FindIndex(definitions, p => p.Id == "device.water.prepare") + 1;
        Assert.True(prepare > 0);
        Assert.Equal(-1, definitions[prepare - 1].Parent);
        // The placeholder and the single-sample copy destination, both created before the water sets bind them.
        Assert.Equal(2, recorder.ReadCounter(0, RendererPerfCounter.TextureCreations, prepare));
        Assert.Equal(recorder.ReadCounter(0, RendererPerfCounter.TextureCreations),
            recorder.ReadCounter(0, RendererPerfCounter.TextureCreations, prepare));
    }

    static RendererPerfPassDefinition[] Discover(bool bound)
    {
        using var discovery = new WaterRig(viewport: true, bound: bound);
        discovery.Frame(); discovery.Frame();
        var observer = new PostTraceObserver(discovery.Commands);
        discovery.Scene.PassObserver = observer;
        discovery.Scene.Post.Water.WaterColumnLight = true;
        discovery.Frame();
        return observer.Freeze();
    }
}
