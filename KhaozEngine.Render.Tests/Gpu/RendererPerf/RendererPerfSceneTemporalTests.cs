using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfSceneTemporalTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(192)]
    public void Counted_frames_initialize_history_without_an_extra_priming_frame(int warmup)
    {
        using var rig = new TemporalWorkload(-warmup);
        rig.Record(-warmup);
        Assert.Equal(1, rig.Work.Scene.CurrentFrameView.FrameIndex);
        Assert.True(rig.Work.Scene.TemporalActive);
        Assert.False(rig.Work.Scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.FirstFrame, rig.Work.Scene.TemporalHistory.LastReset);
        Assert.Equal(32, rig.Work.Scene.DrawnSkinnedInstances);
        Assert.Equal(0, rig.Work.Scene.CulledSkinnedInstances);
        Assert.Equal(4, rig.Work.Scene.LastShadowPassDiagnostics.CascadeCount);

        for (int index = -warmup + 1; index <= 1; index++) rig.Record(index);
        Assert.Equal(warmup + 2, rig.Work.Scene.CurrentFrameView.FrameIndex);
        Assert.True(rig.Work.Scene.TemporalHistory.IsValid);
        Assert.Equal(warmup + 2, rig.Work.Recipe.Facts["recordedFrames"].Value);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(192)]
    public void Measured_frame_zero_requires_history_from_the_counted_warmup(int warmup)
    {
        using var rig = new TemporalWorkload(-warmup);
        for (int index = -warmup; index < 0; index++) rig.Record(index);
        Assert.True(rig.Work.Scene.TemporalHistory.IsValid);
        rig.Work.Scene.CameraCut();

        var error = Assert.Throws<InvalidOperationException>(() => rig.Record(0));
        Assert.Contains("temporal history", error.Message, StringComparison.Ordinal);
        Assert.Equal(warmup + 1, rig.Work.Scene.CurrentFrameView.FrameIndex);
        Assert.False(rig.Work.Scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.CameraCutRequested, rig.Work.Scene.TemporalHistory.LastReset);
    }

    [Fact]
    public void Conditioning_does_not_accept_disabled_temporal_rendering()
    {
        using var rig = new TemporalWorkload(0);
        rig.Work.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        var error = Assert.Throws<InvalidOperationException>(() => rig.Record(0));
        Assert.Contains("temporal history", error.Message, StringComparison.Ordinal);
        Assert.False(rig.Work.Scene.TemporalActive);
    }

    [Fact]
    public void Later_conditioning_frame_does_not_accept_a_history_reset()
    {
        using var rig = new TemporalWorkload(0);
        rig.Record(0);
        rig.Work.Scene.CameraCut();
        var error = Assert.Throws<InvalidOperationException>(() => rig.Record(1));
        Assert.Contains("temporal history", error.Message, StringComparison.Ordinal);
        Assert.Equal(TemporalResetReason.CameraCutRequested, rig.Work.Scene.TemporalHistory.LastReset);
    }

    sealed class TemporalWorkload : IDisposable
    {
        readonly PostTraceDevice _device = new();
        readonly GeometryTraceCommands _commands;
        internal readonly RendererPerfSceneWorkload Work;

        internal TemporalWorkload(int firstIndex)
        {
            using var recipe = RendererPerfRecipe.Create("crowd");
            var facts = recipe.Facts;
            var identity = new RendererPerfRecipeIdentity("S3", "crowd/1", facts.Profile, facts.RecipeSha256,
                facts.SettingsSha256, new string('b', 64), facts.GeometrySha256, facts.AssetSha256);
            _commands = new GeometryTraceCommands(_device.Factory.CreateCommandList());
            var observer = new RendererPerfGeometryTraceTests.GeometryObserver(_commands);
            Work = new RendererPerfSceneWorkload(_device, observer, identity, (_, _) => { });
            Work.PrepareCold(firstIndex);
            Assert.Equal(0, Work.Scene.CurrentFrameView.FrameIndex);
        }

        internal void Record(int index)
        {
            _commands.Events.Clear();
            Work.Scene.PassObserver = new RendererPerfGeometryTraceTests.GeometryObserver(_commands);
            Work.Prepare(index);
            _commands.Begin();
            try { Work.Record(_commands, index); }
            finally { _commands.End(); }
        }

        public void Dispose()
        {
            Work.Dispose();
            _commands.Dispose();
            _device.Dispose();
        }
    }
}
