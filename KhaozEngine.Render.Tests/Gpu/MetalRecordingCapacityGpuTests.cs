using System;
using System.Globalization;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalRecordingCapacityGpuTests(MetalRecordingCapacityFixture fixture, ITestOutputHelper log)
    : IClassFixture<MetalRecordingCapacityFixture>
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
    public void Capacity_preserves_eight_pipelined_outputs_and_same_frame_captures(
        bool transparent, bool temporal, bool geometry, bool hdr)
    {
        Compare(transparent, temporal, geometry, hdr);
    }

    [GpuTheory]
    [InlineData(BillboardBlend.Alpha)]
    [InlineData(BillboardBlend.Additive)]
    public void Changing_view_atmosphere_and_billboard_tails_match(BillboardBlend blend)
        => Compare(false, true, true, true, atmosphere: blend);

    [GpuTheory]
    [InlineData(SceneDebugView.MotionVectors)]
    [InlineData(SceneDebugView.History)]
    [InlineData(SceneDebugView.Disocclusion)]
    [InlineData(SceneDebugView.Reactive)]
    public void Debug_resolve_tails_match(SceneDebugView view) => Compare(false, true, true, true, view);

    [GpuFact]
    public void Resize_matches() => Compare(false, true, true, true, resize: true);

    [GpuTheory]
    [InlineData(ScreenTransitionStyle.Solid)]
    [InlineData(ScreenTransitionStyle.FrozenCrossfade)]
    public void Transitions_match(ScreenTransitionStyle transition)
        => Compare(false, true, true, true, transition: transition);

    [GpuFact]
    public void Both_aged_scenes_match_fresh_scenes()
    {
        foreach (var (slots, aged) in fixture.Modes)
        {
            aged.Render(true, true, false, false, false);
            var images = aged.Render(true, false, true, true, true, atmosphereBillboards: BillboardBlend.Alpha);
            using var fresh = new PreparePhaseSplitFixture(slots);
            var isolated = fresh.Render(true, false, true, true, true, atmosphereBillboards: BillboardBlend.Alpha);
            Equal(images, isolated);
        }
    }

    [GpuTheory]
    [InlineData(3)]
    [InlineData(6)]
    public void Actual_layer_allocator_arena_uniform_and_scene_retirement_counts_match(int slots)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var sceneFixture = slots == 3 ? fixture.Control : fixture.Candidate;
        Assert.Equal(8, sceneFixture.RetireCap);
        MetalRecordingCapacityFixture.WithSlots(slots, () =>
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            using var pool = ObjCAutoreleasePool.Enter();
            CAMetalLayer layer = CAMetalLayer.New();
            Assert.False(layer.IsNull);
            using var device = (MetalGpuDevice)MetalGpuDevice.CreateForHost(
                new(layer, new(64u, 40u)), syncToVerticalBlank: true).Device;
            Assert.Equal(3, layer.MaximumDrawableCount());
            Assert.Equal(slots, device.Rings.FramesInFlight);
            Assert.Equal(slots + 1, device.Uncommitted.Bound);
            using var list = Assert.IsType<MetalCommandList>(device.Factory.CreateCommandList());
            Assert.Equal(slots, list.Arena.Depth);
            using var buffer = Assert.IsType<MetalBuffer>(device.Factory.CreateBuffer(new(257, GpuBufferUsage.UniformBuffer)));
            Assert.Equal((ulong)(512 * slots), buffer.Ring!.TotalBytes);
            Assert.Equal((nuint)(512 * slots), buffer.Handle.Length());
            Assert.NotNull(device.RecordingCapacityProof);
            log.WriteLine(device.RecordingCapacityProof);
            return true;
        });
        log.WriteLine(sceneFixture.ValidationProof);
    }

    void Compare(bool transparent, bool temporal, bool geometry, bool hdr,
        SceneDebugView view = SceneDebugView.None, bool resize = false,
        ScreenTransitionStyle? transition = null, BillboardBlend? atmosphere = null)
    {
        var control = fixture.Control.Render(true, transparent, temporal, geometry, hdr, view, resize, transition,
            atmosphere, ShadowStageProbeMode.Counts, ringWaitProbe: true);
        var candidate = fixture.Candidate.Render(true, transparent, temporal, geometry, hdr, view, resize, transition,
            atmosphere, ShadowStageProbeMode.Counts, ringWaitProbe: true);
        Equal(control, candidate);
        foreach (var (slots, mode) in fixture.Modes)
        {
            foreach (var callback in mode.WaitCallbacks)
            {
                Assert.True(callback.Prefix.Receipt.HasValue);
                Assert.True(callback.Main.Receipt.HasValue);
                Assert.Equal(slots, callback.Prefix.Receipt.Value.Depth);
                Assert.Equal(slots, callback.Main.Receipt.Value.Depth);
                Assert.NotEqual(callback.Prefix.Receipt.Value.Segment, callback.Main.Receipt.Value.Segment);
            }
            log.WriteLine(mode.ValidationProof);
        }
        log.WriteLine("Eight pipelined frames and same-frame capture RGBA bytes match at capacities 3 and 6.");
    }

    static void Equal(byte[][] expected, byte[][] actual)
    {
        Assert.Equal(8, expected.Length);
        Assert.Equal(8, actual.Length);
        for (int i = 0; i < 8; i++) Assert.Equal(expected[i], actual[i]);
    }
}

public sealed class MetalRecordingCapacityFixture : IDisposable
{
    internal PreparePhaseSplitFixture Control { get; } = new(3);
    internal PreparePhaseSplitFixture Candidate { get; } = new(6);
    internal (int Slots, PreparePhaseSplitFixture Fixture)[] Modes => [(3, Control), (6, Candidate)];
    public void Dispose() { Candidate.Dispose(); Control.Dispose(); }

    internal static T WithSlots<T>(int slots, Func<T> action)
    {
        string? originalBase = Environment.GetEnvironmentVariable(MetalFramesInFlight.EnvVarName);
        string? originalSlots = Environment.GetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(MetalFramesInFlight.EnvVarName, "3");
            Environment.SetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName, slots.ToString(CultureInfo.InvariantCulture));
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MetalFramesInFlight.EnvVarName, originalBase);
            Environment.SetEnvironmentVariable(MetalRecordingCapacityPolicy.EnvVarName, originalSlots);
        }
    }
}
