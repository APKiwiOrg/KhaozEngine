using System;
using System.Linq;
using KhaozEngine.Render3D.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfSceneWorkloadTests
{
    [Theory]
    [InlineData("empty", "S0")]
    [InlineData("outdoors", "S2")]
    public void Cold_prepare_renders_no_extra_frame_and_resets_the_integer_clock(string id, string sceneId)
    {
        using var device = new PostTraceDevice();
        using var recipe = RendererPerfRecipe.Create(id);
        var facts = recipe.Facts;
        var identity = new RendererPerfRecipeIdentity(sceneId, id + "/1", facts.Profile, facts.RecipeSha256,
            facts.SettingsSha256, new string('b', 64), facts.GeometrySha256, facts.AssetSha256);
        var trace = new GeometryTraceCommands(device.Factory.CreateCommandList());
        var observer = new RendererPerfGeometryTraceTests.GeometryObserver(trace);
        using var work = new RendererPerfSceneWorkload(device, observer, identity, (_, _) => { });
        work.PrepareCold(-64);
        Assert.Equal(0, work.Scene.CurrentFrameView.FrameIndex);
        Assert.DoesNotContain(device.Lists.SelectMany(l => l.Events), e => e.Op.StartsWith("draw", StringComparison.Ordinal));
        Assert.Empty(observer.Passes);
        int coldSubmits = device.Native.Submits;
        work.Prepare(-64);
        Assert.Equal(coldSubmits, device.Native.Submits);
        Assert.Equal(192 * RendererPerfRecipe.Dt, work.Scene.EffectTimeSeconds);
        trace.Begin();
        work.Record(trace, -64);
        trace.End();
        Assert.Equal(1, work.Scene.CurrentFrameView.FrameIndex);
        Assert.Equal(0, work.Scene.LastWaterStats.OceanStalls);
        Assert.Contains(observer.Passes, p => p.Key == new RenderPassKey("post.blit"));
        Assert.NotNull(work.Recipe);
        Assert.Equal(identity, work.Recipe.Identity);
    }
}
