using System;
using System.Numerics;
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
