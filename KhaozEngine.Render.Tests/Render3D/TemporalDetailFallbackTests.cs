using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

// #1839: an optional Fine creation failure must leave a working Display resolve.
public sealed class TemporalDetailFallbackTests
{
    public enum CreationPoint { Pair, Scratch, ResolvePipeline }
    public enum RetryPoint { Resize, DisplayRequested, ResolveStopped }

    [Theory]
    [InlineData(CreationPoint.Pair)]
    [InlineData(CreationPoint.Scratch)]
    [InlineData(CreationPoint.ResolvePipeline)]
    public void A_refusal_renders_with_display_history_and_holds_the_generation(CreationPoint point)
    {
        using var rig = FineRig();
        Assert.Equal(TemporalHistoryDetailFallback.None, rig.Scene.TemporalHistoryDetailFallback);
        var failure = new InvalidOperationException("planned fine history creation failure");
        rig.Factory.TextureCreationFailure = d => point switch
        {
            CreationPoint.Pair when IsFinePair(d) => failure,
            // Quality keeps the R8 visibility targets at the smaller internal size.
            CreationPoint.Scratch when IsDisplayTexture(d, TemporalFormats.DetailClassification) => failure,
            _ => null,
        };
        rig.Factory.PipelineCreationFailure = d =>
            point == CreationPoint.ResolvePipeline && d.Outputs.Colour.Length == TemporalDetailMath.ResolveAttachments
                ? failure : null;

        rig.Frame();
        AssertFallback(rig.Scene);
        var history = rig.Scene.TemporalHistory;
        Assert.True(history.TargetsAllocated);
        int generation = history.TargetGeneration;

        rig.Factory.TextureCreationFailure = null;
        rig.Factory.PipelineCreationFailure = null;
        for (int frame = 0; frame < 2; frame++)
        {
            rig.Frame();
            AssertFallback(rig.Scene);
            Assert.Equal(generation, history.TargetGeneration);
            Assert.True(history.IsValid);
        }
    }

    [Theory]
    [InlineData(RetryPoint.Resize)]
    [InlineData(RetryPoint.DisplayRequested)]
    [InlineData(RetryPoint.ResolveStopped)]
    public void A_retry_point_lets_fine_history_return(RetryPoint point)
    {
        using var rig = FineRig();
        rig.Factory.TextureCreationFailure = static d =>
            IsFinePair(d) ? new InvalidOperationException("planned fine pair creation failure") : null;
        rig.Frame();
        AssertFallback(rig.Scene);
        rig.Factory.TextureCreationFailure = null;

        switch (point)
        {
            case RetryPoint.Resize:
                rig.Frame(HeadlessSceneRig.Width + 8, HeadlessSceneRig.Height);
                break;
            case RetryPoint.DisplayRequested:
                rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Display;
                rig.Frame();
                Assert.Equal(TemporalHistoryDetailFallback.None, rig.Scene.TemporalHistoryDetailFallback);
                rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
                rig.Frame();
                break;
            case RetryPoint.ResolveStopped:
                rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
                rig.Frame();
                Assert.Equal(TemporalHistoryDetailFallback.NoResolve, rig.Scene.TemporalHistoryDetailFallback);
                rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                rig.Frame();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }

        Assert.Equal(TemporalHistoryDetail.Fine, rig.Scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalHistoryDetailFallback.None, rig.Scene.TemporalHistoryDetailFallback);
        Assert.NotNull(rig.Scene.TemporalHistory.DetailStore);
    }

    [Fact]
    public void A_display_history_creation_failure_still_throws_in_a_fine_frame()
    {
        using var rig = FineRig();
        var failure = new InvalidOperationException("planned display history creation failure");
        rig.Factory.TextureCreationFailure = d => IsDisplayTexture(d, TemporalFormats.HistoryColor) ? failure : null;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => rig.Frame()));
    }

    [Fact]
    public void A_fine_creation_out_of_memory_exception_still_throws()
    {
        using var rig = FineRig();
        var failure = new OutOfMemoryException("planned fine pair creation failure");
        rig.Factory.TextureCreationFailure = d => IsFinePair(d) ? failure : null;
        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => rig.Frame()));
    }

    static HeadlessSceneRig FineRig()
    {
        var rig = new HeadlessSceneRig(historyDetail: TemporalHistoryDetail.Fine);
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        return rig;
    }

    static bool IsFinePair(GpuTextureDescription d) =>
        d.Width == 2 * HeadlessSceneRig.Width && d.Height == 2 * HeadlessSceneRig.Height
        && d.Format == TemporalFormats.HistoryColor;

    static bool IsDisplayTexture(GpuTextureDescription d, GpuPixelFormat format) =>
        d.Width == HeadlessSceneRig.Width && d.Height == HeadlessSceneRig.Height && d.Format == format;

    static void AssertFallback(Scene3D scene)
    {
        Assert.Equal(TemporalHistoryDetail.Display, scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalHistoryDetailFallback.CreationFailed, scene.TemporalHistoryDetailFallback);
        Assert.Null(scene.TemporalHistory.DetailStore);
    }
}
