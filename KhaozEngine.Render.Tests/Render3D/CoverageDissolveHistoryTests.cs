using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageDissolveHistoryTests
{
    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0f, 1f)]
    public void Fully_dissolved_host_rejects_removed_history_and_return_seeds_current(float dissolve, float complement)
    {
        using var fixture = new CoverageSceneFixture();
        fixture.Scene.Post.Pixelated = false;
        fixture.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Draw(fixture, 1, 0, 0);
        Draw(fixture, .5f, dissolve, complement);
        Assert.NotEmpty(fixture.Scene.CoverageTransitionForTests.Rectangles);
        var returned = Draw(fixture, .25f, 0, 0);
        Assert.Equal(fixture.Scene.CoverageVerticesForTests(returned).ToArray().Select(v => v.Position), Previous(fixture));
        Assert.NotEmpty(fixture.Scene.CoverageTransitionForTests.Rectangles);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void Partially_dissolved_host_preserves_final_surface_motion(float complement)
    {
        using var fixture = new CoverageSceneFixture();
        fixture.Scene.Post.Pixelated = false;
        fixture.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Draw(fixture, 1, 0, 0);
        var partial = Draw(fixture, .5f, .5f, complement);
        var final = fixture.Scene.CoverageVerticesForTests(partial).ToArray().Select(v => v.Position).ToArray();
        Draw(fixture, .25f, .5f, complement);
        Assert.Equal(final, Previous(fixture));
        Assert.Empty(fixture.Scene.CoverageTransitionForTests.Rectangles);
    }

    [Fact]
    public void A_fully_dissolved_material_slice_cannot_commit_an_entire_presented_host()
    {
        using var fixture = new CoverageSceneFixture(seams: true);
        fixture.Scene.Post.Pixelated = false;
        fixture.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Draw(fixture, 1, 0, 0);
        Draw(fixture, .5f, 0, 0, hiddenSlice: 1);
        Assert.NotEmpty(fixture.Scene.CoverageTransitionForTests.Rectangles);
        var returned = Draw(fixture, .25f, 0, 0);
        Assert.Equal(fixture.Scene.CoverageVerticesForTests(returned).ToArray().Select(v => v.Position), Previous(fixture));
        Assert.NotEmpty(fixture.Scene.CoverageTransitionForTests.Rectangles);
    }

    static SkinnedCoverageFrame Draw(CoverageSceneFixture fixture, float weight, float dissolve, float complement, int hiddenSlice = -1)
    {
        fixture.Begin();
        var frame = fixture.Stage(weight: weight);
        for (int slice = 0; slice < fixture.Host.Length; slice++)
            fixture.Scene.DrawSkinned(fixture.HostDraw(frame, slice: slice) with
            { Dissolve = slice == hiddenSlice ? 1 : dissolve, DissolveComplement = complement }, fixture.Palette);
        fixture.SubmitEquipment();
        fixture.Render();
        Assert.Equal(CoveragePresentation.Covered, fixture.Scene.GetCoverageOutcome(frame).Presentation);
        return frame;
    }

    static Vector3[] Previous(CoverageSceneFixture fixture) => MemoryMarshal.Cast<byte, Vector3>(Assert.Single(
        fixture.Record.Uploads, upload => ReferenceEquals(upload.Buffer, fixture.Scene.CoverageMotionReserveForTests.Buffer)).Data).ToArray();
}
