using System;
using System.Numerics;
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

        // Every frame swaps the poses and reverses the actor order. The second warm-up frame is required by the
        // double-buffered CoverageMotionHistory: its previous bank first holds committed surfaces after frame one,
        // so PrepareFrame grows the transition mask once to cover current conformers plus previous surfaces.
        WarmFrame(f, a, lifted, b, bent, palette, template);
        WarmFrame(f, b, lifted, a, bent, palette, template);
        var warmA = f.Scene.CoverageVerticesForTests(a.Frame).ToArray();
        var warmB = f.Scene.CoverageVerticesForTests(b.Frame).ToArray();

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

        // Accepted covered presentation, decided by render-time certification for both actors.
        Assert.True(a.Accepted, a.Failure.ToString());
        Assert.True(b.Accepted, b.Failure.ToString());
        Assert.Equal(CoveragePresentation.Pending, pendingA);
        Assert.Equal(CoveragePresentation.Pending, pendingB);
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(a.Frame).Presentation);
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(b.Frame).Presentation);
        Assert.Equal((2, 0), f.Scene.CoverageDiagnosticsForTests);
        Assert.Equal(1, resolutions);
        Assert.Equal(1, surfaces);

        // Changed final geometry. Each actor now matches the surface the other actor produced for the same pose
        // and model in the previous frame, and no longer matches its own previous surface.
        var finalA = f.Scene.CoverageVerticesForTests(a.Frame).ToArray();
        var finalB = f.Scene.CoverageVerticesForTests(b.Frame).ToArray();
        Assert.Equal(warmB, finalA);
        Assert.Equal(warmA, finalB);
        Assert.NotEqual(warmA, finalA);
        Assert.NotEqual(finalA, finalB);
        AssertSubmittedSurface(f, a.Frame, finalA);
        AssertSubmittedSurface(f, b.Frame, finalB);

        // Both actor identities survive the switch, with their own equipment instances in the captured passes.
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
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(first.Frame).Presentation);
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(second.Frame).Presentation);
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

    static void AssertSubmittedSurface(CoverageAttachmentFixture f, SkinnedCoverageFrame frame, ModelVertex[] final)
    {
        var topology = frame.Preparation!.Owner.Topology;
        for (int slice = 0; slice < f.Hosts.Length; slice++)
        {
            var range = topology.MaterialRanges[slice];
            var route = f.Scene.CoverageSliceForTests(frame, slice);
            Assert.Contains(f.Record.IndexedDraws, d => ReferenceEquals(d.IndexBuffer, route.Indices)
                && ReferenceEquals(d.VertexBuffer, route.Vertices) && d.VertexOffset == route.BaseVertex);
            var upload = Assert.Single(f.Record.Uploads, u => ReferenceEquals(u.Buffer, route.Vertices));
            Assert.Equal(final.AsSpan(range.FirstVertex, range.VertexCount).ToArray(),
                MemoryMarshal.Cast<byte, ModelVertex>(upload.Data).Slice(route.BaseVertex, range.VertexCount).ToArray());
        }
    }

    static void AssertPassInstances(CoverageAttachmentFixture f, SkinnedCoverageFrame frame, ulong own, ulong other)
    {
        var passes = f.Scene.CoveragePassesForTests(frame).ToArray();
        Assert.Contains(passes, p => p.EquipmentInstanceId == own);
        Assert.DoesNotContain(passes, p => p.EquipmentInstanceId == other);
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
        if (coverage && points) Assert.True(h.Scene.CoveragePassesForTests(frame).Length >= 16);
        if (coverage) Assert.Equal(fallback ? CoveragePresentation.UncoveredFallback : CoveragePresentation.Covered,
            h.Scene.GetCoverageOutcome(frame).Presentation);
        return new(afterEquipment - before, afterPreparation - afterEquipment, afterSubmission - afterPreparation,
            afterReserve - afterSubmission, resolution, surface, afterRender - afterReserve);
    }
}
