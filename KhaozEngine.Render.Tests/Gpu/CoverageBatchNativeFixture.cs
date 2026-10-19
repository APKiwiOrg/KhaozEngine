using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Real scene submission with preallocated test observations. Only the assertion boundary drains.</summary>
internal sealed class CoverageBatchNativeFixture : IDisposable
{
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    readonly IGpuCommandList commands;
    readonly GpuFrameSubmission submission;
    readonly CoverageGpuOutcome[] outcomes;
    long resolutionStart, surfaceStart;
    int resolutionCalls, surfaceCalls;
    internal readonly CoverageBatchDevice Device;
    internal readonly Scene3D Scene;
    internal readonly CoverageBatchActor[] Actors;
    internal CoverageBatchAllocation Allocation;
    internal IGpuBuffer SubmittedCurrent = null!;
    internal int OutcomeCount;
    internal int Growths, ProductionWaits, ReadbackWaits;
    internal int FrameSubmissions, OtherProductionSubmissions, AssertionCopySubmissions;

    internal CoverageBatchNativeFixture(IGpuDevice native, int actorCount)
    {
        Device = new(native);
        texture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = Device.Factory.CreateFramebuffer(null, texture);
        Scene = new(Device, target.Outputs) { FrustumCulling = false };
        Assert.Null(Scene.CoverageVerificationForTests);
        Scene.Post.Starfield = Scene.Post.Sky.Enabled = Scene.Post.Atmosphere.Enabled = false;
        Scene.Post.Outline = Scene.Post.Hdr.Enabled = Scene.Post.Bloom.Enabled = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Actors = Enumerable.Range(0, actorCount).Select(i => new CoverageBatchActor(Scene, i)).ToArray();
        outcomes = new CoverageGpuOutcome[actorCount * 3];
        commands = Device.Factory.CreateCommandList();
        submission = new(Device);
        Scene.CoverageAllocationObserverForTests = entering =>
        {
            if (entering) resolutionStart = GC.GetAllocatedBytesForCurrentThread();
            else { Allocation.Resolution = GC.GetAllocatedBytesForCurrentThread() - resolutionStart; resolutionCalls++; }
        };
        Scene.CoverageSurfaceAllocationObserverForTests = entering =>
        {
            if (entering) surfaceStart = GC.GetAllocatedBytesForCurrentThread();
            else { Allocation.Surface = GC.GetAllocatedBytesForCurrentThread() - surfaceStart; surfaceCalls++; }
        };
    }

    internal void Pose(int pose, bool refusal, bool reference = true)
    {
        for (int i = 0; i < Actors.Length; i++)
        {
            Actors[i].SetPose(pose + i % 2, refusal && i % 5 == 0);
            if (reference) Actors[i].BuildReference();
        }
    }

