using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWaterUploadTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revised_bathymetry_has_a_leaf_even_when_surface_is_culled(bool culled)
    {
        using var discovery = new WaterRig();
        discovery.Frame(); discovery.Frame();
        var observer = new PostTraceObserver(discovery.Commands);
        discovery.Scene.PassObserver = observer;
        discovery.Frame();
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        using var recorded = new WaterRig(recorder: recorder);
        recorded.Frame(); recorded.Frame();
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorded.Scene.PassObserver = adapter;
        recorded.Field.Depths[0] = 3f;
        recorded.Field.MarkChanged();
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(culled, RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        var timing = new GpuPerfTimestampResult(Enumerable.Range(0, (int)recorder.MarkerCount)
            .Select(i => (ulong)i).ToImmutableArray(), 1d, true, "synthetic marker ticks");
        var sample = recorder.Harvest(0, timing);
        Assert.Empty(sample.Errors);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.TextureUpdates));
        Assert.Equal(128, recorder.ReadCounter(0, RendererPerfCounter.TextureUploadBytes));
        AssertNoParentWork(recorder, definitions);
        int owner = Array.FindIndex(definitions, p => p.Id == "device.water.bathymetry") + 1;
        Assert.True(owner > 0);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.TextureUpdates, owner));
        var upload = Assert.Single(sample.Passes, p => p.Id == "device.water.bathymetry");
        Assert.Equal(RendererPerfMetricStatus.Unsupported, upload.Duration.Status);
        Assert.Null(upload.Duration.Value);
        Assert.Null(upload.ElapsedTicks);
        Assert.Equal(0u, upload.BeginMarker);
        Assert.Equal(0u, upload.EndMarker);
        Assert.Equal((uint)definitions.Count(p => p.BeginMarker > 0) * 2 + 2, recorder.MarkerCount);
        var surface = Assert.Single(sample.Passes, p => p.Id == "water.surface");
        Assert.Equal(culled ? RendererPerfMetricStatus.NotExecuted : RendererPerfMetricStatus.Available, surface.Duration.Status);
        Assert.Equal(culled ? 0 : 1, recorded.Commands.Events.Count(e => e.Op == "draw-indexed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initial_water_indices_have_a_device_upload_leaf(bool focused)
    {
        using var discovery = new WaterRig(focused: focused);
        discovery.Frame(); discovery.Frame();
        var observer = new PostTraceObserver(discovery.Commands);
        discovery.Scene.PassObserver = observer;
        discovery.Frame();
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        using var recorded = new WaterRig(focused, recorder);
        // Initialize the ocean placeholder and bathymetry without allocating either visible geometry route.
        recorded.Frame(culled: true); recorded.Frame(culled: true);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorded.Scene.PassObserver = adapter;
        recorder.BeginFrame(0, 0, 0); adapter.BeginFrame();
        recorded.Frame(list: RendererPerfCommandList.Wrap(recorded.Commands, recorder));
        recorder.EndFrame();
        Assert.Empty(recorder.Harvest(0).Errors);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.DeviceBufferUpdates));
        AssertNoParentWork(recorder, definitions);
        int owner = Array.FindIndex(definitions, p => p.Id == "device.water.geometry") + 1;
        Assert.True(owner > 0);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.DeviceBufferUpdates, owner));
        var upload = Assert.Single(recorded.Commands.Events, e => e.Op == "device-buffer");
        Assert.Equal((long)upload.Y, recorder.ReadCounter(0, RendererPerfCounter.BufferUploadBytes, owner));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Null_observer_keeps_water_device_uploads_and_native_order(bool focused, bool culled)
    {
        using var observed = new WaterRig(focused);
        using var direct = new WaterRig(focused);
        observed.Scene.PassObserver = new PostTraceObserver(observed.Commands);
        observed.Frame(); direct.Frame();
        Assert.Equal(Trace(direct.Commands), Trace(observed.Commands));
        Assert.Contains(observed.Commands.Events, e => e.Op == "device-buffer" && e.Owner?.Id == "device.water.geometry");
        Assert.Contains(observed.Commands.Events, e => e.Op == "device-texture" && e.Owner?.Id == "device.water.bathymetry");
        observed.Field.Depths[0] = direct.Field.Depths[0] = 3f;
        observed.Field.MarkChanged(); direct.Field.MarkChanged();
        observed.Scene.PassObserver = new PostTraceObserver(observed.Commands);
        observed.Frame(culled); direct.Frame(culled);
        Assert.Equal(Trace(direct.Commands), Trace(observed.Commands));
        Assert.Contains(observed.Commands.Events, e => e.Op == "device-texture" && e.Owner?.Id == "device.water.bathymetry");
    }

    [Fact]
    public void Inactive_water_skips_device_owners_without_gpu_markers()
    {
        using var rig = new WaterRig();
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
        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        foreach (string id in new[] { "device.water.geometry", "device.water.bathymetry" })
        {
            var pass = Assert.Single(sample.Passes, p => p.Id == id);
            Assert.Equal(RendererPerfMetricStatus.NotExecuted, pass.Duration.Status);
            Assert.Null(pass.Duration.Value);
            Assert.Null(pass.ElapsedTicks);
            Assert.Equal(0u, pass.BeginMarker);
            Assert.Equal(0u, pass.EndMarker);
        }
    }

    static GeometryTraceCommands.Event[] Trace(GeometryTraceCommands commands) =>
        commands.Events.Select(e => e with { Owner = null, Pipeline = null }).ToArray();

    static void AssertNoParentWork(RendererPerfRecorder recorder, RendererPerfPassDefinition[] definitions)
    {
        foreach (RendererPerfCounter counter in Enum.GetValues<RendererPerfCounter>())
        {
            Assert.Equal(0, recorder.ReadCounter(0, counter, 0));
            for (int parent = 0; parent < definitions.Length; parent++)
                if (definitions.Any(p => p.Parent == parent))
                    Assert.Equal(0, recorder.ReadCounter(0, counter, parent + 1));
        }
    }

    sealed class WaterRig : IDisposable
    {
        readonly PostTraceDevice _device = new();
        readonly IGpuTexture _texture;
        readonly IGpuFramebuffer _target;
        internal readonly Scene3D Scene;
        internal readonly GeometryTraceCommands Commands;
        internal readonly WaterBathymetry Field = new(4, 0f, 0f, 8f);
        internal WaterRig(bool focused = false, RendererPerfRecorder? recorder = null)
        {
            IGpuDevice device = recorder is null ? _device : RendererPerfDevice.Wrap(_device, recorder);
            _texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 32,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            _target = device.Factory.CreateFramebuffer(null, _texture);
            Scene = new Scene3D(device, _target.Outputs);
            Scene.Camera.Frame(Vector3.Zero, new Vector3(8f, 8f, 8f));
            Scene.Post.Water.WaveSource = WaterWaveSource.Procedural;
            Scene.Post.Water.GridMode = WaterGridMode.CameraFocused;
            Scene.Post.Water.SwellAmplitude = focused ? .5f : 0f;
            Scene.Post.Water.Bathymetry = Field;
            Commands = (GeometryTraceCommands)_device.Factory.CreateCommandList();
            _device.UploadTrace = Commands;
        }
        internal void Frame(bool culled = false, IGpuCommandList? list = null, bool water = true)
        {
            Commands.Events.Clear();
            Scene.Begin();
            if (water) Scene.DrawWater(new WaterPlane(culled ? 100000f : 0f, 0f, 0f, 8f));
            Scene.PrepareFrame();
            var commands = list ?? Commands;
            commands.Begin();
            try { Scene.RenderInternal(commands, 32, 32, _target); }
            finally { commands.End(); }
        }
        public void Dispose()
        { Scene.Dispose(); Commands.Dispose(); _target.Dispose(); _texture.Dispose(); _device.Dispose(); }
    }
}
