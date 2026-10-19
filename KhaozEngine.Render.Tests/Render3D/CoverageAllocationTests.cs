using System;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_staging_validation_and_submission_allocate_zero_bytes(bool fallback)
    {
        var control = Measure(fallback, false);
        var covered = Measure(fallback, true);
        output.WriteLine($"fallback={fallback} control={control} coverage={covered}");
        Assert.Equal(0, covered.Equipment);
        Assert.Equal(0, covered.Preparation);
        Assert.Equal(0, covered.Submission);
        Assert.Equal(0, covered.Reserve);
        Assert.Equal(0, covered.Resolution);
        Assert.Equal(0, covered.Surface);
        Assert.True(covered.Render - covered.Resolution - covered.Surface <= control.Render - control.Resolution - control.Surface);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_changed_poses_and_actor_switching_allocate_zero(bool gpuSkinning)
    {
        using var f = new CoverageAttachmentFixture();
        var equipment = f.Load();
        f.Scene.UseGpuSkinning = gpuSkinning;
        // Stage recording is test instrumentation. Real staging and validation still run through the adapter.
        f.Adapter.RecordStages = false;
        using var bindingA = f.Binding(91);
        using var bindingB = f.Binding(92);
        Assert.True(bindingA.TrySetEquipment([equipment], out var failure), failure.ToString());
        Assert.True(bindingB.TrySetEquipment([equipment with { InstanceId = 8 }], out failure), failure.ToString());
        var a = new Actor(bindingA, 91, equipment.InstanceId);
        var b = new Actor(bindingB, 92, 8);
        var lifted = new Pose([Matrix4x4.CreateTranslation(0, .1f, 0), Matrix4x4.CreateTranslation(0, .1f, 0)],
            Matrix4x4.CreateTranslation(4, 0, 0));
        var bent = new Pose([Matrix4x4.Identity, CoverageFixture.Pose[1]], Matrix4x4.Identity);
        // One caller-owned palette and template are reused for every actor, as an animation sampler would.
        var palette = new Matrix4x4[f.Skeleton.BoneCount];
        var template = f.Template;
        var scratchDraws = new AttachmentDraw[equipment.Set.DrawCount];

        // Swap poses and actor order, warming both GPU history generations before allocation checks.
        WarmFrame(f, a, lifted, b, bent, palette, template);
        WarmFrame(f, b, lifted, a, bent, palette, template);
        var warmA = a.Frame.Preparation!.Palette.ToArray();
        var warmB = b.Frame.Preparation!.Palette.ToArray();

        // Measured frame. A returns to the lifted pose and B to the bent pose.
        SubmitFrame(f, a, lifted, b, bent, palette, template);
        var pendingA = f.Scene.GetCoverageOutcome(a.Frame).Presentation;
        var pendingB = f.Scene.GetCoverageOutcome(b.Frame).Presentation;
        // Mutate every reused input after submission, including the shared set's own pose scratch.
        Array.Fill(palette, Matrix4x4.CreateScale(9));
        template = template with { Tint = Color.Black };
        equipment.Set.WriteDraws(f.Skeleton, palette, Matrix4x4.CreateTranslation(99, 0, 0), MotionKey.From(33),
            template, scratchDraws);
        long reserveStart = GC.GetAllocatedBytesForCurrentThread();
        f.Scene.PrepareFrame(32, 24);
        long reserve = GC.GetAllocatedBytesForCurrentThread() - reserveStart;
        long resolutionStart = 0, resolution = 0, surfaceStart = 0, surface = 0;
        int resolutions = 0, surfaces = 0;
        f.Scene.CoverageAllocationObserverForTests = entering =>
        {
            if (entering) resolutionStart = GC.GetAllocatedBytesForCurrentThread();
            else { resolution += GC.GetAllocatedBytesForCurrentThread() - resolutionStart; resolutions++; }
        };
        f.Scene.CoverageSurfaceAllocationObserverForTests = entering =>
        {
            if (entering) surfaceStart = GC.GetAllocatedBytesForCurrentThread();
            else { surface += GC.GetAllocatedBytesForCurrentThread() - surfaceStart; surfaces++; }
        };
        f.Render(prepare: false);
        f.Scene.CoverageAllocationObserverForTests = null;
        f.Scene.CoverageSurfaceAllocationObserverForTests = null;

        long submission = b.Submitted - a.Start;
        output.WriteLine($"gpuSkinning={gpuSkinning} submission={submission} " +
            $"A(prepare={a.Prepared - a.Start} submit={a.Submitted - a.Prepared}) " +
            $"B(prepare={b.Prepared - b.Start} submit={b.Submitted - b.Prepared}) " +
            $"reserve={reserve} resolution={resolution}/{resolutions} surface={surface}/{surfaces}");

        // Both actors retain complete admitted inputs for GPU dispatch.
        Assert.True(a.Accepted, a.Failure.ToString());
        Assert.True(b.Accepted, b.Failure.ToString());
        Assert.Equal(CoveragePresentation.Pending, pendingA);
        Assert.Equal(CoveragePresentation.Pending, pendingB);
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(a.Frame).Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(b.Frame).Presentation);
        Assert.Equal(2, f.Scene.DispatchedHostPreparationCount);
        Assert.Equal(0, f.Scene.UncoveredGpuDrawsForTests);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, surfaces);

        // Uploaded palettes follow the actors after the input buffers have been reused.
        var finalA = a.Frame.Preparation!.Palette.ToArray();
        var finalB = b.Frame.Preparation!.Palette.ToArray();
        Assert.Equal(warmB, finalA);
        Assert.Equal(warmA, finalB);
        Assert.NotEqual(warmA, finalA);
        Assert.NotEqual(finalA, finalB);
        AssertSubmittedSurface(f, a.Frame);
        AssertSubmittedSurface(f, b.Frame);

        // Actor identities and equipment membership survive the switch in the frozen command snapshots.
        Assert.NotSame(a.Frame.Preparation, b.Frame.Preparation);
        Assert.Equal(MotionKey.From(a.Key), a.Frame.Preparation!.Actor);
        Assert.Equal(MotionKey.From(b.Key), b.Frame.Preparation!.Actor);
        AssertPassInstances(f, a.Frame, a.Instance, b.Instance);
        AssertPassInstances(f, b.Frame, b.Instance, a.Instance);

        Assert.Equal(0, submission);
        Assert.Equal(0, reserve);
        Assert.Equal(0, resolution);
        Assert.Equal(0, surface);
    }

    static void WarmFrame(CoverageAttachmentFixture f, Actor first, in Pose firstPose, Actor second, in Pose secondPose,
        Matrix4x4[] palette, in RigidInstanceDraw template)
    {
        SubmitFrame(f, first, firstPose, second, secondPose, palette, template);
        f.Render();
        Assert.True(first.Accepted, first.Failure.ToString());
        Assert.True(second.Accepted, second.Failure.ToString());
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(first.Frame).Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(second.Frame).Presentation);
    }

    static void SubmitFrame(CoverageAttachmentFixture f, Actor first, in Pose firstPose, Actor second, in Pose secondPose,
        Matrix4x4[] palette, in RigidInstanceDraw template)
    {
        f.Scene.Begin();
        PrepareAndSubmit(f, first, firstPose, palette, template);
        PrepareAndSubmit(f, second, secondPose, palette, template);
    }

    static void PrepareAndSubmit(CoverageAttachmentFixture f, Actor actor, in Pose pose, Matrix4x4[] palette,
        in RigidInstanceDraw template)
    {
        actor.Start = GC.GetAllocatedBytesForCurrentThread();
        pose.Palette.CopyTo(palette, 0);
        actor.Accepted = actor.Binding.TryPrepare(f.Skeleton, palette, pose.Model, template, out actor.Frame, out actor.Failure);
        actor.Prepared = GC.GetAllocatedBytesForCurrentThread();
        if (actor.Accepted)
            f.Submit(actor.Frame, actor.Binding.PreparedEquipmentDraws.Span, actor.Key, pose.Model, palette);
        actor.Submitted = GC.GetAllocatedBytesForCurrentThread();
    }

    static void AssertSubmittedSurface(CoverageAttachmentFixture f, SkinnedCoverageFrame frame)
    {
        var p = frame.Preparation!;
        var batch = f.Scene.CoverageGpuBatchForTests!;
        Assert.Contains(batch.Actors.ToArray(), r => r == p.GpuReservation);
        var upload = Assert.Single(f.Record.Uploads, u => ReferenceEquals(u.Buffer, batch.FrameData));
        var words = MemoryMarshal.Cast<byte, uint>(upload.Data).Slice((int)p.GpuReservation.FrameBase + 16, p.Palette.Length * 16);
        Assert.Equal(MemoryMarshal.Cast<Matrix4x4, uint>(p.Palette).ToArray(), words.ToArray());
        Assert.DoesNotContain(f.Record.IndexedDraws, d => p.Owner.Indices.Contains(d.IndexBuffer));
    }

    static void AssertPassInstances(CoverageAttachmentFixture f, SkinnedCoverageFrame frame, ulong own, ulong other)
    {
        var p = frame.Preparation!;
        var commands = p.Commands.AsSpan(0, p.CommandCount).ToArray();
        Assert.Contains(commands, c => c.EquipmentInstanceId == own);
        Assert.DoesNotContain(commands, c => c.EquipmentInstanceId == other);
    }

    readonly record struct Pose(Matrix4x4[] Palette, Matrix4x4 Model);

    sealed class Actor(HostCoverageBinding binding, ulong key, ulong instance)
    {
        internal readonly HostCoverageBinding Binding = binding;
        internal readonly ulong Key = key, Instance = instance;
        internal SkinnedCoverageFrame Frame;
        internal CoverageFailure Failure;
        internal bool Accepted;
        internal long Start, Prepared, Submitted;
    }

    readonly record struct Allocation(long Equipment, long Preparation, long Submission, long Reserve, long Resolution, long Surface, long Render);

    static Allocation Measure(bool fallback, bool coverage)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.UseGpuSkinning = coverage || fallback;
        RigidInstanceDraw[] inputs = [new(h.RigidHandle, Matrix4x4.Identity)];
        var frozen = new RigidInstanceDraw[1];
        bool points = !fallback;
        if (points) h.EnablePointShadows();
        void WarmFrame()
        {
            h.Begin();
            if (points) h.AddPoint();
            SkinnedCoverageFrame warm = coverage ? h.Stage() : default;
            h.Scene.DrawSkinned(h.HostDraw(warm), h.Palette);
            if (!fallback)
            {
                if (coverage) h.SubmitEquipment();
                else h.Scene.Draw(inputs[0]);
            }
            h.Render();
        }
        // Shadow signatures swap two owned list pairs. Warm both before measuring a steady frame.
        WarmFrame();
        WarmFrame();
        var commands = h.CommandPoses;
        h.Begin();
        if (points) h.AddPoint();
        long resolutionStart = 0, resolution = 0;
        h.Scene.CoverageAllocationObserverForTests = entering =>
        {
            if (entering) resolutionStart = GC.GetAllocatedBytesForCurrentThread();
            else resolution = GC.GetAllocatedBytesForCurrentThread() - resolutionStart;
        };
        long surfaceStart = 0, surface = 0;
        h.Scene.CoverageSurfaceAllocationObserverForTests = entering =>
        {
            if (entering) surfaceStart = GC.GetAllocatedBytesForCurrentThread();
            else surface = GC.GetAllocatedBytesForCurrentThread() - surfaceStart;
        };
        using var list = new NullGpuCommandList();
        bool equipment = true, host = true;
        CoverageFailure equipmentFailure = default, hostFailure = default;
        SkinnedCoverageFrame frame = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (coverage) equipment = h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            inputs, frozen, out equipmentFailure);
        long afterEquipment = GC.GetAllocatedBytesForCurrentThread();
        if (coverage) host = h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out frame, out hostFailure);
        long afterPreparation = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        if (!fallback) h.Scene.Draw(coverage ? frozen[0] : inputs[0]);
        long afterSubmission = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.PrepareFrame();
        long afterReserve = GC.GetAllocatedBytesForCurrentThread();
        h.RenderUncaptured(list);
        long afterRender = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(equipment, equipmentFailure.ToString());
        Assert.True(host, hostFailure.ToString());
        if (points) Assert.Equal(1, h.Scene.PointShadowedLights);
        if (coverage && points) Assert.Equal(1, h.Scene.DispatchedHostPreparationCount);
        if (coverage) Assert.Equal(fallback ? CoveragePresentation.UncoveredFallback : CoveragePresentation.Dispatched,
            h.Scene.GetCoverageOutcome(frame).Presentation);
        return new(afterEquipment - before, afterPreparation - afterEquipment, afterSubmission - afterPreparation,
            afterReserve - afterSubmission, resolution, surface, afterRender - afterReserve);
    }
}
