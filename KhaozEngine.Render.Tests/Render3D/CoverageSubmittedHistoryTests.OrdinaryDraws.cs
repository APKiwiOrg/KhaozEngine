using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageSubmittedHistoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shadow_only_fallback_with_no_point_row_preserves_submitted_history(bool gpu)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpu;
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        f.Begin(); f.Queue();
        f.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record(); f.Submit();
        var prior = f.History;
        f.Scene.Post.Quality.Shadows.PointShadows.MaxStaticRebuildsPerFrame = 0;
        f.Begin(); QueueOrdinaryRefusal(f, "point");
        f.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Static(99));
        f.Record();
        Assert.Equal(0, f.Scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls
            + f.Scene.LastShadowPassDiagnostics.PointStaticFallbackSkinnedDrawCalls);
        f.Submit(); Assert.Equal(prior, f.History);
        // Change only the request mode. A budgeted dynamic row really records this same shadow-only pose.
        f.Begin(); QueueOrdinaryRefusal(f, "point");
        f.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record();
        Assert.True(f.Scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.Equal(prior, f.History);
        f.Submit(); Assert.True(f.History.Discontinuity);
    }

    [Theory]
    [InlineData(false, "main")]
    [InlineData(true, "main")]
    [InlineData(false, "cascade")]
    [InlineData(true, "cascade")]
    [InlineData(false, "point")]
    [InlineData(true, "point")]
    [InlineData(false, "outline")]
    [InlineData(true, "outline")]
    public void An_actual_ordinary_draw_commits_discontinuity_at_each_pass(bool gpu, string pass)
    {
        using var f = new CoveragePublicationFixture();
        f.Scene.UseGpuSkinning = gpu;
        f.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        f.Begin(); f.Queue();
        if (pass == "point") f.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record(); f.Submit(); var prior = f.History;
        if (pass == "cascade") f.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        f.Begin(); QueueOrdinaryRefusal(f, pass);
        if (pass == "point") f.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record(); Assert.Equal(prior, f.History);
        f.Submit(); Assert.True(f.History.Discontinuity);
    }

    static void QueueOrdinaryRefusal(CoveragePublicationFixture f, string pass)
    {
        Assert.False(f.Binding.TryPrepare(f.Host.Skeleton, f.Host.Palette, Matrix4x4.CreateScale(0), new(10),
            CoverageBindingFixture.Template, out _, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        var outline = pass == "outline" ? f.Scene.BeginMeshOutline(Color.White, 2) : default;
        foreach (var mesh in f.Host.Hosts)
        {
            var draw = new SkinnedInstanceDraw(mesh, Matrix4x4.Identity) { Motion = f.Host.Actor, ShadowOnly = pass != "main" };
            if (pass == "outline") f.Scene.DrawSkinnedOutline(outline, draw, f.Host.Palette);
            else f.Scene.DrawSkinned(draw, f.Host.Palette);
        }
    }
}
