using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFrameBoundaryTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cold_retirement_wait_is_owned_before_all_feature_msaa_recording(bool totalOnly)
    {
        using var discovery = new PostTraceRig("msaa-blob");
        discovery.Frame(64); discovery.Frame(65);
        var observer = new PostTraceObserver(discovery.Commands);
        discovery.Scene.PassObserver = observer;
        discovery.Frame(66);
        var definitions = observer.Freeze();
        var layout = totalOnly ? RendererPerfLayout.TotalOnly : RendererPerfLayout.PerPass;
        long clock = 0;
        var recorder = new RendererPerfRecorder(1, layout, definitions, () => ++clock, 1000);
        using var recorded = new PostTraceRig("msaa-blob", recorder);
        recorded.Frame(64); recorded.Frame(65); recorded.Frame(66);
        Assert.False(recorded.Device.Capabilities.SupportsCompletionFences);
        int waits = recorded.Device.Waits;
        Assert.True(recorded.Scene.RetiredResourceCount > 0);
        var adapter = new RendererPerfPassObserver(recorder, layout, definitions);
        recorded.Scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(67, RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        var timing = new GpuPerfTimestampResult(Enumerable.Range(0, (int)recorder.MarkerCount)
            .Select(i => (ulong)i).ToImmutableArray(), 1d, true, "synthetic marker ticks");
        RendererPerfFrameSample sample = recorder.Harvest(0, timing);
        Assert.Empty(sample.Errors);
        Assert.Equal(0, recorded.Scene.RetiredResourceCount);
        Assert.Equal(waits + 1, recorded.Device.Waits);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.WaitForIdleCalls));
        if (totalOnly)
        {
            Assert.Empty(sample.Passes);
            Assert.Equal(2u, recorder.MarkerCount);
        }
        else
        {
            AssertLeafOwnership(recorder, definitions);
            int owner = Array.FindIndex(definitions, p => p.Id == "device.scene.begin") + 1;
            Assert.True(owner > 0);
            Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.WaitForIdleCalls, owner));
            var pass = Assert.Single(sample.Passes, p => p.Id == "device.scene.begin");
            Assert.True(pass.DeviceOnly);
            Assert.Equal(0u, pass.BeginMarker);
            Assert.Equal(0u, pass.EndMarker);
            Assert.Null(pass.ElapsedTicks);
            Assert.Null(pass.Duration.Value);
            Assert.Equal(RendererPerfMetricStatus.Unsupported, pass.Duration.Status);
            RendererPerfDeviceReportTests.AssertPublishable(sample);
        }
        var boundary = Assert.Single(sample.CpuPhases, p => p.Id == "device.scene.begin");
        Assert.True(boundary.EndTicks > boundary.StartTicks);
        Assert.True(boundary.EndTicks <= sample.CpuPhases.First(p => p.Id == "begin.acquire").StartTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_begin_observer_failure_survives_preparation_and_resets_next_frame(bool atEnd)
    {
        using var rig = new PostTraceRig("msaa-blob");
        rig.Frame(64); rig.Frame(65); rig.Frame(66);
        int waits = rig.Device.Waits;
        var failure = new InvalidOperationException("frame begin observer");
        rig.Scene.PassObserver = new BeginFailure(failure, atEnd);
        var error = Assert.Throws<InvalidOperationException>(() => rig.Frame(67));
        Assert.Same(failure, error.InnerException);
        Assert.Same(failure, rig.Scene.PassObserverFailure);
        Assert.Equal(waits + 1, rig.Device.Waits);
        Assert.Equal(0, rig.Device.Native.OpenLists);
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw-indexed");
        rig.Frame(68);
        Assert.Null(rig.Scene.PassObserverFailure);
    }

    internal static void AssertLeafOwnership(RendererPerfRecorder recorder, RendererPerfPassDefinition[] definitions)
    {
        foreach (RendererPerfCounter counter in Enum.GetValues<RendererPerfCounter>())
        {
            Assert.True(recorder.ReadCounter(0, counter, owner: 0) == 0, $"Unattributed {counter}");
            for (int parent = 0; parent < definitions.Length; parent++)
                if (definitions.Any(p => p.Parent == parent))
                    Assert.True(recorder.ReadCounter(0, counter, owner: parent + 1) == 0,
                        $"Exclusive {counter} on parent {definitions[parent].Id}");
        }
    }

    sealed class BeginFailure(Exception failure, bool atEnd) : IRenderPassObserver
    {
        bool _thrown;
        public void BeginPass(IGpuCommandList cl, RenderPassKey key) { }
        public void EndPass(IGpuCommandList cl, RenderPassKey key) { }
        public void SkipPass(IGpuCommandList cl, RenderPassKey key) { }
        public void BeginCpuPhase(string id) { if (!atEnd) Check(id); }
        public void EndCpuPhase(string id) { if (atEnd) Check(id); }
        void Check(string id)
        {
            if (id != "device.scene.begin" || _thrown) return;
            _thrown = true;
            throw failure;
        }
    }
}
