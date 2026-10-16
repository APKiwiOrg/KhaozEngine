using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
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
        using var audit = new CoverageTemporalAuditCommands(h.Expected.Length);
        h.Scene.Post.Pixelated = false;
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.TemporalResolveEntryForTests = split ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused;
        Matrix4x4 bone = h.Palette[1];

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
        audit.PreviousBuffer = h.Scene.CoverageMotionReserveForTests.Buffer;
        audit.Mask = h.Scene.CoverageTransitionForTests.Texture;
        audit.ResolveFragment = h.Scene.TemporalResolveRendererForTests!.LastEntryFragmentsForTests()[split ? 1 : 0];
        Warm(1, .8f, .005f);
        Warm(2, .6f, .01f);
        var lastWarm = Submit(2, .4f, .015f);
        h.RenderUncaptured(audit);
        Vector3[] lastFinal = h.Scene.CoverageVerticesForTests(lastWarm).ToArray().Select(v => v.Position).ToArray();

        var steady = Submit(2, .35f, .02f);
        audit.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.PrepareFrame(32, 24);
        long prepared = GC.GetAllocatedBytesForCurrentThread();
        h.RenderUncaptured(audit);
        long rendered = GC.GetAllocatedBytesForCurrentThread();
        long steadyPrepare = prepared - before, steadyRender = rendered - prepared;
        Vector3[] steadyFinal = h.Scene.CoverageVerticesForTests(steady).ToArray().Select(v => v.Position).ToArray();
        Assert.NotEqual(lastFinal, steadyFinal);
        Assert.Equal(lastFinal, audit.PreviousPositions.ToArray());
        Assert.True(audit.PreviousDraws > 0);
        Assert.Equal(1, audit.MaskClears);
        Assert.Equal(0, audit.MaskFills);
        Assert.Equal(1, audit.ResolveMaskReads);
        Assert.False(audit.WrongMaskClear || audit.WrongUploadSize);
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(steady).Presentation);

        var transition = Submit(3, .5f, .025f);
        audit.Reset();
        before = GC.GetAllocatedBytesForCurrentThread();
        h.Scene.PrepareFrame(32, 24);
        prepared = GC.GetAllocatedBytesForCurrentThread();
        h.RenderUncaptured(audit);
        rendered = GC.GetAllocatedBytesForCurrentThread();
        long transitionPrepare = prepared - before, transitionRender = rendered - prepared;
        Vector3[] transitioned = h.Scene.CoverageVerticesForTests(transition).ToArray().Select(v => v.Position).ToArray();
        Assert.Equal(transitioned, audit.PreviousPositions.ToArray());
        Assert.True(audit.PreviousDraws > 0);
        Assert.Equal(1, audit.MaskClears);
        Assert.True(audit.MaskFills > 0);
        Assert.Equal(1, audit.ResolveMaskReads);
        Assert.False(audit.WrongMaskClear || audit.WrongUploadSize);

        // A following frame must read the transitioned final surface, proving the measured frame committed it.
        Submit(3, .7f, .03f);
        audit.Reset(); h.RenderUncaptured(audit);
        Assert.Equal(transitioned, audit.PreviousPositions.ToArray());
        output.WriteLine($"split={split} steady prepare/render={steadyPrepare}/{steadyRender}, transition prepare/render={transitionPrepare}/{transitionRender}");
        Assert.Equal(0, steadyPrepare);
        Assert.Equal(0, steadyRender);
        Assert.Equal(0, transitionPrepare);
        Assert.Equal(0, transitionRender);
    }
}
