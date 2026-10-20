using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageTemporalAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_temporal_prepare_through_successful_encoding_allocates_zero(bool split)
    {
        using var h = new CoverageSceneFixture();
        using var audit = new NullGpuCommandList();
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        Matrix4x4 bone = h.Palette[1];
        int motionDraws = 0;
        CoveragePulledDraw motion = default;
        h.Scene.CoveragePullObserverForTests = (pass, draw) =>
        {
            if (pass == "motion") { motionDraws++; motion = draw; }
        };

        SkinnedCoverageFrame Submit(ulong instance, float weight, float angle)
        {
            h.Palette[1] = bone * Matrix4x4.CreateRotationZ(angle);
            h.Begin();
            var frame = h.Stage(instance: instance, weight: weight);
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            return frame;
        }
        void Warm(ulong instance, float weight, float angle)
        {
            Submit(instance, weight, angle);
            h.RenderUncaptured(audit);
        }
        // Fixed setup warms both history generations, history target pairs, mask fill and changed-pose paths.
        Warm(1, 1, 0);
        Warm(1, .8f, .005f);
        Warm(2, .6f, .01f);
        var lastWarm = Submit(2, .4f, .015f);
        h.RenderUncaptured(audit);
        Matrix4x4[] lastPalette = lastWarm.Preparation!.Palette.ToArray();

        var steady = Submit(2, .35f, .02f);

        using var steadyRegion = AllocationRegion.Start($"warm temporal steady frame split={split}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.PrepareFrame(32, 24);
        long prepared = GC.GetAllocatedBytesForCurrentThread();
        h.RenderUncaptured(audit);
        long rendered = GC.GetAllocatedBytesForCurrentThread();
        steadyRegion.Dispose();
        long steadyPrepare = prepared - before, steadyRender = rendered - prepared;
        Matrix4x4[] steadyPalette = steady.Preparation!.Palette.ToArray();
        Assert.NotEqual(lastPalette, steadyPalette);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(steady).Presentation);

        var transition = Submit(3, .5f, .025f);

        using var transitionRegion = AllocationRegion.Start($"warm temporal transition frame split={split}");
        before = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.PrepareFrame(32, 24);
        prepared = GC.GetAllocatedBytesForCurrentThread();
        h.RenderUncaptured(audit);
        rendered = GC.GetAllocatedBytesForCurrentThread();
        transitionRegion.Dispose();
        long transitionPrepare = prepared - before, transitionRender = rendered - prepared;
        Assert.True(motionDraws > 0);
        Assert.Equal(transition.Preparation!.Actor, motion.Actor);
        Assert.Equal(transition.Preparation.GpuReservation.CurrentBase, motion.Surface.CurrentBase);
        // Submitted commits the encoded current generation as the next frame's previous generation.
        Assert.Same(h.Scene.CoverageGpuBatchForTests!.Previous, motion.Surface.Current);
        Assert.Same(h.Scene.CoverageGpuBatchForTests.Current, motion.Surface.Previous);

        Assert.True(transition.Preparation!.MotionEligible);
        // Native history tests separately prove the selected position words.
        Submit(3, .7f, .03f);
        h.RenderUncaptured(audit);
        output.WriteLine($"split={split} steady prepare/render={steadyPrepare}/{steadyRender}, transition prepare/render={transitionPrepare}/{transitionRender}");
        Assert.Equal(0, steadyPrepare);
        Assert.Equal(0, steadyRender);
        Assert.Equal(0, transitionPrepare);
        Assert.Equal(0, transitionRender);
    }
}
