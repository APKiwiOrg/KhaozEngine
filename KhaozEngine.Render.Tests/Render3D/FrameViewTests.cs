using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="FrameView"/> carries every matrix a frame reads twice: as the camera produced it, and jittered for the
/// rasteriser. A zero jitter keeps the two bit-identical, and a nonzero jitter moves every projected point by exactly
/// the jitter in pixels whichever kind of projection the camera uses.
/// </summary>
public sealed class FrameViewTests
{
    const int W = 1600, H = 900;
    static readonly Vector3 Origin = new(1024f, 0f, 1024f);

    static IIsoCamera3D Camera(bool perspective) => perspective
        ? new FollowCamera3D
        {
            Target = new Vector3(1000f, 1f, 1000f), Yaw = 0.7f, Pitch = 0.5f, Distance = 9f,
            AspectRatio = (float)W / H, RenderOrigin = Origin,
        }
        : new IsoCamera3D { Target = new Vector3(1000f, 0f, 1000f), AspectRatio = (float)W / H, RenderOrigin = Origin };

    static FrameView Snapshot(IIsoCamera3D camera, Vector2 jitter) => new(camera.View, camera.Projection,
        camera.ViewProjection, ((IRenderOriginAware)camera).AbsoluteViewProjection, Origin, W, H, 7, jitter);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AZeroJitterLeavesEveryJitteredMatrixBitIdentical(bool perspective)
    {
        FrameView view = Snapshot(Camera(perspective), Vector2.Zero);
        TemporalAssert.BitIdentical(view.ViewProjection, view.JitteredViewProjection, "the jittered view-projection");
        TemporalAssert.BitIdentical(view.Projection, view.JitteredProjection, "the jittered projection");
        Assert.Equal(Vector2.Zero, view.JitterPixels);
        Assert.Equal(Vector2.Zero, view.JitterClip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheJitteredViewProjectionMovesEveryPointByTheJitterInPixels(bool perspective)
    {
        var jitter = new Vector2(0.375f, -0.25f);
        FrameView view = Snapshot(Camera(perspective), jitter);
        foreach (Vector3 world in new[]
        {
            new Vector3(1000f, 0f, 1000f), new Vector3(1002.5f, 1.25f, 998f), new Vector3(997f, 0.5f, 1003.5f),
        })
        {
            Vector3 local = world - view.RenderOrigin;
            Vector2 plain = TemporalAssert.Pixel(local, view.ViewProjection, W, H);
            Vector2 moved = TemporalAssert.Pixel(local, view.JitteredViewProjection, W, H);
            Assert.Equal(jitter.X, moved.X - plain.X, 2e-3f);   // +x moves right
            Assert.Equal(jitter.Y, moved.Y - plain.Y, 2e-3f);   // +y moves down, the pixel convention
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheJitteredViewProjectionIsTheViewTimesTheJitteredProjection(bool perspective)
    {
        IIsoCamera3D camera = Camera(perspective);
        FrameView view = Snapshot(camera, new Vector2(-0.4375f, 0.125f));
        TemporalAssert.Close(camera.View * view.JitteredProjection, view.JitteredViewProjection, 1e-5f,
            "View * JitteredProjection against JitteredViewProjection");
    }

    [Fact]
    public void JitterClipIsTwoPixelsOverTheSizeWithYNegated()
    {
        FrameView view = Snapshot(Camera(false), new Vector2(0.25f, 0.375f));
        Assert.Equal(new Vector2(2f * 0.25f / W, -2f * 0.375f / H), view.JitterClip);
    }

    [Fact]
    public void TheSnapshotKeepsWhatItWasLatchedWith()
    {
        IIsoCamera3D camera = Camera(true);
        FrameView view = Snapshot(camera, new Vector2(0.125f, 0.25f));
        TemporalAssert.BitIdentical(camera.View, view.View, "View");
        TemporalAssert.BitIdentical(camera.Projection, view.Projection, "Projection");
        TemporalAssert.BitIdentical(camera.ViewProjection, view.ViewProjection, "ViewProjection");
        TemporalAssert.BitIdentical(((IRenderOriginAware)camera).AbsoluteViewProjection, view.AbsoluteViewProjection,
            "AbsoluteViewProjection");
        Assert.Equal(Origin, view.RenderOrigin);
        Assert.Equal((W, H, 7L), (view.Width, view.Height, view.FrameIndex));
        Assert.Equal(new Vector2(0.125f, 0.25f), view.JitterPixels);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsOrthographicReadsTheProjectionKind(bool perspective, bool orthographic)
        => Assert.Equal(orthographic, Snapshot(Camera(perspective), Vector2.Zero).IsOrthographic);
}