    internal void Record(bool reverse)
    {
        Allocation = default; resolutionCalls = surfaceCalls = ReadbackWaits = 0;
        int waits = Device.Waits, creations = Device.Creations;
        int submissions = Device.Submissions;
        Scene.Begin();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < Actors.Length; n++) Actors[reverse ? Actors.Length - n - 1 : n].Draw();
        long staged = GC.GetAllocatedBytesForCurrentThread();
        Scene.PrepareFrame(32, 24);
        long prepared = GC.GetAllocatedBytesForCurrentThread();
        Allocation.Staging = staged - before; Allocation.Prepare = prepared - staged;
        submission.Begin();
        using (GpuRecording.Open(Device, commands, "Coverage.Batch"))
        {
            Allocation.TestWaitStackCaptureEnabled = Device.CaptureWaitSites;
            before = GC.GetAllocatedBytesForCurrentThread();
            Scene.RenderInternal(commands, 32, 24, target);
            Allocation.RenderTotal = GC.GetAllocatedBytesForCurrentThread() - before;
            int readbackWaits = Device.Waits;
            before = GC.GetAllocatedBytesForCurrentThread();
            Scene.RecordCoverageReadbacks(commands, submission);
            Allocation.ReadbackRecording = GC.GetAllocatedBytesForCurrentThread() - before;
            ReadbackWaits = Device.Waits - readbackWaits;
        }
        SubmittedCurrent = Scene.CoverageGpuBatchForTests!.Current;
        foreach (var actor in Actors) actor.CaptureIdentity();
        int beforeSubmit = Device.Submissions;
        before = GC.GetAllocatedBytesForCurrentThread();
        submission.Submit(commands);
        Allocation.SubmissionTotal = GC.GetAllocatedBytesForCurrentThread() - before;
        FrameSubmissions = Device.Submissions - beforeSubmit;
        OtherProductionSubmissions = beforeSubmit - submissions;
        Assert.Equal(1, FrameSubmissions);
        Assert.Same(SubmittedCurrent, Scene.CoverageGpuBatchForTests.Previous);
        Assert.All(Actors, actor => Assert.True(actor.Frame.Preparation!.MotionPresented));
        ProductionWaits = Device.Waits - waits; Growths = Device.Creations - creations;
        Assert.Equal(1, resolutionCalls); Assert.Equal(1, surfaceCalls);
        Assert.All(Actors, actor => Assert.True(actor.Staged, actor.StageFailure.ToString()));
        Assert.All(Actors, actor => Assert.Equal(CoveragePresentation.Dispatched, Scene.GetCoverageOutcome(actor.Frame).Presentation));
        Assert.Equal(Actors.Length, Scene.DispatchedHostPreparationCount);
        Assert.Null(Scene.CoverageVerificationForTests);
    }

    internal void Complete()
    {
        // One assertion drain exposes completion on Direct3D11 too. It is outside every production interval.
        Device.WaitForIdle();
        int waits = Device.Waits, creations = Device.Creations;
        OutcomeCount = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (Scene.TryReadCoverageOutcome(out var outcome)) outcomes[OutcomeCount++] = outcome;
        Allocation.OutcomeEnumeration = GC.GetAllocatedBytesForCurrentThread() - before;
        ReadbackWaits += Device.Waits - waits;
        ProductionWaits += Device.Waits - waits; Growths += Device.Creations - creations;
    }

    internal CoverageBatchObservation[] Observe()
    {
        int submissions = Device.Submissions;
        uint[] words = CoverageSelectedReadback.Read(Device, SubmittedCurrent);
        AssertionCopySubmissions = Device.Submissions - submissions;
        return Actors.Select(actor => CoverageBatchObservation.Capture(actor, words,
            outcomes.AsSpan(0, OutcomeCount).ToArray().Single(o => o.Actor == actor.Key && o.FrameId == actor.Generation))).ToArray();
    }

    internal CoverageGpuOutcome[] CompletedOutcomes() => outcomes.AsSpan(0, OutcomeCount).ToArray();

    public void Dispose()
    {
        submission.Dispose(); commands.Dispose();
        foreach (var actor in Actors) actor.Dispose();
        Scene.Dispose(); target.Dispose(); texture.Dispose();
    }
}

internal struct CoverageBatchAllocation
{
    public long Staging, Prepare, Resolution, Surface, ReadbackRecording, OutcomeEnumeration;
    public long RenderTotal, SubmissionTotal;
    public bool TestWaitStackCaptureEnabled;
    internal readonly void AssertZero()
    {
        Assert.Equal(0, Staging); Assert.Equal(0, Prepare); Assert.Equal(0, Resolution);
        Assert.Equal(0, Surface); Assert.Equal(0, ReadbackRecording); Assert.Equal(0, OutcomeEnumeration);
        Assert.Equal(0, RenderTotal); Assert.Equal(0, SubmissionTotal);
        Assert.False(TestWaitStackCaptureEnabled);
    }
    public override readonly string ToString() => $"staging={Staging} prepare={Prepare} packing/dispatch/resolution={Resolution} surface={Surface} readback-record={ReadbackRecording} completed-enumeration={OutcomeEnumeration} render-total={RenderTotal} submission-total={SubmissionTotal}";
}
