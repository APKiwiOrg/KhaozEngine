using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfHostTests
{
    [Fact]
    public void ZeroCoveredActorMeasuresRealSharedHostWithoutQueryConsumers()
    {
        using var probe = CoveragePerfSceneProbe.Open(0, false, completeOutcomes: true);
        var sample = probe.Frame(0);
        Assert.Equal(1, probe.ParticipantsAtReadback);
        Assert.Empty(probe.TimestampPools);
        Assert.Equal(0, probe.LastSubmittedFenceCount);
        Assert.Null(sample.Gpu);
        Assert.Equal(1, sample.Submissions);
        Assert.Contains("submit-span", probe.Events);
        Assert.True(probe.Events.IndexOf("aggregate-start") < probe.Events.IndexOf("scene-begin"));
        Assert.True(probe.Events.IndexOf("commands-end") < probe.Events.IndexOf("aggregate-stop"));
        Assert.True(probe.Events.IndexOf("submit-span") < probe.Events.IndexOf("aggregate-stop"));
        Assert.True(probe.Events.IndexOf("aggregate-stop") < probe.Events.IndexOf("present-forward"));
        Assert.Equal(probe.TickAtPresentEntry - probe.TickAtStart, sample.CpuFramePreparationTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    public void LegacyAndSharedHaveIdenticalDrawAndPresentPolicy(int actors)
    {
        using var direct = CoveragePerfSceneProbe.OpenDirect(actors);
        using var shared = CoveragePerfSceneProbe.Open(actors, false);
        var a = direct.Frame(0);
        var b = shared.Frame(0);
        Assert.Equal(b.Actors, a.Actors);
        Assert.Equal(b.MainVisibleActors, a.MainVisibleActors);
        Assert.Equal(b.Passes, a.Passes);
        Assert.Equal(b.PointShadowFaces, a.PointShadowFaces);
        Assert.Equal(b.PassFingerprint, a.PassFingerprint);
        Assert.Equal(b.PoseFingerprint, a.PoseFingerprint);
        Assert.Equal(1, a.Submissions);
        Assert.Contains("submit-direct", direct.Events);
        Assert.DoesNotContain("readbacks", direct.Events);
        Assert.Empty(direct.TimestampPools);
        Assert.Equal(direct.TickAtPresentEntry - direct.TickAtStart, a.CpuFramePreparationTicks);
        Assert.Equal(1, direct.Events.Count(e => e == "present-forward"));
        Assert.Equal(1, shared.Events.Count(e => e == "present-forward"));
    }

    [Theory]
    [InlineData(false, 1010)]
    [InlineData(true, 1000)]
    public void WallSpansAreOrderedInsideTheFrameWallAndNeverEnterTheAggregate(bool legacy, long postHost)
    {
        using var probe = legacy ? CoveragePerfSceneProbe.OpenDirect(1)
            : CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true);
        var first = probe.Frame(0);
        probe.CompletePendingOutcomes();
        // These two reads stand in for the driver's ticks around Frame.
        long start = probe.Ticks;
        var sample = probe.Frame(1);
        long end = probe.Ticks;
        foreach (var recorded in new[] { first, sample })
        {
            Assert.NotNull(recorded.FrameEntryToAggregateTicks);
            Assert.NotNull(recorded.PresentToHostReturnTicks);
            Assert.NotNull(recorded.HostReturnToFrameEndTicks);
            Assert.Null(recorded.FrameWallTicks);
        }
        // The counter snapshot advances 100 ticks before the aggregate opens. Present entry to host return is the
        // aggregate-stop trace plus the 10000 tick Present forwarding. Post-host work is one 10 tick trace per
        // outcome match plus the 1000 tick call the sample initializer makes, before the final clock read.
        Assert.Equal(CoveragePerfSceneProbe.SnapshotTicks, sample.FrameEntryToAggregateTicks);
        Assert.Equal(10 + 10000, sample.PresentToHostReturnTicks);
        Assert.Equal(postHost, sample.HostReturnToFrameEndTicks);
        Assert.Equal(1, probe.InitializerCalls);
        Assert.Equal(probe.TickAtStart - start, sample.FrameEntryToAggregateTicks);
        Assert.Equal(probe.TickAtPresentEntry - probe.TickAtStart, sample.CpuFramePreparationTicks);
        Assert.Equal(probe.TickAfterPresentForward - probe.TickAtPresentEntry, sample.PresentToHostReturnTicks);
        Assert.Equal(end - probe.TickAfterPresentForward, sample.HostReturnToFrameEndTicks);
        Assert.Equal(end - start, sample.FrameEntryToAggregateTicks + sample.CpuFramePreparationTicks
            + sample.PresentToHostReturnTicks + sample.HostReturnToFrameEndTicks);
        Assert.InRange(sample.CpuFramePreparationTicks, 1, 9999);
        Assert.Equal(sample.CpuFramePreparationTicks, sample.FrameTicks);
        Assert.Equal(10 * probe.Events.Count(e => e == "outcome-match") + CoveragePerfSceneProbe.InitializerTicks,
            sample.HostReturnToFrameEndTicks);
        Assert.Equal(legacy ? 0 : 1, probe.Events.Count(e => e == "outcome-match"));
        Assert.Equal(1, probe.Events.Count(e => e == "present-forward"));
        Assert.True(probe.Events.IndexOf("aggregate-stop") < probe.Events.IndexOf("present-forward"));
    }

    [Theory]
    [InlineData("render")]
    [InlineData("outcome-match")]
    public void AFrameThatThrowsKeepsEverySpanWhoseBoundingTicksWereTaken(string failAt)
    {
        using var probe = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true);
        probe.Frame(0);
        probe.CompletePendingOutcomes();
        probe.Events.Clear();
        var expected = new InvalidOperationException("frame failure");
        probe.OnTrace = name => { if (name == failAt) throw expected; };
        var sample = new CoveragePerfFrameSample { FrameIndex = 1 };

        var error = Assert.Throws<InvalidOperationException>(() => probe.Session.Frame(1, ref sample));

        Assert.Same(expected, error);
        bool afterHostReturn = failAt == "outcome-match";
        Assert.Equal(afterHostReturn, probe.Events.Contains("present-forward"));
        Assert.Equal(CoveragePerfSceneProbe.SnapshotTicks, sample.FrameEntryToAggregateTicks);
        // Present entry to host return needs the host return tick. Frame end is never taken on a throw.
        Assert.Equal(afterHostReturn ? 10 + 10000L : (long?)null, sample.PresentToHostReturnTicks);
        Assert.Null(sample.HostReturnToFrameEndTicks);
        Assert.Null(sample.FrameWallTicks);
        Assert.Equal(0, sample.CpuFramePreparationTicks);
        Assert.Equal(1, sample.FrameIndex);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SuppressedAndAbandonedFramesNeverSubmitOrPresent(bool legacy, bool abandoned)
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true);
        using var commands = device.Factory.CreateCommandList();
        var frame = new Frame { Commands = commands };
        int prepared = 0, recorded = 0;
        Action run = () => Run(legacy, frame, abandoned, device, commands, _ =>
        {
            Assert.Equal(0, device.OpenLists); prepared++;
        }, _ =>
        {
            recorded++;
            Assert.Equal(abandoned ? 1 : 0, device.OpenLists);
            if (abandoned) throw new InvalidOperationException("abandon frame");
        });
        try
        {
            if (abandoned) Assert.Throws<InvalidOperationException>(run);
            else run();
            Assert.Equal(1, prepared);
            Assert.Equal(1, recorded);
            Assert.Equal(0, device.Submits);
            Assert.Equal(0, device.Presents);
            Assert.Equal(0, device.OpenLists);
            // A failed frame must not poison the following recording.
            Run(legacy, frame, true, device, commands, null, _ => { });
            Assert.Equal(1, device.Submits);
            Assert.Equal(1, device.Presents);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void CaptureTapUsesOneSubmissionAndRunsAfterSubmit()
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true) { CompleteSubmissions = true };
        using var commands = device.Factory.CreateCommandList();
        using var fence = device.Factory.CreateFence();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands };
        taps.Add(new Tap(c =>
        {
            Assert.Equal(1, device.OpenLists);
            device.Order.Add("tap");
            c.SignalOnSubmit(fence);
        }, _ =>
        {
            Assert.Equal(0, device.OpenLists);
            Assert.True(fence.Signaled);
            device.Order.Add("after");
        }));
        try
        {
            FramePhases.Run(frame, true, device, commands, Color.Black, null,
                _ => device.Order.Add("draw"), taps, 16);
            Assert.Equal(1, device.Submits);
            Assert.Equal(1, device.SubmittedFenceCount);
            Assert.Equal(new[] { "draw", "tap", "submit+fence", "after", "present" }, device.Order);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Fact]
    public void RetiredTapResourcesStayAliveUntilTheRegistryDrains()
    {
        using var device = new OpenListTrackingGpuDevice(completionFences: true);
        using var commands = device.Factory.CreateCommandList();
        using var taps = new FrameCaptureTaps(device, null);
        var frame = new Frame { Commands = commands };
        var resource = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(4, 4,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        var tap = new RetiringTap(resource);
        taps.Add(tap);
        try
        {
            FramePhases.Run(frame, true, device, commands, Color.Black, null, _ => { }, taps, 16);
            Assert.Equal(1, device.Submits);
            taps.Remove(tap);
            Assert.Equal(1, device.TexturesAlive);
            Assert.True(taps.HasWork);
            taps.Dispose();
            Assert.Equal(0, device.TexturesAlive);
            Assert.False(taps.HasWork);
        }
        finally { frame.DisposeSubmission(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DirectOffGpuLayoutIsRefusedBeforeFactoryOrFrame(int layout)
    {
        var manifest = CoveragePerfManifest.Parse(new CoveragePerfFakeRun().Json);
        int factories = 0;
        Assert.Throws<NotSupportedException>(() => new CoveragePerfNativeSession(manifest,
            manifest.Blocks[0] with { TimerLayout = (CoveragePerfTimerLayout)layout }, (_, _) =>
            { factories++; throw new InvalidOperationException("factory must not run"); }, new(() => 0, 1000)));
        Assert.Equal(0, factories);
    }

    static void Run(bool legacy, Frame frame, bool render, IGpuDevice device, IGpuCommandList commands,
        Action<Frame>? prepare, Action<Frame> record)
    {
        if (legacy) CoveragePerfLegacyHost.Run(frame, render, device, commands, Color.Black, prepare, record);
        else FramePhases.Run(frame, render, device, commands, Color.Black, prepare, record);
    }

    sealed class RetiringTap(IDisposable resource) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) { }
        public void AfterSubmit(FrameCaptureContext context) { }
        public void Detach(Action<IDisposable> retire) => retire(resource);
    }

    sealed class Tap(Action<FrameCaptureContext> record, Action<FrameCaptureContext> after) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) => record(context);
        public void AfterSubmit(FrameCaptureContext context) => after(context);
        public void Detach(Action<IDisposable> retire) { }
    }
}
